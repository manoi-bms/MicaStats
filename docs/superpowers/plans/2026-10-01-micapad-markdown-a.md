# MicaPad Markdown like Wiki.js — Plan A (styling) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make MicaPad's Markdown tabs show code, tables, quotes, callouts and Wiki.js inline syntax the way Wiki.js does — reading font for prose, monospace and syntax colors for code, lined-up tables — without ever changing the text on its own.

**Architecture:** A per-document scan (`MarkdownStructure`, pure) adds what a line cannot know alone (tables, setext headings, front matter, `$$` blocks, callouts, abbreviation terms) to the existing fence classification; `MarkdownDocumentCache` keeps it per editor. The line tokenizer takes those facts and emits new styles; `MarkdownStyles` maps them to looks (now with monospace, baseline and dotted underline); the colorizer and background renderer draw them; `FenceHighlighter` reuses MicaPad's language highlighting inside fences; `TableFormatter` and `EmojiGenerator` add Format table and emoji. Plan B (spec parts 6–7: math, the `kroki` form, image previews, Settings cards, guide) follows after this plan lands.

**Tech Stack:** .NET 8 WPF; AvalonEdit 6.3.1.120 (`DocumentColorizingTransformer`, `HighlightingEngine`, `FormattedTextElement`); gemoji 4.1.0 data (MIT); xUnit.

**Spec:** `docs/superpowers/specs/2026-10-01-micapad-markdown-wikijs-design.md` (parts 1–5)

## Global Constraints

- Repo `C:\AIProject\kil0bit-system-monitor`, branch `feat/micapad-markdown` (from main 9cb6ee4). Commit on it; never push, merge or tag.
- Build and test only with `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe"`. Full suite: `timeout 500 env DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests 2>&1 | tail -5`. Known flaky: `McpPipeTests.At_most_four_calls_run_at_once_and_the_rest_wait_their_turn` — re-run once if it alone fails.
- Never build or publish into `bin\Release` (the controller deploys). Never launch MicaStats. Tests use temp folders only, never `%APPDATA%`, no network; UI tests only on the shared `UiThread.Run`, never shown.
- New code under `Services/` holds no WPF types. Files use LF line endings; keep them.
- The text is never changed except by an explicit command (Format table). Markers stay visible (dimmed).
- Non-Markdown tabs keep their font and look. The history preview keeps today's look (no reading font).
- Exact values (verbatim from the spec): reading font "Segoe UI Variable Text" with fallback "Segoe UI"; `AppConfig.PadReadingFont` default true; `MdCode` dark `#F2A97B` light `#A33D1F`; a fenced block over 2,000 lines or a line over 4,000 characters is not colored; front matter only when line 1 is `---` and a closing `---`/`...` is within the first 200 lines; quote bars 3 px wide, 8 px apart, up to 6 levels; callout tint = the callout color at 15% (alpha `0x26`); small text (sub/superscript, footnote references) at 0.75 of the size.
- C# string escapes: write non-ASCII as `\uXXXX` escapes. The Write/Edit tools can decode them into raw characters; after writing a `.cs` file run `LC_ALL=C grep -n '[^[:print:][:space:]]' <file>` and turn any raw non-ASCII character inside a string literal back into its escape (PowerShell `[IO.File]::ReadAllText` / `.Replace` / `[IO.File]::WriteAllText(path, text, [Text.UTF8Encoding]::new($false))`). Data files (`Emoji.tsv`) hold raw UTF-8 on purpose.
- `UseWindowsForms` is on: `Brush`, `Brushes`, `Color`, `FontFamily`, `Pen`, `Point`, `Size` and others are ambiguous in WPF files and tests — add `using X = System.Windows...;` aliases as existing files do.
- Commits: `git add` exact paths, `git commit -m "..." -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"`, never amend.
- Never weaken an existing assertion. When an existing test uses a renamed member (`FencesChanged`), change only the name.
- Do not dispatch subagents.

## Rulings on the spec

- **R1 — Copy as RTF follows the new looks.** `RtfRuns` reads `MarkdownStyles.LookOf`, so inline code becomes `MdCode`-colored and sub/superscript smaller in RTF too. The copied text is unchanged; only its styling follows the editor. Cost if wrong: one color in pasted RTF.
- **R2 — monospace by block, not by span.** Fence, table and front-matter lines get the monospace font for the whole line from the colorizer (by the line's block), so the tokenizer's fence spans stay exactly as today (existing tests pin them).
- **R3 — when the structure is rescanned.** On a line-count change; on inserted or removed text holding any of `` ` ~ | $ = - > { [ * ``; when an edited line is a fence delimiter, a table header, a setext heading or underline, front matter or a callout class line; when an edited line starts (after up to 3 spaces) with one of those characters; or when the line after an edited line starts with `=` or `-` (a setext underline may now apply). Typing inside a paragraph, a fenced block or a table cell rescans nothing.
- **R4 — inline extras are parsed only outside code.** Inside inline code, math and `<kbd>` nothing else is styled; abbreviation underlines skip code, math, keys and markers.
- **R5 — emoji glyphs are monochrome** (WPF has no color-font support) and come from the font fallback (Segoe UI Emoji).

## Review Focus

1. Typing prose in a long note, inside a fenced block or inside a table cell: no rescan of the whole note (Task 2 test `Typing_inside_a_paragraph_a_fence_or_a_table_cell_does_not_rescan`).
2. Text that only looks like syntax stays text: "$5 and $10", "a < b", "x^ 2^", "a~b", "12:30:45", "snake_case" (Task 3 test `Text_that_only_looks_like_syntax_stays_text`, Task 7 test `Emoji_need_a_known_name_between_colons`).
3. A `---` right under a paragraph line is a setext heading; after a blank line it is a rule (Task 2 test `A_dash_line_under_text_is_a_heading_and_after_a_blank_line_a_rule`).
4. Format table with Thai, Chinese and an escaped pipe: columns line up by display width and no character is lost (Task 6 test `Wide_and_combining_characters_and_escaped_pipes_line_up_and_survive`).
5. Reading font off, or a JSON tab: the editor is all monospace again (Task 4 test `The_reading_font_is_for_markdown_tabs_only_and_follows_the_setting`).

---

## File Structure

| File | Task | Responsibility |
|---|---|---|
| Create `Services/Pad/FenceLanguages.cs` | 1 | Fence word → MicaPad language id |
| Modify `Services/Pad/FenceTracker.cs` | 1, 2 | `InfoWord` (1); `$$` blocks (2) |
| Modify `Services/Pad/DiagramKinds.cs` | 1 | `FromFence` uses `FenceTracker.InfoWord` |
| Modify `Services/Pad/PadPalette.cs` | 1 | `MdCode`, `MdMath`, `MdKbdBackground`, `MdCallout*` |
| Modify `Services/Pad/MarkdownStyles.cs` | 1 | `MdLook` gains `Mono`, `Baseline`, `Dotted`; new looks; `CalloutColor` |
| Modify `Services/Pad/MarkdownLineTokenizer.cs` | 1, 3 | New `MdStyle` values (1); facts, new blocks, inline extras (3) |
| Create `Services/Pad/TableCells.cs` | 2 | Splitting table lines into cells; pipe offsets |
| Create `Services/Pad/MarkdownStructure.cs` | 2 | `MdLineFacts`, `MdTableRole`, `MdCallout`, the document scan |
| Modify `Pad/MarkdownDocumentCache.cs`, `Pad/EditorLanguage.cs` | 2 | Structure per editor; rescan rule; `StructureChanged` |
| Modify `Pad/MarkdownColorizer.cs`, `Pad/MarkdownBackgroundRenderer.cs` | 4 | Fonts, baselines, dotted underline; quote levels, callouts, front matter |
| Modify `Models/SystemMetrics.cs`, `Pad/MicaPadWindow.xaml.cs`, `Pad/EditorLanguage.cs` | 4 | `PadReadingFont`; the reading font |
| Create `Pad/SyntaxPaint.cs`, `Pad/FenceHighlighter.cs`; modify `Pad/ThemedHighlightingColorizer.cs`, `Pad/MarkdownColorizer.cs` | 5 | Colors inside fences |
| Create `Services/Pad/TableFormatter.cs`; modify `Pad/EditorMenus.cs` | 6 | Format table |
| Create `Services/Pad/Emoji.tsv`, `Services/Pad/Emoji.cs`, `Pad/EmojiGenerator.cs`; modify `Kil0bitSystemMonitor.csproj`, `Pad/EditorLanguage.cs` | 7 | Emoji |
| Tests | | `FenceLanguagesTests`, `MarkdownStructureTests`, `MarkdownExtrasTests`, `MarkdownLookTests`, `FenceColorsTests`, `TableFormatterTests`, `EmojiTests`; modified `PadPaletteTests`, `MarkdownRenderingTests` |

---

### Task 1: Fence languages, new colors and looks

**Files:**
- Create: `Services/Pad/FenceLanguages.cs`
- Modify: `Services/Pad/FenceTracker.cs` (add `InfoWord`), `Services/Pad/DiagramKinds.cs` (`FromFence` uses it)
- Modify: `Services/Pad/PadPalette.cs`, `Services/Pad/MarkdownStyles.cs`, `Services/Pad/MarkdownLineTokenizer.cs` (enum `MdStyle` only)
- Test: `tests/Kil0bitSystemMonitor.Tests/FenceLanguagesTests.cs`, `tests/Kil0bitSystemMonitor.Tests/MarkdownLookTests.cs`, `tests/Kil0bitSystemMonitor.Tests/PadPaletteTests.cs`

**Interfaces:**
- Consumes: `PadLanguages.ById(string?)`, `PadHighlighting.For(PadLanguage)` (existing).
- Produces:
  - `FenceTracker.InfoWord(string openingLine) : string?` — first word of a backtick/tilde fence's info string.
  - `static class FenceLanguages` — `IEnumerable<string> Words`, `string? IdOf(string? word)`, `string? IdOfFence(string openingLine)` (returns `PadLanguages` ids).
  - `PadPalette.MdCode`, `MdMath`, `MdKbdBackground`, `MdCalloutInfo`, `MdCalloutSuccess`, `MdCalloutWarning`, `MdCalloutDanger`.
  - `enum MdBaseline { Normal, Superscript, Subscript }`; `MdLook(... , bool Mono = false, MdBaseline Baseline = MdBaseline.Normal, bool Dotted = false)`.
  - `MdStyle` gains (appended, in this order): `TableHeader`, `FootnoteRef`, `Subscript`, `Superscript`, `KbdText`, `Abbreviation`, `MathText`.
  - `MarkdownStyles.SmallSize` (0.75). (`MarkdownStyles.CalloutColor` comes in Task 2, with `MdCallout`.)

- [ ] **Step 1: Create the branch**

```bash
cd /c/AIProject/kil0bit-system-monitor && git checkout -b feat/micapad-markdown
```

- [ ] **Step 2: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/FenceLanguagesTests.cs`:

```csharp
using System.Linq;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The language a fenced block names (spec 1.2).</summary>
    public class FenceLanguagesTests
    {
        [Theory]
        [InlineData("cs", "csharp")]
        [InlineData("C#", "csharp")]
        [InlineData("TSX", "javascript")]
        [InlineData("node", "javascript")]
        [InlineData("json5", "json")]
        [InlineData("xaml", "xml")]
        [InlineData("htm", "html")]
        [InlineData("pwsh", "powershell")]
        [InlineData("py", "python")]
        [InlineData("postgresql", "sql")]
        [InlineData("c++", "cpp")]
        [InlineData("vbnet", "vb")]
        [InlineData("patch", "diff")]
        [InlineData("toml", "ini")]
        [InlineData("yml", "yaml")]
        [InlineData("cmd", "batch")]
        [InlineData("log", "log")]
        public void A_word_names_its_language(string word, string id) => Assert.Equal(id, FenceLanguages.IdOf(word));

        [Theory]
        [InlineData("bash")]
        [InlineData("sh")]
        [InlineData("mermaid")]
        [InlineData("")]
        [InlineData(null)]
        public void Other_words_name_no_language(string? word) => Assert.Null(FenceLanguages.IdOf(word));

        [Fact]
        public void There_are_62_words_and_each_names_a_language_with_colors() => UiThread.Run(() =>
        {
            var words = FenceLanguages.Words.ToList();
            Assert.Equal(62, words.Count);
            foreach (string word in words)
            {
                var language = PadLanguages.ById(FenceLanguages.IdOf(word));
                Assert.True(language != null, word + " names no MicaPad language");
                Assert.True(PadHighlighting.For(language!) != null, word + " has no colors");
            }
        });

        [Theory]
        [InlineData("```cs title", "csharp")]
        [InlineData("~~~ PY", "python")]
        [InlineData("````sql", "sql")]
        [InlineData("```", null)]
        [InlineData("    ```cs", null)]
        [InlineData("$$", null)]
        public void An_opening_fence_names_its_language_by_the_first_word(string line, string? id) =>
            Assert.Equal(id, FenceLanguages.IdOfFence(line));

        [Theory]
        [InlineData("```mermaid flow", "mermaid")]
        [InlineData("  ~~~  dot", "dot")]
        [InlineData("```", null)]
        [InlineData("text", null)]
        public void The_info_word_is_the_first_word_after_the_fence(string line, string? word) =>
            Assert.Equal(word, FenceTracker.InfoWord(line));
    }
}
```

`tests/Kil0bitSystemMonitor.Tests/MarkdownLookTests.cs`:

```csharp
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The looks of the new Markdown styles (spec 1.4, 3, 5).</summary>
    public class MarkdownLookTests
    {
        [Fact]
        public void Inline_code_is_monospace_in_the_code_color_on_the_code_background()
        {
            var dark = MarkdownStyles.LookOf(MdStyle.Code, PadPalette.Dark);
            var light = MarkdownStyles.LookOf(MdStyle.Code, PadPalette.Light);

            Assert.True(dark.Mono);
            Assert.Equal(PadColor.Parse("#F2A97B"), dark.Foreground);
            Assert.Equal(PadPalette.Dark.MdCodeBackground, dark.Background);
            Assert.Equal(PadColor.Parse("#A33D1F"), light.Foreground);
        }

        [Fact]
        public void Small_raised_and_lowered_text_is_three_quarters_size()
        {
            var palette = PadPalette.Dark;

            Assert.Equal(0.75, MarkdownStyles.SmallSize);
            Assert.Equal(MdBaseline.Superscript, MarkdownStyles.LookOf(MdStyle.Superscript, palette).Baseline);
            Assert.Equal(MdBaseline.Subscript, MarkdownStyles.LookOf(MdStyle.Subscript, palette).Baseline);
            var note = MarkdownStyles.LookOf(MdStyle.FootnoteRef, palette);
            Assert.Equal(MdBaseline.Superscript, note.Baseline);
            Assert.Equal(0.75, note.SizeFactor);
            Assert.Equal(palette.MdLink, note.Foreground);
        }

        [Fact]
        public void Keys_tables_abbreviations_and_math_have_their_looks()
        {
            var palette = PadPalette.Light;

            var key = MarkdownStyles.LookOf(MdStyle.KbdText, palette);
            Assert.True(key.Mono);
            Assert.Equal(palette.MdKbdBackground, key.Background);
            Assert.Equal(MdWeight.Bold, MarkdownStyles.LookOf(MdStyle.TableHeader, palette).Weight);
            Assert.True(MarkdownStyles.LookOf(MdStyle.Abbreviation, palette).Dotted);
            Assert.Equal(palette.MdMath, MarkdownStyles.LookOf(MdStyle.MathText, palette).Foreground);
            Assert.False(MarkdownStyles.LookOf(MdStyle.Bold, palette).Mono);
        }
    }
}
```

In `tests/Kil0bitSystemMonitor.Tests/PadPaletteTests.cs`, add to the `rules` array in `ContrastRules()` (after `("Text", "MdCodeBackground", 7),`):

```csharp
                ("MdCode", "MdCodeBackground", 4.5), ("MdMath", "Background", 4.5), ("Text", "MdKbdBackground", 7),
                ("MdCalloutInfo", "Background", 3), ("MdCalloutSuccess", "Background", 3),
                ("MdCalloutWarning", "Background", 3), ("MdCalloutDanger", "Background", 3),
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~FenceLanguagesTests|FullyQualifiedName~MarkdownLookTests|FullyQualifiedName~PadPaletteTests" 2>&1 | tail -5`
Expected: build error, `FenceLanguages` does not exist.

- [ ] **Step 4: Implement**

Add to `Services/Pad/FenceTracker.cs` (after `MayBeDelimiter`):

```csharp
        /// <summary>
        /// The first word of an opening fence line's info string (<c>```cs title</c> gives "cs"), or
        /// null when the line is no backtick or tilde fence (at most three spaces before it) or has
        /// no info string.
        /// </summary>
        public static string? InfoWord(string openingLine)
        {
            string line = openingLine ?? "";
            int i = 0;
            while (i < line.Length && i < 3 && line[i] == ' ') i++;
            if (i >= line.Length || line[i] is not ('`' or '~')) return null;
            char fence = line[i];
            while (i < line.Length && line[i] == fence) i++;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            int start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            return i > start ? line.Substring(start, i - start) : null;
        }
```

In `Services/Pad/DiagramKinds.cs`, replace the body of `FromFence` with:

```csharp
        public static DiagramKind? FromFence(string openingLine) => FromWord(FenceTracker.InfoWord(openingLine));
