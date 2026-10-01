# MicaPad diagrams — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Draw Mermaid (mindmap included), Graphviz and Markmap blocks offline as pictures under each block in MicaPad's Markdown tabs, and every other well-known type (PlantUML, D2, …) through an optional Kroki server.

**Architecture:** Pure units under `Services/Pad/` find diagram blocks (`DiagramKinds`, `DiagramBlocks`), key and cache results (`DiagramCacheKey`, `DiagramCache`), queue and route draws (`DiagramRenderer`, behind the `IDiagramPage` interface) and talk to Kroki (`KrokiClient`). `Pad/DiagramPage` is the hidden WebView2 that runs the bundled scripts in `Pad/Diagrams/` (copied to the output `Diagrams\`) and turns every SVG into a PNG. In the editor, `DiagramGenerator` puts a `DiagramElement` (a line break, then a `DiagramPicture`) at the end of each closing fence line; `DiagramBoard` holds each block's state, the 600 ms pause, Hide code and the exports. `EditorLanguage` installs them for Markdown; `MicaPadWindow.Diagrams.cs` and `App.xaml.cs` wire the shared renderer, the clipboard and the Save dialog.

**Tech Stack:** .NET 8 WPF; AvalonEdit 6.3.1.120 (`VisualLineElementGenerator`, `InlineObjectRun`, `FoldingManager`); Microsoft.Web.WebView2 1.0.4258.31 (`CoreWebView2Environment`, `CoreWebView2Controller`); mermaid 12.0.0, @viz-js/viz 3.31.0, d3 7.9.0, markmap-lib/markmap-view 0.18.12; System.Net.Http; xUnit.

**Spec:** `docs/superpowers/specs/2026-10-01-micapad-diagrams-design.md`

## Global Constraints

- Repo `C:\AIProject\kil0bit-system-monitor`, branch `feat/micapad-diagrams`. Commit on it; never push, merge or tag.
- Build and test only with `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe"`. Full suite: `timeout 400 env DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests 2>&1 | tail -5`. Known flaky: `McpPipeTests.At_most_four_calls_run_at_once_and_the_rest_wait_their_turn` — re-run once if it alone fails.
- Never build or publish into `bin\Release` (the controller deploys).
- Tests: temp folders only (`PadTempDir`), never the real `%APPDATA%` or `%LOCALAPPDATA%\MicaStats`; never launch MicaStats; no network (Kroki tests use a fake `HttpMessageHandler`; the WebView2 page has no network by design); UI tests only on the shared `UiThread.Run`, never shown.
- New code under `Services/` holds no WPF or WebView2 types. Files in this repo use LF line endings; keep them. Never edit the downloaded scripts (`mermaid.min.js`, `viz-global.js`, `d3.min.js`, `markmap-view.js`, `markmap-lib.js`).
- A diagram's text is never part of a log line, a warning or an exception message: warnings name the engine and the exception type only (spec "Error handling").
- Exact values (verbatim from the spec): fence words in Task 1's table; source limit 50,000 characters ("Too large to draw"); user data folder `%LOCALAPPDATA%\MicaStats\WebView2`; page `https://micapad-diagrams.invalid/render.html` mapped to the app's `Diagrams\` folder; every other request gets a 403; PNG at 2× with the longest side at most 4,096 px; a draw over 15 s is abandoned ("Took too long to draw."); one draw at a time; 64 results in memory, least recently used out, keyed by SHA-256 of (engine, Kroki server for Kroki types, theme, source); nothing written to disk; Kroki `POST {server}/{type}/svg`, body the block's source as `text/plain; charset=utf-8`, 10 s timeout; server `https://kroki.io` by default, http or https only; errors "The Kroki server could not be reached (kroki.io)." and "PlantUML needs Kroki — turn it on in Settings → MicaPad."; pictures redraw 600 ms after the last change; `AppConfig.PadDiagrams = true`, `PadKroki = false`, `PadKrokiServer = "https://kroki.io"`; Kroki card text "Sends the diagram's text (only that block) to this server. Use your own Kroki server for private notes."; default save names `diagram.png` / `diagram.svg`.
- C# string escapes: write non-ASCII as `\uXXXX` escapes (`"Drawing\u2026"`, `"\u2014"`, `"\u2192"`, icon glyphs `"\uE8C8"`). The Write/Edit tools can decode them into raw characters; after writing a `.cs` file, verify with `LC_ALL=C grep -n '[^[:print:][:space:]]' <file>` (no output expected) and repair with PowerShell `[IO.File]::ReadAllText` / `.Replace` / `[IO.File]::WriteAllText(path, text, [Text.UTF8Encoding]::new($false))`.
- `UseWindowsForms` is on: `Image`, `Button`, `TextBox`, `Brushes`, `Color`, `Cursors`, `HorizontalAlignment`, `FontFamily`, `ContextMenu` are ambiguous in WPF files — add `using X = System.Windows...;` aliases as the existing files do.
- Commits: `git add` exact paths, `git commit -m "..."` (no heredoc), never amend, end with the trailer `Co-Authored-By: <your model name> <noreply@anthropic.com>`.
- Never weaken an existing assertion.
- Do not dispatch subagents.

## Rulings on the spec

- **R1 — exports use the light drawing.** Copy picture, Save as PNG… and Save as SVG… export the block drawn in the light theme (drawn on demand when MicaPad is dark), as Copy as RTF already copies in the light palette: a dark-theme Graphviz or Markmap picture (light lines, transparent background) is unreadable on a white page. Cost if wrong: one extra draw per export in the dark theme.
- **R2 — the renderer is split.** The spec's `DiagramRenderer` (hidden WebView2, queue, cache) becomes `Services/Pad/DiagramRenderer` (queue, cache, routing to the page or Kroki; no WPF, unit-tested with a fake page) and `Pad/DiagramPage` (the WebView2). Cost if wrong: none; one more file.
- **R3 — Kroki pictures ignore the theme in the cache key.** A Kroki picture sits on a light card in both themes, so its key's theme part is `"paper"`: switching theme never asks the server again.
- **R4 — a blank block is not drawn.** A block with nothing but whitespace between its fences gets no picture (no error while a new block is being typed).
- **R5 — the spike's answers (done while planning, 2026-10-01, WebView2 runtime 154.0.4258.37).** Hidden controller (`IsVisible = false`) on a never-shown `WS_POPUP` `HwndSource` works; the PNG is made with a canvas from a `data:` URL image of the SVG — Mermaid's `foreignObject` labels (Thai included) and Markmap draw correctly, so no DevTools screenshot is needed. A hidden page gets no animation frames: `frames.js` replaces `requestAnimationFrame` with `setTimeout`, and Markmap's transitions are flushed (`settle`). The page's Content-Security-Policy and the 403 filter each refuse the network on their own. Environment ~0.4 s, page ~0.4–0.6 s, first Mermaid draw ~0.6 s, later draws 20–200 ms.
- **R6 — a passing failure is not retried for that block until something changes.** "Could not be reached", "Took too long", "Runtime missing" and "engine stopped" are shown and not asked again for the same block text, theme and settings — scrolling must not send a request storm to a dead server. Editing the block, switching theme, or changing Draw diagrams / Kroki / the server asks again.
- **R7 — Kroki answers are limited to 16 MB;** a 200 answer that is not an SVG shows "The picture could not be read." and is not cached.
- **R8 — the 600 ms pause is a flag, not a clock.** An edit sets "editing" and restarts a 600 ms `DispatcherTimer`; its tick clears the flag and redraws the picture lines (`DiagramBoard.DrawDue`, which tests call directly). Spec "fake clock" is satisfied by calling `DrawDue`.
- **R9 — the picture element.** At the end offset of the closing fence line, a zero-length element of two columns: a `TextEndOfLine` run, then an `InlineObjectRun` holding the picture. The picture is laid out on its own row under the fence text; the caret stops only before it. Proven by a spike (`TextLines.Count == 2`, picture top = first row's bottom).

## Review Focus

1. Typing inside a diagram block: no draw per keystroke, the previous picture stays, exactly one draw after the pause (Task 6 test `Typing_in_a_block_draws_once_after_the_pause_and_keeps_the_old_picture`).
2. A Kroki server that cannot be reached, with the block scrolled out and back in: one request, not one per redraw; a settings change asks again (Task 6 test `A_passing_failure_is_not_asked_again_until_the_settings_change`).
3. Copy picture or Save as … while MicaPad is dark: the exported picture is the light drawing (Task 6 test `Copy_picture_in_the_dark_theme_copies_the_light_drawing`, Task 7 test `Copy_picture_puts_the_light_png_on_the_clipboard`).
4. A page that hangs or a WebView2 process that dies: "Took too long to draw." / engine stopped, and the next draw gets a fresh page (Task 2 tests `A_draw_over_the_limit_is_abandoned_and_the_next_one_gets_a_new_page`, `A_broken_page_is_replaced_and_its_error_is_not_cached`).
5. The tab switched or closed while a draw is still running: the late result never redraws another document and never throws (Task 6 test `A_draw_finishing_after_the_board_detached_changes_nothing`).

---

## File Structure

| File | Task | Responsibility |
|---|---|---|
| Create `Services/Pad/DiagramKinds.cs` | 1 | Fence word → kind (engine, Kroki type, name) |
| Modify `Services/Pad/FenceTracker.cs` | 1 | `Openings`: which line each closing fence closes |
| Create `Services/Pad/DiagramBlocks.cs` | 1 | Diagram blocks of a document; one block from its lines |
| Create `Services/Pad/DiagramResult.cs` | 2 | `DiagramResult`, `DiagramRequest`, `DiagramText`, `DiagramCacheKey` |
| Create `Services/Pad/DiagramCache.cs` | 2 | LRU of 64 results |
| Create `Services/Pad/DiagramPageContract.cs` | 2 | `IDiagramPage`, `PageRequest`, `PageDrawing`, `DiagramRuntimeMissingException`, `IDiagramRenderer` |
| Create `Services/Pad/DiagramRenderer.cs` | 2, 4 | Queue, cache, routing (page; Kroki in Task 4) |
| Create `Pad/Diagrams/*` | 3 | `render.html`, `frames.js`, `render.js`, five downloaded scripts, `THIRD-PARTY.txt` |
| Modify `Kil0bitSystemMonitor.csproj` | 3 | WebView2 package; `Pad\Diagrams\*` → output `Diagrams\` |
| Create `Pad/DiagramPage.cs` | 3 | The hidden WebView2 page |
| Create `Services/Pad/KrokiClient.cs` | 4 | Kroki request, server check, error mapping |
| Modify `Models/SystemMetrics.cs` (AppConfig) | 4 | `PadDiagrams`, `PadKroki`, `PadKrokiServer` |
| Create `Pad/DiagramElement.cs`, `Pad/DiagramPicture.cs` | 5 | The element under the fence; the picture / drawing / error control |
| Modify `Pad/MarkdownDocumentCache.cs` | 6 | `OpeningLineOf`, `ClosingLineOf` |
| Modify `Pad/FoldingController.cs` | 6 | `ExtraFolds` |
| Create `Pad/DiagramBoard.cs`, `Pad/DiagramGenerator.cs` | 6 | Block state, pause, Hide code, exports; the generator |
| Modify `Pad/EditorLanguage.cs` | 6 | Installs the pictures with Markdown |
| Create `Pad/MicaPadWindow.Diagrams.cs` | 7 | Window wiring, clipboard, Save dialog |
| Modify `Pad/MicaPadWindow.xaml.cs`, `App.xaml.cs` | 7 | Constructor call, config changes; the shared renderer and its disposal |
| Modify `SettingsWindow.xaml(.cs)`, `GUIDE.md` | 8 | Two cards; the Diagrams guide |
| Tests (create) | | `DiagramKindsTests`, `DiagramBlocksTests`, `DiagramRendererTests`, `DiagramPageTests`, `KrokiClientTests`, `DiagramPictureTests`, `DiagramBoardTests`, `DiagramWindowTests`, `DiagramFakes.cs` |
| Tests (modify) | 4 | `PadConfigTests.cs` |

---

### Task 1: Which blocks are diagrams

**Files:**
- Create: `Services/Pad/DiagramKinds.cs`
- Modify: `Services/Pad/FenceTracker.cs` (add `Openings`)
- Create: `Services/Pad/DiagramBlocks.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/DiagramKindsTests.cs`, `tests/Kil0bitSystemMonitor.Tests/DiagramBlocksTests.cs`

**Interfaces:**
- Consumes: `FenceTracker.Classify(IReadOnlyList<string>) : MdFence[]`, `enum MdFence { None, Delimiter, Inside }` (existing, `Services/Pad`).
- Produces: `enum DiagramEngine { Mermaid, Graphviz, Markmap, Kroki }`; `sealed class DiagramKind` with `string Name`, `DiagramEngine Engine`, `string? KrokiType`, `bool NeedsKroki`, `string EngineId`, `string PageKind`; `static class DiagramKinds` with `IEnumerable<string> Words`, `DiagramKind? FromWord(string?)`, `DiagramKind? FromFence(string openingLine)`; `FenceTracker.Openings(IReadOnlyList<MdFence>) : int[]`; `sealed record DiagramBlock(DiagramKind Kind, int OpenLine, int CloseLine, string Source, bool TooLarge)`; `static class DiagramBlocks` with `const int MaxSourceLength = 50_000`, `Find(IReadOnlyList<string> lines, IReadOnlyList<MdFence> kinds) : IReadOnlyList<DiagramBlock>`, `Read(Func<int, string> lineText, int openLine, int closeLine) : DiagramBlock?` (1-based lines).

- [ ] **Step 1: Create the branch**

```bash
cd /c/AIProject/kil0bit-system-monitor && git checkout -b feat/micapad-diagrams
```

- [ ] **Step 2: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/DiagramKindsTests.cs`:

```csharp
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The fence words that make a block a diagram (spec section 1).</summary>
    public class DiagramKindsTests
    {
        [Theory]
        [InlineData("mermaid", DiagramEngine.Mermaid, null, "Mermaid")]
        [InlineData("mmd", DiagramEngine.Mermaid, null, "Mermaid")]
        [InlineData("dot", DiagramEngine.Graphviz, null, "Graphviz")]
        [InlineData("graphviz", DiagramEngine.Graphviz, null, "Graphviz")]
        [InlineData("gv", DiagramEngine.Graphviz, null, "Graphviz")]
        [InlineData("markmap", DiagramEngine.Markmap, null, "Markmap")]
        [InlineData("plantuml", DiagramEngine.Kroki, "plantuml", "PlantUML")]
        [InlineData("puml", DiagramEngine.Kroki, "plantuml", "PlantUML")]
        [InlineData("c4plantuml", DiagramEngine.Kroki, "c4plantuml", "C4 with PlantUML")]
        [InlineData("d2", DiagramEngine.Kroki, "d2", "D2")]
        [InlineData("bpmn", DiagramEngine.Kroki, "bpmn", "BPMN")]
        [InlineData("excalidraw", DiagramEngine.Kroki, "excalidraw", "Excalidraw")]
        [InlineData("vega", DiagramEngine.Kroki, "vega", "Vega")]
        [InlineData("vegalite", DiagramEngine.Kroki, "vegalite", "Vega-Lite")]
        [InlineData("vega-lite", DiagramEngine.Kroki, "vegalite", "Vega-Lite")]
        [InlineData("wavedrom", DiagramEngine.Kroki, "wavedrom", "WaveDrom")]
        [InlineData("ditaa", DiagramEngine.Kroki, "ditaa", "Ditaa")]
        [InlineData("structurizr", DiagramEngine.Kroki, "structurizr", "Structurizr")]
        [InlineData("nomnoml", DiagramEngine.Kroki, "nomnoml", "Nomnoml")]
        [InlineData("pikchr", DiagramEngine.Kroki, "pikchr", "Pikchr")]
        [InlineData("svgbob", DiagramEngine.Kroki, "svgbob", "Svgbob")]
        [InlineData("dbml", DiagramEngine.Kroki, "dbml", "DBML")]
        [InlineData("erd", DiagramEngine.Kroki, "erd", "ERD")]
        [InlineData("bytefield", DiagramEngine.Kroki, "bytefield", "Bytefield")]
        [InlineData("blockdiag", DiagramEngine.Kroki, "blockdiag", "BlockDiag")]
        [InlineData("seqdiag", DiagramEngine.Kroki, "seqdiag", "SeqDiag")]
        [InlineData("actdiag", DiagramEngine.Kroki, "actdiag", "ActDiag")]
        [InlineData("nwdiag", DiagramEngine.Kroki, "nwdiag", "NwDiag")]
        [InlineData("packetdiag", DiagramEngine.Kroki, "packetdiag", "PacketDiag")]
        [InlineData("rackdiag", DiagramEngine.Kroki, "rackdiag", "RackDiag")]
        [InlineData("tikz", DiagramEngine.Kroki, "tikz", "TikZ")]
        [InlineData("umlet", DiagramEngine.Kroki, "umlet", "UMLet")]
        [InlineData("symbolator", DiagramEngine.Kroki, "symbolator", "Symbolator")]
        [InlineData("wireviz", DiagramEngine.Kroki, "wireviz", "WireViz")]
        public void Every_word_maps_to_its_engine_type_and_name(string word, DiagramEngine engine, string? krokiType, string name)
        {
            var kind = DiagramKinds.FromWord(word);

            Assert.NotNull(kind);
            Assert.Equal(engine, kind!.Engine);
            Assert.Equal(krokiType, kind.KrokiType);
            Assert.Equal(name, kind.Name);
            Assert.Equal(engine == DiagramEngine.Kroki, kind.NeedsKroki);
            Assert.Same(kind, DiagramKinds.FromWord(word.ToUpperInvariant()));
        }

        [Fact]
        public void There_are_34_words_all_lower_case()
        {
            var words = DiagramKinds.Words.ToList();

            Assert.Equal(34, words.Count);
            Assert.All(words, w => Assert.Equal(w.ToLowerInvariant(), w));
        }

        [Theory]
        [InlineData("js")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("mermaid2")]
        public void Any_other_word_is_code(string? word) => Assert.Null(DiagramKinds.FromWord(word));

        [Fact]
        public void The_engine_id_and_page_kind_follow_the_engine()
        {
            Assert.Equal("mermaid", DiagramKinds.FromWord("mermaid")!.EngineId);
            Assert.Equal("graphviz", DiagramKinds.FromWord("gv")!.EngineId);
            Assert.Equal("markmap", DiagramKinds.FromWord("markmap")!.EngineId);
            Assert.Equal("kroki/plantuml", DiagramKinds.FromWord("puml")!.EngineId);

            Assert.Equal("mermaid", DiagramKinds.FromWord("mmd")!.PageKind);
            Assert.Equal("dot", DiagramKinds.FromWord("graphviz")!.PageKind);
            Assert.Equal("markmap", DiagramKinds.FromWord("markmap")!.PageKind);
            Assert.Equal("svg", DiagramKinds.FromWord("d2")!.PageKind);
        }

        [Theory]
        [InlineData("```mermaid", "Mermaid")]
        [InlineData("~~~ dot", "Graphviz")]
        [InlineData("   ```PlantUML title here", "PlantUML")]
        [InlineData("````d2", "D2")]
        [InlineData("```vega-lite\t{}", "Vega-Lite")]
        public void An_opening_fence_names_its_kind_by_the_first_word(string line, string name) =>
            Assert.Equal(name, DiagramKinds.FromFence(line)!.Name);

        [Theory]
        [InlineData("```")]
        [InlineData("```js")]
        [InlineData("    ```mermaid")]   // four spaces: indented code, not a fence
        [InlineData("mermaid")]
        [InlineData("")]
        public void Other_lines_name_no_kind(string line) => Assert.Null(DiagramKinds.FromFence(line));
    }
}
```

`tests/Kil0bitSystemMonitor.Tests/DiagramBlocksTests.cs`:

```csharp
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Finding diagram blocks and their sources (spec section 1).</summary>
    public class DiagramBlocksTests
    {
        private static System.Collections.Generic.IReadOnlyList<DiagramBlock> Find(string text)
        {
            var lines = text.Split('\n');
            return DiagramBlocks.Find(lines, FenceTracker.Classify(lines));
        }

        [Fact]
        public void Openings_pair_each_closing_fence_with_its_opening_line()
        {
            var lines = "a\n```\nx\n```\n~~~\n~~~\n```js".Split('\n');

            var openings = FenceTracker.Openings(FenceTracker.Classify(lines));

            Assert.Equal(new[] { 0, 0, 0, 2, 0, 5, 0 }, openings);
        }

        [Fact]
        public void A_diagram_block_has_its_kind_lines_and_source()
        {
            var blocks = Find("# Notes\n```mermaid\nflowchart TD\n  A --> B\n```\ntext\n~~~dot\ndigraph { a -> b }\n~~~");

            Assert.Equal(2, blocks.Count);
            Assert.Equal("Mermaid", blocks[0].Kind.Name);
            Assert.Equal(2, blocks[0].OpenLine);
            Assert.Equal(5, blocks[0].CloseLine);
            Assert.Equal("flowchart TD\n  A --> B", blocks[0].Source);
            Assert.False(blocks[0].TooLarge);
            Assert.Equal("Graphviz", blocks[1].Kind.Name);
            Assert.Equal("digraph { a -> b }", blocks[1].Source);
        }

        [Fact]
        public void Code_blocks_and_unclosed_blocks_are_not_diagrams()
        {
            Assert.Empty(Find("```js\nlet a;\n```"));
            Assert.Empty(Find("```mermaid\nflowchart TD\n  A --> B"));
        }

        [Fact]
        public void A_blank_block_is_not_drawn() => Assert.Empty(Find("```mermaid\n   \n\n```"));

        [Fact]
        public void A_longer_fence_keeps_shorter_ones_inside_its_source()
        {
            var block = Assert.Single(Find("````markmap\n# Root\n```\ncode\n```\n````"));

            Assert.Equal("# Root\n```\ncode\n```", block.Source);
            Assert.Equal(6, block.CloseLine);
        }

        [Fact]
        public void A_source_over_50000_characters_is_too_large_and_not_read()
        {
            string big = new string('x', DiagramBlocks.MaxSourceLength + 1);

            var block = Assert.Single(Find("```dot\n" + big + "\n```"));

            Assert.True(block.TooLarge);
            Assert.Equal("", block.Source);
            Assert.False(Assert.Single(Find("```dot\n" + new string('x', DiagramBlocks.MaxSourceLength) + "\n```")).TooLarge);
        }

        [Fact]
        public void Read_takes_one_block_from_line_numbers()
        {
            var lines = new[] { "```puml", "@startuml", "a -> b", "@enduml", "```" };

            var block = DiagramBlocks.Read(n => lines[n - 1], 1, 5);

            Assert.NotNull(block);
            Assert.Equal("PlantUML", block!.Kind.Name);
            Assert.Equal("@startuml\na -> b\n@enduml", block.Source);
            Assert.Null(DiagramBlocks.Read(n => lines[n - 1], 2, 5));   // line 2 is no fence
            Assert.Null(DiagramBlocks.Read(n => lines[n - 1], 5, 5));
        }

        [Fact]
        public void An_upper_case_word_with_a_title_still_counts()
        {
            var block = Assert.Single(Find("```MERMAID my flow\nflowchart LR\n  a --> b\n```"));

            Assert.Equal(DiagramEngine.Mermaid, block.Kind.Engine);
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~DiagramKindsTests|FullyQualifiedName~DiagramBlocksTests" 2>&1 | tail -5`
Expected: build error, `DiagramKinds` does not exist.

- [ ] **Step 4: Implement**

`Services/Pad/DiagramKinds.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>Who draws a diagram: one of the three engines built into MicaPad, or a Kroki server.</summary>
    public enum DiagramEngine
    {
        Mermaid,
        Graphviz,
        Markmap,
        Kroki,
    }

    /// <summary>One kind of diagram block: its name for messages, its engine and, for Kroki, the type sent.</summary>
    public sealed class DiagramKind
    {
        internal DiagramKind(string name, DiagramEngine engine, string? krokiType)
        {
            Name = name;
            Engine = engine;
            KrokiType = krokiType;
        }

        /// <summary>The name messages use: "Mermaid", "PlantUML".</summary>
        public string Name { get; }

        public DiagramEngine Engine { get; }

        /// <summary>The diagram type in Kroki's URL; null for the built-in engines.</summary>
        public string? KrokiType { get; }

        /// <summary>True when only a Kroki server can draw it.</summary>
        public bool NeedsKroki => Engine == DiagramEngine.Kroki;

        /// <summary>The engine part of a cache key: "mermaid", "graphviz", "markmap" or "kroki/&lt;type&gt;".</summary>
        public string EngineId => Engine switch
        {
            DiagramEngine.Mermaid => "mermaid",
            DiagramEngine.Graphviz => "graphviz",
            DiagramEngine.Markmap => "markmap",
            _ => "kroki/" + KrokiType,
        };

        /// <summary>What the drawing page is asked for: "mermaid", "dot", "markmap", or "svg" for Kroki's picture.</summary>
        public string PageKind => Engine switch
        {
            DiagramEngine.Mermaid => "mermaid",
            DiagramEngine.Graphviz => "dot",
            DiagramEngine.Markmap => "markmap",
            _ => "svg",
        };
    }

    /// <summary>
    /// The fence words that make a fenced block a diagram (spec section 1), compared without case.
    /// Any other word is ordinary code.
    /// </summary>
    public static class DiagramKinds
    {
        private static readonly Dictionary<string, DiagramKind> ByWord = Build();

        /// <summary>Every fence word, lower case.</summary>
        public static IEnumerable<string> Words => ByWord.Keys;

        /// <summary>The kind a fence word names, or null for ordinary code.</summary>
        public static DiagramKind? FromWord(string? word) =>
            word != null && ByWord.TryGetValue(word, out var kind) ? kind : null;

        /// <summary>
        /// The kind an opening fence line names by the first word of its info string
        /// (<c>```mermaid title</c> is Mermaid), or null. At most three spaces before the fence.
        /// </summary>
        public static DiagramKind? FromFence(string openingLine)
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
            return FromWord(line.Substring(start, i - start));
        }

        private static Dictionary<string, DiagramKind> Build()
        {
            var map = new Dictionary<string, DiagramKind>(StringComparer.OrdinalIgnoreCase);
            void Add(DiagramKind kind, params string[] words)
            {
                foreach (string word in words) map.Add(word, kind);
            }

            Add(new DiagramKind("Mermaid", DiagramEngine.Mermaid, null), "mermaid", "mmd");
            Add(new DiagramKind("Graphviz", DiagramEngine.Graphviz, null), "dot", "graphviz", "gv");
            Add(new DiagramKind("Markmap", DiagramEngine.Markmap, null), "markmap");
            Add(Kroki("PlantUML", "plantuml"), "plantuml", "puml");
            Add(Kroki("C4 with PlantUML", "c4plantuml"), "c4plantuml");
            Add(Kroki("D2", "d2"), "d2");
            Add(Kroki("BPMN", "bpmn"), "bpmn");
            Add(Kroki("Excalidraw", "excalidraw"), "excalidraw");
            Add(Kroki("Vega", "vega"), "vega");
            Add(Kroki("Vega-Lite", "vegalite"), "vegalite", "vega-lite");
            Add(Kroki("WaveDrom", "wavedrom"), "wavedrom");
            Add(Kroki("Ditaa", "ditaa"), "ditaa");
            Add(Kroki("Structurizr", "structurizr"), "structurizr");
            Add(Kroki("Nomnoml", "nomnoml"), "nomnoml");
            Add(Kroki("Pikchr", "pikchr"), "pikchr");
            Add(Kroki("Svgbob", "svgbob"), "svgbob");
            Add(Kroki("DBML", "dbml"), "dbml");
            Add(Kroki("ERD", "erd"), "erd");
            Add(Kroki("Bytefield", "bytefield"), "bytefield");
            Add(Kroki("BlockDiag", "blockdiag"), "blockdiag");
            Add(Kroki("SeqDiag", "seqdiag"), "seqdiag");
            Add(Kroki("ActDiag", "actdiag"), "actdiag");
            Add(Kroki("NwDiag", "nwdiag"), "nwdiag");
            Add(Kroki("PacketDiag", "packetdiag"), "packetdiag");
            Add(Kroki("RackDiag", "rackdiag"), "rackdiag");
            Add(Kroki("TikZ", "tikz"), "tikz");
            Add(Kroki("UMLet", "umlet"), "umlet");
            Add(Kroki("Symbolator", "symbolator"), "symbolator");
            Add(Kroki("WireViz", "wireviz"), "wireviz");
            return map;
        }

        private static DiagramKind Kroki(string name, string type) => new(name, DiagramEngine.Kroki, type);
    }
}
```

Add to `Services/Pad/FenceTracker.cs`, after `Classify`:

```csharp
        /// <summary>
        /// For each line, the 1-based number of the line that opened the fence it closes, or 0 when
        /// it closes none. Delimiters alternate (open, close, open…), as <see cref="Classify"/> made
        /// them; a last opening fence that never closes gets no entry.
        /// </summary>
        public static int[] Openings(IReadOnlyList<MdFence> kinds)
        {
            var openings = new int[kinds.Count];
            int open = 0;
            for (int i = 0; i < kinds.Count; i++)
            {
                if (kinds[i] != MdFence.Delimiter) continue;
                if (open == 0)
                {
                    open = i + 1;
                }
                else
                {
                    openings[i] = open;
                    open = 0;
                }
            }
            return openings;
        }
