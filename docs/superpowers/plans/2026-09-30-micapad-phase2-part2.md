# MicaPad phase 2, Part 2: languages, syntax colors, Markdown, folding — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every MicaPad tab has a language (Auto by file type, or chosen in a new status-bar item): code and logs get syntax colors that work in both themes, notes and `.md`/`.txt` get Markdown styled source with a Format menu, and structured text folds.

**Architecture:** Pure rules live in `Services/Pad` (no WPF types): `PadLanguages` (ids, extensions, Auto), `SyntaxColors` (definition color name → palette category), `MarkdownLineTokenizer` + `FenceTracker` + `MarkdownStyles` (what each Markdown run looks like), `MarkdownFormatter` (the Format edits), `BraceFolding` / `HeadingFolding`. Thin AvalonEdit adapters live in `Pad/`: `ThemedHighlightingColorizer` (a `HighlightingColorizer` that repaints AvalonEdit's built-in definitions from the palette at draw time, never mutating the shared definitions), `MarkdownColorizer`, `MarkdownBackgroundRenderer`, `BulletGenerator`, `FoldingController`, all switched by one `EditorLanguage` per editor.

**Tech Stack:** .NET 8 WPF, AvalonEdit 6.3.1.120 (`HighlightingColorizer.ApplyColorToElement` is `protected virtual`; `HighlightingLoader`, `DocumentHighlighter`, `FoldingManager`, `XmlFoldingStrategy`, `FormattedTextElement`), xUnit.

**Spec:** `docs/superpowers/specs/2026-09-30-micapad-phase2-design.md` — Part 2 (sections 2.1–2.5), the `PadMarkdown` row and `meta.json` `language` field of *Settings and storage*, Part 2's testing bullets and manual item 3.

## Global Constraints

- Language per note: status-bar item between the line ending and the save state; menu *Auto (by file type)*, then every language; the choice is stored in `meta.json` as `language` (a language id, or null for Auto) and survives restart.
- Auto: scratch notes and `.md .markdown .txt` → Markdown; the extension table of spec 2.1 for the rest; any other extension → Plain text. When `PadMarkdown` is off, notes and `.md .markdown .txt` resolve to Plain text; an explicit per-note choice still wins.
- Above 2 MB of text, highlighting, Markdown formatting and folding are off and the status item reads `Plain text (large)`.
- Syntax colors: AvalonEdit's definitions are never mutated; each named color maps to a palette category (first matching row wins, case-insensitive substring); font weight and style are kept, backgrounds dropped; every category color meets 4.5:1 on its theme background. A definition that fails to load falls back to Plain text and logs a warning under `pad`.
- Markdown is styled source: markers stay visible but dimmed; the document is never changed by formatting; headings 1.6/1.4/1.25/1.15/1.05/1.0 × font size, semibold; `_` emphasis only at word boundaries; backslash escapes are not formatted; unclosed markers stay plain; no tables, images or HTML.
- `PadMarkdown` (default on): *Markdown formatting* check item in `☰` and a switch in Settings → MicaPad.
- Format menu only when the tab's language is Markdown; each item is one undoable edit; no new shortcuts.
- Folding: braces (JSON, C#, JavaScript, CSS, C/C++, Java, PHP, PowerShell) ignoring braces in strings and comments; `XmlFoldingStrategy` (XML, HTML); headings and fenced blocks (Markdown). Fold margin only for languages that fold. Folds update 500 ms after the last edit and at once on tab switch. Fold state not saved. A caret moved inside a folded section unfolds it.
- New code under `Services/Pad/` holds no WPF types. Only MicaPad changes.
- Tests never touch the real `%APPDATA%`, never launch MicaStats, never start Explorer, never use the network. Never build or publish into `bin\Release`.
- Build/test only with `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe"`. `CultureInfo.InvariantCulture` for number/date text.
- Commits: `git add` exact paths, `-m` messages (no heredoc), never amend, `Co-Authored-By:` trailer naming the model that wrote the commit.

## Review Focus

1. **Typing in a long note** — Markdown fence classification must not rescan the whole document on every ordinary keystroke; it rescans only when an edit touches a fence character, changes the line count, or edits a fence delimiter line (Task 6: `MarkdownDocumentCacheTests` — plain typing raises no fence change, typing after a closing fence does).
2. **Pathological Markdown lines** (a 10,000-character line of `*a *a *a …`, unclosed markers everywhere) — the tokenizer must stay linear enough not to freeze the UI: inline formatting is skipped on lines longer than 4,000 characters (Task 5: `A_very_long_line_gets_no_inline_formatting`).
3. **A `.log` or `.json` file opened in the light theme** — every color a definition names must stay readable (≥ 4.5:1) in both themes, including colors a definition gives without a name (Task 3: `Every_named_color_reads_well_in_both_themes`; Task 2: unnamed-color tests).
4. **A note growing past 2 MB while open** (a big paste) — formatting and folding switch off immediately and the status item says `Plain text (large)`; shrinking back switches them on again (Task 4: `Crossing_the_size_limit_turns_formatting_off_and_on`).
5. **Format on text with CRLF line endings, a selection ending at the start of the next line, or no selection** — the edit keeps the note's line endings, touches only the lines the selection really covers, and undoes in one step (Task 7: `Prefix_keeps_crlf_and_ignores_a_line_the_selection_only_touches`, `Format_from_the_menu_is_one_undo_step`).

## File structure

| File | Task | Responsibility |
|---|---|---|
| `Services/Pad/PadLanguages.cs` (new) | 1 | Language records, extension map, Auto resolution, size limit |
| `Services/Pad/NoteMeta.cs` | 1 | `Language` (string?) |
| `Services/Pad/PadWorkspace.cs` | 1 | `SetLanguage` |
| `Models/SystemMetrics.cs` | 1 | `AppConfig.PadMarkdown` |
| `Services/Pad/PadPalette.cs`, `PadColor.cs` | 2 | 24 new colors (syntax, log, diff, Markdown); `EnsureContrast` |
| `Services/Pad/SyntaxColors.cs` (new) | 2 | Color name → category → palette color |
| `Pad/Highlighting/Ini.xshd`, `Yaml.xshd`, `Batch.xshd`, `Log.xshd` (new) | 3 | MicaPad's own definitions (embedded) |
| `Pad/PadHighlighting.cs` (new) | 3 | Definition for a language, cached; load failures logged |
| `Kil0bitSystemMonitor.csproj` | 3 | Embed the four `.xshd` files |
| `Pad/ThemedHighlightingColorizer.cs` (new) | 4 | Draw-time recoloring of AvalonEdit highlighting |
| `Pad/EditorLanguage.cs` (new) | 4, 6, 8 | Installs/removes a language's colorizers, renderers, generators and folding on one editor |
| `Pad/MicaPadWindow.xaml`, `.xaml.cs` | 4, 6, 7, 8 | Language status item and menu; `ApplyLanguage`; Markdown switch; Format menu; fold margin colors |
| `Services/Pad/FenceTracker.cs`, `MarkdownLineTokenizer.cs`, `MarkdownStyles.cs` (new) | 5 | Pure Markdown line model |
| `Pad/MarkdownDocumentCache.cs`, `MarkdownColorizer.cs`, `MarkdownBackgroundRenderer.cs`, `BulletGenerator.cs` (new) | 6 | Markdown rendering |
| `SettingsWindow.xaml`, `.xaml.cs` | 6 | *Markdown formatting* switch |
| `Services/Pad/MarkdownFormatter.cs` (new) | 7 | Format edits |
| `Services/Pad/BraceFolding.cs`, `HeadingFolding.cs` (new) | 8 | Fold ranges |
| `Pad/FoldingController.cs` (new) | 8 | FoldingManager lifecycle, debounce, reveal |
| `GUIDE.md`, `README.md` | 4, 6, 7, 8 | User docs |
| tests: `PadLanguagesTests.cs`, `SyntaxColorsTests.cs`, `PadHighlightingTests.cs`, `PadLanguageWindowTests.cs`, `MarkdownTokenizerTests.cs`, `MarkdownRenderingTests.cs`, `MarkdownFormatterTests.cs`, `PadFoldingTests.cs` (new); `PadPaletteTests.cs`, `PadConfigTests.cs`, `PadMenuTests.cs` (changed) | all | |

Focused test command (Git Bash, repo root):

```bash
DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~<TestClass>" -nologo
```

Whole suite: the same without `--filter`.

---

### Task 1: Languages, the per-note choice, and the Markdown setting

**Files:**
- Create: `Services/Pad/PadLanguages.cs`
- Modify: `Services/Pad/NoteMeta.cs` (after the `LineEnding` property)
- Modify: `Services/Pad/PadWorkspace.cs` (after `Rename`)
- Modify: `Models/SystemMetrics.cs` (MicaPad block)
- Create: `tests/Kil0bitSystemMonitor.Tests/PadLanguagesTests.cs`
- Modify: `tests/Kil0bitSystemMonitor.Tests/PadConfigTests.cs`

**Interfaces:**
- Produces:
  - `public enum PadFoldKind { None, Braces, Xml, Headings }`
  - `public sealed record PadLanguage(string Id, string Name, string? Definition, bool OwnDefinition, PadFoldKind Fold)`
  - `public sealed record ResolvedLanguage(PadLanguage Language, bool TooLarge)` with `string DisplayName` and `PadLanguage Effective`
  - `public static class PadLanguages` with `const int MaxFormattedChars`, `PadLanguage Plain`, `PadLanguage Markdown`, `IReadOnlyList<PadLanguage> All`, `PadLanguage? ById(string?)`, `PadLanguage ForPath(string)`, `ResolvedLanguage Resolve(string? chosenId, string? sourcePath, bool markdownOn, int textLength)`
  - `NoteMeta.Language` (`string?`), `PadWorkspace.SetLanguage(OpenNote, string?)`, `AppConfig.PadMarkdown` (`bool`, default true)

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/PadLanguagesTests.cs`:

```csharp
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Which language a tab uses: the extension table, Auto for notes, the Markdown switch, an explicit choice and the size limit.</summary>
    public class PadLanguagesTests
    {
        [Theory]
        [InlineData(@"C:\n\a.md", "markdown")]
        [InlineData(@"C:\n\a.markdown", "markdown")]
        [InlineData(@"C:\n\a.txt", "markdown")]
        [InlineData(@"C:\n\a.json", "json")]
        [InlineData(@"C:\n\A.JSON", "json")]
        [InlineData(@"C:\n\a.jsonc", "json")]
        [InlineData(@"C:\n\App.xaml", "xml")]
        [InlineData(@"C:\n\a.csproj", "xml")]
        [InlineData(@"C:\n\a.svg", "xml")]
        [InlineData(@"C:\n\a.htm", "html")]
        [InlineData(@"C:\n\a.cs", "csharp")]
        [InlineData(@"C:\n\a.ts", "javascript")]
        [InlineData(@"C:\n\a.jsx", "javascript")]
        [InlineData(@"C:\n\a.css", "css")]
        [InlineData(@"C:\n\a.psm1", "powershell")]
        [InlineData(@"C:\n\a.py", "python")]
        [InlineData(@"C:\n\a.sql", "sql")]
        [InlineData(@"C:\n\a.hpp", "cpp")]
        [InlineData(@"C:\n\a.java", "java")]
        [InlineData(@"C:\n\a.php", "php")]
        [InlineData(@"C:\n\a.bas", "vb")]
        [InlineData(@"C:\n\a.patch", "diff")]
        [InlineData(@"C:\n\a.cfg", "ini")]
        [InlineData(@"C:\n\.editorconfig", "ini")]
        [InlineData(@"C:\n\a.yml", "yaml")]
        [InlineData(@"C:\n\a.cmd", "batch")]
        [InlineData(@"C:\n\micastats.log", "log")]
        [InlineData(@"C:\n\a.csv", "plain")]
        [InlineData(@"C:\n\README", "plain")]
        public void Auto_picks_by_file_type(string path, string expected)
        {
            Assert.Equal(expected, PadLanguages.Resolve(null, path, markdownOn: true, textLength: 10).Language.Id);
        }

        [Fact]
        public void Notes_are_markdown_unless_markdown_is_off()
        {
            Assert.Same(PadLanguages.Markdown, PadLanguages.Resolve(null, null, true, 10).Language);
            Assert.Same(PadLanguages.Plain, PadLanguages.Resolve(null, null, false, 10).Language);
        }

        [Fact]
        public void With_markdown_off_md_and_txt_are_plain_but_code_keeps_its_colors()
        {
            Assert.Same(PadLanguages.Plain, PadLanguages.Resolve(null, @"C:\n\a.md", false, 10).Language);
            Assert.Same(PadLanguages.Plain, PadLanguages.Resolve(null, @"C:\n\a.txt", false, 10).Language);
            Assert.Equal("json", PadLanguages.Resolve(null, @"C:\n\a.json", false, 10).Language.Id);
        }

        [Fact]
        public void An_explicit_choice_wins_over_auto_and_the_switch()
        {
            Assert.Same(PadLanguages.Plain, PadLanguages.Resolve("plain", @"C:\n\a.json", true, 10).Language);
            Assert.Same(PadLanguages.Markdown, PadLanguages.Resolve("markdown", null, false, 10).Language);
            Assert.Equal("json", PadLanguages.Resolve("JSON", null, true, 10).Language.Id);
        }

        [Fact]
        public void An_unknown_choice_falls_back_to_auto()
        {
            Assert.Equal("json", PadLanguages.Resolve("klingon", @"C:\n\a.json", true, 10).Language.Id);
        }

        [Fact]
        public void Text_over_the_limit_is_shown_plain_and_says_so()
        {
            var atLimit = PadLanguages.Resolve(null, @"C:\n\a.json", true, PadLanguages.MaxFormattedChars);
            Assert.False(atLimit.TooLarge);
            Assert.Equal("JSON", atLimit.DisplayName);

            var over = PadLanguages.Resolve(null, @"C:\n\a.json", true, PadLanguages.MaxFormattedChars + 1);
            Assert.True(over.TooLarge);
            Assert.Equal("Plain text (large)", over.DisplayName);
            Assert.Same(PadLanguages.Plain, over.Effective);
            Assert.Equal("json", over.Language.Id);
        }

        [Fact]
        public void Languages_are_listed_once_with_names_and_fold_kinds()
        {
            var all = PadLanguages.All;
            Assert.Equal(20, all.Count);
            Assert.Equal(all.Count, all.Select(l => l.Id).Distinct().Count());
            Assert.All(all, l => Assert.False(string.IsNullOrWhiteSpace(l.Name)));
            Assert.Same(PadLanguages.Plain, all[0]);
            Assert.Same(PadLanguages.Markdown, all[1]);
            Assert.Equal(PadFoldKind.Braces, PadLanguages.ById("json")!.Fold);
            Assert.Equal(PadFoldKind.Xml, PadLanguages.ById("html")!.Fold);
            Assert.Equal(PadFoldKind.Headings, PadLanguages.Markdown.Fold);
            Assert.Equal(PadFoldKind.None, PadLanguages.ById("python")!.Fold);
            Assert.True(PadLanguages.ById("log")!.OwnDefinition);
            Assert.Null(PadLanguages.Markdown.Definition);
        }

        [Fact]
        public void A_chosen_language_is_saved_with_the_note()
        {
            using var env = new PadTestEnv();
            var note = env.Workspace.NewNote();

            env.Workspace.SetLanguage(note, "json");
            env.Flush();
            Assert.Equal("json", env.Store.LoadMeta(note.Id)!.Language);

            env.Workspace.SetLanguage(note, "klingon");   // unknown: back to Auto
            env.Flush();
            Assert.Null(note.Meta.Language);
            Assert.Null(env.Store.LoadMeta(note.Id)!.Language);
        }
    }
}
```

Add to `PadConfigTests` (inside the class):

```csharp
        [Fact]
        public void Markdown_formatting_is_on_by_default_and_round_trips()
        {
            Assert.True(new AppConfig().PadMarkdown);
            Assert.True(JsonSerializer.Deserialize<AppConfig>("{\"ShowCpu\": false}")!.PadMarkdown);
            var back = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig { PadMarkdown = false }))!;
            Assert.False(back.PadMarkdown);
        }
