using ClangSharp;
using ClangSharp.Interop;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Helix
{
    public unsafe class ClangScanner
    {
        public static List<(string Type, string Name, bool IsStatic)> GlobalPointers = [];
        public static List<(string ClassName, string FuncName, bool IsStatic, string ReturnType, string ArgString, List<string> BodyBlocks, List<string> LiftedDecls, List<string> ExternParams)> VmpTargets = [];

        public static HashSet<string> ExternNames = new();
        public static HashSet<string> DiscoveredReflectionRoots = new();

        // 并发 IO 内存缓存，彻底抹杀重复的磁盘读取放大
        private readonly ConcurrentDictionary<string, byte[]> _ioCache = new();

        public string IntermediateDirectory { get; set; } = string.Empty;
        public string RuntimeDirectory { get; set; } = string.Empty;
        public List<string> SdkPaths { get; set; } = new List<string>();
        public string CppStandard { get; set; } = "-std=c++20";

        private static readonly System.Text.RegularExpressions.Regex s_keywordRegex = new(
            @"(?:New|Deserialize|Serialize|Read|Write|Allocate|Reflec)\s*<\s*([a-zA-Z0-9_:]+)(?:\[\d+\])?\s*>",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly System.Text.RegularExpressions.Regex s_crossRingRegex = new(
            @"\(\s*([a-zA-Z_][a-zA-Z0-9_:]*)\s*\*\s*\)|sizeof\s*\(\s*([a-zA-Z_][a-zA-Z0-9_:]*)\s*\)|\b([a-zA-Z_][a-zA-Z0-9_:]*)\s*\*\s+[a-zA-Z_]",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        private void LogDebug(Action<string> logger, string message)
        {
#if DEBUG
            logger?.Invoke(message);
#endif
        }

        public Dictionary<string, ClassMetadata> Parse(List<string> sourceFiles, string projectDirectory, string platform, bool isKernelMode, bool enableCrossRing, Action<string> logger)
        {
            GlobalPointers.Clear();
            VmpTargets.Clear();
            ExternNames.Clear();
            DiscoveredReflectionRoots.Clear();
            _ioCache.Clear();

            List<string> targetTypes = new List<string>();

            foreach (string file in sourceFiles)
            {
                if (!File.Exists(file)) continue;
                try
                {
                    string content = File.ReadAllText(file);
                    var cleanAndAdd = (string rawType, bool isRoot) => {
                        if (string.IsNullOrEmpty(rawType)) return;
                        rawType = rawType.Trim();
                        if (rawType.StartsWith("const ")) rawType = rawType.Substring(6).Trim();
                        if (rawType.StartsWith("volatile ")) rawType = rawType.Substring(9).Trim();
                        if (string.IsNullOrEmpty(rawType)) return;

                        if (isRoot) lock (DiscoveredReflectionRoots) { DiscoveredReflectionRoots.Add(rawType); }
                        lock (targetTypes) { if (!targetTypes.Contains(rawType)) targetTypes.Add(rawType); }
                    };

                    // 1. 核心关键字 (Reflec, Serialize 等) 始终需要提取
                    foreach (System.Text.RegularExpressions.Match match in s_keywordRegex.Matches(content))
                    {
                        cleanAndAdd(match.Groups[1].Value, true);
                    }

                    // 2. [彻底优化]: 仅在跨环开关开启时，才执行极度消耗算力的跨环特征码正则扫描！
                    if (enableCrossRing)
                    {
                        foreach (System.Text.RegularExpressions.Match match in s_crossRingRegex.Matches(content))
                        {
                            cleanAndAdd(match.Groups.Cast<System.Text.RegularExpressions.Group>().Skip(1).FirstOrDefault(g => g.Success)?.Value, false);
                        }
                    }
                }
                catch { }
            }

            var (passTarget, definedTarget) = RunSinglePass(sourceFiles, projectDirectory, platform, isKernelMode, targetTypes, true, logger);

            bool needsOppositePass = false;

            if (enableCrossRing)
            {
                foreach (var t in targetTypes)
                {
                    if (!passTarget.ContainsKey(t) || passTarget[t].Size <= 0)
                    {
                        needsOppositePass = true;
                        break;
                    }
                }
            }

            Dictionary<string, ClassMetadata> passOpposite = new Dictionary<string, ClassMetadata>();
            HashSet<string> definedOpposite = new HashSet<string>();

            if (needsOppositePass)
            {
                LogDebug(logger, $"[Helix AST] Target ring missing cross-ring entities. Initiating Opposite Pass...");
                var oppositeResult = RunSinglePass(sourceFiles, projectDirectory, platform, !isKernelMode, targetTypes, false, logger);
                passOpposite = oppositeResult.Registry;
                definedOpposite = oppositeResult.DefinedTypes;
            }
            else
            {
                if (!enableCrossRing)
                {
                    LogDebug(logger, $"[Helix AST] Cross-Ring macro disabled. Bypassing Opposite Pass entirely!");
                }
                else
                {
                    LogDebug(logger, $"[Helix AST] All entities resolved in Target ring. Bypassing Opposite Pass (Lazy Evaluation)!");
                }
            }

            var finalRegistry = new Dictionary<string, ClassMetadata>();
            var allKeys = new HashSet<string>(passTarget.Keys);
            allKeys.UnionWith(passOpposite.Keys);

            LogDebug(logger, "\n==================== [Helix Realm Truth Table] ====================");

            foreach (var key in allKeys)
            {
                passTarget.TryGetValue(key, out var metaTarget);
                passOpposite.TryGetValue(key, out var metaOpposite);

                bool validTarget = definedTarget.Contains(key) || (metaTarget != null && metaTarget.Size > 0);
                bool validOpposite = definedOpposite.Contains(key) || (metaOpposite != null && metaOpposite.Size > 0);

                ClassMetadata mergedMeta = metaTarget ?? metaOpposite;
                if (metaTarget != null && metaOpposite != null) mergedMeta = (metaTarget.Size > 0 || metaTarget.Fields.Count > 0) ? metaTarget : metaOpposite;

                if (mergedMeta.IsExternal && mergedMeta.Size > 0 && !mergedMeta.ClassName.Contains("<"))
                {
                    mergedMeta.RequiresPolyfill = !validTarget && validOpposite;
                }
                else
                {
                    mergedMeta.RequiresPolyfill = false;
                }

                finalRegistry[key] = mergedMeta;
            }

            Queue<string> purgeQueue = new Queue<string>();
            foreach (string root in DiscoveredReflectionRoots) if (finalRegistry.ContainsKey(root)) purgeQueue.Enqueue(root);
            foreach (var kvp in finalRegistry) if (kvp.Value.RequiresPolyfill) purgeQueue.Enqueue(kvp.Key);
            foreach (var vmp in VmpTargets) if (!string.IsNullOrEmpty(vmp.ClassName) && finalRegistry.ContainsKey(vmp.ClassName)) purgeQueue.Enqueue(vmp.ClassName);

            HashSet<string> retainedKeys = new HashSet<string>();
            while (purgeQueue.Count > 0)
            {
                string current = purgeQueue.Dequeue();
                if (!retainedKeys.Add(current)) continue;

                ClassMetadata meta = finalRegistry[current];
                foreach (var field in meta.Fields)
                {
                    ReadOnlySpan<char> fTypeSpan = field.TypeSpelling.AsSpan();
                    int bIdx = fTypeSpan.IndexOf('[');
                    if (bIdx != -1) fTypeSpan = fTypeSpan.Slice(0, bIdx);

                    fTypeSpan = fTypeSpan.TrimEnd('*').TrimEnd('&').TrimEnd(' ');
                    int start = 0;
                    while (start < fTypeSpan.Length)
                    {
                        var slice = fTypeSpan.Slice(start);
                        if (slice.StartsWith("struct ".AsSpan())) start += 7;
                        else if (slice.StartsWith("class ".AsSpan())) start += 6;
                        else if (slice.StartsWith("union ".AsSpan())) start += 6;
                        else if (slice.StartsWith("const ".AsSpan())) start += 6;
                        else if (slice.StartsWith("volatile ".AsSpan())) start += 9;
                        else break;
                        while (start < fTypeSpan.Length && fTypeSpan[start] == ' ') start++;
                    }
                    string fType = start > 0 ? fTypeSpan.Slice(start).Trim().ToString() : fTypeSpan.Trim().ToString();

                    if (finalRegistry.ContainsKey(fType) && !retainedKeys.Contains(fType)) purgeQueue.Enqueue(fType);
                }
            }

            var keysToRemove = finalRegistry.Keys.Where(k => !retainedKeys.Contains(k)).ToList();
            foreach (string k in keysToRemove) finalRegistry.Remove(k);

            int pointerPurged = GlobalPointers.RemoveAll(gVar => {
                ReadOnlySpan<char> tNameSpan = gVar.Type.AsSpan().TrimEnd('*').TrimEnd('&').TrimEnd(' ');
                int start = 0;
                while (start < tNameSpan.Length)
                {
                    var slice = tNameSpan.Slice(start);
                    if (slice.StartsWith("struct ".AsSpan())) start += 7;
                    else if (slice.StartsWith("class ".AsSpan())) start += 6;
                    else if (slice.StartsWith("union ".AsSpan())) start += 6;
                    else if (slice.StartsWith("const ".AsSpan())) start += 6;
                    else if (slice.StartsWith("volatile ".AsSpan())) start += 9;
                    else break;
                    while (start < tNameSpan.Length && tNameSpan[start] == ' ') start++;
                }
                tNameSpan = start > 0 ? tNameSpan.Slice(start).Trim() : tNameSpan.Trim();
                int bIdx = tNameSpan.IndexOf('[');
                if (bIdx != -1) tNameSpan = tNameSpan.Slice(0, bIdx).Trim();

                string tName = tNameSpan.ToString();
                return !string.IsNullOrEmpty(tName) && !retainedKeys.Contains(tName);
            });

            LogDebug(logger, $"[Helix AST GC] Purge Complete! Vaporized {keysToRemove.Count} irrelevant structs and {pointerPurged} external pointers.");
            LogDebug(logger, "===================================================================\n");

            return finalRegistry;
        }

        private string GetCleanTypeName(CXType type)
        {
            var clean = (string s) => {
                if (string.IsNullOrEmpty(s)) return "";
                ReadOnlySpan<char> span = s.AsSpan();
                int start = 0;
                while (start < span.Length)
                {
                    var slice = span.Slice(start);
                    if (slice.StartsWith("struct ".AsSpan())) start += 7;
                    else if (slice.StartsWith("class ".AsSpan())) start += 6;
                    else if (slice.StartsWith("union ".AsSpan())) start += 6;
                    else if (slice.StartsWith("const ".AsSpan())) start += 6;
                    else if (slice.StartsWith("volatile ".AsSpan())) start += 9;
                    else break;
                    while (start < span.Length && span[start] == ' ') start++;
                }
                return start > 0 ? span.Slice(start).Trim().ToString() : span.Trim().ToString();
            };

            string name = clean(type.Spelling.ToString());
            string canName = clean(clang.getCanonicalType(type).Spelling.ToString());

            if (string.IsNullOrEmpty(name) || name.Contains("unexposed") || (!name.Contains("::") && canName.Contains("::"))) name = canName;
            if (string.IsNullOrEmpty(name))
            {
                CXCursor decl = clang.getTypeDeclaration(type);
                if (decl.Kind == CXCursorKind.CXCursor_NoDeclFound) decl = clang.getTypeDeclaration(clang.getCanonicalType(type));
                name = decl.Spelling.ToString();
            }
            return name.Trim();
        }

        private CXType GetBaseRecordType(CXType type)
        {
            CXType original = type;
            while (original.kind == CXTypeKind.CXType_Pointer || original.kind == CXTypeKind.CXType_IncompleteArray || original.kind == CXTypeKind.CXType_ConstantArray || original.kind == CXTypeKind.CXType_LValueReference || original.kind == CXTypeKind.CXType_RValueReference)
            {
                CXType p = clang.getPointeeType(original);
                if (p.kind == CXTypeKind.CXType_Invalid) p = clang.getArrayElementType(original);
                if (p.kind == CXTypeKind.CXType_Invalid) break;
                original = p;
            }
            return original;
        }

        private bool IsHeaderFile(CXCursor cursor)
        {
            cursor.Location.GetFileLocation(out CXFile file, out _, out _, out _);
            string path = file.Name.ToString();
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path).ToLower();
            return ext == ".h" || ext == ".hpp" || ext == ".hxx" || ext == ".inl";
        }

        private bool IsSmartBoundary(CXType type)
        {
            CXType baseType = GetBaseRecordType(type);
            CXType canonical = clang.getCanonicalType(baseType);
            CXCursor typeDecl = clang.getTypeDeclaration(canonical);
            if (typeDecl.Kind == CXCursorKind.CXCursor_NoDeclFound) typeDecl = clang.getTypeDeclaration(baseType);

            string typeName = canonical.Spelling.ToString();
            if (string.IsNullOrEmpty(typeName)) typeName = baseType.Spelling.ToString();

            if (typeDecl.kind == 0 || canonical.kind <= CXTypeKind.CXType_NullPtr) return true;
            if (typeName.Contains("<") || typeName.Contains(">")) return true;

            if (clang.Type_getSizeOf(canonical) < 0 && canonical.kind != CXTypeKind.CXType_Record) return true;

            if (clang.CXXRecord_isAbstract(typeDecl) != 0) return true;
            if (typeName.Contains("(lambda") || typeName.Contains("(unnamed") || typeName.Contains("anonymous")) return true;

            return false;
        }

        private (Dictionary<string, ClassMetadata> Registry, HashSet<string> DefinedTypes) RunSinglePass(List<string> sourceFiles, string projectDirectory, string platform, bool isKernelMode, List<string> explicitTargetTypes, bool extractGlobalsAndVmps, Action<string> logger)
        {
            string targetArch = "--target=x86_64-pc-windows-msvc";
            string archMacro = "-D_AMD64_";
            string winMacro = "-D_WIN64";

            if (platform.Equals("Win32", StringComparison.OrdinalIgnoreCase) || platform.Equals("x86", StringComparison.OrdinalIgnoreCase))
            {
                targetArch = "--target=i686-pc-windows-msvc";
                archMacro = "-D_X86_";
                winMacro = "-D_WIN32";
            }
            else if (platform.Equals("ARM64", StringComparison.OrdinalIgnoreCase))
            {
                targetArch = "--target=aarch64-pc-windows-msvc";
                archMacro = "-D_ARM64_";
            }

            List<string> commandLineArgs = [
                "-xc++", CppStandard, targetArch, "-fms-extensions", "-fms-compatibility", archMacro, winMacro, "-Wno-everything", $"-I{projectDirectory}"
            ];

            if (isKernelMode) commandLineArgs.Add("-D_KERNEL_MODE");
            foreach (var sdkPath in SdkPaths) commandLineArgs.Add($"-I{sdkPath}");
            if (!string.IsNullOrEmpty(IntermediateDirectory)) commandLineArgs.Add($"-I{IntermediateDirectory}");
            if (!string.IsNullOrEmpty(RuntimeDirectory)) commandLineArgs.Add($"-I{RuntimeDirectory}");

            var results = new ConcurrentBag<(Dictionary<string, ClassMetadata> Reg, HashSet<string> Def)>();
            var threadRoots = new ConcurrentBag<string>(explicitTargetTypes);

            Parallel.ForEach(sourceFiles, file =>
            {
                if (!File.Exists(file)) return;

                using CXIndex index = CXIndex.Create();
                CXTranslationUnit translationUnit = CXTranslationUnit.Parse(index, file, commandLineArgs.ToArray(), Array.Empty<CXUnsavedFile>(), CXTranslationUnit_Flags.CXTranslationUnit_KeepGoing);
                if (translationUnit == null) return;

                Dictionary<string, ClassMetadata> localRegistry = new Dictionary<string, ClassMetadata>();
                HashSet<string> localDefined = new HashSet<string>();
                var crawlQueue = new Queue<(CXCursor, string)>();
                HashSet<string> localAllRoots = new HashSet<string>(explicitTargetTypes);

                Action<CXType> tryEnqueueRoot = (type) => {
                    if (!IsSmartBoundary(type))
                    {
                        CXType baseType = GetBaseRecordType(type);
                        string name = GetCleanTypeName(baseType);
                        if (!string.IsNullOrEmpty(name) && !localRegistry.ContainsKey(name))
                        {
                            CXCursor decl = clang.getTypeDeclaration(baseType);
                            if (decl.Kind == CXCursorKind.CXCursor_NoDeclFound) decl = clang.getTypeDeclaration(clang.getCanonicalType(baseType));
                            localRegistry[name] = new ClassMetadata { ClassName = name, Size = -1 };
                            crawlQueue.Enqueue((decl, name));
                        }
                    }
                };

                translationUnit.Cursor.VisitChildren((childCursor, parent, clientData) =>
                {
                    if (childCursor.Kind == CXCursorKind.CXCursor_StructDecl || childCursor.Kind == CXCursorKind.CXCursor_ClassDecl || childCursor.Kind == CXCursorKind.CXCursor_UnionDecl || childCursor.Kind == CXCursorKind.CXCursor_TypedefDecl)
                    {
                        if (IsHeaderFile(childCursor))
                        {
                            string name = GetCleanTypeName(clang.getCursorType(childCursor));
                            if (!string.IsNullOrEmpty(name))
                            {
                                localDefined.Add(name);

                                childCursor.Location.GetFileLocation(out CXFile cxFile, out _, out _, out _);
                                string nodeFilePath = cxFile.Name.ToString().Replace("\\", "/").ToLower();
                                string projDir = projectDirectory.Replace("\\", "/").ToLower().TrimEnd('/', '\\');
                                bool isUserProject = !string.IsNullOrEmpty(nodeFilePath) && nodeFilePath.StartsWith(projDir);

                                if ((isUserProject || localAllRoots.Contains(name)) && !IsSmartBoundary(clang.getCursorType(childCursor)))
                                {
                                    if (!localRegistry.ContainsKey(name))
                                    {
                                        localRegistry[name] = new ClassMetadata { ClassName = name, Size = -1 };
                                        crawlQueue.Enqueue((childCursor, name));
                                    }
                                }
                            }
                        }
                    }

                    if (childCursor.Kind == CXCursorKind.CXCursor_CallExpr)
                    {
                        CXCursor refDecl = clang.getCursorReferenced(childCursor);
                        string funcName = refDecl.Spelling.ToString();
                        string parentName = clang.getCursorSemanticParent(refDecl).Spelling.ToString();

                        if (funcName == "MakeProxy" || funcName == "SmartNew" || funcName == "Read" || funcName == "Write" || funcName == "Serialize" || funcName == "Deserialize" || funcName == "Allocate" || (parentName == "HelixAny" && (funcName == "HelixAny" || funcName == "operator=")))
                        {
                            CXType retType = clang.getCursorResultType(refDecl);
                            var match = System.Text.RegularExpressions.Regex.Match(retType.Spelling.ToString(), @"(?:ReflectorProxy|HelixRef)\s*<\s*([^>]+)\s*>");
                            if (match.Success)
                            {
                                string tName = match.Groups[1].Value.Replace("struct ", "").Replace("class ", "").Replace("union ", "").Trim();
                                if (!IsSmartBoundary(retType) && !localAllRoots.Contains(tName)) localAllRoots.Add(tName);
                                lock (DiscoveredReflectionRoots) { DiscoveredReflectionRoots.Add(tName); }
                            }
                            int numArgs = clang.Cursor_getNumArguments(childCursor);
                            for (uint i = 0; i < numArgs; i++) tryEnqueueRoot(clang.getCursorType(clang.Cursor_getArgument(childCursor, i)));
                        }
                    }
                    else if (childCursor.Kind == CXCursorKind.CXCursor_VarDecl)
                    {
                        string typeName = GetCleanTypeName(clang.getCursorType(childCursor));
                        if (typeName == "Any" || typeName == "HelixAny")
                        {
                            childCursor.VisitChildren((c, p, d) => {
                                if (c.Kind >= CXCursorKind.CXCursor_FirstExpr && c.Kind <= CXCursorKind.CXCursor_LastExpr) tryEnqueueRoot(clang.getCursorType(c));
                                return CXChildVisitResult.CXChildVisit_Continue;
                            }, default);
                        }
                    }

                    if (childCursor.Kind == CXCursorKind.CXCursor_FieldDecl || childCursor.Kind == CXCursorKind.CXCursor_VarDecl || childCursor.Kind == CXCursorKind.CXCursor_ParmDecl)
                    {
                        childCursor.VisitChildren((attr, _, _) => {
                            if (attr.Kind == CXCursorKind.CXCursor_AnnotateAttr && attr.Spelling.ToString() == "HelixExtern")
                            {
                                lock (ExternNames) { ExternNames.Add(childCursor.Spelling.ToString()); }
                                tryEnqueueRoot(clang.getCursorType(childCursor));
                            }
                            return CXChildVisitResult.CXChildVisit_Continue;
                        }, default);
                    }

                    if (extractGlobalsAndVmps)
                    {
                        if (childCursor.Kind == CXCursorKind.CXCursor_FunctionDecl || childCursor.Kind == CXCursorKind.CXCursor_CXXMethod)
                        {
                            bool isVmp = false;
                            childCursor.VisitChildren((attrCursor, _, _) => { if (attrCursor.Kind == CXCursorKind.CXCursor_AnnotateAttr && attrCursor.Spelling.ToString() == "HelixVmp") { isVmp = true; return CXChildVisitResult.CXChildVisit_Break; } return CXChildVisitResult.CXChildVisit_Continue; }, default);

                            if (isVmp)
                            {
                                CXCursor semanticParent = clang.getCursorSemanticParent(childCursor);
                                if (semanticParent.Kind == CXCursorKind.CXCursor_ClassDecl || semanticParent.Kind == CXCursorKind.CXCursor_StructDecl) tryEnqueueRoot(clang.getCursorType(semanticParent));
                                int numArgs = clang.Cursor_getNumArguments(childCursor);
                                for (uint i = 0; i < numArgs; i++) tryEnqueueRoot(clang.getCursorType(clang.Cursor_getArgument(childCursor, i)));
                            }
                        }
                        ExtractGlobalsAndVmps(childCursor, translationUnit, projectDirectory);
                    }

                    return CXChildVisitResult.CXChildVisit_Recurse;
                }, default);

                while (crawlQueue.Count > 0)
                {
                    var (decl, name) = crawlQueue.Dequeue();
                    CrawlDependencyGraph(decl, name, localRegistry, projectDirectory, tryEnqueueRoot);
                }

                translationUnit.Dispose();

                results.Add((localRegistry, localDefined));
                foreach (var t in localAllRoots) threadRoots.Add(t);
            });

            Dictionary<string, ClassMetadata> finalRegistry = new Dictionary<string, ClassMetadata>();
            HashSet<string> finalDefined = new HashSet<string>();
            HashSet<string> finalRoots = new HashSet<string>(threadRoots);

            foreach (var res in results)
            {
                foreach (var def in res.Def) finalDefined.Add(def);
                foreach (var kvp in res.Reg)
                {
                    if (!finalRegistry.TryGetValue(kvp.Key, out var existing))
                    {
                        finalRegistry[kvp.Key] = kvp.Value;
                    }
                    else if (kvp.Value.Size > 0 && (existing.Size <= 0 || existing.Fields.Count < kvp.Value.Fields.Count))
                    {
                        finalRegistry[kvp.Key] = kvp.Value;
                    }
                }
            }

            foreach (var t in finalRoots)
            {
                if (!finalRegistry.ContainsKey(t)) finalRegistry[t] = new ClassMetadata { ClassName = t, Size = 0, IsExternal = true, OriginPath = "Unknown (From Inference)" };
            }

            return (finalRegistry, finalDefined);
        }

        public void CrawlDependencyGraph(CXCursor cursor, string className, Dictionary<string, ClassMetadata> registry, string projectDirectory, Action<CXType> tryEnqueueRoot)
        {
            try
            {
                if (cursor.kind == 0) return;

                CXCursor declCursor = clang.getCursorDefinition(cursor);
                if (declCursor.Kind == CXCursorKind.CXCursor_NoDeclFound) declCursor = clang.getTypeDeclaration(clang.getCursorType(cursor));
                if (declCursor.Kind == CXCursorKind.CXCursor_NoDeclFound) declCursor = cursor;

                declCursor.Location.GetFileLocation(out CXFile file, out _, out _, out _);
                string nodeFilePath = file.Name.ToString();
                string nfpLower = nodeFilePath.Replace("\\", "/").ToLower();
                bool isExternal = string.IsNullOrEmpty(nodeFilePath) || !nfpLower.StartsWith(projectDirectory.Replace("\\", "/").ToLower());

                CXType cursorType = clang.getCursorType(cursor);
                CXType canonicalType = clang.getCanonicalType(cursorType);

                long totalSize = clang.Type_getSizeOf(canonicalType);
                if (totalSize < 0) totalSize = 0;

                long align = clang.Type_getAlignOf(canonicalType);
                if (align < 1) align = 1;

                if (!registry.TryGetValue(className, out ClassMetadata metadata) || metadata.Size == -1)
                {
                    metadata = new ClassMetadata
                    {
                        ClassName = className,
                        Size = totalSize,
                        Alignment = align,
                        IsExternal = isExternal,
                        OriginPath = nodeFilePath,
                        RequiresPolyfill = false
                    };
                    registry[className] = metadata;
                }
                else if (metadata.Fields.Count > 0 || metadata.Methods.Count > 0) return;

                CXCursor extractCursor = declCursor;
                if (cursorType.kind == CXTypeKind.CXType_Typedef && canonicalType.kind == CXTypeKind.CXType_Record)
                {
                    CXCursor canonicalDecl = clang.getTypeDeclaration(canonicalType);
                    CXCursor canonicalDef = clang.getCursorDefinition(canonicalDecl);
                    extractCursor = canonicalDef.Kind != CXCursorKind.CXCursor_NoDeclFound ? canonicalDef : canonicalDecl;
                }

                HashSet<string> addedFields = new HashSet<string>();
                HashSet<string> addedMethods = new HashSet<string>();

                Action<CXCursor, long> ExtractMembers = null;
                ExtractMembers = (targetCursor, baseOffsetBits) => {
                    targetCursor.VisitChildren((memberCursor, memberParent, memberData) =>
                    {
                        CX_CXXAccessSpecifier access = clang.getCXXAccessSpecifier(memberCursor);
                        if (access == CX_CXXAccessSpecifier.CX_CXXPrivate || access == CX_CXXAccessSpecifier.CX_CXXProtected)
                            return CXChildVisitResult.CXChildVisit_Continue;

                        if (access == CX_CXXAccessSpecifier.CX_CXXInvalidAccessSpecifier)
                        {
                            CXCursor parentDecl = clang.getCursorSemanticParent(memberCursor);
                            if (parentDecl.kind == CXCursorKind.CXCursor_ClassDecl || parentDecl.kind == CXCursorKind.CXCursor_ClassTemplate)
                            {
                                return CXChildVisitResult.CXChildVisit_Continue;
                            }
                        }

                        if (memberCursor.Kind == CXCursorKind.CXCursor_CXXBaseSpecifier)
                        {
                            CXCursor baseDecl = clang.getTypeDeclaration(clang.getCursorType(memberCursor));
                            CXCursor baseDef = clang.getCursorDefinition(baseDecl);
                            ExtractMembers(baseDef.Kind != CXCursorKind.CXCursor_NoDeclFound ? baseDef : baseDecl, baseOffsetBits);
                        }
                        else if (memberCursor.Kind == CXCursorKind.CXCursor_UnionDecl || memberCursor.Kind == CXCursorKind.CXCursor_StructDecl || memberCursor.Kind == CXCursorKind.CXCursor_ClassDecl)
                        {
                            string innerName = memberCursor.Spelling.ToString();
                            if (string.IsNullOrEmpty(innerName) || innerName.Contains("unnamed") || innerName.Contains("anonymous"))
                            {
                                ExtractMembers(memberCursor, baseOffsetBits);
                            }
                        }
                        else if (memberCursor.Kind == CXCursorKind.CXCursor_FieldDecl)
                        {
                            string fieldName = memberCursor.Spelling.ToString();

                            if (string.IsNullOrEmpty(fieldName) || fieldName.Contains("unnamed") || fieldName.Contains("anonymous"))
                            {
                                return CXChildVisitResult.CXChildVisit_Continue;
                            }

                            if (!addedFields.Contains(fieldName) && fieldName != "Offset" && !fieldName.StartsWith("_helix_pad_") && clang.Cursor_isBitField(memberCursor) == 0)
                            {
                                addedFields.Add(fieldName);

                                long localOffsetBits = clang.Cursor_getOffsetOfField(memberCursor);
                                if (localOffsetBits < 0) localOffsetBits = 0;
                                long absoluteOffsetBits = baseOffsetBits + localOffsetBits;

                                long finalOffsetBits = -1;
                                fixed (byte* pName = Encoding.UTF8.GetBytes(fieldName + "\0"))
                                {
                                    finalOffsetBits = clang.Type_getOffsetOf(canonicalType, (sbyte*)pName);
                                }
                                if (finalOffsetBits < 0) finalOffsetBits = absoluteOffsetBits;

                                CXType fieldType = clang.getCursorType(memberCursor);
                                long fieldSize = clang.Type_getSizeOf(fieldType);
                                if (fieldSize < 0) fieldSize = 0;

                                FieldMetadata fieldMetadata = new()
                                {
                                    Name = fieldName,
                                    Offset = finalOffsetBits / 8,
                                    Size = fieldSize,
                                    TypeSpelling = clang.getCanonicalType(fieldType).Spelling.ToString(),
                                    IsPointer = clang.getCanonicalType(fieldType).kind == CXTypeKind.CXType_Pointer,
                                    IsExtern = false
                                };
                                memberCursor.VisitChildren((attrCursor, _, _) => { if (attrCursor.Kind == CXCursorKind.CXCursor_AnnotateAttr && attrCursor.Spelling.ToString() == "HelixExtern") fieldMetadata.IsExtern = true; return CXChildVisitResult.CXChildVisit_Continue; }, default);
                                metadata.Fields.Add(fieldMetadata);

                                tryEnqueueRoot(fieldType);
                            }
                        }
                        else if (memberCursor.Kind == CXCursorKind.CXCursor_CXXMethod)
                        {
                            ParseMethod(memberCursor, metadata, default, tryEnqueueRoot, addedMethods);
                        }
                        return CXChildVisitResult.CXChildVisit_Continue;
                    }, default);
                };

                ExtractMembers(extractCursor, 0);
            }
            catch (Exception) { }
        }

        private void ExtractGlobalsAndVmps(CXCursor childCursor, CXTranslationUnit tu, string projectDirectory)
        {
            childCursor.Location.GetFileLocation(out CXFile cxFile, out _, out _, out _);
            string nodeFilePath = cxFile.Name.ToString();
            if (string.IsNullOrEmpty(nodeFilePath)) return;

            string normNodePath = nodeFilePath.Replace("\\", "/").ToLower();
            string normProjDir = projectDirectory.Replace("\\", "/").ToLower().TrimEnd('/', '\\');
            if (!normNodePath.StartsWith(normProjDir)) return;

            if (childCursor.Kind == CXCursorKind.CXCursor_FunctionDecl)
            {
                bool isVmp = false;
                childCursor.VisitChildren((attrCursor, attrParent, attrData) => { if (attrCursor.Kind == CXCursorKind.CXCursor_AnnotateAttr && attrCursor.Spelling.ToString() == "HelixVmp") { isVmp = true; return CXChildVisitResult.CXChildVisit_Break; } return CXChildVisitResult.CXChildVisit_Continue; }, default);

                List<string> externParams = [];
                int numArgs = clang.Cursor_getNumArguments(childCursor);
                if (numArgs > 0)
                {
                    for (uint i = 0; i < (uint)numArgs; i++)
                    {
                        CXCursor arg = clang.Cursor_getArgument(childCursor, i);
                        bool hasExtern = false;
                        arg.VisitChildren((attrCursor, attrParent, attrData) => { if (attrCursor.Kind == CXCursorKind.CXCursor_AnnotateAttr && attrCursor.Spelling.ToString() == "HelixExtern") { hasExtern = true; return CXChildVisitResult.CXChildVisit_Break; } return CXChildVisitResult.CXChildVisit_Continue; }, default);
                        if (hasExtern) externParams.Add(arg.Spelling.ToString());
                    }
                }

                ExtractFunctionBodyBlocks(childCursor, tu);

                if (isVmp || externParams.Count > 0)
                {
                    string funcName = childCursor.Spelling.ToString();

                    bool vmpExists = false;
                    lock (VmpTargets) { vmpExists = VmpTargets.Exists(v => v.ClassName == string.Empty && v.FuncName == funcName); }

                    if (!vmpExists)
                    {
                        string retType = clang.getCursorResultType(childCursor).Spelling.ToString();
                        List<string> argList = new();
                        if (numArgs > 0)
                        {
                            for (uint i = 0; i < (uint)numArgs; i++)
                            {
                                CXCursor arg = clang.Cursor_getArgument(childCursor, i);
                                string name = arg.Spelling.ToString();
                                argList.Add($"{arg.Type.Spelling} {(string.IsNullOrEmpty(name) ? $"_arg{i}" : name)}");
                            }
                        }
                        var bodyData = ExtractFunctionBodyBlocks(childCursor, tu);
                        lock (VmpTargets)
                        {
                            if (!VmpTargets.Exists(v => v.ClassName == string.Empty && v.FuncName == funcName))
                            {
                                VmpTargets.Add((string.Empty, funcName, true, retType, string.Join(", ", argList), bodyData.Blocks, bodyData.LiftedDecls, externParams));
                            }
                        }
                    }
                }
            }
            else if (childCursor.Kind == CXCursorKind.CXCursor_VarDecl)
            {
                CXCursor semanticParent = clang.getCursorSemanticParent(childCursor);
                bool isStaticMember = (semanticParent.Kind == CXCursorKind.CXCursor_ClassDecl || semanticParent.Kind == CXCursorKind.CXCursor_StructDecl);
                bool isGlobal = (semanticParent.Kind == CXCursorKind.CXCursor_TranslationUnit || semanticParent.Kind == CXCursorKind.CXCursor_Namespace || semanticParent.Kind == CXCursorKind.CXCursor_LinkageSpec);

                CXLinkageKind linkage = clang.getCursorLinkage(childCursor);
                if (linkage == CXLinkageKind.CXLinkage_Internal && semanticParent.Kind == CXCursorKind.CXCursor_TranslationUnit) return;

                if ((isStaticMember || isGlobal) && clang.getCanonicalType(childCursor.Type).kind == CXTypeKind.CXType_Pointer)
                {
                    string varName = childCursor.Spelling.ToString();
                    if (isStaticMember)
                    {
                        string clsName = semanticParent.Spelling.ToString();
                        string staticVarName = clsName + "::" + varName;
                        if (!varName.StartsWith("_"))
                        {
                            lock (GlobalPointers) { if (!GlobalPointers.Exists(g => g.Name == staticVarName)) GlobalPointers.Add((childCursor.Type.Spelling.ToString(), staticVarName, true)); }
                        }
                    }
                    else if (isGlobal)
                    {
                        if (!varName.StartsWith("_"))
                        {
                            lock (GlobalPointers) { if (!GlobalPointers.Exists(g => g.Name == varName)) GlobalPointers.Add((childCursor.Type.Spelling.ToString(), varName, false)); }
                        }
                    }
                }
            }
        }

        private void ParseMethod(CXCursor methodCursor, ClassMetadata meta, CXTranslationUnit tu, Action<CXType> tryEnqueueRoot, HashSet<string> addedMethods = null)
        {
            if (methodCursor.CXXMethod_IsStatic || methodCursor.Kind == CXCursorKind.CXCursor_Constructor || methodCursor.Kind == CXCursorKind.CXCursor_Destructor) return;

            int numArgs = clang.Cursor_getNumArguments(methodCursor);
            string nameStr = methodCursor.Spelling.ToString();
            List<string> paramTypes = new List<string>();

            bool isSafeMethod = true;
            if (numArgs > 0)
            {
                for (uint i = 0; i < (uint)numArgs; i++)
                {
                    CXCursor arg = clang.Cursor_getArgument(methodCursor, i);
                    CXType canonicalArg = clang.getCanonicalType(clang.getCursorType(arg));
                    string argTypeStr = canonicalArg.Spelling.ToString();

                    if (argTypeStr.Contains("<") || argTypeStr.Contains(">") || argTypeStr.Contains("::"))
                    {
                        isSafeMethod = false; break;
                    }

                    if (tryEnqueueRoot != null) tryEnqueueRoot(canonicalArg);

                    paramTypes.Add(argTypeStr);
                }
            }

            if (!isSafeMethod) return;

            if (addedMethods != null)
            {
                if (addedMethods.Contains(nameStr)) return;
                addedMethods.Add(nameStr);
            }

            MethodMetadata methodMeta = new MethodMetadata { Name = nameStr };
            methodMeta.ParamTypeSpellings.AddRange(paramTypes);
            meta.Methods.Add(methodMeta);
        }

        private string StripComments(string code)
        {
            if (string.IsNullOrEmpty(code)) return code;
            StringBuilder result = new StringBuilder(code.Length);
            bool inString = false, inChar = false, inLineComment = false, inBlockComment = false;

            for (int i = 0; i < code.Length; i++)
            {
                if (inLineComment) { if (code[i] == '\n') { inLineComment = false; result.Append('\n'); } continue; }
                if (inBlockComment) { if (i < code.Length - 1 && code[i] == '*' && code[i + 1] == '/') { inBlockComment = false; i++; } continue; }
                if (inString) { result.Append(code[i]); if (code[i] == '\\' && i < code.Length - 1) { result.Append(code[i + 1]); i++; } else if (code[i] == '"') { inString = false; } continue; }
                if (inChar) { result.Append(code[i]); if (code[i] == '\\' && i < code.Length - 1) { result.Append(code[i + 1]); i++; } else if (code[i] == '\'') { inChar = false; } continue; }
                if (i < code.Length - 1 && code[i] == '/' && code[i + 1] == '/') { inLineComment = true; i++; continue; }
                if (i < code.Length - 1 && code[i] == '/' && code[i + 1] == '*') { inBlockComment = true; i++; continue; }
                if (code[i] == '"') inString = true; else if (code[i] == '\'') inChar = true;
                result.Append(code[i]);
            }
            return result.ToString();
        }

        private List<CXCursor> GetChildren(CXCursor cursor)
        {
            List<CXCursor> list = new();
            cursor.VisitChildren((c, p, d) => { list.Add(c); return CXChildVisitResult.CXChildVisit_Continue; }, default);
            return list;
        }

        private string GetCursorText(CXCursor cursor, byte[] fileBytes)
        {
            CXSourceRange range = clang.getCursorExtent(cursor);
            CXSourceLocation startLoc = clang.getRangeStart(range);
            CXSourceLocation endLoc = clang.getRangeEnd(range);
            startLoc.GetFileLocation(out _, out _, out _, out uint startOffset);
            endLoc.GetFileLocation(out _, out _, out _, out uint endOffset);
            if (endOffset > startOffset && endOffset <= fileBytes.Length) return StripComments(Encoding.UTF8.GetString(fileBytes, (int)startOffset, (int)(endOffset - startOffset)));
            return string.Empty;
        }

        private (List<string> Blocks, List<string> LiftedDecls) ExtractFunctionBodyBlocks(CXCursor cursor, CXTranslationUnit tu)
        {
            List<string> blocks = new();
            List<string> liftedDecls = new();
            Dictionary<string, string> renames = new();

            try
            {
                CXSourceRange range = clang.getCursorExtent(cursor); clang.getRangeStart(range).GetFileLocation(out CXFile file, out _, out _, out _); string filePath = file.Name.ToString();

                if (!_ioCache.TryGetValue(filePath, out byte[] fileBytes))
                {
                    if (!System.IO.File.Exists(filePath)) return (blocks, liftedDecls);
                    fileBytes = System.IO.File.ReadAllBytes(filePath);
                    _ioCache[filePath] = fileBytes;
                }

                CXCursor bodyCursor = default; bool foundBody = false;
                cursor.VisitChildren((c, p, d) => { if (c.Kind == CXCursorKind.CXCursor_CompoundStmt) { bodyCursor = c; foundBody = true; return CXChildVisitResult.CXChildVisit_Break; } return CXChildVisitResult.CXChildVisit_Continue; }, default);
                if (!foundBody) return (blocks, liftedDecls);

                List<CXCursor> topLevelStmts = GetChildren(bodyCursor); bool mergedRest = false;
                foreach (var stmt in topLevelStmts)
                {
                    string rawStmtText = GetCursorText(stmt, fileBytes).Trim();
                    if (string.IsNullOrEmpty(rawStmtText)) continue;
                    if (!rawStmtText.EndsWith(";") && !rawStmtText.EndsWith("}")) rawStmtText += ";";

                    if (mergedRest)
                    {
                        blocks[blocks.Count - 1] += "\n        " + rawStmtText;
                        continue;
                    }

                    if (stmt.Kind == CXCursorKind.CXCursor_DeclStmt)
                    {
                        var varDecls = GetChildren(stmt); List<string> assignments = new(); bool canLiftAll = true;
                        foreach (var child in varDecls)
                        {
                            if (child.Kind == CXCursorKind.CXCursor_VarDecl)
                            {
                                string typeStr = clang.getCursorType(child).Spelling.ToString(), oldName = child.Spelling.ToString();

                                if (typeStr.Contains("const ") || typeStr.Contains("&") || typeStr.Contains("[]") || typeStr.Contains("auto") || typeStr.Contains("<") || typeStr.Contains("(")) { canLiftAll = false; break; }

                                typeStr = typeStr.Replace("class ", "").Replace("struct ", "").Trim();
                                string newName = oldName + "_vmp" + Guid.NewGuid().ToString("N").Substring(0, 4); renames[oldName] = newName;

                                liftedDecls.Add($"{typeStr} {newName};");

                                string initText = ""; bool hasInit = false;
                                child.VisitChildren((c, p, d) => {
                                    if (!hasInit && c.Kind >= CXCursorKind.CXCursor_FirstExpr && c.Kind <= CXCursorKind.CXCursor_LastExpr) { initText = GetCursorText(c, fileBytes).Trim(); hasInit = true; }
                                    return CXChildVisitResult.CXChildVisit_Continue;
                                }, default);

                                if (hasInit && !string.IsNullOrEmpty(initText))
                                {
                                    if (!initText.EndsWith(";")) initText += ";";
                                    assignments.Add($"{newName} = {initText}");
                                }
                            }
                            else { canLiftAll = false; break; }
                        }
                        if (canLiftAll)
                        {
                            if (assignments.Count > 0) blocks.Add(string.Join(" ", assignments));
                        }
                        else
                        {
                            mergedRest = true;
                            blocks.Add(rawStmtText);
                        }
                    }
                    else
                    {
                        blocks.Add(rawStmtText);
                    }
                }
                for (int i = 0; i < blocks.Count; i++) foreach (var kvp in renames) blocks[i] = System.Text.RegularExpressions.Regex.Replace(blocks[i], $@"(?<!(?:->|\.))\b{kvp.Key}\b", kvp.Value);
            }
            catch (Exception) { }
            return (blocks, liftedDecls);
        }
    }
}