```

`Services/Pad/DiagramBlocks.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// A diagram block: its kind, its opening and closing fence lines (1-based) and its source,
    /// the text between them joined with "\n". A source over <see cref="DiagramBlocks.MaxSourceLength"/>
    /// characters is not read: <see cref="TooLarge"/> is set and the source is empty.
    /// </summary>
    public sealed record DiagramBlock(DiagramKind Kind, int OpenLine, int CloseLine, string Source, bool TooLarge);

    /// <summary>
    /// Finds the diagram blocks of a document (spec section 1): a closed fenced block whose info
    /// string's first word is a diagram word, with something other than whitespace inside (R4).
    /// </summary>
    public static class DiagramBlocks
    {
        /// <summary>The longest source that is drawn.</summary>
        public const int MaxSourceLength = 50_000;

        /// <summary>Every diagram block of the lines, in order.</summary>
        public static IReadOnlyList<DiagramBlock> Find(IReadOnlyList<string> lines, IReadOnlyList<MdFence> kinds)
        {
            var openings = FenceTracker.Openings(kinds);
            var blocks = new List<DiagramBlock>();
            for (int i = 0; i < openings.Length; i++)
            {
                if (openings[i] == 0) continue;
                if (Read(n => lines[n - 1] ?? "", openings[i], i + 1) is { } block) blocks.Add(block);
            }
            return blocks;
        }

        /// <summary>
        /// The block between the fence on <paramref name="openLine"/> and the one on
        /// <paramref name="closeLine"/> (1-based, already known to pair), or null when the opening
        /// fence names no diagram or the source is blank.
        /// </summary>
        public static DiagramBlock? Read(Func<int, string> lineText, int openLine, int closeLine)
        {
            if (openLine < 1 || closeLine <= openLine) return null;
            var kind = DiagramKinds.FromFence(lineText(openLine));
            if (kind == null) return null;

            var parts = new List<string>();
            int length = 0;
            bool blank = true;
            for (int n = openLine + 1; n < closeLine; n++)
            {
                string text = lineText(n) ?? "";
                length += text.Length + (parts.Count > 0 ? 1 : 0);
                if (length > MaxSourceLength) return new DiagramBlock(kind, openLine, closeLine, "", TooLarge: true);
                if (blank && !string.IsNullOrWhiteSpace(text)) blank = false;
                parts.Add(text);
            }
            return blank ? null : new DiagramBlock(kind, openLine, closeLine, string.Join("\n", parts), TooLarge: false);
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~DiagramKindsTests|FullyQualifiedName~DiagramBlocksTests|FullyQualifiedName~MarkdownTokenizerTests|FullyQualifiedName~MarkdownRenderingTests" 2>&1 | tail -5`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add Services/Pad/DiagramKinds.cs Services/Pad/DiagramBlocks.cs Services/Pad/FenceTracker.cs tests/Kil0bitSystemMonitor.Tests/DiagramKindsTests.cs tests/Kil0bitSystemMonitor.Tests/DiagramBlocksTests.cs
git commit -m "feat(pad): diagram blocks - fence words for Mermaid, Graphviz, Markmap and the Kroki types; blocks with their sources" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 2: Results, cache and the renderer's queue

**Files:**
- Create: `Services/Pad/DiagramResult.cs` (`DiagramResult`, `DiagramRequest`, `DiagramText`, `DiagramCacheKey`)
- Create: `Services/Pad/DiagramCache.cs`
- Create: `Services/Pad/DiagramPageContract.cs` (`IDiagramPage`, `PageRequest`, `PageDrawing`, `DiagramRuntimeMissingException`, `IDiagramRenderer`)
- Create: `Services/Pad/DiagramRenderer.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/DiagramFakes.cs`, `tests/Kil0bitSystemMonitor.Tests/DiagramRendererTests.cs`

**Interfaces:**
- Consumes: `DiagramKind`, `DiagramKinds` (Task 1); `PadThemes`, `PadColor` (existing).
- Produces:
  - `sealed class DiagramResult` — `byte[]? Png`, `string? Svg`, `double Width`, `double Height`, `string? Error`, `Uri? HelpLink`, `bool Paper`, `bool Lasting`, `bool IsReplaced`, `bool IsPicture`; `static Picture(byte[] png, string svg, double width, double height, bool paper)`, `static Failure(string error, bool lasting, Uri? helpLink = null)`, `static DiagramResult Replaced`.
  - `sealed record DiagramRequest(DiagramKind Kind, string Source, string Theme, string Foreground, string Background, string? KrokiServer)` — `bool Dark`, `string Key`, `static string Css(PadColor)`.
  - `static class DiagramText` — `Drawing`, `TooLarge`, `TookTooLong`, `RuntimeMissing`, `RuntimeDownload` (Uri), `EngineStopped`, `Failed`, `CouldNotRead`, `NeedsKroki(DiagramKind)`, `Unreachable(string host)`.
  - `static class DiagramCacheKey` — `Of(string engine, string? krokiServer, string theme, string source) : string` (64 hex chars).
  - `sealed class DiagramCache` — `DiagramCache(int capacity = 64)`, `int Count`, `TryGet(string, out DiagramResult)`, `Add(string, DiagramResult)`.
  - `sealed record PageRequest(string Kind, string Source, bool Dark, string Foreground, string Background)`; `sealed record PageDrawing(string? Svg, byte[]? Png, double Width, double Height, string? Error)`; `interface IDiagramPage : IDisposable { bool IsBroken { get; } Task<PageDrawing> DrawAsync(PageRequest request, CancellationToken cancel); }`; `sealed class DiagramRuntimeMissingException : Exception`.
  - `interface IDiagramRenderer { bool TryGetCached(string key, out DiagramResult result); Task<DiagramResult> RenderAsync(DiagramRequest request, object slot); }`.
  - `sealed class DiagramRenderer : IDiagramRenderer, IDisposable` — `DiagramRenderer(Func<Task<IDiagramPage>> createPage, Action<string>? warn = null, TimeSpan? drawLimit = null)` (Task 4 adds a fourth parameter `KrokiClient? kroki = null`), `static TimeSpan DefaultDrawLimit` (15 s), `internal int CachedCount`.
  - Test helpers (`DiagramFakes.cs`): `FakePage`, `DiagramFakes.Png`, `DiagramFakes.Drawn(...)`, `DiagramFakes.Picture(...)`, `DiagramFakes.Request(...)`, `DiagramFakes.WaitUntil(...)`.

- [ ] **Step 1: Write the test helpers and the failing tests**

`tests/Kil0bitSystemMonitor.Tests/DiagramFakes.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>A drawing page that answers as a test says, or waits until the test calls <see cref="Finish"/>.</summary>
    internal sealed class FakePage : IDiagramPage
    {
        private readonly object _gate = new();
        private readonly List<PageRequest> _requests = new();
        private readonly List<TaskCompletionSource<PageDrawing>> _waiting = new();

        /// <summary>The answer to a request; null (or no function) leaves it waiting.</summary>
        public Func<PageRequest, PageDrawing?>? Answer { get; set; }

        public bool IsBroken { get; set; }

        public bool Disposed { get; private set; }

        /// <summary>Every request so far, oldest first.</summary>
        public IReadOnlyList<PageRequest> Requests
        {
            get { lock (_gate) return _requests.ToList(); }
        }

        public Task<PageDrawing> DrawAsync(PageRequest request, CancellationToken cancel)
        {
            var done = new TaskCompletionSource<PageDrawing>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancel.Register(() => done.TrySetCanceled(cancel));
            lock (_gate)
            {
                _requests.Add(request);
                _waiting.Add(done);
            }
            if (Answer?.Invoke(request) is { } answer) done.TrySetResult(answer);
            return done.Task;
        }

        /// <summary>Answers the oldest request still waiting.</summary>
        public void Finish(PageDrawing drawing)
        {
            TaskCompletionSource<PageDrawing> done;
            lock (_gate) done = _waiting.First(w => !w.Task.IsCompleted);
            done.TrySetResult(drawing);
        }

        public void Dispose() => Disposed = true;
    }

    internal static class DiagramFakes
    {
        /// <summary>A valid 1x1 PNG.</summary>
        public static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

        public const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"50\"/>";

        public static PageDrawing Drawn(double width = 100, double height = 50) => new(Svg, Png, width, height, null);

        public static DiagramResult Picture(double width = 100, double height = 50, bool paper = false) =>
            DiagramResult.Picture(Png, Svg, width, height, paper);

        public static DiagramRequest Request(string word = "mermaid", string source = "flowchart LR\n  a --> b",
                                             string theme = PadThemes.Dark, string? server = null) =>
            new(DiagramKinds.FromWord(word)!, source, theme, "#EDEDF2", "#0E0E13", server);

        /// <summary>Waits up to 10 s for a condition another thread makes true.</summary>
        public static void WaitUntil(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for " + what);
                Thread.Sleep(5);
            }
        }
    }
}
```

`tests/Kil0bitSystemMonitor.Tests/DiagramRendererTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The renderer's queue, cache and failures (spec 2.3, 2.4, "Error handling"), with a fake page.</summary>
    public class DiagramRendererTests
    {
        private static DiagramResult Wait(Task<DiagramResult> task)
        {
            Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "the draw did not finish");
            return task.Result;
        }

        private static DiagramRenderer Over(FakePage page, Action<string>? warn = null) =>
            new(() => Task.FromResult<IDiagramPage>(page), warn);

        [Fact]
        public void A_built_in_kind_is_drawn_on_the_page_and_cached()
        {
            var page = new FakePage { Answer = _ => DiagramFakes.Drawn(120, 60) };
            int created = 0;
            using var renderer = new DiagramRenderer(() =>
            {
                created++;
                return Task.FromResult<IDiagramPage>(page);
            });
            var request = DiagramFakes.Request("graphviz", "digraph { a -> b }", PadThemes.Light);

            var result = Wait(renderer.RenderAsync(request, new object()));

            Assert.True(result.IsPicture);
            Assert.Equal(120, result.Width);
            Assert.Equal(60, result.Height);
            Assert.False(result.Paper);
            Assert.Equal(new PageRequest("dot", "digraph { a -> b }", false, "#EDEDF2", "#0E0E13"), Assert.Single(page.Requests));
            Assert.True(renderer.TryGetCached(request.Key, out var cached));
            Assert.Same(result, cached);

            var again = renderer.RenderAsync(request, new object());
            Assert.True(again.IsCompleted);
            Assert.Same(result, again.Result);
            Assert.Single(page.Requests);
            Assert.Equal(1, created);
        }

        [Fact]
        public void One_draw_runs_at_a_time()
        {
            var page = new FakePage();
            using var renderer = Over(page);
            var first = renderer.RenderAsync(DiagramFakes.Request(source: "flowchart LR\n  a"), new object());
            var second = renderer.RenderAsync(DiagramFakes.Request(source: "flowchart LR\n  b"), new object());

            DiagramFakes.WaitUntil(() => page.Requests.Count == 1, "the first draw");
            Thread.Sleep(50);
            Assert.Single(page.Requests);   // the second waits its turn

            page.Finish(DiagramFakes.Drawn());
            Assert.True(Wait(first).IsPicture);
            DiagramFakes.WaitUntil(() => page.Requests.Count == 2, "the second draw");
            page.Finish(DiagramFakes.Drawn());
            Assert.True(Wait(second).IsPicture);
        }

        [Fact]
        public void A_newer_request_for_the_same_block_replaces_the_waiting_one()
        {
            var page = new FakePage();
            using var renderer = Over(page);
            var block = new object();
            var running = renderer.RenderAsync(DiagramFakes.Request(source: "a"), new object());
            DiagramFakes.WaitUntil(() => page.Requests.Count == 1, "the first draw");

            var older = renderer.RenderAsync(DiagramFakes.Request(source: "b"), block);
            var newer = renderer.RenderAsync(DiagramFakes.Request(source: "c"), block);

            Assert.True(Wait(older).IsReplaced);
            page.Finish(DiagramFakes.Drawn());
            Wait(running);
            DiagramFakes.WaitUntil(() => page.Requests.Count == 2, "the newer draw");
            Assert.Equal("c", page.Requests[1].Source);
            page.Finish(DiagramFakes.Drawn());
            Assert.True(Wait(newer).IsPicture);
        }

        [Fact]
        public void A_syntax_error_is_a_lasting_result_and_is_cached()
        {
            var page = new FakePage { Answer = _ => new PageDrawing(null, null, 0, 0, "Parse error on line 1") };
            using var renderer = Over(page);
            var bad = DiagramFakes.Request(source: "bad");

            var result = Wait(renderer.RenderAsync(bad, new object()));

            Assert.False(result.IsPicture);
            Assert.Equal("Parse error on line 1", result.Error);
            Assert.True(result.Lasting);
            Assert.True(renderer.TryGetCached(bad.Key, out _));
        }

        [Fact]
        public void A_draw_over_the_limit_is_abandoned_and_the_next_one_gets_a_new_page()
        {
            var pages = new List<FakePage>();
            using var renderer = new DiagramRenderer(() =>
            {
                var page = new FakePage();
                lock (pages) pages.Add(page);
                return Task.FromResult<IDiagramPage>(page);
            }, drawLimit: TimeSpan.FromMilliseconds(100));
            var request = DiagramFakes.Request();

            var result = Wait(renderer.RenderAsync(request, new object()));

            Assert.Equal("Took too long to draw.", result.Error);
            Assert.False(result.Lasting);
            Assert.False(renderer.TryGetCached(request.Key, out _));
            lock (pages) Assert.True(pages[0].Disposed);

            var next = renderer.RenderAsync(request, new object());
            DiagramFakes.WaitUntil(() =>
            {
                lock (pages) return pages.Count == 2 && pages[1].Requests.Count == 1;
            }, "a new page");
            lock (pages) pages[1].Finish(DiagramFakes.Drawn());
            Assert.True(Wait(next).IsPicture);
        }

        [Fact]
        public void A_broken_page_is_replaced_and_its_error_is_not_cached()
        {
            int made = 0;
            var first = new FakePage();
            first.Answer = _ =>
            {
                first.IsBroken = true;
                return new PageDrawing(null, null, 0, 0, DiagramText.EngineStopped);
            };
            var second = new FakePage { Answer = _ => DiagramFakes.Drawn() };
            using var renderer = new DiagramRenderer(() => Task.FromResult<IDiagramPage>(++made == 1 ? first : second));
            var request = DiagramFakes.Request();

            var failed = Wait(renderer.RenderAsync(request, new object()));

            Assert.Equal(DiagramText.EngineStopped, failed.Error);
            Assert.False(failed.Lasting);
            Assert.False(renderer.TryGetCached(request.Key, out _));
            Assert.True(first.Disposed);

            Assert.True(Wait(renderer.RenderAsync(request, new object())).IsPicture);
            Assert.Equal(2, made);
        }

        [Fact]
        public void A_missing_runtime_says_so_with_a_link_and_is_tried_again_next_time()
        {
            int tries = 0;
            using var renderer = new DiagramRenderer(() =>
            {
                tries++;
                throw new DiagramRuntimeMissingException();
            });
            var request = DiagramFakes.Request();

            var result = Wait(renderer.RenderAsync(request, new object()));

            Assert.Equal("Diagrams need the Microsoft Edge WebView2 Runtime.", result.Error);
            Assert.Equal(DiagramText.RuntimeDownload, result.HelpLink);
            Assert.False(result.Lasting);
            Wait(renderer.RenderAsync(request, new object()));
            Assert.Equal(2, tries);
        }

        [Fact]
        public void A_failure_is_a_result_and_its_warning_names_only_the_engine_and_the_exception()
        {
            var warnings = new List<string>();
            var page = new FakePage { Answer = _ => throw new InvalidOperationException("secret diagram text") };
            using var renderer = Over(page, warnings.Add);

            var result = Wait(renderer.RenderAsync(DiagramFakes.Request(source: "secret diagram text"), new object()));

            Assert.Equal(DiagramText.Failed, result.Error);
            Assert.False(result.Lasting);
            Assert.Equal("Mermaid drawing failed (InvalidOperationException)", Assert.Single(warnings));
        }

        [Fact]
        public void Kroki_kinds_without_a_server_say_they_need_Kroki()
        {
            var page = new FakePage { Answer = _ => DiagramFakes.Drawn() };
            using var renderer = Over(page);

            var result = Wait(renderer.RenderAsync(DiagramFakes.Request("plantuml", "a -> b"), new object()));

            Assert.Equal("PlantUML needs Kroki \u2014 turn it on in Settings \u2192 MicaPad.", result.Error);
            Assert.Empty(page.Requests);
        }

        [Fact]
        public void Dispose_answers_every_draw_and_closes_the_page()
        {
            var page = new FakePage();
            var renderer = Over(page);
            var running = renderer.RenderAsync(DiagramFakes.Request(source: "a"), new object());
            DiagramFakes.WaitUntil(() => page.Requests.Count == 1, "the draw");
            var waiting = renderer.RenderAsync(DiagramFakes.Request(source: "b"), new object());

            renderer.Dispose();

            Assert.Equal(DiagramText.Failed, Wait(waiting).Error);
            Assert.Equal(DiagramText.Failed, Wait(running).Error);
            Assert.True(page.Disposed);
            Assert.Equal(DiagramText.Failed, Wait(renderer.RenderAsync(DiagramFakes.Request(source: "c"), new object())).Error);
        }

        [Fact]
        public void The_cache_keeps_64_and_drops_the_least_recently_used()
        {
            var cache = new DiagramCache();
            for (int i = 0; i < 64; i++) cache.Add("k" + i, DiagramFakes.Picture());
            Assert.True(cache.TryGet("k0", out _));   // k0 is now the most recently used

            cache.Add("k64", DiagramFakes.Picture());

            Assert.Equal(64, cache.Count);
            Assert.True(cache.TryGet("k0", out _));
            Assert.False(cache.TryGet("k1", out _));
            Assert.True(cache.TryGet("k64", out _));
        }

        [Fact]
        public void The_key_changes_with_engine_server_theme_and_source()
        {
            string key = DiagramCacheKey.Of("mermaid", null, "Dark", "a");

            Assert.Equal(64, key.Length);
            Assert.Equal(key, DiagramCacheKey.Of("mermaid", null, "Dark", "a"));
            Assert.NotEqual(key, DiagramCacheKey.Of("graphviz", null, "Dark", "a"));
            Assert.NotEqual(key, DiagramCacheKey.Of("mermaid", "https://kroki.io", "Dark", "a"));
            Assert.NotEqual(key, DiagramCacheKey.Of("mermaid", null, "Light", "a"));
            Assert.NotEqual(key, DiagramCacheKey.Of("mermaid", null, "Dark", "b"));
        }

        [Fact]
        public void A_kroki_picture_has_one_key_for_both_themes_and_one_per_server()
        {
            var dark = DiagramFakes.Request("plantuml", "a -> b", PadThemes.Dark, "https://kroki.io");
            var light = DiagramFakes.Request("plantuml", "a -> b", PadThemes.Light, "https://kroki.io");
            var other = DiagramFakes.Request("plantuml", "a -> b", PadThemes.Dark, "http://localhost:8000");

            Assert.Equal(dark.Key, light.Key);
            Assert.NotEqual(dark.Key, other.Key);
            Assert.NotEqual(DiagramFakes.Request(theme: PadThemes.Dark).Key, DiagramFakes.Request(theme: PadThemes.Light).Key);
            Assert.True(dark.Dark);
            Assert.False(light.Dark);
        }

        [Fact]
        public void Css_colors_drop_the_alpha() => Assert.Equal("#0E0E13", DiagramRequest.Css(PadColor.Parse("#FF0E0E13")));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~DiagramRendererTests" 2>&1 | tail -5`
Expected: build error, `IDiagramPage` does not exist.

- [ ] **Step 3: Implement**

`Services/Pad/DiagramResult.cs`:

```csharp
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// What drawing a diagram gave: a picture (a PNG at twice its size, the SVG, and its size in
    /// device-independent pixels) or an error message. Lasting results — pictures and syntax
    /// errors — are cached; passing ones (a server out of reach, a timeout) are not.
    /// </summary>
    public sealed class DiagramResult
    {
        private DiagramResult()
        {
        }

        public byte[]? Png { get; private init; }

        public string? Svg { get; private init; }

        /// <summary>The picture's natural width in device-independent pixels.</summary>
        public double Width { get; private init; }

        public double Height { get; private init; }

        public string? Error { get; private init; }

        /// <summary>A page that helps with the error (the WebView2 Runtime download), or null.</summary>
        public Uri? HelpLink { get; private init; }

        /// <summary>True for a Kroki picture: drawn on a light card in both themes.</summary>
        public bool Paper { get; private init; }

        /// <summary>True when the same request would give the same result again.</summary>
        public bool Lasting { get; private init; }

        /// <summary>True when a newer request for the same block took this one's place before it was drawn.</summary>
        public bool IsReplaced { get; private init; }

        public bool IsPicture => Png != null;

        public static DiagramResult Picture(byte[] png, string svg, double width, double height, bool paper) =>
            new() { Png = png, Svg = svg, Width = width, Height = height, Paper = paper, Lasting = true };

        public static DiagramResult Failure(string error, bool lasting, Uri? helpLink = null) =>
            new() { Error = error, Lasting = lasting, HelpLink = helpLink };

        public static DiagramResult Replaced { get; } = new() { IsReplaced = true };
    }

    /// <summary>
    /// One diagram to draw: its kind and source, MicaPad's theme with its text and background
    /// colors as CSS (<c>#RRGGBB</c>), and the Kroki server (null while Kroki is off).
    /// </summary>
    public sealed record DiagramRequest(DiagramKind Kind, string Source, string Theme, string Foreground, string Background, string? KrokiServer)
    {
        /// <summary>True unless the theme is Light.</summary>
        public bool Dark => Theme != PadThemes.Light;

        /// <summary>
        /// The cache key (spec 2.4): engine, Kroki server, theme and source. A Kroki picture is the
        /// same in both themes (it sits on a light card), so its theme part is "paper" (R3).
        /// </summary>
        public string Key => DiagramCacheKey.Of(Kind.EngineId, Kind.NeedsKroki ? KrokiServer : null, Kind.NeedsKroki ? "paper" : Theme, Source);

        /// <summary>A palette color as CSS <c>#RRGGBB</c>; the alpha is dropped.</summary>
        public static string Css(PadColor color) =>
            string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);
    }

    /// <summary>The words MicaPad shows about diagrams.</summary>
    public static class DiagramText
    {
        public const string Drawing = "Drawing\u2026";
        public const string TooLarge = "Too large to draw";
        public const string TookTooLong = "Took too long to draw.";
        public const string RuntimeMissing = "Diagrams need the Microsoft Edge WebView2 Runtime.";
        public const string EngineStopped = "The diagram engine stopped. It starts again with the next drawing.";
        public const string Failed = "The diagram could not be drawn.";
        public const string CouldNotRead = "The picture could not be read.";

        /// <summary>Where the WebView2 Runtime is downloaded.</summary>
        public static readonly Uri RuntimeDownload = new("https://developer.microsoft.com/microsoft-edge/webview2/");

        /// <summary>"PlantUML needs Kroki — turn it on in Settings → MicaPad."</summary>
        public static string NeedsKroki(DiagramKind kind) => kind.Name + " needs Kroki \u2014 turn it on in Settings \u2192 MicaPad.";

        /// <summary>"The Kroki server could not be reached (kroki.io)."</summary>
        public static string Unreachable(string host) => "The Kroki server could not be reached (" + host + ").";
    }

    /// <summary>The cache key of a drawing: a SHA-256 of its engine, Kroki server, theme and source.</summary>
    public static class DiagramCacheKey
    {
        /// <summary>64 upper-case hex characters.</summary>
        public static string Of(string engine, string? krokiServer, string theme, string source)
        {
            string text = engine + "\0" + (krokiServer ?? "") + "\0" + theme + "\0" + source;
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }
    }
}
```

`Services/Pad/DiagramCache.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Drawn diagrams in memory, the least recently used dropped first (spec 2.4). Nothing is
    /// written to disk. Not thread-safe: the renderer locks around it.
    /// </summary>
    public sealed class DiagramCache
    {
        public const int DefaultCapacity = 64;

        private readonly int _capacity;
        private readonly Dictionary<string, LinkedListNode<(string Key, DiagramResult Result)>> _map = new();
        private readonly LinkedList<(string Key, DiagramResult Result)> _order = new();

        public DiagramCache(int capacity = DefaultCapacity) => _capacity = Math.Max(1, capacity);

        public int Count => _map.Count;

        /// <summary>The result for <paramref name="key"/>, which becomes the most recently used.</summary>
        public bool TryGet(string key, [MaybeNullWhen(false)] out DiagramResult result)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                result = node.Value.Result;
                return true;
            }
            result = null;
            return false;
        }

        /// <summary>Keeps <paramref name="result"/> as the most recently used, dropping the least recently used beyond the capacity.</summary>
        public void Add(string key, DiagramResult result)
        {
            if (_map.TryGetValue(key, out var old))
            {
                _order.Remove(old);
                _map.Remove(key);
            }
            _map[key] = _order.AddFirst((key, result));
            while (_map.Count > _capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }
}
```

`Services/Pad/DiagramPageContract.cs`:

```csharp
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// What the drawing page is asked: <see cref="Kind"/> is "mermaid", "dot", "markmap" or "svg"
    /// (a picture Kroki drew); the colors are CSS <c>#RRGGBB</c>.
    /// </summary>
    public sealed record PageRequest(string Kind, string Source, bool Dark, string Foreground, string Background);

    /// <summary>The page's answer: the SVG and a PNG with the picture's size in device-independent pixels, or an error.</summary>
    public sealed record PageDrawing(string? Svg, byte[]? Png, double Width, double Height, string? Error);

    /// <summary>The page that draws diagrams (in the app, a hidden WebView2: <c>Pad/DiagramPage</c>).</summary>
    public interface IDiagramPage : IDisposable
    {
        /// <summary>True once the page cannot draw any more (its browser process died).</summary>
        bool IsBroken { get; }

        /// <summary>Draws one diagram; cancelling abandons it.</summary>
        Task<PageDrawing> DrawAsync(PageRequest request, CancellationToken cancel);
    }

    /// <summary>The Microsoft Edge WebView2 Runtime is not installed, so there is no page.</summary>
    public sealed class DiagramRuntimeMissingException : Exception
    {
        public DiagramRuntimeMissingException(Exception? inner = null) : base("The WebView2 Runtime is not installed.", inner)
        {
        }
    }

    /// <summary>Draws diagrams: the renderer, or a test's fake.</summary>
    public interface IDiagramRenderer
    {
        /// <summary>A result already drawn for <paramref name="key"/> (<see cref="DiagramRequest.Key"/>).</summary>
        bool TryGetCached(string key, [MaybeNullWhen(false)] out DiagramResult result);

        /// <summary>
        /// Draws <paramref name="request"/>. <paramref name="slot"/> names the block asking: a newer
        /// request with the same slot replaces this one while it still waits (its result is then
        /// <see cref="DiagramResult.Replaced"/>). Never throws.
        /// </summary>
        Task<DiagramResult> RenderAsync(DiagramRequest request, object slot);
    }
}
```

`Services/Pad/DiagramRenderer.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Draws diagrams for every MicaPad window (spec 2.3, 2.4): one draw at a time, a newer request
    /// for the same block replacing a waiting older one, results kept in memory (64, least
    /// recently used out; nothing on disk). Built-in kinds go to the drawing page. The page is
    /// created on the first draw and replaced after it breaks or a draw runs over the limit.
    /// Never throws: every failure is a result, and a warning names only the engine and the
    /// exception type. Call <see cref="RenderAsync"/> from one thread (the UI thread in the app).
    /// </summary>
    public sealed class DiagramRenderer : IDiagramRenderer, IDisposable
    {
        public static readonly TimeSpan DefaultDrawLimit = TimeSpan.FromSeconds(15);

        private readonly Func<Task<IDiagramPage>> _createPage;
        private readonly Action<string> _warn;
        private readonly TimeSpan _drawLimit;
        private readonly DiagramCache _cache = new();
        private readonly List<Job> _waiting = new();
        private readonly object _gate = new();
        private readonly CancellationTokenSource _shutdown = new();
        private IDiagramPage? _page;
        private bool _pumping;
        private bool _disposed;

        public DiagramRenderer(Func<Task<IDiagramPage>> createPage, Action<string>? warn = null, TimeSpan? drawLimit = null)
        {
            _createPage = createPage;
            _warn = warn ?? (_ => { });
            _drawLimit = drawLimit ?? DefaultDrawLimit;
        }

        /// <summary>How many results the cache holds; for tests.</summary>
        internal int CachedCount
        {
            get { lock (_gate) return _cache.Count; }
        }

        public bool TryGetCached(string key, [MaybeNullWhen(false)] out DiagramResult result)
        {
            lock (_gate) return _cache.TryGet(key, out result);
        }

        public Task<DiagramResult> RenderAsync(DiagramRequest request, object slot)
        {
            Job job;
            lock (_gate)
            {
                if (_disposed) return Task.FromResult(DiagramResult.Failure(DiagramText.Failed, lasting: false));
                if (_cache.TryGet(request.Key, out var cached)) return Task.FromResult(cached);

                job = new Job(request, slot);
                int waiting = _waiting.FindIndex(j => ReferenceEquals(j.Slot, slot));
                if (waiting >= 0)
                {
                    _waiting[waiting].Done.TrySetResult(DiagramResult.Replaced);
                    _waiting[waiting] = job;
                }
                else
                {
                    _waiting.Add(job);
                }
                if (_pumping) return job.Done.Task;
                _pumping = true;
            }
            _ = PumpAsync();
            return job.Done.Task;
        }

        /// <summary>Answers every draw still waiting, abandons the running one and closes the page.</summary>
        public void Dispose()
        {
            List<Job> left;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                left = new List<Job>(_waiting);
                _waiting.Clear();
            }
            foreach (var job in left) job.Done.TrySetResult(DiagramResult.Failure(DiagramText.Failed, lasting: false));
            _shutdown.Cancel();
            DropPage();
        }

        private async Task PumpAsync()
        {
            while (true)
            {
                Job job;
                lock (_gate)
                {
                    if (_waiting.Count == 0 || _disposed)
                    {
                        _pumping = false;
                        return;
                    }
                    job = _waiting[0];
                    _waiting.RemoveAt(0);
                }

                DiagramResult result;
                try
                {
                    result = await DrawAsync(job.Request);
                }
                catch (Exception ex)
                {
                    _warn(job.Request.Kind.Name + " drawing failed (" + ex.GetType().Name + ")");
                    DropPage();
                    result = DiagramResult.Failure(DiagramText.Failed, lasting: false);
                }

                lock (_gate)
                {
                    if (_disposed) result = DiagramResult.Failure(DiagramText.Failed, lasting: false);
                    else if (result.Lasting) _cache.Add(job.Request.Key, result);
                }
                job.Done.TrySetResult(result);
            }
        }

        private async Task<DiagramResult> DrawAsync(DiagramRequest request)
        {
            if (request.Kind.NeedsKroki) return DiagramResult.Failure(DiagramText.NeedsKroki(request.Kind), lasting: false);
            var page = new PageRequest(request.Kind.PageKind, request.Source, request.Dark, request.Foreground, request.Background);
            return await DrawOnPageAsync(request.Kind, page, paper: false);
        }

        private async Task<DiagramResult> DrawOnPageAsync(DiagramKind kind, PageRequest request, bool paper)
        {
            if (_page is { IsBroken: true }) DropPage();

            IDiagramPage page;
            try
            {
                page = _page ??= await _createPage();
            }
            catch (DiagramRuntimeMissingException)
            {
                return DiagramResult.Failure(DiagramText.RuntimeMissing, lasting: false, DiagramText.RuntimeDownload);
            }

            PageDrawing drawing;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
            {
                limit.CancelAfter(_drawLimit);
                try
                {
                    drawing = await page.DrawAsync(request, limit.Token);
                }
                catch (OperationCanceledException)
                {
                    if (_shutdown.IsCancellationRequested) return DiagramResult.Failure(DiagramText.Failed, lasting: false);
                    _warn(kind.Name + " drawing took longer than "
                          + _drawLimit.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s; the page was closed");
                    DropPage();
                    return DiagramResult.Failure(DiagramText.TookTooLong, lasting: false);
                }
            }

            if (page.IsBroken)
            {
                DropPage();
                return DiagramResult.Failure(DiagramText.EngineStopped, lasting: false);
            }
            if (drawing.Error != null) return DiagramResult.Failure(drawing.Error, lasting: true);
            if (drawing.Png == null || drawing.Svg == null || !(drawing.Width > 0) || !(drawing.Height > 0))
                return DiagramResult.Failure(DiagramText.Failed, lasting: false);
            return DiagramResult.Picture(drawing.Png, drawing.Svg, drawing.Width, drawing.Height, paper);
        }

        private void DropPage()
        {
            IDiagramPage? page;
            lock (_gate)
            {
                page = _page;
                _page = null;
            }
            try
            {
                page?.Dispose();
            }
            catch (Exception ex)
            {
                _warn("Closing the diagram page failed (" + ex.GetType().Name + ")");
            }
        }

        private sealed class Job
        {
            public Job(DiagramRequest request, object slot)
            {
                Request = request;
                Slot = slot;
            }

            public DiagramRequest Request { get; }

            public object Slot { get; }

            public TaskCompletionSource<DiagramResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~DiagramRendererTests" 2>&1 | tail -5`
Expected: all pass. Then check the escapes: `LC_ALL=C grep -n '[^[:print:][:space:]]' Services/Pad/DiagramResult.cs tests/Kil0bitSystemMonitor.Tests/DiagramRendererTests.cs` prints nothing.

- [ ] **Step 5: Commit**

```bash
git add Services/Pad/DiagramResult.cs Services/Pad/DiagramCache.cs Services/Pad/DiagramPageContract.cs Services/Pad/DiagramRenderer.cs tests/Kil0bitSystemMonitor.Tests/DiagramFakes.cs tests/Kil0bitSystemMonitor.Tests/DiagramRendererTests.cs
git commit -m "feat(pad): diagram renderer - one draw at a time, newer requests replace waiting ones, 64 results in memory, failures as results" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 3: The drawing page — bundled scripts and the hidden WebView2

**Files:**
- Create: `Pad/Diagrams/render.html`, `Pad/Diagrams/frames.js`, `Pad/Diagrams/render.js`, `Pad/Diagrams/THIRD-PARTY.txt`
- Create (downloaded, never edited): `Pad/Diagrams/mermaid.min.js`, `Pad/Diagrams/viz-global.js`, `Pad/Diagrams/d3.min.js`, `Pad/Diagrams/markmap-view.js`, `Pad/Diagrams/markmap-lib.js`
- Modify: `Kil0bitSystemMonitor.csproj`
- Create: `Pad/DiagramPage.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/DiagramPageTests.cs`

**Interfaces:**
- Consumes: `IDiagramPage`, `PageRequest`, `PageDrawing`, `DiagramRuntimeMissingException`, `DiagramText` (Task 2).
- Produces: `internal sealed class DiagramPage : IDiagramPage` (namespace `Kil0bitSystemMonitor.Pad`) — `static Task<IDiagramPage> CreateAsync(string userDataFolder, string scriptsFolder)`, `static string DefaultUserDataFolder`, `static string ScriptsFolder`, `const string HostName = "micapad-diagrams.invalid"`, `const string PageUrl`, `bool IsBroken`, `Task<PageDrawing> DrawAsync(PageRequest, CancellationToken)`, `internal int RefusedRequests`, `internal Task<string> EvaluateForTestAsync(string expression)`, `void Dispose()`. The page protocol: the app posts `{ id, kind, source, dark, fg, bg }`; the page posts `{ ready: true }` once, then `{ id, ok: true, svg, png (base64), width, height }` or `{ id, ok: false, error }`.

- [ ] **Step 1: Download the scripts and check them**

```bash
D=/c/AIProject/kil0bit-system-monitor/Pad/Diagrams && mkdir -p $D
curl -sSfL -o $D/mermaid.min.js https://cdn.jsdelivr.net/npm/mermaid@12.0.0/dist/mermaid.min.js
curl -sSfL -o $D/viz-global.js https://cdn.jsdelivr.net/npm/@viz-js/viz@3.31.0/dist/viz-global.js
curl -sSfL -o $D/d3.min.js https://cdn.jsdelivr.net/npm/d3@7.9.0/dist/d3.min.js
curl -sSfL -o $D/markmap-view.js https://cdn.jsdelivr.net/npm/markmap-view@0.18.12/dist/browser/index.js
curl -sSfL -o $D/markmap-lib.js https://cdn.jsdelivr.net/npm/markmap-lib@0.18.12/dist/browser/index.iife.js
cd $D && sha256sum mermaid.min.js viz-global.js d3.min.js markmap-view.js markmap-lib.js
```

Expected, exactly:

```
28fca7ae6ebc7ed7bb63bde63136a74bfef14f296a57e403657eeb8b32836073 *mermaid.min.js
c9e0b310f9883910e01c66054b48b7ff4be3d8695141116635e72b0af152692e *viz-global.js
f2094bbf6141b359722c4fe454eb6c4b0f0e42cc10cc7af921fc158fceb86539 *d3.min.js
861eb6d20af18aaa300878d46d695722a73625c58b40f49236af027f18b74e07 *markmap-view.js
edfe321a3fe46e9c0f0af8370145aee180975f243f2242f0f8181ac4273e08e1 *markmap-lib.js
```

A different hash means a different file: stop and report BLOCKED with the hashes you got.

- [ ] **Step 2: Write the page**

`Pad/Diagrams/render.html`:

```html
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:">
<style>html,body{margin:0;padding:0;background:transparent;font-family:"Segoe UI",sans-serif}#stage{position:absolute;left:0;top:0;width:1600px}</style>
<script src="frames.js"></script>
<script src="mermaid.min.js"></script>
<script src="viz-global.js"></script>
<script src="d3.min.js"></script>
<script src="markmap-view.js"></script>
<script src="markmap-lib.js"></script>
<script src="render.js"></script>
</head>
<body><div id="stage"></div></body>
</html>
```

`Pad/Diagrams/frames.js`:

```js
"use strict";
// A hidden page never gets animation frames; d3 (Markmap's transitions) waits for them.
window.requestAnimationFrame = cb => setTimeout(() => cb(performance.now()), 0);
window.cancelAnimationFrame = id => clearTimeout(id);
```

`Pad/Diagrams/render.js`:

```js
"use strict";
// MicaPad's diagram page. The app posts { id, kind, source, dark, fg, bg }; the page answers
// { id, ok: true, svg, png, width, height } or { id, ok: false, error }. kind is "mermaid", "dot",
// "markmap" or "svg" (a picture Kroki drew). Nothing here reaches the network: the page's
// Content-Security-Policy and the app's request filter both refuse it.
(function () {
  const SVG_NS = "http://www.w3.org/2000/svg";
  const SCALE = 2;
  const MAX_SIDE = 4096;
  let counter = 0;
  let viz = null;

  const stage = () => document.getElementById("stage");

  // Lengths in px; Graphviz and Kroki often write pt.
  function length(text) {
    const m = /^\s*([0-9.]+)\s*(px|pt)?\s*$/.exec(text || "");
    if (!m) return 0;
    const n = parseFloat(m[1]);
    return m[2] === "pt" ? n * 4 / 3 : n;
  }

  // Gives the picture a fixed size in px (the viewBox's, else its width and height) and returns its markup.
  // The markup is parsed, never put into the page, so nothing in a picture runs or loads.
  function finish(svgText) {
    const doc = new DOMParser().parseFromString(svgText, "image/svg+xml");
    const el = doc.documentElement;
    if (!el || el.localName !== "svg" || doc.getElementsByTagName("parsererror").length) {
      throw new Error("The picture could not be read.");
    }
    let w = 0, h = 0;
    const box = (el.getAttribute("viewBox") || "").trim().split(/[\s,]+/).map(Number);
    if (box.length === 4 && box[2] > 0 && box[3] > 0) { w = box[2]; h = box[3]; }
    else { w = length(el.getAttribute("width")); h = length(el.getAttribute("height")); }
    if (!(w > 0 && h > 0)) throw new Error("The picture has no size.");
    el.setAttribute("width", String(w));
    el.setAttribute("height", String(h));
    el.style.removeProperty("max-width");
    if (!el.getAttribute("xmlns")) el.setAttribute("xmlns", SVG_NS);
    return { svg: new XMLSerializer().serializeToString(el), width: w, height: h };
  }

  async function drawMermaid(req) {
    mermaid.initialize({
      startOnLoad: false,
      theme: req.dark ? "dark" : "default",
      securityLevel: "strict",
      fontFamily: "\"Segoe UI\", sans-serif"
    });
    const id = "mermaid-" + (++counter);
    try {
      const result = await mermaid.render(id, req.source, stage());
      return finish(result.svg);
    } finally {
      for (const leftover of [document.getElementById(id), document.getElementById("d" + id)]) {
        if (leftover) leftover.remove();
      }
    }
  }

  async function drawDot(req) {
    if (!viz) viz = await Viz.instance();
    const text = viz.renderString(req.source, {
      format: "svg",
      graphAttributes: { bgcolor: "transparent", fontcolor: req.fg, fontname: "Helvetica,Arial,sans-serif" },
      nodeAttributes: { color: req.fg, fontcolor: req.fg, fontname: "Helvetica,Arial,sans-serif" },
      edgeAttributes: { color: req.fg, fontcolor: req.fg, fontname: "Helvetica,Arial,sans-serif" }
    });
    return finish(text.substring(text.indexOf("<svg")));
  }

  // Markmap starts its d3 transitions (duration 0) without waiting for them; let every one finish.
  async function settle(el) {
    for (let i = 0; i < 50; i++) {
      d3.timerFlush();
      let busy = false;
      el.querySelectorAll("*").forEach(n => {
        if (n.__transition && Object.keys(n.__transition).length) busy = true;
      });
      if (!busy) return;
      await new Promise(resolve => setTimeout(resolve, 10));
    }
  }

  async function drawMarkmap(req) {
    const { Transformer, Markmap } = window.markmap;
    const { root } = new Transformer([]).transform(req.source);
    const el = document.createElementNS(SVG_NS, "svg");
    el.setAttribute("width", "1600");
    el.setAttribute("height", "1200");
    el.style.color = req.fg;
    el.style.setProperty("--markmap-text-color", req.fg);
    el.style.setProperty("--markmap-circle-open-bg", req.bg);
    stage().appendChild(el);
    let mm = null;
    try {
      mm = new Markmap(el, { duration: 0, autoFit: false });
      await mm.setData(root);
      await settle(el);
      const r = mm.state.rect;
      const pad = 8;
      const w = r.x2 - r.x1 + 2 * pad, h = r.y2 - r.y1 + 2 * pad;
      el.querySelector("g").removeAttribute("transform");
      el.setAttribute("viewBox", (r.x1 - pad) + " " + (r.y1 - pad) + " " + w + " " + h);
      return finish(new XMLSerializer().serializeToString(el));
    } finally {
      if (mm) mm.destroy();
      el.remove();
    }
  }

  // The picture as a PNG, SCALE times its size, its longest side at most MAX_SIDE pixels.
  async function toPng(svg, width, height) {
    const scale = Math.min(SCALE, MAX_SIDE / Math.max(width, height));
    const pw = Math.max(1, Math.round(width * scale));
    const ph = Math.max(1, Math.round(height * scale));
    const img = new Image();
    await new Promise((resolve, reject) => {
      img.onload = resolve;
      img.onerror = () => reject(new Error("The picture could not be made."));
      img.src = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(svg);
    });
    const canvas = document.createElement("canvas");
    canvas.width = pw;
    canvas.height = ph;
    canvas.getContext("2d").drawImage(img, 0, 0, pw, ph);
    const url = canvas.toDataURL("image/png");
    return url.substring(url.indexOf(",") + 1);
  }

  async function draw(req) {
    const drawn = req.kind === "mermaid" ? await drawMermaid(req)
      : req.kind === "dot" ? await drawDot(req)
      : req.kind === "markmap" ? await drawMarkmap(req)
      : req.kind === "svg" ? finish(req.source)
      : null;
    if (!drawn) throw new Error("Unknown diagram kind.");
    const png = await toPng(drawn.svg, drawn.width, drawn.height);
    return { id: req.id, ok: true, svg: drawn.svg, png, width: drawn.width, height: drawn.height };
  }

  function message(x) {
    const text = String((x && x.message) || x || "The diagram could not be drawn.");
    return text.length > 2000 ? text.substring(0, 2000) : text;
  }

  window.chrome.webview.addEventListener("message", async e => {
    const req = e.data;
    let answer;
    try {
      answer = await draw(req);
    } catch (x) {
      answer = { id: req.id, ok: false, error: message(x) };
    }
    window.chrome.webview.postMessage(answer);
  });
  window.chrome.webview.postMessage({ ready: true });
})();
```

- [ ] **Step 3: Write `Pad/Diagrams/THIRD-PARTY.txt`**

Write the header with the Write tool (exactly this text):

```
MicaPad diagrams - third-party scripts

These files are copied unchanged from npm (through cdn.jsdelivr.net). They load only inside
MicaPad's hidden diagram page, which cannot reach the network. Each minified bundle keeps the
copyright comments of the code it includes.

File              Package               Version   License
mermaid.min.js    mermaid               12.0.0    MIT      https://github.com/mermaid-js/mermaid
viz-global.js     @viz-js/viz           3.31.0    MIT      https://github.com/mdaines/viz-js
                  includes Graphviz (EPL-2.0) and Expat (MIT), compiled to WebAssembly
d3.min.js         d3                    7.9.0     ISC      https://github.com/d3/d3
markmap-view.js   markmap-view          0.18.12   MIT      https://github.com/markmap/markmap
markmap-lib.js    markmap-lib           0.18.12   MIT      https://github.com/markmap/markmap

SHA-256
28fca7ae6ebc7ed7bb63bde63136a74bfef14f296a57e403657eeb8b32836073  mermaid.min.js
c9e0b310f9883910e01c66054b48b7ff4be3d8695141116635e72b0af152692e  viz-global.js
f2094bbf6141b359722c4fe454eb6c4b0f0e42cc10cc7af921fc158fceb86539  d3.min.js
861eb6d20af18aaa300878d46d695722a73625c58b40f49236af027f18b74e07  markmap-view.js
edfe321a3fe46e9c0f0af8370145aee180975f243f2242f0f8181ac4273e08e1  markmap-lib.js

render.html, frames.js and render.js are MicaStats' own (MIT, see LICENSE).
```

Then append the license texts:

```bash
T=/c/AIProject/kil0bit-system-monitor/Pad/Diagrams/THIRD-PARTY.txt
add() { printf '\n\n==== %s ====\n\n' "$1" >> $T && curl -sSfL "$2" >> $T; }
add "mermaid 12.0.0 (MIT)" https://cdn.jsdelivr.net/npm/mermaid@12.0.0/LICENSE
add "@viz-js/viz 3.31.0 (MIT)" https://raw.githubusercontent.com/mdaines/viz-js/v3/LICENSE
add "Graphviz, inside viz-global.js (EPL-2.0)" https://gitlab.com/graphviz/graphviz/-/raw/main/LICENSE
add "Expat, inside viz-global.js (MIT)" https://raw.githubusercontent.com/libexpat/libexpat/master/expat/COPYING
add "d3 7.9.0 (ISC)" https://cdn.jsdelivr.net/npm/d3@7.9.0/LICENSE
add "markmap-view 0.18.12 (MIT)" https://cdn.jsdelivr.net/npm/markmap-view@0.18.12/LICENSE
add "markmap-lib 0.18.12 (MIT)" https://cdn.jsdelivr.net/npm/markmap-lib@0.18.12/LICENSE
grep -c '^==== ' $T
```

Expected: `7`.

- [ ] **Step 4: Add the package and copy the folder to the output**

In `Kil0bitSystemMonitor.csproj`, in the `ItemGroup` with `AvalonEdit`, add (keeping the list alphabetical):

```xml
        <PackageReference Include="Microsoft.Web.WebView2" Version="1.0.4258.31" />
```

and after the `micapad.ico` `ItemGroup`, add:

```xml
    <ItemGroup>
        <!-- MicaPad diagrams: the drawing page and its scripts, copied to Diagrams\ beside MicaStats.exe (Pad/DiagramPage.cs). -->
        <None Update="Pad\Diagrams\*">
            <Link>Diagrams\%(Filename)%(Extension)</Link>
            <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
        </None>
    </ItemGroup>
```

Build and check: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" build tests/Kil0bitSystemMonitor.Tests 2>&1 | tail -3 && ls tests/Kil0bitSystemMonitor.Tests/bin/Debug/net8.0-windows/Diagrams` — expected: 0 errors, and the nine files (`THIRD-PARTY.txt`, `d3.min.js`, `frames.js`, `markmap-lib.js`, `markmap-view.js`, `mermaid.min.js`, `render.html`, `render.js`, `viz-global.js`).

- [ ] **Step 5: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/DiagramPageTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Microsoft.Web.WebView2.Core;
using Xunit;
using Xunit.Abstractions;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The real drawing page (spec "Testing": the renderer with a real WebView2), built on the
    /// shared UI thread over a temp user data folder and never shown. Where the WebView2 Runtime
    /// is not installed, the page tests write that they were skipped and pass.
    /// </summary>
    public class DiagramPageTests
    {
        private readonly ITestOutputHelper _output;

        public DiagramPageTests(ITestOutputHelper output) => _output = output;

        private static bool RuntimeInstalled()
        {
            try
            {
                return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch (WebView2RuntimeNotFoundException)
            {
                return false;
            }
        }

        private void WithPage(Func<DiagramPage, Task> test)
        {
            if (!RuntimeInstalled())
            {
                _output.WriteLine("Skipped: the Microsoft Edge WebView2 Runtime is not installed.");
                return;
            }
            using var dir = new PadTempDir();
            UiThread.Run(() =>
            {
                var created = DiagramPage.CreateAsync(dir.Root, DiagramPage.ScriptsFolder);
                UiPump.Wait(created, 30_000);
                var page = (DiagramPage)created.Result;
                try
                {
                    UiPump.Wait(test(page), 60_000);
                }
                finally
                {
                    page.Dispose();
                }
            });
        }

        private static Task<PageDrawing> Draw(DiagramPage page, string kind, string source, bool dark = false) =>
            page.DrawAsync(new PageRequest(kind, source, dark, dark ? "#EDEDF2" : "#1B1B1F", dark ? "#0E0E13" : "#FBFBFD"), CancellationToken.None);

        /// <summary>A PNG's width and height from its header.</summary>
        private static (int Width, int Height) PngSize(byte[] png)
        {
            Assert.True(png.Length > 24 && png[0] == 0x89 && png[1] == (byte)'P' && png[2] == (byte)'N' && png[3] == (byte)'G', "not a PNG");
            int Read(int at) => png[at] << 24 | png[at + 1] << 16 | png[at + 2] << 8 | png[at + 3];
            return (Read(16), Read(20));
        }

        [Fact]
        public void The_scripts_are_copied_beside_the_app()
        {
            string folder = DiagramPage.ScriptsFolder;
            string html = File.ReadAllText(Path.Combine(folder, "render.html"));

            foreach (string name in new[] { "frames.js", "mermaid.min.js", "viz-global.js", "d3.min.js", "markmap-view.js", "markmap-lib.js", "render.js" })
            {
                Assert.True(File.Exists(Path.Combine(folder, name)), name + " is missing");
                Assert.Contains("<script src=\"" + name + "\"></script>", html);
            }
            Assert.Contains("@viz-js/viz", File.ReadAllText(Path.Combine(folder, "THIRD-PARTY.txt")));
        }

        [Fact]
        public void Every_built_in_kind_draws_a_png_and_an_svg() => WithPage(async page =>
        {
            var samples = new (string Kind, string Source)[]
            {
                ("mermaid", "flowchart TD\n  A[Start] --> B{Ok?}\n  B -->|Yes| C[Done \u0E2A\u0E27\u0E31\u0E2A\u0E14\u0E35]"),
                ("mermaid", "sequenceDiagram\n  Alice->>Bob: Hello\n  Bob-->>Alice: Hi"),
                ("mermaid", "mindmap\n  root((MicaPad))\n    Diagrams\n      Mermaid\n      Graphviz\n    Notes"),
                ("dot", "digraph { rankdir=LR; a -> b -> c }"),
                ("markmap", "# MicaPad\n## Diagrams\n- Mermaid\n- Graphviz\n## Notes"),
            };
            foreach (var (kind, source) in samples)
            {
                var drawing = await Draw(page, kind, source);

                Assert.True(drawing.Error == null, kind + ": " + drawing.Error);
                Assert.Contains("<svg", drawing.Svg);
                Assert.True(drawing.Width > 0 && drawing.Height > 0, kind + " has no size");
                var (width, height) = PngSize(drawing.Png!);
                Assert.InRange(width, (int)(drawing.Width * 2) - 1, (int)(drawing.Width * 2) + 1);
                Assert.InRange(height, (int)(drawing.Height * 2) - 1, (int)(drawing.Height * 2) + 1);
            }
        });

        [Fact]
        public void Syntax_errors_come_back_as_messages() => WithPage(async page =>
        {
            Assert.Contains("Parse error", (await Draw(page, "mermaid", "flowchart TD\n  A -->")).Error);
            Assert.Contains("syntax error", (await Draw(page, "dot", "digraph { a -> }")).Error);
            Assert.Equal(DiagramText.CouldNotRead, (await Draw(page, "svg", "<html>nope</html>")).Error);

            // The page still draws after errors.
            Assert.Null((await Draw(page, "dot", "digraph { a -> b }")).Error);
        });

        [Fact]
        public void A_kroki_svg_in_points_gets_its_size_in_pixels() => WithPage(async page =>
        {
            var drawing = await Draw(page, "svg",
                "<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\" width=\"90pt\" height=\"30pt\"><rect width=\"120\" height=\"40\" fill=\"#eee\"/></svg>");

            Assert.Null(drawing.Error);
            Assert.Equal(120, drawing.Width, 3);
            Assert.Equal(40, drawing.Height, 3);
        });

        [Fact]
        public void A_huge_picture_is_scaled_to_4096_pixels() => WithPage(async page =>
        {
            string chain = string.Join(" -> ", Enumerable.Range(1, 120).Select(i => "n" + i));

            var drawing = await Draw(page, "dot", "digraph { rankdir=LR; " + chain + " }");

            Assert.Null(drawing.Error);
            Assert.True(drawing.Width * 2 > 4096);
            Assert.Equal(4096, PngSize(drawing.Png!).Width);
        });

        [Fact]
        public void Graphviz_follows_the_theme_colors() => WithPage(async page =>
        {
            var light = await Draw(page, "dot", "digraph { a -> b }", dark: false);
            var dark = await Draw(page, "dot", "digraph { a -> b }", dark: true);

            Assert.Contains("#1b1b1f", light.Svg, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("#ededf2", dark.Svg, StringComparison.OrdinalIgnoreCase);
        });

        [Fact]
        public void The_page_cannot_reach_the_network() => WithPage(async page =>
        {
            string answer = await page.EvaluateForTestAsync("fetch('https://example.com/').then(() => 'reached', () => 'refused')");

            Assert.Contains("\"refused\"", answer);
        });
    }
}
```

- [ ] **Step 6: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~DiagramPageTests" 2>&1 | tail -5`
Expected: build error, `DiagramPage` does not exist.

- [ ] **Step 7: Implement `Pad/DiagramPage.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using Kil0bitSystemMonitor.Services.Pad;
using Microsoft.Web.WebView2.Core;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// The hidden browser page that draws Mermaid, Graphviz and Markmap and turns any SVG into a
    /// PNG (spec 2.1-2.3, R5). It lives on a popup window that is never shown, with the WebView2
    /// controller hidden, so it is in no window's visual tree. Only
    /// https://micapad-diagrams.invalid/ (the app's Diagrams folder) loads: the page's
    /// Content-Security-Policy and this class's request filter (403) each refuse everything else.
    /// WebView2 runs in its own processes, so MicaStats' software drawing is unaffected. Use it on
    /// the UI thread.
    /// </summary>
    internal sealed class DiagramPage : IDiagramPage
    {
        internal const string HostName = "micapad-diagrams.invalid";
        internal const string PageUrl = "https://" + HostName + "/render.html";
        private const int PageWidth = 1600;
        private const int PageHeight = 1200;
        private static readonly TimeSpan ReadyLimit = TimeSpan.FromSeconds(15);

        private readonly CoreWebView2Environment _environment;
        private readonly HwndSource _host;
        private readonly CoreWebView2Controller _controller;
        private readonly CoreWebView2 _core;
        private readonly Dictionary<int, TaskCompletionSource<PageDrawing>> _pending = new();
        private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _nextId;
        private bool _disposed;

        private DiagramPage(CoreWebView2Environment environment, HwndSource host, CoreWebView2Controller controller)
        {
            _environment = environment;
            _host = host;
            _controller = controller;
            _core = controller.CoreWebView2;
        }

        /// <summary>%LOCALAPPDATA%\MicaStats\WebView2: the browser's own files (spec 2.1).</summary>
        public static string DefaultUserDataFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicaStats", "WebView2");

        /// <summary>The bundled scripts, copied beside MicaStats.exe.</summary>
        public static string ScriptsFolder => Path.Combine(AppContext.BaseDirectory, "Diagrams");

        public bool IsBroken { get; private set; }

        /// <summary>How many requests the filter refused; for tests.</summary>
        internal int RefusedRequests { get; private set; }

        /// <summary>Starts the browser, loads the page and waits until its scripts are ready (about a second).</summary>
        /// <exception cref="DiagramRuntimeMissingException">The WebView2 Runtime is not installed.</exception>
        public static async Task<IDiagramPage> CreateAsync(string userDataFolder, string scriptsFolder)
        {
            CoreWebView2Environment environment;
            try
            {
                environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, new CoreWebView2EnvironmentOptions());
            }
            catch (WebView2RuntimeNotFoundException ex)
            {
                throw new DiagramRuntimeMissingException(ex);
            }

            var host = new HwndSource(new HwndSourceParameters("MicaPad diagrams")
            {
                WindowStyle = unchecked((int)0x80000000),   // WS_POPUP, never WS_VISIBLE
                ExtendedWindowStyle = 0x08000080,           // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW: no taskbar button
                Width = PageWidth,
                Height = PageHeight,
            });
            CoreWebView2Controller controller;
            try
            {
                controller = await environment.CreateCoreWebView2ControllerAsync(host.Handle);
            }
            catch
            {
                host.Dispose();
                throw;
            }

            var page = new DiagramPage(environment, host, controller);
            try
            {
                await page.LoadAsync(scriptsFolder);
            }
            catch
            {
                page.Dispose();
                throw;
            }
            return page;
        }

        private async Task LoadAsync(string scriptsFolder)
        {
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, PageWidth, PageHeight);
            _controller.IsVisible = false;
            _controller.DefaultBackgroundColor = System.Drawing.Color.Transparent;

            var settings = _core.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;

            _core.SetVirtualHostNameToFolderMapping(HostName, scriptsFolder, CoreWebView2HostResourceAccessKind.Deny);
            _core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            _core.WebResourceRequested += OnResourceRequested;
            _core.WebMessageReceived += OnMessage;
            _core.ProcessFailed += OnProcessFailed;
            _core.NewWindowRequested += OnNewWindow;
            _core.Navigate(PageUrl);

            if (await Task.WhenAny(_ready.Task, Task.Delay(ReadyLimit)) != _ready.Task)
                throw new TimeoutException("The diagram page did not load.");
        }

        public Task<PageDrawing> DrawAsync(PageRequest request, CancellationToken cancel)
        {
            if (_disposed || IsBroken) return Task.FromResult(new PageDrawing(null, null, 0, 0, DiagramText.EngineStopped));

            int id = ++_nextId;
            var done = new TaskCompletionSource<PageDrawing>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pending) _pending[id] = done;
            cancel.Register(() =>
            {
                lock (_pending) _pending.Remove(id);
                done.TrySetCanceled(cancel);
            });

            _core.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                id,
                kind = request.Kind,
                source = request.Source,
                dark = request.Dark,
                fg = request.Foreground,
                bg = request.Background,
            }));
            return done.Task;
        }

        /// <summary>Runs a script in the page and returns its value as DevTools JSON; tests use it to show the page has no network.</summary>
        internal Task<string> EvaluateForTestAsync(string expression) =>
            _core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true }));

        private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && string.Equals(uri.Host, HostName, StringComparison.OrdinalIgnoreCase)) return;

            RefusedRequests++;
            e.Response = _environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
        }

        private void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;

        private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!e.Source.StartsWith("https://" + HostName + "/", StringComparison.OrdinalIgnoreCase)) return;

            int id;
            PageDrawing drawing;
            try
            {
                using var json = JsonDocument.Parse(e.WebMessageAsJson);
                var root = json.RootElement;
                if (root.TryGetProperty("ready", out _))
                {
                    _ready.TrySetResult(true);
                    return;
                }
                id = root.GetProperty("id").GetInt32();
                drawing = root.GetProperty("ok").GetBoolean()
                    ? new PageDrawing(root.GetProperty("svg").GetString(),
                                      Convert.FromBase64String(root.GetProperty("png").GetString() ?? ""),
                                      root.GetProperty("width").GetDouble(),
                                      root.GetProperty("height").GetDouble(),
                                      null)
                    : new PageDrawing(null, null, 0, 0, root.GetProperty("error").GetString() ?? DiagramText.Failed);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                return;   // a malformed answer: the draw it was for runs into the time limit
            }

            TaskCompletionSource<PageDrawing>? done;
            lock (_pending)
            {
                if (!_pending.Remove(id, out done)) return;
            }
            done.TrySetResult(drawing);
        }

        private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
        {
            IsBroken = true;
            FailPending();
        }

        private void FailPending()
        {
            List<TaskCompletionSource<PageDrawing>> left;
            lock (_pending)
            {
                left = new List<TaskCompletionSource<PageDrawing>>(_pending.Values);
                _pending.Clear();
            }
            foreach (var done in left) done.TrySetResult(new PageDrawing(null, null, 0, 0, DiagramText.EngineStopped));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            FailPending();
            try
            {
                _core.WebResourceRequested -= OnResourceRequested;
                _core.WebMessageReceived -= OnMessage;
                _core.ProcessFailed -= OnProcessFailed;
                _core.NewWindowRequested -= OnNewWindow;
                _controller.Close();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // The browser process is already gone.
            }
            _host.Dispose();
        }
    }
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~DiagramPageTests" -v n 2>&1 | grep -E "Passed|Failed|Skipped|error" | head -20`
Expected: 7 passed (on this machine the runtime is installed: no "Skipped" line in the output). Then the full suite (Global Constraints command): all pass.

- [ ] **Step 9: Commit**

```bash
git add Pad/Diagrams Pad/DiagramPage.cs Kil0bitSystemMonitor.csproj tests/Kil0bitSystemMonitor.Tests/DiagramPageTests.cs
git commit -m "feat(pad): diagram page - hidden WebView2 draws Mermaid, Graphviz and Markmap offline and makes PNGs; bundled scripts with licenses" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 4: Kroki — the client, the renderer's Kroki route, the settings

**Files:**
- Create: `Services/Pad/KrokiClient.cs`
- Modify: `Services/Pad/DiagramRenderer.cs` (constructor parameter `kroki`; the Kroki route in `DrawAsync`)
- Modify: `Models/SystemMetrics.cs` (`AppConfig`: `PadDiagrams`, `PadKroki`, `PadKrokiServer`)
- Test: `tests/Kil0bitSystemMonitor.Tests/KrokiClientTests.cs`, `tests/Kil0bitSystemMonitor.Tests/PadConfigTests.cs`

**Interfaces:**
- Consumes: `DiagramRenderer`, `DiagramText`, `PageRequest`, `DiagramResult` (Task 2); `FakePage`, `DiagramFakes` (Task 2 tests).
- Produces:
  - `sealed record KrokiResult(string? Svg, string? Error, bool Lasting)`.
  - `sealed class KrokiClient : IDisposable` — `const string DefaultServer = "https://kroki.io"`, `static TimeSpan DefaultTimeout` (10 s), `KrokiClient(HttpMessageHandler? handler = null, TimeSpan? timeout = null)`, `Task<KrokiResult> DrawAsync(string server, string type, string source, CancellationToken cancel = default)`, `static bool TryParseServer(string? text, out string? server)` (normalized: no trailing `/`), `static string HostOf(string server)` (`kroki.io`, `localhost:8000`).
  - `DiagramRenderer(Func<Task<IDiagramPage>> createPage, Action<string>? warn = null, TimeSpan? drawLimit = null, KrokiClient? kroki = null)`.
  - `AppConfig.PadDiagrams` (bool, default true), `AppConfig.PadKroki` (bool, default false), `AppConfig.PadKrokiServer` (string, default `"https://kroki.io"`; an invalid value reads as the default).

- [ ] **Step 1: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/KrokiClientTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>A Kroki server as a test scripts it; it records what was sent.</summary>
    internal sealed class FakeKrokiHandler : HttpMessageHandler
    {
        private readonly List<(HttpMethod Method, Uri Uri, string Body, string? ContentType)> _requests = new();

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _) => Task.FromResult(Answer(HttpStatusCode.OK, DiagramFakes.Svg));

        public IReadOnlyList<(HttpMethod Method, Uri Uri, string Body, string? ContentType)> Requests
        {
            get { lock (_requests) return _requests.ToArray(); }
        }

        public static HttpResponseMessage Answer(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "image/svg+xml") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancel);
            lock (_requests) _requests.Add((request.Method, request.RequestUri!, body, request.Content?.Headers.ContentType?.ToString()));
            return await Respond(request, cancel);
        }
    }

    /// <summary>Kroki (spec section 3): the request, its errors, the server setting, and the renderer's Kroki route.</summary>
    public class KrokiClientTests
    {
        private static KrokiResult Draw(KrokiClient client, string server, string type, string source)
        {
            var task = client.DrawAsync(server, type, source);
            Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "Kroki did not answer");
            return task.Result;
        }

        private static DiagramResult Wait(Task<DiagramResult> task)
        {
            Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "the draw did not finish");
            return task.Result;
        }

        [Fact]
        public void Only_the_block_text_is_posted_to_the_type_url()
        {
            var handler = new FakeKrokiHandler();
            using var client = new KrokiClient(handler);

            var result = Draw(client, "https://kroki.io", "plantuml", "@startuml\na -> b\n@enduml");

            Assert.Equal(DiagramFakes.Svg, result.Svg);
            Assert.Null(result.Error);
            Assert.True(result.Lasting);
            var sent = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, sent.Method);
            Assert.Equal("https://kroki.io/plantuml/svg", sent.Uri.AbsoluteUri);
            Assert.Equal("@startuml\na -> b\n@enduml", sent.Body);
            Assert.Equal("text/plain; charset=utf-8", sent.ContentType);
        }

        [Fact]
        public void A_server_with_a_path_keeps_it()
        {
            var handler = new FakeKrokiHandler();
            using var client = new KrokiClient(handler);
            Assert.True(KrokiClient.TryParseServer("http://localhost:8000/kroki/", out var server));

            Draw(client, server!, "d2", "a -> b");

            Assert.Equal("http://localhost:8000/kroki/d2/svg", Assert.Single(handler.Requests).Uri.AbsoluteUri);
        }

        [Fact]
        public void A_400_answer_shows_its_first_line_and_lasts()
        {
            var handler = new FakeKrokiHandler
            {
                Respond = (_, _) => Task.FromResult(FakeKrokiHandler.Answer(HttpStatusCode.BadRequest,
                    "\n  Syntax Error? (Assumed diagram type: sequence) (line: 2)  \nmore detail\n")),
            };
            using var client = new KrokiClient(handler);

            var result = Draw(client, "https://kroki.io", "plantuml", "@startuml\nnope\n@enduml");

            Assert.Null(result.Svg);
            Assert.Equal("Syntax Error? (Assumed diagram type: sequence) (line: 2)", result.Error);
            Assert.True(result.Lasting);
        }

        [Fact]
        public void Other_failures_say_the_server_could_not_be_reached_and_pass()
        {
            using var down = new KrokiClient(new FakeKrokiHandler
            {
                Respond = (_, _) => Task.FromResult(FakeKrokiHandler.Answer(HttpStatusCode.ServiceUnavailable, "busy")),
            });
            using var refused = new KrokiClient(new FakeKrokiHandler
            {
                Respond = (_, _) => throw new HttpRequestException("No connection could be made"),
            });
            using var slow = new KrokiClient(new FakeKrokiHandler
            {
                Respond = async (_, cancel) =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancel);
                    return FakeKrokiHandler.Answer(HttpStatusCode.OK, DiagramFakes.Svg);
                },
            }, timeout: TimeSpan.FromMilliseconds(100));

            foreach (var client in new[] { down, refused, slow })
            {
                var result = Draw(client, "https://kroki.io", "d2", "a -> b");

                Assert.Equal("The Kroki server could not be reached (kroki.io).", result.Error);
                Assert.False(result.Lasting);
            }
        }

        [Theory]
        [InlineData("https://kroki.io", "https://kroki.io")]
        [InlineData("https://kroki.io/", "https://kroki.io")]
        [InlineData("  http://localhost:8000  ", "http://localhost:8000")]
        [InlineData("https://diagrams.example.com/kroki", "https://diagrams.example.com/kroki")]
        public void A_server_is_an_http_or_https_address(string text, string normalized)
        {
            Assert.True(KrokiClient.TryParseServer(text, out var server));
            Assert.Equal(normalized, server);
        }

        [Theory]
        [InlineData("ftp://kroki.io")]
        [InlineData("kroki.io")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("https://user:secret@kroki.io")]
        [InlineData("https://kroki.io/?q=1")]
        [InlineData("javascript:alert(1)")]
        [InlineData("file:///C:/notes")]
        public void Anything_else_is_refused(string? text) => Assert.False(KrokiClient.TryParseServer(text, out _));

        [Theory]
        [InlineData("https://kroki.io", "kroki.io")]
        [InlineData("http://localhost:8000/kroki", "localhost:8000")]
        public void The_host_names_the_server_in_messages(string server, string host) => Assert.Equal(host, KrokiClient.HostOf(server));

        [Fact]
        public void The_renderer_fetches_a_kroki_picture_then_draws_it_on_paper()
        {
            var page = new FakePage { Answer = _ => DiagramFakes.Drawn(80, 40) };
            var handler = new FakeKrokiHandler
            {
                Respond = (_, _) => Task.FromResult(FakeKrokiHandler.Answer(HttpStatusCode.OK, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"40\"/>")),
            };
            using var renderer = new DiagramRenderer(() => Task.FromResult<IDiagramPage>(page), kroki: new KrokiClient(handler));
            var request = DiagramFakes.Request("puml", "@startuml\na -> b\n@enduml", PadThemes.Dark, "https://kroki.io");

            var result = Wait(renderer.RenderAsync(request, new object()));

            Assert.True(result.IsPicture);
            Assert.True(result.Paper);
            Assert.Equal("https://kroki.io/plantuml/svg", Assert.Single(handler.Requests).Uri.AbsoluteUri);
            var drawn = Assert.Single(page.Requests);
            Assert.Equal("svg", drawn.Kind);
            Assert.Equal("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"40\"/>", drawn.Source);
            Assert.False(drawn.Dark);
            Assert.True(renderer.TryGetCached(request.Key, out _));
        }

        [Fact]
        public void An_unreachable_server_is_not_cached_and_the_page_is_not_asked()
        {
            var page = new FakePage { Answer = _ => DiagramFakes.Drawn() };
            var handler = new FakeKrokiHandler
            {
                Respond = (_, _) => Task.FromResult(FakeKrokiHandler.Answer(HttpStatusCode.BadGateway, "")),
            };
            using var renderer = new DiagramRenderer(() => Task.FromResult<IDiagramPage>(page), kroki: new KrokiClient(handler));
            var request = DiagramFakes.Request("d2", "a -> b", server: "https://kroki.io");

            var result = Wait(renderer.RenderAsync(request, new object()));

            Assert.Equal("The Kroki server could not be reached (kroki.io).", result.Error);
            Assert.False(renderer.TryGetCached(request.Key, out _));
            Assert.Empty(page.Requests);
        }

        [Fact]
        public void A_kroki_syntax_error_is_cached()
        {
            var handler = new FakeKrokiHandler
            {
                Respond = (_, _) => Task.FromResult(FakeKrokiHandler.Answer(HttpStatusCode.BadRequest, "Error: unexpected token")),
            };
            using var renderer = new DiagramRenderer(() => Task.FromResult<IDiagramPage>(new FakePage()), kroki: new KrokiClient(handler));
            var request = DiagramFakes.Request("d2", "a ->", server: "https://kroki.io");

            Assert.Equal("Error: unexpected token", Wait(renderer.RenderAsync(request, new object())).Error);
            Assert.True(renderer.TryGetCached(request.Key, out _));
        }

        [Fact]
        public void An_answer_that_is_no_picture_is_not_cached()
        {
            var page = new FakePage { Answer = r => new PageDrawing(null, null, 0, 0, DiagramText.CouldNotRead) };
            var handler = new FakeKrokiHandler
            {
                Respond = (_, _) => Task.FromResult(FakeKrokiHandler.Answer(HttpStatusCode.OK, "<html>Sign in to the network</html>")),
            };
            using var renderer = new DiagramRenderer(() => Task.FromResult<IDiagramPage>(page), kroki: new KrokiClient(handler));
            var request = DiagramFakes.Request("d2", "a -> b", server: "https://kroki.io");

            var result = Wait(renderer.RenderAsync(request, new object()));

            Assert.Equal(DiagramText.CouldNotRead, result.Error);
            Assert.False(result.Lasting);
            Assert.False(renderer.TryGetCached(request.Key, out _));
        }
    }
}
```

Add to `tests/Kil0bitSystemMonitor.Tests/PadConfigTests.cs` (inside the class):

```csharp
        [Fact]
        public void Diagrams_default_to_drawing_offline_only()
        {
            var config = new AppConfig();

            Assert.True(config.PadDiagrams);
            Assert.False(config.PadKroki);
            Assert.Equal("https://kroki.io", config.PadKrokiServer);
        }

        [Fact]
        public void An_older_config_gets_the_diagram_defaults()
        {
            var config = JsonSerializer.Deserialize<AppConfig>("{\"PadMarkdown\": true}")!;

            Assert.True(config.PadDiagrams);
            Assert.False(config.PadKroki);
            Assert.Equal("https://kroki.io", config.PadKrokiServer);
        }

        [Theory]
        [InlineData("ftp://kroki.io")]
        [InlineData("not a server")]
        [InlineData("")]
        public void An_invalid_kroki_server_reads_as_the_default(string server)
        {
            var config = JsonSerializer.Deserialize<AppConfig>("{\"PadKrokiServer\": " + JsonSerializer.Serialize(server) + "}")!;

            Assert.Equal("https://kroki.io", config.PadKrokiServer);
        }

        [Fact]
        public void Diagram_settings_survive_a_round_trip()
        {
            var config = new AppConfig { PadDiagrams = false, PadKroki = true, PadKrokiServer = "http://localhost:8000/" };

            var back = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;

            Assert.False(back.PadDiagrams);
            Assert.True(back.PadKroki);
            Assert.Equal("http://localhost:8000", back.PadKrokiServer);
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~KrokiClientTests|FullyQualifiedName~PadConfigTests" 2>&1 | tail -5`
Expected: build error, `KrokiClient` does not exist.

- [ ] **Step 3: Implement**

`Services/Pad/KrokiClient.cs`:

```csharp
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>What a Kroki server gave: the SVG, or an error that lasts (a syntax error) or passes (no answer).</summary>
    public sealed record KrokiResult(string? Svg, string? Error, bool Lasting);

    /// <summary>
    /// Asks a Kroki server to draw one diagram (spec section 3): <c>POST {server}/{type}/svg</c>
    /// with the block's text — only that — as <c>text/plain; charset=utf-8</c>, 10 s at most.
    /// A 400 answer's first line is the error; anything else that fails says the server could not
    /// be reached. Answers over 16 MB are refused (R7).
    /// </summary>
    public sealed class KrokiClient : IDisposable
    {
        public const string DefaultServer = "https://kroki.io";
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
        internal const int MaxAnswerBytes = 16 * 1024 * 1024;
        private const int MaxErrorLength = 500;

        private readonly HttpClient _http;

        /// <param name="handler">Tests pass a fake server; null uses the system's network settings.</param>
        public KrokiClient(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
        {
            _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            _http.Timeout = timeout ?? DefaultTimeout;
            _http.MaxResponseContentBufferSize = MaxAnswerBytes;
        }

        /// <summary>
        /// An http or https address with a host, no user name, query or fragment, without its
        /// trailing slash; false for anything else (Settings refuses it).
        /// </summary>
        public static bool TryParseServer(string? text, [NotNullWhen(true)] out string? server)
        {
            server = null;
            if (!Uri.TryCreate((text ?? "").Trim(), UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            if (uri.Host.Length == 0 || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
            server = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            return true;
        }

        /// <summary>The server's host (and port) as messages name it: "kroki.io", "localhost:8000".</summary>
        public static string HostOf(string server) =>
            Uri.TryCreate(server, UriKind.Absolute, out var uri) ? uri.Authority : server;

        /// <summary>Draws <paramref name="source"/> as a <paramref name="type"/> diagram. Never throws for a network failure.</summary>
        public async Task<KrokiResult> DrawAsync(string server, string type, string source, CancellationToken cancel = default)
        {
            string unreachable = DiagramText.Unreachable(HostOf(server));
            try
            {
                using var content = new StringContent(source, Encoding.UTF8, "text/plain");
                using var response = await _http.PostAsync(server.TrimEnd('/') + "/" + type + "/svg", content, cancel);
                string body = await response.Content.ReadAsStringAsync(cancel);
                if (response.IsSuccessStatusCode) return new KrokiResult(body, null, Lasting: true);
                if (response.StatusCode == HttpStatusCode.BadRequest)
                    return new KrokiResult(null, FirstLine(body) ?? DiagramText.Failed, Lasting: true);
                return new KrokiResult(null, unreachable, Lasting: false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                return new KrokiResult(null, unreachable, Lasting: false);
            }
        }

        public void Dispose() => _http.Dispose();

        private static string? FirstLine(string body)
        {
            foreach (string line in body.Split('\n'))
            {
                string text = line.Trim();
                if (text.Length > 0) return text.Length > MaxErrorLength ? text.Substring(0, MaxErrorLength) : text;
            }
            return null;
        }
    }
}
```

In `Services/Pad/DiagramRenderer.cs`:

1. Add the field `private readonly KrokiClient? _kroki;`, change the constructor to

```csharp
        public DiagramRenderer(Func<Task<IDiagramPage>> createPage, Action<string>? warn = null, TimeSpan? drawLimit = null, KrokiClient? kroki = null)
        {
            _createPage = createPage;
            _warn = warn ?? (_ => { });
            _drawLimit = drawLimit ?? DefaultDrawLimit;
            _kroki = kroki;
        }
```

and say in the class comment "Kroki kinds first go to the Kroki server (when one is set), then to the page as an SVG.".

2. Replace `DrawAsync` with:

```csharp
        private async Task<DiagramResult> DrawAsync(DiagramRequest request)
        {
            if (!request.Kind.NeedsKroki)
            {
                var page = new PageRequest(request.Kind.PageKind, request.Source, request.Dark, request.Foreground, request.Background);
                return await DrawOnPageAsync(request.Kind, page, paper: false);
            }

            if (request.KrokiServer == null || _kroki == null)
                return DiagramResult.Failure(DiagramText.NeedsKroki(request.Kind), lasting: false);

            var kroki = await _kroki.DrawAsync(request.KrokiServer, request.Kind.KrokiType!, request.Source, _shutdown.Token);
            if (kroki.Svg == null) return DiagramResult.Failure(kroki.Error ?? DiagramText.Failed, kroki.Lasting);

            // Kroki's colors cannot follow the theme: the picture goes on a light card in both (spec 3).
            var drawn = await DrawOnPageAsync(request.Kind, new PageRequest("svg", kroki.Svg, false, "#000000", "#FFFFFF"), paper: true);
            return drawn.IsPicture ? drawn : DiagramResult.Failure(drawn.Error ?? DiagramText.Failed, lasting: false, drawn.HelpLink);
        }
```

3. In `Dispose`, after `DropPage();`, add `_kroki?.Dispose();`.

In `Models/SystemMetrics.cs` (`AppConfig`, MicaPad section), add the fields after `_padAutoClose`:

```csharp
        private bool _padDiagrams = true;
        private bool _padKroki;
        private string _padKrokiServer = Kil0bitSystemMonitor.Services.Pad.KrokiClient.DefaultServer;
```

and the properties after `PadAutoClose`:

```csharp
        /// <summary>Draw a picture under each diagram block (Mermaid, Graphviz, Markmap; the rest through Kroki) in Markdown tabs.</summary>
        public bool PadDiagrams { get => _padDiagrams; set { Set(ref _padDiagrams, value); } }

        /// <summary>
        /// Draw the types MicaPad cannot draw itself (PlantUML, D2, ...) by sending that block's
        /// text to <see cref="PadKrokiServer"/>. Off by default: nothing leaves the PC until it is on.
        /// </summary>
        public bool PadKroki { get => _padKroki; set { Set(ref _padKroki, value); } }

        /// <summary>The Kroki server: an http or https address, kept without its trailing slash; anything else reads as https://kroki.io.</summary>
        public string PadKrokiServer
        {
            get => _padKrokiServer;
            set
            {
                Set(ref _padKrokiServer, Kil0bitSystemMonitor.Services.Pad.KrokiClient.TryParseServer(value, out var server)
                    ? server
                    : Kil0bitSystemMonitor.Services.Pad.KrokiClient.DefaultServer);
            }
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~KrokiClientTests|FullyQualifiedName~PadConfigTests|FullyQualifiedName~DiagramRendererTests" 2>&1 | tail -5`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Services/Pad/KrokiClient.cs Services/Pad/DiagramRenderer.cs Models/SystemMetrics.cs tests/Kil0bitSystemMonitor.Tests/KrokiClientTests.cs tests/Kil0bitSystemMonitor.Tests/PadConfigTests.cs
git commit -m "feat(pad): Kroki - post only the block's text, errors as results, pictures on a light card; diagram settings in the config" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 5: The picture under the fence — element and control

**Files:**
- Create: `Pad/DiagramElement.cs`
- Create: `Pad/DiagramPicture.cs` (`DiagramView`, `DiagramPicture`)
- Test: `tests/Kil0bitSystemMonitor.Tests/DiagramPictureTests.cs`

**Interfaces:**
- Consumes: `DiagramResult`, `DiagramText` (Task 2); `PadPalette`, `PadThemeApplier.ToBrush(PadColor) : SolidColorBrush`, `EditorMenus.Style(ContextMenu, PadPalette)`, `EditorMenus.Item(string header, string? gesture, Action action, bool enabled = true, string? icon = null)` (existing).
- Produces:
  - `internal sealed class DiagramElement : VisualLineElement` — `DiagramElement(UIElement picture)`, `UIElement Picture`.
  - `internal sealed class DiagramView` — init properties `DiagramResult? Result` (null: still drawing), `required PadPalette Palette`, `double MaxWidth` (default 600), `bool CanHideCode`, `bool CodeHidden`, `Action? ToggleCode`, `Action? CopyPicture`, `Action? SavePng`, `Action? SaveSvg`, `Action<Uri>? OpenLink`.
  - `internal sealed class DiagramPicture : Border` — `DiagramPicture(DiagramView view)`, `DiagramView View`, `bool IsDrawing`, `Image? Image`, `Button? CodeButton`, `TextBox? ErrorText`, `Hyperlink? HelpLink`, `const double PaperPadding = 8`, `static BitmapSource BitmapOf(DiagramResult result)` (decoded once per result).

- [ ] **Step 1: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/DiagramPictureTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

// UseWindowsForms puts System.Windows.Forms and System.Drawing in scope; these names exist in both.
using Brush = System.Windows.Media.Brush;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The picture under a closing fence (spec section 4): where it sits, and what it shows and offers.</summary>
    public class DiagramPictureTests
    {
        /// <summary>Puts one diagram element at the end of a line, as the diagram generator does.</summary>
        private sealed class AtLineEnd : VisualLineElementGenerator
        {
            public int Line { get; init; }
            public UIElement Picture { get; init; } = null!;

            public override int GetFirstInterestedOffset(int startOffset)
            {
                var line = CurrentContext.Document.GetLineByNumber(Line);
                return line.EndOffset >= startOffset && line.EndOffset <= CurrentContext.VisualLine.LastDocumentLine.EndOffset ? line.EndOffset : -1;
            }

            public override VisualLineElement ConstructElement(int offset) => new DiagramElement(Picture);
        }

        private static DiagramView View(DiagramResult? result, double maxWidth = 600, PadPalette? palette = null) =>
            new() { Result = result, Palette = palette ?? PadPalette.Dark, MaxWidth = maxWidth };

        private static bool Same(PadColor expected, Brush brush) =>
            brush is SolidColorBrush solid && solid.Color == Color.FromArgb(expected.A, expected.R, expected.G, expected.B);

        [Fact]
        public void The_picture_gets_its_own_row_under_the_fence_and_the_caret_stays_before_it() => UiThread.Run(() =>
        {
            var view = new TextView { Document = new TextDocument("```mermaid\nA-->B\n```\nafter") };
            var picture = new Border { Width = 300, Height = 120 };
            view.ElementGenerators.Add(new AtLineEnd { Line = 3, Picture = picture });
            view.Measure(new Size(600, 400));
            view.Arrange(new Rect(0, 0, 600, 400));
            view.UpdateLayout();
            view.EnsureVisualLines();

            var line = view.GetVisualLine(3)!;
            Assert.Equal(2, line.TextLines.Count);
            var top = picture.TranslatePoint(new Point(0, 0), view);
            Assert.Equal(line.VisualTop + line.TextLines[0].Height, top.Y, 1);
            Assert.Equal(0, top.X, 1);
            Assert.True(line.Height >= 120);

            var element = line.Elements.OfType<DiagramElement>().Single();
            int end = line.GetVisualColumn(line.LastDocumentLine.EndOffset - line.FirstDocumentLine.Offset);
            Assert.Equal(element.VisualColumn, end);
            Assert.Equal(-1, element.GetNextCaretPosition(element.VisualColumn, LogicalDirection.Forward, CaretPositioningMode.Normal));
            Assert.Equal(element.VisualColumn, element.GetNextCaretPosition(element.VisualColumn + 2, LogicalDirection.Backward, CaretPositioningMode.Normal));
            Assert.Equal(0, element.DocumentLength);
        });

        [Fact]
        public void While_drawing_it_says_so() => UiThread.Run(() =>
        {
            var picture = new DiagramPicture(View(null));

            Assert.True(picture.IsDrawing);
            Assert.Equal("Drawing\u2026", Assert.IsType<TextBlock>(picture.Child).Text);
            Assert.Null(picture.ContextMenu);
            Assert.Null(picture.CodeButton);
        });

        [Fact]
        public void A_picture_fits_the_width_and_is_never_enlarged() => UiThread.Run(() =>
        {
            var wide = new DiagramPicture(View(DiagramFakes.Picture(1000, 500), maxWidth: 400));
            var small = new DiagramPicture(View(DiagramFakes.Picture(100, 50), maxWidth: 400));

            Assert.Equal(400, wide.Image!.Width);
            Assert.Equal(200, wide.Image.Height);
            Assert.Equal(100, small.Image!.Width);
            Assert.Equal(50, small.Image.Height);
            Assert.False(wide.IsDrawing);
            Assert.NotNull(wide.ContextMenu);

            var result = DiagramFakes.Picture();
            Assert.Same(DiagramPicture.BitmapOf(result), DiagramPicture.BitmapOf(result));   // decoded once
            Assert.Equal(1, DiagramPicture.BitmapOf(result).PixelWidth);
        });

        [Fact]
        public void A_kroki_picture_sits_on_a_white_card_in_the_dark_theme() => UiThread.Run(() =>
        {
            var picture = new DiagramPicture(View(DiagramFakes.Picture(100, 50, paper: true), palette: PadPalette.Dark));

            var card = Assert.IsType<Border>(picture.Image!.Parent);
            Assert.Equal(Colors.White, ((SolidColorBrush)card.Background).Color);
            Assert.Equal(new Thickness(DiagramPicture.PaperPadding), card.Padding);
        });

        [Fact]
        public void An_error_shows_its_message_selectable_in_the_alert_color() => UiThread.Run(() =>
        {
            var picture = new DiagramPicture(View(DiagramResult.Failure("Parse error on line 3:\n---^", lasting: true)));

            Assert.Equal("Parse error on line 3:\n---^", picture.ErrorText!.Text);
            Assert.True(picture.ErrorText.IsReadOnly);
            var box = Assert.IsType<Border>(picture.Child);
            Assert.True(Same(PadPalette.Dark.AlertRed, box.BorderBrush));
            Assert.Null(picture.ContextMenu);
            Assert.Null(picture.HelpLink);
            Assert.Null(picture.Image);
        });

        [Fact]
        public void The_missing_runtime_error_links_to_the_download() => UiThread.Run(() =>
        {
            Uri? opened = null;
            var picture = new DiagramPicture(new DiagramView
            {
                Result = DiagramResult.Failure(DiagramText.RuntimeMissing, lasting: false, DiagramText.RuntimeDownload),
                Palette = PadPalette.Light,
                OpenLink = uri => opened = uri,
            });

            picture.HelpLink!.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));

            Assert.Equal(DiagramText.RuntimeDownload, opened);
            Assert.Equal(DiagramText.RuntimeMissing, picture.ErrorText!.Text);
        });

        [Fact]
        public void Hide_code_shows_on_hover_and_says_what_it_will_do() => UiThread.Run(() =>
        {
            int toggled = 0;
            var picture = new DiagramPicture(new DiagramView
            {
                Result = DiagramFakes.Picture(),
                Palette = PadPalette.Dark,
                CanHideCode = true,
                ToggleCode = () => toggled++,
            });

            Assert.Equal("Hide code", picture.CodeButton!.Content);
            Assert.Equal(Visibility.Hidden, picture.CodeButton.Visibility);
            var hover = (UIElement)picture.Child;
            hover.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            Assert.Equal(Visibility.Visible, picture.CodeButton.Visibility);
            hover.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            Assert.Equal(Visibility.Hidden, picture.CodeButton.Visibility);

            picture.CodeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(1, toggled);

            var hidden = new DiagramPicture(new DiagramView { Result = DiagramFakes.Picture(), Palette = PadPalette.Dark, CanHideCode = true, CodeHidden = true, ToggleCode = () => { } });
            Assert.Equal("Show code", hidden.CodeButton!.Content);
            Assert.Null(new DiagramPicture(new DiagramView { Result = DiagramFakes.Picture(), Palette = PadPalette.Dark, ToggleCode = () => { } }).CodeButton);
        });

        [Fact]
        public void The_picture_menu_copies_and_saves() => UiThread.Run(() =>
        {
            var done = new List<string>();
            var picture = new DiagramPicture(new DiagramView
            {
                Result = DiagramFakes.Picture(),
                Palette = PadPalette.Light,
                CopyPicture = () => done.Add("copy"),
                SavePng = () => done.Add("png"),
                SaveSvg = () => done.Add("svg"),
            });

            var items = picture.ContextMenu!.Items.OfType<MenuItem>().ToList();
            Assert.Equal(new[] { "Copy picture", "Save as PNG\u2026", "Save as SVG\u2026" }, items.Select(i => (string)i.Header));
            foreach (var item in items) item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Equal(new[] { "copy", "png", "svg" }, done);
        });
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~DiagramPictureTests" 2>&1 | tail -5`
Expected: build error, `DiagramElement` does not exist.

- [ ] **Step 3: Implement**

`Pad/DiagramElement.cs`:

```csharp
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// The picture under a closing fence line (spec section 4, R9): at the line's end, a line
    /// break and then the picture, standing for no document text. The fence text stays ordinary,
    /// editable text above it. The caret stops only in front of the element, so it never sits
    /// after the picture; copying and the document are unaffected.
    /// </summary>
    internal sealed class DiagramElement : VisualLineElement
    {
        public DiagramElement(UIElement picture) : base(2, 0) => Picture = picture;

        public UIElement Picture { get; }

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
            startVisualColumn == VisualColumn
                ? new TextEndOfLine(1)
                : new InlineObjectRun(1, TextRunProperties, Picture);

        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode)
        {
            if (direction == LogicalDirection.Forward) return visualColumn < VisualColumn ? VisualColumn : -1;
            return visualColumn > VisualColumn ? VisualColumn : -1;
        }
    }
}
```

`Pad/DiagramPicture.cs`:

```csharp
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kil0bitSystemMonitor.Services.Pad;

// UseWindowsForms puts System.Windows.Forms and System.Drawing in scope; these names exist in both.
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using TextBox = System.Windows.Controls.TextBox;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>What one picture under a fence shows and does; the board builds one for each drawing of its line.</summary>
    internal sealed class DiagramView
    {
        /// <summary>The picture or the error; null while the first drawing runs.</summary>
        public DiagramResult? Result { get; init; }

        public required PadPalette Palette { get; init; }

        /// <summary>The text area's width: a picture is scaled down to it, never up.</summary>
        public double MaxWidth { get; init; } = 600;

        /// <summary>True when the block has lines Hide code can fold.</summary>
        public bool CanHideCode { get; init; }

        /// <summary>True while the block's code is folded away.</summary>
        public bool CodeHidden { get; init; }

        public Action? ToggleCode { get; init; }

        public Action? CopyPicture { get; init; }

        public Action? SavePng { get; init; }

        public Action? SaveSvg { get; init; }

        /// <summary>Opens the error's help link (through the window's safe-link path).</summary>
        public Action<Uri>? OpenLink { get; init; }
    }

    /// <summary>
    /// One block's picture, a small "Drawing…" line while its first drawing runs, or its error in
    /// a box of the palette's alert color with selectable text (spec section 4). A picture has a
    /// Hide code / Show code button on hover and the right-click menu Copy picture, Save as PNG…,
    /// Save as SVG…. A Kroki picture sits on a white card in both themes.
    /// </summary>
    internal sealed class DiagramPicture : Border
    {
        internal const double PaperPadding = 8;

        /// <summary>Each result's bitmap, decoded once however often its line is drawn.</summary>
        private static readonly ConditionalWeakTable<DiagramResult, BitmapSource> s_bitmaps = new();

        public DiagramPicture(DiagramView view)
        {
            View = view;
            Margin = new Thickness(0, 4, 0, 8);
            HorizontalAlignment = HorizontalAlignment.Left;
            Cursor = Cursors.Arrow;
            if (view.Result == null) Child = DrawingText(view.Palette);
            else if (view.Result.IsPicture) Child = PictureOf(view, view.Result);
            else Child = ErrorOf(view, view.Result);
        }

        internal DiagramView View { get; }

        internal bool IsDrawing => View.Result == null;

        internal Image? Image { get; private set; }

        internal Button? CodeButton { get; private set; }

        internal TextBox? ErrorText { get; private set; }

        internal Hyperlink? HelpLink { get; private set; }

        /// <summary>The result's PNG as a frozen bitmap.</summary>
        internal static BitmapSource BitmapOf(DiagramResult result) => s_bitmaps.GetValue(result, r =>
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(r.Png!);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        });

        private static UIElement DrawingText(PadPalette palette) => new TextBlock
        {
            Text = DiagramText.Drawing,
            FontSize = 12,
            FontStyle = FontStyles.Italic,
            Foreground = PadThemeApplier.ToBrush(palette.Muted),
        };

        private UIElement PictureOf(DiagramView view, DiagramResult result)
        {
            double room = view.MaxWidth - (result.Paper ? 2 * PaperPadding : 0);
            double width = Math.Max(1, Math.Min(result.Width, room));
            Image = new Image
            {
                Source = BitmapOf(result),
                Width = width,
                Height = width * result.Height / result.Width,
                Stretch = Stretch.Uniform,
            };
            RenderOptions.SetBitmapScalingMode(Image, BitmapScalingMode.HighQuality);

            UIElement content = Image;
            if (result.Paper)
            {
                content = new Border
                {
                    Background = Brushes.White,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(PaperPadding),
                    Child = Image,
                };
            }

            var grid = new Grid { Background = Brushes.Transparent };
            grid.Children.Add(content);
            if (view.CanHideCode && view.ToggleCode is { } toggle)
            {
                var button = new Button
                {
                    Content = view.CodeHidden ? "Show code" : "Hide code",
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(4),
                    Padding = new Thickness(8, 2, 8, 2),
                    FontSize = 12,
                    Visibility = Visibility.Hidden,
                };
                button.Click += (s, e) => toggle();
                grid.Children.Add(button);
                grid.MouseEnter += (s, e) => button.Visibility = Visibility.Visible;
                grid.MouseLeave += (s, e) => button.Visibility = Visibility.Hidden;
                CodeButton = button;
            }

            var menu = new ContextMenu();
            EditorMenus.Style(menu, view.Palette);
            ModernWpf.ThemeManager.SetRequestedTheme(menu, view.Palette.IsDark ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);
            menu.Items.Add(EditorMenus.Item("Copy picture", null, () => view.CopyPicture?.Invoke(), icon: "\uE8C8"));
            menu.Items.Add(EditorMenus.Item("Save as PNG\u2026", null, () => view.SavePng?.Invoke(), icon: "\uE74E"));
            menu.Items.Add(EditorMenus.Item("Save as SVG\u2026", null, () => view.SaveSvg?.Invoke(), icon: "\uE74E"));
            ContextMenu = menu;
            return grid;
        }

        private UIElement ErrorOf(DiagramView view, DiagramResult result)
        {
            var palette = view.Palette;
            ErrorText = new TextBox
            {
                // An explicit style: ModernWpf's would add its own border and focus look.
                Style = new Style(typeof(TextBox)),
                Text = result.Error ?? DiagramText.Failed,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = PadThemeApplier.ToBrush(palette.Text),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(0),
            };

            var panel = new StackPanel();
            panel.Children.Add(ErrorText);
            if (result.HelpLink is { } link)
            {
                HelpLink = new Hyperlink(new Run("Get the WebView2 Runtime")) { Foreground = PadThemeApplier.ToBrush(palette.MdLink) };
                HelpLink.Click += (s, e) => view.OpenLink?.Invoke(link);
                panel.Children.Add(new TextBlock(HelpLink) { Margin = new Thickness(0, 4, 0, 0), FontSize = 12 });
            }

            return new Border
            {
                Child = panel,
                MaxWidth = view.MaxWidth,
                BorderBrush = PadThemeApplier.ToBrush(palette.AlertRed),
                BorderThickness = new Thickness(1),
                Background = PadThemeApplier.ToBrush(palette.AlertRed with { A = 0x1F }),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
            };
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~DiagramPictureTests" 2>&1 | tail -5`
Expected: all pass. Check escapes: `LC_ALL=C grep -n '[^[:print:][:space:]]' Pad/DiagramPicture.cs Pad/DiagramElement.cs tests/Kil0bitSystemMonitor.Tests/DiagramPictureTests.cs` prints only comment lines (the "…" in the class comment is allowed in comments) — no string literal may hold a raw non-ASCII character.

- [ ] **Step 5: Commit**

```bash
git add Pad/DiagramElement.cs Pad/DiagramPicture.cs tests/Kil0bitSystemMonitor.Tests/DiagramPictureTests.cs
git commit -m "feat(pad): diagram picture - its own row under the closing fence, Drawing and error states, Hide code, Copy and Save menu" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 6: Pictures in the editor — board, generator, Hide code, exports

**Files:**
- Modify: `Pad/MarkdownDocumentCache.cs` (`OpeningLineOf`, `ClosingLineOf`)
- Modify: `Pad/FoldingController.cs` (`ExtraFolds`)
- Create: `Pad/DiagramServices.cs`, `Pad/DiagramBoard.cs`, `Pad/DiagramGenerator.cs`
- Modify: `Pad/EditorLanguage.cs` (installs the pictures with Markdown)
- Modify: `tests/Kil0bitSystemMonitor.Tests/DiagramFakes.cs` (add `FakeRenderer`)
- Test: `tests/Kil0bitSystemMonitor.Tests/DiagramBoardTests.cs`

**Interfaces:**
- Consumes: `DiagramBlocks.Read`, `DiagramKinds.FromFence`, `FenceTracker.Openings` (Task 1); `IDiagramRenderer`, `DiagramRequest`, `DiagramResult`, `DiagramText` (Task 2); `DiagramElement`, `DiagramPicture`, `DiagramView` (Task 5); `MarkdownDocumentCache`, `FoldingController`, `EditorLanguage`, `PadPalette` (existing).
- Produces:
  - `MarkdownDocumentCache.OpeningLineOf(TextDocument, int lineNumber) : int`, `MarkdownDocumentCache.ClosingLineOf(TextDocument, int lineNumber) : int` (1-based, 0 for none).
  - `FoldingController.ExtraFolds : Func<TextDocument, IEnumerable<NewFolding>>?` (merged into every recompute).
  - `internal sealed class DiagramServices` — `required IDiagramRenderer Renderer`, `required Func<bool> Enabled`, `required Func<string?> KrokiServer`, `TimeSpan Pause` (600 ms), `Action<Uri> OpenLink`, `Func<byte[], bool> TrySetClipboardImage`, `Func<string, string, string?> AskSavePath` (default name, filter → path or null), `Action<string> ShowStatus`, `Action<string> Warn`.
  - `internal sealed class DiagramBoard` — `DiagramBoard(TextEditor, MarkdownDocumentCache, FoldingController?, DiagramServices, Func<PadPalette>)`, `TextDocument Document`, `int Draws`, `void Detach()`, `void Refresh()`, `void DrawDue()`, `void WarnOnce(string)`, `DiagramBlock? BlockClosedBy(DocumentLine)`, `UIElement PictureFor(DocumentLine, DiagramBlock)`, `bool IsCodeHidden(DocumentLine)`, `void SetCodeHidden(DocumentLine, bool)`.
  - `internal sealed class DiagramGenerator : VisualLineElementGenerator` — `DiagramGenerator(MarkdownDocumentCache, DiagramBoard)`.
  - `EditorLanguage.Diagrams : DiagramServices?` (set by the window), `EditorLanguage.DiagramBoard : DiagramBoard?`, `EditorLanguage.RefreshDiagrams()`.
  - Test helper `FakeRenderer : IDiagramRenderer` with `Cache`, `Calls`, `Finish(int index, DiagramResult result)`.

- [ ] **Step 1: Add the fake renderer**

Append to `tests/Kil0bitSystemMonitor.Tests/DiagramFakes.cs` (inside the namespace; add `using System.Diagnostics.CodeAnalysis;` at the top):

```csharp
    /// <summary>A renderer that records each request and answers when the test says; lasting answers are cached, as the real one does.</summary>
    internal sealed class FakeRenderer : IDiagramRenderer
    {
        public Dictionary<string, DiagramResult> Cache { get; } = new();

        public List<(DiagramRequest Request, object Slot, TaskCompletionSource<DiagramResult> Done)> Calls { get; } = new();

        public bool TryGetCached(string key, [MaybeNullWhen(false)] out DiagramResult result) => Cache.TryGetValue(key, out result);

        public Task<DiagramResult> RenderAsync(DiagramRequest request, object slot)
        {
            var done = new TaskCompletionSource<DiagramResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add((request, slot, done));
            return done.Task;
        }

        /// <summary>Answers call <paramref name="index"/>; the caller's continuation runs at the next dispatcher pump.</summary>
        public void Finish(int index, DiagramResult result)
        {
            var call = Calls[index];
            if (result.Lasting) Cache[call.Request.Key] = result;
            call.Done.TrySetResult(result);
        }
    }
```

- [ ] **Step 2: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/DiagramBoardTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Pictures in a Markdown editor (spec section 4 and "Testing": editor), over a fake renderer.
    /// The 600 ms pause is the board's DrawDue, called by hand (R8); its timer is set to an hour so
    /// it never fires inside a test.
    /// </summary>
    public class DiagramBoardTests
    {
        private sealed class Board
        {
            public Board(string text, PadPalette? palette = null)
            {
                Palette = palette ?? PadPalette.Dark;
                Editor = new TextEditor { Document = new TextDocument(text) };
                Language = new EditorLanguage(Editor, () => Palette, folds: true)
                {
                    Warn = Warnings.Add,
                    Diagrams = new DiagramServices
                    {
                        Renderer = Renderer,
                        Enabled = () => Enabled,
                        KrokiServer = () => Server,
                        Pause = TimeSpan.FromHours(1),
                        TrySetClipboardImage = png =>
                        {
                            Clipboard = png;
                            return ClipboardWorks;
                        },
                        AskSavePath = (name, filter) =>
                        {
                            AskedName = name;
                            AskedFilter = filter;
                            return SavePath;
                        },
                        ShowStatus = Status.Add,
                        Warn = Warnings.Add,
                    },
                };
                Language.Apply(PadLanguages.Markdown);
                Render();
            }

            public PadPalette Palette { get; set; }
            public TextEditor Editor { get; }
            public EditorLanguage Language { get; }
            public FakeRenderer Renderer { get; } = new();
            public bool Enabled { get; set; } = true;
            public string? Server { get; set; }
            public List<string> Status { get; } = new();
            public List<string> Warnings { get; } = new();
            public byte[]? Clipboard { get; private set; }
            public bool ClipboardWorks { get; set; } = true;
            public string? SavePath { get; set; }
            public string? AskedName { get; private set; }
            public string? AskedFilter { get; private set; }

            public DiagramBoard Diagrams => Language.DiagramBoard!;
            public TextView View => Editor.TextArea.TextView;

            public void Render()
            {
                View.Measure(new Size(600, 400));
                View.Arrange(new Rect(0, 0, 600, 400));
                View.EnsureVisualLines();
            }

            /// <summary>Runs what the dispatcher has queued (a finished draw's continuation, a fence repaint), then lays out again.</summary>
            public void PumpAndRender()
            {
                PadLanguageWindowTests.Pump();
                Render();
            }

            /// <summary>The picture under line <paramref name="number"/>, or null.</summary>
            public DiagramPicture? PictureUnder(int number) =>
                View.GetVisualLine(number)?.Elements.OfType<DiagramElement>().SingleOrDefault()?.Picture as DiagramPicture;
        }

        [Fact]
        public void The_pause_after_typing_is_600_ms() =>
            Assert.Equal(TimeSpan.FromMilliseconds(600), new DiagramServices { Renderer = new FakeRenderer(), Enabled = () => true, KrokiServer = () => null }.Pause);

        [Fact]
        public void The_fence_cache_pairs_closing_and_opening_lines() => UiThread.Run(() =>
        {
            var document = new TextDocument("```mermaid\nx\n```\n~~~\ny");
            var cache = new MarkdownDocumentCache();

            Assert.Equal(1, cache.OpeningLineOf(document, 3));
            Assert.Equal(3, cache.ClosingLineOf(document, 1));
            Assert.Equal(0, cache.OpeningLineOf(document, 4));   // opens and never closes
            Assert.Equal(0, cache.ClosingLineOf(document, 4));
            Assert.Equal(0, cache.OpeningLineOf(document, 2));

            document.Insert(0, "```\n");   // "```mermaid" is now code inside the new block
            Assert.Equal(1, cache.OpeningLineOf(document, 4));
            Assert.Equal(0, cache.OpeningLineOf(document, 2));
            Assert.Equal(4, cache.ClosingLineOf(document, 1));
        });

        [Fact]
        public void A_mermaid_block_gets_one_picture_under_its_closing_fence() => UiThread.Run(() =>
        {
            var board = new Board("# Notes\n```mermaid\nflowchart LR\n  a --> b\n```\nafter");

            Assert.Null(board.PictureUnder(2));
            Assert.Null(board.PictureUnder(6));
            Assert.True(board.PictureUnder(5)!.IsDrawing);
            var call = Assert.Single(board.Renderer.Calls);
            Assert.Equal("flowchart LR\n  a --> b", call.Request.Source);
            Assert.Equal(PadThemes.Dark, call.Request.Theme);
            Assert.Equal(DiagramRequest.Css(PadPalette.Dark.Text), call.Request.Foreground);
            Assert.Equal(DiagramRequest.Css(PadPalette.Dark.Background), call.Request.Background);
            Assert.Null(call.Request.KrokiServer);

            board.Renderer.Finish(0, DiagramFakes.Picture(120, 60));
            board.PumpAndRender();

            Assert.Equal(120, board.PictureUnder(5)!.Image!.Width);
            Assert.Single(board.Renderer.Calls);   // drawn again from the cache
            Assert.Equal(1, board.Diagrams.Draws);
        });

        [Fact]
        public void Another_language_or_draw_diagrams_off_gets_no_picture() => UiThread.Run(() =>
        {
            var board = new Board("```mermaid\nflowchart LR\n  a --> b\n```");
            Assert.NotNull(board.PictureUnder(4));

            board.Language.Apply(PadLanguages.ById("json")!);
            board.Render();
            Assert.Null(board.Language.DiagramBoard);
            Assert.Null(board.PictureUnder(4));
            Assert.Empty(board.View.ElementGenerators.OfType<DiagramGenerator>());

            board.Language.Apply(PadLanguages.Markdown);
            board.Enabled = false;
            board.Language.RefreshDiagrams();
            board.Render();
            Assert.Null(board.Language.DiagramBoard);
            Assert.Null(board.PictureUnder(4));

            board.Enabled = true;
            board.Language.RefreshDiagrams();
            board.Render();
            Assert.NotNull(board.PictureUnder(4));
            Assert.Single(board.View.ElementGenerators.OfType<DiagramGenerator>());
        });

        [Fact]
        public void An_unclosed_block_gets_no_picture_until_it_is_closed() => UiThread.Run(() =>
        {
            var board = new Board("```mermaid\nflowchart LR\n  a --> b");
            Assert.Empty(board.Renderer.Calls);

            board.Editor.Document.Insert(board.Editor.Document.TextLength, "\n```");
            board.PumpAndRender();

            Assert.True(board.PictureUnder(4)!.IsDrawing);
            Assert.Empty(board.Renderer.Calls);   // still typing

            board.Diagrams.DrawDue();
            board.Render();
            Assert.Single(board.Renderer.Calls);
        });

        [Fact]
        public void Typing_in_a_block_draws_once_after_the_pause_and_keeps_the_old_picture() => UiThread.Run(() =>
        {
            var board = new Board("```mermaid\nflowchart LR\n  a --> b\n```");
            board.Renderer.Finish(0, DiagramFakes.Picture(120, 60));
            board.PumpAndRender();
            var document = board.Editor.Document;

            document.Insert(document.GetLineByNumber(3).EndOffset, " --> c");
            board.PumpAndRender();
            document.Insert(document.GetLineByNumber(3).EndOffset, " --> d");
            board.View.Redraw();
            board.PumpAndRender();

            Assert.Single(board.Renderer.Calls);                      // nothing while typing
            Assert.Equal(120, board.PictureUnder(4)!.Image!.Width);   // the old picture stays

            board.Diagrams.DrawDue();
            board.Render();

            Assert.Equal(2, board.Renderer.Calls.Count);
            Assert.Equal("flowchart LR\n  a --> b --> c --> d", board.Renderer.Calls[1].Request.Source);
            Assert.Equal(120, board.PictureUnder(4)!.Image!.Width);   // still, until the new one is ready

            board.Renderer.Finish(1, DiagramFakes.Picture(200, 60));
            board.PumpAndRender();
            Assert.Equal(200, board.PictureUnder(4)!.Image!.Width);
        });

        [Fact]
        public void An_error_result_shows_the_error_box() => UiThread.Run(() =>
        {
            var board = new Board("```dot\ndigraph { a -> }\n```");

            board.Renderer.Finish(0, DiagramResult.Failure("syntax error in line 1 near '}'", lasting: true));
            board.PumpAndRender();

            Assert.Equal("syntax error in line 1 near '}'", board.PictureUnder(3)!.ErrorText!.Text);
        });

        [Fact]
        public void A_block_over_the_limit_says_it_is_too_large_and_is_not_drawn() => UiThread.Run(() =>
        {
            var board = new Board("```dot\n" + new string('x', DiagramBlocks.MaxSourceLength + 1) + "\n```");

            Assert.Equal("Too large to draw", board.PictureUnder(3)!.ErrorText!.Text);
            Assert.Empty(board.Renderer.Calls);
        });

        [Fact]
        public void A_plantuml_block_needs_kroki_until_it_is_on() => UiThread.Run(() =>
        {
            var board = new Board("```plantuml\n@startuml\na -> b\n@enduml\n```");

            Assert.Equal("PlantUML needs Kroki \u2014 turn it on in Settings \u2192 MicaPad.", board.PictureUnder(5)!.ErrorText!.Text);
            Assert.Empty(board.Renderer.Calls);

            board.Server = "https://kroki.io";
            board.Language.RefreshDiagrams();
            board.Render();

            var call = Assert.Single(board.Renderer.Calls);
            Assert.Equal("https://kroki.io", call.Request.KrokiServer);
            Assert.Equal("plantuml", call.Request.Kind.KrokiType);
        });

        [Fact]
        public void A_passing_failure_is_not_asked_again_until_the_settings_change() => UiThread.Run(() =>
        {
            var board = new Board("```d2\na -> b\n```");
            board.Server = "https://kroki.io";
            board.Language.RefreshDiagrams();
            board.Render();
            board.Renderer.Finish(0, DiagramResult.Failure("The Kroki server could not be reached (kroki.io).", lasting: false));
            board.PumpAndRender();
            Assert.Equal("The Kroki server could not be reached (kroki.io).", board.PictureUnder(3)!.ErrorText!.Text);

            for (int i = 0; i < 3; i++)   // scrolled away and back, repainted
            {
                board.View.Redraw();
                board.Render();
            }
            Assert.Single(board.Renderer.Calls);

            board.Language.RefreshDiagrams();   // Kroki or its server changed in Settings
            board.Render();
            Assert.Equal(2, board.Renderer.Calls.Count);
        });

        [Fact]
        public void Hide_code_folds_the_lines_inside_the_block_and_show_code_unfolds_them() => UiThread.Run(() =>
        {
            var board = new Board("```mermaid\nflowchart LR\n  a --> b\n```\nafter");
            board.Renderer.Finish(0, DiagramFakes.Picture());
            board.PumpAndRender();
            var document = board.Editor.Document;
            var closing = document.GetLineByNumber(4);
            var manager = board.Language.Folding!.Manager!;
            Assert.Equal("Hide code", board.PictureUnder(4)!.CodeButton!.Content);

            board.PictureUnder(4)!.View.ToggleCode!();
            board.Render();

            var fold = Assert.Single(manager.AllFoldings, f => f.IsFolded);
            Assert.Equal(document.GetLineByNumber(1).EndOffset, fold.StartOffset);
            Assert.Equal(document.GetLineByNumber(3).EndOffset, fold.EndOffset);
            Assert.True(board.Diagrams.IsCodeHidden(closing));
            Assert.Equal("Show code", board.PictureUnder(4)!.CodeButton!.Content);

            board.Language.Folding.Update();   // the recompute after an edit keeps it folded
            Assert.Single(manager.AllFoldings, f => f.IsFolded);

            board.PictureUnder(4)!.View.ToggleCode!();
            board.Render();
            Assert.DoesNotContain(manager.AllFoldings, f => f.IsFolded);
            Assert.False(board.Diagrams.IsCodeHidden(closing));
            Assert.Equal("Hide code", board.PictureUnder(4)!.CodeButton!.Content);
        });

        [Fact]
        public void Only_the_blocks_in_view_are_drawn() => UiThread.Run(() =>
        {
            string text = string.Join("\n", Enumerable.Range(1, 50).Select(i => "```dot\ndigraph { a" + i + " -> b }\n```\ntext"));

            var board = new Board(text);

            Assert.InRange(board.Renderer.Calls.Count, 1, 10);
        });

        [Fact]
        public void A_draw_finishing_after_the_board_detached_changes_nothing() => UiThread.Run(() =>
        {
            var board = new Board("```mermaid\nflowchart LR\n  a --> b\n```");
            var first = board.Diagrams;

            board.Editor.Document = new TextDocument("other text");
            board.Language.Apply(PadLanguages.Markdown);
            board.Renderer.Finish(0, DiagramFakes.Picture());
            board.PumpAndRender();

            Assert.NotSame(first, board.Language.DiagramBoard);
            Assert.Equal("other text", board.Editor.Document.Text);
            Assert.Empty(board.Warnings);
        });

        [Fact]
        public void A_theme_switch_draws_the_other_theme_once() => UiThread.Run(() =>
        {
            var board = new Board("```mermaid\nflowchart LR\n  a --> b\n```");
            board.Renderer.Finish(0, DiagramFakes.Picture());
            board.PumpAndRender();

            board.Palette = PadPalette.Light;
            board.Language.Redraw();
            board.Render();
            Assert.Equal(PadThemes.Light, board.Renderer.Calls[1].Request.Theme);
            board.Renderer.Finish(1, DiagramFakes.Picture());
            board.PumpAndRender();

            board.Palette = PadPalette.Dark;
            board.Language.Redraw();
            board.Render();
            Assert.Equal(2, board.Renderer.Calls.Count);   // the dark picture came from the cache
        });

        [Fact]
        public void Copy_picture_in_the_dark_theme_copies_the_light_drawing() => UiThread.Run(() =>
        {
            var board = new Board("```dot\ndigraph { a -> b }\n```");
            board.Renderer.Finish(0, DiagramFakes.Picture());
            board.PumpAndRender();

            board.PictureUnder(3)!.View.CopyPicture!();
            var light = board.Renderer.Calls[1];
            Assert.Equal(PadThemes.Light, light.Request.Theme);
            Assert.Equal(DiagramRequest.Css(PadPalette.Light.Text), light.Request.Foreground);
            byte[] png = (byte[])DiagramFakes.Png.Clone();
            board.Renderer.Finish(1, DiagramResult.Picture(png, DiagramFakes.Svg, 100, 50, paper: false));
            board.PumpAndRender();

            Assert.Same(png, board.Clipboard);
            Assert.Empty(board.Status);

            board.ClipboardWorks = false;
            board.PictureUnder(3)!.View.CopyPicture!();   // the light drawing is cached now
            Assert.Equal("Clipboard busy, try again", Assert.Single(board.Status));
        });

        [Fact]
        public void Save_as_writes_the_picture_where_the_user_chose() => UiThread.Run(() =>
        {
            using var dir = new PadTempDir();
            var board = new Board("```dot\ndigraph { a -> b }\n```", PadPalette.Light);
            board.Renderer.Finish(0, DiagramFakes.Picture());
            board.PumpAndRender();
            var view = board.PictureUnder(3)!.View;

            board.SavePath = dir.PathOf("flow.svg");
            view.SaveSvg!();
            Assert.Equal("diagram.svg", board.AskedName);
            Assert.Equal("SVG picture (*.svg)|*.svg", board.AskedFilter);
            Assert.Equal(DiagramFakes.Svg, File.ReadAllText(board.SavePath));

            board.SavePath = dir.PathOf("flow.png");
            view.SavePng!();
            Assert.Equal("diagram.png", board.AskedName);
            Assert.Equal("PNG picture (*.png)|*.png", board.AskedFilter);
            Assert.Equal(DiagramFakes.Png, File.ReadAllBytes(board.SavePath));

            board.SavePath = null;   // the user cancelled
            view.SavePng!();
            Assert.Single(board.Renderer.Calls);   // every export came from the cache
            Assert.Empty(board.Status);
        });
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~DiagramBoardTests" 2>&1 | tail -5`
Expected: build error, `DiagramServices` does not exist.