```

(keep its doc comment; `DiagramKindsTests` must still pass unchanged).

`Services/Pad/FenceLanguages.cs`:

```csharp
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
```

In `Services/Pad/PadPalette.cs`, add the properties after `MdRule`:

```csharp
        /// <summary>Inline code's text (spec 1.4).</summary>
        public PadColor MdCode { get; private init; }
        /// <summary>Inline math <c>$…$</c>.</summary>
        public PadColor MdMath { get; private init; }
        /// <summary>Behind the key text of <c>&lt;kbd&gt;</c>.</summary>
        public PadColor MdKbdBackground { get; private init; }
        /// <summary>Wiki.js callouts' bars; the background tint is the same color at 15%.</summary>
        public PadColor MdCalloutInfo { get; private init; }
        public PadColor MdCalloutSuccess { get; private init; }
        public PadColor MdCalloutWarning { get; private init; }
        public PadColor MdCalloutDanger { get; private init; }
```

in `Dark`, after `MdRule = PadColor.Parse("#33FFFFFF"),`:

```csharp
            MdCode = PadColor.Parse("#F2A97B"),
            MdMath = PadColor.Parse("#C3A6FF"),
            MdKbdBackground = PadColor.Parse("#2A2A36"),
            MdCalloutInfo = PadColor.Parse("#4C9AFF"),
            MdCalloutSuccess = PadColor.Parse("#3FBF7F"),
            MdCalloutWarning = PadColor.Parse("#E8A53C"),
            MdCalloutDanger = PadColor.Parse("#FF6B6B"),
```

in `Light`, after `MdRule = PadColor.Parse("#26000000"),`:

```csharp
            MdCode = PadColor.Parse("#A33D1F"),
            MdMath = PadColor.Parse("#6B3FB5"),
            MdKbdBackground = PadColor.Parse("#E2E2EA"),
            MdCalloutInfo = PadColor.Parse("#1F6FEB"),
            MdCalloutSuccess = PadColor.Parse("#1A7F37"),
            MdCalloutWarning = PadColor.Parse("#9A6700"),
            MdCalloutDanger = PadColor.Parse("#C62828"),
```

and in `Resources()`, after `Pair(nameof(MdRule), MdRule),`:

```csharp
            Pair(nameof(MdCode), MdCode),
            Pair(nameof(MdMath), MdMath),
            Pair(nameof(MdKbdBackground), MdKbdBackground),
            Pair(nameof(MdCalloutInfo), MdCalloutInfo),
            Pair(nameof(MdCalloutSuccess), MdCalloutSuccess),
            Pair(nameof(MdCalloutWarning), MdCalloutWarning),
            Pair(nameof(MdCalloutDanger), MdCalloutDanger),
```

In `Services/Pad/MarkdownLineTokenizer.cs`, append to `enum MdStyle` after `CodeBlock,`:

```csharp
        TableHeader,
        FootnoteRef,
        Subscript,
        Superscript,
        KbdText,
        Abbreviation,
        MathText,
```

In `Services/Pad/MarkdownStyles.cs`:

1. Add after `MdWeight`:

```csharp
    /// <summary>Where a Markdown run sits against the line's baseline.</summary>
    public enum MdBaseline
    {
        Normal,
        Superscript,
        Subscript,
    }
```

2. Replace the `MdLook` declaration with (keep its doc comment, adding "Mono switches to the editor's monospace font while the reading font is on; Dotted adds a dotted underline."):

```csharp
    public readonly record struct MdLook(PadColor? Foreground, PadColor? Background, double SizeFactor, MdWeight Weight, bool Italic, bool Strike,
                                         bool Mono = false, MdBaseline Baseline = MdBaseline.Normal, bool Dotted = false);
```

3. In `MarkdownStyles`, add after `HeadingSizes`:

```csharp
        /// <summary>The size of sub- and superscript and footnote references, as a multiple of the text around them.</summary>
        public const double SmallSize = 0.75;
```

4. In `LookOf`, replace the `MdStyle.Code` arm and add the new arms before the `_` arm:

```csharp
                MdStyle.Code => new MdLook(palette.MdCode, palette.MdCodeBackground, 1, MdWeight.Keep, false, false, Mono: true),
                MdStyle.TableHeader => new MdLook(null, null, 1, MdWeight.Bold, false, false),
                MdStyle.FootnoteRef => new MdLook(palette.MdLink, null, SmallSize, MdWeight.Keep, false, false, Baseline: MdBaseline.Superscript),
                MdStyle.Subscript => new MdLook(null, null, SmallSize, MdWeight.Keep, false, false, Baseline: MdBaseline.Subscript),
                MdStyle.Superscript => new MdLook(null, null, SmallSize, MdWeight.Keep, false, false, Baseline: MdBaseline.Superscript),
                MdStyle.KbdText => new MdLook(null, palette.MdKbdBackground, 1, MdWeight.Keep, false, false, Mono: true),
                MdStyle.Abbreviation => new MdLook(null, null, 1, MdWeight.Keep, false, false, Dotted: true),
                MdStyle.MathText => new MdLook(palette.MdMath, null, 1, MdWeight.Keep, false, false),
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~FenceLanguagesTests|FullyQualifiedName~MarkdownLookTests|FullyQualifiedName~PadPaletteTests|FullyQualifiedName~DiagramKindsTests|FullyQualifiedName~MarkdownTokenizerTests|FullyQualifiedName~PadCopyRtfTests" 2>&1 | tail -5`
Expected: all pass. Then the full suite.

- [ ] **Step 6: Commit**

```bash
git add Services/Pad/FenceLanguages.cs Services/Pad/FenceTracker.cs Services/Pad/DiagramKinds.cs Services/Pad/PadPalette.cs Services/Pad/MarkdownStyles.cs Services/Pad/MarkdownLineTokenizer.cs tests/Kil0bitSystemMonitor.Tests/FenceLanguagesTests.cs tests/Kil0bitSystemMonitor.Tests/MarkdownLookTests.cs tests/Kil0bitSystemMonitor.Tests/PadPaletteTests.cs
git commit -m "feat(pad): fence languages, code/math/key/callout colors and the new Markdown looks" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 2: Document structure

**Files:**
- Modify: `Services/Pad/FenceTracker.cs` (`$$` blocks)
- Create: `Services/Pad/TableCells.cs`, `Services/Pad/MarkdownStructure.cs`
- Modify: `Services/Pad/MarkdownStyles.cs` (`CalloutColor`, `CalloutTintAlpha`)
- Modify: `Pad/MarkdownDocumentCache.cs` (rewrite), `Pad/EditorLanguage.cs` (`FencesChanged` → `StructureChanged`), `tests/Kil0bitSystemMonitor.Tests/MarkdownRenderingTests.cs` (the same rename, one line)
- Test: `tests/Kil0bitSystemMonitor.Tests/MarkdownStructureTests.cs`

**Interfaces:**
- Consumes: `FenceTracker.Classify`, `FenceTracker.Openings`, `MarkdownLineTokenizer.BlockOf(string, MdFence)` (existing).
- Produces:
  - `enum MdTableRole { None, Header, Delimiter, Row }`, `enum MdCallout { None, Info, Success, Warning, Danger }`.
  - `readonly record struct MdLineFacts(MdFence Fence, MdTableRole Table = None, int SetextLevel = 0, bool SetextUnderline = false, bool FrontMatter = false, MdCallout Callout = None, bool CalloutClass = false)` with `bool IsStructural`.
  - `sealed class MarkdownStructure` — `static MarkdownStructure Scan(IReadOnlyList<string> lines)`, `static MarkdownStructure Empty`, `int LineCount`, `MdFence[] Fences`, `int[] Openings`, `int[] Closings`, `int[] BlockOpenings` (inside line → its opening line, 1-based, else 0), `MdLineFacts[] Facts`, `IReadOnlySet<string> Abbreviations`, `static bool IsDelimiterRow(string line, out int columns)`.
  - `static class TableCells` — `List<string> Split(string line)` (outer pipes dropped, split on unescaped `|`, each cell trimmed, `\|` kept in the cell), `List<int> Pipes(string line)` (offsets of unescaped `|`).
  - `MarkdownDocumentCache` — `KindOf`, `OpeningLineOf`, `ClosingLineOf` (unchanged), new `MdLineFacts FactsOf(TextDocument, int lineNumber)`, `int BlockOpeningOf(TextDocument, int lineNumber)`, `IReadOnlySet<string> AbbreviationsOf(TextDocument)`, event `StructureChanged` (was `FencesChanged`), `int Recomputes`.
  - `MarkdownStyles.CalloutColor(MdCallout, PadPalette) : PadColor`, `MarkdownStyles.CalloutTintAlpha` (`0x26`).

- [ ] **Step 1: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/MarkdownStructureTests.cs`:

```csharp
using System.Linq;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>What a line is in its document (spec 2): fences and $$ blocks, tables, setext headings, front matter, callouts, abbreviations.</summary>
    public class MarkdownStructureTests
    {
        private static MarkdownStructure Scan(string text) => MarkdownStructure.Scan(text.Split('\n'));

        [Fact]
        public void Dollar_lines_open_and_close_math_blocks_like_fences()
        {
            var s = Scan("a\n$$\nx^2\n$$\nb\n$$ not a block");

            Assert.Equal(new[] { MdFence.None, MdFence.Delimiter, MdFence.Inside, MdFence.Delimiter, MdFence.None, MdFence.None }, s.Fences);
            Assert.Equal(2, s.Openings[3]);
            Assert.Equal(4, s.Closings[1]);
            Assert.Equal(2, s.BlockOpenings[2]);
            Assert.Equal(0, s.BlockOpenings[4]);
        }

        [Fact]
        public void Inside_lines_know_their_opening_line()
        {
            var s = Scan("```cs\na\nb\n```\n~~~\nc");

            Assert.Equal(new[] { 0, 1, 1, 0, 0, 5 }, s.BlockOpenings);
        }

        [Fact]
        public void Tables_have_a_header_a_delimiter_and_rows()
        {
            var s = Scan("intro\n| a | b |\n|:--|--:|\n| 1 | 2 |\n3 | 4\n\nafter | x");

            Assert.Equal(
                new[] { MdTableRole.None, MdTableRole.Header, MdTableRole.Delimiter, MdTableRole.Row, MdTableRole.Row, MdTableRole.None, MdTableRole.None },
                s.Facts.Select(f => f.Table));
        }

        [Theory]
        [InlineData("a | b\nc | d")]
        [InlineData("a | b\n---")]
        [InlineData("a | b | c\n|---|---|")]
        [InlineData("```\na | b\n|---|---|\n```")]
        [InlineData("> a | b\n> |---|---|")]
        public void Not_every_pipe_line_is_a_table(string text) =>
            Assert.All(Scan(text).Facts, f => Assert.Equal(MdTableRole.None, f.Table));

        [Fact]
        public void A_dash_line_under_text_is_a_heading_and_after_a_blank_line_a_rule()
        {
            var s = Scan("Title\n===\ntext\n---\n\n---\n- item\n---");

            Assert.Equal(1, s.Facts[0].SetextLevel);
            Assert.True(s.Facts[1].SetextUnderline);
            Assert.Equal(2, s.Facts[2].SetextLevel);
            Assert.True(s.Facts[3].SetextUnderline);
            Assert.False(s.Facts[5].SetextUnderline);   // after a blank line: a rule
            Assert.Equal(0, s.Facts[6].SetextLevel);    // a list item is no heading text
            Assert.False(s.Facts[7].SetextUnderline);
        }

        [Fact]
        public void Front_matter_is_the_first_dash_block_within_200_lines()
        {
            var s = Scan("---\ntitle: x\n---\n# H");
            Assert.Equal(new[] { true, true, true, false }, s.Facts.Select(f => f.FrontMatter));

            Assert.All(Scan("---\ntitle: x\n# H").Facts, f => Assert.False(f.FrontMatter));
            Assert.All(Scan("text\n---\na\n---").Facts, f => Assert.False(f.FrontMatter));

            string far = "---\n" + string.Join("\n", Enumerable.Repeat("k: v", 250)) + "\n---";
            Assert.All(Scan(far).Facts, f => Assert.False(f.FrontMatter));
            Assert.True(Scan("---\n" + string.Join("\n", Enumerable.Repeat("k: v", 199)) + "\n...").Facts[0].FrontMatter);
        }

        [Fact]
        public void A_callout_class_marks_the_quote_above_it()
        {
            var s = Scan("> one\n> two\n{.is-warning}\nafter\n{.is-info}");

            Assert.Equal(new[] { MdCallout.Warning, MdCallout.Warning, MdCallout.None, MdCallout.None, MdCallout.None }, s.Facts.Select(f => f.Callout));
            Assert.True(s.Facts[2].CalloutClass);
            Assert.False(s.Facts[4].CalloutClass);   // not under a quote
        }

        [Fact]
        public void Abbreviation_terms_are_collected_outside_fences()
        {
            var s = Scan("*[HTML]: Hyper Text Markup Language\n*[W3C]: World Wide Web Consortium\n```\n*[NO]: x\n```");

            Assert.Equal(new[] { "HTML", "W3C" }, s.Abbreviations.OrderBy(t => t));
        }

        [Fact]
        public void Plain_facts_are_not_structural()
        {
            Assert.False(new MdLineFacts(MdFence.None).IsStructural);
            Assert.True(new MdLineFacts(MdFence.Inside).IsStructural);
            Assert.True(new MdLineFacts(MdFence.None, Table: MdTableRole.Row).IsStructural);
        }

        [Fact]
        public void Table_cells_split_on_unescaped_pipes()
        {
            Assert.Equal(new[] { "a", "b" }, TableCells.Split("| a | b |"));
            Assert.Equal(new[] { "a", "b" }, TableCells.Split("a|b"));
            Assert.Equal(new[] { @"x \| y", "z" }, TableCells.Split(@"| x \| y | z |"));
            Assert.Equal(new[] { 0, 4, 8 }, TableCells.Pipes("| a | b |"));
            Assert.Equal(new[] { 0, 9 }, TableCells.Pipes(@"| a \| b |"));
        }

        [Fact]
        public void Callout_colors_follow_the_kind()
        {
            var palette = PadPalette.Dark;
            Assert.Equal(palette.MdCalloutInfo, MarkdownStyles.CalloutColor(MdCallout.Info, palette));
            Assert.Equal(palette.MdCalloutSuccess, MarkdownStyles.CalloutColor(MdCallout.Success, palette));
            Assert.Equal(palette.MdCalloutWarning, MarkdownStyles.CalloutColor(MdCallout.Warning, palette));
            Assert.Equal(palette.MdCalloutDanger, MarkdownStyles.CalloutColor(MdCallout.Danger, palette));
            Assert.Equal(0x26, MarkdownStyles.CalloutTintAlpha);
        }

        [Fact]
        public void The_cache_gives_facts_and_follows_a_new_table() => UiThread.Run(() =>
        {
            var document = new TextDocument("| a | b |\nnext");
            var cache = new MarkdownDocumentCache();
            int changes = 0;
            cache.StructureChanged += () => changes++;
            Assert.Equal(MdTableRole.None, cache.FactsOf(document, 1).Table);

            document.Insert(document.GetLineByNumber(1).EndOffset, "\n|---|---|");

            Assert.Equal(MdTableRole.Header, cache.FactsOf(document, 1).Table);
            Assert.Equal(MdTableRole.Delimiter, cache.FactsOf(document, 2).Table);
            Assert.Equal(1, changes);
        });

        [Fact]
        public void Typing_inside_a_paragraph_a_fence_or_a_table_cell_does_not_rescan() => UiThread.Run(() =>
        {
            var document = new TextDocument("# T\nsome text\n| a | b |\n|---|---|\n| 1 | 2 |\n```\ncode\n```");
            var cache = new MarkdownDocumentCache();
            cache.FactsOf(document, 1);
            int before = cache.Recomputes;

            document.Insert(document.GetLineByNumber(2).EndOffset, " more");
            document.Insert(document.GetLineByNumber(5).Offset + 3, "0");    // the cell "1" becomes "10"
            document.Insert(document.GetLineByNumber(7).EndOffset, "();");

            Assert.Equal(before, cache.Recomputes);
            Assert.Equal(MdTableRole.Row, cache.FactsOf(document, 5).Table);
        });

        [Fact]
        public void Typing_a_pipe_rescans() => UiThread.Run(() =>
        {
            var document = new TextDocument("a\nb");
            var cache = new MarkdownDocumentCache();
            cache.FactsOf(document, 1);
            int before = cache.Recomputes;

            document.Insert(1, " |");

            Assert.Equal(before + 1, cache.Recomputes);
        });

        [Fact]
        public void Editing_a_definition_term_updates_the_abbreviations() => UiThread.Run(() =>
        {
            var document = new TextDocument("*[HTML]: x\nHTML here");
            var cache = new MarkdownDocumentCache();
            Assert.Contains("HTML", cache.AbbreviationsOf(document));

            document.Insert(6, "X");   // *[HTMLX]: x

            Assert.Equal(new[] { "HTMLX" }, cache.AbbreviationsOf(document));
        });
    }
}
```

In `tests/Kil0bitSystemMonitor.Tests/MarkdownRenderingTests.cs`, change `cache.FencesChanged += () => changes++;` to `cache.StructureChanged += () => changes++;` (nothing else).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~MarkdownStructureTests" 2>&1 | tail -5`
Expected: build error, `MarkdownStructure` does not exist.

- [ ] **Step 3: `$$` blocks in `Services/Pad/FenceTracker.cs`**

1. Add after `OpenRx`:

```csharp
        private static readonly Regex MathRx = new(@"^ {0,3}\$\$[ \t]*$", RegexOptions.CultureInvariant);
```

2. In `Classify`, inside `if (fenceLength == 0)`, right after `if (!MayBeDelimiter(line)) continue;`, add:

```csharp
                    if (MathRx.IsMatch(line))
                    {
                        // A line of only $$ opens a math block (spec 2); the next such line closes it.
                        fenceChar = '$';
                        fenceLength = 2;
                        kinds[i] = MdFence.Delimiter;
                        continue;
                    }
```

3. In `MayBeDelimiter`, change the last line to `return i < line.Length && line[i] is '`' or '~' or '$';` and its comment to "backtick, tilde or dollar".
4. Add to the class comment: "A line that is only <c>$$</c> opens a math block in the same way."