```

- [ ] **Step 2: Run to verify they fail**

Run the focused command for `PadLanguagesTests|PadConfigTests` (use `--filter "FullyQualifiedName~PadLanguagesTests|FullyQualifiedName~PadConfigTests"`). Expected: build FAILS (`PadLanguages`, `PadFoldKind` not found; `NoteMeta` has no `Language`; `AppConfig` has no `PadMarkdown`).

- [ ] **Step 3: Write `Services/Pad/PadLanguages.cs`**

```csharp
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
    /// which MicaPad formats itself).
    /// </summary>
    public sealed record PadLanguage(string Id, string Name, string? Definition, bool OwnDefinition, PadFoldKind Fold);

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
            new PadLanguage("csharp", "C#", "C#", false, PadFoldKind.Braces),
            new PadLanguage("javascript", "JavaScript", "JavaScript", false, PadFoldKind.Braces),
            new PadLanguage("css", "CSS", "CSS", false, PadFoldKind.Braces),
            new PadLanguage("powershell", "PowerShell", "PowerShell", false, PadFoldKind.Braces),
            new PadLanguage("python", "Python", "Python", false, PadFoldKind.None),
            new PadLanguage("sql", "SQL", "TSQL", false, PadFoldKind.None),
            new PadLanguage("cpp", "C/C++", "C++", false, PadFoldKind.Braces),
            new PadLanguage("java", "Java", "Java", false, PadFoldKind.Braces),
            new PadLanguage("php", "PHP", "PHP", false, PadFoldKind.Braces),
            new PadLanguage("vb", "VB", "VB", false, PadFoldKind.None),
            new PadLanguage("diff", "Diff", "Patch", false, PadFoldKind.None),
            new PadLanguage("ini", "INI", "Ini", true, PadFoldKind.None),
            new PadLanguage("yaml", "YAML", "Yaml", true, PadFoldKind.None),
            new PadLanguage("batch", "Batch", "Batch", true, PadFoldKind.None),
            new PadLanguage("log", "Log", "Log", true, PadFoldKind.None),
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
            [".ts"] = "javascript", [".tsx"] = "javascript", [".jsx"] = "javascript",
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

        /// <summary>
        /// The language a file's extension implies, or Plain text. <c>.editorconfig</c> and
        /// <c>.gitconfig</c> count: for a name that starts with a dot, the whole name is the extension.
        /// </summary>
        public static PadLanguage ForPath(string path)
        {
            string extension = Path.GetExtension(path);
            return extension.Length > 0 && ByExtension.TryGetValue(extension, out string? id) ? ById(id)! : Plain;
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
```

- [ ] **Step 4: Add `NoteMeta.Language`**

In `Services/Pad/NoteMeta.cs`, after the `LineEnding` property:

```csharp
        /// <summary>
        /// The language chosen for this note in the status bar (a <see cref="PadLanguage.Id"/>), or
        /// null for Auto: by file type, Markdown for notes. Older meta.json files have none.
        /// </summary>
        public string? Language { get; set; }
```

- [ ] **Step 5: Add `PadWorkspace.SetLanguage`**

In `Services/Pad/PadWorkspace.cs`, after `Rename`:

```csharp
        /// <summary>
        /// Sets the language a note is shown in; null or an unknown id returns it to Auto. Saved with
        /// the note like a rename: it is not an edit of a file.
        /// </summary>
        public void SetLanguage(OpenNote note, string? languageId)
        {
            note.Meta.Language = PadLanguages.ById(languageId)?.Id;
            EnqueueSave(note);
        }
```

- [ ] **Step 6: Add `AppConfig.PadMarkdown`**

In `Models/SystemMetrics.cs`, MicaPad block: field `private bool _padMarkdown = true;` next to the other `_pad*` fields, and after the `PadTheme` property:

```csharp
        /// <summary>
        /// Markdown formatting for notes and .md/.markdown/.txt files; off shows them as plain text.
        /// A language chosen for a tab still wins.
        /// </summary>
        public bool PadMarkdown { get => _padMarkdown; set { Set(ref _padMarkdown, value); } }
```

- [ ] **Step 7: Run the focused tests, then the whole suite** — expected PASS.

- [ ] **Step 8: Commit**

```bash
git add Services/Pad/PadLanguages.cs Services/Pad/NoteMeta.cs Services/Pad/PadWorkspace.cs Models/SystemMetrics.cs tests/Kil0bitSystemMonitor.Tests/PadLanguagesTests.cs tests/Kil0bitSystemMonitor.Tests/PadConfigTests.cs
git commit -m "feat(pad): languages by file type, a per-note choice, and the Markdown switch" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

### Task 2: Syntax, log, diff and Markdown colors, and how definition colors map onto them

**Files:**
- Modify: `Services/Pad/PadPalette.cs` (24 properties, both palettes, `Resources()`)
- Modify: `Services/Pad/PadColor.cs` (`EnsureContrast`)
- Create: `Services/Pad/SyntaxColors.cs`
- Modify: `tests/Kil0bitSystemMonitor.Tests/PadPaletteTests.cs` (count 25 → 49; more contrast rules)
- Create: `tests/Kil0bitSystemMonitor.Tests/SyntaxColorsTests.cs`

**Interfaces:**
- Consumes (Part 1): `PadPalette`, `PadColor`, `PadThemes`.
- Produces:
  - New `PadPalette` properties: `SyntaxComment, SyntaxString, SyntaxKeyword, SyntaxNumber, SyntaxType, SyntaxPreprocessor, SyntaxTag, SyntaxAttribute, SyntaxOperator, LogError, LogWarning, LogInfo, LogDebug, DiffAdded, DiffRemoved, MdHeading, MdMarker, MdCodeBackground, MdLink, MdQuoteBar, MdQuoteText, MdListMarker, MdTaskDone, MdRule`
  - `PadColor.EnsureContrast(PadColor background, PadColor toward, double minimum)`
  - `public enum SyntaxCategory { Text, Comment, String, Keyword, Number, Type, Preprocessor, Tag, Attribute, Operator, Error, Warning, Info, Debug, Added, Removed }`
  - `SyntaxColors.Categorize(string?)`, `SyntaxColors.ColorOf(SyntaxCategory, PadPalette)`, `SyntaxColors.Resolve(string? colorName, PadColor? original, PadPalette palette)` → `PadColor?`

- [ ] **Step 1: Write the failing tests**

In `tests/Kil0bitSystemMonitor.Tests/PadPaletteTests.cs`:

1. In `Every_color_is_listed_once_under_its_resource_key`, change `Assert.Equal(25, properties.Count);` to `Assert.Equal(49, properties.Count);`.
2. In `ContrastRules`, add these rows to the `rules` array:

```csharp
                ("SyntaxComment", "Background", 4.5), ("SyntaxString", "Background", 4.5),
                ("SyntaxKeyword", "Background", 4.5), ("SyntaxNumber", "Background", 4.5),
                ("SyntaxType", "Background", 4.5), ("SyntaxPreprocessor", "Background", 4.5),
                ("SyntaxTag", "Background", 4.5), ("SyntaxAttribute", "Background", 4.5),
                ("SyntaxOperator", "Background", 4.5),
                ("LogError", "Background", 4.5), ("LogWarning", "Background", 4.5),
                ("LogInfo", "Background", 4.5), ("LogDebug", "Background", 4.5),
                ("DiffAdded", "Background", 4.5), ("DiffRemoved", "Background", 4.5),
                ("MdHeading", "Background", 4.5), ("MdLink", "Background", 4.5),
                ("MdQuoteText", "Background", 4.5), ("MdListMarker", "Background", 4.5),
                ("MdTaskDone", "Background", 4.5), ("MdMarker", "Background", 3),
                ("Text", "MdCodeBackground", 7),
```

Create `tests/Kil0bitSystemMonitor.Tests/SyntaxColorsTests.cs`:

```csharp
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>How a highlighting definition's color names map onto the MicaPad palette.</summary>
    public class SyntaxColorsTests
    {
        [Theory]
        [InlineData("Comment", SyntaxCategory.Comment)]
        [InlineData("DocComment", SyntaxCategory.Comment)]
        [InlineData("CommentTags", SyntaxCategory.Comment)]
        [InlineData("String", SyntaxCategory.String)]
        [InlineData("XmlString", SyntaxCategory.String)]
        [InlineData("Char", SyntaxCategory.String)]
        [InlineData("Character", SyntaxCategory.String)]
        [InlineData("AttributeValue", SyntaxCategory.String)]
        [InlineData("Regex", SyntaxCategory.String)]
        [InlineData("Keywords", SyntaxCategory.Keyword)]
        [InlineData("KeywordX", SyntaxCategory.Keyword)]
        [InlineData("JavaScriptKeyWords", SyntaxCategory.Keyword)]
        [InlineData("ValueTypeKeywords", SyntaxCategory.Keyword)]
        [InlineData("ThisOrBaseReference", SyntaxCategory.Keyword)]
        [InlineData("TrueFalse", SyntaxCategory.Keyword)]
        [InlineData("Bool", SyntaxCategory.Keyword)]
        [InlineData("Null", SyntaxCategory.Keyword)]
        [InlineData("Modifiers", SyntaxCategory.Keyword)]
        [InlineData("NumberLiteral", SyntaxCategory.Number)]
        [InlineData("Digits", SyntaxCategory.Number)]
        [InlineData("LogTimestamp", SyntaxCategory.Number)]
        [InlineData("ReferenceTypes", SyntaxCategory.Type)]
        [InlineData("DataTypes", SyntaxCategory.Type)]
        [InlineData("Class", SyntaxCategory.Type)]
        [InlineData("Preprocessor", SyntaxCategory.Preprocessor)]
        [InlineData("DocType", SyntaxCategory.Preprocessor)]
        [InlineData("Header", SyntaxCategory.Preprocessor)]
        [InlineData("XmlTag", SyntaxCategory.Tag)]
        [InlineData("Selector", SyntaxCategory.Tag)]
        [InlineData("Section", SyntaxCategory.Tag)]
        [InlineData("AttributeName", SyntaxCategory.Attribute)]
        [InlineData("Property", SyntaxCategory.Attribute)]
        [InlineData("FieldName", SyntaxCategory.Attribute)]
        [InlineData("KeyName", SyntaxCategory.Attribute)]
        [InlineData("Variable", SyntaxCategory.Attribute)]
        [InlineData("Punctuation", SyntaxCategory.Operator)]
        [InlineData("CurlyBraces", SyntaxCategory.Operator)]
        [InlineData("LogError", SyntaxCategory.Error)]
        [InlineData("LogWarning", SyntaxCategory.Warning)]
        [InlineData("LogInfo", SyntaxCategory.Info)]
        [InlineData("LogDebug", SyntaxCategory.Debug)]
        [InlineData("AddedText", SyntaxCategory.Added)]
        [InlineData("RemovedText", SyntaxCategory.Removed)]
        [InlineData("MethodCall", SyntaxCategory.Text)]
        [InlineData("", SyntaxCategory.Text)]
        [InlineData(null, SyntaxCategory.Text)]
        public void Color_names_map_to_categories(string? name, SyntaxCategory expected)
        {
            Assert.Equal(expected, SyntaxColors.Categorize(name));
        }

        [Fact]
        public void A_named_color_takes_the_palette_color_of_its_category()
        {
            Assert.Equal(PadPalette.Dark.SyntaxComment, SyntaxColors.Resolve("Comment", PadColor.Parse("#008000"), PadPalette.Dark));
            Assert.Equal(PadPalette.Light.SyntaxKeyword, SyntaxColors.Resolve("Keywords", null, PadPalette.Light));
            Assert.Equal(PadPalette.Light.Text, SyntaxColors.Resolve("MethodCall", PadColor.Parse("#FFFF00"), PadPalette.Light));
        }

        [Fact]
        public void An_unnamed_color_keeps_its_hue_but_is_made_readable()
        {
            var yellow = PadColor.Parse("#FFFF00");   // unreadable on white
            var onLight = SyntaxColors.Resolve(null, yellow, PadPalette.Light)!.Value;
            Assert.True(PadColor.Contrast(onLight, PadPalette.Light.Background) >= 4.5);

            var navy = PadColor.Parse("#000080");     // unreadable on the dark page
            var onDark = SyntaxColors.Resolve("", navy, PadPalette.Dark)!.Value;
            Assert.True(PadColor.Contrast(onDark, PadPalette.Dark.Background) >= 4.5);
        }

        [Fact]
        public void A_readable_unnamed_color_is_kept_as_it_is()
        {
            var green = PadColor.Parse("#1A7F37");
            Assert.Equal(green, SyntaxColors.Resolve(null, green, PadPalette.Light));
        }

        [Fact]
        public void With_neither_name_nor_color_nothing_changes()
        {
            Assert.Null(SyntaxColors.Resolve(null, null, PadPalette.Dark));
        }

        [Fact]
        public void EnsureContrast_mixes_toward_the_text_color_only_as_far_as_needed()
        {
            var white = PadColor.Parse("#FFFFFF");
            var black = PadColor.Parse("#000000");
            var pale = PadColor.Parse("#EEEEEE");

            var result = pale.EnsureContrast(white, black, 4.5);

            Assert.True(PadColor.Contrast(result, white) >= 4.5);
            Assert.True(result.R > 0);                                         // not all the way to black
            Assert.Equal(black, pale.EnsureContrast(white, black, 30));       // impossible: ends at the target
        }

        [Theory]
        [InlineData("Dark")]
        [InlineData("Light")]
        public void Every_category_reads_well(string theme)
        {
            var palette = PadPalette.For(theme);
            foreach (SyntaxCategory category in System.Enum.GetValues(typeof(SyntaxCategory)))
                Assert.True(PadColor.Contrast(SyntaxColors.ColorOf(category, palette), palette.Background) >= 4.5, theme + " " + category);
        }
    }
}
```

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~SyntaxColorsTests|FullyQualifiedName~PadPaletteTests"`). Expected: build FAILS (`SyntaxCategory`, `SyntaxColors`, `EnsureContrast`, the new palette properties not found).

- [ ] **Step 3: Add `EnsureContrast` to `Services/Pad/PadColor.cs`** (after `Contrast`):

```csharp
        /// <summary>
        /// This color painted on <paramref name="background"/>, or the first mix of it toward
        /// <paramref name="toward"/> (in tenths) that reaches <paramref name="minimum"/> contrast —
        /// the hue survives as far as readability allows. Always opaque; ends at
        /// <paramref name="toward"/> itself when no mix is enough.
        /// </summary>
        public PadColor EnsureContrast(PadColor background, PadColor toward, double minimum)
        {
            var solid = background with { A = 255 };
            PadColor from = Over(solid);
            PadColor to = toward.Over(solid);
            for (int step = 0; step <= 10; step++)
            {
                double t = step / 10.0;
                var mixed = new PadColor(255, Lerp(from.R, to.R, t), Lerp(from.G, to.G, t), Lerp(from.B, to.B, t));
                if (Contrast(mixed, solid) >= minimum) return mixed;
            }
            return to;
        }

        private static byte Lerp(byte a, byte b, double t) =>
            (byte)Math.Round(a + (b - a) * t, MidpointRounding.AwayFromZero);
```

- [ ] **Step 4: Add the 24 colors to `Services/Pad/PadPalette.cs`**

Add the properties after `FindMatch` (same `{ get; private init; }` pattern):

```csharp
        /// <summary>Syntax colors (spec 2.2): one per category, each at least 4.5:1 on the background.</summary>
        public PadColor SyntaxComment { get; private init; }
        public PadColor SyntaxString { get; private init; }
        public PadColor SyntaxKeyword { get; private init; }
        public PadColor SyntaxNumber { get; private init; }
        public PadColor SyntaxType { get; private init; }
        public PadColor SyntaxPreprocessor { get; private init; }
        public PadColor SyntaxTag { get; private init; }
        public PadColor SyntaxAttribute { get; private init; }
        public PadColor SyntaxOperator { get; private init; }
        /// <summary>Log levels (MicaPad's Log.xshd).</summary>
        public PadColor LogError { get; private init; }
        public PadColor LogWarning { get; private init; }
        public PadColor LogInfo { get; private init; }
        public PadColor LogDebug { get; private init; }
        /// <summary>Added and removed lines (Diff files; Part 5's history compare).</summary>
        public PadColor DiffAdded { get; private init; }
        public PadColor DiffRemoved { get; private init; }
        /// <summary>Markdown styled source (spec 2.3).</summary>
        public PadColor MdHeading { get; private init; }
        /// <summary>The dimmed # ** ` > markers.</summary>
        public PadColor MdMarker { get; private init; }
        /// <summary>Behind inline code and fenced blocks.</summary>
        public PadColor MdCodeBackground { get; private init; }
        public PadColor MdLink { get; private init; }
        public PadColor MdQuoteBar { get; private init; }
        public PadColor MdQuoteText { get; private init; }
        public PadColor MdListMarker { get; private init; }
        public PadColor MdTaskDone { get; private init; }
        public PadColor MdRule { get; private init; }
```

In the `Dark` initializer add:

```csharp
            SyntaxComment = PadColor.Parse("#6A9955"),
            SyntaxString = PadColor.Parse("#CE9178"),
            SyntaxKeyword = PadColor.Parse("#569CD6"),
            SyntaxNumber = PadColor.Parse("#B5CEA8"),
            SyntaxType = PadColor.Parse("#4EC9B0"),
            SyntaxPreprocessor = PadColor.Parse("#C586C0"),
            SyntaxTag = PadColor.Parse("#4FC1FF"),
            SyntaxAttribute = PadColor.Parse("#9CDCFE"),
            SyntaxOperator = PadColor.Parse("#D4D4D4"),
            LogError = PadColor.Parse("#FF6B6B"),
            LogWarning = PadColor.Parse("#E8A53C"),
            LogInfo = PadColor.Parse("#3FD2E4"),
            LogDebug = PadColor.Parse("#88EDEDF2"),
            DiffAdded = PadColor.Parse("#6BD968"),
            DiffRemoved = PadColor.Parse("#FF7B72"),
            MdHeading = PadColor.Parse("#7FE3F0"),
            MdMarker = PadColor.Parse("#66EDEDF2"),
            MdCodeBackground = PadColor.Parse("#1B1B24"),
            MdLink = PadColor.Parse("#3FD2E4"),
            MdQuoteBar = PadColor.Parse("#663FD2E4"),
            MdQuoteText = PadColor.Parse("#B0EDEDF2"),
            MdListMarker = PadColor.Parse("#3FD2E4"),
            MdTaskDone = PadColor.Parse("#88EDEDF2"),
            MdRule = PadColor.Parse("#33FFFFFF"),
```

In the `Light` initializer add:

```csharp
            SyntaxComment = PadColor.Parse("#1E7A1E"),
            SyntaxString = PadColor.Parse("#A31515"),
            SyntaxKeyword = PadColor.Parse("#0000E0"),
            SyntaxNumber = PadColor.Parse("#07704A"),
            SyntaxType = PadColor.Parse("#1D6B80"),
            SyntaxPreprocessor = PadColor.Parse("#8A00B0"),
            SyntaxTag = PadColor.Parse("#800000"),
            SyntaxAttribute = PadColor.Parse("#B00000"),
            SyntaxOperator = PadColor.Parse("#3B3B3B"),
            LogError = PadColor.Parse("#C62828"),
            LogWarning = PadColor.Parse("#8A5200"),
            LogInfo = PadColor.Parse("#06707C"),
            LogDebug = PadColor.Parse("#666670"),
            DiffAdded = PadColor.Parse("#1A7F37"),
            DiffRemoved = PadColor.Parse("#C62828"),
            MdHeading = PadColor.Parse("#0B4F59"),
            MdMarker = PadColor.Parse("#8A8A94"),
            MdCodeBackground = PadColor.Parse("#EEEEF3"),
            MdLink = PadColor.Parse("#06707C"),
            MdQuoteBar = PadColor.Parse("#6606707C"),
            MdQuoteText = PadColor.Parse("#55555F"),
            MdListMarker = PadColor.Parse("#06707C"),
            MdTaskDone = PadColor.Parse("#666670"),
            MdRule = PadColor.Parse("#26000000"),
```

In `Resources()` add one `Pair(nameof(X), X),` line for each of the 24 new properties, after `FindMatch`.

- [ ] **Step 5: Write `Services/Pad/SyntaxColors.cs`**

```csharp
using System;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>What a highlighting color is, whatever the definition calls it (spec 2.2).</summary>
    public enum SyntaxCategory
    {
        Text,
        Comment,
        String,
        Keyword,
        Number,
        Type,
        Preprocessor,
        Tag,
        Attribute,
        Operator,
        Error,
        Warning,
        Info,
        Debug,
        Added,
        Removed,
    }

    /// <summary>
    /// Maps the color names in AvalonEdit's definitions (and MicaPad's own) onto the palette.
    /// AvalonEdit's colors were chosen for white pages; MicaPad paints each named color with its
    /// category's palette color instead, so every language reads in both themes.
    /// </summary>
    public static class SyntaxColors
    {
        /// <summary>
        /// Tried top to bottom; the first row with a name contained in the color name (ignoring case)
        /// wins, so "KeywordX" is a keyword before "Key" could make it an attribute, and
        /// "AttributeValue" a string before "Value" could make it a keyword.
        /// </summary>
        private static readonly (SyntaxCategory Category, string[] Names)[] Rows =
        {
            (SyntaxCategory.Comment, new[] { "Comment" }),
            (SyntaxCategory.String, new[] { "String", "Char", "Verbatim", "Regex", "AttributeValue" }),
            (SyntaxCategory.Keyword, new[] { "Keyword", "Modifier", "Visibility", "Access", "This", "Null", "True", "False", "Bool", "Value" }),
            (SyntaxCategory.Number, new[] { "Number", "Digit", "Timestamp" }),
            (SyntaxCategory.Type, new[] { "Type", "Class", "Reference" }),
            (SyntaxCategory.Preprocessor, new[] { "Preprocessor", "Directive", "Region", "DocType", "XmlDeclaration", "Header", "Position" }),
            (SyntaxCategory.Tag, new[] { "Tag", "Element", "Selector", "Section" }),
            (SyntaxCategory.Attribute, new[] { "Attribute", "Property", "Key", "FieldName", "Variable" }),
            (SyntaxCategory.Operator, new[] { "Operator", "Punctuation", "Brace" }),
            (SyntaxCategory.Error, new[] { "Error", "Fatal" }),
            (SyntaxCategory.Warning, new[] { "Warn" }),
            (SyntaxCategory.Info, new[] { "Info" }),
            (SyntaxCategory.Debug, new[] { "Debug", "Trace" }),
            (SyntaxCategory.Added, new[] { "Added" }),
            (SyntaxCategory.Removed, new[] { "Removed" }),
        };

        /// <summary>The category of a definition's color name; an empty or unknown name is Text.</summary>
        public static SyntaxCategory Categorize(string? colorName)
        {
            if (string.IsNullOrEmpty(colorName)) return SyntaxCategory.Text;
            foreach (var (category, names) in Rows)
                foreach (string name in names)
                    if (colorName.Contains(name, StringComparison.OrdinalIgnoreCase)) return category;
            return SyntaxCategory.Text;
        }

        /// <summary>The palette color of a category.</summary>
        public static PadColor ColorOf(SyntaxCategory category, PadPalette palette) => category switch
        {
            SyntaxCategory.Comment => palette.SyntaxComment,
            SyntaxCategory.String => palette.SyntaxString,
            SyntaxCategory.Keyword => palette.SyntaxKeyword,
            SyntaxCategory.Number => palette.SyntaxNumber,
            SyntaxCategory.Type => palette.SyntaxType,
            SyntaxCategory.Preprocessor => palette.SyntaxPreprocessor,
            SyntaxCategory.Tag => palette.SyntaxTag,
            SyntaxCategory.Attribute => palette.SyntaxAttribute,
            SyntaxCategory.Operator => palette.SyntaxOperator,
            SyntaxCategory.Error => palette.LogError,
            SyntaxCategory.Warning => palette.LogWarning,
            SyntaxCategory.Info => palette.LogInfo,
            SyntaxCategory.Debug => palette.LogDebug,
            SyntaxCategory.Added => palette.DiffAdded,
            SyntaxCategory.Removed => palette.DiffRemoved,
            _ => palette.Text,
        };

        /// <summary>
        /// The color to paint a highlighting color with. A named color takes its category's palette
        /// color. An unnamed one (a definition's inline color) keeps its own hue, moved toward the
        /// text color until it reads at 4.5:1. With neither, null: leave the text as it is.
        /// </summary>
        public static PadColor? Resolve(string? colorName, PadColor? original, PadPalette palette)
        {
            if (!string.IsNullOrEmpty(colorName)) return ColorOf(Categorize(colorName), palette);
            return original?.EnsureContrast(palette.Background, palette.Text, 4.5);
        }
    }
}
```

- [ ] **Step 6: Run the focused tests, then the whole suite** — expected PASS.

- [ ] **Step 7: Commit**

```bash
git add Services/Pad/PadPalette.cs Services/Pad/PadColor.cs Services/Pad/SyntaxColors.cs tests/Kil0bitSystemMonitor.Tests/PadPaletteTests.cs tests/Kil0bitSystemMonitor.Tests/SyntaxColorsTests.cs
git commit -m "feat(pad): syntax, log, diff and Markdown colors for both themes" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