- [ ] **Step 4: Implement the cache and folding additions**

In `Pad/MarkdownDocumentCache.cs`, add the fields after `_kinds`:

```csharp
        private int[] _openings = Array.Empty<int>();
        private int[] _closings = Array.Empty<int>();
```

add after `KindOf`:

```csharp
        /// <summary>The 1-based line that opened the fence line <paramref name="lineNumber"/> closes, or 0 when it closes none.</summary>
        public int OpeningLineOf(TextDocument document, int lineNumber)
        {
            Track(document);
            if (_stale) Recompute();
            int index = lineNumber - 1;
            return index >= 0 && index < _openings.Length ? _openings[index] : 0;
        }

        /// <summary>The 1-based line that closes the fence line <paramref name="lineNumber"/> opens, or 0 (it opens none, or never closes).</summary>
        public int ClosingLineOf(TextDocument document, int lineNumber)
        {
            Track(document);
            if (_stale) Recompute();
            int index = lineNumber - 1;
            return index >= 0 && index < _closings.Length ? _closings[index] : 0;
        }
```

in `Detach`, after `_kinds = Array.Empty<MdFence>();`, add:

```csharp
            _openings = Array.Empty<int>();
            _closings = Array.Empty<int>();
```

and in `Recompute`, after `_kinds = FenceTracker.Classify(lines);`, add:

