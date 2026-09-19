using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace Helix
{
    public static class HelixConstants
    {
        public const string HeaderFileName = "Helix.h";
        public const string CppFileName = "Helix.cpp";
        public const string VcProjectKindGuid = "{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}";
    }

    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(HelixPackage.PackageGuidString)]
    public sealed class HelixPackage : AsyncPackage, IVsUpdateSolutionEvents
    {
        public const string PackageGuidString = "6cb6dfee-43b7-46dc-b2e5-e20f359e1e07";

        private DTE2 dteInstance;
        private IncrementalCache buildCache;
        private IVsOutputWindowPane helixOutputPane;

        private uint solutionEventsCookie;
        private IVsSolutionBuildManager2 buildManager;
        private static readonly Guid OutputPaneGuid = new Guid("d6741b8a-b856-4c3e-8e6d-74d3204e3897");

        private static readonly Regex s_wrapKernelRegex = new(@"^[ \t]*#if\s+defined\(_KERNEL_MODE\)(?:\s*\|\|\s*defined\(__clang__\))?[ \t]*\r?\n([ \t]*#include\s*[<""][^>""]+[>""][^\r\n]*?)\r?\n[ \t]*#endif[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex s_wrapUserRegex = new(@"^[ \t]*#if\s+!defined\(_KERNEL_MODE\)(?:\s*\|\|\s*defined\(__clang__\))?[ \t]*\r?\n([ \t]*#include\s*[<""][^>""]+[>""][^\r\n]*?)\r?\n[ \t]*#endif[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex s_cleanWrapRegex = new(@"^([ \t]*#include\s*[<""][^>""]+[>""][^\r\n]*?)(?:\s*//\s*Helix Auto-Wrapped)+[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex s_applyWrapRegex = new(@"^([ \t]*#include\s*[<""]([^>""]+)[>""][^\r\n]*?)\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex s_newlineRegex = new(@"\r\n|\n\r|\n|\r", RegexOptions.Compiled);

        private EnvDTE.DocumentEvents documentEvents;
        private EnvDTE.TextEditorEvents textEditorEvents;
        private volatile bool isProcessing = false;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            object dteService = await GetServiceAsync(typeof(DTE));
            if (dteService == null) throw new InvalidOperationException("Failed to get Visual Studio DTE service.");

            dteInstance = (DTE2)dteService;
            buildCache = new IncrementalCache();

            buildManager = await GetServiceAsync(typeof(SVsSolutionBuildManager)) as IVsSolutionBuildManager2;
            buildManager?.AdviseUpdateSolutionEvents(this, out solutionEventsCookie);

            documentEvents = dteInstance.Events.DocumentEvents;
            textEditorEvents = dteInstance.Events.TextEditorEvents;

            textEditorEvents.LineChanged += OnLineChanged;
        }

        protected override void Dispose(bool disposing)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (buildManager != null && solutionEventsCookie != 0) buildManager.UnadviseUpdateSolutionEvents(solutionEventsCookie);
            base.Dispose(disposing);
        }

        private void OnLineChanged(TextPoint StartPoint, TextPoint EndPoint, int Hint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (isProcessing) return;

            try
            {
                Document document = StartPoint.Parent.Parent;
                if (document == null || document.ProjectItem == null || document.ProjectItem.ContainingProject == null) return;
                Project project = document.ProjectItem.ContainingProject;

                if (project.Kind == HelixConstants.VcProjectKindGuid)
                {
                    _ = this.JoinableTaskFactory.RunAsync(async delegate
                    {
                        await this.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (isProcessing) return;
                        isProcessing = true;

                        try { EnsureBaseHeaderExists(project); } catch { } finally { isProcessing = false; }
                    });
                }
            }
            catch { }
        }

        private void EnsureBaseHeaderExists(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string projectDirectory = Path.GetDirectoryName(project.FullName);
            if (string.IsNullOrEmpty(projectDirectory)) return;

            Configuration activeConfig = project.ConfigurationManager.ActiveConfiguration;
            string ghostDir = Path.Combine(projectDirectory, "obj", activeConfig.PlatformName, activeConfig.ConfigurationName);
            if (!Directory.Exists(ghostDir)) Directory.CreateDirectory(ghostDir);

            string expectedHeader = Path.Combine(ghostDir, HelixConstants.HeaderFileName);

            if (!File.Exists(expectedHeader))
            {
                LogToOutputWindow($"[Helix] Code edited and Helix.h is missing. Fast-generating base keywords and tricking IntelliSense...");

                CodeGenEngine codeGen = new CodeGenEngine();
                codeGen.GenerateRuntimeHeader(ghostDir, LogToOutputWindow);

                string expectedCpp = Path.Combine(ghostDir, HelixConstants.CppFileName);
                if (!File.Exists(expectedCpp))
                {
                    File.WriteAllText(expectedCpp, "// Pending Full Compilation...\r\n#include \"Helix.h\"\r\n");
                }

                try
                {
                    string runtimeDir = Path.GetFullPath(Path.Combine(projectDirectory, "Helix.Runtime"));
                    string targetsFileDir = Path.Combine(projectDirectory, "obj");
                    if (!Directory.Exists(targetsFileDir)) Directory.CreateDirectory(targetsFileDir);

                    string targetsFilePath = Path.Combine(targetsFileDir, $"{project.Name}.vcxproj.Helix.targets");
                    string targetsContent = $$"""
                        <?xml version="1.0" encoding="utf-8"?>
                        <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                          <ItemDefinitionGroup>
                            <ClCompile>
                              <AdditionalIncludeDirectories>%(AdditionalIncludeDirectories);{{ghostDir}};{{runtimeDir}}</AdditionalIncludeDirectories>
                              <ForcedIncludeFiles>%(ForcedIncludeFiles);{{ghostDir}}\{{HelixConstants.HeaderFileName}}</ForcedIncludeFiles>
                            </ClCompile>
                          </ItemDefinitionGroup>
                          <Target Name="InjectHelixCpp" BeforeTargets="ClCompile">
                            <ItemGroup>
                              <ClCompile Include="{{ghostDir}}\{{HelixConstants.CppFileName}}">
                                <Visible>false</Visible>
                              </ClCompile>
                            </ItemGroup>
                          </Target>
                        </Project>
                        """;
                    if (!File.Exists(targetsFilePath) || File.ReadAllText(targetsFilePath) != targetsContent)
                    {
                        File.WriteAllText(targetsFilePath, targetsContent);
                    }

                    dteInstance.ExecuteCommand("Project.RescanSolution");

                    if (dteInstance.ActiveDocument != null)
                    {
                        var txt = dteInstance.ActiveDocument.Object("TextDocument") as TextDocument;
                        if (txt != null)
                        {
                            var epStart = txt.StartPoint.CreateEditPoint();
                            var epEnd = txt.EndPoint.CreateEditPoint();
                            string content = epStart.GetText(epEnd);

                            int idx = content.IndexOf("#include \"Helix.h\"");
                            if (idx >= 0)
                            {
                                var editPt = txt.StartPoint.CreateEditPoint();
                                editPt.MoveToAbsoluteOffset(idx + 1);
                                editPt.Insert(" ");
                                editPt.Delete(-1);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        public int UpdateSolution_Begin(ref int pfCancelUpdate)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (isProcessing) return VSConstants.S_OK;
            isProcessing = true;

            try
            {
                foreach (Project project in dteInstance.Solution.Projects)
                    if (project.Kind == HelixConstants.VcProjectKindGuid) ProcessProject(project);
            }
            catch (Exception exception) { LogToOutputWindow($"[Error] {exception.Message}"); }
            finally { isProcessing = false; }
            return VSConstants.S_OK;
        }

        public int UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand) => VSConstants.S_OK;
        public int UpdateSolution_StartUpdate(ref int pfCancelUpdate) => VSConstants.S_OK;
        public int UpdateSolution_Cancel() => VSConstants.S_OK;
        public int OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy) => VSConstants.S_OK;

        private void LogToOutputWindow(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (helixOutputPane == null)
            {
                if (ServiceProvider.GlobalProvider.GetService(typeof(SVsOutputWindow)) is IVsOutputWindow outputWindow)
                {
                    Guid customGuid = OutputPaneGuid;
                    outputWindow.CreatePane(ref customGuid, "Helix Scanner", 1, 1);
                    outputWindow.GetPane(ref customGuid, out helixOutputPane);
                }
            }
            helixOutputPane?.OutputStringThreadSafe(message + Environment.NewLine);
            helixOutputPane?.Activate();
        }

        private List<string> GetWindowsSdkIncludePaths(Configuration activeConfig)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            List<string> paths = new();
            try
            {
                dynamic vcConfig = activeConfig.Object;
                string windowsSdkDir = vcConfig.Evaluate("$(WindowsSdkDir)");
                string targetPlatformVersion = vcConfig.Evaluate("$(WindowsTargetPlatformVersion)");
                string ucrtSdkDir = vcConfig.Evaluate("$(UniversalCRTSdkDir)");
                string ucrtVersion = vcConfig.Evaluate("$(UCRTVersion)");

                if (!string.IsNullOrEmpty(windowsSdkDir) && !string.IsNullOrEmpty(targetPlatformVersion))
                {
                    string includeRoot = Path.Combine(windowsSdkDir, "Include", targetPlatformVersion);
                    string km = Path.Combine(includeRoot, "km");
                    string um = Path.Combine(includeRoot, "um");
                    string shared = Path.Combine(includeRoot, "shared");

                    if (Directory.Exists(km)) paths.Add(km);
                    if (Directory.Exists(um)) paths.Add(um);
                    if (Directory.Exists(shared)) paths.Add(shared);
                }

                if (!string.IsNullOrEmpty(ucrtSdkDir) && !string.IsNullOrEmpty(ucrtVersion))
                {
                    string ucrt = Path.Combine(ucrtSdkDir, "Include", ucrtVersion, "ucrt");
                    if (Directory.Exists(ucrt)) paths.Add(ucrt);
                }

                string includePath = vcConfig.Evaluate("$(IncludePath)");
                if (!string.IsNullOrEmpty(includePath))
                {
                    foreach (string p in includePath.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string trimmed = p.Trim();
                        if (Directory.Exists(trimmed) && !paths.Contains(trimmed))
                        {
                            string lower = trimmed.ToLower();
                            if (lower.Contains("\\km") || lower.Contains("\\um") || lower.Contains("\\shared") || lower.Contains("\\ucrt"))
                            {
                                paths.Add(trimmed);
                            }
                        }
                    }
                }
            }
            catch
            {
                try
                {
                    string kitsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10");
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots");
                        if (key != null)
                        {
                            string k10 = key.GetValue("KitsRoot10") as string;
                            if (!string.IsNullOrEmpty(k10)) kitsRoot = k10;
                        }
                    }

                    string includeRoot = Path.Combine(kitsRoot, "Include");
                    if (Directory.Exists(includeRoot))
                    {
                        var dirs = new List<string>(Directory.GetDirectories(includeRoot));
                        dirs.Sort((a, b) => b.CompareTo(a));

                        foreach (string dir in dirs)
                        {
                            string km = Path.Combine(dir, "km");
                            string um = Path.Combine(dir, "um");
                            string shared = Path.Combine(dir, "shared");
                            string ucrt = Path.Combine(dir, "ucrt");

                            if (Directory.Exists(km) && !paths.Contains(km)) paths.Add(km);
                            if (Directory.Exists(um) && !paths.Contains(um)) paths.Add(um);
                            if (Directory.Exists(shared) && !paths.Contains(shared)) paths.Add(shared);
                            if (Directory.Exists(ucrt) && !paths.Contains(ucrt)) paths.Add(ucrt);
                        }
                    }
                }
                catch { }
            }
            return paths;
        }

        private string GetHeaderRealm(string headerName, List<string> sdkPaths)
        {
            foreach (string sdkPath in sdkPaths)
            {
                if (File.Exists(Path.Combine(sdkPath, headerName)))
                {
                    string lowerPath = sdkPath.ToLower();
                    if (lowerPath.EndsWith("\\km") || lowerPath.Contains("\\km\\")) return "kernel";
                    if (lowerPath.EndsWith("\\um") || lowerPath.Contains("\\um\\")) return "user";
                }
            }
            return "neutral";
        }

        private string ApplyHelixWrappers(string text, List<string> sdkPaths)
        {
            for (int i = 0; i < 3; i++)
            {
                text = s_wrapKernelRegex.Replace(text, "$1");
                text = s_wrapUserRegex.Replace(text, "$1");
            }
            text = s_cleanWrapRegex.Replace(text, "$1");

            text = s_applyWrapRegex.Replace(text, match =>
            {
                string fullMatch = match.Groups[1].Value;
                string headerName = match.Groups[2].Value;
                string realm = GetHeaderRealm(headerName, sdkPaths);

                if (realm == "kernel") return $"#if defined(_KERNEL_MODE)\r\n{fullMatch} // Helix Auto-Wrapped\r\n#endif";
                else if (realm == "user") return $"#if !defined(_KERNEL_MODE)\r\n{fullMatch} // Helix Auto-Wrapped\r\n#endif";
                return match.Value;
            });
            return text;
        }

        private void ProcessProject(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string projectFilePath = project.FullName;
            string projectDirectory = Path.GetDirectoryName(projectFilePath);
            if (string.IsNullOrEmpty(projectDirectory)) return;

            Configuration activeConfig = project.ConfigurationManager.ActiveConfiguration;
            string ghostDir = Path.Combine(projectDirectory, "obj", activeConfig.PlatformName, activeConfig.ConfigurationName);
            if (!Directory.Exists(ghostDir)) Directory.CreateDirectory(ghostDir);

            string runtimeDir = Path.GetFullPath(Path.Combine(projectDirectory, "..", "Helix.Runtime"));

            string targetsFileDir = Path.Combine(projectDirectory, "obj");
            if (!Directory.Exists(targetsFileDir)) Directory.CreateDirectory(targetsFileDir);

            string targetsFilePath = Path.Combine(targetsFileDir, $"{project.Name}.vcxproj.Helix.targets");
            List<string> sdkPaths = GetWindowsSdkIncludePaths(activeConfig);

            string targetsContent = $$"""
                <?xml version="1.0" encoding="utf-8"?>
                <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <ItemDefinitionGroup>
                    <ClCompile>
                      <AdditionalIncludeDirectories>%(AdditionalIncludeDirectories);{{ghostDir}};{{runtimeDir}}</AdditionalIncludeDirectories>
                      <ForcedIncludeFiles>%(ForcedIncludeFiles);{{ghostDir}}\{{HelixConstants.HeaderFileName}}</ForcedIncludeFiles>
                    </ClCompile>
                  </ItemDefinitionGroup>
                  <Target Name="InjectHelixCpp" BeforeTargets="ClCompile">
                    <ItemGroup>
                      <ClCompile Include="{{ghostDir}}\{{HelixConstants.CppFileName}}">
                        <Visible>false</Visible>
                      </ClCompile>
                    </ItemGroup>
                  </Target>
                </Project>
                """;

            if (!File.Exists(targetsFilePath) || File.ReadAllText(targetsFilePath) != targetsContent)
            {
                File.WriteAllText(targetsFilePath, targetsContent);
                LogToOutputWindow($"[Helix] MSBuild target isolated and injected at {targetsFilePath}.");
                try { dteInstance.ExecuteCommand("Project.RescanSolution"); } catch { }
            }

            List<string> sourceFiles = GetProjectSourceFiles(project.ProjectItems);
            List<string> userFiles = sourceFiles.FindAll(file => !file.EndsWith(HelixConstants.HeaderFileName, StringComparison.OrdinalIgnoreCase) && !file.EndsWith(HelixConstants.CppFileName, StringComparison.OrdinalIgnoreCase));

            bool forceRebuild = false;

            try { dteInstance.Documents.SaveAll(); } catch { }
            try { if (!project.Saved) project.Save(); } catch { }
            try { dteInstance.ExecuteCommand("File.SaveAll"); } catch { }

            foreach (string file in userFiles)
            {
                try
                {
                    if (!File.Exists(file)) continue;

                    string text = File.ReadAllText(file, Encoding.Default);
                    string newText = ApplyHelixWrappers(text, sdkPaths);

                    string normText = s_newlineRegex.Replace(text, "\r\n");
                    string normNewText = s_newlineRegex.Replace(newText, "\r\n");

                    if (normText != normNewText)
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        using (FileStream fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                        using (StreamWriter writer = new StreamWriter(fs, new UTF8Encoding(true)))
                        {
                            writer.Write(normNewText);
                        }

                        LogToOutputWindow($"[Helix] Seamless Polyfill Applied: {Path.GetFileName(file)}");
                        forceRebuild = true;
                    }
                }
                catch { }
            }

            if (forceRebuild || buildCache.IsRebuildRequired(projectFilePath, userFiles))
            {
                try
                {
                    CodeGenEngine codeGen = new CodeGenEngine();
                    codeGen.GenerateRuntimeHeader(ghostDir, LogToOutputWindow);

                    bool isKernelMode = false;
                    bool enableCrossRing = false; // [新增] 跨环控制开关
                    string cppStandard = "-std=c++20";
                    try
                    {
                        string xmlContent = File.ReadAllText(projectFilePath);

                        dynamic vcConfig = activeConfig.Object;
                        string driverType = "";
                        string preprocessor = "";
                        try
                        {
                            driverType = vcConfig.Evaluate("$(DriverType)");
                            preprocessor = vcConfig.Evaluate("$(PreprocessorDefinitions)");

                            string langStd = vcConfig.Evaluate("$(LanguageStandard)");
                            if (!string.IsNullOrEmpty(langStd))
                            {
                                if (langStd.Contains("stdcpp14")) cppStandard = "-std=c++14";
                                else if (langStd.Contains("stdcpp17")) cppStandard = "-std=c++17";
                                else if (langStd.Contains("stdcpp20")) cppStandard = "-std=c++20";
                                else if (langStd.Contains("stdcpplatest")) cppStandard = "-std=c++2b";
                            }
                        }
                        catch { }

                        if ((!string.IsNullOrEmpty(driverType) && (driverType.Contains("KMDF") || driverType.Contains("WDM"))) ||
                            (!string.IsNullOrEmpty(preprocessor) && preprocessor.Contains("_KERNEL_MODE")) ||
                            xmlContent.Contains("<DriverType>KMDF</DriverType>") ||
                            xmlContent.Contains("<DriverType>WDM</DriverType>") ||
                            xmlContent.Contains("_KERNEL_MODE"))
                        {
                            isKernelMode = true;
                        }

                        // [极速优化]: 动态探测跨环宏开关
                        if ((!string.IsNullOrEmpty(preprocessor) && preprocessor.Contains("HELIX_ENABLE_CROSS_RING")) ||
                            xmlContent.Contains("HELIX_ENABLE_CROSS_RING"))
                        {
                            enableCrossRing = true;
                        }
                    }
                    catch { }

                    string platformName = activeConfig.PlatformName;

                    ClangScanner scanner = new()
                    {
                        IntermediateDirectory = ghostDir,
                        RuntimeDirectory = runtimeDir,
                        SdkPaths = sdkPaths,
                        CppStandard = cppStandard
                    };

                    // 传递跨环标志
                    Dictionary<string, ClassMetadata> registry = scanner.Parse(userFiles, projectDirectory, platformName, isKernelMode, enableCrossRing, LogToOutputWindow);

                    LogToOutputWindow($"[Helix] Full Scan complete. Target Mode: {(isKernelMode ? "Ring 0" : "Ring 3")}. Cross-Ring: {(enableCrossRing ? "Enabled" : "Disabled")}. Found {registry.Count} type(s).");

                    List<string> headerFiles = userFiles.FindAll(file => file.EndsWith(".h", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".hpp", StringComparison.OrdinalIgnoreCase));

                    codeGen.Generate(registry, ghostDir, headerFiles, isKernelMode, LogToOutputWindow);

                    buildCache.UpdateCache(projectFilePath, sourceFiles);

                    LogToOutputWindow($"[Helix] Refreshing IntelliSense and Syntax Highlighting...");
                    try { dteInstance.ExecuteCommand("Project.RescanSolution"); } catch { }
                }
                catch (Exception exception)
                {
                    string errMsg = exception.Message.Replace("\r", "").Replace("\n", " ");
                    LogToOutputWindow($"[Helix] Scanner Execution Error: {errMsg}\n{exception.StackTrace}");
                    try { File.WriteAllText(Path.Combine(ghostDir, HelixConstants.CppFileName), $"#error [Helix VSIX Engine Crash] Detail: {errMsg}\n", Encoding.UTF8); } catch { }
                }
            }
        }

        private List<string> GetProjectSourceFiles(ProjectItems items)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            List<string> files = [];
            if (items == null) return files;
            foreach (ProjectItem item in items)
            {
                if (item.FileCount > 0)
                {
                    string filePath = item.FileNames[1];
                    string extension = Path.GetExtension(filePath).ToLower();
                    if (extension == ".h" || extension == ".hpp" || extension == ".cpp") files.Add(filePath);
                }
                if (item.ProjectItems != null && item.ProjectItems.Count > 0) files.AddRange(GetProjectSourceFiles(item.ProjectItems));
            }
            return files;
        }
    }
}