- [ ] **Step 4: Implement the structure**

`Services/Pad/TableCells.cs`:

```csharp
using System.Collections.Generic;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>The cells and separating pipes of a Markdown table line (spec 2, 3).</summary>
    public static class TableCells
    {
        /// <summary>
        /// The cells of a table line: the outer pipes are dropped, the rest is split on pipes not
        /// escaped by a backslash, and each cell is trimmed. An escaped pipe stays in its cell as <c>\|</c>.
        /// </summary>
        public static List<string> Split(string line)
        {
            string s = (line ?? "").Trim();
            if (s.StartsWith('|')) s = s.Substring(1);
            if (s.EndsWith('|') && !s.EndsWith("\\|")) s = s.Substring(0, s.Length - 1);

            var cells = new List<string>();
            var cell = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '|')
                {
                    cell.Append("\\|");
                    i++;
                    continue;
                }
                if (s[i] == '|')
                {
                    cells.Add(cell.ToString().Trim());
                    cell.Clear();
                    continue;
                }
                cell.Append(s[i]);
            }
            cells.Add(cell.ToString().Trim());
            return cells;
        }

        /// <summary>The offsets of the pipes that separate cells (not escaped by a backslash).</summary>
        public static List<int> Pipes(string line)
        {
            var pipes = new List<int>();
            string s = line ?? "";
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '|')
                {
                    i++;
                    continue;
                }
                if (s[i] == '|') pipes.Add(i);
            }
            return pipes;
        }
    }
}
```

`Services/Pad/MarkdownStructure.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>A table line's part (spec 2).</summary>
    public enum MdTableRole
    {
        None,
        Header,
        Delimiter,
        Row,
    }

    /// <summary>A Wiki.js callout kind: <c>{.is-info}</c> and friends under a quote.</summary>
    public enum MdCallout
    {
        None,
        Info,
        Success,
        Warning,
        Danger,
    }

    /// <summary>What a line is in its document, beyond what the line alone says (spec 2).</summary>
    public readonly record struct MdLineFacts(
        MdFence Fence,
        MdTableRole Table = MdTableRole.None,
        int SetextLevel = 0,
        bool SetextUnderline = false,
        bool FrontMatter = false,
        MdCallout Callout = MdCallout.None,
        bool CalloutClass = false)
    {
        /// <summary>True when the document, not the line alone, decides how the line is shown.</summary>
        public bool IsStructural =>
            Fence != MdFence.None || Table != MdTableRole.None || SetextLevel != 0 || SetextUnderline
            || FrontMatter || Callout != MdCallout.None || CalloutClass;
    }

    /// <summary>
    /// One scan of a Markdown document (spec 2): fences and <c>$$</c> blocks, front matter,
    /// tables, setext headings, Wiki.js callouts and abbreviation terms. Pure; always recomputed
    /// from the text, nothing is saved.
    /// </summary>
    public sealed class MarkdownStructure
    {
        /// <summary>How far down front matter may close (line index).</summary>
        public const int FrontMatterSearch = 200;

        private static readonly Regex DelimiterCellRx = new(@"^:?-+:?$", RegexOptions.CultureInvariant);
        private static readonly Regex SetextRx = new(@"^ {0,3}(=+|-+)[ \t]*$", RegexOptions.CultureInvariant);
        private static readonly Regex CalloutRx = new(@"^ {0,3}\{\.is-(info|success|warning|danger)\}[ \t]*$", RegexOptions.CultureInvariant);
        private static readonly Regex QuoteRx = new(@"^ {0,3}>", RegexOptions.CultureInvariant);
        private static readonly Regex AbbreviationRx = new(@"^\*\[([^\]]+)\]:", RegexOptions.CultureInvariant);

        private MarkdownStructure(MdFence[] fences, int[] openings, int[] closings, int[] blockOpenings, MdLineFacts[] facts, IReadOnlySet<string> abbreviations)
        {
            Fences = fences;
            Openings = openings;
            Closings = closings;
            BlockOpenings = blockOpenings;
            Facts = facts;
            Abbreviations = abbreviations;
        }

        /// <summary>The structure of an empty document.</summary>
        public static MarkdownStructure Empty { get; } = Scan(Array.Empty<string>());

        public int LineCount => Facts.Length;

        /// <summary>Each line's fence kind (index = line number - 1).</summary>
        public MdFence[] Fences { get; }

        /// <summary>For a closing delimiter, the 1-based line it closes; else 0.</summary>
        public int[] Openings { get; }

        /// <summary>For an opening delimiter, the 1-based line that closes it; else 0.</summary>
        public int[] Closings { get; }

        /// <summary>For a line inside a fenced or <c>$$</c> block, the 1-based line that opened it; else 0.</summary>
        public int[] BlockOpenings { get; }

        public MdLineFacts[] Facts { get; }

        /// <summary>The terms defined by <c>*[TERM]: text</c> lines.</summary>
        public IReadOnlySet<string> Abbreviations { get; }

        public static MarkdownStructure Scan(IReadOnlyList<string> lines)
        {
            int n = lines.Count;
            var fences = FenceTracker.Classify(lines);
            var openings = FenceTracker.Openings(fences);
            var closings = new int[n];
            var blockOpenings = new int[n];
            int open = 0;
            for (int i = 0; i < n; i++)
            {
                if (openings[i] > 0) closings[openings[i] - 1] = i + 1;
                if (fences[i] == MdFence.Delimiter) open = open == 0 ? i + 1 : 0;
                else if (fences[i] == MdFence.Inside) blockOpenings[i] = open;
            }

            var facts = new MdLineFacts[n];
            for (int i = 0; i < n; i++) facts[i] = new MdLineFacts(fences[i]);
            MarkFrontMatter(lines, facts);
            MarkTables(lines, facts);
            MarkSetext(lines, facts);
            MarkCallouts(lines, facts);
            return new MarkdownStructure(fences, openings, closings, blockOpenings, facts, CollectAbbreviations(lines, facts));
        }

        /// <summary>
        /// A delimiter row: it holds a pipe and every cell is dashes with optional colons at its
        /// ends (<c>:--</c>, <c>:-:</c>, <c>--:</c>).
        /// </summary>
        public static bool IsDelimiterRow(string line, out int columns)
        {
            columns = 0;
            if (line == null || line.IndexOf('|') < 0 || line.IndexOf('-') < 0) return false;
            var cells = TableCells.Split(line);
            foreach (string cell in cells)
                if (!DelimiterCellRx.IsMatch(cell)) return false;
            columns = cells.Count;
            return true;
        }

        private static string At(IReadOnlyList<string> lines, int i) => lines[i] ?? "";

        /// <summary>Neither fenced, front matter nor already part of a table.</summary>
        private static bool Free(MdLineFacts f) => f.Fence == MdFence.None && !f.FrontMatter && f.Table == MdTableRole.None;

        private static void MarkFrontMatter(IReadOnlyList<string> lines, MdLineFacts[] facts)
        {
            if (lines.Count < 2 || At(lines, 0).TrimEnd() != "---") return;
            int last = Math.Min(lines.Count - 1, FrontMatterSearch);
            for (int k = 1; k <= last; k++)
            {
                string text = At(lines, k).TrimEnd();
                if (text != "---" && text != "...") continue;
                for (int i = 0; i <= k; i++) facts[i] = facts[i] with { FrontMatter = true };
                return;
            }
        }

        private static void MarkTables(IReadOnlyList<string> lines, MdLineFacts[] facts)
        {
            int i = 0;
            while (i + 1 < lines.Count)
            {
                string header = At(lines, i);
                if (!Free(facts[i]) || !Free(facts[i + 1]) || header.IndexOf('|') < 0 || QuoteRx.IsMatch(header)
                    || !IsDelimiterRow(At(lines, i + 1), out int columns) || TableCells.Split(header).Count != columns)
                {
                    i++;
                    continue;
                }

                facts[i] = facts[i] with { Table = MdTableRole.Header };
                facts[i + 1] = facts[i + 1] with { Table = MdTableRole.Delimiter };
                int j = i + 2;
                while (j < lines.Count && Free(facts[j]) && At(lines, j).IndexOf('|') >= 0)
                {
                    facts[j] = facts[j] with { Table = MdTableRole.Row };
                    j++;
                }
                i = j;
            }
        }

        private static void MarkSetext(IReadOnlyList<string> lines, MdLineFacts[] facts)
        {
            for (int i = 1; i < lines.Count; i++)
            {
                var m = SetextRx.Match(At(lines, i));
                if (!m.Success || m.Groups[1].Length < 2 || !Free(facts[i]) || !Free(facts[i - 1]) || facts[i - 1].SetextUnderline) continue;

                string text = At(lines, i - 1);
                if (string.IsNullOrWhiteSpace(text) || MarkdownLineTokenizer.BlockOf(text, MdFence.None) != MdBlock.Paragraph) continue;

                facts[i - 1] = facts[i - 1] with { SetextLevel = m.Groups[1].Value[0] == '=' ? 1 : 2 };
                facts[i] = facts[i] with { SetextUnderline = true };
            }
        }

        private static void MarkCallouts(IReadOnlyList<string> lines, MdLineFacts[] facts)
        {
            for (int i = 1; i < lines.Count; i++)
            {
                var m = CalloutRx.Match(At(lines, i));
                if (!m.Success || facts[i].Fence != MdFence.None || !IsQuote(lines, facts, i - 1)) continue;

                var kind = m.Groups[1].Value switch
                {
                    "info" => MdCallout.Info,
                    "success" => MdCallout.Success,
                    "warning" => MdCallout.Warning,
                    _ => MdCallout.Danger,
                };
                facts[i] = facts[i] with { CalloutClass = true };
                for (int k = i - 1; k >= 0 && IsQuote(lines, facts, k); k--) facts[k] = facts[k] with { Callout = kind };
            }
        }

        private static bool IsQuote(IReadOnlyList<string> lines, MdLineFacts[] facts, int i) =>
            facts[i].Fence == MdFence.None && !facts[i].FrontMatter && QuoteRx.IsMatch(At(lines, i));

        private static IReadOnlySet<string> CollectAbbreviations(IReadOnlyList<string> lines, MdLineFacts[] facts)
        {
            var terms = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < lines.Count; i++)
            {
                if (facts[i].Fence != MdFence.None) continue;
                var m = AbbreviationRx.Match(At(lines, i));
                if (m.Success && m.Groups[1].Value.Trim().Length > 0) terms.Add(m.Groups[1].Value.Trim());
            }
            return terms;
        }
    }
}
```

In `Services/Pad/MarkdownStyles.cs`, add to `MarkdownStyles`:

```csharp
        /// <summary>The alpha of a callout's background tint (15%).</summary>
        public const byte CalloutTintAlpha = 0x26;

        /// <summary>A callout's bar color; its background is the same color at <see cref="CalloutTintAlpha"/>.</summary>
        public static PadColor CalloutColor(MdCallout kind, PadPalette palette) => kind switch
        {
            MdCallout.Success => palette.MdCalloutSuccess,
            MdCallout.Warning => palette.MdCalloutWarning,
            MdCallout.Danger => palette.MdCalloutDanger,
            _ => palette.MdCalloutInfo,
        };
```

- [ ] **Step 5: The cache keeps the structure**

Replace `Pad/MarkdownDocumentCache.cs` with:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// The structure of the shown Markdown document (<see cref="MarkdownStructure"/>: fences, $$
    /// blocks, tables, setext headings, front matter, callouts, abbreviations), shared by the
    /// colorizer, the background renderer, the generators and the diagram pictures. Rescans only
    /// after an edit that can change it (R3), so ordinary typing never walks the whole note.
    /// </summary>
    internal sealed class MarkdownDocumentCache
    {
        /// <summary>Characters whose typing or removal can change the structure.</summary>
        private static readonly char[] Triggers = { '`', '~', '|', '$', '=', '-', '>', '{', '[', '*' };

        /// <summary>Characters that start a structural line (a table pipe does not: typing in a row changes nothing).</summary>
        private static readonly char[] LineStarts = { '`', '~', '$', '=', '-', '>', '{', '*' };

        private readonly Action<Exception> _onFailure;
        private TextDocument? _document;
        private MarkdownStructure _structure = MarkdownStructure.Empty;
        private bool _stale = true;

        /// <param name="onFailure">Told when following an edit fails; the edit itself never sees the exception.</param>
        public MarkdownDocumentCache(Action<Exception>? onFailure = null) => _onFailure = onFailure ?? (_ => { });

        /// <summary>Raised after an edit changed the structure, so lines far from the edit repaint.</summary>
        public event Action? StructureChanged;

        /// <summary>How many times the whole document was scanned; for tests.</summary>
        internal int Recomputes { get; private set; }

        /// <summary>The fence kind of a line (1-based) of <paramref name="document"/>.</summary>
        public MdFence KindOf(TextDocument document, int lineNumber) => Get(document, s => s.Fences, lineNumber, MdFence.None);

        /// <summary>What line <paramref name="lineNumber"/> is in its document.</summary>
        public MdLineFacts FactsOf(TextDocument document, int lineNumber) =>
            Get(document, s => s.Facts, lineNumber, new MdLineFacts(MdFence.None));

        /// <summary>The 1-based line that opened the fence line <paramref name="lineNumber"/> closes, or 0 when it closes none.</summary>
        public int OpeningLineOf(TextDocument document, int lineNumber) => Get(document, s => s.Openings, lineNumber, 0);

        /// <summary>The 1-based line that closes the fence line <paramref name="lineNumber"/> opens, or 0 (it opens none, or never closes).</summary>
        public int ClosingLineOf(TextDocument document, int lineNumber) => Get(document, s => s.Closings, lineNumber, 0);

        /// <summary>For a line inside a fenced or $$ block, the 1-based line that opened it; else 0.</summary>
        public int BlockOpeningOf(TextDocument document, int lineNumber) => Get(document, s => s.BlockOpenings, lineNumber, 0);

        /// <summary>The abbreviation terms the document defines.</summary>
        public IReadOnlySet<string> AbbreviationsOf(TextDocument document)
        {
            Track(document);
            if (_stale) Recompute();
            return _structure.Abbreviations;
        }

        /// <summary>Stops following the document.</summary>
        public void Detach()
        {
            if (_document != null) _document.Changed -= OnChanged;
            _document = null;
            _structure = MarkdownStructure.Empty;
            _stale = true;
        }

        private T Get<T>(TextDocument document, Func<MarkdownStructure, T[]> part, int lineNumber, T none)
        {
            Track(document);
            if (_stale) Recompute();
            var values = part(_structure);
            int index = lineNumber - 1;
            return index >= 0 && index < values.Length ? values[index] : none;
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
            // AvalonEdit calls every handler the document had when the change began, so a handler
            // that ran earlier in this change may already have detached the cache.
            var document = _document;
            if (document == null || !ReferenceEquals(sender, document)) return;
            try
            {
                if (!_stale && !TouchesStructure(document, e)) return;
                var before = _structure;
                Recompute();
                if (!before.Facts.AsSpan().SequenceEqual(_structure.Facts) || !before.Abbreviations.SetEquals(_structure.Abbreviations))
                    StructureChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _stale = true;
                _onFailure(ex);
            }
        }

        /// <summary>Whether an edit can change the structure (R3).</summary>
        private bool TouchesStructure(TextDocument document, DocumentChangeEventArgs e)
        {
            if (document.LineCount != _structure.LineCount) return true;
            if (e.InsertedText.Text.IndexOfAny(Triggers) >= 0 || e.RemovedText.Text.IndexOfAny(Triggers) >= 0) return true;

            int first = document.GetLineByOffset(Math.Min(e.Offset, document.TextLength)).LineNumber;
            int last = document.GetLineByOffset(Math.Min(e.Offset + e.InsertionLength, document.TextLength)).LineNumber;
            for (int number = first; number <= last; number++)
            {
                var facts = _structure.Facts[number - 1];
                if (facts.Fence == MdFence.Delimiter || facts.Table == MdTableRole.Header || facts.SetextLevel != 0
                    || facts.SetextUnderline || facts.FrontMatter || facts.CalloutClass) return true;
                if (StartsWithAny(document, number, LineStarts)) return true;
                // Text typed on a line may make the dashes below it a setext underline.
                if (number < document.LineCount && StartsWithAny(document, number + 1, new[] { '=', '-' })) return true;
            }
            return false;
        }

        /// <summary>The line's first character after at most three spaces is one of <paramref name="chars"/>.</summary>
        private static bool StartsWithAny(TextDocument document, int number, char[] chars)
        {
            var line = document.GetLineByNumber(number);
            string start = document.GetText(line.Offset, Math.Min(line.Length, 4));
            int i = 0;
            while (i < start.Length && i < 3 && start[i] == ' ') i++;
            return i < start.Length && chars.Contains(start[i]);
        }

        private void Recompute()
        {
            var document = _document!;
            var lines = new string[document.LineCount];
            foreach (var line in document.Lines) lines[line.LineNumber - 1] = document.GetText(line);
            _structure = MarkdownStructure.Scan(lines);
            _stale = false;
            Recomputes++;
        }
    }
}
```

In `Pad/EditorLanguage.cs`, rename both uses of `FencesChanged` to `StructureChanged` (subscribe in `Apply`, unsubscribe in `Clear`), rename `OnFencesChanged` to `OnStructureChanged`, and change its comment to "The structure moved (a fence, a table, a heading underline): lines far from the edit changed look, so repaint them all once the edit is done."

- [ ] **Step 6: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~MarkdownStructureTests|FullyQualifiedName~MarkdownRenderingTests|FullyQualifiedName~MarkdownTokenizerTests|FullyQualifiedName~DiagramBoardTests|FullyQualifiedName~PadFoldingTests" 2>&1 | tail -5`
Expected: all pass. Then the full suite.

- [ ] **Step 7: Commit**

