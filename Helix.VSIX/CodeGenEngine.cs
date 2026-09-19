using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Helix
{
    public class CodeGenEngine
    {
        private class ApiDefinition { public string DllName { get; set; } public string ReturnType { get; set; } public string CallingConvention { get; set; } public string FunctionName { get; set; } public string Parameters { get; set; } public string ArgumentNames { get; set; } }

        // 64 位 FNV-1a 终极哈希算法
        private ulong ComputeFnv1a(string text)
        {
            ulong hash = 14695981039346656037ul;
            foreach (char c in text) { hash ^= (byte)c; unchecked { hash *= 1099511628211ul; } }
            return hash;
        }

        private static readonly Regex s_newlineRegex = new(@"\r\n|\n\r|\n|\r", RegexOptions.Compiled);
        private static readonly Regex s_apiRegex = new(@"API\(\s*(?:\""([^""]*)\"")?\s*\)\s+(.+?)\s+([A-Za-z_]*API|__stdcall|__cdecl|__fastcall)?\s*(\w+)\s*\((.*?)\)\s*;", RegexOptions.Singleline | RegexOptions.Compiled);

        private void LogDebug(Action<string> logger, string message)
        {
#if DEBUG
            logger?.Invoke(message);
#endif
        }

        private void ForceWriteAllText(string path, string content)
        {
            content = s_newlineRegex.Replace(content, "\r\n");
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            using StreamWriter writer = new StreamWriter(fs, new UTF8Encoding(true));
            writer.Write(content);
        }

        private string ForceReadAllText(string path)
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new StreamReader(fs, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private void SafeWriteFileIfChanged(string outputPath, string content, Action<string> logger)
        {
            if (File.Exists(outputPath))
            {
                string oldContent = ForceReadAllText(outputPath);
                if (oldContent == content) return;
            }
            ForceWriteAllText(outputPath, content);
            LogDebug(logger, $"[CodeGen] Successfully generated: {outputPath}");
        }

        private void ScanApiDefinitions(List<string> headerFiles, out List<ApiDefinition> imports, out List<ApiDefinition> exports)
        {
            imports = new List<ApiDefinition>(); exports = new List<ApiDefinition>();
            foreach (var file in headerFiles)
            {
                if (!File.Exists(file)) continue;
                try
                {
                    string sourceCode = ForceReadAllText(file);
                    var matches = s_apiRegex.Matches(sourceCode);
                    foreach (Match match in matches)
                    {
                        var argNames = string.Join(", ", match.Groups[5].Value.Trim().Split(',').Select(p => p.Trim().Split(new[] { ' ', '*', '&' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault()).Where(n => !string.IsNullOrEmpty(n) && n != "void"));
                        string dll = match.Groups[1].Success ? match.Groups[1].Value.Trim() : "";
                        var def = new ApiDefinition { DllName = dll, ReturnType = match.Groups[2].Value.Trim(), CallingConvention = match.Groups[3].Value.Trim(), FunctionName = match.Groups[4].Value, Parameters = match.Groups[5].Value.Trim(), ArgumentNames = argNames };
                        if (string.IsNullOrEmpty(dll) || dll.Equals("export", StringComparison.OrdinalIgnoreCase)) exports.Add(def); else imports.Add(def);
                    }
                }
                catch { }
            }
        }

        public void Generate(Dictionary<string, ClassMetadata> registry, string projectDirectory, List<string> headerFiles, bool isKernelMode, Action<string> logger)
        {
            string outputPath = Path.Combine(projectDirectory, HelixConstants.CppFileName);
            ScanApiDefinitions(headerFiles, out var apiImports, out var apiExports);

            var validMetas = registry.Values.Where(m =>
                m.Size >= 0 &&
                !string.IsNullOrEmpty(m.ClassName) &&
                !m.ClassName.Contains(" ") &&
                !m.ClassName.Contains("<") &&
                !m.ClassName.Contains(">") &&
                !m.ClassName.Contains("::") &&
                !m.ClassName.Contains("`") &&
                !m.ClassName.Contains("anonymous") &&
                !m.ClassName.Contains("unnamed") &&
                !m.ClassName.Contains("lambda")
            ).ToList();

            var validGlobalPointers = ClangScanner.GlobalPointers.Where(gVar => {
                if (gVar.IsStatic)
                {
                    int colIdx = gVar.Name.IndexOf("::");
                    if (colIdx > 0)
                    {
                        string hostClass = gVar.Name.Substring(0, colIdx);
                        return validMetas.Any(m => m.ClassName == hostClass);
                    }
                }
                return true;
            }).ToList();

            StringBuilder cppBuilder = new();

            cppBuilder.AppendLine("// THIS FILE IS AUTO-GENERATED BY HELIX VSIX PLUGIN.\n// DO NOT MODIFY MANUALLY.\n");
            cppBuilder.AppendLine("#ifdef _MSC_VER\n    #pragma warning(disable : 4458 4100 4595 4456 4189 4702 4701 4703 4120 4644 4065)\n#endif\n");
            cppBuilder.AppendLine("#define HELIX_CPP_GENERATION");

            var distinctApis = apiImports.GroupBy(a => a.FunctionName).Select(g => g.First()).ToList();
            if (distinctApis.Count > 0)
            {
                cppBuilder.AppendLine("// 宏劫持屏蔽系统头文件 dllimport 污染");
                foreach (var api in distinctApis) cppBuilder.AppendLine($"#define {api.FunctionName} HelixProxy_{api.FunctionName}");
            }

            cppBuilder.AppendLine($"#include \"{HelixConstants.HeaderFileName}\"\n");
            cppBuilder.AppendLine("#define private public\n#define protected public");
            foreach (string header in headerFiles) if (File.Exists(header)) cppBuilder.AppendLine($"#include \"{MakeRelativePath(projectDirectory, header)}\"");
            cppBuilder.AppendLine("#undef private\n#undef protected\n");

            if (distinctApis.Count > 0)
            {
                foreach (var api in distinctApis) cppBuilder.AppendLine($"#undef {api.FunctionName}");
                cppBuilder.AppendLine("\nnamespace HelixApiProxy {");
                foreach (var api in distinctApis)
                {
                    cppBuilder.AppendLine($"    extern \"C\" {api.ReturnType} {api.CallingConvention} HelixApiStub_{api.FunctionName}({api.Parameters}) {{");
                    if (api.ReturnType != "void") cppBuilder.AppendLine($"        return {{}};");
                    cppBuilder.AppendLine($"    }}");
                    cppBuilder.AppendLine($"    extern \"C\" __declspec(selectany) void* __imp_{api.FunctionName} = (void*)HelixApiStub_{api.FunctionName};");
                    cppBuilder.AppendLine($"    extern \"C\" {api.ReturnType} {api.CallingConvention} {api.FunctionName}({api.Parameters}) {{");
                    cppBuilder.AppendLine($"        using PFN = decltype(&HelixApiStub_{api.FunctionName});");
                    if (api.ReturnType != "void") cppBuilder.AppendLine($"        return ((PFN)__imp_{api.FunctionName})({api.ArgumentNames});");
                    else cppBuilder.AppendLine($"        ((PFN)__imp_{api.FunctionName})({api.ArgumentNames});");
                    cppBuilder.AppendLine($"    }}\n");
                }
                cppBuilder.AppendLine("}\n");
            }

            foreach (var gVar in validGlobalPointers) if (!gVar.IsStatic) cppBuilder.AppendLine($"extern {gVar.Type} {gVar.Name};");

            foreach (var vmp in ClangScanner.VmpTargets)
            {
                if (string.IsNullOrEmpty(vmp.ClassName))
                {
                    cppBuilder.AppendLine($"extern {vmp.ReturnType} {vmp.FuncName}({vmp.ArgString});");
                }
                string safeVmpClassName = string.IsNullOrEmpty(vmp.ClassName) ? "" : "C" + ComputeFnv1a(vmp.ClassName).ToString("X16");
                string detourName = string.IsNullOrEmpty(vmp.ClassName) ? $"Detour_{vmp.FuncName}" : $"Detour_{safeVmpClassName}_{vmp.FuncName}";
                cppBuilder.AppendLine($"extern {vmp.ReturnType} {detourName}({vmp.ArgString});");
            }

            cppBuilder.AppendLine("\nnamespace HelixAutoReg {\n    using namespace HelixRuntime;\n");

            foreach (var vmp in ClangScanner.VmpTargets)
            {
                List<string> blocks = new(vmp.BodyBlocks); List<string> liftedDecls = new(vmp.LiftedDecls); List<string> refDecls = new(); HashSet<string> boundNames = new();
                if (!string.IsNullOrEmpty(vmp.ClassName) && registry.TryGetValue(vmp.ClassName, out ClassMetadata meta))
                {
                    if (!vmp.IsStatic)
                    {
                        for (int i = 0; i < blocks.Count; i++) blocks[i] = blocks[i].Replace("this->", "_this->");
                        for (int i = 0; i < liftedDecls.Count; i++) liftedDecls[i] = liftedDecls[i].Replace("this->", "_this->");
                        foreach (var field in meta.Fields) if (boundNames.Add(field.Name)) refDecls.Add($"auto& {field.Name} = _this->{field.Name};");
                        foreach (var method in meta.Methods) if (boundNames.Add(method.Name)) refDecls.Add($"auto {method.Name} = [&](auto&&... _args) -> decltype(auto) {{ return _this->{method.Name}(HelixRuntime::forward<decltype(_args)>(_args)...); }};");
                    }
                    foreach (var gVar in validGlobalPointers)
                    {
                        if (gVar.IsStatic && gVar.Name.StartsWith(meta.ClassName + "::"))
                        {
                            string shortName = gVar.Name.Substring(meta.ClassName.Length + 2);
                            if (boundNames.Add(shortName)) refDecls.Add($"auto& {shortName} = {gVar.Name};");
                        }
                    }
                }
                string flattenedBody = VmpEngine.FlattenFunction(blocks, liftedDecls, refDecls).Replace("HelixRuntime::Vmp::", "HelixRuntime::VmpCore::");
                string safeVmpClassName = string.IsNullOrEmpty(vmp.ClassName) ? "" : "C" + ComputeFnv1a(vmp.ClassName).ToString("X16");
                string detourName = string.IsNullOrEmpty(vmp.ClassName) ? $"Detour_{vmp.FuncName}" : $"Detour_{safeVmpClassName}_{vmp.FuncName}";

                string fullArgs = vmp.ArgString;
                if (!string.IsNullOrEmpty(vmp.ClassName) && !vmp.IsStatic) fullArgs = string.IsNullOrEmpty(fullArgs) ? $"{vmp.ClassName}* _this" : $"{vmp.ClassName}* _this, {fullArgs}";
                cppBuilder.AppendLine($"    {vmp.ReturnType} {detourName}({fullArgs}) {{");
                foreach (string ep in vmp.ExternParams) if (!string.IsNullOrEmpty(ep)) cppBuilder.AppendLine($"        HelixRuntime::ExternParamGuard<decltype({ep})> _guard_{ep}({ep});");
                cppBuilder.AppendLine(flattenedBody);
                if (vmp.ReturnType != "void") cppBuilder.AppendLine($"        return {{}};");
                cppBuilder.AppendLine($"    }}");
            }

            foreach (ClassMetadata meta in validMetas)
            {
                if (meta.Size == 0 && meta.Fields.Count == 0) continue;

                string safeRegName = "Reg_" + ComputeFnv1a(meta.ClassName).ToString("X16");

                cppBuilder.AppendLine($$"""
                    struct {{safeRegName}} {
                        using ClassType = {{meta.ClassName}};
                        {{safeRegName}}() {
                            static HelixRuntime::ClassMeta metaInfo;
                            static bool init = false;
                            if (init) return;
                            init = true;
                            metaInfo.classHash = HelixRuntime::GetTypeHash<ClassType>();
                """);

                var distinctFields = meta.Fields.GroupBy(f => f.Name).Select(g => g.First()).ToList();
                foreach (FieldMetadata field in distinctFields)
                {
                    cppBuilder.AppendLine($$"""
                            {
                                metaInfo.Fields.push_back({
                                    0x{{ComputeFnv1a(field.Name):X16}}ull, 
                                    HelixRuntime::GetTypeHash<typename HelixRuntime::StripModifiers<decltype(((ClassType*)0)->{{field.Name}})>::type>(), 
                                    offsetof(ClassType, {{field.Name}}), 
                                    {{field.IsExtern.ToString().ToLower()}}, 
                                    {{field.IsPointer.ToString().ToLower()}}
                                });
                            }
                    """);
                }

                var distinctMethods = meta.Methods.GroupBy(m => m.Name).Select(g => g.First()).ToList();
                foreach (MethodMetadata method in distinctMethods)
                {
                    string paramsList = "";
                    string argsList = "";
                    if (method.ParamTypeSpellings != null && method.ParamTypeSpellings.Count > 0)
                    {
                        var pList = new List<string>();
                        var aList = new List<string>();
                        for (int i = 0; i < method.ParamTypeSpellings.Count; ++i)
                        {
                            pList.Add($"typename HelixRuntime::StripParam<{method.ParamTypeSpellings[i]}>::type a{i}");
                            aList.Add($"a{i}");
                        }
                        paramsList = ", " + string.Join(", ", pList);
                        argsList = string.Join(", ", aList);
                    }

                    cppBuilder.AppendLine($$"""
                            {
                                HelixRuntime::MethodMeta mMeta;
                                mMeta.nameHash = 0x{{ComputeFnv1a(method.Name):X16}}ull;
                                static auto thunk = [](void* inst{{paramsList}}) {
                                    return ((ClassType*)inst)->{{method.Name}}({{argsList}});
                                };
                                mMeta.methodPtr = (void*)+thunk;
                                metaInfo.Methods.push_back(mMeta);
                            }
                    """);
                }
                cppBuilder.AppendLine($$"""
                            HelixRuntime::RegisterMeta(HelixRuntime::GetTypeHash<ClassType>(), metaInfo);
                            HelixGenerated::GetDB().Add(HelixRuntime::GetTypeHash<ClassType>(), "{{meta.ClassName}}", &metaInfo);
                            
                            #ifdef _KERNEL_MODE
                            if constexpr (__is_polymorphic(ClassType) && __is_constructible(ClassType) && !__is_abstract(ClassType)) {
                                void* mem = HelixRuntime::OSAlloc(sizeof(ClassType));
                                if (mem) {
                                    HelixRuntime::HMemSet(mem, 0, sizeof(ClassType));
                                    ClassType* dummy = new(mem) ClassType();
                                    void* vtable = *(void**)dummy;
                                    HelixGenerated::GetDB().AddVTable(vtable, metaInfo.classHash);
                                    dummy->~ClassType();
                                    HelixRuntime::OSFree(mem);
                                }
                            }
                            #endif
                        }
                    };
                """);
            }

            cppBuilder.AppendLine("    struct Registrar_Globals {");
            cppBuilder.AppendLine("        Registrar_Globals() {");
            foreach (var gVar in validGlobalPointers) cppBuilder.AppendLine($"            HelixRuntime::GC::RegisterGlobalRoot((void**)&{gVar.Name});");
            foreach (var vmp in ClangScanner.VmpTargets)
            {
                string safeVmpClassName = string.IsNullOrEmpty(vmp.ClassName) ? "" : "C" + ComputeFnv1a(vmp.ClassName).ToString("X16");
                string detourName = string.IsNullOrEmpty(vmp.ClassName) ? $"Detour_{vmp.FuncName}" : $"Detour_{safeVmpClassName}_{vmp.FuncName}";
                if (string.IsNullOrEmpty(vmp.ClassName)) cppBuilder.AppendLine($"            HelixRuntime::VmpCore::InstallHook((void*)&{vmp.FuncName}, (void*)&{detourName});");
                else cppBuilder.AppendLine($"            HelixRuntime::VmpCore::InstallHook(HelixRuntime::VmpCore::GetMethodAddress(&{vmp.ClassName}::{vmp.FuncName}), (void*)&{detourName});");
            }
            cppBuilder.AppendLine("        }\n    };\n");

            cppBuilder.AppendLine("    inline void InitializeHelix(void* driverObject = nullptr) {");
            cppBuilder.AppendLine("        static bool initialized = false; if (initialized) return; initialized = true;");
            cppBuilder.AppendLine("        HELIX_PLATFORM_INIT(driverObject);");
            cppBuilder.AppendLine("#ifdef _KERNEL_MODE\n        HelixRuntime::GetShards();\n#endif");

            foreach (ClassMetadata meta in validMetas)
            {
                if (meta.Size > 0 || meta.Fields.Count > 0)
                {
                    string safeRegName = "Reg_" + ComputeFnv1a(meta.ClassName).ToString("X16");
                    cppBuilder.AppendLine($"        {safeRegName} instance_{safeRegName};");
                }
            }
            cppBuilder.AppendLine("        Registrar_Globals reg_Globals;\n");

            if (apiImports.Count > 0)
            {
                foreach (var group in apiImports.GroupBy(a => a.FunctionName))
                {
                    var api = group.First();
                    string exportName = api.FunctionName.StartsWith("_") && uint.TryParse(api.FunctionName.Substring(1), out _) ? $"(const char*){api.FunctionName.Substring(1)}" : $"\"{api.FunctionName}\"";
                    cppBuilder.AppendLine($"        if (HelixApiProxy::__imp_{api.FunctionName} == (void*)HelixApiProxy::HelixApiStub_{api.FunctionName}) {{");
                    foreach (var dll in group.Select(a => a.DllName).Distinct())
                    {
                        cppBuilder.AppendLine($"            if (HelixApiProxy::__imp_{api.FunctionName} == (void*)HelixApiProxy::HelixApiStub_{api.FunctionName}) {{");
                        cppBuilder.AppendLine($"                void* hMod = HelixRuntime::HAL::GetPlatform()->GetHModuleBase(\"{dll}\", driverObject);");
                        cppBuilder.AppendLine($"                if (hMod) HelixApiProxy::__imp_{api.FunctionName} = HelixRuntime::HAL::GetPlatform()->GetHExportAddress(hMod, {exportName});");
                        cppBuilder.AppendLine($"            }}");
                    }
                    cppBuilder.AppendLine($"        }}");
                }
            }
            cppBuilder.AppendLine("    }\n\n#ifndef _KERNEL_MODE\n    struct AutoInit { AutoInit() { InitializeHelix(); } };\n    static AutoInit g_autoInit;\n#endif\n}\n");

            cppBuilder.AppendLine("namespace HelixGenerated {");

            cppBuilder.AppendLine("    const char* GetHashName(uint64_t hash) {");
            cppBuilder.AppendLine("        switch(hash) {");
            HashSet<string> allNames = new HashSet<string>();
            foreach (var meta in validMetas)
            {
                allNames.Add(meta.ClassName);
                foreach (var f in meta.Fields) if (!string.IsNullOrEmpty(f.Name)) allNames.Add(f.Name);
                foreach (var m in meta.Methods) if (!string.IsNullOrEmpty(m.Name)) allNames.Add(m.Name);
            }
            foreach (var name in allNames)
            {
                cppBuilder.AppendLine($"            case 0x{ComputeFnv1a(name):X16}ull: return \"{name}\";");
            }
            cppBuilder.AppendLine("            default: return \"Unknown\";");
            cppBuilder.AppendLine("        }");
            cppBuilder.AppendLine("    }");

            cppBuilder.AppendLine("    uint64_t GetFieldTypeHash(uint64_t classHash, uint64_t fieldHash) {");
            bool isFirst = true;
            foreach (var meta in validMetas)
            {
                if (meta.Fields.Count == 0) continue;
                cppBuilder.AppendLine($"        {(isFirst ? "" : "else ")}if (classHash == HelixRuntime::GetTypeHash<{meta.ClassName}>()) {{");
                cppBuilder.AppendLine("            switch(fieldHash) {");
                var distinctFields = meta.Fields.GroupBy(f => f.Name).Select(g => g.First()).ToList();
                foreach (var f in distinctFields)
                {
                    cppBuilder.AppendLine($"                case 0x{ComputeFnv1a(f.Name):X16}ull: return HelixRuntime::GetTypeHash<typename HelixRuntime::StripModifiers<decltype(((::{meta.ClassName}*)0)->{f.Name})>::type>();");
                }
                cppBuilder.AppendLine("            }");
                cppBuilder.AppendLine("        }");
                isFirst = false;
            }
            cppBuilder.AppendLine("        return 0;");
            cppBuilder.AppendLine("    }");

            cppBuilder.AppendLine("    void* GetMethodPtr(uint64_t classHash, uint64_t methodHash) {");
            isFirst = true;
            foreach (var meta in validMetas)
            {
                if (meta.Methods.Count == 0) continue;
                cppBuilder.AppendLine($"        {(isFirst ? "" : "else ")}if (classHash == HelixRuntime::GetTypeHash<{meta.ClassName}>()) {{");
                cppBuilder.AppendLine("            switch(methodHash) {");
                var distinctMethods = meta.Methods.GroupBy(m => m.Name).Select(g => g.First()).ToList();
                foreach (var m in distinctMethods)
                {
                    string paramsList = "";
                    string argsList = "";
                    if (m.ParamTypeSpellings != null && m.ParamTypeSpellings.Count > 0)
                    {
                        var pList = new List<string>();
                        var aList = new List<string>();
                        for (int i = 0; i < m.ParamTypeSpellings.Count; ++i)
                        {
                            pList.Add($"typename HelixRuntime::StripParam<{m.ParamTypeSpellings[i]}>::type a{i}");
                            aList.Add($"a{i}");
                        }
                        paramsList = ", " + string.Join(", ", pList);
                        argsList = string.Join(", ", aList);
                    }
                    cppBuilder.AppendLine($"                case 0x{ComputeFnv1a(m.Name):X16}ull: {{");
                    cppBuilder.AppendLine($"                    static auto thunk = [](void* inst{paramsList}) {{");
                    cppBuilder.AppendLine($"                        return ((::{meta.ClassName}*)inst)->{m.Name}({argsList});");
                    cppBuilder.AppendLine($"                    }};");
                    cppBuilder.AppendLine($"                    return (void*)+thunk;");
                    cppBuilder.AppendLine($"                }}");
                }
                cppBuilder.AppendLine("            }");
                cppBuilder.AppendLine("        }");
                isFirst = false;
            }
            cppBuilder.AppendLine("        return nullptr;");
            cppBuilder.AppendLine("    }");

            cppBuilder.AppendLine("    bool VerifyMethodParams(uint64_t classHash, uint64_t methodHash, const uint64_t* pHashes, size_t count) {");
            isFirst = true;
            foreach (var meta in validMetas)
            {
                if (meta.Methods.Count == 0) continue;
                cppBuilder.AppendLine($"        {(isFirst ? "" : "else ")}if (classHash == HelixRuntime::GetTypeHash<{meta.ClassName}>()) {{");
                cppBuilder.AppendLine("            switch(methodHash) {");
                var distinctMethods = meta.Methods.GroupBy(m => m.Name).Select(g => g.First()).ToList();
                foreach (var m in distinctMethods)
                {
                    cppBuilder.AppendLine($"                case 0x{ComputeFnv1a(m.Name):X16}ull: {{");
                    if (m.ParamTypeSpellings != null && m.ParamTypeSpellings.Count > 0)
                    {
                        cppBuilder.AppendLine($"                    if (count != {m.ParamTypeSpellings.Count}) return false;");
                        for (int i = 0; i < m.ParamTypeSpellings.Count; i++)
                        {
                            cppBuilder.AppendLine($"                    if (pHashes[{i}] != HelixRuntime::GetTypeHash<typename HelixRuntime::StripParam<{m.ParamTypeSpellings[i]}>::type>()) return false;");
                        }
                        cppBuilder.AppendLine("                    return true;");
                    }
                    else
                    {
                        cppBuilder.AppendLine("                    return count == 0;");
                    }
                    cppBuilder.AppendLine("                }");
                }
                cppBuilder.AppendLine("            }");
                cppBuilder.AppendLine("        }");
                isFirst = false;
            }
            cppBuilder.AppendLine("        return false;");
            cppBuilder.AppendLine("    }");
            cppBuilder.AppendLine("}\n");

            cppBuilder.AppendLine("int HelixMain();\nint HelixMain(void**);\n#pragma comment(linker, \"/alternatename:?HelixMain@@YAHXZ=?DefaultHelixMain_NoArgs@@YAHXZ\")\n#pragma comment(linker, \"/alternatename:?HelixMain@@YAHPEAPEAX@Z=?DefaultHelixMain_Args@@YAHPEAPEAX@Z\")\nint DefaultHelixMain_NoArgs() { return 0; }\nint DefaultHelixMain_Args(void**) { return HelixMain(); }\n");
            cppBuilder.AppendLine("#undef main\n");

            cppBuilder.AppendLine("#ifdef _KERNEL_MODE\ntypedef struct _LDR_DATA_TABLE_ENTRY_COMPAT {\n    void* InLoadOrderLinks[2];\n    void* ExceptionTable;\n    unsigned long ExceptionTableSize;\n    void* GpValue;\n    void* NonPagedDebugInfo;\n    void* DllBase;\n    void* EntryPoint;\n    unsigned long SizeOfImage;\n    struct { unsigned short Length; unsigned short MaximumLength; wchar_t* Buffer; } FullDllName;\n    struct { unsigned short Length; unsigned short MaximumLength; wchar_t* Buffer; } BaseDllName;\n} LDR_DATA_TABLE_ENTRY_COMPAT;\n");
            cppBuilder.AppendLine("extern \"C\" long DriverEntry(void* DriverObject, void* RegistryPath) {\n    HelixAutoReg::InitializeHelix(DriverObject);\n    char* ansiPathBuf = (char*)HelixRuntime::OSAlloc(256);\n    HelixRuntime::HMemSet(ansiPathBuf, 0, 256);\n    void* driverSection = *(void**)((char*)DriverObject + 0x28);\n    if (driverSection) {\n        LDR_DATA_TABLE_ENTRY_COMPAT* ldrEntry = (LDR_DATA_TABLE_ENTRY_COMPAT*)driverSection;\n        if (ldrEntry->FullDllName.Buffer) {\n            for(int i = 0; i < ldrEntry->FullDllName.Length / 2 && i < 255; i++) {\n                ansiPathBuf[i] = (char)ldrEntry->FullDllName.Buffer[i];\n            }\n        }\n    }\n    void** args = (void**)HelixRuntime::OSAlloc(4 * sizeof(void*));\n    args[0] = (void*)ansiPathBuf;\n    args[1] = DriverObject;\n    args[2] = RegistryPath;\n    args[3] = nullptr;\n    HelixGenerated::GetArgsTracker().Add(ansiPathBuf);\n    HelixGenerated::GetArgsTracker().Add(args);\n    long status = (long)HelixMain(args);\n    HelixGenerated::GetArgsTracker().FreeAll();\n    HelixRuntime::GC::Shutdown();\n    return status;\n}\n#else");
            cppBuilder.AppendLine("    #if defined(_WINDLL) || defined(_USRDLL)");
            cppBuilder.AppendLine("    extern \"C\" int __stdcall DllMain(void* hinstDLL, unsigned long fdwReason, void* lpvReserved) {");
            cppBuilder.AppendLine("        if (fdwReason == 1) { // DLL_PROCESS_ATTACH");
            cppBuilder.AppendLine("            HelixAutoReg::InitializeHelix();");
            cppBuilder.AppendLine("        }");
            cppBuilder.AppendLine("        static char processPathBuf[512] = {0};");
            cppBuilder.AppendLine("        if (fdwReason == 1) {");
            cppBuilder.AppendLine("            GetModuleFileNameA((HMODULE)hinstDLL, processPathBuf, 512);");
            cppBuilder.AppendLine("        }");
            cppBuilder.AppendLine("        char* pathBuf = (char*)HelixRuntime::OSAlloc(512);");
            cppBuilder.AppendLine("        HelixRuntime::HMemCpy(pathBuf, processPathBuf, 512);");
            cppBuilder.AppendLine("        void** args = (void**)HelixRuntime::OSAlloc(5 * sizeof(void*));");
            cppBuilder.AppendLine("        args[0] = (void*)pathBuf;\n        args[1] = hinstDLL;\n        args[2] = (void*)(unsigned long long)fdwReason;\n        args[3] = lpvReserved;\n        args[4] = nullptr;");
            cppBuilder.AppendLine("        HelixGenerated::GetArgsTracker().Add(pathBuf);");
            cppBuilder.AppendLine("        HelixGenerated::GetArgsTracker().Add(args);");
            cppBuilder.AppendLine("        HelixMain(args);");
            cppBuilder.AppendLine("        if (fdwReason == 0) { // DLL_PROCESS_DETACH");
            cppBuilder.AppendLine("            HelixGenerated::GetArgsTracker().FreeAll();");
            cppBuilder.AppendLine("            HelixRuntime::GC::Shutdown();");
            cppBuilder.AppendLine("        }");
            cppBuilder.AppendLine("        return 1;");
            cppBuilder.AppendLine("    }");
            cppBuilder.AppendLine("    #else");
            cppBuilder.AppendLine("    int main(int argc, char** argv) {\n        HelixAutoReg::InitializeHelix();\n        void** args = (void**)HelixRuntime::OSAlloc((argc + 2) * sizeof(void*));\n        for(int i = 0; i < argc; i++) args[i] = (void*)argv[i];\n        args[argc] = nullptr;\n        HelixGenerated::GetArgsTracker().Add(args);\n        int status = HelixMain(args);\n        HelixGenerated::GetArgsTracker().FreeAll();\n        HelixRuntime::GC::Shutdown();\n        return status;\n    }");
            cppBuilder.AppendLine("    #endif");
            cppBuilder.AppendLine("#endif");

            SafeWriteFileIfChanged(outputPath, cppBuilder.ToString(), logger);

            string headerPath = Path.Combine(projectDirectory, HelixConstants.HeaderFileName);
            if (!File.Exists(headerPath))
            {
                GenerateRuntimeHeader(projectDirectory, logger);
            }

            AppendBlackboxStructsToHeader(validMetas, projectDirectory, logger);
        }

        private void AppendBlackboxStructsToHeader(List<ClassMetadata> sortedMetas, string projectDirectory, Action<string> logger)
        {
            string headerPath = Path.Combine(projectDirectory, HelixConstants.HeaderFileName);
            if (!File.Exists(headerPath)) return;

            StringBuilder headerAppender = new();
            headerAppender.AppendLine();
            headerAppender.AppendLine("// ========================================================");
            headerAppender.AppendLine("// [Helix Dependency Polyfill]: Synthesizing Missing External Structures");
            headerAppender.AppendLine("// ========================================================");

            bool hasPolyfills = false;
            HashSet<string> fwdDecls = new HashSet<string>();

            foreach (ClassMetadata meta in sortedMetas)
            {
                if (!meta.RequiresPolyfill || meta.Size <= 0 || meta.Fields.Count == 0 || meta.ClassName.Contains("<") || meta.ClassName.StartsWith("std::")) continue;

                hasPolyfills = true;

                foreach (var f in meta.Fields)
                {
                    if (f.IsPointer)
                    {
                        string cleanName = f.TypeSpelling.Replace("*", "").Replace("const ", "").Replace("volatile ", "").Trim();
                        if (cleanName.StartsWith("struct ")) fwdDecls.Add(cleanName + ";");
                        else if (!cleanName.Contains(" ") && cleanName.StartsWith("_")) fwdDecls.Add("struct " + cleanName + ";");
                    }
                }

                headerAppender.AppendLine($"#pragma pack(push, 1)");
                headerAppender.AppendLine($"__declspec(align({meta.Alignment})) struct {meta.ClassName} {{");

                var sortedFields = meta.Fields.OrderBy(f => f.Offset).ToList();
                bool hasOverlaps = false;
                for (int i = 0; i < sortedFields.Count - 1; i++)
                {
                    if (sortedFields[i + 1].Offset < sortedFields[i].Offset + sortedFields[i].Size) { hasOverlaps = true; break; }
                }

                long currentOffset = 0;
                int padCounter = 0;

                if (!hasOverlaps)
                {
                    foreach (var field in sortedFields)
                    {
                        if (field.Offset > currentOffset)
                        {
                            long diff = field.Offset - currentOffset;
                            headerAppender.AppendLine($"    unsigned char _pad{padCounter++}[{diff}];");
                            currentOffset = field.Offset;
                        }

                        string typeSpelling = field.TypeSpelling.Trim();
                        bool isWeird = typeSpelling.Contains("unnamed") || typeSpelling.Contains("anonymous") || typeSpelling.Contains("(unnamed");
                        long fSize = field.Size > 0 ? field.Size : 1;

                        string realDecl;
                        if (isWeird) realDecl = $"unsigned char {field.Name}[{fSize}];";
                        else
                        {
                            int bIdx = typeSpelling.IndexOf('[');
                            if (bIdx != -1) realDecl = $"{typeSpelling.Substring(0, bIdx).Trim()} {field.Name}{typeSpelling.Substring(bIdx)};";
                            else if (typeSpelling.Contains("(*)")) realDecl = $"void* {field.Name};";
                            else realDecl = $"{typeSpelling} {field.Name};";
                        }

                        headerAppender.AppendLine($"    {realDecl}");
                        currentOffset += fSize;
                    }

                    if (meta.Size > currentOffset)
                    {
                        long tailDiff = meta.Size - currentOffset;
                        headerAppender.AppendLine($"    unsigned char _pad{padCounter++}[{tailDiff}];");
                    }
                }
                else
                {
                    headerAppender.AppendLine($"    union {{");
                    foreach (var field in sortedFields)
                    {
                        string typeSpelling = field.TypeSpelling.Trim();
                        bool isWeird = typeSpelling.Contains("unnamed") || typeSpelling.Contains("anonymous") || typeSpelling.Contains("(unnamed");
                        long fSize = field.Size > 0 ? field.Size : 1;

                        string realDecl;
                        if (isWeird) realDecl = $"unsigned char {field.Name}[{fSize}];";
                        else
                        {
                            int bIdx = typeSpelling.IndexOf('[');
                            if (bIdx != -1) realDecl = $"{typeSpelling.Substring(0, bIdx).Trim()} {field.Name}{typeSpelling.Substring(bIdx)};";
                            else if (typeSpelling.Contains("(*)")) realDecl = $"void* {field.Name};";
                            else realDecl = $"{typeSpelling} {field.Name};";
                        }

                        if (field.Offset > 0) headerAppender.AppendLine($"        struct {{ unsigned char _pad{padCounter++}[{field.Offset}]; {realDecl} }};");
                        else headerAppender.AppendLine($"        struct {{ {realDecl} }};");
                    }
                    if (meta.Size > 0) headerAppender.AppendLine($"        unsigned char _struct_size[{meta.Size}];");
                    headerAppender.AppendLine($"    }};");
                }

                headerAppender.AppendLine($"}};");
                headerAppender.AppendLine($"#pragma pack(pop)\n");
            }

            string polyfillStr = "";
            if (hasPolyfills)
            {
                StringBuilder fwdBuilder = new StringBuilder();
                foreach (string fwd in fwdDecls) fwdBuilder.AppendLine($"{fwd}");
                polyfillStr = fwdBuilder.ToString() + "\n" + headerAppender.ToString();
            }

            string oldContent = ForceReadAllText(headerPath); string oldBase = oldContent;
            int markerIdx = oldContent.IndexOf("// [Helix Dependency Polyfill]");
            if (markerIdx != -1) { int bannerStart = oldContent.LastIndexOf("// [Helix Dependency Polyfill]", markerIdx); if (bannerStart != -1) oldBase = oldContent.Substring(0, bannerStart); }

            string finalContent = oldBase.TrimEnd() + (hasPolyfills ? "\r\n\r\n" + polyfillStr.TrimStart() : "\r\n");
            finalContent = s_newlineRegex.Replace(finalContent, "\r\n");

            if (s_newlineRegex.Replace(oldContent.Trim(), "\n") != s_newlineRegex.Replace(finalContent.Trim(), "\n"))
            {
                ForceWriteAllText(headerPath, finalContent);
                LogDebug(logger, $"[CodeGen] Perfect SDK polyfills dynamically appended to header.");
            }
        }

        public void GenerateRuntimeHeader(string projectDirectory, Action<string> logger)
        {
            string outputPath = Path.Combine(projectDirectory, HelixConstants.HeaderFileName);

            string rawHeader = """
            // THIS FILE IS AUTO-GENERATED BY HELIX VSIX PLUGIN.
            // DO NOT MODIFY MANUALLY.
            #pragma once

            #ifdef _MSC_VER
                #pragma warning(disable : 4458 4100 4595 4456 4189 4702 4701 4703 4273 4005 4083 4065)
            #endif

            #ifndef _KERNEL_MODE
                #include <typeinfo>
                #include <type_traits>
            #endif

            #ifndef __clang__
                #ifndef _KERNEL_MODE
                    #ifndef UMDF_USING_NTSTATUS
                        #define UMDF_USING_NTSTATUS
                    #endif
                    #ifndef WIN32_NO_STATUS
                        #define WIN32_NO_STATUS
                    #endif
                #endif

                #ifdef _KERNEL_MODE
                    #include <ntifs.h>
                    #include <ntstrsafe.h>
                    #ifndef __clang__
                        #include <intrin.h>
                    #endif
                #else
                    #ifndef NOMINMAX
                        #define NOMINMAX
                    #endif
                    #ifndef WIN32_LEAN_AND_MEAN
                        #define WIN32_LEAN_AND_MEAN
                    #endif
                    #include <Windows.h>
                    #include <winternl.h>
                    #ifndef __clang__
                        #include <intrin.h>
                    #endif
                    #ifndef NT_SUCCESS
                        #define NT_SUCCESS(Status) (((NTSTATUS)(Status)) >= 0)
                    #endif
                #endif
            #endif

            #include "Core/CoreDefines.h"
            #include "Core/Collections.h"
            #include "Core/ReflectionTypes.h"
            #include "Core/GC.h"
            #include "Core/Reflection.h"
            #include "Core/System.h"
            #include "Core/VmpCore.h"

            #ifdef _KERNEL_MODE
            #ifndef __PLACEMENT_NEW_INLINE
            #define __PLACEMENT_NEW_INLINE
            inline void* __cdecl operator new(size_t, void* _Where) { return _Where; }
            inline void __cdecl operator delete(void*, void*) { }
            #endif
            #endif

            #if defined(_WIN32) && defined(_KERNEL_MODE)
                #include "HAL/Windows/WinKernelProvider.hpp"
                #define HELIX_PLATFORM_INIT(ctx) static HelixRuntime::HAL::WinKernelProvider s_prov; HelixRuntime::HAL::SetPlatform(&s_prov)
            #elif defined(_WIN32)
                #include "HAL/Windows/WinUserProvider.hpp"
                #define HELIX_PLATFORM_INIT(ctx) static HelixRuntime::HAL::WinUserProvider s_prov; HelixRuntime::HAL::SetPlatform(&s_prov)
            #elif defined(__linux__) || defined(__ANDROID__)
                #include "HAL/Linux/LinuxProvider.hpp"
                #define HELIX_PLATFORM_INIT(ctx) static HelixRuntime::HAL::LinuxProvider s_prov; HelixRuntime::HAL::SetPlatform(&s_prov)
            #elif defined(__APPLE__)
                #include "HAL/Apple/DarwinProvider.hpp"
                #define HELIX_PLATFORM_INIT(ctx) static HelixRuntime::HAL::DarwinProvider s_prov; HelixRuntime::HAL::SetPlatform(&s_prov)
            #else
                #define HELIX_PLATFORM_INIT(ctx)
            #endif

            namespace HelixGenerated {
                const char* GetHashName(uint64_t hash);
                uint64_t GetFieldTypeHash(uint64_t classHash, uint64_t fieldHash);
                void* GetMethodPtr(uint64_t classHash, uint64_t methodHash);
                bool VerifyMethodParams(uint64_t classHash, uint64_t methodHash, const uint64_t* pHashes, size_t count);

                struct MetaDB {
                    HelixRuntime::ClassMeta** metas;
                    uint64_t* hashes;
                    const char** names;
                    int count; int cap;
                    void** vtables;
                    uint64_t* vtableHashes;
                    int vtableCount; int vtableCap;
                    MetaDB() : metas(nullptr), hashes(nullptr), names(nullptr), count(0), cap(0), vtables(nullptr), vtableHashes(nullptr), vtableCount(0), vtableCap(0) {}

                    void Add(uint64_t h, const char* n, HelixRuntime::ClassMeta* m) {
                        if (count >= cap) {
                            int nCap = cap == 0 ? 256 : cap * 2;
                            auto nMetas = (HelixRuntime::ClassMeta**)HelixRuntime::OSAlloc(nCap * sizeof(void*));
                            auto nHashes = (uint64_t*)HelixRuntime::OSAlloc(nCap * sizeof(uint64_t));
                            auto nNames = (const char**)HelixRuntime::OSAlloc(nCap * sizeof(void*));
                            if (metas) {
                                HelixRuntime::HMemCpy(nMetas, metas, count * sizeof(void*));
                                HelixRuntime::HMemCpy(nHashes, hashes, count * sizeof(uint64_t));
                                HelixRuntime::HMemCpy(nNames, names, count * sizeof(void*));
                                HelixRuntime::OSFree(metas); HelixRuntime::OSFree(hashes); HelixRuntime::OSFree(names);
                            }
                            metas = nMetas; hashes = nHashes; names = nNames; cap = nCap;
                        }
                        hashes[count] = h; names[count] = n; metas[count] = m; count++;
                    }

                    void AddVTable(void* vtable, uint64_t hash) {
                        if (!vtable) return;
                        if (vtableCount >= vtableCap) {
                            int nCap = vtableCap == 0 ? 256 : vtableCap * 2;
                            void** nVtables = (void**)HelixRuntime::OSAlloc(nCap * sizeof(void*));
                            uint64_t* nHashes = (uint64_t*)HelixRuntime::OSAlloc(nCap * sizeof(uint64_t));
                            if (vtables) {
                                HelixRuntime::HMemCpy(nVtables, vtables, vtableCount * sizeof(void*));
                                HelixRuntime::HMemCpy(nHashes, vtableHashes, vtableCount * sizeof(uint64_t));
                                HelixRuntime::OSFree(vtables); HelixRuntime::OSFree(vtableHashes);
                            }
                            vtables = nVtables; vtableHashes = nHashes; vtableCap = nCap;
                        }
                        vtables[vtableCount] = vtable; vtableHashes[vtableCount] = hash; vtableCount++;
                    }

                    HelixRuntime::ClassMeta* Get(uint64_t h) { for(int i=0; i<count; ++i) if(hashes[i] == h) return metas[i]; return nullptr; }
                    const char* GetName(uint64_t h) { for(int i=0; i<count; ++i) if(hashes[i] == h) return names[i]; return "Unknown"; }
                    uint64_t GetHashByName(const char* n) {
                        for(int i=0; i<count; ++i) {
                            const char* a = names[i]; const char* b = n;
                            while(*a && *b && *a == *b) { a++; b++; }
                            if (*a == '\0' && (*b == '\0' || *b == '@')) return hashes[i];
                        }
                        return 0;
                    }
                    uint64_t GetHashByVTable(void* vtable) {
                        for(int i=0; i<vtableCount; ++i) if(vtables[i] == vtable) return vtableHashes[i];
                        return 0;
                    }
                };
                inline MetaDB& GetDB() { static MetaDB db; return db; }

                struct ArgsTracker {
                    void** allocs; int count; int cap;
                    ArgsTracker() : allocs(nullptr), count(0), cap(0) {}
                    void Add(void* ptr) {
                        if (!ptr) return;
                        if (count >= cap) {
                            int nCap = cap == 0 ? 16 : cap * 2;
                            void** nAllocs = (void**)HelixRuntime::OSAlloc(nCap * sizeof(void*));
                            if (allocs) { HelixRuntime::HMemCpy(nAllocs, allocs, count * sizeof(void*)); HelixRuntime::OSFree(allocs); }
                            allocs = nAllocs; cap = nCap;
                        }
                        allocs[count++] = ptr;
                    }
                    void FreeAll() {
                        for (int i=0; i<count; i++) HelixRuntime::OSFree(allocs[i]);
                        if (allocs) HelixRuntime::OSFree(allocs);
                        allocs = nullptr; count = 0; cap = 0;
                    }
                };
                inline ArgsTracker& GetArgsTracker() { static ArgsTracker t; return t; }
            }

            namespace HelixRuntime {
                template<typename T> struct StripModifiers { using type = T; };
                template<typename T> struct StripModifiers<T*> { using type = typename StripModifiers<T>::type; };
                template<typename T> struct StripModifiers<T&> { using type = typename StripModifiers<T>::type; };
                template<typename T> struct StripModifiers<T&&> { using type = typename StripModifiers<T>::type; };
                template<typename T> struct StripModifiers<const T> { using type = typename StripModifiers<T>::type; };
                template<typename T> struct StripModifiers<volatile T> { using type = typename StripModifiers<T>::type; };
                template<typename T> struct StripParam { using type = T; };
                template<typename T> struct StripParam<T&> { using type = typename StripParam<T>::type; };
                template<typename T> struct StripParam<T&&> { using type = typename StripParam<T>::type; };
                template<typename T> struct StripParam<const T> { using type = typename StripParam<T>::type; };
                template<typename T> struct StripParam<volatile T> { using type = typename StripParam<T>::type; };
                template<typename T> struct RemovePtr { using type = T; };
                template<typename T> struct RemovePtr<T*> { using type = T; };
                
                template<typename T> struct IsArray { static constexpr bool value = false; };
                template<typename T, size_t N> struct IsArray<T[N]> { static constexpr bool value = true; };
                template<typename T> struct RemoveExtent { using type = T; };
                template<typename T, size_t N> struct RemoveExtent<T[N]> { using type = T; };
                
                template<typename T, typename... Args>
                inline auto SmartNew(Args&&... args) {
                    if constexpr (IsArray<T>::value) {
                        using ElementT = typename RemoveExtent<T>::type;
                        constexpr size_t N = sizeof(T) / sizeof(ElementT);
                        void* mem = OSAlloc(sizeof(T));
                        if (mem) {
                            HelixRuntime::HMemSet(mem, 0, sizeof(T));
                            ElementT* arr = (ElementT*)mem;
                            for (size_t i = 0; i < N; ++i) { new(&arr[i]) ElementT(); }
                            auto& ctx = GetLocalContext();
                            uintptr_t state; ctx.Lock(state);
                            ctx.heap.push_back({ mem, GetTypeHash<T>(), false, false, false, 0, nullptr, [](void* p, uint32_t) {
                                ElementT* a = (ElementT*)p;
                                for (size_t i = 0; i < N; ++i) { a[i].~ElementT(); }
                                OSFree(p);
                            } });
                            ctx.Unlock(state);
                        }
                        return HelixRef<ElementT>((ElementT*)mem);
                    } else {
                        return GCAllocate<T>(HelixRuntime::forward<Args>(args)...);
                    }
                }
                
                template<typename ObjT>
                class ReflectorProxy {
                public:
                    ObjT* instance;
                    uint64_t rootClassHash;
                    static constexpr uint64_t HashStr(const char* str, size_t len) { 
                        uint64_t h = 14695981039346656037ull; 
                        for(size_t i=0; i<len; ++i){ h ^= (uint8_t)str[i]; h *= 1099511628211ull; } 
                        return h; 
                    }
                    ReflectorProxy(ObjT* obj) : instance(obj), rootClassHash(GetTypeHash<ObjT>()) {}
                    ReflectorProxy(ObjT* obj, uint64_t overrideHash) : instance(obj), rootClassHash(overrideHash) {}
                    
                    const char* GetClassName() { return HelixGenerated::GetDB().GetName(rootClassHash); }
                    int GetFieldCount() { auto meta = HelixGenerated::GetDB().Get(rootClassHash); return meta ? (int)meta->Fields.size() : 0; }
                    const char* GetFieldName(int index) {
                        auto meta = HelixGenerated::GetDB().Get(rootClassHash);
                        if (meta && index >= 0 && index < (int)meta->Fields.size()) return HelixGenerated::GetHashName(meta->Fields[index].nameHash);
                        return nullptr;
                    }
                    int GetMethodCount() { auto meta = HelixGenerated::GetDB().Get(rootClassHash); return meta ? (int)meta->Methods.size() : 0; }
                    const char* GetMethodName(int index) {
                        auto meta = HelixGenerated::GetDB().Get(rootClassHash);
                        if (meta && index >= 0 && index < (int)meta->Methods.size()) return HelixGenerated::GetHashName(meta->Methods[index].nameHash);
                        return nullptr;
                    }
                    
                    void* ResolvePath(const char* path, uint64_t& outClassHash, bool derefLeafPointer = false) {
                        void* cursor = instance; outClassHash = rootClassHash; const char* p = path;
                        while(*p) {
                            const char* dot = p; while(*dot && *dot != '.') dot++;
                            uint64_t nodeHash = HashStr(p, dot - p);
                            auto meta = HelixGenerated::GetDB().Get(outClassHash); bool found = false;
                            if(!meta) return nullptr;
                            for(size_t i=0; i<meta->Fields.size(); ++i) {
                                if (meta->Fields[i].nameHash == nodeHash) {
                                    cursor = (void*)((char*)cursor + meta->Fields[i].offset);
                                    p = dot; if(*p == '.') p++;
                                    if (meta->Fields[i].isPointer) {
                                        if (*p != '\0' || derefLeafPointer) {
                                            cursor = *(void**)cursor;
                                            if(!cursor) return nullptr;
                                        }
                                    }
                                    outClassHash = HelixGenerated::GetFieldTypeHash(outClassHash, nodeHash);
                                    found = true; break;
                                }
                            }
                            if(!found) return nullptr;
                        }
                        return cursor;
                    }
                    template<typename ValT> bool SetValue(const char* path, ValT val) { uint64_t cHash; void* ptr = ResolvePath(path, cHash, false); if(ptr){ *(ValT*)ptr = val; return true; } return false; }
                    template<typename ValT> ValT GetValue(const char* path, ValT def = {}) { uint64_t cHash; void* ptr = ResolvePath(path, cHash, false); if(ptr) return *(ValT*)ptr; return def; }
                    
                    intptr_t GetOffset(const char* path) {
                        uint64_t cHash = rootClassHash;
                        intptr_t totalOffset = 0;
                        const char* p = path;
                        while(*p) {
                            const char* dot = p; while(*dot && *dot != '.') dot++;
                            uint64_t nodeHash = HashStr(p, dot - p);
                            auto meta = HelixGenerated::GetDB().Get(cHash); bool found = false;
                            if(!meta) return -1;
                            for(size_t i=0; i<meta->Fields.size(); ++i) {
                                if (meta->Fields[i].nameHash == nodeHash) {
                                    totalOffset += meta->Fields[i].offset;
                                    p = dot; if(*p == '.') p++;
                                    if (meta->Fields[i].isPointer) {
                                        if (*p != '\0') {
                                            totalOffset = 0; 
                                        }
                                    }
                                    cHash = HelixGenerated::GetFieldTypeHash(cHash, nodeHash);
                                    found = true; break;
                                }
                            }
                            if(!found) return -1;
                        }
                        return totalOffset;
                    }

                    template<typename Ret = void, typename... Args> Ret Invoke(const char* path, Args... args) {
                        char objPath[256] = {0}; const char* lastDot = nullptr; const char* tmp = path;
                        while(*tmp) { if(*tmp == '.') lastDot = tmp; tmp++; }
                        void* targetInst = instance; uint64_t targetClass = rootClassHash;
                        if(lastDot) { size_t len = lastDot - path; for(size_t i=0; i<len; ++i) objPath[i] = path[i]; objPath[len] = '\0'; targetInst = ResolvePath(objPath, targetClass, true); path = lastDot + 1; }
                        if(!targetInst) return Ret();
                        uint64_t methodHash = HashStr(path, tmp - path);
                        void* rawFuncPtr = HelixGenerated::GetMethodPtr(targetClass, methodHash);
                        if(!rawFuncPtr) return Ret();
                        uint64_t pHashes[] = { 0, GetTypeHash<typename StripParam<Args>::type>()... };
                        if(HelixGenerated::VerifyMethodParams(targetClass, methodHash, sizeof...(Args) > 0 ? pHashes + 1 : nullptr, sizeof...(Args))) {
                            using Func = Ret(*)(void*, typename StripParam<Args>::type...); Func f = (Func)rawFuncPtr;
                            return f(targetInst, HelixRuntime::forward<Args>(args)...);
                        }
                        return Ret();
                    }
                };

                template<typename T> struct UnwrapHelixRef { using type = T; static T* get(T& obj) { return &obj; } };
                template<typename T> struct UnwrapHelixRef<T*> { using type = T; static T* get(T* const& obj) { return (T*)obj; } };
                template<typename T> struct UnwrapHelixRef<HelixRef<T>> { using type = T; static T* get(HelixRef<T>& obj) { return &(*obj); } };
                
                template<typename T>
                inline ReflectorProxy<typename StripModifiers<typename UnwrapHelixRef<typename remove_reference<T>::type>::type>::type> MakeProxy(T& obj) {
                    using RawT = typename StripModifiers<typename UnwrapHelixRef<typename remove_reference<T>::type>::type>::type;
                    return ReflectorProxy<RawT>((RawT*)UnwrapHelixRef<typename remove_reference<T>::type>::get(obj));
                }

                template<typename T>
                inline ReflectorProxy<typename StripModifiers<T>::type> MakeProxy(T* obj) {
                    using RawT = typename StripModifiers<T>::type;
                    return ReflectorProxy<RawT>((RawT*)obj);
                }
                
                inline auto MakeProxy(void* obj, const char* typeName) {
                    uint64_t hash = HelixGenerated::GetDB().GetHashByName(typeName);
                    return ReflectorProxy<void>(obj, hash);
                }
                
                #ifdef _KERNEL_MODE
                extern "C" unsigned char MmIsAddressValid(void* VirtualAddress);
                #endif
                
                inline uint64_t SafeProbeRTTI(void* p) {
                    if (!p) return 0;
                    uint64_t realHash = 0;
                    #ifdef _KERNEL_MODE
                    if (!MmIsAddressValid(p)) return 0;
                    __try { 
                        void** vtable = *(void***)p;
                        if (vtable && MmIsAddressValid(vtable)) {
                            realHash = HelixGenerated::GetDB().GetHashByVTable((void*)vtable);
                        }
                    } __except(1 /* EXCEPTION_EXECUTE_HANDLER */) {
                        realHash = 0;
                    }
                    #endif
                    return realHash;
                }
                
                class HelixAny {
                    void* ptr;
                    uint64_t typeHash;
                public:
                    HelixAny() : ptr(nullptr), typeHash(0) {}
                    HelixAny(decltype(nullptr)) : ptr(nullptr), typeHash(0) {}
                    HelixAny(void* p, uint64_t hash) : ptr(p), typeHash(hash) { if (ptr) GC::AddRoot(ptr); }
                    HelixAny(const HelixAny& other) : ptr(other.ptr), typeHash(other.typeHash) { if (ptr) GC::AddRoot(ptr); }
                    HelixAny(HelixAny&& other) : ptr(other.ptr), typeHash(other.typeHash) { other.ptr = nullptr; other.typeHash = 0; }
                    
                    template<typename U> HelixAny(U* p) {
                        ptr = (void*)p;
                        if (ptr) GC::AddRoot(ptr);
                        typeHash = HelixRuntime::GetTypeHash<typename StripModifiers<U>::type>();

                        #ifndef _KERNEL_MODE
                        if constexpr (std::is_polymorphic_v<U>) {
                            if (p) {
                                const char* rtti = typeid(*p).name();
                                if (strncmp(rtti, "class ", 6) == 0) rtti += 6;
                                else if (strncmp(rtti, "struct ", 7) == 0) rtti += 7;
                                uint64_t realHash = HelixGenerated::GetDB().GetHashByName(rtti);
                                if (realHash != 0) typeHash = realHash;
                            }
                        }
                        #else
                        if constexpr (__is_polymorphic(U)) {
                            uint64_t realHash = SafeProbeRTTI((void*)p);
                            if (realHash != 0) typeHash = realHash;
                        }
                        #endif
                    }
                    template<typename U> HelixAny(const HelixRef<U>& other) : HelixAny(other.operator->()) {}
                    template<typename U> HelixAny(HelixRef<U>& other) : HelixAny(other.operator->()) {}
                    
                    HelixAny& operator=(const HelixAny& other) {
                        if (ptr != other.ptr) { if (ptr) GC::RemoveRoot(ptr); ptr = other.ptr; typeHash = other.typeHash; if (ptr) GC::AddRoot(ptr); }
                        return *this;
                    }
                    HelixAny& operator=(HelixAny&& other) {
                        if (this != &other) { if (ptr) GC::RemoveRoot(ptr); ptr = other.ptr; typeHash = other.typeHash; other.ptr = nullptr; other.typeHash = 0; }
                        return *this;
                    }
                    HelixAny& operator=(decltype(nullptr)) {
                        if (ptr) { GC::RemoveRoot(ptr); ptr = nullptr; typeHash = 0; }
                        return *this;
                    }
                    ~HelixAny() { if (ptr) { GC::RemoveRoot(ptr); ptr = nullptr; GC::CollectLocal(); } }
                    
                    uint64_t GetDynamicHash() const { return typeHash; }


                    struct DerefProxy {
                        void* p;
                        template<typename U> operator U&() const { return *(U*)p; }
                    };

                    void* operator&() const { return ptr; }
                    
                    DerefProxy operator*() const { return { ptr }; }
                    
                    template<typename U> operator U*() const { return (U*)ptr; }
                    operator void*() const { return ptr; }
                    bool operator!=(decltype(nullptr)) const { return ptr != nullptr; }
                    bool operator==(decltype(nullptr)) const { return ptr == nullptr; }
                };
                
                inline auto MakeProxy(HelixAny& obj) { return ReflectorProxy<void>(&obj, obj.GetDynamicHash()); }
                inline auto MakeProxy(const HelixAny& obj) { return ReflectorProxy<void>(&obj, obj.GetDynamicHash()); }
                inline auto MakeProxy(HelixAny&& obj) { return ReflectorProxy<void>(&obj, obj.GetDynamicHash()); }
            }

            using Any = HelixRuntime::HelixAny;
            template<typename T, typename... Args> inline auto New(Args&&... args) { return HelixRuntime::SmartNew<T>(HelixRuntime::forward<Args>(args)...); }
            template<typename... Args> inline auto Reflec(Args&&... args) { return HelixRuntime::MakeProxy(HelixRuntime::forward<Args>(args)...); }

            template<typename T> inline auto Serialize(T& obj) { using RawT = typename HelixRuntime::StripModifiers<typename HelixRuntime::UnwrapHelixRef<T>::type>::type; return HelixRuntime::DumpMemory<RawT>(*HelixRuntime::UnwrapHelixRef<T>::get(obj)); }
            template<typename T> inline auto Serialize(const T& obj) { using RawT = typename HelixRuntime::StripModifiers<typename HelixRuntime::UnwrapHelixRef<T>::type>::type; return HelixRuntime::DumpMemory<RawT>(*HelixRuntime::UnwrapHelixRef<T>::get(const_cast<T&>(obj))); }

            template<typename T, typename... Args> inline auto Deserialize(Args&&... args) { return HelixRuntime::LoadMemory<T>(HelixRuntime::forward<Args>(args)...); }
            template<typename T> inline auto Read(uint32_t pid, void* address) { return HelixRuntime::HelixSystem::Read<T>(pid, address); }
            template<typename T> inline auto Write(uint32_t pid, void* address, const T& value) { return HelixRuntime::HelixSystem::Write<T>(pid, address, value); }
            inline auto Cpu() { return HelixRuntime::HelixSystem::Cpu(); }
            inline auto System() { return HelixRuntime::HelixSystem::System(); }
            inline auto Mac() { return HelixRuntime::HelixSystem::Mac(); }
            inline auto Disk() { return HelixRuntime::HelixSystem::Disk(); }
            #ifdef HELIX_GET_FRAME
            inline void* Allocate(uint32_t pid, size_t size, void* frame = HELIX_GET_FRAME()) { return HelixRuntime::HelixSystem::AllocRemote(pid, size, frame); }
            #else
            inline void* Allocate(uint32_t pid, size_t size) { return HelixRuntime::HelixSystem::AllocRemote(pid, size, nullptr); }
            #endif

            #undef API

            #if defined(__clang__)
                #define Extern [[clang::annotate("HelixExtern")]]
                #define Vmp [[clang::annotate("HelixVmp")]]
                #define API(...) extern "C" [[clang::annotate("HelixApi:" #__VA_ARGS__)]]
            #else
                #define Extern
                #define Vmp
                #if defined(_WIN32)
                    #ifdef HELIX_CPP_GENERATION
                        #define API(...) extern "C"
                    #else
                        #define API(...) extern "C" __declspec(dllexport)
                    #endif
                #else
                    #define API(...) extern "C" __attribute__((visibility("default")))
                #endif
            #endif

            #ifndef HELIX_CPP_GENERATION
                #define main HelixMain
            #endif

            #define HELIX_CONCAT_IMPL(a, b) a##b
            #define HELIX_CONCAT(a, b) HELIX_CONCAT_IMPL(a, b)
            #define Offset(n) uint8_t HELIX_CONCAT(_helix_pad_, __COUNTER__)[n]
            """;

            StringBuilder finalContent = new StringBuilder();
            finalContent.Append(rawHeader);

            string newBase = finalContent.ToString();
            bool shouldWriteBase = true;
            string polyfills = "";
            if (File.Exists(outputPath))
            {
                string oldContent = ForceReadAllText(outputPath);
                string oldBase = oldContent;
                int markerIdx = oldContent.IndexOf("// [Helix Dependency Polyfill]");
                if (markerIdx != -1)
                {
                    int bannerStart = oldContent.LastIndexOf("// [Helix Dependency Polyfill]", markerIdx);
                    if (bannerStart != -1)
                    {
                        oldBase = oldContent.Substring(0, bannerStart);
                        polyfills = oldContent.Substring(bannerStart);
                    }
                }
                if (s_newlineRegex.Replace(oldBase.Trim(), "\n") == s_newlineRegex.Replace(newBase.Trim(), "\n")) shouldWriteBase = false;
            }

            if (shouldWriteBase)
            {
                string finalOut = newBase.TrimEnd() + (string.IsNullOrEmpty(polyfills) ? "\r\n" : "\r\n\r\n" + polyfills.TrimStart());
                ForceWriteAllText(outputPath, finalOut);
            }
        }
        private string MakeRelativePath(string fromPath, string toPath)
        {
            Uri fromUri = new(fromPath.EndsWith("\\") ? fromPath : fromPath + "\\");
            Uri toUri = new(toPath);
            return Uri.UnescapeDataString(fromUri.MakeRelativeUri(toUri).ToString()).Replace('/', '\\');
        }
    }
}