```csharp
            _openings = FenceTracker.Openings(_kinds);
            _closings = new int[_kinds.Length];
            for (int i = 0; i < _openings.Length; i++)
                if (_openings[i] > 0) _closings[_openings[i] - 1] = i + 1;
```

Also extend the class comment: "…shared by the Markdown colorizer, the background renderer, the bullet generator and the diagram pictures (which fence closes which)."

In `Pad/FoldingController.cs`, add after `Manager`:

```csharp
        /// <summary>
        /// Folds added to the language's own at every recompute: the diagram blocks whose code is
        /// hidden (Hide code). Null when nothing adds any.
        /// </summary>
        internal Func<TextDocument, IEnumerable<NewFolding>>? ExtraFolds { get; set; }
```

and in `Update`, replace

```csharp
                var foldings = Compute(_document, _language, out int firstError);
                _manager.UpdateFoldings(foldings, firstError);
```

with

```csharp
                var foldings = Compute(_document, _language, out int firstError);
                // UpdateFoldings needs them in order of their start.
                if (ExtraFolds is { } extra) foldings = foldings.Concat(extra(_document)).OrderBy(f => f.StartOffset).ToList();
                _manager.UpdateFoldings(foldings, firstError);
```

- [ ] **Step 5: Implement the services, the board and the generator**