```bash
git add Services/Pad/FenceTracker.cs Services/Pad/TableCells.cs Services/Pad/MarkdownStructure.cs Services/Pad/MarkdownStyles.cs Pad/MarkdownDocumentCache.cs Pad/EditorLanguage.cs tests/Kil0bitSystemMonitor.Tests/MarkdownStructureTests.cs tests/Kil0bitSystemMonitor.Tests/MarkdownRenderingTests.cs
git commit -m "feat(pad): Markdown document structure - \$\$ blocks, tables, setext headings, front matter, callouts, abbreviations; rescans only when an edit can change it" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 3: The tokenizer learns the structure and the inline extras

**Files:**
- Modify: `Services/Pad/MarkdownLineTokenizer.cs` (rewrite, keeping every existing behaviour)
- Test: `tests/Kil0bitSystemMonitor.Tests/MarkdownExtrasTests.cs`

**Interfaces:**
- Consumes: `MdLineFacts`, `MdTableRole`, `TableCells.Pipes` (Task 2); the `MdStyle` values added in Task 1.
- Produces:
  - `MdBlock` gains (appended): `Table`, `FrontMatter`, `SetextUnderline`, `CalloutClass`, `Definition`.
  - `sealed record MdLine(MdBlock Block, IReadOnlyList<MdSpan> Spans, int QuoteDepth = 0)`.
  - `MarkdownLineTokenizer.Tokenize(string line, MdLineFacts facts, IReadOnlySet<string>? abbreviations = null)`; the old `Tokenize(string line, MdFence fence)` stays and means `Tokenize(line, new MdLineFacts(fence))`.
  - `MarkdownLineTokenizer.BlockOf(string line, MdLineFacts facts)`; the old `BlockOf(string line, MdFence fence)` stays.
  - `MarkdownLineTokenizer.QuoteDepth(string line) : int` (0–6).

- [ ] **Step 1: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/MarkdownExtrasTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The Wiki.js constructs of a line (spec 2-5): structure-dependent blocks and the inline extras.</summary>
    public class MarkdownExtrasTests
    {
        private static List<(string Text, MdStyle Style)> Runs(string line, MdLineFacts facts = default, IReadOnlySet<string>? terms = null) =>
            MarkdownLineTokenizer.Tokenize(line, facts, terms).Spans.Select(s => (line.Substring(s.Start, s.Length), s.Style)).ToList();

        private static MdLineFacts Plain => new(MdFence.None);

        [Fact]
        public void Table_lines_have_a_bold_header_and_dimmed_pipes()
        {
            var header = MarkdownLineTokenizer.Tokenize("| a | **b** |", Plain with { Table = MdTableRole.Header });
            Assert.Equal(MdBlock.Table, header.Block);
            var runs = Runs("| a | **b** |", Plain with { Table = MdTableRole.Header });
            Assert.Contains(("| a | **b** |", MdStyle.TableHeader), runs);
            Assert.Equal(3, runs.Count(r => r.Text == "|" && r.Style == MdStyle.Marker));
            Assert.Contains(("b", MdStyle.Bold), runs);

            Assert.Equal(new[] { ("|:--|--:|", MdStyle.Marker) }, Runs("|:--|--:|", Plain with { Table = MdTableRole.Delimiter }));
            var row = Runs(@"| x \| y | `c|d` |", Plain with { Table = MdTableRole.Row });
            Assert.DoesNotContain(row, r => r.Style == MdStyle.TableHeader);
            Assert.Contains(("c|d", MdStyle.Code), row);
        }

        [Fact]
        public void Setext_text_is_a_heading_and_its_underline_is_dimmed()
        {
            var text = MarkdownLineTokenizer.Tokenize("Title", Plain with { SetextLevel = 1 });
            Assert.Equal(MdBlock.Heading, text.Block);
            Assert.Contains(("Title", MdStyle.Heading1), Runs("Title", Plain with { SetextLevel = 1 }));
            Assert.Contains(("Sub", MdStyle.Heading2), Runs("Sub", Plain with { SetextLevel = 2 }));

            var under = MarkdownLineTokenizer.Tokenize("---", Plain with { SetextUnderline = true });
            Assert.Equal(MdBlock.SetextUnderline, under.Block);
            Assert.Equal(new[] { ("---", MdStyle.Marker) }, Runs("---", Plain with { SetextUnderline = true }));
        }

        [Fact]
        public void Front_matter_and_callout_class_lines_are_dimmed()
        {
            Assert.Equal(MdBlock.FrontMatter, MarkdownLineTokenizer.Tokenize("title: x", Plain with { FrontMatter = true }).Block);
            Assert.Equal(new[] { ("title: **x**", MdStyle.Marker) }, Runs("title: **x**", Plain with { FrontMatter = true }));
            Assert.Equal(MdBlock.CalloutClass, MarkdownLineTokenizer.Tokenize("{.is-info}", Plain with { CalloutClass = true }).Block);
            Assert.Equal(new[] { ("{.is-info}", MdStyle.Marker) }, Runs("{.is-info}", Plain with { CalloutClass = true }));
        }

        [Theory]
        [InlineData("> a", 1)]
        [InlineData(">> a", 2)]
        [InlineData("> > > a", 3)]
        [InlineData("   >a", 1)]
        [InlineData("a > b", 0)]
        [InlineData(">>>>>>>> deep", 6)]
        public void Quote_depth_counts_the_markers(string line, int depth) => Assert.Equal(depth, MarkdownLineTokenizer.QuoteDepth(line));

        [Fact]
        public void Nested_quotes_dim_every_marker()
        {
            var line = MarkdownLineTokenizer.Tokenize("> > inner **b**", Plain);
            Assert.Equal(MdBlock.Quote, line.Block);
            Assert.Equal(2, line.QuoteDepth);
            var runs = Runs("> > inner **b**");
            Assert.Equal(2, runs.Count(r => r.Text == ">" && r.Style == MdStyle.Marker));
            Assert.Contains(("inner **b**", MdStyle.QuoteText), runs);
            Assert.Contains(("b", MdStyle.Bold), runs);
        }

        [Fact]
        public void Definitions_dim_their_labels()
        {
            Assert.Equal(MdBlock.Definition, MarkdownLineTokenizer.Tokenize("[^1]: the note", Plain).Block);
            Assert.Contains(("[^1]:", MdStyle.Marker), Runs("[^1]: the note"));
            Assert.Contains(("*[HTML]:", MdStyle.Marker), Runs("*[HTML]: Hyper Text"));
            var reference = Runs("[docs]: https://example.com \"Docs\"");
            Assert.Contains(("[docs]:", MdStyle.Marker), reference);
            Assert.Contains(("https://example.com", MdStyle.LinkText), reference);
        }

        [Fact]
        public void Footnote_references_are_small_raised_links()
        {
            var runs = Runs("text[^note] more");
            Assert.Contains(("[^", MdStyle.Marker), runs);
            Assert.Contains(("note", MdStyle.FootnoteRef), runs);
            Assert.Contains(("]", MdStyle.Marker), runs);
            Assert.DoesNotContain(runs, r => r.Style == MdStyle.LinkText);
        }

        [Fact]
        public void Reference_links_and_images()
        {
            var runs = Runs("see [docs][d] and [faq][] and ![logo](a.png)");
            Assert.Contains(("docs", MdStyle.LinkText), runs);
            Assert.Contains(("][d]", MdStyle.Marker), runs);
            Assert.Contains(("faq", MdStyle.LinkText), runs);
            Assert.Contains(("][]", MdStyle.Marker), runs);
            Assert.Contains(("!", MdStyle.Marker), runs);
            Assert.Contains(("logo", MdStyle.LinkText), runs);
        }

        [Fact]
        public void Sub_and_superscript()
        {
            Assert.Contains(("2", MdStyle.Subscript), Runs("H~2~O"));
            Assert.Contains(("2", MdStyle.Superscript), Runs("x^2^ + y"));
            Assert.Contains(("gone", MdStyle.Strike), Runs("~~gone~~"));
            Assert.Equal(2, Runs("H~2~O").Count(r => r.Text == "~" && r.Style == MdStyle.Marker));
        }

        [Fact]
        public void Keys_tags_attributes_and_escapes()
        {
            var keys = Runs("press <kbd>Ctrl</kbd>+<KBD>C</KBD>");
            Assert.Contains(("Ctrl", MdStyle.KbdText), keys);
            Assert.Contains(("C", MdStyle.KbdText), keys);
            Assert.Contains(("<kbd>", MdStyle.Marker), keys);
            Assert.Contains(("</kbd>", MdStyle.Marker), keys);

            var tags = Runs("a<br>b <sup>x</sup> <!-- note -->");
            Assert.Contains(("<br>", MdStyle.Marker), tags);
            Assert.Contains(("<sup>", MdStyle.Marker), tags);
            Assert.Contains(("<!-- note -->", MdStyle.Marker), tags);

            Assert.Contains(("{.tabset}", MdStyle.Marker), Runs("# Tabs {.tabset}"));
            Assert.Contains(("{#id .wide}", MdStyle.Marker), Runs("text {#id .wide}"));
            Assert.Contains((@"\", MdStyle.Marker), Runs(@"\*not italic\*"));
        }

        [Fact]
        public void Inline_math_is_colored()
        {
            var runs = Runs("area $\\pi r^2$ and $$E=mc^2$$");
            Assert.Contains(("\\pi r^2", MdStyle.MathText), runs);
            Assert.Contains(("E=mc^2", MdStyle.MathText), runs);
            Assert.DoesNotContain(runs, r => r.Style == MdStyle.Superscript);
        }

        [Theory]
        [InlineData("costs $5 and $10")]
        [InlineData("a < b and c > d")]
        [InlineData("x^ 2^")]
        [InlineData("a~b")]
        [InlineData("$ x$")]
        [InlineData("x$5")]
        [InlineData("{not attributes} here")]
        public void Text_that_only_looks_like_syntax_stays_text(string line)
        {
            Assert.DoesNotContain(Runs(line), r => r.Style is MdStyle.MathText or MdStyle.Superscript or MdStyle.Subscript or MdStyle.Marker);
        }

        [Fact]
        public void Code_hides_the_extras()
        {
            var runs = Runs("`$x$ <kbd>K</kbd> H~2~O`");
            Assert.Single(runs, r => r.Style == MdStyle.Code);
            Assert.DoesNotContain(runs, r => r.Style is MdStyle.MathText or MdStyle.KbdText or MdStyle.Subscript);
        }

        [Fact]
        public void Abbreviations_get_a_dotted_underline_on_whole_words_outside_code()
        {
            var terms = new HashSet<string> { "HTML" };
            var runs = Runs("HTML and XHTML and `HTML` and HTML5 and (HTML)", default, terms);

            Assert.Equal(2, runs.Count(r => r.Style == MdStyle.Abbreviation));
            Assert.Equal(new[] { "HTML", "HTML" }, runs.Where(r => r.Style == MdStyle.Abbreviation).Select(r => r.Text));
            Assert.DoesNotContain(Runs("*[HTML]: Hyper Text", default, terms), r => r.Style == MdStyle.Abbreviation);
        }

        [Fact]
        public void The_old_overloads_still_work()
        {
            Assert.Equal(MdBlock.Quote, MarkdownLineTokenizer.BlockOf("> q", MdFence.None));
            Assert.Equal(MdBlock.Fence, MarkdownLineTokenizer.BlockOf("x", MdFence.Inside));
            Assert.Equal(MdBlock.Table, MarkdownLineTokenizer.BlockOf("| a |", Plain with { Table = MdTableRole.Row }));
            Assert.Equal(new[] { ("```cs", MdStyle.Marker) },
                MarkdownLineTokenizer.Tokenize("```cs", MdFence.Delimiter).Spans.Select(s => ("```cs".Substring(s.Start, s.Length), s.Style)));
        }
    }
}
```

Note `default(MdLineFacts)` has `Fence == MdFence.None`, so `Runs(line)` tokenizes as a plain line.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~MarkdownExtrasTests" 2>&1 | tail -5`
Expected: build error (no `Tokenize(string, MdLineFacts, …)` overload).

- [ ] **Step 3: Implement — replace `Services/Pad/MarkdownLineTokenizer.cs` with:**