### Task 3: MicaPad's own definitions (INI, YAML, Batch, Log) and a definition loader

**Files:**
- Create: `Pad/Highlighting/Ini.xshd`, `Pad/Highlighting/Yaml.xshd`, `Pad/Highlighting/Batch.xshd`, `Pad/Highlighting/Log.xshd`
- Modify: `Kil0bitSystemMonitor.csproj` (embed them)
- Create: `Pad/PadHighlighting.cs`
- Create: `tests/Kil0bitSystemMonitor.Tests/PadHighlightingTests.cs`

**Interfaces:**
- Consumes: `PadLanguage`, `PadLanguages.All` (Task 1); `SyntaxColors.Resolve`, `PadPalette` (Task 2).
- Produces: `internal static class PadHighlighting` with `IHighlightingDefinition? For(PadLanguage language)` (cached; null for Plain/Markdown or when loading fails, which is logged once under `pad`). Embedded resource names: `MicaPad.Highlighting.<Name>.xshd`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/PadHighlightingTests.cs`:

```csharp
using System.Linq;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Every language's definition loads, MicaPad's own definitions color what they should, and every named color reads well in both themes.</summary>
    public class PadHighlightingTests
    {
        [Fact]
        public void Every_language_with_colors_has_a_definition() => UiThread.Run(() =>
        {
            foreach (var language in PadLanguages.All.Where(l => l.Definition != null))
                Assert.True(PadHighlighting.For(language) != null, language.Name + " did not load");
            Assert.Null(PadHighlighting.For(PadLanguages.Plain));
            Assert.Null(PadHighlighting.For(PadLanguages.Markdown));
        });

        [Fact]
        public void Definitions_are_loaded_once() => UiThread.Run(() =>
        {
            var log = PadLanguages.ById("log")!;
            Assert.Same(PadHighlighting.For(log), PadHighlighting.For(log));
        });

        [Fact]
        public void Every_named_color_reads_well_in_both_themes() => UiThread.Run(() =>
        {
            foreach (var language in PadLanguages.All.Where(l => l.Definition != null))
            {
                var definition = PadHighlighting.For(language)!;
                foreach (var color in definition.NamedHighlightingColors)
                    foreach (var palette in new[] { PadPalette.Dark, PadPalette.Light })
                    {
                        var paint = SyntaxColors.Resolve(color.Name, null, palette)!.Value;
                        Assert.True(PadColor.Contrast(paint, palette.Background) >= 4.5,
                            language.Name + "." + color.Name + " in " + palette.Name);
                    }
            }
        });

        [Theory]
        [InlineData("ini", "[main]", "[main]", "Section")]
        [InlineData("ini", "key = value", "key", "KeyName")]
        [InlineData("ini", "key = value", "value", "String")]
        [InlineData("ini", "; a comment", "; a comment", "Comment")]
        [InlineData("yaml", "name: MicaPad # note", "name", "KeyName")]
        [InlineData("yaml", "name: MicaPad # note", "# note", "Comment")]
        [InlineData("yaml", "enabled: true", "true", "Bool")]
        [InlineData("yaml", "count: 42", "42", "Number")]
        [InlineData("batch", "@echo off", "echo", "Keywords")]
        [InlineData("batch", "rem cleanup", "rem cleanup", "Comment")]
        [InlineData("batch", ":: cleanup", ":: cleanup", "Comment")]
        [InlineData("batch", ":done", ":done", "LabelDirective")]
        [InlineData("batch", "echo %PATH%", "%PATH%", "Variable")]
        [InlineData("log", "2026-09-30 16:05:34.932 [ERROR] boom", "2026-09-30 16:05:34.932", "LogTimestamp")]
        [InlineData("log", "2026-09-30 16:05:34.932 [ERROR] boom", "ERROR", "LogError")]
        [InlineData("log", "[warn] disk low", "warn", "LogWarning")]
        [InlineData("log", "INFO started", "INFO", "LogInfo")]
        [InlineData("log", "DEBUG x=1", "DEBUG", "LogDebug")]
        public void Own_definitions_color_what_they_should(string languageId, string line, string part, string colorName) => UiThread.Run(() =>
        {
            var definition = PadHighlighting.For(PadLanguages.ById(languageId)!)!;
            var highlighter = new DocumentHighlighter(new TextDocument(line), definition);
            var sections = highlighter.HighlightLine(1).Sections;
            int start = line.IndexOf(part, System.StringComparison.Ordinal);

            Assert.Contains(sections, s => s.Offset == start && s.Length == part.Length && s.Color.Name == colorName);
        });
    }
}
```

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~PadHighlightingTests"`). Expected: build FAILS (`PadHighlighting` not found).

- [ ] **Step 3: Write the four definitions**

`Pad/Highlighting/Ini.xshd`:

```xml
<?xml version="1.0"?>
<!-- MicaPad's INI highlighting. Colors are names only: MicaPad paints them from its palette. -->
<SyntaxDefinition name="MicaPad INI" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
  <Color name="Comment" />
  <Color name="Section" />
  <Color name="KeyName" />
  <Color name="String" />
  <RuleSet ignoreCase="true">
    <Rule color="Comment">^\s*[;\#].*$</Rule>
    <Rule color="Section">^\s*\[[^\]\r\n]*\]</Rule>
    <Rule color="KeyName">^\s*[^=:\s;\#\[][^=:\r\n]*?(?=\s*[=:])</Rule>
    <Rule color="String">(?&lt;=[=:]\s*)\S.*$</Rule>
  </RuleSet>
</SyntaxDefinition>
```

`Pad/Highlighting/Yaml.xshd`:

```xml
<?xml version="1.0"?>
<!-- MicaPad's YAML highlighting. Colors are names only: MicaPad paints them from its palette. -->
<SyntaxDefinition name="MicaPad YAML" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
  <Color name="Comment" />
  <Color name="KeyName" />
  <Color name="String" />
  <Color name="Number" />
  <Color name="Bool" />
  <Color name="Directive" />
  <RuleSet>
    <Span color="Comment" begin="(?&lt;=^|\s)\#" />
    <Span color="String" begin="&quot;" end="&quot;">
      <RuleSet>
        <Span begin="\\" end="." />
      </RuleSet>
    </Span>
    <Span color="String" begin="'" end="'" />
    <Rule color="Directive">^(---|\.\.\.)\s*$</Rule>
    <Rule color="KeyName">^\s*(-\s+)?[^\s\#'"-][^:\#\r\n]*?(?=:(\s|$))</Rule>
    <Rule color="Bool">\b(true|false|yes|no|on|off|null)\b</Rule>
    <Rule color="Number">(?&lt;![\w.])-?\d+(\.\d+)?(?![\w.])</Rule>
  </RuleSet>
</SyntaxDefinition>
```

`Pad/Highlighting/Batch.xshd`:

```xml
<?xml version="1.0"?>
<!-- MicaPad's Windows batch highlighting. Colors are names only: MicaPad paints them from its palette. -->
<SyntaxDefinition name="MicaPad Batch" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
  <Color name="Comment" />
  <Color name="Keywords" />
  <Color name="Variable" />
  <Color name="LabelDirective" />
  <Color name="String" />
  <RuleSet ignoreCase="true">
    <Rule color="Comment">^\s*@?rem(\s.*)?$</Rule>
    <Rule color="Comment">^\s*::.*$</Rule>
    <Rule color="LabelDirective">^\s*:[^:\s].*$</Rule>
    <Span color="String" begin="&quot;" end="&quot;" />
    <Rule color="Variable">%[^%\s]+%|%~?[0-9a-z]|![\w]+!</Rule>
    <Keywords color="Keywords">
      <Word>echo</Word><Word>set</Word><Word>if</Word><Word>else</Word><Word>for</Word><Word>in</Word>
      <Word>do</Word><Word>goto</Word><Word>call</Word><Word>exit</Word><Word>not</Word><Word>exist</Word>
      <Word>errorlevel</Word><Word>defined</Word><Word>setlocal</Word><Word>endlocal</Word><Word>pushd</Word>
      <Word>popd</Word><Word>shift</Word><Word>start</Word><Word>cd</Word><Word>equ</Word><Word>neq</Word>
      <Word>lss</Word><Word>leq</Word><Word>gtr</Word><Word>geq</Word><Word>nul</Word>
    </Keywords>
  </RuleSet>
</SyntaxDefinition>
```

`Pad/Highlighting/Log.xshd`:

```xml
<?xml version="1.0"?>
<!-- MicaPad's log highlighting: timestamps and levels. Colors are names only: MicaPad paints them from its palette. -->
<SyntaxDefinition name="MicaPad Log" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
  <Color name="LogTimestamp" />
  <Color name="LogError" />
  <Color name="LogWarning" />
  <Color name="LogInfo" />
  <Color name="LogDebug" />
  <RuleSet ignoreCase="true">
    <Rule color="LogTimestamp">\b\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}([.,]\d+)?(Z|[+-]\d{2}:?\d{2})?|\b\d{2}:\d{2}:\d{2}([.,]\d+)?\b</Rule>
    <Rule color="LogError">\b(ERROR|ERR|FATAL|CRITICAL|CRIT|FAIL|FAILED|EXCEPTION)\b</Rule>
    <Rule color="LogWarning">\b(WARNING|WARN)\b</Rule>
    <Rule color="LogInfo">\b(INFORMATION|INFO|NOTICE)\b</Rule>
    <Rule color="LogDebug">\b(DEBUG|TRACE|VERBOSE)\b</Rule>
  </RuleSet>
</SyntaxDefinition>
```

- [ ] **Step 4: Embed them**

In `Kil0bitSystemMonitor.csproj`, add a new `ItemGroup` after the one holding `InternalsVisibleTo`:

```xml
    <ItemGroup>
        <!-- MicaPad's own highlighting definitions, loaded by Pad/PadHighlighting.cs. -->
        <EmbeddedResource Include="Pad\Highlighting\*.xshd" LogicalName="MicaPad.Highlighting.%(Filename)%(Extension)" />
    </ItemGroup>
```

- [ ] **Step 5: Write `Pad/PadHighlighting.cs`**

```csharp
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// The highlighting definition of a language: AvalonEdit's built-in ones by name, MicaPad's own
    /// (INI, YAML, Batch, Log) from embedded .xshd files. Loaded once and shared; never modified —
    /// <see cref="ThemedHighlightingColorizer"/> repaints them at draw time.
    /// </summary>
    internal static class PadHighlighting
    {
        private static readonly ConcurrentDictionary<string, IHighlightingDefinition?> Cache = new();

        /// <summary>
        /// The definition for <paramref name="language"/>, or null when it has none (Plain text,
        /// Markdown) or it could not be loaded — then the note shows as plain text and the failure
        /// is logged once.
        /// </summary>
        public static IHighlightingDefinition? For(PadLanguage language)
        {
            if (language.Definition == null) return null;
            return Cache.GetOrAdd(language.Id, _ => Load(language));
        }

        private static IHighlightingDefinition? Load(PadLanguage language)
        {
            try
            {
                if (!language.OwnDefinition)
                    return HighlightingManager.Instance.GetDefinition(language.Definition!)
                           ?? throw new InvalidOperationException("AvalonEdit has no definition named " + language.Definition);

                string name = "MicaPad.Highlighting." + language.Definition + ".xshd";
                using Stream stream = typeof(PadHighlighting).Assembly.GetManifestResourceStream(name)
                                      ?? throw new InvalidOperationException("Missing resource " + name);
                using var reader = XmlReader.Create(stream);
                return HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
            catch (Exception ex) when (ex is HighlightingDefinitionInvalidException or XmlException or InvalidOperationException or IOException)
            {
                DiagnosticsLog.Warn("pad", "The " + language.Name + " colors could not be loaded, so it shows as plain text: " + ex.Message);
                return null;
            }
        }
    }
}
```

- [ ] **Step 6: Run the focused tests, then the whole suite** — expected PASS. If an own-definition row fails, fix the `.xshd` regex (not the test): each row is what a user sees in that file type.

- [ ] **Step 7: Commit**

```bash
git add Pad/Highlighting/Ini.xshd Pad/Highlighting/Yaml.xshd Pad/Highlighting/Batch.xshd Pad/Highlighting/Log.xshd Pad/PadHighlighting.cs Kil0bitSystemMonitor.csproj tests/Kil0bitSystemMonitor.Tests/PadHighlightingTests.cs
git commit -m "feat(pad): INI, YAML, batch and log colors, and one loader for every language" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

### Task 4: Syntax colors in the editor, and the language item in the status bar

**Files:**
- Create: `Pad/ThemedHighlightingColorizer.cs`
- Create: `Pad/EditorLanguage.cs`
- Modify: `Pad/MicaPadWindow.xaml` (status bar)
- Modify: `Pad/MicaPadWindow.xaml.cs` (fields, constructor, `ApplyTheme`, `ShowNote`, `EnsureDocument`, `OnConfigChanged`, `OnVersionSelected`, new language members)
- Modify: `GUIDE.md`, `README.md`
- Create: `tests/Kil0bitSystemMonitor.Tests/PadLanguageWindowTests.cs`

**Interfaces:**
- Consumes: `PadLanguages`, `ResolvedLanguage`, `PadWorkspace.SetLanguage`, `AppConfig.PadMarkdown` (Task 1); `SyntaxColors.Resolve` (Task 2); `PadHighlighting.For` (Task 3); Part 1's `_palette`, `NewMenu`, `Check`, `PadThemeApplier.ToBrush`.
- Produces:
  - `internal sealed class ThemedHighlightingColorizer : HighlightingColorizer` — ctor `(IHighlightingDefinition definition, Func<PadPalette> palette)`.
  - `internal sealed class EditorLanguage` — ctor `(TextEditor editor, Func<PadPalette> palette)`; `PadLanguage Current`; `void Apply(PadLanguage language)`; `void Redraw()`; `bool HasSyntaxColors`. Task 6 adds Markdown to it; Task 8 adds folding.
  - `MicaPadWindow`: `LanguageButton` (XAML), `internal ResolvedLanguage ShownLanguage`, `internal EditorLanguage LanguageView`, `internal void ChooseLanguage(OpenNote note, string? languageId)`, `internal ContextMenu BuildLanguageMenu(OpenNote note)`, private `ApplyLanguage()`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/PadLanguageWindowTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The MicaPad window shows each tab in its language and says which in the status bar.</summary>
    public class PadLanguageWindowTests
    {
        internal static void WithWindow(Action<MicaPadWindow, PadTestEnv, AppConfig> test) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var config = new AppConfig();
            var window = new MicaPadWindow(env.Workspace, config);
            try
            {
                window.LoadSession();
                test(window, env, config);
            }
            finally
            {
                window.CloseForExit();
            }
        });

        internal static void OpenFile(MicaPadWindow window, PadTestEnv env, string name, string text)
        {
            string path = env.FileOf(name);
            File.WriteAllText(path, text);
            window.OpenPath(path);
        }

        private static int SyntaxColorizers(MicaPadWindow window) =>
            window.Editor.TextArea.TextView.LineTransformers.OfType<ThemedHighlightingColorizer>().Count();

        [Fact]
        public void A_note_is_markdown_and_says_so() => WithWindow((window, env, config) =>
        {
            Assert.Equal("Markdown", window.LanguageButton.Content);
            Assert.Same(PadLanguages.Markdown, window.LanguageView.Current);
            Assert.False(window.LanguageView.HasSyntaxColors);
        });

        [Fact]
        public void A_json_file_gets_json_colors() => WithWindow((window, env, config) =>
        {
            OpenFile(window, env, "a.json", "{\n  \"a\": 1\n}");

            Assert.Equal("JSON", window.LanguageButton.Content);
            Assert.True(window.LanguageView.HasSyntaxColors);
            Assert.Equal(1, SyntaxColorizers(window));
        });

        [Fact]
        public void Choosing_a_language_repaints_and_is_remembered() => WithWindow((window, env, config) =>
        {
            OpenFile(window, env, "a.json", "{}");
            var note = env.Workspace.Active!;

            window.ChooseLanguage(note, "plain");
            Assert.Equal("Plain text", window.LanguageButton.Content);
            Assert.False(window.LanguageView.HasSyntaxColors);
            env.Flush();
            Assert.Equal("plain", env.Store.LoadMeta(note.Id)!.Language);

            window.ChooseLanguage(note, null);
            Assert.Equal("JSON", window.LanguageButton.Content);
            Assert.Equal(1, SyntaxColorizers(window));
        });

        [Fact]
        public void Switching_tabs_switches_the_language_without_piling_up_colorizers() => WithWindow((window, env, config) =>
        {
            OpenFile(window, env, "a.json", "{}");
            int json = env.Workspace.Open.Count - 1;
            window.NewTab();
            Assert.Equal("Markdown", window.LanguageButton.Content);

            window.SelectTab(json);
            window.SelectTab(json + 1);
            window.SelectTab(json);

            Assert.Equal("JSON", window.LanguageButton.Content);
            Assert.Equal(1, SyntaxColorizers(window));
        });

        [Fact]
        public void Turning_markdown_off_shows_notes_as_plain_text() => WithWindow((window, env, config) =>
        {
            config.PadMarkdown = false;
            Assert.Equal("Plain text", window.LanguageButton.Content);

            config.PadMarkdown = true;
            Assert.Equal("Markdown", window.LanguageButton.Content);
        });

        [Fact]
        public void Crossing_the_size_limit_turns_formatting_off_and_on() => WithWindow((window, env, config) =>
        {
            OpenFile(window, env, "a.json", "{}");
            var document = window.Editor.Document;

            document.Insert(0, new string('x', PadLanguages.MaxFormattedChars));
            Assert.Equal("Plain text (large)", window.LanguageButton.Content);
            Assert.False(window.LanguageView.HasSyntaxColors);

            document.Remove(0, PadLanguages.MaxFormattedChars);
            Assert.Equal("JSON", window.LanguageButton.Content);
            Assert.True(window.LanguageView.HasSyntaxColors);
        });

        [Fact]
        public void The_language_menu_lists_auto_then_every_language() => WithWindow((window, env, config) =>
        {
            var menu = window.BuildLanguageMenu(env.Workspace.Active!);
            var headers = menu.Items.Cast<object>().Select(i => i is MenuItem m ? (string)m.Header : "-").ToList();

            Assert.Equal("Auto (by file type)", headers[0]);
            Assert.Equal("-", headers[1]);
            Assert.Equal(PadLanguages.All.Select(l => l.Name), headers.Skip(2));
            Assert.True(((MenuItem)menu.Items[0]).IsChecked);
        });

        [Fact]
        public void A_theme_switch_keeps_the_colors() => WithWindow((window, env, config) =>
        {
            OpenFile(window, env, "a.log", "2026-09-30 10:00:00 ERROR boom");

            window.ToggleTheme();

            Assert.Equal("Log", window.LanguageButton.Content);
            Assert.True(window.LanguageView.HasSyntaxColors);
        });
    }
}
```

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~PadLanguageWindowTests"`). Expected: build FAILS (`LanguageButton`, `LanguageView`, `ChooseLanguage`, `BuildLanguageMenu`, `ThemedHighlightingColorizer` not found).