`Pad/DiagramServices.cs`:

```csharp
using System;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>What the diagram pictures of an editor need from the window and the app. Tests pass fakes.</summary>
    internal sealed class DiagramServices
    {
        public required IDiagramRenderer Renderer { get; init; }

        /// <summary>True while Settings → MicaPad → Draw diagrams is on.</summary>
        public required Func<bool> Enabled { get; init; }

        /// <summary>The Kroki server while Draw other types with Kroki is on; null while it is off.</summary>
        public required Func<string?> KrokiServer { get; init; }

        /// <summary>How long typing must pause before a block is drawn again (spec 4: 600 ms). Tests lengthen it.</summary>
        public TimeSpan Pause { get; init; } = TimeSpan.FromMilliseconds(600);

        /// <summary>Opens an error's help link through the window's safe-link path.</summary>
        public Action<Uri> OpenLink { get; init; } = _ => { };

        /// <summary>Puts a PNG on the clipboard; false when the clipboard stays busy.</summary>
        public Func<byte[], bool> TrySetClipboardImage { get; init; } = _ => false;

        /// <summary>Asks where to save (default file name, filter); null when the user cancels.</summary>
        public Func<string, string, string?> AskSavePath { get; init; } = (_, _) => null;

        public Action<string> ShowStatus { get; init; } = _ => { };

        /// <summary>Logs a warning; it names an engine and an exception type, never a diagram's text.</summary>
        public Action<string> Warn { get; init; } = _ => { };
    }
}
```