```csharp
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>How one run of a Markdown line is shown (spec 2.3, and the Wiki.js spec 1-5). Markers stay visible, dimmed.</summary>
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
        TableHeader,
        FootnoteRef,
        Subscript,
        Superscript,
        KbdText,
        Abbreviation,
        MathText,
    }

    /// <summary>What kind of line it is; the background renderer draws quote bars, code shading, callouts and rules from it.</summary>
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
        Table,
        FrontMatter,
        SetextUnderline,
        CalloutClass,
        Definition,
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
    /// styles, then markers — and may overlap (bold inside a heading). <see cref="QuoteDepth"/> is
    /// the number of quote levels (0 for other lines).
    /// </summary>
    public sealed record MdLine(MdBlock Block, IReadOnlyList<MdSpan> Spans, int QuoteDepth = 0);

    /// <summary>
    /// Markdown styled source, one line at a time, told by <see cref="MdLineFacts"/> what the
    /// document says about the line (fences, tables, setext headings, front matter, callouts).
    /// Deliberately smaller than CommonMark — unclosed or ambiguous markers stay plain text — and
    /// never changes the text, only says how to show it.
    /// </summary>
    public static class MarkdownLineTokenizer
    {
        /// <summary>
        /// Lines longer than this keep their block style but get no inline formatting, so a
        /// pathological line (thousands of unmatched markers) cannot stall typing.
        /// </summary>
        public const int MaxInlineLength = 4000;

        /// <summary>The deepest quote level drawn.</summary>
        public const int MaxQuoteDepth = 6;

        private static readonly Regex HeadingRx = new(@"^ {0,3}(#{1,6})(?:[ \t]+|$)", RegexOptions.CultureInvariant);
        private static readonly Regex RuleRx = new(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$", RegexOptions.CultureInvariant);
        private static readonly Regex TaskRx = new(@"^[ \t]*([-*+])[ \t]+(\[[ xX]\])(?=[ \t]|$)[ \t]*", RegexOptions.CultureInvariant);
        private static readonly Regex BulletRx = new(@"^[ \t]*([-*+])[ \t]+", RegexOptions.CultureInvariant);
        private static readonly Regex NumberedRx = new(@"^[ \t]*(\d{1,9}[.)])[ \t]+", RegexOptions.CultureInvariant);
        private static readonly Regex QuoteRx = new(@"^ {0,3}(>)[ \t]?", RegexOptions.CultureInvariant);
        private static readonly Regex FootnoteDefRx = new(@"^ {0,3}(\[\^[^\]\s]+\]:)", RegexOptions.CultureInvariant);
        private static readonly Regex AbbreviationDefRx = new(@"^(\*\[[^\]]+\]:)", RegexOptions.CultureInvariant);
        private static readonly Regex ReferenceDefRx = new(@"^ {0,3}(\[[^\]^][^\]]*\]:)[ \t]*(\S+)", RegexOptions.CultureInvariant);
        private static readonly Regex FootnoteRefRx = new(@"\G\[\^([^\]\s]+)\]", RegexOptions.CultureInvariant);
        private static readonly Regex KbdRx = new(@"\G<kbd>(.*?)</kbd>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex HtmlTagRx = new(@"\G(?:<!--.*?-->|</?[A-Za-z][A-Za-z0-9-]*(?:\s[^<>]*)?/?>)", RegexOptions.CultureInvariant);
        private static readonly Regex AttributesRx = new(@"\G\{[.#][^{}]*\}[ \t]*$", RegexOptions.CultureInvariant);

        /// <summary>The line's styled runs, the line alone deciding (no tables, setext headings or callouts).</summary>
        public static MdLine Tokenize(string line, MdFence fence) => Tokenize(line, new MdLineFacts(fence));

        /// <summary>
        /// The line's styled runs. <paramref name="abbreviations"/> are the document's defined terms;
        /// whole-word uses get a dotted underline.
        /// </summary>
        public static MdLine Tokenize(string line, MdLineFacts facts, IReadOnlySet<string>? abbreviations = null)
        {
            line ??= "";
            var spans = new List<MdSpan>();
            MdBlock block = Classify(line, facts, out Match? m);
            int depth = 0;
            switch (block)
            {
                case MdBlock.Fence:
                    Add(spans, 0, line.Length, facts.Fence == MdFence.Delimiter ? MdStyle.Marker : MdStyle.CodeBlock);
                    return new MdLine(block, spans);
                case MdBlock.Rule:
                case MdBlock.FrontMatter:
                case MdBlock.SetextUnderline:
                case MdBlock.CalloutClass:
                    Add(spans, 0, line.Length, MdStyle.Marker);
                    return new MdLine(block, spans);
                case MdBlock.Table:
                    TableLine(line, facts.Table, spans);
                    break;
                case MdBlock.Heading when facts.SetextLevel > 0:
                    Add(spans, 0, line.Length, facts.SetextLevel == 1 ? MdStyle.Heading1 : MdStyle.Heading2);
                    Inline(line, 0, line.Length, spans);
                    break;
                case MdBlock.Heading:
                    Add(spans, 0, line.Length, MdStyle.Heading1 + (m!.Groups[1].Length - 1));
                    Inline(line, m.Length, line.Length, spans);
                    Add(spans, m.Groups[1].Index, m.Groups[1].Length, MdStyle.Marker);
                    break;
                case MdBlock.Quote:
                    depth = QuoteMarkers(line, out int text, spans);
                    Add(spans, text, line.Length - text, MdStyle.QuoteText);
                    Inline(line, text, line.Length, spans);
                    break;
                case MdBlock.Definition:
                    Definition(line, m!, spans);
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
            Abbreviations(line, spans, abbreviations);
            return new MdLine(block, spans, depth);
        }

        /// <summary>The kind of a line, the line alone deciding; cheap enough to call per visible line.</summary>
        public static MdBlock BlockOf(string line, MdFence fence) => BlockOf(line, new MdLineFacts(fence));

        /// <summary>The kind of a line without its inline runs.</summary>
        public static MdBlock BlockOf(string line, MdLineFacts facts) => Classify(line ?? "", facts, out _);

        /// <summary>Where the list marker (<c>-</c>, <c>*</c>, <c>+</c>) of a bullet or task line is, or -1.</summary>
        public static int BulletOffset(string line)
        {
            MdBlock block = Classify(line ?? "", new MdLineFacts(MdFence.None), out Match? m);
            return block is MdBlock.Bullet or MdBlock.Task ? m!.Groups[1].Index : -1;
        }

        /// <summary>How many quote levels a line opens with (<c>&gt; &gt; text</c> is 2), up to <see cref="MaxQuoteDepth"/>.</summary>
        public static int QuoteDepth(string line) => QuoteMarkers(line ?? "", out _, null);

        private static MdBlock Classify(string line, MdLineFacts facts, out Match? match)
        {
            match = null;
            if (facts.Fence != MdFence.None) return MdBlock.Fence;
            if (facts.FrontMatter) return MdBlock.FrontMatter;
            if (facts.Table != MdTableRole.None) return MdBlock.Table;
            if (facts.SetextUnderline) return MdBlock.SetextUnderline;
            if (facts.SetextLevel > 0) return MdBlock.Heading;
            if (facts.CalloutClass) return MdBlock.CalloutClass;
            if (RuleRx.IsMatch(line)) return MdBlock.Rule;
            if ((match = HeadingRx.Match(line)).Success) return MdBlock.Heading;
            if ((match = QuoteRx.Match(line)).Success) return MdBlock.Quote;
            if ((match = FootnoteDefRx.Match(line)).Success
                || (match = AbbreviationDefRx.Match(line)).Success
                || (match = ReferenceDefRx.Match(line)).Success) return MdBlock.Definition;
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

        /// <summary>
        /// Dims each <c>&gt;</c> of a quote (spaces allowed between them) and returns the depth;
        /// <paramref name="text"/> is where the quoted text starts (after one optional space).
        /// </summary>
        private static int QuoteMarkers(string line, out int text, List<MdSpan>? spans)
        {
            int i = 0;
            int depth = 0;
            while (i < line.Length && i < 3 && line[i] == ' ') i++;
            while (i < line.Length && line[i] == '>' && depth < MaxQuoteDepth)
            {
                if (spans != null) Add(spans, i, 1, MdStyle.Marker);
                depth++;
                i++;
                int j = i;
                while (j < line.Length && (line[j] == ' ' || line[j] == '\t')) j++;
                if (j < line.Length && line[j] == '>')
                {
                    i = j;
                    continue;
                }
                if (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
                break;
            }
            text = i;
            return depth;
        }

        /// <summary>A table line: the header bold, the delimiter dimmed, every separating pipe dimmed.</summary>
        private static void TableLine(string line, MdTableRole role, List<MdSpan> spans)
        {
            if (role == MdTableRole.Delimiter)
            {
                Add(spans, 0, line.Length, MdStyle.Marker);
                return;
            }
            if (role == MdTableRole.Header) Add(spans, 0, line.Length, MdStyle.TableHeader);
            Inline(line, 0, line.Length, spans);
            if (line.Length <= MaxInlineLength)
                foreach (int pipe in TableCells.Pipes(line)) Add(spans, pipe, 1, MdStyle.Marker);
        }

        /// <summary><c>[^id]:</c>, <c>*[TERM]:</c> and <c>[id]: url</c> lines: the label dimmed, a reference's address in the link color.</summary>
        private static void Definition(string line, Match m, List<MdSpan> spans)
        {
            var label = m.Groups[1];
            int after = label.Index + label.Length;
            if (m.Groups.Count > 2 && m.Groups[2].Success)
            {
                Add(spans, m.Groups[2].Index, m.Groups[2].Length, MdStyle.LinkText);
                after = m.Groups[2].Index + m.Groups[2].Length;
            }
            Inline(line, after, line.Length, spans);
            Add(spans, label.Index, label.Length, MdStyle.Marker);
        }

        /// <summary>Code spans, math, tags, links, footnotes, emphasis and scripts between <paramref name="start"/> and <paramref name="end"/>.</summary>
        private static void Inline(string s, int start, int end, List<MdSpan> spans)
        {
            if (s.Length > MaxInlineLength) return;
            int i = start;
            while (i < end)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < end && IsEscapable(s[i + 1]))
                {
                    Add(spans, i, 1, MdStyle.Marker);
                    i += 2;
                    continue;
                }
                if (c == '`') { i = CodeSpan(s, i, end, spans); continue; }
                if (c == '$' && TryMath(s, i, end, spans, out int afterMath)) { i = afterMath; continue; }
                if (c == '<' && TryTag(s, i, end, spans, out int afterTag)) { i = afterTag; continue; }
                if (c == '{' && TryAttributes(s, i, end, spans)) { i = end; continue; }
                if (c == '!' && i + 1 < end && s[i + 1] == '[' && TryLink(s, i + 1, end, spans, out int afterImage))
                {
                    Add(spans, i, 1, MdStyle.Marker);
                    i = afterImage;
                    continue;
                }
                if (c == '[' && i + 1 < end && s[i + 1] == '^' && TryFootnote(s, i, end, spans, out int afterNote)) { i = afterNote; continue; }
                if (c == '[' && TryLink(s, i, end, spans, out int afterLink)) { i = afterLink; continue; }
                if (c is '*' or '_' or '~') { i = Emphasis(s, i, end, spans); continue; }
                if (c == '^') { i = Script(s, i, end, spans, '^', MdStyle.Superscript); continue; }
                i++;
            }
        }

        private static bool IsEscapable(char c) => "\\`*_{}[]()#+-.!~>|$^<=".IndexOf(c) >= 0;

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

        /// <summary>
        /// Inline math, <c>$…$</c> or <c>$$…$$</c>: the content must not start or end with a space
        /// and the closing dollar must not be followed by a digit, so "$5 and $10" stays text.
        /// </summary>
        private static bool TryMath(string s, int i, int end, List<MdSpan> spans, out int after)
        {
            after = i;
            int n = Run(s, i, end, '$');
            if (n > 2) return false;
            int open = i + n;
            if (open >= end || char.IsWhiteSpace(s[open])) return false;
            int j = open;
            while (j < end)
            {
                if (s[j] == '\\') { j += 2; continue; }
                if (s[j] != '$') { j++; continue; }
                int m = Run(s, j, end, '$');
                if (m == n && !char.IsWhiteSpace(s[j - 1]) && (j + m >= end || !char.IsDigit(s[j + m])))
                {
                    Add(spans, i, n, MdStyle.Marker);
                    Add(spans, open, j - open, MdStyle.MathText);
                    Add(spans, j, n, MdStyle.Marker);
                    after = j + n;
                    return true;
                }
                j += m;
            }
            return false;
        }

        /// <summary><c>&lt;kbd&gt;key&lt;/kbd&gt;</c> (the key on a key background) and other HTML tags and comments (dimmed).</summary>
        private static bool TryTag(string s, int i, int end, List<MdSpan> spans, out int after)
        {
            after = i;
            var key = KbdRx.Match(s, i);
            if (key.Success && key.Index + key.Length <= end)
            {
                var inner = key.Groups[1];
                Add(spans, i, inner.Index - i, MdStyle.Marker);
                Add(spans, inner.Index, inner.Length, MdStyle.KbdText);
                Add(spans, inner.Index + inner.Length, key.Index + key.Length - inner.Index - inner.Length, MdStyle.Marker);
                after = key.Index + key.Length;
                return true;
            }
            var tag = HtmlTagRx.Match(s, i);
            if (!tag.Success || tag.Index + tag.Length > end) return false;
            Add(spans, i, tag.Length, MdStyle.Marker);
            after = i + tag.Length;
            return true;
        }

        /// <summary><c>{.class #id}</c> at the very end of the line (Wiki.js attributes, <c>{.tabset}</c>): dimmed.</summary>
        private static bool TryAttributes(string s, int i, int end, List<MdSpan> spans)
        {
            if (end != s.Length || !AttributesRx.IsMatch(s, i)) return false;
            Add(spans, i, end - i, MdStyle.Marker);
            return true;
        }

        /// <summary><c>[^id]</c>: the id small and raised in the link color, the brackets dimmed.</summary>
        private static bool TryFootnote(string s, int i, int end, List<MdSpan> spans, out int after)
        {
            after = i;
            var note = FootnoteRefRx.Match(s, i);
            if (!note.Success || note.Index + note.Length > end) return false;
            var id = note.Groups[1];
            Add(spans, i, 2, MdStyle.Marker);
            Add(spans, id.Index, id.Length, MdStyle.FootnoteRef);
            Add(spans, id.Index + id.Length, 1, MdStyle.Marker);
            after = i + note.Length;
            return true;
        }

        /// <summary>
        /// <c>[text](address)</c>, <c>[text][id]</c> and <c>[text][]</c>: the text in the link color,
        /// the brackets and the address or id dimmed.
        /// </summary>
        private static bool TryLink(string s, int i, int end, List<MdSpan> spans, out int after)
        {
            after = i;
            int close = s.IndexOf(']', i + 1, end - i - 1);
            if (close < 0 || close + 1 >= end) return false;
            int stop;
            if (s[close + 1] == '(') stop = s.IndexOf(')', close + 2, end - close - 2);
            else if (s[close + 1] == '[') stop = s.IndexOf(']', close + 2, end - close - 2);
            else return false;
            if (stop < 0) return false;

            Add(spans, i, 1, MdStyle.Marker);
            Add(spans, i + 1, close - i - 1, MdStyle.LinkText);
            Inline(s, i + 1, close, spans);
            Add(spans, close, stop - close + 1, MdStyle.Marker);
            after = stop + 1;
            return true;
        }

        /// <summary>
        /// <c>*</c>/<c>_</c> runs of one to three (italic, bold, both), <c>~~</c> (strike) and a single
        /// <c>~</c> (subscript). A closing run of the same length wins; a longer closing run is used
        /// only when no exact one follows (<c>**bold *italic***</c>). Returns where scanning continues.
        /// </summary>
        private static int Emphasis(string s, int i, int end, List<MdSpan> spans)
        {
            char c = s[i];
            int n = Run(s, i, end, c);
            if (c == '~' && n == 1) return Script(s, i, end, spans, '~', MdStyle.Subscript);
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

        /// <summary><c>~sub~</c> and <c>^sup^</c>: one marker each side, no space inside, on one line.</summary>
        private static int Script(string s, int i, int end, List<MdSpan> spans, char c, MdStyle style)
        {
            int n = Run(s, i, end, c);
            if (n != 1) return i + n;
            int j = i + 1;
            while (j < end && s[j] != c && !char.IsWhiteSpace(s[j])) j++;
            if (j >= end || s[j] != c || j == i + 1) return i + 1;
            if (j + 1 < end && s[j + 1] == c) return i + 1;
            Add(spans, i, 1, MdStyle.Marker);
            Add(spans, i + 1, j - i - 1, style);
            Add(spans, j, 1, MdStyle.Marker);
            return j + 1;
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

        /// <summary>
        /// Whole-word uses of the document's abbreviation terms get a dotted underline — never inside
        /// code, math, keys or markers (R4).
        /// </summary>
        private static void Abbreviations(string line, List<MdSpan> spans, IReadOnlySet<string>? terms)
        {
            if (terms == null || terms.Count == 0 || line.Length > MaxInlineLength) return;
            int styled = spans.Count;
            foreach (string term in terms)
            {
                int at = 0;
                while (at < line.Length && (at = line.IndexOf(term, at, StringComparison.Ordinal)) >= 0)
                {
                    int stop = at + term.Length;
                    bool whole = (at == 0 || !IsWordChar(line[at - 1])) && (stop >= line.Length || !IsWordChar(line[stop]));
                    if (whole && !Covered(spans, styled, at, stop)) spans.Add(new MdSpan(at, term.Length, MdStyle.Abbreviation));
                    at = stop;
                }
            }
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static bool Covered(List<MdSpan> spans, int count, int from, int to)
        {
            for (int k = 0; k < count; k++)
            {
                var span = spans[k];
                bool hides = span.Style is MdStyle.Code or MdStyle.MathText or MdStyle.KbdText or MdStyle.Marker;
                if (hides && span.Start < to && from < span.Start + span.Length) return true;
            }
            return false;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~MarkdownExtrasTests|FullyQualifiedName~MarkdownTokenizerTests|FullyQualifiedName~MarkdownStructureTests|FullyQualifiedName~MarkdownRenderingTests|FullyQualifiedName~PadCopyRtfTests|FullyQualifiedName~ChatMarkdown" 2>&1 | tail -5`
Expected: all pass. Then the full suite. Check escapes in the new test file (it holds `\pi` and `\|` on purpose: those are C# escapes of backslashes, not decoded characters).

- [ ] **Step 5: Commit**

```bash
git add Services/Pad/MarkdownLineTokenizer.cs tests/Kil0bitSystemMonitor.Tests/MarkdownExtrasTests.cs
git commit -m "feat(pad): Markdown tables, setext headings, front matter, nested quotes, definitions, footnotes, sub/superscript, keys, tags, attributes, inline math and abbreviations" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 4: Drawing it — reading font, monospace parts, baselines, quote levels and callouts

**Files:**
- Modify: `Pad/MarkdownColorizer.cs`, `Pad/MarkdownBackgroundRenderer.cs`
- Modify: `Pad/EditorLanguage.cs` (`MonoFont`), `Pad/MicaPadWindow.xaml.cs` (`ReadingFont`, `ApplyEditorFont`), `Models/SystemMetrics.cs` (`PadReadingFont`)
- Test: `tests/Kil0bitSystemMonitor.Tests/MarkdownLookTests.cs` (rendering facts), `tests/Kil0bitSystemMonitor.Tests/PadConfigTests.cs`, `tests/Kil0bitSystemMonitor.Tests/PadLanguageWindowTests.cs`

**Interfaces:**
- Consumes: `MarkdownDocumentCache.FactsOf`, `AbbreviationsOf` (Task 2); `MarkdownLineTokenizer.Tokenize(string, MdLineFacts, IReadOnlySet<string>?)`, `BlockOf(string, MdLineFacts)`, `QuoteDepth` (Task 3); `MdLook.Mono/Baseline/Dotted`, `MarkdownStyles.CalloutColor`, `CalloutTintAlpha` (Tasks 1–2).
- Produces:
  - `MarkdownColorizer(MarkdownDocumentCache cache, Func<PadPalette> palette, Action<Exception>? onFailure = null, Func<FontFamily?>? monoFont = null)` — `monoFont` returns the editor's monospace family while the reading font is on, else null.
  - `MarkdownBackgroundRenderer.QuoteBarWidth` (3), `MarkdownBackgroundRenderer.QuoteIndent` (8).
  - `EditorLanguage.MonoFont : FontFamily?`.
  - `MicaPadWindow.ReadingFont` (static `FontFamily`, source `"Segoe UI Variable Text, Segoe UI"`).
  - `AppConfig.PadReadingFont` (bool, default true).

- [ ] **Step 1: Write the failing tests**

Add to `tests/Kil0bitSystemMonitor.Tests/MarkdownLookTests.cs` (add the usings `System.Linq`, `System.Windows`, `System.Windows.Media`, `ICSharpCode.AvalonEdit.Document`, `ICSharpCode.AvalonEdit.Rendering`, `Kil0bitSystemMonitor.Pad`, and the aliases `using FontFamily = System.Windows.Media.FontFamily;`, `using Size = System.Windows.Size;`, `using Color = System.Windows.Media.Color;`):

```csharp
        private static readonly FontFamily Mono = new("Consolas");

        private static TextView Render(string text, out MarkdownDocumentCache cache)
        {
            cache = new MarkdownDocumentCache();
            var view = new TextView { Document = new TextDocument(text) };
            view.LineTransformers.Add(new MarkdownColorizer(cache, () => PadPalette.Dark, null, () => Mono));
            view.Measure(new Size(800, 600));
            view.Arrange(new Rect(0, 0, 800, 600));
            view.EnsureVisualLines();
            return view;
        }

        /// <summary>The element of line <paramref name="lineNumber"/> holding column <paramref name="column"/> (0-based).</summary>
        private static VisualLineElement ElementAt(TextView view, int lineNumber, int column) =>
            view.GetVisualLine(lineNumber)!.Elements.First(e => e.RelativeTextOffset <= column && column < e.RelativeTextOffset + e.DocumentLength);

        [Fact]
        public void Code_tables_and_front_matter_are_monospace_and_prose_is_not() => UiThread.Run(() =>
        {
            var view = Render("---\nk: v\n---\ntext `code` here\n```\nx\n```\n| a | b |\n|---|---|", out _);

            Assert.Equal("Consolas", ElementAt(view, 2, 0).TextRunProperties.Typeface.FontFamily.Source);   // front matter
            Assert.NotEqual("Consolas", ElementAt(view, 4, 0).TextRunProperties.Typeface.FontFamily.Source); // prose
            Assert.Equal("Consolas", ElementAt(view, 4, 6).TextRunProperties.Typeface.FontFamily.Source);   // inline code
            Assert.Equal("Consolas", ElementAt(view, 6, 0).TextRunProperties.Typeface.FontFamily.Source);   // fenced
            Assert.Equal("Consolas", ElementAt(view, 8, 2).TextRunProperties.Typeface.FontFamily.Source);   // table header
            Assert.Equal(FontWeights.Bold, ElementAt(view, 8, 2).TextRunProperties.Typeface.Weight);
        });

        [Fact]
        public void Scripts_are_small_and_shifted_and_abbreviations_dotted() => UiThread.Run(() =>
        {
            var view = Render("H~2~O x^3^\n*[AB]: a b\nsee AB", out _);
            double size = ElementAt(view, 1, 0).TextRunProperties.FontRenderingEmSize;

            var sub = ElementAt(view, 1, 2).TextRunProperties;
            Assert.Equal(BaselineAlignment.Subscript, sub.BaselineAlignment);
            Assert.Equal(size * 0.75, sub.FontRenderingEmSize, 3);
            Assert.Equal(BaselineAlignment.Superscript, ElementAt(view, 1, 8).TextRunProperties.BaselineAlignment);

            var abbreviation = ElementAt(view, 3, 4).TextRunProperties.TextDecorations;
            Assert.NotNull(abbreviation);
            Assert.Contains(abbreviation!, d => d.Location == TextDecorationLocation.Underline && d.Pen?.DashStyle == DashStyles.Dot);
        });

        [Fact]
        public void Without_a_mono_font_nothing_changes_family() => UiThread.Run(() =>
        {
            var cache = new MarkdownDocumentCache();
            var view = new TextView { Document = new TextDocument("text `code`\n```\nx\n```") };
            view.LineTransformers.Add(new MarkdownColorizer(cache, () => PadPalette.Dark));
            view.Measure(new Size(800, 600));
            view.Arrange(new Rect(0, 0, 800, 600));
            view.EnsureVisualLines();

            string family = ElementAt(view, 1, 0).TextRunProperties.Typeface.FontFamily.Source;
            Assert.Equal(family, ElementAt(view, 1, 6).TextRunProperties.Typeface.FontFamily.Source);
            Assert.Equal(family, ElementAt(view, 3, 0).TextRunProperties.Typeface.FontFamily.Source);
        });

        [Fact]
        public void Quotes_draw_a_bar_per_level_and_callouts_a_tint() => UiThread.Run(() =>
        {
            var cache = new MarkdownDocumentCache();
            var view = new TextView { Document = new TextDocument("> > a\n> b\n{.is-danger}\n> plain") };
            view.Measure(new Size(800, 600));
            view.Arrange(new Rect(0, 0, 800, 600));
            view.EnsureVisualLines();
            var renderer = new MarkdownBackgroundRenderer(cache, () => PadPalette.Dark);

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen()) renderer.Draw(view, context);
            var rects = Rectangles(visual.Drawing).ToList();

            var danger = PadPalette.Dark.MdCalloutDanger;
            Color Of(PadColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
            double top1 = view.GetVisualLine(1)!.VisualTop;
            Assert.Contains(rects, r => r.Rect.X == 0 && r.Rect.Width == 3 && r.Rect.Y == top1 && r.Color == Of(danger));
            Assert.Contains(rects, r => r.Rect.X == 8 && r.Rect.Width == 3 && r.Rect.Y == top1 && r.Color == Of(danger));
            Assert.Contains(rects, r => r.Rect.Width > 100 && r.Rect.Y == top1 && r.Color == Of(danger with { A = 0x26 }));

            double top4 = view.GetVisualLine(4)!.VisualTop;
            Assert.Contains(rects, r => r.Rect.X == 0 && r.Rect.Y == top4 && r.Color == Of(PadPalette.Dark.MdQuoteBar));
            Assert.DoesNotContain(rects, r => r.Rect.Y == top4 && r.Rect.Width > 100);
        });

        private static System.Collections.Generic.IEnumerable<(Rect Rect, Color Color)> Rectangles(Drawing? drawing)
        {
            switch (drawing)
            {
                case DrawingGroup group:
                    foreach (var child in group.Children)
                        foreach (var r in Rectangles(child)) yield return r;
                    break;
                case GeometryDrawing { Geometry: RectangleGeometry rect, Brush: SolidColorBrush brush }:
                    yield return (rect.Rect, brush.Color);
                    break;
            }
        }
```

Add to `tests/Kil0bitSystemMonitor.Tests/PadConfigTests.cs`:

```csharp
        [Fact]
        public void The_reading_font_is_on_by_default_and_survives_a_round_trip()
        {
            Assert.True(new AppConfig().PadReadingFont);
            Assert.True(JsonSerializer.Deserialize<AppConfig>("{\"PadMarkdown\": true}")!.PadReadingFont);
            var back = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig { PadReadingFont = false }))!;
            Assert.False(back.PadReadingFont);
        }
```

Add to `tests/Kil0bitSystemMonitor.Tests/PadLanguageWindowTests.cs`:

```csharp
        [Fact]
        public void The_reading_font_is_for_markdown_tabs_only_and_follows_the_setting() => WithWindow((window, env, config) =>
        {
            Assert.Equal(MicaPadWindow.ReadingFont.Source, window.Editor.FontFamily.Source);
            Assert.StartsWith("Cascadia Mono", window.LanguageView.MonoFont!.Source);

            config.PadReadingFont = false;
            Assert.StartsWith("Cascadia Mono", window.Editor.FontFamily.Source);
            Assert.Null(window.LanguageView.MonoFont);

            config.PadReadingFont = true;
            Assert.Equal(MicaPadWindow.ReadingFont.Source, window.Editor.FontFamily.Source);
            OpenFile(window, env, "data.json", "{ \"a\": 1 }");
            Assert.StartsWith("Cascadia Mono", window.Editor.FontFamily.Source);
            Assert.Null(window.LanguageView.MonoFont);
        });
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~MarkdownLookTests|FullyQualifiedName~PadConfigTests|FullyQualifiedName~PadLanguageWindowTests" 2>&1 | tail -5`
Expected: build errors (no `monoFont` parameter, no `PadReadingFont`, no `ReadingFont`).

- [ ] **Step 3: Implement the colorizer**

Replace `Pad/MarkdownColorizer.cs` with:

```csharp
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Markdown styled source (spec 2.3 and the Wiki.js spec 1-5): each run of a line gets the look
    /// <see cref="MarkdownStyles.LookOf"/> gives it — heading sizes, bold, italic, strike, link and
    /// marker colors, monospace code, raised and lowered scripts, dotted abbreviations. While the
    /// reading font is on (a mono family is given), code, table and front-matter lines are drawn in
    /// the editor's monospace font (R2). Only how text is drawn changes; the document is never touched.
    /// </summary>
    internal sealed class MarkdownColorizer : DocumentColorizingTransformer
    {
        private readonly MarkdownDocumentCache _cache;
        private readonly Func<PadPalette> _palette;
        private readonly Action<Exception> _onFailure;
        private readonly Func<FontFamily?> _monoFont;
        private readonly Dictionary<PadColor, Brush> _brushes = new();
        private readonly Dictionary<PadColor, TextDecoration> _dotted = new();

        /// <param name="onFailure">Told when a line cannot be formatted; that line is left as it is.</param>
        /// <param name="monoFont">The editor's monospace family while the reading font is on, else null.</param>
        public MarkdownColorizer(MarkdownDocumentCache cache, Func<PadPalette> palette, Action<Exception>? onFailure = null, Func<FontFamily?>? monoFont = null)
        {
            _cache = cache;
            _palette = palette;
            _onFailure = onFailure ?? (_ => { });
            _monoFont = monoFont ?? (() => null);
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            try
            {
                var document = CurrentContext.Document;
                var facts = _cache.FactsOf(document, line.LineNumber);
                var md = MarkdownLineTokenizer.Tokenize(document.GetText(line), facts, _cache.AbbreviationsOf(document));
                var palette = _palette();
                var mono = _monoFont();
                double baseSize = CurrentContext.GlobalTextRunProperties.FontRenderingEmSize;

                if (mono != null && line.Length > 0 && (md.Block is MdBlock.Fence or MdBlock.Table or MdBlock.FrontMatter))
                    ChangeLinePart(line.Offset, line.EndOffset, element => SetFamily(element, mono));

                foreach (var span in md.Spans)
                {
                    var look = MarkdownStyles.LookOf(span.Style, palette);
                    int start = line.Offset + span.Start;
                    ChangeLinePart(start, start + span.Length, element => Apply(element, look, baseSize, mono, palette));
                }
            }
            catch (Exception ex)
            {
                _onFailure(ex);
            }
        }

        private void Apply(VisualLineElement element, MdLook look, double baseSize, FontFamily? mono, PadPalette palette)
        {
            var properties = element.TextRunProperties;
            if (look.Foreground is PadColor foreground) properties.SetForegroundBrush(BrushFor(foreground));
            if (look.Background is PadColor background) properties.SetBackgroundBrush(BrushFor(background));
            if (look.SizeFactor != 1) properties.SetFontRenderingEmSize(baseSize * look.SizeFactor);
            if (look.Mono && mono != null) SetFamily(element, mono);

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

            if (look.Baseline != MdBaseline.Normal)
                properties.SetBaselineAlignment(look.Baseline == MdBaseline.Superscript ? BaselineAlignment.Superscript : BaselineAlignment.Subscript);
            if (look.Strike) AddDecorations(properties, TextDecorations.Strikethrough);
            if (look.Dotted) AddDecorations(properties, new[] { DottedUnderline(palette.MdMarker) });
        }

        private static void SetFamily(VisualLineElement element, FontFamily family)
        {
            var face = element.TextRunProperties.Typeface;
            element.TextRunProperties.SetTypeface(new Typeface(family, face.Style, face.Weight, face.Stretch));
        }

        private static void AddDecorations(VisualLineElementTextRunProperties properties, IEnumerable<TextDecoration> added)
        {
            var decorations = properties.TextDecorations == null
                ? new TextDecorationCollection()
                : new TextDecorationCollection(properties.TextDecorations);
            foreach (var decoration in added) decorations.Add(decoration);
            decorations.Freeze();
            properties.SetTextDecorations(decorations);
        }

        private TextDecoration DottedUnderline(PadColor color)
        {
            if (_dotted.TryGetValue(color, out var decoration)) return decoration;
            var pen = new Pen(BrushFor(color), 1) { DashStyle = DashStyles.Dot };
            pen.Freeze();
            decoration = new TextDecoration(TextDecorationLocation.Underline, pen, 0, TextDecorationUnit.FontRecommended, TextDecorationUnit.FontRecommended);
            decoration.Freeze();
            _dotted[color] = decoration;
            return decoration;
        }

        private Brush BrushFor(PadColor color)
        {
            if (!_brushes.TryGetValue(color, out var brush)) _brushes[color] = brush = PadThemeApplier.ToBrush(color);
            return brush;
        }
    }
}
```

- [ ] **Step 4: Implement the background renderer**

In `Pad/MarkdownBackgroundRenderer.cs`:

1. Add after the class's opening brace:

```csharp
        /// <summary>A quote bar's width and the distance between the bars of nested quotes.</summary>
        internal const double QuoteBarWidth = 3;
        internal const double QuoteIndent = 8;
```

2. Change the class comment to: "What Markdown draws behind the text: shading across fenced code and front matter, a bar per quote level at the left (in the callout's color, over a tint of it, for a Wiki.js callout), and a line through horizontal rules. Only visible lines are drawn."

3. Replace the loop body in `DrawVisibleLines` (from `var line = visual.FirstDocumentLine;` to the end of the `switch`) with:

```csharp
                var line = visual.FirstDocumentLine;
                var facts = _cache.FactsOf(document, line.LineNumber);
                string text = document.GetText(line);
                var block = MarkdownLineTokenizer.BlockOf(text, facts);
                double top = visual.VisualTop - textView.VerticalOffset;
                double height = visual.Height;

                switch (block)
                {
                    case MdBlock.Fence:
                    case MdBlock.FrontMatter:
                        drawingContext.DrawRectangle(BrushFor(palette.MdCodeBackground), null, new Rect(0, top, width, height));
                        break;
                    case MdBlock.Quote:
                        var bar = palette.MdQuoteBar;
                        if (facts.Callout != MdCallout.None)
                        {
                            bar = MarkdownStyles.CalloutColor(facts.Callout, palette);
                            drawingContext.DrawRectangle(BrushFor(bar with { A = MarkdownStyles.CalloutTintAlpha }), null, new Rect(0, top, width, height));
                        }
                        int depth = Math.Max(1, MarkdownLineTokenizer.QuoteDepth(text));
                        for (int level = 0; level < depth; level++)
                            drawingContext.DrawRectangle(BrushFor(bar), null, new Rect(level * QuoteIndent, top, QuoteBarWidth, height));
                        break;
                    case MdBlock.Rule:
                        double y = Math.Round(top + height / 2) + 0.5;
                        drawingContext.DrawLine(new Pen(BrushFor(palette.MdRule), 1), new Point(0, y), new Point(width, y));
                        break;
                }
```

- [ ] **Step 5: The reading font**

In `Models/SystemMetrics.cs` (`AppConfig`), add the field `private bool _padReadingFont = true;` next to the other MicaPad fields and, after `PadKrokiServer`:

```csharp
        /// <summary>
        /// Prose in Markdown tabs in a reading font (Segoe UI Variable Text); code, inline code and
        /// tables stay in the editor font (Wiki.js spec 1.1).
        /// </summary>
        public bool PadReadingFont { get => _padReadingFont; set { Set(ref _padReadingFont, value); } }
```

In `Pad/EditorLanguage.cs`:

1. Add `using FontFamily = System.Windows.Media.FontFamily;` with the other usings.
2. Add the property after `DiagramBoard`:

```csharp
        /// <summary>The editor's monospace family while the reading font is on (the window sets it); null otherwise.</summary>
        internal FontFamily? MonoFont { get; set; }
```

3. In `Apply`, change `_markdown = new MarkdownColorizer(_markdownCache, _palette, ReportFailure);` to `_markdown = new MarkdownColorizer(_markdownCache, _palette, ReportFailure, () => MonoFont);`.

In `Pad/MicaPadWindow.xaml.cs`:

1. Add next to `ApplyEditorSettings`:

```csharp
        /// <summary>Prose in Markdown tabs while Settings → MicaPad → Reading font is on (Wiki.js spec 1.1).</summary>
        internal static readonly FontFamily ReadingFont = new("Segoe UI Variable Text, Segoe UI");

        /// <summary>
        /// The editor's font for the shown tab: the reading font for a Markdown tab while it is on,
        /// with code, inline code and tables kept in the editor font; the editor font otherwise.
        /// </summary>
        private void ApplyEditorFont()
        {
            var mono = new FontFamily(_config.PadFontFamily + ", Cascadia Mono, Consolas");
            bool reading = _config.PadReadingFont && ReferenceEquals(_language.Current, PadLanguages.Markdown);
            Editor.FontFamily = reading ? ReadingFont : mono;
            _language.MonoFont = reading ? mono : null;
            _language.Redraw();
        }
```

2. In `ApplyEditorSettings`, replace `Editor.FontFamily = new FontFamily(_config.PadFontFamily + ", Cascadia Mono, Consolas");` with `ApplyEditorFont();`.
3. In `ApplyLanguage`, after `_language.Apply(_resolved.Effective);`, add `ApplyEditorFont();`.
4. In `OnConfigChanged`, add `or nameof(AppConfig.PadReadingFont)` to the first branch's condition (the one that calls `ApplyEditorSettings`).

- [ ] **Step 6: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~MarkdownLookTests|FullyQualifiedName~PadConfigTests|FullyQualifiedName~PadLanguageWindowTests|FullyQualifiedName~MarkdownRenderingTests|FullyQualifiedName~PadCopyRtfTests|FullyQualifiedName~DiagramBoardTests|FullyQualifiedName~DiagramWindowTests" 2>&1 | tail -5`
Expected: all pass. Then the full suite.

- [ ] **Step 7: Commit**

```bash
git add Pad/MarkdownColorizer.cs Pad/MarkdownBackgroundRenderer.cs Pad/EditorLanguage.cs Pad/MicaPadWindow.xaml.cs Models/SystemMetrics.cs tests/Kil0bitSystemMonitor.Tests/MarkdownLookTests.cs tests/Kil0bitSystemMonitor.Tests/PadConfigTests.cs tests/Kil0bitSystemMonitor.Tests/PadLanguageWindowTests.cs
git commit -m "feat(pad): reading font for Markdown prose with code and tables in the editor font; scripts, dotted abbreviations, nested quote bars and Wiki.js callouts" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 5: Colors inside fenced code

**Files:**
- Create: `Pad/SyntaxPaint.cs`, `Pad/FenceHighlighter.cs`
- Modify: `Pad/ThemedHighlightingColorizer.cs` (uses `SyntaxPaint`), `Pad/MarkdownColorizer.cs` (paints fence lines)
- Test: `tests/Kil0bitSystemMonitor.Tests/FenceColorsTests.cs`

**Interfaces:**
- Consumes: `MarkdownDocumentCache.BlockOpeningOf` (Task 2), `FenceLanguages.IdOfFence` (Task 1), `PadLanguages.ById`, `PadHighlighting.For`, `SyntaxColors.Resolve` (existing), `MarkdownColorizer` (Task 4).
- Produces:
  - `internal static class SyntaxPaint` — `Apply(VisualLineElement element, HighlightingColor color, PadPalette palette, ITextRunConstructionContext context, Func<PadColor, Brush> brushFor)`.
  - `internal sealed class FenceHighlighter` — `FenceHighlighter(MarkdownDocumentCache cache)`, `const int MaxBlockLines = 2000`, `HighlightedLine? HighlightLine(TextDocument document, int lineNumber)`.

- [ ] **Step 1: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/FenceColorsTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using Brush = System.Windows.Media.Brush;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Colors inside fenced code (spec 1.3): the same as a whole file in that language.</summary>
    public class FenceColorsTests
    {
        private static TextView Render(string text)
        {
            var cache = new MarkdownDocumentCache();
            var view = new TextView { Document = new TextDocument(text) };
            view.LineTransformers.Add(new MarkdownColorizer(cache, () => PadPalette.Dark));
            view.Measure(new Size(1200, 800));
            view.Arrange(new Rect(0, 0, 1200, 800));
            view.EnsureVisualLines();
            return view;
        }

        private static PadColor? ForegroundAt(TextView view, int lineNumber, int column)
        {
            var element = view.GetVisualLine(lineNumber)!.Elements.First(e => e.RelativeTextOffset <= column && column < e.RelativeTextOffset + e.DocumentLength);
            return element.TextRunProperties.ForegroundBrush is SolidColorBrush b ? new PadColor(b.Color.A, b.Color.R, b.Color.G, b.Color.B) : null;
        }

        /// <summary>What whole-file highlighting paints at each column of line <paramref name="lineNumber"/> of <paramref name="code"/>: the last section covering it with a palette color wins.</summary>
        private static Dictionary<int, PadColor> WholeFileColors(string languageId, string code, int lineNumber)
        {
            var definition = PadHighlighting.For(PadLanguages.ById(languageId)!)!;
            var document = new TextDocument(code);
            var highlighter = new DocumentHighlighter(document, definition);
            var colors = new Dictionary<int, PadColor>();
            var line = document.GetLineByNumber(lineNumber);
            foreach (var section in highlighter.HighlightLine(lineNumber).Sections)
            {
                var color = section.Color;
                PadColor? original = color?.Foreground?.GetColor(null) is Color c ? new PadColor(c.A, c.R, c.G, c.B) : null;
                if (color == null || SyntaxColors.Resolve(color.Name, original, PadPalette.Dark) is not PadColor paint) continue;
                for (int i = section.Offset; i < section.Offset + section.Length; i++) colors[i - line.Offset] = paint;
            }
            return colors;
        }

        [Fact]
        public void A_csharp_fence_is_colored_like_a_csharp_file() => UiThread.Run(() =>
        {
            const string code = "public class A { string s = \"x\"; } // done";
            var view = Render("text\n```cs\n" + code + "\n```");
            var expected = WholeFileColors("csharp", code, 1);

            Assert.NotEmpty(expected);
            foreach (var (column, color) in expected) Assert.Equal(color, ForegroundAt(view, 3, column));
        });

        [Fact]
        public void A_comment_that_spans_lines_stays_a_comment() => UiThread.Run(() =>
        {
            const string code = "/* start\nend */ int x;";
            var view = Render("```csharp\n" + code + "\n```");
            var expected = WholeFileColors("csharp", code, 2);

            Assert.True(expected.ContainsKey(0));
            Assert.Equal(expected[0], ForegroundAt(view, 3, 0));
        });

        [Fact]
        public void An_unknown_language_is_not_colored() => UiThread.Run(() =>
        {
            var view = Render("plain\n```bash\necho hi\n```");

            Assert.Equal(ForegroundAt(view, 1, 0), ForegroundAt(view, 3, 0));
        });

        [Fact]
        public void A_block_over_2000_lines_is_not_colored_and_edits_are_followed() => UiThread.Run(() =>
        {
            string body = string.Join("\n", Enumerable.Repeat("int x;", 2001));
            var document = new TextDocument("```cs\n" + body + "\n```");
            var highlighter = new FenceHighlighter(new MarkdownDocumentCache());

            Assert.NotNull(highlighter.HighlightLine(document, 2));
            Assert.Null(highlighter.HighlightLine(document, 2002));
            Assert.Null(highlighter.HighlightLine(document, 1));   // the fence line itself

            var small = new TextDocument("```cs\nint x;\n```");
            var line = highlighter.HighlightLine(small, 2)!;
            int sections = line.Sections.Count;
            small.Insert(small.GetLineByNumber(2).EndOffset, " // note");
            Assert.True(highlighter.HighlightLine(small, 2)!.Sections.Count > sections);
        });
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~FenceColorsTests" 2>&1 | tail -5`
Expected: build error, `FenceHighlighter` does not exist.

- [ ] **Step 3: Implement**

`Pad/SyntaxPaint.cs`:

```csharp
using System;
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
    /// Paints one highlighting color from the MicaPad palette, for whole-file highlighting and for
    /// the colors inside fenced code alike: a named color takes its category's palette color; an
    /// unnamed one keeps its hue, nudged until it reads at 4.5:1 (<see cref="SyntaxColors.Resolve"/>).
    /// Bold, italic, underline and strikethrough are kept; definition backgrounds are dropped.
    /// </summary>
    internal static class SyntaxPaint
    {
        public static void Apply(VisualLineElement element, HighlightingColor color, PadPalette palette,
                                 ITextRunConstructionContext context, Func<PadColor, Brush> brushFor)
        {
            var properties = element.TextRunProperties;

            PadColor? original = color.Foreground?.GetColor(context) is Color c ? new PadColor(c.A, c.R, c.G, c.B) : null;
            if (SyntaxColors.Resolve(color.Name, original, palette) is PadColor paint)
                properties.SetForegroundBrush(brushFor(paint));

            if (color.FontWeight != null || color.FontStyle != null)
            {
                var face = properties.Typeface;
                properties.SetTypeface(new Typeface(face.FontFamily, color.FontStyle ?? face.Style, color.FontWeight ?? face.Weight, face.Stretch));
            }
            if (color.Underline == true) properties.SetTextDecorations(TextDecorations.Underline);
            if (color.Strikethrough == true) properties.SetTextDecorations(TextDecorations.Strikethrough);
        }
    }
}
```

In `Pad/ThemedHighlightingColorizer.cs`, replace the body of `ApplyColorToElement` with:

```csharp
            try
            {
                SyntaxPaint.Apply(element, color, _palette(), CurrentContext, BrushFor);
            }
            catch (Exception ex)
            {
                _onFailure(ex);
            }
```

(and remove the `using Color = …` alias if it is no longer used).

`Pad/FenceHighlighter.cs`:

```csharp
using System.Collections.Generic;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Utils;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// The colors of the lines inside a fenced block whose info word names a MicaPad language (spec
    /// 1.3). AvalonEdit's highlighting engine runs over the block from its first inside line with an
    /// empty state, so a comment or string that spans lines is colored as in a whole file. Each
    /// block is highlighted once per document version, line by line as far down as it is shown.
    /// A block over <see cref="MaxBlockLines"/> lines, or one with a line over the inline limit,
    /// gets no colors from there on.
    /// </summary>
    internal sealed class FenceHighlighter
    {
        internal const int MaxBlockLines = 2000;

        private readonly MarkdownDocumentCache _cache;
        private readonly Dictionary<int, Block> _blocks = new();
        private ITextSourceVersion? _version;

        public FenceHighlighter(MarkdownDocumentCache cache) => _cache = cache;

        /// <summary>The highlighting of inside line <paramref name="lineNumber"/>, or null when it gets no colors.</summary>
        public HighlightedLine? HighlightLine(TextDocument document, int lineNumber)
        {
            int open = _cache.BlockOpeningOf(document, lineNumber);
            if (open == 0 || lineNumber - open > MaxBlockLines) return null;

            if (!ReferenceEquals(document.Version, _version))
            {
                _blocks.Clear();
                _version = document.Version;
            }
            if (!_blocks.TryGetValue(open, out var block))
            {
                block = Start(document, open);
                _blocks[open] = block;
            }

            int index = lineNumber - open - 1;
            while (block.Engine != null && block.Lines.Count <= index)
            {
                var line = document.GetLineByNumber(open + 1 + block.Lines.Count);
                if (line.Length > MarkdownLineTokenizer.MaxInlineLength)
                {
                    block.Engine = null;
                    break;
                }
                block.Engine.CurrentSpanStack = block.Stack;
                block.Lines.Add(block.Engine.HighlightLine(document, line));
                block.Stack = block.Engine.CurrentSpanStack;
            }
            return index < block.Lines.Count ? block.Lines[index] : null;
        }

        private static Block Start(TextDocument document, int open)
        {
            string? id = FenceLanguages.IdOfFence(document.GetText(document.GetLineByNumber(open)));
            var language = PadLanguages.ById(id);
            var definition = language == null ? null : PadHighlighting.For(language);
            return new Block(definition == null ? null : new HighlightingEngine(definition.MainRuleSet));
        }

        private sealed class Block
        {
            public Block(HighlightingEngine? engine) => Engine = engine;

            public HighlightingEngine? Engine;

            public ImmutableStack<HighlightingSpan> Stack = ImmutableStack<HighlightingSpan>.Empty;

            public readonly List<HighlightedLine> Lines = new();
        }
    }
}
```

In `Pad/MarkdownColorizer.cs`:

1. Add the field `private readonly FenceHighlighter _fences;` and in the constructor `_fences = new FenceHighlighter(cache);`.
2. Add `using ICSharpCode.AvalonEdit.Highlighting;`.
3. In `ColorizeLine`, right after the whole-line monospace `ChangeLinePart` (before the `foreach (var span …)`), add:

```csharp
                if (facts.Fence == MdFence.Inside && _fences.HighlightLine(document, line.LineNumber) is { } highlighted)
                {
                    foreach (var section in highlighted.Sections)
                    {
                        if (section.Color == null || section.Length == 0) continue;
                        var color = section.Color;
                        ChangeLinePart(section.Offset, section.Offset + section.Length,
                            element => SyntaxPaint.Apply(element, color, palette, CurrentContext, BrushFor));
                    }
                }
```

4. Add to the class comment: "Lines inside a fence whose info word names a MicaPad language get that language's colors (<see cref="FenceHighlighter"/>)."

- [ ] **Step 4: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~FenceColorsTests|FullyQualifiedName~PadHighlightingTests|FullyQualifiedName~MarkdownLookTests|FullyQualifiedName~PadLanguageWindowTests|FullyQualifiedName~SyntaxColorsTests" 2>&1 | tail -5`
Expected: all pass. Then the full suite.

- [ ] **Step 5: Commit**

```bash
git add Pad/SyntaxPaint.cs Pad/FenceHighlighter.cs Pad/ThemedHighlightingColorizer.cs Pad/MarkdownColorizer.cs tests/Kil0bitSystemMonitor.Tests/FenceColorsTests.cs
git commit -m "feat(pad): fenced code is colored by its language, the same as a whole file in that language" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 6: Format → Format table

**Files:**
- Create: `Services/Pad/TableFormatter.cs`
- Modify: `Pad/EditorMenus.cs` (`FormatMenu` gains "Format table")
- Test: `tests/Kil0bitSystemMonitor.Tests/TableFormatterTests.cs`

**Interfaces:**
- Consumes: `MarkdownStructure.Scan`, `MdTableRole`, `TableCells.Split` (Task 2); `TextEdit` (existing, `Services/Pad/MarkdownFormatter.cs`); `EditorMenus.Item`, `EditorMenus.ApplyEdit` (existing, private to `EditorMenus`).
- Produces: `static class TableFormatter` — `(int First, int Last)? TableAt(string text, int offset)` (0-based line indexes), `TextEdit? Format(string text, int offset)`, `int DisplayWidth(string s)`.

- [ ] **Step 1: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/TableFormatterTests.cs`:

```csharp
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Format → Format table (spec 3).</summary>
    public class TableFormatterTests
    {
        private static string Apply(string text, int offset)
        {
            var edit = TableFormatter.Format(text, offset);
            Assert.NotNull(edit);
            return text.Substring(0, edit!.Value.Offset) + edit.Value.Text + text.Substring(edit.Value.Offset + edit.Value.Length);
        }

        [Fact]
        public void Columns_line_up_by_their_alignment()
        {
            string text = "| Name | Qty |\n|:-|-:|\n| apple | 3 |\n| kiwi | 12 |";

            Assert.Equal("| Name  | Qty |\n| :---- | --: |\n| apple |   3 |\n| kiwi  |  12 |", Apply(text, 20));
        }

        [Fact]
        public void Centered_columns_and_tables_without_outer_pipes()
        {
            string text = "a|b\n:-:|---\nlong text|x";

            Assert.Equal("|     a     | b   |\n| :-------: | --- |\n| long text | x   |", Apply(text, 0));
        }

        [Fact]
        public void Uneven_rows_get_empty_cells_and_extra_cells_become_columns()
        {
            string text = "| a | b |\n|---|---|\n| 1 |\n| 1 | 2 | 3 |";

            Assert.Equal("| a   | b   |     |\n| --- | --- | --- |\n| 1   |     |     |\n| 1   | 2   | 3   |", Apply(text, 0));
        }

        [Fact]
        public void Wide_and_combining_characters_and_escaped_pipes_line_up_and_survive()
        {
            string thai = "\u0E1C\u0E25\u0E44\u0E21\u0E49";   // 4 columns: the tone mark takes none
            string han = "\u679C\u7269";                       // 4 columns: two wide characters
            string text = "| " + thai + " | " + han + " |\n|---|---|\n| a \\| b | x |";

            string result = Apply(text, 0);

            Assert.Equal("| " + thai + "   | " + han + " |\n| ------ | ---- |\n| a \\| b | x    |", result);
            var pipeColumns = result.Split('\n').Select(line =>
                TableCells.Pipes(line).Select(p => TableFormatter.DisplayWidth(line.Substring(0, p))).ToArray()).ToList();
            Assert.All(pipeColumns, columns => Assert.Equal(pipeColumns[0], columns));
        }

        [Theory]
        [InlineData("abc", 3)]
        [InlineData("\u0E2A\u0E27\u0E31\u0E2A\u0E14\u0E35", 4)]
        [InlineData("\u65E5\u672C", 4)]
        [InlineData("", 0)]
        public void Display_width_counts_wide_characters_twice_and_marks_never(string text, int width) =>
            Assert.Equal(width, TableFormatter.DisplayWidth(text));

        [Fact]
        public void Line_endings_and_the_text_around_the_table_are_kept()
        {
            string text = "before\r\n| a | b |\r\n|-|-|\r\n| 1 | 2 |\r\nafter";

            Assert.Equal("before\r\n| a   | b   |\r\n| --- | --- |\r\n| 1   | 2   |\r\nafter", Apply(text, 12));
        }

        [Fact]
        public void A_formatted_table_stays_the_same_and_other_text_is_no_table()
        {
            string formatted = "| a   | b   |\n| --- | --- |\n| 1   | 2   |";

            Assert.Equal(formatted, Apply(formatted, 30));
            Assert.Null(TableFormatter.Format("just text\nmore", 3));
            Assert.Null(TableFormatter.TableAt("a | b\nc | d", 0));
            Assert.Equal((1, 3), TableFormatter.TableAt("x\n| a | b |\n|---|---|\n| 1 | 2 |\n\ny", 30));
        }

        [Fact]
        public void The_menu_item_formats_in_one_undo_step_and_is_off_outside_tables() => UiThread.Run(() =>
        {
            string text = "| a | bb |\n|-|-|\n| 1 | 2 |\n\nprose";
            var editor = new TextEditor { Document = new TextDocument(text) };

            editor.CaretOffset = 2;
            var item = EditorMenus.FormatMenu(editor).Items.OfType<MenuItem>().Single(i => (string)i.Header == "Format table");
            Assert.True(item.IsEnabled);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.StartsWith("| a   | bb  |\n| --- | --- |", editor.Document.Text);

            editor.Undo();
            Assert.Equal(text, editor.Document.Text);

            editor.CaretOffset = text.Length - 1;
            Assert.False(EditorMenus.FormatMenu(editor).Items.OfType<MenuItem>().Single(i => (string)i.Header == "Format table").IsEnabled);
        });
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~TableFormatterTests" 2>&1 | tail -5`
Expected: build error, `TableFormatter` does not exist.

- [ ] **Step 3: Implement**

`Services/Pad/TableFormatter.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Format → Format table (spec 3): pads a pipe table's cells so the pipes line up by display
    /// width (East Asian wide characters count 2, combining marks 0), honoring each column's
    /// alignment. Pure: it returns one replacement for the window to apply as one undo step. The
    /// text is changed only by this command.
    /// </summary>
    public static class TableFormatter
    {
        private enum Align
        {
            None,
            Left,
            Center,
            Right,
        }

        private readonly record struct Line(int Start, string Text, string Break);

        /// <summary>The 0-based first and last line of the table around <paramref name="offset"/>, or null.</summary>
        public static (int First, int Last)? TableAt(string text, int offset)
        {
            var lines = Lines(text ?? "");
            return Range(lines, Facts(lines), LineOf(lines, offset));
        }

        /// <summary>The replacement that formats the table around <paramref name="offset"/>, or null when there is none.</summary>
        public static TextEdit? Format(string text, int offset)
        {
            var lines = Lines(text ?? "");
            int at = LineOf(lines, offset);
            if (Range(lines, Facts(lines), at) is not { } range) return null;

            var rows = new List<List<string>>();
            var aligns = new List<Align>();
            for (int i = range.First; i <= range.Last; i++)
            {
                var cells = TableCells.Split(lines[i].Text);
                if (i == range.First + 1) aligns = cells.Select(AlignOf).ToList();
                else rows.Add(cells);
            }

            int columns = Math.Max(rows.Max(r => r.Count), aligns.Count);
            var widths = new int[columns];
            for (int c = 0; c < columns; c++)
                widths[c] = Math.Max(3, rows.Max(r => c < r.Count ? DisplayWidth(r[c]) : 0));

            string indent = Indent(lines[range.First].Text);
            var output = new List<string> { Row(rows[0], widths, aligns, indent), Delimiter(widths, aligns, indent) };
            output.AddRange(rows.Skip(1).Select(row => Row(row, widths, aligns, indent)));

            string newline = lines[range.First].Break.Length > 0 ? lines[range.First].Break : "\n";
            int start = lines[range.First].Start;
            int end = lines[range.Last].Start + lines[range.Last].Text.Length;
            int caretLine = Math.Clamp(at, range.First, range.Last) - range.First;
            int caret = start + output.Take(caretLine).Sum(l => l.Length + newline.Length);
            return new TextEdit(start, end - start, string.Join(newline, output), caret, 0);
        }

        /// <summary>How many monospace columns <paramref name="s"/> takes: wide East Asian characters 2, combining marks 0, others 1.</summary>
        public static int DisplayWidth(string s)
        {
            int width = 0;
            foreach (var rune in (s ?? "").EnumerateRunes())
            {
                var category = Rune.GetUnicodeCategory(rune);
                if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) continue;
                width += IsWide(rune.Value) ? 2 : 1;
            }
            return width;
        }

        private static bool IsWide(int c) =>
            (c >= 0x1100 && c <= 0x115F) || (c >= 0x2E80 && c <= 0xA4CF && c != 0x303F) || (c >= 0xAC00 && c <= 0xD7A3)
            || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE4F) || (c >= 0xFF00 && c <= 0xFF60)
            || (c >= 0xFFE0 && c <= 0xFFE6) || (c >= 0x1F300 && c <= 0x1F64F) || (c >= 0x1F900 && c <= 0x1F9FF)
            || (c >= 0x20000 && c <= 0x3FFFD);

        private static MdLineFacts[] Facts(List<Line> lines) => MarkdownStructure.Scan(lines.Select(l => l.Text).ToList()).Facts;

        private static (int First, int Last)? Range(List<Line> lines, MdLineFacts[] facts, int at)
        {
            if (at < 0 || facts[at].Table == MdTableRole.None) return null;
            int first = at;
            while (facts[first].Table != MdTableRole.Header && first > 0 && facts[first - 1].Table != MdTableRole.None) first--;
            int last = at;
            while (last + 1 < lines.Count && facts[last + 1].Table == MdTableRole.Row) last++;
            return (first, last);
        }

        private static List<Line> Lines(string text)
        {
            var lines = new List<Line>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                int textEnd = i > start && text[i - 1] == '\r' ? i - 1 : i;
                lines.Add(new Line(start, text.Substring(start, textEnd - start), text.Substring(textEnd, i + 1 - textEnd)));
                start = i + 1;
            }
            lines.Add(new Line(start, text.Substring(start), ""));
            return lines;
        }

        private static int LineOf(List<Line> lines, int offset)
        {
            for (int i = lines.Count - 1; i >= 0; i--)
                if (offset >= lines[i].Start) return i;
            return -1;
        }

        private static Align AlignOf(string cell)
        {
            bool left = cell.StartsWith(':');
            bool right = cell.EndsWith(':');
            return left && right ? Align.Center : right ? Align.Right : left ? Align.Left : Align.None;
        }

        private static string Indent(string line)
        {
            int i = 0;
            while (i < line.Length && i < 3 && line[i] == ' ') i++;
            return line.Substring(0, i);
        }

        private static string Row(List<string> cells, int[] widths, List<Align> aligns, string indent)
        {
            var row = new StringBuilder(indent).Append('|');
            for (int c = 0; c < widths.Length; c++)
            {
                string cell = c < cells.Count ? cells[c] : "";
                int pad = widths[c] - DisplayWidth(cell);
                var align = c < aligns.Count ? aligns[c] : Align.None;
                int left = align switch { Align.Right => pad, Align.Center => pad / 2, _ => 0 };
                row.Append(' ').Append(' ', left).Append(cell).Append(' ', pad - left).Append(" |");
            }
            return row.ToString();
        }

        private static string Delimiter(int[] widths, List<Align> aligns, string indent)
        {
            var row = new StringBuilder(indent).Append('|');
            for (int c = 0; c < widths.Length; c++)
            {
                int w = widths[c];
                string dashes = (c < aligns.Count ? aligns[c] : Align.None) switch
                {
                    Align.Left => ":" + new string('-', w - 1),
                    Align.Center => ":" + new string('-', w - 2) + ":",
                    Align.Right => new string('-', w - 1) + ":",
                    _ => new string('-', w),
                };
                row.Append(' ').Append(dashes).Append(" |");
            }
            return row.ToString();
        }
    }
}
```

In `Pad/EditorMenus.cs`, in `FormatMenu`, after `Add("Code block", MarkdownFormatter.CodeBlock, "");` add:

```csharp
            format.Items.Add(new Separator());
            bool inTable = TableFormatter.TableAt(editor.Document.Text, editor.CaretOffset) != null;
            format.Items.Add(Item("Format table", null, () =>
            {
                if (TableFormatter.Format(editor.Document.Text, editor.CaretOffset) is { } edit) ApplyEdit(editor, edit);
            }, inTable, icon: "\uE8A9"));
```

and add to `FormatMenu`'s comment: "Format table lines up the table the caret is in (spec 3) and is off elsewhere."

- [ ] **Step 4: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~TableFormatterTests|FullyQualifiedName~MarkdownFormatterTests|FullyQualifiedName~PadMenuTests" 2>&1 | tail -5`
Expected: all pass. Then the full suite. Check escapes (the Thai and Han strings must stay `\uXXXX` in the test file).

- [ ] **Step 5: Commit**

```bash
git add Services/Pad/TableFormatter.cs Pad/EditorMenus.cs tests/Kil0bitSystemMonitor.Tests/TableFormatterTests.cs
git commit -m "feat(pad): Format table lines up a Markdown table by display width and alignment in one undo step" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 7: Emoji

**Files:**
- Create: `Services/Pad/Emoji.tsv` (generated from gemoji 4.1.0), `Services/Pad/Emoji.cs`, `Pad/EmojiGenerator.cs`
- Modify: `Kil0bitSystemMonitor.csproj` (embed the TSV), `Pad/EditorLanguage.cs` (installs the generator), `Pad/Diagrams/THIRD-PARTY.txt` (gemoji's license)
- Test: `tests/Kil0bitSystemMonitor.Tests/EmojiTests.cs`

**Interfaces:**
- Consumes: `MarkdownDocumentCache.FactsOf` (Task 2), `MarkdownLineTokenizer.MaxInlineLength`.
- Produces: `static class Emoji` — `int Count`, `string? GlyphOf(string name)`, `IReadOnlyList<(int Start, int Length, string Glyph)> Find(string line)`; `internal sealed class EmojiGenerator : VisualLineElementGenerator` — `EmojiGenerator(MarkdownDocumentCache cache, Action<Exception>? onFailure = null)`.

- [ ] **Step 1: Generate the emoji table**

```bash
cd /c/AIProject/kil0bit-system-monitor
curl -sSfL -o "$TEMP/gemoji-4.1.0.json" https://raw.githubusercontent.com/github/gemoji/v4.1.0/db/emoji.json
sha256sum "$TEMP/gemoji-4.1.0.json" | cut -c1-64
node -e "const fs=require('fs');const d=JSON.parse(fs.readFileSync(process.argv[1],'utf8'));const rows=[];for(const e of d){if(!e.emoji)continue;for(const a of e.aliases)rows.push(a+'\t'+e.emoji);}rows.sort((x,y)=>x<y?-1:x>y?1:0);fs.writeFileSync(process.argv[2],rows.join('\n')+'\n','utf8');" "$TEMP/gemoji-4.1.0.json" Services/Pad/Emoji.tsv
wc -l Services/Pad/Emoji.tsv; sha256sum Services/Pad/Emoji.tsv | cut -c1-64
```

Expected: the JSON hash `b174ae2aeb321b52f64adb9ff412f966a7f338839d780784dd15dcad702c2dd6`, `1913` lines, and the TSV hash `197b93665f3ffe270ffed750a1a7fc443b3c12a7920b0047491eb2307fb979e1`. Anything else: stop and report BLOCKED with what you got.

Append gemoji's license to `Pad/Diagrams/THIRD-PARTY.txt` (it ships beside the app):

```bash
T=/c/AIProject/kil0bit-system-monitor/Pad/Diagrams/THIRD-PARTY.txt
printf '\n\n==== %s ====\n\n' "gemoji 4.1.0 (MIT) - emoji names inside MicaStats.dll" >> $T && curl -sSfL https://raw.githubusercontent.com/github/gemoji/v4.1.0/LICENSE >> $T
grep -c '^==== ' $T
```

Expected: `8`.

In `Kil0bitSystemMonitor.csproj`, in the `ItemGroup` with the `.xshd` resources, add:

```xml
        <!-- Emoji names (gemoji 4.1.0, MIT), loaded by Services/Pad/Emoji.cs. -->
        <EmbeddedResource Include="Services\Pad\Emoji.tsv" LogicalName="MicaPad.Emoji.tsv" />
```

- [ ] **Step 2: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/EmojiTests.cs`:

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
    /// <summary>Emoji codes (spec 5, R5).</summary>
    public class EmojiTests
    {
        [Fact]
        public void The_table_holds_every_gemoji_name()
        {
            Assert.Equal(1913, Emoji.Count);
            Assert.Equal("\uD83D\uDE04", Emoji.GlyphOf("smile"));
            Assert.Equal("\uD83D\uDC4D", Emoji.GlyphOf("+1"));
            Assert.Equal("\uD83D\uDE80", Emoji.GlyphOf("rocket"));
            Assert.Null(Emoji.GlyphOf("not-an-emoji"));
        }

        [Fact]
        public void Codes_are_found_with_their_colons()
        {
            var found = Emoji.Find(":smile: and :tada:");

            Assert.Equal(new[] { (0, 7), (12, 6) }, found.Select(f => (f.Start, f.Length)));
            Assert.Equal("\uD83C\uDF89", found[1].Glyph);
            Assert.Single(Emoji.Find("a:b:smile:"));
            Assert.Equal(3, Emoji.Find("a:b:smile:")[0].Start);
        }

        [Theory]
        [InlineData("12:30:45")]
        [InlineData("::")]
        [InlineData(":SMILE:")]
        [InlineData("`:smile:`")]
        [InlineData(":smile")]
        [InlineData("http://example.com:8080/")]
        public void Emoji_need_a_known_name_between_colons(string line) => Assert.Empty(Emoji.Find(line));

        [Fact]
        public void The_generator_shows_a_glyph_for_the_whole_code_outside_fences() => UiThread.Run(() =>
        {
            var cache = new MarkdownDocumentCache();
            var view = new TextView { Document = new TextDocument(":rocket: go\n```\n:rocket:\n```") };
            view.ElementGenerators.Add(new EmojiGenerator(cache));
            view.Measure(new Size(600, 400));
            view.Arrange(new Rect(0, 0, 600, 400));
            view.EnsureVisualLines();

            var first = view.GetVisualLine(1)!.Elements.First();
            Assert.IsType<FormattedTextElement>(first);
            Assert.Equal(8, first.DocumentLength);
            Assert.DoesNotContain(view.GetVisualLine(3)!.Elements, e => e is FormattedTextElement);
        });

        [Fact]
        public void Markdown_tabs_get_emoji_and_others_do_not() => UiThread.Run(() =>
        {
            var editor = new ICSharpCode.AvalonEdit.TextEditor { Document = new TextDocument(":smile:") };
            var language = new EditorLanguage(editor, () => PadPalette.Dark, folds: false);

            language.Apply(PadLanguages.Markdown);
            Assert.Single(editor.TextArea.TextView.ElementGenerators.OfType<EmojiGenerator>());

            language.Apply(PadLanguages.ById("json")!);
            Assert.Empty(editor.TextArea.TextView.ElementGenerators.OfType<EmojiGenerator>());
        });
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~EmojiTests" 2>&1 | tail -5`
Expected: build error, `Emoji` does not exist.

- [ ] **Step 4: Implement**

`Services/Pad/Emoji.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// <c>:name:</c> emoji codes (spec 5), from GitHub's gemoji 4.1.0 list (MIT), embedded as
    /// <c>Emoji.tsv</c> (name, tab, emoji). Names are lower case; unknown names are not emoji.
    /// </summary>
    public static class Emoji
    {
        internal const string ResourceName = "MicaPad.Emoji.tsv";
        private const int MaxNameLength = 40;
        private static readonly Lazy<Dictionary<string, string>> Table = new(Load);

        /// <summary>How many names the table holds.</summary>
        public static int Count => Table.Value.Count;

        /// <summary>The emoji a name stands for, or null.</summary>
        public static string? GlyphOf(string name) => Table.Value.TryGetValue(name ?? "", out var glyph) ? glyph : null;

        /// <summary>
        /// Every known <c>:name:</c> in the line, outside backtick code spans: where it starts, its
        /// length with both colons, and its emoji. Lines over the inline limit give none.
        /// </summary>
        public static IReadOnlyList<(int Start, int Length, string Glyph)> Find(string line)
        {
            var found = new List<(int, int, string)>();
            string s = line ?? "";
            if (s.Length > MarkdownLineTokenizer.MaxInlineLength || s.IndexOf(':') < 0) return found;

            int i = 0;
            while (i < s.Length)
            {
                if (s[i] == '`')
                {
                    i = SkipCode(s, i);
                    continue;
                }
                if (s[i] != ':')
                {
                    i++;
                    continue;
                }
                int j = i + 1;
                while (j < s.Length && j - i <= MaxNameLength && IsNameChar(s[j])) j++;
                bool closed = j < s.Length && s[j] == ':';
                if (closed && j > i + 1 && GlyphOf(s.Substring(i + 1, j - i - 1)) is { } glyph)
                {
                    found.Add((i, j - i + 1, glyph));
                    i = j + 1;
                }
                else
                {
                    i = closed ? j : i + 1;
                }
            }
            return found;
        }

        private static bool IsNameChar(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '+' or '-';

        /// <summary>Past a backtick code span (a run closed by a run of the same length), or past the run when it never closes.</summary>
        private static int SkipCode(string s, int i)
        {
            int n = 0;
            while (i + n < s.Length && s[i + n] == '`') n++;
            int j = i + n;
            while (j < s.Length)
            {
                if (s[j] != '`') { j++; continue; }
                int m = 0;
                while (j + m < s.Length && s[j + m] == '`') m++;
                if (m == n) return j + m;
                j += m;
            }
            return i + n;
        }

        private static Dictionary<string, string> Load()
        {
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            using var stream = typeof(Emoji).Assembly.GetManifestResourceStream(ResourceName)
                               ?? throw new InvalidOperationException("Missing resource " + ResourceName);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? row;
            while ((row = reader.ReadLine()) != null)
            {
                int tab = row.IndexOf('\t');
                if (tab > 0) table[row.Substring(0, tab)] = row.Substring(tab + 1);
            }
            return table;
        }
    }
}
```

`Pad/EmojiGenerator.cs`:

```csharp
using System;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Shows a known <c>:name:</c> as its emoji (spec 5, R5): one element standing for the whole
    /// code, so the caret steps over it, Backspace or Delete next to it removes it whole, and
    /// copying copies the code. Not in fenced blocks or front matter. The glyph is drawn by the
    /// font fallback (Segoe UI Emoji), in one color.
    /// </summary>
    internal sealed class EmojiGenerator : VisualLineElementGenerator
    {
        private readonly MarkdownDocumentCache _cache;
        private readonly Action<Exception> _onFailure;

        /// <param name="onFailure">Told when a line cannot be looked at; that line gets no emoji.</param>
        public EmojiGenerator(MarkdownDocumentCache cache, Action<Exception>? onFailure = null)
        {
            _cache = cache;
            _onFailure = onFailure ?? (_ => { });
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            try
            {
                var document = CurrentContext.Document;
                int last = CurrentContext.VisualLine.LastDocumentLine.LineNumber;
                for (var line = document.GetLineByOffset(startOffset); line != null && line.LineNumber <= last; line = line.NextLine)
                {
                    var facts = _cache.FactsOf(document, line.LineNumber);
                    if (facts.Fence != MdFence.None || facts.FrontMatter) continue;
                    foreach (var (start, _, _) in Emoji.Find(document.GetText(line)))
                    {
                        int offset = line.Offset + start;
                        if (offset >= startOffset) return offset;
                    }
                }
                return -1;
            }
            catch (Exception ex)
            {
                _onFailure(ex);
                return -1;
            }
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            try
            {
                var document = CurrentContext.Document;
                var line = document.GetLineByOffset(offset);
                foreach (var (start, length, glyph) in Emoji.Find(document.GetText(line)))
                    if (line.Offset + start == offset) return new FormattedTextElement(glyph, length);
                return null;
            }
            catch (Exception ex)
            {
                _onFailure(ex);
                return null;
            }
        }
    }
}
```

In `Pad/EditorLanguage.cs`:

1. Add the field `private EmojiGenerator? _emoji;` after `_bullets`.
2. In `Apply`'s Markdown branch, after `view.ElementGenerators.Add(_bullets);`, add:

```csharp
                _emoji = new EmojiGenerator(_markdownCache, ReportFailure);
                view.ElementGenerators.Add(_emoji);