- [ ] **Step 3: Write `Pad/ThemedHighlightingColorizer.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// AvalonEdit's highlighting, painted from the MicaPad palette instead of the definition's own
    /// colors (chosen for white pages). A named color takes its category's palette color; an
    /// unnamed one keeps its hue, nudged until it reads at 4.5:1 (<see cref="SyntaxColors.Resolve"/>).
    /// Bold and italic are kept; definition backgrounds are dropped. The shared definitions are
    /// never modified: the palette is read at draw time, so a theme switch only needs a redraw.
    /// </summary>
    internal sealed class ThemedHighlightingColorizer : HighlightingColorizer
    {
        private readonly Func<PadPalette> _palette;
        private readonly Dictionary<PadColor, Brush> _brushes = new();

        public ThemedHighlightingColorizer(IHighlightingDefinition definition, Func<PadPalette> palette)
            : base(definition)
        {
            _palette = palette;
        }

        protected override void ApplyColorToElement(VisualLineElement element, HighlightingColor color)
        {
            var properties = element.TextRunProperties;

            PadColor? original = color.Foreground?.GetColor(CurrentContext) is Color c ? new PadColor(c.A, c.R, c.G, c.B) : null;
            if (SyntaxColors.Resolve(color.Name, original, _palette()) is PadColor paint)
                properties.SetForegroundBrush(BrushFor(paint));

            if (color.FontWeight != null || color.FontStyle != null)
            {
                var face = properties.Typeface;
                properties.SetTypeface(new Typeface(face.FontFamily, color.FontStyle ?? face.Style, color.FontWeight ?? face.Weight, face.Stretch));
            }
            if (color.Underline == true) properties.SetTextDecorations(TextDecorations.Underline);
            if (color.Strikethrough == true) properties.SetTextDecorations(TextDecorations.Strikethrough);
        }

        private Brush BrushFor(PadColor color)
        {
            if (!_brushes.TryGetValue(color, out var brush)) _brushes[color] = brush = PadThemeApplier.ToBrush(color);
            return brush;
        }
    }
}
```

- [ ] **Step 4: Write `Pad/EditorLanguage.cs`**

```csharp
using System;
using ICSharpCode.AvalonEdit;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Everything a language adds to one editor, installed and removed together: syntax colors now,
    /// Markdown formatting (Task 6) and folding (Task 8) later. <see cref="Apply"/> always removes
    /// the previous language first, so switching tabs never piles colorizers up.
    /// </summary>
    internal sealed class EditorLanguage
    {
        private readonly TextEditor _editor;
        private readonly Func<PadPalette> _palette;
        private ThemedHighlightingColorizer? _syntax;

        public EditorLanguage(TextEditor editor, Func<PadPalette> palette)
        {
            _editor = editor;
            _palette = palette;
        }

        /// <summary>The language applied now.</summary>
        public PadLanguage Current { get; private set; } = PadLanguages.Plain;

        /// <summary>True while syntax colors are installed.</summary>
        internal bool HasSyntaxColors => _syntax != null;

        /// <summary>Shows the editor's text in <paramref name="language"/>.</summary>
        public void Apply(PadLanguage language)
        {
            Clear();
            Current = language;
            if (PadHighlighting.For(language) is { } definition)
            {
                _syntax = new ThemedHighlightingColorizer(definition, _palette);
                _editor.TextArea.TextView.LineTransformers.Add(_syntax);
            }
            Redraw();
        }

        /// <summary>Repaints; the colorizers read the palette as they draw, so a theme switch needs only this.</summary>
        public void Redraw() => _editor.TextArea.TextView.Redraw();

        private void Clear()
        {
            if (_syntax != null)
            {
                _editor.TextArea.TextView.LineTransformers.Remove(_syntax);
                _syntax = null;
            }
        }
    }
}
```

- [ ] **Step 5: Add the language item to the status bar**

In `Pad/MicaPadWindow.xaml`, in the status-bar `DockPanel`, insert between the `SaveText` TextBlock and the `EolButton`:

```xml
                <Button x:Name="LanguageButton" DockPanel.Dock="Right" Style="{StaticResource FlatButton}" FontSize="11.5"
                        ToolTip="Language of this tab: colors and formatting" Click="OnLanguageClick" />
```

- [ ] **Step 6: Wire it in `Pad/MicaPadWindow.xaml.cs`**

1. Fields, after `private PadPalette _palette = PadPalette.Dark;`:

```csharp
        private EditorLanguage _language = null!;
        private EditorLanguage _previewLanguage = null!;
        private ResolvedLanguage _resolved = new(PadLanguages.Plain, false);
```

2. In the constructor, immediately before `ApplyTheme();`:

```csharp
            _language = new EditorLanguage(Editor, () => _palette);
            _previewLanguage = new EditorLanguage(PreviewEditor, () => _palette);
```

3. At the end of `ApplyTheme()`:

```csharp
            _language.Redraw();
            _previewLanguage.Redraw();
```

4. In `ShowNote`, directly after `Editor.Document = EnsureDocument(note);` add `ApplyLanguage();` — before anything moves the caret, so a language's folding (Task 8) never sees the new document while still bound to the old one.

5. In `EnsureDocument`, inside the `document.Changed` handler, after the `UpdateCharsText()` line, add:

```csharp
                // A big paste crosses the 2 MB limit: formatting switches off (and back on) at once.
                if (ReferenceEquals(_shown, note) && (document.TextLength > PadLanguages.MaxFormattedChars) != _resolved.TooLarge)
                    ApplyLanguage();
```

6. In `OnConfigChanged`, add a branch:

```csharp
            else if (e.PropertyName == nameof(AppConfig.PadMarkdown))
            {
                if (Dispatcher.CheckAccess()) ApplyLanguage();
                else Dispatcher.BeginInvoke(new Action(ApplyLanguage));
            }
```

7. In `OnVersionSelected`, after `PreviewEditor.Text = text;`:

```csharp
            _previewLanguage.Apply(PadLanguages.Resolve(_shown?.Meta.Language, _shown?.Meta.SourcePath, _config.PadMarkdown, text.Length).Effective);
```

8. Add a region after the status-bar helpers:

```csharp
        // ---- language ------------------------------------------------------------------------

        /// <summary>The shown tab's language, after Auto and the size limit.</summary>
        internal ResolvedLanguage ShownLanguage => _resolved;

        /// <summary>What the shown tab's language installed in the editor.</summary>
        internal EditorLanguage LanguageView => _language;

        /// <summary>Shows the current tab in its language (spec 2.1) and names it in the status bar.</summary>
        private void ApplyLanguage()
        {
            if (_shown == null) return;
            _resolved = PadLanguages.Resolve(_shown.Meta.Language, _shown.Meta.SourcePath, _config.PadMarkdown, Editor.Document.TextLength);
            LanguageButton.Content = _resolved.DisplayName;
            _language.Apply(_resolved.Effective);
        }

        private void OnLanguageClick(object sender, RoutedEventArgs e)
        {
            if (_shown == null) return;
            BuildLanguageMenu(_shown).IsOpen = true;
        }

        /// <summary>The status-bar language menu: Auto, then every language, the tab's choice checked.</summary>
        internal ContextMenu BuildLanguageMenu(OpenNote note)
        {
            var menu = NewMenu(LanguageButton, PlacementMode.Top);
            menu.Items.Add(Check("Auto (by file type)", null, note.Meta.Language == null, () => ChooseLanguage(note, null)));
            menu.Items.Add(new Separator());
            foreach (var language in PadLanguages.All)
            {
                string id = language.Id;
                menu.Items.Add(Check(language.Name, null, note.Meta.Language == id, () => ChooseLanguage(note, id)));
            }
            return menu;
        }

        /// <summary>Sets a tab's language (null for Auto), saves it with the note and repaints.</summary>
        internal void ChooseLanguage(OpenNote note, string? languageId)
        {
            _workspace.SetLanguage(note, languageId);
            if (ReferenceEquals(note, _shown)) ApplyLanguage();
        }
```

- [ ] **Step 7: Run the new tests, then the whole suite** — expected PASS (existing `PadWindowTests`, `PadThemeTests` and `PadMenuTests` unchanged and green).

- [ ] **Step 8: Document it**

`GUIDE.md`, `## 📝 MicaPad`, insert before `### Where notes live`:

```markdown
### Colors for code, logs and settings files

JSON, XML, HTML, C#, JavaScript, CSS, PowerShell, Python, SQL, C/C++, Java, PHP, VB, diff, INI,
YAML, batch and log files open colored, in both themes. The language shows in the status bar;
click it to pick another for that tab (or **Auto** to go back to the file type). Notes and
`.md`/`.txt` files are Markdown. Text over 2 MB is shown plain, so huge files stay fast.

```

`README.md` English MicaPad list, after the *Right-click menus* bullet:

```markdown
* **Colors for code and logs**: JSON, XML, C#, PowerShell, Python, SQL, INI, YAML, batch and log files are colored in both themes; the status bar shows the language and changes it per tab
```

Thai list, after the *เมนูคลิกขวา* bullet:

```markdown
* **สีสำหรับโค้ดและล็อก**: ไฟล์ JSON, XML, C#, PowerShell, Python, SQL, INI, YAML, batch และไฟล์ล็อก แสดงสีได้ทั้งธีมสว่างและมืด ภาษาแสดงที่แถบสถานะและเปลี่ยนได้ทีละแท็บ
```

- [ ] **Step 9: Commit**

```bash
git add Pad/ThemedHighlightingColorizer.cs Pad/EditorLanguage.cs Pad/MicaPadWindow.xaml Pad/MicaPadWindow.xaml.cs GUIDE.md README.md tests/Kil0bitSystemMonitor.Tests/PadLanguageWindowTests.cs
git commit -m "feat(pad): syntax colors in both themes, and a language item in the status bar" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

### Task 5: The Markdown line model (fences, tokens, looks)

**Files:**
- Create: `Services/Pad/FenceTracker.cs`
- Create: `Services/Pad/MarkdownLineTokenizer.cs`
- Create: `Services/Pad/MarkdownStyles.cs`
- Create: `tests/Kil0bitSystemMonitor.Tests/MarkdownTokenizerTests.cs`

**Interfaces:**
- Consumes: `PadPalette` Markdown colors (Task 2).
- Produces:
  - `public enum MdStyle { Marker, Heading1, Heading2, Heading3, Heading4, Heading5, Heading6, Bold, Italic, BoldItalic, Strike, Code, LinkText, ListMarker, TaskDone, QuoteText, CodeBlock }`
  - `public enum MdBlock { Paragraph, Heading, Quote, Fence, Rule, Bullet, Numbered, Task }`
  - `public enum MdFence { None, Delimiter, Inside }`
  - `public readonly record struct MdSpan(int Start, int Length, MdStyle Style)`
  - `public sealed record MdLine(MdBlock Block, IReadOnlyList<MdSpan> Spans)`
  - `MarkdownLineTokenizer.Tokenize(string line, MdFence fence)`, `.BlockOf(string line, MdFence fence)`, `.BulletOffset(string line)`, `const int MaxInlineLength = 4000`
  - `FenceTracker.Classify(IReadOnlyList<string> lines)` → `MdFence[]`
  - `public enum MdWeight { Keep, SemiBold, Bold }`, `public readonly record struct MdLook(PadColor? Foreground, PadColor? Background, double SizeFactor, MdWeight Weight, bool Italic, bool Strike)`, `MarkdownStyles.LookOf(MdStyle, PadPalette)`, `MarkdownStyles.HeadingSizes`
- Span order is application order: a line's block style comes first, inline styles next, markers last (so a marker inside a heading is dimmed but keeps the heading size).

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/MarkdownTokenizerTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The Markdown line model: which runs of a line get which style, and which lines are fenced.</summary>
    public class MarkdownTokenizerTests
    {
        private static List<(string Text, MdStyle Style)> Runs(string line, MdFence fence = MdFence.None) =>
            MarkdownLineTokenizer.Tokenize(line, fence).Spans.Select(s => (line.Substring(s.Start, s.Length), s.Style)).ToList();

        private static List<string> TextsOf(string line, MdStyle style) =>
            Runs(line).Where(r => r.Style == style).Select(r => r.Text).ToList();

        [Theory]
        [InlineData("# Title", MdStyle.Heading1)]
        [InlineData("## Two", MdStyle.Heading2)]
        [InlineData("   ###### Six", MdStyle.Heading6)]
        [InlineData("#", MdStyle.Heading1)]
        public void Headings_style_the_whole_line_and_dim_the_hashes(string line, MdStyle expected)
        {
            var result = MarkdownLineTokenizer.Tokenize(line, MdFence.None);
            Assert.Equal(MdBlock.Heading, result.Block);
            Assert.Equal((line, expected), Runs(line)[0]);
            Assert.Contains(Runs(line), r => r.Style == MdStyle.Marker && r.Text.Trim('#').Length == 0);
        }

        [Theory]
        [InlineData("####### seven")]
        [InlineData("#nospace")]
        [InlineData("    # indented four")]
        public void Not_every_hash_is_a_heading(string line)
        {
            Assert.NotEqual(MdBlock.Heading, MarkdownLineTokenizer.Tokenize(line, MdFence.None).Block);
        }

        [Theory]
        [InlineData("a **bold** b", "bold", MdStyle.Bold)]
        [InlineData("__bold__", "bold", MdStyle.Bold)]
        [InlineData("an *it* here", "it", MdStyle.Italic)]
        [InlineData("_it_", "it", MdStyle.Italic)]
        [InlineData("***both***", "both", MdStyle.BoldItalic)]
        [InlineData("~~gone~~", "gone", MdStyle.Strike)]
        [InlineData("use `co*de*` here", "co*de*", MdStyle.Code)]
        [InlineData("``a`b``", "a`b", MdStyle.Code)]
        [InlineData("**สวัสดี** ครับ", "สวัสดี", MdStyle.Bold)]
        public void Inline_styles_cover_the_text_between_markers(string line, string text, MdStyle style)
        {
            Assert.Contains((text, style), Runs(line));
        }

        [Fact]
        public void Markers_are_dimmed_not_hidden()
        {
            var runs = Runs("a **bold** b");
            Assert.Equal(2, runs.Count(r => r.Text == "**" && r.Style == MdStyle.Marker));
        }

        [Theory]
        [InlineData("snake_case_name")]
        [InlineData("a * b * c")]
        [InlineData("**unclosed")]
        [InlineData("~one~")]
        [InlineData(@"\*not italic\*")]
        [InlineData("2 * 3 * 4")]
        public void Some_marker_characters_are_just_text(string line)
        {
            Assert.DoesNotContain(Runs(line), r => r.Style is MdStyle.Bold or MdStyle.Italic or MdStyle.BoldItalic or MdStyle.Strike);
        }

        [Fact]
        public void Code_hides_emphasis_inside_it()
        {
            Assert.Empty(TextsOf("use `co*de*` here", MdStyle.Italic));
        }

        [Fact]
        public void Emphasis_nests()
        {
            Assert.Equal(new[] { "bold _it_ bold" }, TextsOf("**bold _it_ bold**", MdStyle.Bold));
            Assert.Equal(new[] { "it" }, TextsOf("**bold _it_ bold**", MdStyle.Italic));

            Assert.Equal(new[] { "a **b** c" }, TextsOf("*a **b** c*", MdStyle.Italic));
            Assert.Equal(new[] { "b" }, TextsOf("*a **b** c*", MdStyle.Bold));

            Assert.Equal(new[] { "bold *italic*" }, TextsOf("**bold *italic***", MdStyle.Bold));
            Assert.Equal(new[] { "italic" }, TextsOf("**bold *italic***", MdStyle.Italic));
        }

        [Fact]
        public void Links_color_the_text_and_dim_the_address()
        {
            var runs = Runs("see [docs](https://x.y/a_b*c) now");
            Assert.Contains(("[", MdStyle.Marker), runs);
            Assert.Contains(("docs", MdStyle.LinkText), runs);
            Assert.Contains(("](https://x.y/a_b*c)", MdStyle.Marker), runs);
            Assert.DoesNotContain(runs, r => r.Style is MdStyle.Italic or MdStyle.Bold);
        }

        [Fact]
        public void Bullets_numbers_and_tasks()
        {
            var bullet = MarkdownLineTokenizer.Tokenize("  - item", MdFence.None);
            Assert.Equal(MdBlock.Bullet, bullet.Block);
            Assert.Contains(("-", MdStyle.ListMarker), Runs("  - item"));
            Assert.Equal(2, MarkdownLineTokenizer.BulletOffset("  - item"));
            Assert.Equal(0, MarkdownLineTokenizer.BulletOffset("* item"));

            Assert.Equal(MdBlock.Numbered, MarkdownLineTokenizer.Tokenize("12. twelfth", MdFence.None).Block);
            Assert.Contains(("12.", MdStyle.ListMarker), Runs("12. twelfth"));
            Assert.Equal(-1, MarkdownLineTokenizer.BulletOffset("12. twelfth"));

            Assert.Equal(MdBlock.Task, MarkdownLineTokenizer.Tokenize("- [ ] todo", MdFence.None).Block);
            Assert.Contains(("[ ]", MdStyle.Marker), Runs("- [ ] todo"));
            Assert.DoesNotContain(Runs("- [ ] todo"), r => r.Style == MdStyle.TaskDone);
            Assert.Contains(("done", MdStyle.TaskDone), Runs("- [x] done"));
            Assert.Equal(0, MarkdownLineTokenizer.BulletOffset("- [x] done"));
        }

        [Fact]
        public void Quotes_mute_the_text_and_keep_inline_styles()
        {
            var runs = Runs("> quote **b**");
            Assert.Equal(MdBlock.Quote, MarkdownLineTokenizer.Tokenize("> quote **b**", MdFence.None).Block);
            Assert.Contains((">", MdStyle.Marker), runs);
            Assert.Contains(("quote **b**", MdStyle.QuoteText), runs);
            Assert.Contains(("b", MdStyle.Bold), runs);
        }

        [Theory]
        [InlineData("---")]
        [InlineData("* * *")]
        [InlineData("___")]
        [InlineData("  - - -  ")]
        public void Rules_are_not_bullets(string line)
        {
            Assert.Equal(MdBlock.Rule, MarkdownLineTokenizer.Tokenize(line, MdFence.None).Block);
            Assert.Equal(-1, MarkdownLineTokenizer.BulletOffset(line));
        }

        [Fact]
        public void Fenced_lines_are_code_and_nothing_else()
        {
            Assert.Equal(new[] { ("# not a heading **x**", MdStyle.CodeBlock) }, Runs("# not a heading **x**", MdFence.Inside));
            Assert.Equal(new[] { ("```cs", MdStyle.Marker) }, Runs("```cs", MdFence.Delimiter));
            Assert.Equal(MdBlock.Fence, MarkdownLineTokenizer.BlockOf("anything", MdFence.Inside));
        }

        [Fact]
        public void A_very_long_line_gets_no_inline_formatting()
        {
            string line = "**x** " + new string('a', MarkdownLineTokenizer.MaxInlineLength);
            Assert.Empty(TextsOf(line, MdStyle.Bold));

            string heading = "# " + new string('a', MarkdownLineTokenizer.MaxInlineLength);
            Assert.Equal(MdBlock.Heading, MarkdownLineTokenizer.Tokenize(heading, MdFence.None).Block);
        }

        [Fact]
        public void Empty_and_plain_lines_have_no_spans()
        {
            Assert.Empty(MarkdownLineTokenizer.Tokenize("", MdFence.None).Spans);
            Assert.Empty(MarkdownLineTokenizer.Tokenize("just words", MdFence.None).Spans);
        }

        [Fact]
        public void Fences_open_close_and_contain()
        {
            var kinds = FenceTracker.Classify(new[] { "text", "```cs", "code", "```", "after" });
            Assert.Equal(new[] { MdFence.None, MdFence.Delimiter, MdFence.Inside, MdFence.Delimiter, MdFence.None }, kinds);
        }

        [Fact]
        public void A_fence_closes_only_with_its_own_kind_and_length()
        {
            Assert.Equal(new[] { MdFence.Delimiter, MdFence.Inside, MdFence.Inside, MdFence.Delimiter },
                         FenceTracker.Classify(new[] { "````", "```", "~~~~", "`````" }));
            Assert.Equal(new[] { MdFence.Delimiter, MdFence.Inside, MdFence.Delimiter },
                         FenceTracker.Classify(new[] { "~~~", "x", "~~~~" }));
        }

        [Fact]
        public void An_unclosed_fence_runs_to_the_end()
        {
            Assert.Equal(new[] { MdFence.None, MdFence.Delimiter, MdFence.Inside, MdFence.Inside },
                         FenceTracker.Classify(new[] { "a", "```", "b", "c" }));
        }

        [Theory]
        [InlineData("```a`b")]
        [InlineData("    ```")]
        [InlineData("``")]
        public void Not_every_backtick_line_opens_a_fence(string line)
        {
            Assert.Equal(new[] { MdFence.None, MdFence.None }, FenceTracker.Classify(new[] { line, "x" }));
        }

        [Fact]
        public void Heading_sizes_and_looks_follow_the_spec()
        {
            Assert.Equal(new[] { 1.6, 1.4, 1.25, 1.15, 1.05, 1.0 }, MarkdownStyles.HeadingSizes);

            var dark = PadPalette.Dark;
            var h2 = MarkdownStyles.LookOf(MdStyle.Heading2, dark);
            Assert.Equal(1.4, h2.SizeFactor);
            Assert.Equal(MdWeight.SemiBold, h2.Weight);
            Assert.Equal(dark.MdHeading, h2.Foreground);

            Assert.Equal(dark.MdMarker, MarkdownStyles.LookOf(MdStyle.Marker, dark).Foreground);
            Assert.Equal(dark.MdCodeBackground, MarkdownStyles.LookOf(MdStyle.Code, dark).Background);
            Assert.True(MarkdownStyles.LookOf(MdStyle.TaskDone, dark).Strike);
            Assert.True(MarkdownStyles.LookOf(MdStyle.BoldItalic, dark).Italic);
            Assert.Equal(MdWeight.Bold, MarkdownStyles.LookOf(MdStyle.BoldItalic, dark).Weight);
            Assert.Equal(PadPalette.Light.MdLink, MarkdownStyles.LookOf(MdStyle.LinkText, PadPalette.Light).Foreground);
        }
    }
}
```

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~MarkdownTokenizerTests"`). Expected: build FAILS.

