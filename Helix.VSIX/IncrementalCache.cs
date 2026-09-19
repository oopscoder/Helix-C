using System;
using System.Collections.Generic;
using System.IO;

namespace Helix
{
    public class IncrementalCache
    {
        private readonly Dictionary<string, Dictionary<string, (DateTime Time, long Size)>> projectCaches = [];

        public bool IsRebuildRequired(string projectPath, List<string> sourceFiles)
        {
            if (!projectCaches.TryGetValue(projectPath, out var cachedFiles)) return true;

            if (File.Exists(projectPath))
            {
                var projInfo = new FileInfo(projectPath);
                if (!cachedFiles.TryGetValue(projectPath, out var cachedProj) ||
                    projInfo.LastWriteTimeUtc > cachedProj.Time ||
                    projInfo.Length != cachedProj.Size)
                {
                    return true;
                }
            }

            foreach (string file in sourceFiles)
            {
                if (!File.Exists(file)) continue;

                var fileInfo = new FileInfo(file);
                if (!cachedFiles.TryGetValue(file, out var cachedData) ||
                    fileInfo.LastWriteTimeUtc > cachedData.Time ||
                    fileInfo.Length != cachedData.Size)
                {
                    return true;
                }
            }
            return false;
        }

        public void UpdateCache(string projectPath, List<string> sourceFiles)
        {
            Dictionary<string, (DateTime, long)> newCache = [];

            if (File.Exists(projectPath))
            {
                var projInfo = new FileInfo(projectPath);
                newCache[projectPath] = (projInfo.LastWriteTimeUtc, projInfo.Length);
            }

            foreach (string file in sourceFiles)
            {
                if (File.Exists(file))
                {
                    var fileInfo = new FileInfo(file);
                    newCache[file] = (fileInfo.LastWriteTimeUtc, fileInfo.Length);
                }
            }
            projectCaches[projectPath] = newCache;
        }
    }
}