`Pad/DiagramBoard.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// The diagram pictures of one Markdown document in one editor (spec section 4): each block's
    /// shown result and pending draw (kept per closing fence line), the pause after typing (R8),
    /// Hide code through the editor's folding, and the exports (R1). Nothing here waits on a draw:
    /// the renderer answers later and the block's line is drawn again then. Blocks out of view are
    /// never asked for, because the generator only runs for the lines in view.
    /// </summary>
    internal sealed class DiagramBoard
    {
        private const double MinPictureWidth = 120;
        private const double PictureMargin = 24;
        private static readonly TimeSpan ResizePause = TimeSpan.FromMilliseconds(200);

        private readonly TextEditor _editor;
        private readonly MarkdownDocumentCache _cache;
        private readonly FoldingController? _folding;
        private readonly DiagramServices _services;
        private readonly Func<PadPalette> _palette;
        private readonly TextDocument _document;
        private readonly DispatcherTimer _pauseTimer;
        private readonly DispatcherTimer _resizeTimer;
        private readonly Func<TextDocument, IEnumerable<NewFolding>> _hiddenFolds;
        private readonly ConditionalWeakTable<DocumentLine, BlockState> _states = new();
        private readonly List<TextAnchor> _hidden = new();
        private bool _attached = true;
        private bool _editing;
        private bool _warned;
        private int _generation;

        public DiagramBoard(TextEditor editor, MarkdownDocumentCache cache, FoldingController? folding, DiagramServices services, Func<PadPalette> palette)
        {
            _editor = editor;
            _cache = cache;
            _folding = folding;
            _services = services;
            _palette = palette;
            _document = editor.Document;

            _pauseTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = services.Pause };
            _pauseTimer.Tick += (s, e) =>
            {
                _pauseTimer.Stop();
                DrawDue();
            };
            _resizeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ResizePause };
            _resizeTimer.Tick += (s, e) =>
            {
                _resizeTimer.Stop();
                RedrawPictureLines();
            };

            _document.Changed += OnChanged;
            _editor.TextArea.TextView.SizeChanged += OnViewSizeChanged;
            _hiddenFolds = HiddenFolds;
            if (_folding != null) _folding.ExtraFolds = _hiddenFolds;
        }

        private enum DiagramExport
        {
            Copy,
            Png,
            Svg,
        }

        /// <summary>The document the pictures belong to.</summary>
        internal TextDocument Document => _document;

        /// <summary>How many draws this board asked for; for tests.</summary>
        internal int Draws { get; private set; }

        /// <summary>Stops following the document; a draw that finishes later changes nothing.</summary>
        public void Detach()
        {
            if (!_attached) return;
            _attached = false;
            _pauseTimer.Stop();
            _resizeTimer.Stop();
            _document.Changed -= OnChanged;
            _editor.TextArea.TextView.SizeChanged -= OnViewSizeChanged;
            if (_folding != null && ReferenceEquals(_folding.ExtraFolds, _hiddenFolds)) _folding.ExtraFolds = null;
            _hidden.Clear();
        }

        /// <summary>Kroki or its server changed: every picture is asked for again, passing failures included (R6).</summary>
        public void Refresh()
        {
            _generation++;
            _editor.TextArea.TextView.Redraw();
        }

        /// <summary>Typing paused: the pictures in view are drawn for their new text. The pause timer calls it; so do tests.</summary>
        internal void DrawDue()
        {
            _editing = false;
            RedrawPictureLines();
        }

        /// <summary>Logs once per board; a failure here never reaches the editor.</summary>
        internal void WarnOnce(string message)
        {
            if (_warned) return;
            _warned = true;
            try
            {
                _services.Warn(message);
            }
            catch (Exception)
            {
                // Logging is best effort.
            }
        }

        /// <summary>The diagram block <paramref name="closing"/> closes, or null.</summary>
        internal DiagramBlock? BlockClosedBy(DocumentLine closing)
        {
            if (!_attached || closing.IsDeleted) return null;
            int open = _cache.OpeningLineOf(_document, closing.LineNumber);
            return open == 0 ? null : Read(open, closing.LineNumber);
        }

        /// <summary>
        /// What goes under <paramref name="closing"/>: the picture, the error, or "Drawing…". Asks
        /// for a drawing when one is due: not while typing, and not again for a text whose drawing
        /// already ended (R6).
        /// </summary>
        internal UIElement PictureFor(DocumentLine closing, DiagramBlock block)
        {
            var state = _states.GetValue(closing, _ => new BlockState());
            var palette = _palette();
            string? server = _services.KrokiServer();
            DiagramResult? result;
            if (block.TooLarge)
            {
                result = state.Shown = DiagramResult.Failure(DiagramText.TooLarge, lasting: true);
            }
            else if (block.Kind.NeedsKroki && server == null)
            {
                result = state.Shown = DiagramResult.Failure(DiagramText.NeedsKroki(block.Kind), lasting: true);
            }
            else
            {
                var request = RequestFor(block, palette, server);
                string key = request.Key;
                if (_services.Renderer.TryGetCached(key, out var cached))
                {
                    state.PendingKey = null;
                    result = state.Shown = cached;
                }
                else
                {
                    bool settled = state.SettledKey == key && state.SettledGeneration == _generation;
                    if (!_editing && !settled && state.PendingKey != key) Request(closing, state, request, key);
                    result = state.Shown;   // the previous picture stays until the new one is ready
                }
            }
            return new DiagramPicture(ViewOf(closing, block, result, palette));
        }

        /// <summary>True while the code of the block <paramref name="closing"/> closes is folded away.</summary>
        internal bool IsCodeHidden(DocumentLine closing)
        {
            if (_folding?.Manager is not { } manager || BlockClosedBy(closing) is not { } block || InnerRange(block) is not { } range) return false;
            return manager.GetFoldingsAt(range.Start).Any(f => f.EndOffset == range.End && f.IsFolded);
        }

        /// <summary>
        /// Hide code / Show code: folds the block's lines between its fences, or unfolds them. The
        /// fold follows edits (an anchor on the opening fence line) and is not saved.
        /// </summary>
        internal void SetCodeHidden(DocumentLine closing, bool hide)
        {
            if (_folding?.Manager is not { } manager || BlockClosedBy(closing) is not { } block || InnerRange(block) is not { } range) return;

            var opening = _document.GetLineByNumber(block.OpenLine);
            _hidden.RemoveAll(a => a.IsDeleted || a.Line == opening);
            if (hide)
            {
                var anchor = _document.CreateAnchor(opening.Offset);
                anchor.MovementType = AnchorMovementType.AfterInsertion;
                _hidden.Add(anchor);
            }
            _folding.Update();
            foreach (var section in manager.GetFoldingsAt(range.Start))
                if (section.EndOffset == range.End) section.IsFolded = hide;
            _editor.TextArea.TextView.Redraw(closing, DispatcherPriority.Normal);
        }

        private DiagramBlock? Read(int open, int close) =>
            DiagramBlocks.Read(n => _document.GetText(_document.GetLineByNumber(n)), open, close);

        private static DiagramRequest RequestFor(DiagramBlock block, PadPalette palette, string? server) =>
            new(block.Kind, block.Source, palette.Name, DiagramRequest.Css(palette.Text), DiagramRequest.Css(palette.Background),
                block.Kind.NeedsKroki ? server : null);

        private void Request(DocumentLine closing, BlockState state, DiagramRequest request, string key)
        {
            state.PendingKey = key;
            Draws++;
            _ = AwaitDrawAsync(closing, state, request, key);
        }

        private async Task AwaitDrawAsync(DocumentLine closing, BlockState state, DiagramRequest request, string key)
        {
            int generation = _generation;
            DiagramResult result;
            try
            {
                result = await _services.Renderer.RenderAsync(request, closing);
            }
            catch (Exception ex)
            {
                WarnOnce(request.Kind.Name + " drawing failed (" + ex.GetType().Name + ")");
                result = DiagramResult.Failure(DiagramText.Failed, lasting: false);
            }

            if (result.IsReplaced || state.PendingKey != key) return;
            state.PendingKey = null;
            state.Shown = result;
            state.SettledKey = key;
            state.SettledGeneration = generation;
            if (_attached && !closing.IsDeleted && ReferenceEquals(_editor.Document, _document))
                _editor.TextArea.TextView.Redraw(closing, DispatcherPriority.Normal);
        }

        private DiagramView ViewOf(DocumentLine closing, DiagramBlock block, DiagramResult? result, PadPalette palette)
        {
            var view = _editor.TextArea.TextView;
            double width = ((IScrollInfo)view).ViewportWidth;
            if (!(width > 0)) width = view.ActualWidth;
            return new DiagramView
            {
                Result = result,
                Palette = palette,
                MaxWidth = Math.Max(MinPictureWidth, width - PictureMargin),
                CanHideCode = _folding != null && block.CloseLine - block.OpenLine > 1,
                CodeHidden = IsCodeHidden(closing),
                ToggleCode = () => SetCodeHidden(closing, !IsCodeHidden(closing)),
                CopyPicture = () => _ = ExportAsync(closing, DiagramExport.Copy),
                SavePng = () => _ = ExportAsync(closing, DiagramExport.Png),
                SaveSvg = () => _ = ExportAsync(closing, DiagramExport.Svg),
                OpenLink = _services.OpenLink,
            };
        }

        private void OnChanged(object? sender, DocumentChangeEventArgs e)
        {
            if (!_attached || !ReferenceEquals(sender, _document)) return;
            _editing = true;
            _pauseTimer.Stop();
            _pauseTimer.Start();
        }

        private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_attached || !e.WidthChanged) return;
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }

        /// <summary>Builds again the lines in view that hold a picture, so each fits the width and shows its latest drawing.</summary>
        private void RedrawPictureLines()
        {
            if (!_attached) return;
            var view = _editor.TextArea.TextView;
            if (!view.VisualLinesValid) return;
            foreach (var line in view.VisualLines.ToList())
                if (line.Elements.OfType<DiagramElement>().Any()) view.Redraw(line, DispatcherPriority.Normal);
        }

        /// <summary>The offsets Hide code folds: from the end of the opening fence line to the end of the block's last line.</summary>
        private (int Start, int End)? InnerRange(DiagramBlock block)
        {
            if (block.CloseLine - block.OpenLine < 2) return null;
            return (_document.GetLineByNumber(block.OpenLine).EndOffset, _document.GetLineByNumber(block.CloseLine - 1).EndOffset);
        }

        /// <summary>The folds of the blocks whose code is hidden, for the folding's recompute.</summary>
        private IEnumerable<NewFolding> HiddenFolds(TextDocument document)
        {
            var folds = new List<NewFolding>();
            if (!_attached || !ReferenceEquals(document, _document)) return folds;

            _hidden.RemoveAll(a => a.IsDeleted);
            foreach (var anchor in _hidden)
            {
                int open = anchor.Line.LineNumber;
                int close = _cache.ClosingLineOf(document, open);
                if (close == 0 || Read(open, close) is not { } block || InnerRange(block) is not { } range) continue;
                folds.Add(new NewFolding(range.Start, range.End) { DefaultClosed = true });
            }
            return folds;
        }

        private async Task ExportAsync(DocumentLine closing, DiagramExport what)
        {
            try
            {
                if (BlockClosedBy(closing) is not { TooLarge: false } block) return;
                string? server = _services.KrokiServer();
                if (block.Kind.NeedsKroki && server == null) return;

                // Exports are the light drawing (R1): a dark picture is unreadable on white paper.
                var request = RequestFor(block, PadPalette.Light, server);
                var result = _services.Renderer.TryGetCached(request.Key, out var cached)
                    ? cached
                    : await _services.Renderer.RenderAsync(request, new object());
                if (!result.IsPicture)
                {
                    _services.ShowStatus("The picture could not be made.");
                    return;
                }

                switch (what)
                {
                    case DiagramExport.Copy:
                        if (!_services.TrySetClipboardImage(result.Png!)) _services.ShowStatus("Clipboard busy, try again");
                        break;
                    case DiagramExport.Png:
                        Save(result.Png!, "diagram.png", "PNG picture (*.png)|*.png");
                        break;
                    default:
                        Save(Encoding.UTF8.GetBytes(result.Svg!), "diagram.svg", "SVG picture (*.svg)|*.svg");
                        break;
                }
            }
            catch (Exception ex)
            {
                _services.Warn("Exporting a diagram failed (" + ex.GetType().Name + ")");
                _services.ShowStatus("The picture could not be made.");
            }
        }

        private void Save(byte[] bytes, string name, string filter)
        {
            string? path = _services.AskSavePath(name, filter);
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                File.WriteAllBytes(path, bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                _services.Warn("Saving a diagram failed (" + ex.GetType().Name + ")");
                _services.ShowStatus("The picture could not be saved.");
            }
        }

        /// <summary>One block's pictures: the one shown, the drawing asked for, and the last text whose drawing ended.</summary>
        private sealed class BlockState
        {
            public DiagramResult? Shown;
            public string? PendingKey;
            public string? SettledKey;
            public int SettledGeneration;
        }
    }
}
```