- [ ] **Step 3: Write `Services/Pad/MarkdownLineTokenizer.cs`**

```csharp
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>How one run of a Markdown line is shown (spec 2.3). Markers stay visible, dimmed.</summary>
    public enum MdStyle
    {
        Marker,
        Heading1,
        Heading2,
        Heading3,
        Heading4,
        Heading5,
        Heading6,
        Bold,
        Italic,
        BoldItalic,
        Strike,
        Code,
        LinkText,
        ListMarker,
        TaskDone,
        QuoteText,
        CodeBlock,
    }

    /// <summary>What kind of line it is; the background renderer draws quote bars, code shading and rules from it.</summary>
    public enum MdBlock
    {
        Paragraph,
        Heading,
        Quote,
        Fence,
        Rule,
        Bullet,
        Numbered,
        Task,
    }

    /// <summary>Whether a line belongs to a fenced code block.</summary>
    public enum MdFence
    {
        None,
        Delimiter,
        Inside,
    }

    /// <summary>A run of a line: characters <see cref="Start"/> to <see cref="Start"/> + <see cref="Length"/>.</summary>
    public readonly record struct MdSpan(int Start, int Length, MdStyle Style);

    /// <summary>
    /// One tokenized line. <see cref="Spans"/> are in application order — block style, then inline
    /// styles, then markers — and may overlap (bold inside a heading).
    /// </summary>
    public sealed record MdLine(MdBlock Block, IReadOnlyList<MdSpan> Spans);

    /// <summary>
    /// Markdown styled source, one line at a time: headings, emphasis, code, links, lists, tasks,
    /// quotes, rules. Deliberately smaller than CommonMark — unclosed or ambiguous markers stay
    /// plain text — and never changes the text, only says how to show it.
    /// </summary>
    public static class MarkdownLineTokenizer
    {
        /// <summary>
        /// Lines longer than this keep their block style but get no inline formatting, so a
        /// pathological line (thousands of unmatched markers) cannot stall typing.
        /// </summary>
        public const int MaxInlineLength = 4000;

        private static readonly Regex HeadingRx = new(@"^ {0,3}(#{1,6})(?:[ \t]+|$)", RegexOptions.CultureInvariant);
        private static readonly Regex RuleRx = new(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$", RegexOptions.CultureInvariant);
        private static readonly Regex TaskRx = new(@"^[ \t]*([-*+])[ \t]+(\[[ xX]\])(?=[ \t]|$)[ \t]*", RegexOptions.CultureInvariant);
        private static readonly Regex BulletRx = new(@"^[ \t]*([-*+])[ \t]+", RegexOptions.CultureInvariant);
        private static readonly Regex NumberedRx = new(@"^[ \t]*(\d{1,9}[.)])[ \t]+", RegexOptions.CultureInvariant);
        private static readonly Regex QuoteRx = new(@"^ {0,3}(>)[ \t]?", RegexOptions.CultureInvariant);

        /// <summary>The line's styled runs.</summary>
        public static MdLine Tokenize(string line, MdFence fence)
        {
            line ??= "";
            var spans = new List<MdSpan>();
            MdBlock block = Classify(line, fence, out Match? m);
            switch (block)
            {
                case MdBlock.Fence:
                    Add(spans, 0, line.Length, fence == MdFence.Delimiter ? MdStyle.Marker : MdStyle.CodeBlock);
                    break;
                case MdBlock.Rule:
                    Add(spans, 0, line.Length, MdStyle.Marker);
                    break;
                case MdBlock.Heading:
                    Add(spans, 0, line.Length, MdStyle.Heading1 + (m!.Groups[1].Length - 1));
                    Inline(line, m.Length, line.Length, spans);
                    Add(spans, m.Groups[1].Index, m.Groups[1].Length, MdStyle.Marker);
                    break;
                case MdBlock.Quote:
                    Add(spans, m!.Length, line.Length - m.Length, MdStyle.QuoteText);
                    Inline(line, m.Length, line.Length, spans);
                    Add(spans, m.Groups[1].Index, 1, MdStyle.Marker);
                    break;
                case MdBlock.Task:
                    Add(spans, m!.Groups[1].Index, 1, MdStyle.ListMarker);
                    if (m.Groups[2].Value[1] != ' ') Add(spans, m.Length, line.Length - m.Length, MdStyle.TaskDone);
                    Inline(line, m.Length, line.Length, spans);
                    Add(spans, m.Groups[2].Index, 3, MdStyle.Marker);
                    break;
                case MdBlock.Bullet:
                case MdBlock.Numbered:
                    Add(spans, m!.Groups[1].Index, m.Groups[1].Length, MdStyle.ListMarker);
                    Inline(line, m.Length, line.Length, spans);
                    break;
                default:
                    Inline(line, 0, line.Length, spans);
                    break;
            }
            return new MdLine(block, spans);
        }

        /// <summary>The kind of a line without its inline runs; cheap enough to call per visible line.</summary>
        public static MdBlock BlockOf(string line, MdFence fence) => Classify(line ?? "", fence, out _);

        /// <summary>Where the list marker (<c>-</c>, <c>*</c>, <c>+</c>) of a bullet or task line is, or -1.</summary>
        public static int BulletOffset(string line)
        {
            MdBlock block = Classify(line ?? "", MdFence.None, out Match? m);
            return block is MdBlock.Bullet or MdBlock.Task ? m!.Groups[1].Index : -1;
        }

        private static MdBlock Classify(string line, MdFence fence, out Match? match)
        {
            match = null;
            if (fence != MdFence.None) return MdBlock.Fence;
            if (RuleRx.IsMatch(line)) return MdBlock.Rule;
            if ((match = HeadingRx.Match(line)).Success) return MdBlock.Heading;
            if ((match = QuoteRx.Match(line)).Success) return MdBlock.Quote;
            if ((match = TaskRx.Match(line)).Success) return MdBlock.Task;
            if ((match = BulletRx.Match(line)).Success) return MdBlock.Bullet;
            if ((match = NumberedRx.Match(line)).Success) return MdBlock.Numbered;
            match = null;
            return MdBlock.Paragraph;
        }

        private static void Add(List<MdSpan> spans, int start, int length, MdStyle style)
        {
            if (length > 0) spans.Add(new MdSpan(start, length, style));
        }

        /// <summary>Code spans, links and emphasis between <paramref name="start"/> and <paramref name="end"/>.</summary>
        private static void Inline(string s, int start, int end, List<MdSpan> spans)
        {
            if (s.Length > MaxInlineLength) return;
            int i = start;
            while (i < end)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < end && IsEscapable(s[i + 1])) { i += 2; continue; }
                if (c == '`') { i = CodeSpan(s, i, end, spans); continue; }
                if (c == '[' && TryLink(s, i, end, spans, out int afterLink)) { i = afterLink; continue; }
                if (c is '*' or '_' or '~') { i = Emphasis(s, i, end, spans); continue; }
                i++;
            }
        }

        private static bool IsEscapable(char c) => "\\`*_{}[]()#+-.!~>|".IndexOf(c) >= 0;

        private static int Run(string s, int i, int end, char c)
        {
            int j = i;
            while (j < end && s[j] == c) j++;
            return j - i;
        }

        /// <summary>A run of backticks closed by a run of the same length; unclosed backticks are text.</summary>
        private static int CodeSpan(string s, int i, int end, List<MdSpan> spans)
        {
            int n = Run(s, i, end, '`');
            int close = FindCodeClose(s, i + n, end, n);
            if (close < 0) return i + n;
            Add(spans, i, n, MdStyle.Marker);
            Add(spans, i + n, close - i - n, MdStyle.Code);
            Add(spans, close, n, MdStyle.Marker);
            return close + n;
        }

        /// <summary>The start of the next run of exactly <paramref name="n"/> backticks, or -1.</summary>
        private static int FindCodeClose(string s, int from, int end, int n)
        {
            int j = from;
            while (j < end)
            {
                if (s[j] != '`') { j++; continue; }
                int m = Run(s, j, end, '`');
                if (m == n) return j;
                j += m;
            }
            return -1;
        }

        /// <summary><c>[text](address)</c>: the text in the link color, the brackets and address dimmed.</summary>
        private static bool TryLink(string s, int i, int end, List<MdSpan> spans, out int after)
        {
            after = i;
            int close = s.IndexOf(']', i + 1, end - i - 1);
            if (close < 0 || close + 1 >= end || s[close + 1] != '(') return false;
            int paren = s.IndexOf(')', close + 2, end - close - 2);
            if (paren < 0) return false;

            Add(spans, i, 1, MdStyle.Marker);
            Add(spans, i + 1, close - i - 1, MdStyle.LinkText);
            Inline(s, i + 1, close, spans);
            Add(spans, close, paren - close + 1, MdStyle.Marker);
            after = paren + 1;
            return true;
        }

        /// <summary>
        /// <c>*</c>/<c>_</c> runs of one to three (italic, bold, both) and <c>~~</c> (strike). A
        /// closing run of the same length wins; a longer closing run is used only when no exact one
        /// follows (<c>**bold *italic***</c>). Returns where scanning continues.
        /// </summary>
        private static int Emphasis(string s, int i, int end, List<MdSpan> spans)
        {
            char c = s[i];
            int n = Run(s, i, end, c);
            if ((c == '~' && n != 2) || n > 3 || !CanOpen(s, i, n, end, c)) return i + n;

            int fallback = -1;
            int fallbackRun = 0;
            int j = i + n;
            while (j < end)
            {
                char d = s[j];
                if (d == '\\' && j + 1 < end) { j += 2; continue; }
                if (d == '`')
                {
                    int ticks = Run(s, j, end, '`');
                    int codeClose = FindCodeClose(s, j + ticks, end, ticks);
                    j = codeClose < 0 ? j + ticks : codeClose + ticks;
                    continue;
                }
                if (d != c) { j++; continue; }

                int m = Run(s, j, end, c);
                if (CanClose(s, j, m, end, c))
                {
                    if (m == n) return Close(s, i, n, j, c, spans);
                    if (m > n && fallback < 0)
                    {
                        fallback = j;
                        fallbackRun = m;
                    }
                }
                j += m;
            }
            return fallback >= 0 ? Close(s, i, n, fallback + fallbackRun - n, c, spans) : i + n;
        }

        private static int Close(string s, int open, int n, int close, char c, List<MdSpan> spans)
        {
            Add(spans, open, n, MdStyle.Marker);
            Add(spans, open + n, close - open - n, StyleOf(c, n));
            Inline(s, open + n, close, spans);
            Add(spans, close, n, MdStyle.Marker);
            return close + n;
        }

        private static MdStyle StyleOf(char c, int n) =>
            c == '~' ? MdStyle.Strike : n switch { 1 => MdStyle.Italic, 2 => MdStyle.Bold, _ => MdStyle.BoldItalic };

        /// <summary>An opening run is followed by text; <c>_</c> also needs a word boundary before it.</summary>
        private static bool CanOpen(string s, int i, int n, int end, char c)
        {
            int after = i + n;
            if (after >= end || char.IsWhiteSpace(s[after])) return false;
            return c != '_' || i == 0 || !char.IsLetterOrDigit(s[i - 1]);
        }

        /// <summary>A closing run follows text; <c>_</c> also needs a word boundary after it.</summary>
        private static bool CanClose(string s, int j, int m, int end, char c)
        {
            if (j == 0 || char.IsWhiteSpace(s[j - 1])) return false;
            int after = j + m;
            return c != '_' || after >= end || !char.IsLetterOrDigit(s[after]);
        }
    }
}
```

- [ ] **Step 4: Write `Services/Pad/FenceTracker.cs`**

```csharp
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Which lines are fenced code: a line of three or more backticks or tildes (indented at most
    /// three spaces) opens a fence; a line of at least as many of the same character, and nothing
    /// else, closes it. An unclosed fence runs to the end.
    /// </summary>
    public static class FenceTracker
    {
        private static readonly Regex OpenRx = new(@"^ {0,3}(`{3,}|~{3,})(.*)$", RegexOptions.CultureInvariant);

        /// <summary>One entry per line: delimiter, inside, or neither.</summary>
        public static MdFence[] Classify(IReadOnlyList<string> lines)
        {
            var kinds = new MdFence[lines.Count];
            char fenceChar = '\0';
            int fenceLength = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i] ?? "";
                if (fenceLength == 0)
                {
                    Match m = OpenRx.Match(line);
                    // A backtick fence's info string cannot contain a backtick (that is inline code).
                    if (m.Success && !(m.Groups[1].Value[0] == '`' && m.Groups[2].Value.Contains('`')))
                    {
                        fenceChar = m.Groups[1].Value[0];
                        fenceLength = m.Groups[1].Length;
                        kinds[i] = MdFence.Delimiter;
                    }
                }
                else if (IsClose(line, fenceChar, fenceLength))
                {
                    kinds[i] = MdFence.Delimiter;
                    fenceLength = 0;
                }
                else
                {
                    kinds[i] = MdFence.Inside;
                }
            }
            return kinds;
        }

        private static bool IsClose(string line, char c, int length)
        {
            int i = 0;
            while (i < line.Length && i < 3 && line[i] == ' ') i++;
            int run = 0;
            while (i + run < line.Length && line[i + run] == c) run++;
            if (run < length) return false;
            for (int k = i + run; k < line.Length; k++)
                if (!char.IsWhiteSpace(line[k])) return false;
            return true;
        }
    }
}
```

- [ ] **Step 5: Write `Services/Pad/MarkdownStyles.cs`**

```csharp
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>How heavy a Markdown run's text is; Keep leaves the weight as it is.</summary>
    public enum MdWeight
    {
        Keep,
        SemiBold,
        Bold,
    }

    /// <summary>
    /// What one Markdown style changes on the text it covers. A null color, <see cref="MdWeight.Keep"/>,
    /// a size factor of 1 and false leave that property as it is, so styles stack: bold inside a
    /// heading keeps the heading's size.
    /// </summary>
    public readonly record struct MdLook(PadColor? Foreground, PadColor? Background, double SizeFactor, MdWeight Weight, bool Italic, bool Strike);

    /// <summary>The look of each Markdown style in a palette (spec 2.3).</summary>
    public static class MarkdownStyles
    {
        /// <summary>Heading sizes, level 1 to 6, as a multiple of the editor font.</summary>
        public static IReadOnlyList<double> HeadingSizes { get; } = new[] { 1.6, 1.4, 1.25, 1.15, 1.05, 1.0 };

        public static MdLook LookOf(MdStyle style, PadPalette palette)
        {
            if (style >= MdStyle.Heading1 && style <= MdStyle.Heading6)
                return new MdLook(palette.MdHeading, null, HeadingSizes[style - MdStyle.Heading1], MdWeight.SemiBold, false, false);

            return style switch
            {
                MdStyle.Marker => new MdLook(palette.MdMarker, null, 1, MdWeight.Keep, false, false),
                MdStyle.Bold => new MdLook(null, null, 1, MdWeight.Bold, false, false),
                MdStyle.Italic => new MdLook(null, null, 1, MdWeight.Keep, true, false),
                MdStyle.BoldItalic => new MdLook(null, null, 1, MdWeight.Bold, true, false),
                MdStyle.Strike => new MdLook(null, null, 1, MdWeight.Keep, false, true),
                MdStyle.Code => new MdLook(null, palette.MdCodeBackground, 1, MdWeight.Keep, false, false),
                MdStyle.LinkText => new MdLook(palette.MdLink, null, 1, MdWeight.Keep, false, false),
                MdStyle.ListMarker => new MdLook(palette.MdListMarker, null, 1, MdWeight.Keep, false, false),
                MdStyle.TaskDone => new MdLook(palette.MdTaskDone, null, 1, MdWeight.Keep, false, true),
                MdStyle.QuoteText => new MdLook(palette.MdQuoteText, null, 1, MdWeight.Keep, false, false),
                // CodeBlock: its background is drawn per line by the background renderer.
                _ => new MdLook(null, null, 1, MdWeight.Keep, false, false),
            };
        }
    }
}
```

- [ ] **Step 6: Run the focused tests, then the whole suite** — expected PASS. If a tokenizer case fails, fix the tokenizer, not the test: each case is what a user sees.

- [ ] **Step 7: Commit**

```bash
git add Services/Pad/MarkdownLineTokenizer.cs Services/Pad/FenceTracker.cs Services/Pad/MarkdownStyles.cs tests/Kil0bitSystemMonitor.Tests/MarkdownTokenizerTests.cs
git commit -m "feat(pad): the Markdown line model - fences, styled runs and their looks" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

### Task 6: Markdown styled source in the editor

**Files:**
- Create: `Pad/MarkdownDocumentCache.cs`, `Pad/MarkdownColorizer.cs`, `Pad/MarkdownBackgroundRenderer.cs`, `Pad/BulletGenerator.cs`
- Modify: `Pad/EditorLanguage.cs` (whole file below)
- Modify: `Pad/MicaPadWindow.xaml.cs` (`☰` menu item)
- Modify: `SettingsWindow.xaml` (MicaPad section), `SettingsWindow.xaml.cs` (`LoadPadSettings`, `OnPadToggled`)
- Modify: `GUIDE.md`, `README.md`
- Create: `tests/Kil0bitSystemMonitor.Tests/MarkdownRenderingTests.cs`

