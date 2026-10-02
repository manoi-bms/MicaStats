using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>How a language folds (spec 2.5).</summary>
    public enum PadFoldKind
    {
        None,
        Braces,
        Xml,
        Headings,
    }

    /// <summary>
    /// A language MicaPad can show a note in. <see cref="Definition"/> names the AvalonEdit
    /// highlighting definition, or with <see cref="OwnDefinition"/> one of MicaPad's own
    /// <c>Pad/Highlighting/*.xshd</c> files; null means no syntax colors (Plain text, and Markdown,
    /// which MicaPad formats itself). <see cref="FunctionCalls"/> marks a language that calls
    /// functions as <c>name(...)</c>: names its colors leave plain get the Function color (ruling R4).
    /// <see cref="TightCalls"/> marks one whose calls never have a space before the <c>(</c>
    /// (PowerShell, where <c>-f ($x)</c> passes an argument), so <c>name (</c> is no call there.
    /// </summary>
    public sealed record PadLanguage(string Id, string Name, string? Definition, bool OwnDefinition, PadFoldKind Fold, bool FunctionCalls = false,
                                     bool TightCalls = false);

    /// <summary>A note's language after Auto and the size limit.</summary>
    public sealed record ResolvedLanguage(PadLanguage Language, bool TooLarge)
    {
        /// <summary>What the status bar shows.</summary>
        public string DisplayName => TooLarge ? "Plain text (large)" : Language.Name;

        /// <summary>The language actually applied: Plain text while the note is too large to format.</summary>
        public PadLanguage Effective => TooLarge ? PadLanguages.Plain : Language;
    }

    /// <summary>Every language, the extension table of spec 2.1, and Auto.</summary>
    public static class PadLanguages
    {
        /// <summary>Above this many characters (2 MB of text), colors, Markdown and folding are off.</summary>
        public const int MaxFormattedChars = 2 * 1024 * 1024;

        public static PadLanguage Plain { get; } = new("plain", "Plain text", null, false, PadFoldKind.None);

        public static PadLanguage Markdown { get; } = new("markdown", "Markdown", null, false, PadFoldKind.Headings);

        /// <summary>Every language, in the order the status-bar menu lists them.</summary>
        public static IReadOnlyList<PadLanguage> All { get; } = new[]
        {
            Plain,
            Markdown,
            new PadLanguage("json", "JSON", "Json", false, PadFoldKind.Braces),
            new PadLanguage("xml", "XML", "XML", false, PadFoldKind.Xml),
            new PadLanguage("html", "HTML", "HTML", false, PadFoldKind.Xml),
            new PadLanguage("csharp", "C#", "C#", false, PadFoldKind.Braces, FunctionCalls: true),
            new PadLanguage("javascript", "JavaScript", "JavaScript", false, PadFoldKind.Braces, FunctionCalls: true),
            new PadLanguage("typescript", "TypeScript", "TypeScript", true, PadFoldKind.Braces, FunctionCalls: true),
            new PadLanguage("css", "CSS", "CSS", false, PadFoldKind.Braces),
            new PadLanguage("powershell", "PowerShell", "PowerShell", false, PadFoldKind.Braces, FunctionCalls: true, TightCalls: true),
            new PadLanguage("shell", "Shell", "Shell", true, PadFoldKind.None),
            new PadLanguage("python", "Python", "Python", false, PadFoldKind.None, FunctionCalls: true),
            new PadLanguage("sql", "SQL", "TSQL", false, PadFoldKind.None),
            new PadLanguage("cpp", "C/C++", "C++", false, PadFoldKind.Braces, FunctionCalls: true),
            new PadLanguage("java", "Java", "Java", false, PadFoldKind.Braces, FunctionCalls: true),
            new PadLanguage("kotlin", "Kotlin", "Kotlin", true, PadFoldKind.Braces, FunctionCalls: true),
            new PadLanguage("go", "Go", "Go", true, PadFoldKind.Braces, FunctionCalls: true),
            new PadLanguage("rust", "Rust", "Rust", true, PadFoldKind.Braces, FunctionCalls: true),
            new PadLanguage("php", "PHP", "PHP", false, PadFoldKind.Braces, FunctionCalls: true),
            new PadLanguage("ruby", "Ruby", "Ruby", true, PadFoldKind.None, FunctionCalls: true),
            new PadLanguage("pascal", "Pascal/Delphi", "Pascal", true, PadFoldKind.None, FunctionCalls: true),
            new PadLanguage("vb", "VB", "VB", false, PadFoldKind.None),
            new PadLanguage("diff", "Diff", "Patch", false, PadFoldKind.None),
            new PadLanguage("dockerfile", "Dockerfile", "Dockerfile", true, PadFoldKind.None),
            new PadLanguage("ini", "INI", "Ini", true, PadFoldKind.None),
            new PadLanguage("yaml", "YAML", "Yaml", true, PadFoldKind.None),
            new PadLanguage("batch", "Batch", "Batch", true, PadFoldKind.None),
            new PadLanguage("log", "Log", "Log", true, PadFoldKind.None),
        };

        /// <summary>
        /// Languages only a fenced block can name: not in the menu and never a note's language.
        /// MicaPad's real Markdown formatting cannot nest inside a fence, so a <c>```md</c> block
        /// gets this small own definition instead.
        /// </summary>
        public static IReadOnlyList<PadLanguage> FenceOnly { get; } = new[]
        {
            new PadLanguage("markdown-fence", "Markdown", "MarkdownCode", true, PadFoldKind.None),
        };

        private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
        {
            [".md"] = "markdown", [".markdown"] = "markdown", [".txt"] = "markdown",
            [".json"] = "json", [".jsonc"] = "json",
            [".xml"] = "xml", [".xaml"] = "xml", [".csproj"] = "xml", [".props"] = "xml", [".targets"] = "xml",
            [".config"] = "xml", [".svg"] = "xml", [".resx"] = "xml", [".xsd"] = "xml",
            [".html"] = "html", [".htm"] = "html",
            [".cs"] = "csharp",
            [".js"] = "javascript", [".mjs"] = "javascript", [".cjs"] = "javascript",
            [".jsx"] = "javascript",
            [".ts"] = "typescript", [".tsx"] = "typescript", [".mts"] = "typescript", [".cts"] = "typescript",
            [".sh"] = "shell", [".bash"] = "shell", [".zsh"] = "shell", [".ksh"] = "shell",
            [".bashrc"] = "shell", [".bash_profile"] = "shell", [".zshrc"] = "shell", [".profile"] = "shell",
            [".pas"] = "pascal", [".dpr"] = "pascal", [".dpk"] = "pascal", [".lpr"] = "pascal", [".pp"] = "pascal",
            [".go"] = "go",
            [".rs"] = "rust",
            [".rb"] = "ruby", [".rake"] = "ruby", [".gemspec"] = "ruby",
            [".kt"] = "kotlin", [".kts"] = "kotlin",
            [".dockerfile"] = "dockerfile",
            [".css"] = "css",
            [".ps1"] = "powershell", [".psm1"] = "powershell", [".psd1"] = "powershell",
            [".py"] = "python", [".pyw"] = "python",
            [".sql"] = "sql",
            [".c"] = "cpp", [".h"] = "cpp", [".cpp"] = "cpp", [".hpp"] = "cpp", [".cc"] = "cpp",
            [".java"] = "java",
            [".php"] = "php",
            [".vb"] = "vb", [".bas"] = "vb",
            [".diff"] = "diff", [".patch"] = "diff",
            [".ini"] = "ini", [".cfg"] = "ini", [".conf"] = "ini", [".inf"] = "ini",
            [".editorconfig"] = "ini", [".gitconfig"] = "ini",
            [".yml"] = "yaml", [".yaml"] = "yaml",
            [".bat"] = "batch", [".cmd"] = "batch",
            [".log"] = "log",
        };

        /// <summary>The language with this id, ignoring case, or null.</summary>
        public static PadLanguage? ById(string? id) =>
            id == null ? null : All.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>The language a fenced block's id names: a listed language or a <see cref="FenceOnly"/> one, or null.</summary>
        public static PadLanguage? ForFence(string? id) =>
            ById(id) ?? (id == null ? null : FenceOnly.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase)));

        /// <summary>
        /// The language a file's extension implies, or Plain text. <c>.editorconfig</c> and
        /// <c>.gitconfig</c> count: for a name that starts with a dot, the whole name is the extension.
        /// </summary>
        public static PadLanguage ForPath(string path)
        {
            string extension = Path.GetExtension(path);
            if (extension.Length > 0 && ByExtension.TryGetValue(extension, out string? id)) return ById(id)!;
            return ByFileName(Path.GetFileName(path)) is { } named ? ById(named)! : Plain;
        }

        /// <summary>Files known by name rather than extension: Dockerfile, Dockerfile.dev, Containerfile, Gemfile, Rakefile.</summary>
        private static string? ByFileName(string name)
        {
            if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Containerfile", StringComparison.OrdinalIgnoreCase)) return "dockerfile";
            if (name.Equals("Gemfile", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Rakefile", StringComparison.OrdinalIgnoreCase)) return "ruby";
            return null;
        }

        /// <summary>
        /// The language a note is shown in: its explicit choice if it has a known one, otherwise
        /// Auto (notes and .md/.markdown/.txt are Markdown unless <paramref name="markdownOn"/> is off).
        /// </summary>
        public static ResolvedLanguage Resolve(string? chosenId, string? sourcePath, bool markdownOn, int textLength)
        {
            PadLanguage language = ById(chosenId) ?? Auto(sourcePath, markdownOn);
            return new ResolvedLanguage(language, textLength > MaxFormattedChars);
        }

        private static PadLanguage Auto(string? sourcePath, bool markdownOn)
        {
            PadLanguage language = sourcePath == null ? Markdown : ForPath(sourcePath);
            return ReferenceEquals(language, Markdown) && !markdownOn ? Plain : language;
        }
    }
}