`Pad/DiagramGenerator.cs`:

```csharp
using System;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Puts a <see cref="DiagramElement"/> at the end of each closing fence line of a diagram block
    /// (spec section 4). A visual line can hold several document lines (a fold): the scan runs to
    /// its last one. A failure is logged once by the board and that line simply gets no picture;
    /// the rest of the Markdown formatting is untouched.
    /// </summary>
    internal sealed class DiagramGenerator : VisualLineElementGenerator
    {
        private readonly MarkdownDocumentCache _cache;
        private readonly DiagramBoard _board;

        public DiagramGenerator(MarkdownDocumentCache cache, DiagramBoard board)
        {
            _cache = cache;
            _board = board;
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            try
            {
                var document = CurrentContext.Document;
                if (!ReferenceEquals(document, _board.Document)) return -1;
                int last = CurrentContext.VisualLine.LastDocumentLine.LineNumber;
                for (var line = document.GetLineByOffset(startOffset); line != null && line.LineNumber <= last; line = line.NextLine)
                {
                    if (line.EndOffset < startOffset) continue;
                    if (_cache.KindOf(document, line.LineNumber) != MdFence.Delimiter) continue;
                    int open = _cache.OpeningLineOf(document, line.LineNumber);
                    if (open > 0 && DiagramKinds.FromFence(document.GetText(document.GetLineByNumber(open))) != null) return line.EndOffset;
                }
                return -1;
            }
            catch (Exception ex)
            {
                _board.WarnOnce("Diagram pictures failed (" + ex.GetType().Name + ")");
                return -1;
            }
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            try
            {
                var line = CurrentContext.Document.GetLineByOffset(offset);
                if (line.EndOffset != offset || _board.BlockClosedBy(line) is not { } block) return null;
                return new DiagramElement(_board.PictureFor(line, block));
            }
            catch (Exception ex)
            {
                _board.WarnOnce("Diagram pictures failed (" + ex.GetType().Name + ")");
                return null;
            }
        }
    }
}
```

- [ ] **Step 6: Install the pictures with Markdown in `Pad/EditorLanguage.cs`**

1. Add the fields after `_bullets`:

```csharp
        private DiagramBoard? _diagramBoard;
        private DiagramGenerator? _diagramGenerator;
```

2. Add the properties after `HasMarkdown`:

```csharp
        /// <summary>What diagram pictures need (spec Part 3); set by the window. Null: no pictures (the history preview).</summary>
        internal DiagramServices? Diagrams { get; set; }

        /// <summary>The pictures of the shown Markdown document, or null.</summary>
        internal DiagramBoard? DiagramBoard => _diagramBoard;
```

3. In `Apply`, in the Markdown branch, after `view.ElementGenerators.Add(_bullets);`, add `InstallDiagrams();`.

4. In `Clear`, as the first statement inside `if (_markdown != null)`, add `RemoveDiagrams();`.

5. Add these methods after `Redraw`:

```csharp
        /// <summary>
        /// Draw diagrams, Kroki or the Kroki server changed in Settings: puts the pictures in or
        /// takes them out, or asks every picture again, without touching the rest of the formatting.
        /// </summary>
        internal void RefreshDiagrams()
        {
            if (_markdown == null) return;
            bool wanted = Diagrams != null && Diagrams.Enabled();
            if (!wanted)
            {
                if (_diagramBoard == null) return;
                RemoveDiagrams();
                _folding?.Update();
            }
            else if (_diagramBoard == null)
            {
                InstallDiagrams();
                _folding?.Update();
            }
            else
            {
                _diagramBoard.Refresh();
            }
            Redraw();
        }

        private void InstallDiagrams()
        {
            if (Diagrams is not { } services || !services.Enabled() || _markdownCache == null) return;
            _diagramBoard = new DiagramBoard(_editor, _markdownCache, _folding, services, _palette);
            _diagramGenerator = new DiagramGenerator(_markdownCache, _diagramBoard);
            _editor.TextArea.TextView.ElementGenerators.Add(_diagramGenerator);
        }

        private void RemoveDiagrams()
        {
            if (_diagramBoard == null) return;
            _editor.TextArea.TextView.ElementGenerators.Remove(_diagramGenerator!);
            _diagramBoard.Detach();
            _diagramBoard = null;
            _diagramGenerator = null;
        }
```

Also extend the class comment's first sentence: "…or Markdown formatting (colorizer, background, bullets, diagram pictures), and folding."

- [ ] **Step 7: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~DiagramBoardTests|FullyQualifiedName~MarkdownRenderingTests|FullyQualifiedName~PadFoldingTests|FullyQualifiedName~PadLanguageWindowTests" 2>&1 | tail -5`
Expected: all pass. Then the full suite (Global Constraints command): all pass. Check escapes in the new `.cs` files (string literals only).

- [ ] **Step 8: Commit**

```bash
git add Pad/MarkdownDocumentCache.cs Pad/FoldingController.cs Pad/DiagramServices.cs Pad/DiagramBoard.cs Pad/DiagramGenerator.cs Pad/EditorLanguage.cs tests/Kil0bitSystemMonitor.Tests/DiagramFakes.cs tests/Kil0bitSystemMonitor.Tests/DiagramBoardTests.cs
git commit -m "feat(pad): diagram pictures under each block in Markdown tabs - drawn 600 ms after typing stops, old picture kept, Hide code, exports in the light theme" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 7: The window and the app — shared renderer, clipboard, Save dialog, settings changes

**Files:**
- Create: `Pad/MicaPadWindow.Diagrams.cs`
- Modify: `Pad/MicaPadWindow.xaml.cs` (constructor call; `OnConfigChanged`)
- Modify: `App.xaml.cs` (`s_diagrams`; created in `OpenPad`, disposed in `OnExit`)
- Test: `tests/Kil0bitSystemMonitor.Tests/DiagramWindowTests.cs`

**Interfaces:**
- Consumes: `DiagramServices`, `EditorLanguage.Diagrams`, `EditorLanguage.DiagramBoard`, `EditorLanguage.RefreshDiagrams()`, `DiagramBoard.DrawDue()` (Task 6); `DiagramRenderer`, `KrokiClient` (Tasks 2, 4); `DiagramPage.CreateAsync`, `DiagramPage.DefaultUserDataFolder`, `DiagramPage.ScriptsFolder` (Task 3); `AppConfig.PadDiagrams`, `PadKroki`, `PadKrokiServer` (Task 4); existing window members `_language`, `_config`, `OnLinkRequested(Uri)`, `ShowStatus(string)`, `Warn` (an `Action<string>` property).
- Produces: `internal static IDiagramRenderer? MicaPadWindow.DiagramRenderer` (set by the app; null means no pictures), `internal Func<byte[], bool> MicaPadWindow.TrySetClipboardImage`, `internal Func<string, string, string?> MicaPadWindow.AskSavePath`.

- [ ] **Step 1: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/DiagramWindowTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Diagram pictures in the real MicaPad window, built on the UI thread and never shown, over a fake renderer.</summary>
    public class DiagramWindowTests
    {
        private static void WithDiagramWindow(Action<MicaPadWindow, FakeRenderer, AppConfig> test) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var config = new AppConfig();
            var renderer = new FakeRenderer();
            var before = MicaPadWindow.DiagramRenderer;
            MicaPadWindow.DiagramRenderer = renderer;
            MicaPadWindow? window = null;
            try
            {
                window = new MicaPadWindow(env.Workspace, config);
                window.LoadSession();
                test(window, renderer, config);
            }
            finally
            {
                window?.CloseForExit();
                MicaPadWindow.DiagramRenderer = before;
            }
        });

        /// <summary>Lays the editor out and returns the picture under line <paramref name="line"/>, or null.</summary>
        private static DiagramPicture? PictureUnder(MicaPadWindow window, int line)
        {
            PadLanguageWindowTests.Render(window);
            return window.Editor.TextArea.TextView.GetVisualLine(line)?.Elements.OfType<DiagramElement>().SingleOrDefault()?.Picture as DiagramPicture;
        }

        [Fact]
        public void A_note_with_a_mermaid_block_shows_its_picture() => WithDiagramWindow((window, renderer, config) =>
        {
            window.Editor.Document.Text = "# Plan\n```mermaid\nflowchart LR\n  a --> b\n```";
            Assert.True(PictureUnder(window, 5)!.IsDrawing);

            window.LanguageView.DiagramBoard!.DrawDue();   // as if typing had paused
            Assert.NotNull(PictureUnder(window, 5));
            var call = Assert.Single(renderer.Calls);
            Assert.Equal(PadThemes.Dark, call.Request.Theme);
            Assert.Null(call.Request.KrokiServer);

            renderer.Finish(0, DiagramFakes.Picture(120, 60));
            PadLanguageWindowTests.Pump();
            Assert.Equal(120, PictureUnder(window, 5)!.Image!.Width);
        });

        [Fact]
        public void Settings_turn_kroki_on_and_pictures_off() => WithDiagramWindow((window, renderer, config) =>
        {
            window.Editor.Document.Text = "```puml\n@startuml\na -> b\n@enduml\n```";
            Assert.Equal("PlantUML needs Kroki \u2014 turn it on in Settings \u2192 MicaPad.", PictureUnder(window, 5)!.ErrorText!.Text);

            config.PadKroki = true;
            config.PadKrokiServer = "http://localhost:8000";
            window.LanguageView.DiagramBoard!.DrawDue();
            PictureUnder(window, 5);
            Assert.Equal("http://localhost:8000", Assert.Single(renderer.Calls).Request.KrokiServer);

            config.PadDiagrams = false;
            Assert.Null(PictureUnder(window, 5));
            Assert.Null(window.LanguageView.DiagramBoard);

            config.PadDiagrams = true;
            Assert.NotNull(PictureUnder(window, 5));
        });

        [Fact]
        public void Copy_picture_puts_the_light_png_on_the_clipboard() => WithDiagramWindow((window, renderer, config) =>
        {
            var copied = new List<byte[]>();
            window.TrySetClipboardImage = png =>
            {
                copied.Add(png);
                return true;
            };
            window.Editor.Document.Text = "```dot\ndigraph { a -> b }\n```";
            PictureUnder(window, 3);
            window.LanguageView.DiagramBoard!.DrawDue();
            PictureUnder(window, 3);
            renderer.Finish(0, DiagramFakes.Picture());
            PadLanguageWindowTests.Pump();

            PictureUnder(window, 3)!.View.CopyPicture!();
            Assert.Equal(PadThemes.Light, renderer.Calls[1].Request.Theme);
            renderer.Finish(1, DiagramFakes.Picture());
            PadLanguageWindowTests.Pump();

            Assert.Same(DiagramFakes.Png, Assert.Single(copied));
        });

        [Fact]
        public void Without_a_renderer_no_pictures_are_installed() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "```mermaid\nflowchart LR\n  a --> b\n```";
            PadLanguageWindowTests.Render(window);

            Assert.Null(window.LanguageView.DiagramBoard);
            Assert.Empty(window.Editor.TextArea.TextView.ElementGenerators.OfType<DiagramGenerator>());
        });

        [Fact]
        public void The_history_preview_never_shows_pictures() => WithDiagramWindow((window, renderer, config) =>
        {
            Assert.NotNull(window.LanguageView.DiagramBoard);
            Assert.Empty(window.PreviewEditor.TextArea.TextView.ElementGenerators.OfType<DiagramGenerator>());
        });
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~DiagramWindowTests" 2>&1 | tail -5`
Expected: build error, `MicaPadWindow.DiagramRenderer` does not exist.

- [ ] **Step 3: Implement**

`Pad/MicaPadWindow.Diagrams.cs`:

```csharp
using System;
using System.IO;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// MicaPad's side of diagram pictures (spec Part 3): the renderer the app shares between
    /// windows, the editor's <see cref="DiagramServices"/>, the clipboard and the Save dialog.
    /// Settings changes reach the editor through <see cref="OnConfigChanged"/>.
    /// </summary>
    public partial class MicaPadWindow
    {
        /// <summary>Draws the diagrams of every MicaPad window; set by the app before the first window opens. Null (most tests) means no pictures.</summary>
        internal static IDiagramRenderer? DiagramRenderer { get; set; }

        /// <summary>Puts a PNG on the clipboard; false when it stays busy. Tests replace it.</summary>
        internal Func<byte[], bool> TrySetClipboardImage { get; set; } = TrySetClipboardPng;

        /// <summary>Asks where to save a picture (default file name, filter); null when cancelled. Tests replace it.</summary>
        internal Func<string, string, string?> AskSavePath { get; set; } = (_, _) => null;

        /// <summary>Gives the note editor its pictures; the history preview gets none.</summary>
        private void ConfigureDiagrams()
        {
            AskSavePath = ShowSaveDialog;
            if (DiagramRenderer is not { } renderer) return;
            _language.Diagrams = new DiagramServices
            {
                Renderer = renderer,
                Enabled = () => _config.PadDiagrams,
                KrokiServer = () => _config.PadKroki ? _config.PadKrokiServer : null,
                OpenLink = OnLinkRequested,
                TrySetClipboardImage = png => TrySetClipboardImage(png),
                AskSavePath = (name, filter) => AskSavePath(name, filter),
                ShowStatus = ShowStatus,
                Warn = message => Warn(message),
            };
        }

        /// <summary>Settings → MicaPad changed Draw diagrams, Kroki or the Kroki server.</summary>
        private void ApplyDiagramSettings() => _language.RefreshDiagrams();

        private string? ShowSaveDialog(string name, string filter)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = name,
                Filter = filter,
                AddExtension = true,
                DefaultExt = Path.GetExtension(name),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }

        /// <summary>
        /// The PNG as "PNG" (with its transparency; Word and browsers take it) and as a bitmap on
        /// white for everything else. The WinForms call tries three times, 100 ms apart, as Copy as
        /// RTF does.
        /// </summary>
        private static bool TrySetClipboardPng(byte[] png)
        {
            try
            {
                using var stream = new MemoryStream(png);
                using var source = new System.Drawing.Bitmap(stream);
                using var flat = new System.Drawing.Bitmap(source.Width, source.Height);
                using (var graphics = System.Drawing.Graphics.FromImage(flat))
                {
                    graphics.Clear(System.Drawing.Color.White);
                    graphics.DrawImage(source, 0, 0, source.Width, source.Height);
                }
                var data = new System.Windows.Forms.DataObject();
                data.SetData("PNG", false, new MemoryStream(png));
                data.SetData(System.Windows.Forms.DataFormats.Bitmap, true, flat);
                System.Windows.Forms.Clipboard.SetDataObject(data, copy: true, retryTimes: 3, retryDelay: 100);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return false;
            }
        }
    }
}
```

In `Pad/MicaPadWindow.xaml.cs`:

1. In the constructor, right after `_previewLanguage = new EditorLanguage(PreviewEditor, () => _palette, folds: false);`, add `ConfigureDiagrams();`.
2. In `OnConfigChanged`, after the `PadMarkdown` branch, add:

```csharp
            else if (e.PropertyName is nameof(AppConfig.PadDiagrams) or nameof(AppConfig.PadKroki) or nameof(AppConfig.PadKrokiServer))
            {
                if (Dispatcher.CheckAccess()) ApplyDiagramSettings();
                else Dispatcher.BeginInvoke(new Action(ApplyDiagramSettings));
            }
```

In `App.xaml.cs`:

1. Next to `s_vaultSession`, add:

```csharp
        /// <summary>Draws MicaPad's diagrams for every window (one hidden WebView2); created with the first MicaPad window, disposed at exit.</summary>
        private static Kil0bitSystemMonitor.Services.Pad.DiagramRenderer? s_diagrams;
```

2. In `OpenPad`, inside the `try`, right before `Kil0bitSystemMonitor.Pad.MicaPadWindow.Open(s_pad, config, ...)`, add:

```csharp
                if (s_diagrams == null)
                {
                    s_diagrams = new Kil0bitSystemMonitor.Services.Pad.DiagramRenderer(
                        () => Kil0bitSystemMonitor.Pad.DiagramPage.CreateAsync(
                            Kil0bitSystemMonitor.Pad.DiagramPage.DefaultUserDataFolder,
                            Kil0bitSystemMonitor.Pad.DiagramPage.ScriptsFolder),
                        warn: message => Kil0bitSystemMonitor.Services.DiagnosticsLog.Warn("pad", message),
                        kroki: new Kil0bitSystemMonitor.Services.Pad.KrokiClient());
                    Kil0bitSystemMonitor.Pad.MicaPadWindow.DiagramRenderer = s_diagrams;
                }
```

3. In `OnExit`, right after `FlushPad();`, add `s_diagrams?.Dispose();`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~DiagramWindowTests|FullyQualifiedName~PadWindowTests|FullyQualifiedName~PadLanguageWindowTests" 2>&1 | tail -5`
Expected: all pass. Then the full suite: all pass.

- [ ] **Step 5: Commit**

```bash
git add Pad/MicaPadWindow.Diagrams.cs Pad/MicaPadWindow.xaml.cs App.xaml.cs tests/Kil0bitSystemMonitor.Tests/DiagramWindowTests.cs
git commit -m "feat(pad): MicaPad windows draw diagrams through one shared renderer; clipboard PNG, Save dialog, Settings changes apply at once" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 8: Settings → MicaPad and the guide

**Files:**
- Modify: `SettingsWindow.xaml` (two cards after "Markdown formatting")
- Modify: `SettingsWindow.xaml.cs` (`LoadPadSettings`, `OnPadToggled`, `OnPadKrokiServerChanged`)
- Modify: `GUIDE.md` (`### Diagrams` after `### Markdown`)
- Test: `tests/Kil0bitSystemMonitor.Tests/DiagramWindowTests.cs` (two more facts)

**Interfaces:**
- Consumes: `AppConfig.PadDiagrams`, `PadKroki`, `PadKrokiServer`, `KrokiClient.TryParseServer` (Task 4); `DiagramKinds.Words` (Task 1); `PadWindowTests.RepoRoot()` (existing test helper).
- Produces: nothing later tasks use.

- [ ] **Step 1: Write the failing tests**

Add to `DiagramWindowTests` (add `using System.IO;`):

```csharp
        [Fact]
        public void Settings_has_the_diagram_and_kroki_cards()
        {
            string xaml = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "SettingsWindow.xaml"));

            foreach (string name in new[] { "PadDiagramsToggle", "PadKrokiToggle", "PadKrokiServerBox", "PadKrokiHint" })
                Assert.Contains("x:Name=\"" + name + "\"", xaml);
            Assert.Contains("Text=\"Draw diagrams\"", xaml);
            Assert.Contains("Text=\"Draw other types with Kroki\"", xaml);
            Assert.Contains("Sends the diagram's text (only that block) to this server. Use your own Kroki server for private notes.", xaml);
        }

        [Fact]
        public void The_guide_lists_every_fence_word_and_what_a_picture_offers()
        {
            string guide = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "GUIDE.md"));
            int at = guide.IndexOf("### Diagrams", StringComparison.Ordinal);
            Assert.True(at > guide.IndexOf("### Markdown", StringComparison.Ordinal));
            int next = guide.IndexOf("\n### ", at + 1, StringComparison.Ordinal);
            string section = guide.Substring(at, (next < 0 ? guide.Length : next) - at);

            foreach (string word in DiagramKinds.Words) Assert.Contains("`" + word + "`", section);
            foreach (string phrase in new[] { "Hide code", "Show code", "Copy picture", "Save as PNG", "Save as SVG",
                                              "Draw diagrams", "Draw other types with Kroki", "http://localhost:8000", "WebView2", "light" })
                Assert.Contains(phrase, section);
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~DiagramWindowTests" 2>&1 | tail -5`
Expected: the two new facts fail (no cards, no section).

- [ ] **Step 3: Add the cards**

In `SettingsWindow.xaml`, right after the `Border` holding `PadMarkdownToggle` (the "Markdown formatting" card), add:

```xml
                        <Border Background="{DynamicResource SystemControlBackgroundChromeMediumLowBrush}" BorderBrush="{DynamicResource SystemControlElevationBorderBrush}" BorderThickness="1" CornerRadius="8" Margin="0,0,0,12" Padding="20,16">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto" />
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="Auto" />
                                </Grid.ColumnDefinitions>
                                <ui:FontIcon Glyph="&#xE8B9;" FontSize="20" Foreground="{DynamicResource SystemAccentColorBrush}" Margin="0,0,20,0" VerticalAlignment="Center"/>
                                <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,16,0">
                                    <TextBlock Text="Draw diagrams" FontWeight="SemiBold" FontSize="15"/>
                                    <TextBlock Text="A picture under each mermaid, dot (Graphviz) and markmap block in Markdown notes, drawn on this PC. Right-click a picture to copy or save it." Opacity="0.6" FontSize="12.5" TextWrapping="Wrap"/>
                                </StackPanel>
                                <ui:ToggleSwitch Grid.Column="2" x:Name="PadDiagramsToggle" Toggled="OnPadToggled" VerticalAlignment="Center"/>
                            </Grid>
                        </Border>
                        <Border Background="{DynamicResource SystemControlBackgroundChromeMediumLowBrush}" BorderBrush="{DynamicResource SystemControlElevationBorderBrush}" BorderThickness="1" CornerRadius="8" Margin="0,0,0,12" Padding="20,16">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto" />
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="Auto" />
                                </Grid.ColumnDefinitions>
                                <ui:FontIcon Glyph="&#xE774;" FontSize="20" Foreground="{DynamicResource SystemAccentColorBrush}" Margin="0,0,20,0" VerticalAlignment="Top"/>
                                <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,16,0">
                                    <TextBlock Text="Draw other types with Kroki" FontWeight="SemiBold" FontSize="15"/>
                                    <TextBlock Text="PlantUML, D2, BPMN and more. Sends the diagram's text (only that block) to this server. Use your own Kroki server for private notes." Opacity="0.6" FontSize="12.5" TextWrapping="Wrap"/>
                                    <TextBox x:Name="PadKrokiServerBox" Width="260" HorizontalAlignment="Left" Margin="0,8,0,0" LostFocus="OnPadKrokiServerChanged"/>
                                    <TextBlock x:Name="PadKrokiHint" Opacity="0.6" FontSize="12.5" TextWrapping="Wrap" Margin="0,4,0,0"/>
                                </StackPanel>
                                <ui:ToggleSwitch Grid.Column="2" x:Name="PadKrokiToggle" Toggled="OnPadToggled" VerticalAlignment="Top"/>
                            </Grid>
                        </Border>
```

In `SettingsWindow.xaml.cs`:

1. Next to `PadHotkeyHelp`, add:

```csharp
        private const string PadKrokiHelp = "The Kroki server, like https://kroki.io or http://localhost:8000.";
        private const string PadKrokiInvalid = "Not a server address. Use http:// or https:// and a host name, like https://kroki.io.";
```

2. In `LoadPadSettings`, after `PadAutoCloseToggle.IsOn = cfg.PadAutoClose;`, add:

```csharp
                PadDiagramsToggle.IsOn = cfg.PadDiagrams;
                PadKrokiToggle.IsOn = cfg.PadKroki;
                PadKrokiServerBox.Text = cfg.PadKrokiServer;
                PadKrokiHint.Text = PadKrokiHelp;
```

3. In `OnPadToggled`, after `cfg.PadAutoClose = PadAutoCloseToggle.IsOn;`, add:

```csharp
            cfg.PadDiagrams = PadDiagramsToggle.IsOn;
            cfg.PadKroki = PadKrokiToggle.IsOn;
```

4. After `OnPadHotkeyChanged`, add:

```csharp
        /// <summary>The Kroki server box lost focus: an http or https address is kept (without its trailing slash); anything else is refused.</summary>
        private void OnPadKrokiServerChanged(object sender, RoutedEventArgs e)
        {
            if (_loadingPad) return;
            if (!Kil0bitSystemMonitor.Services.Pad.KrokiClient.TryParseServer(PadKrokiServerBox.Text, out var server))
            {
                PadKrokiHint.Text = PadKrokiInvalid;
                return;
            }
            PadKrokiServerBox.Text = server;
            PadKrokiHint.Text = PadKrokiHelp;
            if (_config.Config.PadKrokiServer == server) return;
            _config.Config.PadKrokiServer = server;
            _config.SaveConfig();
        }
```

- [ ] **Step 4: Write the guide section**

In `GUIDE.md`, right before `### Colors for code, logs and settings files`, add:

````markdown
### Diagrams

A fenced block whose first word names a diagram type gets a picture right under it in Markdown
notes. The code stays above the picture and stays editable; the picture follows a moment after
you stop typing.

```mermaid
mindmap
  root((MicaPad))
    Notes
    Diagrams
```

| Drawn on this PC | Fence words |
|---|---|
| Mermaid — flowchart, sequence, class, state, ER, Gantt, pie, mindmap, timeline and more | `mermaid`, `mmd` |
| Graphviz | `dot`, `graphviz`, `gv` |
| Markmap — a mindmap from a Markdown outline | `markmap` |

Every other type is drawn by a **Kroki** server, which stays off until you turn on
**Settings → MicaPad → Draw other types with Kroki**: `plantuml`, `puml`, `c4plantuml`, `d2`,
`bpmn`, `excalidraw`, `vega`, `vegalite`, `vega-lite`, `wavedrom`, `ditaa`, `structurizr`,
`nomnoml`, `pikchr`, `svgbob`, `dbml`, `erd`, `bytefield`, `blockdiag`, `seqdiag`, `actdiag`,
`nwdiag`, `packetdiag`, `rackdiag`, `tikz`, `umlet`, `symbolator`, `wireviz`. Only that block's
text is sent, to `https://kroki.io` unless you enter your own server. For private notes run Kroki
yourself (`docker run -d -p 8000:8000 yuzutech/kroki`) and enter `http://localhost:8000`. Kroki
pictures sit on a white card in both themes.

Hover a picture for **Hide code** (folds the block's lines away; **Show code** brings them back).
Right-click it for **Copy picture**, **Save as PNG…** and **Save as SVG…** — these always give the
light version, which reads well on white paper. A mistake in a diagram shows the engine's message
in a red box instead of the picture. **Settings → MicaPad → Draw diagrams** turns pictures off.
Pictures need the Microsoft Edge WebView2 Runtime, which Windows 11 includes.
````

- [ ] **Step 5: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~DiagramWindowTests|FullyQualifiedName~VaultStatusTextTests" 2>&1 | tail -5`
Expected: all pass. Then the full suite: all pass.

- [ ] **Step 6: Commit**

```bash
git add SettingsWindow.xaml SettingsWindow.xaml.cs GUIDE.md tests/Kil0bitSystemMonitor.Tests/DiagramWindowTests.cs
git commit -m "feat(settings): MicaPad Draw diagrams and Kroki cards; guide: diagrams, fence words, Kroki and private servers" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

## After the last task (controller)

1. Final whole-branch review, one fix wave, rulings doc `docs/superpowers/plans/2026-10-01-micapad-diagrams-rulings.md`.
2. Deploy for e2e (standing rule): kill MicaStats, `dotnet build -c Release` into `bin\Release\net8.0-windows` (check `Diagrams\` and `runtimes\win-x64\native\WebView2Loader.dll` are there), relaunch. The vault build's encrypted store stays readable (this branch descends from 0f3536f).
3. Manual checks for the owner (spec "Testing"): a note with a Mermaid mindmap, a flowchart, a dot graph and a Markmap; dark/light switch; Hide code; Copy picture into Word; Save as SVG opened in a browser; Kroki on with kroki.io for a PlantUML mindmap and a D2 diagram; Kroki off message; typing speed in a note with many diagrams.