**Interfaces:**
- Consumes: Task 5's tokenizer, fences and looks; Task 4's `EditorLanguage` and window wiring; `PadThemeApplier.ToBrush`.
- Produces:
  - `internal sealed class MarkdownDocumentCache` — `MdFence KindOf(TextDocument, int lineNumber)`, `void Detach()`, `event Action? FencesChanged`, `internal int Recomputes`.
  - `internal sealed class MarkdownColorizer : DocumentColorizingTransformer` — ctor `(MarkdownDocumentCache cache, Func<PadPalette> palette)`.
  - `internal sealed class MarkdownBackgroundRenderer : IBackgroundRenderer` — same ctor.
  - `internal sealed class BulletGenerator : VisualLineElementGenerator` — ctor `(MarkdownDocumentCache cache)`.
  - `EditorLanguage.HasMarkdown` (`bool`).

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/MarkdownRenderingTests.cs`:

```csharp
using System.Linq;
using System.Windows;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Markdown in the editor: the fence cache, the colorizer on a real TextView, and the window wiring.</summary>
    public class MarkdownRenderingTests
    {
        [Fact]
        public void The_cache_classifies_fences_and_follows_edits() => UiThread.Run(() =>
        {
            var document = new TextDocument("a\n```\nx\n```\nb");
            var cache = new MarkdownDocumentCache();
            int changes = 0;
            cache.FencesChanged += () => changes++;

            Assert.Equal(MdFence.Inside, cache.KindOf(document, 3));
            Assert.Equal(MdFence.None, cache.KindOf(document, 5));

            // Typing after the closing fence stops it closing: line 5 is now inside.
            document.Insert(document.GetLineByNumber(4).EndOffset, "x");
            Assert.Equal(MdFence.Inside, cache.KindOf(document, 5));
            Assert.Equal(1, changes);
        });

        [Fact]
        public void Ordinary_typing_does_not_rescan_the_document() => UiThread.Run(() =>
        {
            var document = new TextDocument("# Title\nsome text\n```\ncode\n```");
            var cache = new MarkdownDocumentCache();
            cache.KindOf(document, 1);
            int before = cache.Recomputes;

            document.Insert(document.GetLineByNumber(2).EndOffset, " more words");
            document.Insert(document.GetLineByNumber(4).EndOffset, "();");

            Assert.Equal(before, cache.Recomputes);
            Assert.Equal(MdFence.Inside, cache.KindOf(document, 4));
        });

        [Fact]
        public void The_colorizer_sizes_headings_and_bolds_bold_text() => UiThread.Run(() =>
        {
            var document = new TextDocument("# Title\nsome **bold** text");
            var cache = new MarkdownDocumentCache();
            var view = new TextView { Document = document };
            view.LineTransformers.Add(new MarkdownColorizer(cache, () => PadPalette.Dark));
            view.Measure(new Size(600, 400));
            view.Arrange(new Rect(0, 0, 600, 400));
            view.EnsureVisualLines();

            var plain = view.GetVisualLine(2)!.Elements.First(e => e.RelativeTextOffset == 0);
            double baseSize = plain.TextRunProperties.FontRenderingEmSize;
            var title = view.GetVisualLine(1)!.Elements.Last();
            Assert.Equal(baseSize * 1.6, title.TextRunProperties.FontRenderingEmSize, 3);

            var bold = view.GetVisualLine(2)!.Elements.Single(e => e.RelativeTextOffset == 7);
            Assert.Equal(FontWeights.Bold, bold.TextRunProperties.Typeface.Weight);
        });

        [Fact]
        public void A_markdown_note_gets_the_colorizer_the_background_and_bullets() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var view = window.Editor.TextArea.TextView;
            Assert.True(window.LanguageView.HasMarkdown);
            Assert.Single(view.LineTransformers.OfType<MarkdownColorizer>());
            Assert.Single(view.BackgroundRenderers.OfType<MarkdownBackgroundRenderer>());
            Assert.Single(view.ElementGenerators.OfType<BulletGenerator>());
        });

        [Fact]
        public void Plain_text_and_code_files_have_no_markdown() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var note = env.Workspace.Active!;
            window.ChooseLanguage(note, "plain");
            var view = window.Editor.TextArea.TextView;
            Assert.False(window.LanguageView.HasMarkdown);
            Assert.Empty(view.LineTransformers.OfType<MarkdownColorizer>());
            Assert.Empty(view.BackgroundRenderers.OfType<MarkdownBackgroundRenderer>());
            Assert.Empty(view.ElementGenerators.OfType<BulletGenerator>());

            PadLanguageWindowTests.OpenFile(window, env, "a.json", "{}");
            Assert.False(window.LanguageView.HasMarkdown);
        });

        [Fact]
        public void The_markdown_switch_takes_it_away_and_back() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            config.PadMarkdown = false;
            Assert.False(window.LanguageView.HasMarkdown);

            config.PadMarkdown = true;
            Assert.True(window.LanguageView.HasMarkdown);
            Assert.Single(window.Editor.TextArea.TextView.LineTransformers.OfType<MarkdownColorizer>());
        });

        [Fact]
        public void Settings_offers_the_markdown_switch()
        {
            string xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(PadWindowTests.RepoRoot(), "SettingsWindow.xaml"));
            Assert.Contains("x:Name=\"PadMarkdownToggle\"", xaml);
        }
    }
}
```

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~MarkdownRenderingTests"`). Expected: build FAILS (`MarkdownDocumentCache`, `MarkdownColorizer`, `MarkdownBackgroundRenderer`, `BulletGenerator`, `HasMarkdown` not found).

If `The_colorizer_sizes_headings_and_bolds_bold_text` fails because a `TextView` that is not in a window cannot build visual lines (it throws, or `GetVisualLine` returns null after `EnsureVisualLines`), do not delete the test: report BLOCKED with the exact output, and the controller will decide.

- [ ] **Step 3: Write `Pad/MarkdownDocumentCache.cs`**

```csharp
using System;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Which lines of the shown document are fenced code, shared by the Markdown colorizer, the
    /// background renderer and the bullet generator. Rescans only after an edit that can move a
    /// fence (a backtick or tilde, a new or removed line, or an edit on a delimiter line), so
    /// ordinary typing never walks the whole note.
    /// </summary>
    internal sealed class MarkdownDocumentCache
    {
        private static readonly char[] FenceChars = { '`', '~' };

        private TextDocument? _document;
        private MdFence[] _kinds = Array.Empty<MdFence>();
        private bool _stale = true;

        /// <summary>Raised after an edit changed which lines are fenced, so lines far from the edit repaint.</summary>
        public event Action? FencesChanged;

        /// <summary>How many times the whole document was scanned; for tests.</summary>
        internal int Recomputes { get; private set; }

        /// <summary>The fence kind of a line (1-based) of <paramref name="document"/>.</summary>
        public MdFence KindOf(TextDocument document, int lineNumber)
        {
            Track(document);
            if (_stale) Recompute();
            int index = lineNumber - 1;
            return index >= 0 && index < _kinds.Length ? _kinds[index] : MdFence.None;
        }

        /// <summary>Stops following the document.</summary>
        public void Detach()
        {
            if (_document != null) _document.Changed -= OnChanged;
            _document = null;
            _kinds = Array.Empty<MdFence>();
            _stale = true;
        }

        private void Track(TextDocument document)
        {
            if (ReferenceEquals(document, _document)) return;
            Detach();
            _document = document;
            _document.Changed += OnChanged;
        }

        private void OnChanged(object? sender, DocumentChangeEventArgs e)
        {
            var document = _document!;
            if (!_stale && !TouchesFences(document, e)) return;
            var before = _kinds;
            Recompute();
            if (!before.AsSpan().SequenceEqual(_kinds)) FencesChanged?.Invoke();
        }

        private bool TouchesFences(TextDocument document, DocumentChangeEventArgs e)
        {
            if (document.LineCount != _kinds.Length) return true;
            if (e.InsertedText.Text.IndexOfAny(FenceChars) >= 0 || e.RemovedText.Text.IndexOfAny(FenceChars) >= 0) return true;
            int line = document.GetLineByOffset(Math.Min(e.Offset, document.TextLength)).LineNumber;
            return _kinds[line - 1] == MdFence.Delimiter;
        }

        private void Recompute()
        {
            var document = _document!;
            var lines = new string[document.LineCount];
            foreach (var line in document.Lines) lines[line.LineNumber - 1] = document.GetText(line);
            _kinds = FenceTracker.Classify(lines);
            _stale = false;
            Recomputes++;
        }
    }
}
```

- [ ] **Step 4: Write `Pad/MarkdownColorizer.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

using Brush = System.Windows.Media.Brush;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Markdown styled source (spec 2.3): each run of a line gets the look
    /// <see cref="MarkdownStyles.LookOf"/> gives it — heading sizes, bold, italic, strike, link and
    /// marker colors. Only how text is drawn changes; the document is never touched.
    /// </summary>
    internal sealed class MarkdownColorizer : DocumentColorizingTransformer
    {
        private readonly MarkdownDocumentCache _cache;
        private readonly Func<PadPalette> _palette;
        private readonly Dictionary<PadColor, Brush> _brushes = new();

        public MarkdownColorizer(MarkdownDocumentCache cache, Func<PadPalette> palette)
        {
            _cache = cache;
            _palette = palette;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            var document = CurrentContext.Document;
            var md = MarkdownLineTokenizer.Tokenize(document.GetText(line), _cache.KindOf(document, line.LineNumber));
            var palette = _palette();
            double baseSize = CurrentContext.GlobalTextRunProperties.FontRenderingEmSize;

            foreach (var span in md.Spans)
            {
                var look = MarkdownStyles.LookOf(span.Style, palette);
                int start = line.Offset + span.Start;
                ChangeLinePart(start, start + span.Length, element => Apply(element, look, baseSize));
            }
        }

        private void Apply(VisualLineElement element, MdLook look, double baseSize)
        {
            var properties = element.TextRunProperties;
            if (look.Foreground is PadColor foreground) properties.SetForegroundBrush(BrushFor(foreground));
            if (look.Background is PadColor background) properties.SetBackgroundBrush(BrushFor(background));
            if (look.SizeFactor != 1) properties.SetFontRenderingEmSize(baseSize * look.SizeFactor);

            if (look.Weight != MdWeight.Keep || look.Italic)
            {
                var face = properties.Typeface;
                var weight = look.Weight switch
                {
                    MdWeight.Bold => FontWeights.Bold,
                    MdWeight.SemiBold => FontWeights.SemiBold,
                    _ => face.Weight,
                };
                properties.SetTypeface(new Typeface(face.FontFamily, look.Italic ? FontStyles.Italic : face.Style, weight, face.Stretch));
            }

            if (look.Strike)
            {
                var decorations = properties.TextDecorations == null
                    ? new TextDecorationCollection()
                    : new TextDecorationCollection(properties.TextDecorations);
                decorations.Add(TextDecorations.Strikethrough);
                decorations.Freeze();
                properties.SetTextDecorations(decorations);
            }
        }

        private Brush BrushFor(PadColor color)
        {
            if (!_brushes.TryGetValue(color, out var brush)) _brushes[color] = brush = PadThemeApplier.ToBrush(color);
            return brush;
        }
    }
}
```

- [ ] **Step 5: Write `Pad/MarkdownBackgroundRenderer.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// What Markdown draws behind the text: shading across fenced code lines, a bar at the left of
    /// quotes, and a line through horizontal rules. Only visible lines are drawn.
    /// </summary>
    internal sealed class MarkdownBackgroundRenderer : IBackgroundRenderer
    {
        private readonly MarkdownDocumentCache _cache;
        private readonly Func<PadPalette> _palette;
        private readonly Dictionary<PadColor, Brush> _brushes = new();

        public MarkdownBackgroundRenderer(MarkdownDocumentCache cache, Func<PadPalette> palette)
        {
            _cache = cache;
            _palette = palette;
        }

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var document = textView.Document;
            if (document == null || !textView.VisualLinesValid) return;
            var palette = _palette();
            double width = textView.ActualWidth;

            foreach (var visual in textView.VisualLines)
            {
                var line = visual.FirstDocumentLine;
                var block = MarkdownLineTokenizer.BlockOf(document.GetText(line), _cache.KindOf(document, line.LineNumber));
                double top = visual.VisualTop - textView.VerticalOffset;
                double height = visual.Height;

                switch (block)
                {
                    case MdBlock.Fence:
                        drawingContext.DrawRectangle(BrushFor(palette.MdCodeBackground), null, new Rect(0, top, width, height));
                        break;
                    case MdBlock.Quote:
                        drawingContext.DrawRectangle(BrushFor(palette.MdQuoteBar), null, new Rect(0, top, 3, height));
                        break;
                    case MdBlock.Rule:
                        double y = Math.Round(top + height / 2) + 0.5;
                        drawingContext.DrawLine(new Pen(BrushFor(palette.MdRule), 1), new Point(0, y), new Point(width, y));
                        break;
                }
            }
        }

        private Brush BrushFor(PadColor color)
        {
            if (!_brushes.TryGetValue(color, out var brush)) _brushes[color] = brush = PadThemeApplier.ToBrush(color);
            return brush;
        }
    }
}
```

- [ ] **Step 6: Write `Pad/BulletGenerator.cs`**

```csharp
using System;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Draws a list item's <c>-</c>, <c>*</c> or <c>+</c> as <c>•</c>. The element stands for exactly
    /// one character of the document, so the caret, selection and copying are unaffected; the
    /// Markdown colorizer paints it in the list-marker color.
    /// </summary>
    internal sealed class BulletGenerator : VisualLineElementGenerator
    {
        private const int ScanLength = 256;
        private readonly MarkdownDocumentCache _cache;

        public BulletGenerator(MarkdownDocumentCache cache) => _cache = cache;

        public override int GetFirstInterestedOffset(int startOffset)
        {
            var document = CurrentContext.Document;
            var line = document.GetLineByOffset(startOffset);
            if (_cache.KindOf(document, line.LineNumber) != MdFence.None) return -1;

            int marker = MarkdownLineTokenizer.BulletOffset(document.GetText(line.Offset, Math.Min(line.Length, ScanLength)));
            if (marker < 0) return -1;
            int offset = line.Offset + marker;
            return offset >= startOffset ? offset : -1;
        }

        public override VisualLineElement ConstructElement(int offset) => new FormattedTextElement("•", 1);
    }
}
```

- [ ] **Step 7: Replace `Pad/EditorLanguage.cs` with**

```csharp
using System;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Everything a language adds to one editor, installed and removed together: syntax colors, or
    /// Markdown formatting (colorizer, background, bullets); folding comes in Task 8.
    /// <see cref="Apply"/> always removes the previous language first, so switching tabs never
    /// piles anything up.
    /// </summary>
    internal sealed class EditorLanguage
    {
        private readonly TextEditor _editor;
        private readonly Func<PadPalette> _palette;
        private ThemedHighlightingColorizer? _syntax;
        private MarkdownDocumentCache? _markdownCache;
        private MarkdownColorizer? _markdown;
        private MarkdownBackgroundRenderer? _markdownBackground;
        private BulletGenerator? _bullets;

        public EditorLanguage(TextEditor editor, Func<PadPalette> palette)
        {
            _editor = editor;
            _palette = palette;
        }

        /// <summary>The language applied now.</summary>
        public PadLanguage Current { get; private set; } = PadLanguages.Plain;

        /// <summary>True while syntax colors are installed.</summary>
        internal bool HasSyntaxColors => _syntax != null;

        /// <summary>True while Markdown formatting is installed.</summary>
        internal bool HasMarkdown => _markdown != null;

        /// <summary>Shows the editor's text in <paramref name="language"/>.</summary>
        public void Apply(PadLanguage language)
        {
            Clear();
            Current = language;
            var view = _editor.TextArea.TextView;

            if (ReferenceEquals(language, PadLanguages.Markdown))
            {
                _markdownCache = new MarkdownDocumentCache();
                _markdownCache.FencesChanged += OnFencesChanged;
                _markdown = new MarkdownColorizer(_markdownCache, _palette);
                _markdownBackground = new MarkdownBackgroundRenderer(_markdownCache, _palette);
                _bullets = new BulletGenerator(_markdownCache);
                view.LineTransformers.Add(_markdown);
                view.BackgroundRenderers.Add(_markdownBackground);
                view.ElementGenerators.Add(_bullets);
            }
            else if (PadHighlighting.For(language) is { } definition)
            {
                _syntax = new ThemedHighlightingColorizer(definition, _palette);
                view.LineTransformers.Add(_syntax);
            }
            Redraw();
        }

        /// <summary>Repaints; the colorizers read the palette as they draw, so a theme switch needs only this.</summary>
        public void Redraw() => _editor.TextArea.TextView.Redraw();

        /// <summary>A fence moved: lines far from the edit changed look, so repaint them all once the edit is done.</summary>
        private void OnFencesChanged() =>
            _editor.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Redraw));

        private void Clear()
        {
            var view = _editor.TextArea.TextView;
            if (_syntax != null)
            {
                view.LineTransformers.Remove(_syntax);
                _syntax = null;
            }
            if (_markdown != null)
            {
                view.LineTransformers.Remove(_markdown);
                view.BackgroundRenderers.Remove(_markdownBackground!);
                view.ElementGenerators.Remove(_bullets!);
                _markdownCache!.FencesChanged -= OnFencesChanged;
                _markdownCache.Detach();
                _markdown = null;
                _markdownBackground = null;
                _bullets = null;
                _markdownCache = null;
            }
        }
    }
}
```

- [ ] **Step 8: The Markdown switch in `☰` and Settings**

In `Pad/MicaPadWindow.xaml.cs`, `OnMenuButtonClick`, after the *Line numbers* item:

```csharp
            menu.Items.Add(Check("Markdown formatting", null, _config.PadMarkdown, () => _config.PadMarkdown = !_config.PadMarkdown));
```

In `SettingsWindow.xaml`, MicaPad section, insert after the *Theme* card from Part 1 (the one with `x:Name="PadThemeBox"`):

```xml
                        <Border Background="{DynamicResource SystemControlBackgroundChromeMediumLowBrush}" BorderBrush="{DynamicResource SystemControlElevationBorderBrush}" BorderThickness="1" CornerRadius="8" Margin="0,0,0,12" Padding="20,16">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto" />
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="Auto" />
                                </Grid.ColumnDefinitions>
                                <ui:FontIcon Glyph="&#xE8D2;" FontSize="20" Foreground="{DynamicResource SystemAccentColorBrush}" Margin="0,0,20,0" VerticalAlignment="Center"/>
                                <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,16,0">
                                    <TextBlock Text="Markdown formatting" FontWeight="SemiBold" FontSize="15"/>
                                    <TextBlock Text="Headings, bold, lists and code in notes and .md and .txt files are shown formatted as you type. The file itself never changes." Opacity="0.6" FontSize="12.5" TextWrapping="Wrap"/>
                                </StackPanel>
                                <ui:ToggleSwitch Grid.Column="2" x:Name="PadMarkdownToggle" Toggled="OnPadToggled" VerticalAlignment="Center"/>
                            </Grid>
                        </Border>
```

In `SettingsWindow.xaml.cs`: in `LoadPadSettings`, after `PadLineNumbersToggle.IsOn = cfg.PadShowLineNumbers;` add `PadMarkdownToggle.IsOn = cfg.PadMarkdown;`; in `OnPadToggled`, after `cfg.PadShowLineNumbers = PadLineNumbersToggle.IsOn;` add `cfg.PadMarkdown = PadMarkdownToggle.IsOn;`.

- [ ] **Step 9: Run the new tests, then the whole suite** — expected PASS.

- [ ] **Step 10: Document it**

`GUIDE.md`, `## 📝 MicaPad`, insert before `### Colors for code, logs and settings files`:

```markdown
### Markdown

Notes and `.md`/`.txt` files are formatted as you type: `#` headings grow, `**bold**`, `*italic*`,
`~~strike~~` and `` `code` `` look the part, `- ` items get bullets, `- [x]` tasks are crossed off,
`>` quotes get a bar and fenced ```` ``` ```` blocks a shaded background. The markers stay visible
(dimmed) — the file is plain text and never changes. Turn it off with **☰ → Markdown formatting**
or in **Settings → MicaPad**.

```

`README.md` English list, after the *Colors for code and logs* bullet:

```markdown
* **Markdown as you type**: headings, bold, italic, code, lists, tasks and quotes are formatted in place, with the markers still visible; the file stays plain text
```

Thai list, after the *สีสำหรับโค้ดและล็อก* bullet:

```markdown
* **Markdown ขณะพิมพ์**: หัวข้อ ตัวหนา ตัวเอียง โค้ด รายการ งานที่ต้องทำ และข้อความอ้างอิง แสดงผลตามรูปแบบทันที โดยยังเห็นเครื่องหมาย และไฟล์ยังเป็นข้อความธรรมดา
```

- [ ] **Step 11: Commit**

```bash
git add Pad/MarkdownDocumentCache.cs Pad/MarkdownColorizer.cs Pad/MarkdownBackgroundRenderer.cs Pad/BulletGenerator.cs Pad/EditorLanguage.cs Pad/MicaPadWindow.xaml.cs SettingsWindow.xaml SettingsWindow.xaml.cs GUIDE.md README.md tests/Kil0bitSystemMonitor.Tests/MarkdownRenderingTests.cs
git commit -m "feat(pad): Markdown styled source for notes and md/txt files" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

### Task 7: The Format menu

**Files:**
- Create: `Services/Pad/MarkdownFormatter.cs`
- Modify: `Pad/MicaPadWindow.xaml.cs` (`FillEditorMenu`, new `BuildFormatMenu`, `ApplyEdit`)
- Modify: `tests/Kil0bitSystemMonitor.Tests/PadMenuTests.cs` (the Part 1 header test gains the Format group; new Format tests)
- Modify: `GUIDE.md`
- Create: `tests/Kil0bitSystemMonitor.Tests/MarkdownFormatterTests.cs`

**Interfaces:**
- Consumes: Part 1's `FillEditorMenu`, `Item`; Task 4's `_resolved`.
- Produces: `public readonly record struct TextEdit(int Offset, int Length, string Text, int SelectionStart, int SelectionLength)`, `public enum LinePrefix { Heading1, Heading2, Heading3, Bullet, Numbered, Task, Quote }`, `MarkdownFormatter.Wrap(string text, int start, int length, string marker)`, `.Link(string, int, int)`, `.Prefix(string, int, int, LinePrefix)`, `.CodeBlock(string, int, int)`; `MicaPadWindow.ApplyEdit(TextEditor, TextEdit)` (internal static).

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/MarkdownFormatterTests.cs`:

