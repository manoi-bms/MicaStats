using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// The MicaPad language a fenced code block names by the first word of its info string (spec
    /// 1.2), compared without case. Unknown words (bash, sh, the diagram words) name none: such a
    /// block is monospace but not colored.
    /// </summary>
    public static class FenceLanguages
    {
        private static readonly Dictionary<string, string> IdByWord = Build();

        /// <summary>Every fence word, lower case.</summary>
        public static IEnumerable<string> Words => IdByWord.Keys;

        /// <summary>The <see cref="PadLanguages"/> id a fence word names, or null.</summary>
        public static string? IdOf(string? word) =>
            word != null && IdByWord.TryGetValue(word, out var id) ? id : null;

        /// <summary>The language id an opening fence line names (<c>```cs title</c> gives "csharp"), or null.</summary>
        public static string? IdOfFence(string openingLine) => IdOf(FenceTracker.InfoWord(openingLine));

        private static Dictionary<string, string> Build()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Add(string id, params string[] words)
            {
                foreach (string word in words) map.Add(word, id);
            }

            Add("csharp", "cs", "csharp", "c#");
            Add("javascript", "js", "javascript", "jsx", "mjs", "cjs", "ts", "typescript", "tsx", "node");
            Add("json", "json", "jsonc", "json5");
            Add("xml", "xml", "xaml", "svg", "csproj", "xsd", "plist");
            Add("html", "html", "htm", "xhtml");
            Add("css", "css");
            Add("powershell", "powershell", "ps", "ps1", "pwsh");
            Add("python", "python", "py");
            Add("sql", "sql", "tsql", "mssql", "mysql", "postgres", "postgresql", "plsql");
            Add("cpp", "c", "cpp", "c++", "h", "hpp", "cc");
            Add("java", "java");
            Add("php", "php");
            Add("vb", "vb", "vbnet", "vba");
            Add("diff", "diff", "patch");
            Add("ini", "ini", "cfg", "conf", "toml", "properties");
            Add("yaml", "yaml", "yml");
            Add("batch", "bat", "batch", "cmd");
            Add("log", "log");
            return map;
        }
    }
}
