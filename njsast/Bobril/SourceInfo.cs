using System.Collections.Generic;

namespace Njsast.Bobril
{
    public class SourceInfo
    {
        public string BobrilImport { get; set; }
        public string BobrilG11nImport { get; set; }

        public class Diagnostic
        {
            public int StartCol { get; set; }
            public int StartLine { get; set; }
            public int EndCol { get; set; }
            public int EndLine { get; set; }
            public bool IsError { get; set; }
            public int Code { get; set; }
            public string Text { get; set; }
        }

        public List<Diagnostic> Diagnostics { get; set; }

        public class Asset
        {
            public int StartCol { get; set; }
            public int StartLine { get; set; }
            public int EndCol { get; set; }
            public int EndLine { get; set; }
            public string Name { get; set; }
        }

        public List<Asset> Assets { get; set; }

        public class Sprite
        {
            public int StartCol { get; set; }
            public int StartLine { get; set; }
            public int EndCol { get; set; }
            public int EndLine { get; set; }
            public string Name { get; set; }
            public int NameStartCol { get; set; }
            public int NameStartLine { get; set; }
            public int NameEndCol { get; set; }
            public int NameEndLine { get; set; }
            public string Color { get; set; }
            public bool HasColor { get; set; }
            public int ColorStartCol { get; set; }
            public int ColorStartLine { get; set; }
            public int ColorEndCol { get; set; }
            public int ColorEndLine { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
        }

        public List<Sprite> Sprites { get; set; }

        public class Translation
        {
            public int StartCol { get; set; }
            public int StartLine { get; set; }
            public int EndCol { get; set; }
            public int EndLine { get; set; }
            public int StartHintCol { get; set; }
            public int StartHintLine { get; set; }
            public int EndHintCol { get; set; }
            public int EndHintLine { get; set; }
            public string Message { get; set; }
            public string Hint { get; set; }
            public bool JustFormat { get; set; }
            public bool WithParams { get; set; }
            public List<string> KnownParams { get; set; }
        }

        public List<Translation> Translations { get; set; }

        public class StyleDef
        {
            public int BeforeNameLine { get; set; }
            public int BeforeNameCol { get; set; }
            public int StartCol { get; set; }
            public int StartLine { get; set; }
            public int EndCol { get; set; }
            public int EndLine { get; set; }
            public string Name { get; set; }
            public bool UserNamed { get; set; }
            public bool IsEx { get; set; }
        }

        public List<StyleDef> StyleDefs { get; set; }
    }
}