```csharp
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The Format menu's edits: wrapping, links, line prefixes and code blocks, each toggling where it makes sense.</summary>
    public class MarkdownFormatterTests
    {
        private static string Apply(string text, TextEdit edit) => text.Remove(edit.Offset, edit.Length).Insert(edit.Offset, edit.Text);

        private static string Selected(string text, TextEdit edit) => Apply(text, edit).Substring(edit.SelectionStart, edit.SelectionLength);

        [Fact]
        public void Wrap_surrounds_the_selection_and_keeps_it_selected()
        {
            var edit = MarkdownFormatter.Wrap("hello world", 0, 5, "**");
            Assert.Equal("**hello** world", Apply("hello world", edit));
            Assert.Equal("hello", Selected("hello world", edit));
        }

        [Fact]
        public void Wrap_with_nothing_selected_puts_the_caret_between_the_markers()
        {
            var edit = MarkdownFormatter.Wrap("ab", 1, 0, "`");
            Assert.Equal("a``b", Apply("ab", edit));
            Assert.Equal(2, edit.SelectionStart);
            Assert.Equal(0, edit.SelectionLength);
        }

        [Fact]
        public void Wrap_again_unwraps_a_selection_that_includes_the_markers()
        {
            var edit = MarkdownFormatter.Wrap("**hello** x", 0, 9, "**");
            Assert.Equal("hello x", Apply("**hello** x", edit));
            Assert.Equal("hello", Selected("**hello** x", edit));
        }

        [Fact]
        public void Wrap_again_unwraps_markers_just_outside_the_selection()
        {
            var edit = MarkdownFormatter.Wrap("**hello** x", 2, 5, "**");
            Assert.Equal("hello x", Apply("**hello** x", edit));
            Assert.Equal("hello", Selected("**hello** x", edit));
        }

        [Fact]
        public void Link_wraps_the_text_and_selects_the_address()
        {
            var edit = MarkdownFormatter.Link("see docs", 4, 4);
            Assert.Equal("see [docs](url)", Apply("see docs", edit));
            Assert.Equal("url", Selected("see docs", edit));

            var empty = MarkdownFormatter.Link("", 0, 0);
            Assert.Equal("[](url)", Apply("", empty));
            Assert.Equal("url", Selected("", empty));
        }

        [Theory]
        [InlineData("a\nb", LinePrefix.Bullet, "- a\n- b")]
        [InlineData("- a\n- b", LinePrefix.Bullet, "a\nb")]
        [InlineData("x\n\ny", LinePrefix.Numbered, "1. x\n\n2. y")]
        [InlineData("- x\n- y", LinePrefix.Numbered, "1. x\n2. y")]
        [InlineData("1. x\n2. y", LinePrefix.Numbered, "x\ny")]
        [InlineData("buy milk", LinePrefix.Task, "- [ ] buy milk")]
        [InlineData("- [x] done", LinePrefix.Task, "done")]
        [InlineData("- item", LinePrefix.Task, "- [ ] item")]
        [InlineData("# Title", LinePrefix.Heading2, "## Title")]
        [InlineData("## Title", LinePrefix.Heading2, "Title")]
        [InlineData("Title", LinePrefix.Heading1, "# Title")]
        [InlineData("a", LinePrefix.Quote, "> a")]
        [InlineData("> a", LinePrefix.Quote, "a")]
        [InlineData("", LinePrefix.Bullet, "- ")]
        [InlineData("  a", LinePrefix.Bullet, "  - a")]
        public void Prefix_toggles_on_every_selected_line(string text, LinePrefix kind, string expected)
        {
            var edit = MarkdownFormatter.Prefix(text, 0, text.Length, kind);
            Assert.Equal(expected, Apply(text, edit));
            Assert.Equal(expected, Selected(text, edit));
        }

        [Fact]
        public void Prefix_takes_the_whole_line_under_a_caret()
        {
            string text = "first\nsecond\nthird";
            var edit = MarkdownFormatter.Prefix(text, 9, 0, LinePrefix.Bullet);   // caret inside "second"
            Assert.Equal("first\n- second\nthird", Apply(text, edit));
        }

        [Fact]
        public void Prefix_keeps_crlf_and_ignores_a_line_the_selection_only_touches()
        {
            string text = "one\r\ntwo\r\nthree";
            var edit = MarkdownFormatter.Prefix(text, 0, "one\r\ntwo\r\n".Length, LinePrefix.Bullet);
            Assert.Equal("- one\r\n- two\r\nthree", Apply(text, edit));
        }

        [Fact]
        public void Prefix_keeps_mixed_line_endings_as_they_are()
        {
            string text = "a\r\nb\nc";
            Assert.Equal("- a\r\n- b\n- c", Apply(text, MarkdownFormatter.Prefix(text, 0, text.Length, LinePrefix.Bullet)));
        }

        [Fact]
        public void Code_block_fences_the_selected_lines_with_the_notes_line_ending()
        {
            string crlf = "a\r\nb";
            var edit = MarkdownFormatter.CodeBlock(crlf, 0, crlf.Length);
            Assert.Equal("```\r\na\r\nb\r\n```", Apply(crlf, edit));
            Assert.Equal("a\r\nb", Selected(crlf, edit));

            string lf = "x\ny";
            Assert.Equal("```\nx\n```\ny", Apply(lf, MarkdownFormatter.CodeBlock(lf, 0, 1)));

            Assert.Equal("```\r\nsolo\r\n```", Apply("solo", MarkdownFormatter.CodeBlock("solo", 0, 0)));
        }
    }
}
```

In `tests/Kil0bitSystemMonitor.Tests/PadMenuTests.cs`:

1. In `The_editor_menu_lists_edit_then_find_items` (a new note is Markdown, so it has the Format group), change the expected array to:

```csharp
            Assert.Equal(new[] { "Undo", "Redo", "-", "Cut", "Copy", "Paste", "Delete", "Select all", "-", "Format", "-", "Find", "Replace", "Go to line…" },
                         Headers(window.EditorMenu));
```

2. Add:

```csharp
        [Fact]
        public void A_markdown_note_has_the_format_menu_and_a_json_file_does_not() => WithWindow((window, env) =>
        {
            window.RefreshEditorMenu();
            var format = ItemOf(window.EditorMenu, "Format");
            var headers = format.Items.Cast<object>().Select(i => i is MenuItem m ? (string)m.Header : "-").ToArray();
            Assert.Equal(new[] { "Bold", "Italic", "Strikethrough", "Code", "Link", "-", "Heading 1", "Heading 2", "Heading 3", "-",
                                 "Bullet list", "Numbered list", "Task", "Quote", "Code block" }, headers);

            string path = env.FileOf("a.json");
            File.WriteAllText(path, "{}");
            window.OpenPath(path);
            window.RefreshEditorMenu();
            Assert.DoesNotContain("Format", Headers(window.EditorMenu));
        });

        [Fact]
        public void Format_from_the_menu_is_one_undo_step() => WithWindow((window, env) =>
        {
            window.Editor.Document.Insert(0, "hello");
            window.Editor.Select(0, 5);
            window.RefreshEditorMenu();

            var bold = ItemOf(window.EditorMenu, "Format").Items.OfType<MenuItem>().Single(m => (string)m.Header == "Bold");
            Click(bold);

            Assert.Equal("**hello**", window.Editor.Document.Text);
            Assert.Equal("hello", window.Editor.SelectedText);
            window.Editor.Undo();
            Assert.Equal("hello", window.Editor.Document.Text);
        });
```

(`PadMenuTests` already has `using System.IO;`, `System.Linq` and the `MenuItem` alias or `System.Windows.Controls`; add what the compiler asks for.)

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~MarkdownFormatterTests|FullyQualifiedName~PadMenuTests"`). Expected: build FAILS (`MarkdownFormatter`, `TextEdit`, `LinePrefix` not found).

- [ ] **Step 3: Write `Services/Pad/MarkdownFormatter.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>One replacement in the text, and the selection to show after it.</summary>
    public readonly record struct TextEdit(int Offset, int Length, string Text, int SelectionStart, int SelectionLength);

    /// <summary>The line prefixes the Format menu toggles.</summary>
    public enum LinePrefix
    {
        Heading1,
        Heading2,
        Heading3,
        Bullet,
        Numbered,
        Task,
        Quote,
    }

    /// <summary>
    /// The edits behind MicaPad's Format menu (spec 2.4). Pure: each returns one replacement, which
    /// the window applies as a single undoable change. Line endings already in the text are kept.
    /// </summary>
    public static class MarkdownFormatter
    {
        private static readonly char[] LineBreaks = { '\r', '\n' };
        private static readonly Regex HeadingRx = new(@"^(#{1,6})[ \t]+", RegexOptions.CultureInvariant);
        private static readonly Regex TaskRx = new(@"^[-*+][ \t]+\[[ xX]\][ \t]+", RegexOptions.CultureInvariant);
        private static readonly Regex BulletRx = new(@"^[-*+][ \t]+(?!\[[ xX]\])", RegexOptions.CultureInvariant);
        private static readonly Regex NumberedRx = new(@"^\d{1,9}[.)][ \t]+", RegexOptions.CultureInvariant);
        private static readonly Regex AnyListRx = new(@"^([-*+][ \t]+\[[ xX]\][ \t]+|[-*+][ \t]+|\d{1,9}[.)][ \t]+)", RegexOptions.CultureInvariant);
        private static readonly Regex QuoteRx = new(@"^>[ \t]?", RegexOptions.CultureInvariant);

        /// <summary>
        /// Wraps the selection in <paramref name="marker"/>, or unwraps it when the selection already
        /// starts and ends with it, or has it just outside. With nothing selected, inserts the pair and
        /// puts the caret between.
        /// </summary>
        public static TextEdit Wrap(string text, int start, int length, string marker)
        {
            string selected = text.Substring(start, length);
            int m = marker.Length;

            if (length >= 2 * m && selected.StartsWith(marker, StringComparison.Ordinal) && selected.EndsWith(marker, StringComparison.Ordinal))
                return new TextEdit(start, length, selected.Substring(m, length - 2 * m), start, length - 2 * m);

            if (length > 0 && start >= m && start + length + m <= text.Length
                && string.CompareOrdinal(text, start - m, marker, 0, m) == 0
                && string.CompareOrdinal(text, start + length, marker, 0, m) == 0)
                return new TextEdit(start - m, length + 2 * m, selected, start - m, length);

            return new TextEdit(start, length, marker + selected + marker, start + m, length);
        }

        /// <summary>Turns the selection into <c>[selection](url)</c> and selects <c>url</c> to type over.</summary>
        public static TextEdit Link(string text, int start, int length)
        {
            string selected = text.Substring(start, length);
            return new TextEdit(start, length, "[" + selected + "](url)", start + selected.Length + 3, 3);
        }

        /// <summary>
        /// Toggles a prefix on every line the selection touches (or the caret's line). If every
        /// non-blank line already has it, it is removed; otherwise it is added to those that lack it,
        /// replacing another heading level or list kind. Blank lines are left alone unless every line
        /// is blank. The whole changed block is selected afterwards.
        /// </summary>
        public static TextEdit Prefix(string text, int start, int length, LinePrefix kind)
        {
            var (blockStart, blockEnd) = LineBlock(text, start, length);
            var (lines, breaks) = SplitLines(text.Substring(blockStart, blockEnd - blockStart));

            bool[] targets = lines.Select(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            if (!targets.Any(t => t)) targets = lines.Select(_ => true).ToArray();

            bool remove = true;
            for (int i = 0; i < lines.Count; i++)
                if (targets[i] && !Has(Split(lines[i]).Rest, kind)) { remove = false; break; }

            int number = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                if (!targets[i]) continue;
                var (indent, rest) = Split(lines[i]);
                lines[i] = indent + (remove ? Remove(rest, kind) : Add(rest, kind, ++number));
            }

            string block = Join(lines, breaks);
            return new TextEdit(blockStart, blockEnd - blockStart, block, blockStart, block.Length);
        }

        /// <summary>Puts <c>```</c> lines around the selected lines, using the note's line ending.</summary>
        public static TextEdit CodeBlock(string text, int start, int length)
        {
            var (blockStart, blockEnd) = LineBlock(text, start, length);
            string block = text.Substring(blockStart, blockEnd - blockStart);
            string newline = NewlineOf(text);
            string fenced = "```" + newline + block + newline + "```";
            return new TextEdit(blockStart, blockEnd - blockStart, fenced, blockStart + 3 + newline.Length, block.Length);
        }

        private static bool Has(string rest, LinePrefix kind) => kind switch
        {
            LinePrefix.Heading1 => HeadingLevel(rest) == 1,
            LinePrefix.Heading2 => HeadingLevel(rest) == 2,
            LinePrefix.Heading3 => HeadingLevel(rest) == 3,
            LinePrefix.Bullet => BulletRx.IsMatch(rest),
            LinePrefix.Numbered => NumberedRx.IsMatch(rest),
            LinePrefix.Task => TaskRx.IsMatch(rest),
            _ => QuoteRx.IsMatch(rest),
        };

        private static string Remove(string rest, LinePrefix kind) => kind switch
        {
            LinePrefix.Heading1 or LinePrefix.Heading2 or LinePrefix.Heading3 => HeadingRx.Replace(rest, "", 1),
            LinePrefix.Quote => QuoteRx.Replace(rest, "", 1),
            _ => AnyListRx.Replace(rest, "", 1),
        };

        private static string Add(string rest, LinePrefix kind, int number) => kind switch
        {
            LinePrefix.Heading1 => "# " + HeadingRx.Replace(rest, "", 1),
            LinePrefix.Heading2 => "## " + HeadingRx.Replace(rest, "", 1),
            LinePrefix.Heading3 => "### " + HeadingRx.Replace(rest, "", 1),
            LinePrefix.Bullet => "- " + AnyListRx.Replace(rest, "", 1),
            LinePrefix.Numbered => number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " + AnyListRx.Replace(rest, "", 1),
            LinePrefix.Task => "- [ ] " + AnyListRx.Replace(rest, "", 1),
            _ => "> " + rest,
        };

        private static int HeadingLevel(string rest)
        {
            var m = HeadingRx.Match(rest);
            return m.Success ? m.Groups[1].Length : 0;
        }

        /// <summary>A line's leading spaces and tabs, and the rest.</summary>
        private static (string Indent, string Rest) Split(string line)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            return (line.Substring(0, i), line.Substring(i));
        }

        /// <summary>
        /// The whole lines a selection touches. A selection that ends right after a line break does
        /// not take the next line.
        /// </summary>
        private static (int Start, int End) LineBlock(string text, int start, int length)
        {
            int end = start + length;
            if (length > 0 && text[end - 1] == '\n')
            {
                end--;
                if (end > start && text[end - 1] == '\r') end--;
            }
            else if (length > 0 && text[end - 1] == '\r')
            {
                end--;
            }
            int blockStart = start == 0 ? 0 : text.LastIndexOfAny(LineBreaks, start - 1) + 1;
            int lineEnd = text.IndexOfAny(LineBreaks, Math.Max(end, blockStart));
            return (blockStart, lineEnd < 0 ? text.Length : lineEnd);
        }

        /// <summary>Splits a block into lines, remembering each line break exactly (CRLF, LF or CR).</summary>
        private static (List<string> Lines, List<string> Breaks) SplitLines(string block)
        {
            var lines = new List<string>();
            var breaks = new List<string>();
            int lineStart = 0;
            int i = 0;
            while (i < block.Length)
            {
                char c = block[i];
                if (c != '\r' && c != '\n') { i++; continue; }
                int width = c == '\r' && i + 1 < block.Length && block[i + 1] == '\n' ? 2 : 1;
                lines.Add(block.Substring(lineStart, i - lineStart));
                breaks.Add(block.Substring(i, width));
                i += width;
                lineStart = i;
            }
            lines.Add(block.Substring(lineStart));
            return (lines, breaks);
        }

        private static string Join(List<string> lines, List<string> breaks)
        {
            var sb = new StringBuilder(lines[0]);
            for (int i = 1; i < lines.Count; i++) sb.Append(breaks[i - 1]).Append(lines[i]);
            return sb.ToString();
        }

        /// <summary>The note's line ending: its first line break, or CRLF for a note without one yet.</summary>
        private static string NewlineOf(string text)
        {
            int lf = text.IndexOf('\n');
            if (lf < 0) return text.Contains('\r') ? "\r" : "\r\n";
            return lf > 0 && text[lf - 1] == '\r' ? "\r\n" : "\n";
        }
    }
}
```

Note: `Prefix_toggles_on_every_selected_line` case `("- item", Task)`: `Has(TaskRx)` is false, so the task prefix is added and the bullet it replaces is removed by `AnyListRx` — giving `- [ ] item`.

- [ ] **Step 4: Add the Format group in `Pad/MicaPadWindow.xaml.cs`**

1. In `FillEditorMenu`, directly after the line that adds *Select all*, insert:

```csharp
            if (!readOnly && ReferenceEquals(editor, Editor) && ReferenceEquals(_resolved.Effective, PadLanguages.Markdown))
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(BuildFormatMenu(editor));
            }
```

2. Add after `FillEditorMenu`:

```csharp
        /// <summary>Format ▸ for a Markdown tab (spec 2.4): each item is one undoable edit; no new shortcuts.</summary>
        private MenuItem BuildFormatMenu(ICSharpCode.AvalonEdit.TextEditor editor)
        {
            var format = new MenuItem { Header = "Format" };
            void Add(string header, Func<string, int, int, TextEdit> edit) =>
                format.Items.Add(Item(header, null, () =>
                    ApplyEdit(editor, edit(editor.Document.Text, editor.SelectionStart, editor.SelectionLength))));

            Add("Bold", (t, s, l) => MarkdownFormatter.Wrap(t, s, l, "**"));
            Add("Italic", (t, s, l) => MarkdownFormatter.Wrap(t, s, l, "*"));
            Add("Strikethrough", (t, s, l) => MarkdownFormatter.Wrap(t, s, l, "~~"));
            Add("Code", (t, s, l) => MarkdownFormatter.Wrap(t, s, l, "`"));
            Add("Link", MarkdownFormatter.Link);
            format.Items.Add(new Separator());
            Add("Heading 1", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Heading1));
            Add("Heading 2", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Heading2));
            Add("Heading 3", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Heading3));
            format.Items.Add(new Separator());
            Add("Bullet list", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Bullet));
            Add("Numbered list", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Numbered));
            Add("Task", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Task));
            Add("Quote", (t, s, l) => MarkdownFormatter.Prefix(t, s, l, LinePrefix.Quote));
            Add("Code block", MarkdownFormatter.CodeBlock);
            return format;
        }

        /// <summary>Applies an edit as one undoable change and selects what it says.</summary>
        internal static void ApplyEdit(ICSharpCode.AvalonEdit.TextEditor editor, TextEdit edit)
        {
            editor.Document.Replace(edit.Offset, edit.Length, edit.Text);
            editor.Select(edit.SelectionStart, edit.SelectionLength);
        }
```

- [ ] **Step 5: Run the focused tests, then the whole suite** — expected PASS.

- [ ] **Step 6: Document it**

In `GUIDE.md`, at the end of the `### Markdown` section (before the blank line that ends it), add:

```markdown
Right-click → **Format** wraps the selection in bold, italic, strikethrough, code or a link, or
turns the selected lines into headings, lists, tasks, quotes or a code block — choose it again to
undo the formatting. Each is a single **Ctrl+Z**.
```

- [ ] **Step 7: Commit**

