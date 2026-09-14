using System.Collections.Generic;

namespace Helix
{
    public class FieldMetadata
    {
        public string Name { get; set; }
        public long Offset { get; set; }
        public long Size { get; set; }
        public string TypeSpelling { get; set; }
        public bool IsPointer { get; set; }
        public bool IsExtern { get; set; }
    }

    public class MethodMetadata
    {
        public string Name { get; set; }
        public List<string> ParamTypeSpellings { get; set; } = new List<string>();
    }

    public class ClassMetadata
    {
        public string ClassName { get; set; }
        public long Size { get; set; }
        public long Alignment { get; set; }

        public bool IsExternal { get; set; }

        // 终极真理表裁决标志
        public bool RequiresPolyfill { get; set; }
        public string OriginPath { get; set; }
        public List<FieldMetadata> Fields { get; set; } = [];
        public List<MethodMetadata> Methods { get; set; } = [];
        public List<string> Dependencies { get; set; } = [];
    }
}