```

3. In `Clear`, inside `if (_markdown != null)`, after `view.ElementGenerators.Remove(_bullets!);`, add `view.ElementGenerators.Remove(_emoji!);` and after `_bullets = null;` add `_emoji = null;`.
4. Add "emoji" to the class comment's list of what Markdown installs.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~EmojiTests|FullyQualifiedName~MarkdownRenderingTests|FullyQualifiedName~PadLanguageWindowTests|FullyQualifiedName~DiagramPageTests" 2>&1 | tail -5`
Expected: all pass. Then the full suite. Check escapes in `EmojiTests.cs` (the surrogate pairs must stay `😄` etc.).

- [ ] **Step 6: Commit**

```bash
git add Services/Pad/Emoji.tsv Services/Pad/Emoji.cs Pad/EmojiGenerator.cs Pad/EditorLanguage.cs Kil0bitSystemMonitor.csproj Pad/Diagrams/THIRD-PARTY.txt tests/Kil0bitSystemMonitor.Tests/EmojiTests.cs
git commit -m "feat(pad): :emoji: codes show their glyph in Markdown tabs (gemoji 4.1.0 names)" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

## After the last task (controller)

Final whole-branch review, one fix wave, rulings doc `docs/superpowers/plans/2026-10-01-micapad-markdown-rulings.md`; then Plan B (spec parts 6–7) is written and executed on the same branch before the deploy for e2e.