```bash
git add Services/Pad/MarkdownFormatter.cs Pad/MicaPadWindow.xaml.cs GUIDE.md tests/Kil0bitSystemMonitor.Tests/MarkdownFormatterTests.cs tests/Kil0bitSystemMonitor.Tests/PadMenuTests.cs
git commit -m "feat(pad): Format menu for Markdown - wrap, link, headings, lists, quotes, code" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

### Task 8: Folding

**Files:**
- Create: `Services/Pad/BraceFolding.cs`, `Services/Pad/HeadingFolding.cs`
- Create: `Pad/FoldingController.cs`
- Modify: `Pad/EditorLanguage.cs` (constructor gains `folds`; install/remove folding)
- Modify: `Pad/MicaPadWindow.xaml.cs` (construct the two `EditorLanguage`s with `folds`; fold-margin colors in `ApplyTheme`)
- Modify: `GUIDE.md`, `README.md`
- Create: `tests/Kil0bitSystemMonitor.Tests/PadFoldingTests.cs`

**Interfaces:**
- Consumes: `PadFoldKind`, `PadLanguage.Fold` (Task 1); `FenceTracker`, `MdFence` (Task 5); `EditorLanguage` (Task 6).
- Produces: `public readonly record struct FoldRange(int Start, int End)` (End exclusive); `public sealed record BraceSyntax(IReadOnlyList<string> LineComments, IReadOnlyList<(string Open, string Close)> BlockComments, IReadOnlyList<char> Quotes)` with `For(string languageId)`; `BraceFolding.Compute(string, BraceSyntax)`; `HeadingFolding.Compute(string)`; `internal sealed class FoldingController` (`Attach(PadLanguage)`, `Detach()`, `Update()`, `IsActive`, `Manager`); `EditorLanguage(TextEditor editor, Func<PadPalette> palette, bool folds)` and `EditorLanguage.Folding`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/PadFoldingTests.cs`:

```csharp
using System.Linq;
using ICSharpCode.AvalonEdit.Folding;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Where text folds — braces, headings, fences — and the fold margin in the window.</summary>
    public class PadFoldingTests
    {
        private static string[] Folded(string text, System.Collections.Generic.IReadOnlyList<FoldRange> folds) =>
            folds.Select(f => text.Substring(f.Start, f.End - f.Start)).ToArray();

        [Fact]
        public void Multi_line_braces_fold_and_single_line_ones_do_not()
        {
            string json = "{\n  \"a\": [1, 2],\n  \"b\": [\n    3\n  ]\n}";
            var folds = BraceFolding.Compute(json, BraceSyntax.For("json"));
            Assert.Equal(new[] { json, "[\n    3\n  ]" }, Folded(json, folds));
        }

        [Fact]
        public void Braces_in_strings_and_comments_are_ignored()
        {
            string cs = "void F() {\n  var s = \"{\";\n  // {\n  /* {\n  */ char c = '{';\n}";
            var folds = BraceFolding.Compute(cs, BraceSyntax.For("csharp"));
            Assert.Single(folds);
            Assert.Equal(cs.IndexOf('{'), folds[0].Start);
            Assert.Equal(cs.Length, folds[0].End);
        }

        [Fact]
        public void PowerShell_block_comments_and_hash_comments_are_ignored()
        {
            string ps = "function F {\n  # {\n  <# {\n  #>\n}";
            var folds = BraceFolding.Compute(ps, BraceSyntax.For("powershell"));
            Assert.Single(folds);
        }

        [Fact]
        public void Mismatched_brackets_never_throw_and_folds_come_sorted()
        {
            string text = "{\n [\n }\n ]\n{\n{\n}\n}";
            var folds = BraceFolding.Compute(text, BraceSyntax.For("javascript"));
            Assert.Equal(folds.OrderBy(f => f.Start).ToList(), folds.ToList());
            Assert.Empty(BraceFolding.Compute("", BraceSyntax.For("json")));
            Assert.Empty(BraceFolding.Compute("\"unclosed {\n", BraceSyntax.For("json")));
        }

        [Fact]
        public void Headings_fold_to_the_next_heading_of_the_same_or_higher_level()
        {
            string md = "# A\ntext\n## B\nmore\n\n# C\nend";
            var folds = HeadingFolding.Compute(md);
            Assert.Equal(new[] { new FoldRange(3, 18), new FoldRange(13, 18), new FoldRange(23, 27) }, folds);
        }

        [Fact]
        public void A_heading_with_nothing_under_it_does_not_fold()
        {
            Assert.Empty(HeadingFolding.Compute("# A\n# B"));
            Assert.Empty(HeadingFolding.Compute("# A\n\n\n# B"));
        }

        [Fact]
        public void Fenced_blocks_fold_and_hide_their_hashes()
        {
            string md = "x\n```\n# not a heading\n```";
            var folds = HeadingFolding.Compute(md);
            Assert.Equal(new[] { new FoldRange(5, md.Length) }, folds);
        }

        [Fact]
        public void A_json_file_gets_a_fold_margin_and_plain_text_does_not() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            PadLanguageWindowTests.OpenFile(window, env, "a.json", "{\n  \"a\": [\n    1\n  ]\n}");
            var folding = window.LanguageView.Folding!;
            folding.Update();

            Assert.True(folding.IsActive);
            Assert.Equal(2, folding.Manager!.AllFoldings.Count());
            Assert.Single(window.Editor.TextArea.LeftMargins.OfType<FoldingMargin>());

            window.ChooseLanguage(env.Workspace.Active!, "plain");
            Assert.False(folding.IsActive);
            Assert.Empty(window.Editor.TextArea.LeftMargins.OfType<FoldingMargin>());
        });

        [Fact]
        public void Moving_the_caret_into_a_fold_opens_it() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            PadLanguageWindowTests.OpenFile(window, env, "a.json", "{\n  \"a\": 1,\n  \"b\": 2\n}");
            var folding = window.LanguageView.Folding!;
            folding.Update();
            var fold = folding.Manager!.AllFoldings.Single();
            fold.IsFolded = true;

            window.Editor.CaretOffset = window.Editor.Document.Text.IndexOf("\"b\"", System.StringComparison.Ordinal);

            Assert.False(fold.IsFolded);
        });

        [Fact]
        public void Switching_tabs_folds_the_new_document() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "# A\ntext\n# B\nmore";
            var folding = window.LanguageView.Folding!;
            folding.Update();
            Assert.Equal(2, folding.Manager!.AllFoldings.Count());

            PadLanguageWindowTests.OpenFile(window, env, "a.json", "{\n  \"a\": 1\n}");
            folding.Update();
            Assert.Single(folding.Manager!.AllFoldings);
        });
    }
}
```

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~PadFoldingTests"`). Expected: build FAILS.

- [ ] **Step 3: Write `Services/Pad/BraceFolding.cs`**

```csharp
using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>A range of text that folds; <see cref="End"/> is exclusive.</summary>
    public readonly record struct FoldRange(int Start, int End);

    /// <summary>What a brace-folding language calls comments and strings, so braces inside them are skipped.</summary>
    public sealed record BraceSyntax(IReadOnlyList<string> LineComments, IReadOnlyList<(string Open, string Close)> BlockComments, IReadOnlyList<char> Quotes)
    {
        public static BraceSyntax CLike { get; } = new(new[] { "//" }, new[] { ("/*", "*/") }, new[] { '"', '\'', '`' });
        public static BraceSyntax Json { get; } = new(Array.Empty<string>(), Array.Empty<(string, string)>(), new[] { '"' });
        public static BraceSyntax Css { get; } = new(Array.Empty<string>(), new[] { ("/*", "*/") }, new[] { '"', '\'' });
        public static BraceSyntax PowerShell { get; } = new(new[] { "#" }, new[] { ("<#", "#>") }, new[] { '"', '\'' });

        /// <summary>The syntax of a brace-folding language (spec 2.5).</summary>
        public static BraceSyntax For(string languageId) => languageId switch
        {
            "json" => Json,
            "css" => Css,
            "powershell" => PowerShell,
            _ => CLike,
        };
    }

    /// <summary>
    /// Fold ranges for every <c>{ }</c> and <c>[ ]</c> pair that spans more than one line, skipping
    /// braces in strings and comments with a small lexer. Mismatched brackets are dropped, never
    /// thrown on. Ranges come sorted by start, as AvalonEdit's FoldingManager requires.
    /// </summary>
    public static class BraceFolding
    {
        public static IReadOnlyList<FoldRange> Compute(string text, BraceSyntax syntax)
        {
            var folds = new List<FoldRange>();
            var open = new Stack<(char Brace, int Offset, int Line)>();
            int line = 0;
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '\n') { line++; i++; continue; }

                if (TryBlockComment(text, i, syntax, out int blockEnd))
                {
                    line += CountLines(text, i, blockEnd);
                    i = blockEnd;
                    continue;
                }
                if (StartsWithAny(text, i, syntax.LineComments))
                {
                    int newline = text.IndexOf('\n', i);
                    i = newline < 0 ? text.Length : newline;
                    continue;
                }
                if (Contains(syntax.Quotes, c))
                {
                    int stop = SkipString(text, i, c);
                    line += CountLines(text, i, stop);
                    i = stop;
                    continue;
                }

                if (c is '{' or '[')
                {
                    open.Push((c, i, line));
                }
                else if (c is '}' or ']')
                {
                    char opener = c == '}' ? '{' : '[';
                    while (open.Count > 0 && open.Peek().Brace != opener) open.Pop();
                    if (open.Count > 0)
                    {
                        var start = open.Pop();
                        if (start.Line != line) folds.Add(new FoldRange(start.Offset, i + 1));
                    }
                }
                i++;
            }
            folds.Sort((a, b) => a.Start.CompareTo(b.Start));
            return folds;
        }

        private static bool TryBlockComment(string text, int i, BraceSyntax syntax, out int end)
        {
            foreach (var (openMark, closeMark) in syntax.BlockComments)
            {
                if (string.CompareOrdinal(text, i, openMark, 0, openMark.Length) != 0) continue;
                int close = text.IndexOf(closeMark, i + openMark.Length, StringComparison.Ordinal);
                end = close < 0 ? text.Length : close + closeMark.Length;
                return true;
            }
            end = i;
            return false;
        }

        private static bool StartsWithAny(string text, int i, IReadOnlyList<string> prefixes)
        {
            foreach (string prefix in prefixes)
                if (string.CompareOrdinal(text, i, prefix, 0, prefix.Length) == 0) return true;
            return false;
        }

        private static bool Contains(IReadOnlyList<char> chars, char c)
        {
            foreach (char x in chars) if (x == c) return true;
            return false;
        }

        /// <summary>
        /// The offset just past a string starting at <paramref name="i"/>. Backslash escapes; an
        /// unclosed ' or " string ends at the line's end (so one stray quote cannot swallow the file);
        /// backtick strings may span lines.
        /// </summary>
        private static int SkipString(string text, int i, char quote)
        {
            int j = i + 1;
            while (j < text.Length)
            {
                char c = text[j];
                if (c == '\\') { j += 2; continue; }
                if (c == quote) return j + 1;
                if (c == '\n' && quote != '`') return j;
                j++;
            }
            return text.Length;
        }

        private static int CountLines(string text, int from, int to)
        {
            int count = 0;
            for (int k = from; k < to && k < text.Length; k++) if (text[k] == '\n') count++;
            return count;
        }
    }
}
```

- [ ] **Step 4: Write `Services/Pad/HeadingFolding.cs`**

```csharp
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Markdown folds: a heading folds from the end of its line to the last non-blank line before
    /// the next heading of the same or higher level; a fenced block folds from the end of its
    /// opening line to the end of its closing line. Headings inside fences are not headings.
    /// </summary>
    public static class HeadingFolding
    {
        private static readonly Regex HeadingRx = new(@"^ {0,3}(#{1,6})(?:[ \t]|$)", RegexOptions.CultureInvariant);

        public static IReadOnlyList<FoldRange> Compute(string text)
        {
            var lines = Lines(text);
            var texts = new string[lines.Count];
            for (int k = 0; k < lines.Count; k++) texts[k] = text.Substring(lines[k].Start, lines[k].End - lines[k].Start);
            var fences = FenceTracker.Classify(texts);
            var folds = new List<FoldRange>();

            for (int i = 0; i < lines.Count; i++)
            {
                int level = fences[i] == MdFence.None ? LevelOf(texts[i]) : 0;
                if (level == 0) continue;
                int last = lines.Count - 1;
                for (int j = i + 1; j < lines.Count; j++)
                {
                    int other = fences[j] == MdFence.None ? LevelOf(texts[j]) : 0;
                    if (other > 0 && other <= level) { last = j - 1; break; }
                }
                while (last > i && string.IsNullOrWhiteSpace(texts[last])) last--;
                if (last > i) folds.Add(new FoldRange(lines[i].End, lines[last].End));
            }

            for (int i = 0; i < lines.Count; i++)
            {
                if (fences[i] != MdFence.Delimiter) continue;
                int close = i + 1;
                while (close < lines.Count && fences[close] != MdFence.Delimiter) close++;
                int last = close < lines.Count ? close : lines.Count - 1;
                if (last > i) folds.Add(new FoldRange(lines[i].End, lines[last].End));
                i = close;
            }

            folds.Sort((a, b) => a.Start.CompareTo(b.Start));
            return folds;
        }

        private static int LevelOf(string line)
        {
            var m = HeadingRx.Match(line);
            return m.Success ? m.Groups[1].Length : 0;
        }

        /// <summary>Each line's start and end (before its line break).</summary>
        private static List<(int Start, int End)> Lines(string text)
        {
            var lines = new List<(int, int)>();
            int start = 0;
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c != '\r' && c != '\n') { i++; continue; }
                lines.Add((start, i));
                i += c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                start = i;
            }
            lines.Add((start, text.Length));
            return lines;
        }
    }
}
```

- [ ] **Step 5: Write `Pad/FoldingController.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Folding for one editor (spec 2.5): installs AvalonEdit's FoldingManager (and so the fold
    /// margin) only for a language that folds, and recomputes 500 ms after the last edit. The
    /// installed manager itself opens a fold the caret moves into (AvalonEdit's
    /// FoldingManagerInstallation), so Find, Go to line and restore never leave it hidden. Fold
    /// state is not saved.
    /// </summary>
    internal sealed class FoldingController
    {
        private readonly TextEditor _editor;
        private readonly DispatcherTimer _timer;
        private FoldingManager? _manager;
        private TextDocument? _document;
        private PadLanguage _language = PadLanguages.Plain;

        public FoldingController(TextEditor editor)
        {
            _editor = editor;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (s, e) =>
            {
                _timer.Stop();
                Update();
            };
        }

        /// <summary>True while folding is installed.</summary>
        internal bool IsActive => _manager != null;

        /// <summary>The manager while folding is installed; for tests.</summary>
        internal FoldingManager? Manager => _manager;

        /// <summary>Installs folding for <paramref name="language"/> on the editor's current document, or nothing if it does not fold.</summary>
        public void Attach(PadLanguage language)
        {
            Detach();
            _language = language;
            if (language.Fold == PadFoldKind.None) return;

            _manager = FoldingManager.Install(_editor.TextArea);
            _document = _editor.Document;
            _document.Changed += OnChanged;
            Update();
        }

        /// <summary>Removes folding and its margin.</summary>
        public void Detach()
        {
            _timer.Stop();
            if (_document != null) _document.Changed -= OnChanged;
            _document = null;
            if (_manager != null)
            {
                FoldingManager.Uninstall(_manager);
                _manager = null;
            }
        }

        /// <summary>Recomputes the folds now. A failure is logged and leaves the old folds in place.</summary>
        internal void Update()
        {
            if (_manager == null || _document == null) return;
            try
            {
                _manager.UpdateFoldings(Compute(_document, _language), -1);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Xml.XmlException)
            {
                DiagnosticsLog.Warn("pad", "Folding could not be updated (" + ex.GetType().Name + ")");
            }
        }

        private void OnChanged(object? sender, DocumentChangeEventArgs e)
        {
            _timer.Stop();
            _timer.Start();
        }

        private static IEnumerable<NewFolding> Compute(TextDocument document, PadLanguage language) => language.Fold switch
        {
            PadFoldKind.Xml => new XmlFoldingStrategy().CreateNewFoldings(document, out _),
            PadFoldKind.Braces => BraceFolding.Compute(document.Text, BraceSyntax.For(language.Id)).Select(f => new NewFolding(f.Start, f.End)),
            PadFoldKind.Headings => HeadingFolding.Compute(document.Text).Select(f => new NewFolding(f.Start, f.End)),
            _ => Array.Empty<NewFolding>(),
        };
    }
}
```

- [ ] **Step 6: Fold with the language in `Pad/EditorLanguage.cs`**

1. Add a field `private readonly FoldingController? _folding;` and change the constructor to:

```csharp
        public EditorLanguage(TextEditor editor, Func<PadPalette> palette, bool folds)
        {
            _editor = editor;
            _palette = palette;
            _folding = folds ? new FoldingController(editor) : null;
        }

        /// <summary>The folding of this editor, or null when it never folds (the history preview).</summary>
        internal FoldingController? Folding => _folding;
```

2. In `Apply`, just before `Redraw();`, add `_folding?.Attach(language);`.
3. At the start of `Clear()`, add `_folding?.Detach();`.
4. Update the class summary: "…syntax colors, or Markdown formatting (colorizer, background, bullets), and folding."

- [ ] **Step 7: Window changes in `Pad/MicaPadWindow.xaml.cs`**

1. Construct the two languages with folding on for the editor only:

```csharp
            _language = new EditorLanguage(Editor, () => _palette, folds: true);
            _previewLanguage = new EditorLanguage(PreviewEditor, () => _palette, folds: false);
```

2. In `ApplyTheme()`, before `_language.Redraw();`, color the fold margin (these AvalonEdit attached properties are inherited by the margin):

```csharp
            Editor.SetValue(ICSharpCode.AvalonEdit.Folding.FoldingMargin.FoldingMarkerBrushProperty, PadThemeApplier.ToBrush(_palette.LineNumbers));
            Editor.SetValue(ICSharpCode.AvalonEdit.Folding.FoldingMargin.FoldingMarkerBackgroundBrushProperty, PadThemeApplier.ToBrush(_palette.Background));
            Editor.SetValue(ICSharpCode.AvalonEdit.Folding.FoldingMargin.SelectedFoldingMarkerBrushProperty, PadThemeApplier.ToBrush(_palette.Accent));
            Editor.SetValue(ICSharpCode.AvalonEdit.Folding.FoldingMargin.SelectedFoldingMarkerBackgroundBrushProperty, PadThemeApplier.ToBrush(_palette.Background));
```

- [ ] **Step 8: Run the focused tests, then the whole suite** — expected PASS.

- [ ] **Step 9: Document it**

In `GUIDE.md`, at the end of `### Colors for code, logs and settings files`, add:

```markdown
JSON, C#, JavaScript, CSS, C/C++, Java, PHP and PowerShell fold at braces, XML and HTML at tags,
and Markdown at headings and code blocks: click the arrows in the margin. Moving to text inside a
fold (Find, Go to line) opens it.
```

In `README.md`, English list, after the *Markdown as you type* bullet:

```markdown
* **Folding**: collapse braces, tags, Markdown sections and code blocks from the margin
```

Thai list, after the *Markdown ขณะพิมพ์* bullet:

```markdown
* **ย่อ/ขยายโค้ด**: ย่อส่วนในวงเล็บปีกกา แท็ก หัวข้อ Markdown และบล็อกโค้ดได้จากขอบซ้าย
```

- [ ] **Step 10: Commit**

```bash
git add Services/Pad/BraceFolding.cs Services/Pad/HeadingFolding.cs Pad/FoldingController.cs Pad/EditorLanguage.cs Pad/MicaPadWindow.xaml.cs GUIDE.md README.md tests/Kil0bitSystemMonitor.Tests/PadFoldingTests.cs
git commit -m "feat(pad): folding for braces, tags, Markdown headings and code blocks" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

## After the plan (controller)

1. Whole suite green; build with 0 warnings.
2. Deploy for the owner's e2e (standing rule): stop MicaStats, build Release into `bin\Release\net8.0-windows`, relaunch.
3. Owner's manual checks for Part 2 (spec *Testing → Manual* item 3): a real note with headings, lists, tasks, code, quotes and links; typing stays smooth in a 2 MB note; `.json`, `.cs`, `.ps1`, `.log`, `.ini` files open colored in both themes; folding.
