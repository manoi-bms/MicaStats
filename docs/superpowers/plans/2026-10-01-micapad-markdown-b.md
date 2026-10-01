# MicaPad Markdown like Wiki.js — Plan B (pictures, settings, guide) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Draw math blocks (MathJax, offline) and the Wiki.js ```` ```kroki ```` form through the diagram engine, show image previews under the lines that name them, and finish the feature with two Settings cards and a rewritten Markdown section in GUIDE.md.

**Architecture:** Math is a new page kind (`math`) in the hidden diagram page, drawn by the vendored MathJax 3.2.2; a `$$` line and the words `math`/`latex`/`tex` name a new `DiagramEngine.Math` kind in `DiagramKinds.FromFence`, so the existing generator, board, cache, Hide code and exports draw it unchanged. The kroki form is read by `DiagramBlocks.Read`. Image previews: `ImageSources` (pure) finds `![…](…)`, resolves the source and loads its bytes (file, data URI, or web while allowed); `ImageBoard` (per editor document) turns them into `DiagramResult`s — raster images decoded by WPF, SVG images drawn by the diagram renderer as a new `DiagramEngine.Svg` kind — and `ImageGenerator` puts a row of `DiagramPicture`s (an `ImageRow`) in a `DiagramElement` at the end of the line. The window wires the app-wide `ImageSources`, the tab's file folder and the new `PadWebImages` setting.

**Tech Stack:** .NET 8 WPF; AvalonEdit 6.3.1.120 (`VisualLineElementGenerator`); Microsoft.Web.WebView2 1.0.4258.31 (the existing hidden page); MathJax 3.2.2 `tex-svg-full.js` (Apache-2.0); WPF imaging (`BitmapDecoder`, `BitmapImage`); `HttpClient`; xUnit.

**Spec:** `docs/superpowers/specs/2026-10-01-micapad-markdown-wikijs-design.md` (parts 6–7, with "Architecture", "Error handling" and "Testing"). Plan A (parts 1–5): `docs/superpowers/plans/2026-10-01-micapad-markdown-a.md`.

## Global Constraints

- Repo `C:\AIProject\kil0bit-system-monitor`, branch `feat/micapad-markdown` (Plan A's tasks are on it). Commit on it; never push, merge or tag. Another agent may commit on this branch between tasks: `git add` only the exact paths a task lists, never `git add -A` or `.`, and never touch, revert or commit a file a task does not list.
- Build and test only with `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe"`. Full suite: `timeout 500 env DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests 2>&1 | tail -5`. Known flaky: `McpPipeTests.At_most_four_calls_run_at_once_and_the_rest_wait_their_turn` — re-run once if it alone fails.
- Never build or publish into `bin\Release` (the controller deploys). Never launch MicaStats. Tests use temp folders only, never `%APPDATA%`; no network — downloads go through a fake `HttpMessageHandler`; UI tests only on the shared `UiThread.Run`, never shown. Page tests use the real WebView2 as `DiagramPageTests` does (they pass with a note where the runtime is missing).
- The only network use in this plan: Task 1's two downloads from `cdn.jsdelivr.net`, each checked against its SHA-256 before it is used.
- New code under `Services/` holds no WPF types. Files use LF line endings; keep them.
- The text is never changed by any of this: math and images are pictures under their lines, standing for no text. The history preview gets no pictures or previews; non-Markdown tabs get none.
- Do not depend on Plan A internals that may still change: `MarkdownColorizer`, `FenceHighlighter`, `Emoji`, `EmojiGenerator`. Use only `MarkdownDocumentCache` (`FactsOf`, `KindOf`, `OpeningLineOf`, `ClosingLineOf`), `MdLineFacts`, `FenceTracker` and the diagram classes.
- Exact values (verbatim from the spec): MathJax **3.2.2** `tex-svg-full.js` (Apache-2.0) in `Diagrams\`, SHA-256 `a4354ff94fd868aea0cc6eaaa79a57fda0588646fc46ee3700a349ee0a11cbe6`; its menus, accessibility extras and any network loading switched off; page kind `math` returns an SVG of glyph outlines and its PNG in the theme's text color; math fence words `math`, `latex`, `tex`, and `$$` blocks; a MathJax error shows its message ("Missing close brace"). The kroki form: a fence whose word is `kroki` takes the type from its first inside line (lower case); known types use their name ("PlantUML"). Images: `![alt](source "title" =WxH)`, `=200x`, `=x120`, `=200x120` in device-independent pixels, natural size fitted to the text width (never enlarged); `AppConfig.PadWebImages` default **false**; web: GET, 10 s, 10 MB at most; files over 20 MB are not read; PNG, JPEG, GIF (first frame), BMP, TIFF, ICO, WebP where Windows has the codec, and SVG; messages "Image not found: {source}", "Web images are off — turn them on in Settings → MicaPad.", "The image could not be read.", "The image could not be downloaded ({host}).", "A relative path needs a saved file."; previews have no right-click menu; the same 600 ms pause, 64-entry memory-only cache and fit-to-width as diagrams. Settings cards: **Reading font** — "Prose in Markdown notes uses Segoe UI; code and tables stay in the editor font."; **Load images from the web** — "Images with an http or https address are downloaded when the note is shown. Off: only images on this PC are shown."
- Logging (spec "Error handling"): a failure is caught and logged once under `pad` naming the exception type only — never note text, an image path or a URL.
- C# string escapes: write non-ASCII as `\uXXXX` escapes (and a lone char as `(char)0xFEFF`). The Write/Edit tools can decode them into raw characters; after writing a `.cs` file run `LC_ALL=C grep -n '[^[:print:][:space:]]' <file>` and turn any raw non-ASCII character inside a string literal back into its escape (PowerShell `[IO.File]::ReadAllText` / `.Replace` / `[IO.File]::WriteAllText(path, text, [Text.UTF8Encoding]::new($false))`). User-visible "—" and "→" are `\u2014` and `\u2192`, "…" is `\u2026`.
- `UseWindowsForms` is on: `Brush`, `Brushes`, `Color`, `FontFamily`, `Image`, `Orientation`, `Pen`, `Point`, `Size` and others are ambiguous in WPF files and tests — add `using X = System.Windows...;` aliases as existing files do.
- Commits: `git add` exact paths, `git commit -m '...' -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"` (single quotes around a subject holding `$`), never amend.
- Never weaken an existing assertion. Where a count grows because this plan adds words (`DiagramKindsTests`: 34 → 38 words), change only that number and the test's name.
- Do not dispatch subagents.

## Rulings on the spec

- **R6 — one switch for pictures.** Math pictures and image previews follow **Draw diagrams** (`PadDiagrams`): they are the same machinery (a picture under a line, the pause, the cache) and the owner gets one place to turn pictures off. The card's text now says so. Cost if wrong: a third toggle card and a second `Enabled` source.
- **R7 — `$$` blocks reach the diagram engine through `DiagramKinds.FromFence`.** A line that is only `$$` names the Math kind just as a fence word does, so `DiagramGenerator`, `DiagramBoard`, the cache, Hide code and the light exports work unchanged; Plan A's structure scan already pairs `$$` lines. Cost if wrong: a few lines in `FromFence`.
- **R8 — the kroki form.** The first inside line is trimmed and compared without case with MicaPad's words: a Kroki word gives that kind ("PlantUML"); `mermaid`/`mmd`, `dot`/`graphviz`/`gv` and `markmap` are drawn on this PC by the built-in engine (nothing leaves the PC and Kroki is not needed); `math` and `kroki` are no types. Any other type must be lower-case letters and digits (Kroki's own names are) and is sent as is, named by itself. Anything else, or a block holding only its type line, is plain code. Cost if wrong: a Wiki.js page that relied on Kroki's Mermaid version draws with MicaPad's.
- **R9 — where previews sit.** One row per line: an `ImageRow` (a `WrapPanel` as wide as the text area less the margin diagrams use) inside the diagram picture element at the line's end, so the caret, copying and the document are untouched. Pictures sit side by side with an 8 px gap and wrap when the next does not fit. Only a line that starts its visual line gets a row (lines folded away get none); lines inside fences or front matter get none; table lines do (the spec says "anywhere on a line"). Cost if wrong: layout only.
- **R10 — sizes.** An image's natural size is one device-independent pixel per image pixel (the DPI in its header is ignored, as browsers do). `=200x` and `=x120` keep the proportions; `=200x120` is drawn at exactly that size, as a browser does. Any picture wider than the row is scaled down to fit, an asked-for size included: a 2000 px `=WxH` would otherwise run off the editor. Cost if wrong: one branch of `DiagramPicture.SizeOf`.
- **R11 — formats.** Bytes are SVG when, after a byte order mark and spaces, they start with `<` and their first 4 KB hold `<svg` — never by extension. SVG text goes to the diagram renderer as the new `DiagramEngine.Svg` kind: the page only turns it into a PNG (scripts and `on*` attributes dropped, as for Kroki's), with no white card (an image is shown as it is), cached by content whatever the theme. Every other image is decoded by WPF: its pixel size read from the header when it loads, its pixels decoded at the shown size through the diagram pictures' decoded-bitmap cache (24 kept). Without a codec (WebP on some PCs), or for a file that is no image: "The image could not be read." Cost if wrong: an SVG on a dark background may be hard to read (one flag to put it on the card).
- **R12 — resolving a source.** `data:` → only `data:image/…;base64,` (any other data URI cannot be read); `http(s)://` → only while web images are on; `file:` → its local path; a fully qualified path (`C:\…`, `\\server\share\…`) as it is; any other `scheme:`, a rooted path without a drive (`/uploads/a.png`, a Wiki.js server path), `C:a.png` and device paths (`\\.\`, `\\?\`) → "Image not found: {source}"; a relative path → against the file tab's folder, or "A relative path needs a saved file." in a note. Sources are percent-decoded (`my%20pic.png`); `<…>` may hold spaces. Cost if wrong: a Wiki.js `/uploads/…` image never shows (it lives on the wiki's server anyway).
- **R13 — cache and refresh.** Each window keeps one cache (64 results, memory only) shared by its tabs, keyed by origin and full path, web address, or the SHA-256 of a data URI. A tab shown again shows its images at once from the cache, and each of its files is read once more in the background, so an image edited elsewhere updates when its tab is shown again or a setting changes. A web or data image that loaded is not fetched again in the session; a failed download is not cached and is tried again when the tab is shown again or a setting changes. File reads and downloads run off the UI thread; the header probe and the decode run on it, as diagrams' decode does. Cost if wrong: an image edited while its tab is open needs a tab switch to update.
- **R14 — web requests.** One GET of the address through the system proxy, no cookies, at most 5 redirects (image hosts move images), User-Agent "MicaPad" (some hosts refuse requests without one), 10 s, 10 MB. Any failure — no answer, an error status, too large — reads "The image could not be downloaded ({host})." and is not cached. Cost if wrong: a redirect reaches a host the note did not name.
- **R15 — preview details.** A loading image shows "Loading…"; while its source is being typed the line's previous preview stays, as a diagram's does. A preview's title is its tooltip. A right-click on a preview opens no menu and leaves the caret and selection alone (it is a `DiagramPicture`). Lines up to 200,000 characters are searched for images (a data URI of about 150 KB fits), not the 4,000 of inline styling. Cost if wrong: small.

## Review Focus

1. Nothing leaves the PC by default: with Load images from the web off, a web image makes no request and says how to turn it on (Task 4 test `A_web_image_is_not_downloaded_while_web_images_are_off`).
2. MathJax draws offline: a formula and `\ce{}` come back as glyph outlines in the text color and the page asks the network for nothing (Task 1 test `Math_and_chemistry_draw_offline_in_the_text_color`).
3. The kroki form never sends a built-in type to Kroki, and waits for Kroki for the rest (Task 2 test `The_kroki_form_takes_its_type_from_the_first_line`).
4. Typing never loads images: nothing loads until the pause, and the line's old preview stays meanwhile (Task 4 test `Typing_on_an_image_line_loads_nothing_until_the_pause_and_keeps_the_old_preview`).
5. A relative image in a note says it needs a saved file; in a file tab it resolves beside the file (Task 5 test `A_relative_image_needs_a_saved_file_and_resolves_beside_one`).

---

## File Structure

| File | Task | Responsibility |
|---|---|---|
| Create `Pad/Diagrams/tex-svg-full.js` (vendored), `Pad/Diagrams/mathjax-config.js` | 1 | MathJax 3.2.2 and its settings (no menu, no extras, no network) |
| Modify `Pad/Diagrams/render.html`, `Pad/Diagrams/render.js`, `Pad/Diagrams/THIRD-PARTY.txt` | 1 | Script order; the page's `math` kind; MathJax's license |
| Modify `Services/Pad/FenceTracker.cs`, `Services/Pad/DiagramKinds.cs`, `Services/Pad/DiagramBlocks.cs` | 2 | `IsMathDelimiter`; the Math engine, its words and `$$`; the kroki form |
| Modify `GUIDE.md` (`### Diagrams`) | 2 | Math and the kroki form |
| Create `Services/Pad/ImageSources.cs` | 3 | `ImageRef`, `ImageOrigin`, `ImageLocation`, `ImageLoad`, `ImageText`, `ImageSources` |
| Modify `Services/Pad/DiagramResult.cs`, `Services/Pad/DiagramKinds.cs` | 4 | `DiagramResult.Image`, `PixelWidth`; `DiagramEngine.Svg`, `DiagramKinds.SvgImage`; a theme-free key for SVG images |
| Modify `Pad/DiagramPicture.cs` | 4 | `DiagramView.Width`/`Height`/`Menu`/`WaitingText`; `SizeOf`; decode width from `PixelWidth` |
| Create `Pad/ImageServices.cs`, `Pad/ImageBoard.cs` (with `ImageRow`), `Pad/ImageGenerator.cs`; modify `Pad/EditorLanguage.cs` | 4 | Previews in the editor |
| Modify `Models/SystemMetrics.cs`, `Pad/MicaPadWindow.Diagrams.cs`, `Pad/MicaPadWindow.xaml.cs`, `App.xaml.cs`, `SettingsWindow.xaml`, `SettingsWindow.xaml.cs` | 5 | `PadWebImages`; the window's previews; the app's loader; the Settings cards |
| Modify `GUIDE.md` (`### Markdown`) | 6 | The Markdown section as a feature list, with Format table and images |
| Tests | | modified `DiagramPageTests`, `DiagramKindsTests`, `DiagramBlocksTests`, `DiagramRendererTests`, `DiagramBoardTests`, `PadConfigTests`; new `ImageSourcesTests`, `ImageBoardTests`, `ImageWindowTests`, `MarkdownGuideTests` |

---

### Task 1: Math on the drawing page

**Files:**
- Create: `Pad/Diagrams/tex-svg-full.js` (downloaded), `Pad/Diagrams/mathjax-config.js`
- Modify: `Pad/Diagrams/render.html`, `Pad/Diagrams/render.js`, `Pad/Diagrams/THIRD-PARTY.txt`
- Test: `tests/Kil0bitSystemMonitor.Tests/DiagramPageTests.cs`

**Interfaces:**
- Consumes: `DiagramPage.CreateAsync`, `DiagramPage.DrawAsync(PageRequest, CancellationToken)`, `DiagramPage.ScriptsFolder`, `DiagramPage.RefusedRequests` (internal), `PageRequest(Kind, Source, Dark, Foreground, Background)` (existing). `Kil0bitSystemMonitor.csproj` already copies `Pad\Diagrams\*` to `Diagrams\`.
- Produces: page kind `"math"` — the page answers `{ ok: true, svg, png, width, height }` (an SVG of glyph outlines, `color` = the request's `fg`, size in px at 18 px type; the PNG at twice that) or `{ ok: false, error }` with MathJax's message. `finish(svgText, size)` in `render.js` takes an optional `{ width, height }`.

- [ ] **Step 1: Write the failing tests**

In `tests/Kil0bitSystemMonitor.Tests/DiagramPageTests.cs`, add `using System.Security.Cryptography;` to the usings, and add these tests after `The_scripts_are_copied_beside_the_app`:

```csharp
        [Fact]
        public void MathJax_is_vendored_and_loads_before_the_page_script()
        {
            string folder = DiagramPage.ScriptsFolder;
            string html = File.ReadAllText(Path.Combine(folder, "render.html"));
            int config = html.IndexOf("<script src=\"mathjax-config.js\"></script>", StringComparison.Ordinal);
            int mathjax = html.IndexOf("<script src=\"tex-svg-full.js\"></script>", StringComparison.Ordinal);
            int render = html.IndexOf("<script src=\"render.js\"></script>", StringComparison.Ordinal);

            Assert.True(config > 0 && config < mathjax && mathjax < render, "mathjax-config.js, then tex-svg-full.js, then render.js");
            Assert.Equal("a4354ff94fd868aea0cc6eaaa79a57fda0588646fc46ee3700a349ee0a11cbe6",
                         Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, "tex-svg-full.js")))).ToLowerInvariant());
            Assert.Contains("enableMenu: false", File.ReadAllText(Path.Combine(folder, "mathjax-config.js")));
            Assert.Contains("MathJax 3.2.2 (Apache-2.0)", File.ReadAllText(Path.Combine(folder, "THIRD-PARTY.txt")));
        }

        [Fact]
        public void Math_and_chemistry_draw_offline_in_the_text_color() => WithPage(async page =>
        {
            var formula = await Draw(page, "math", @"x = \frac{-b \pm \sqrt{b^2-4ac}}{2a}");

            Assert.True(formula.Error == null, formula.Error);
            Assert.Contains("<path", formula.Svg);
            Assert.DoesNotContain("<text", formula.Svg);   // glyph outlines: no font to load
            Assert.Contains("#1B1B1F", formula.Svg, StringComparison.OrdinalIgnoreCase);
            var (width, height) = PngSize(formula.Png!);
            Assert.InRange(width, (int)(formula.Width * 2) - 1, (int)(formula.Width * 2) + 1);
            Assert.InRange(height, (int)(formula.Height * 2) - 1, (int)(formula.Height * 2) + 1);

            var water = await Draw(page, "math", @"\ce{2H2 + O2 -> 2H2O}", dark: true);

            Assert.True(water.Error == null, water.Error);
            Assert.Contains("#EDEDF2", water.Svg, StringComparison.OrdinalIgnoreCase);
            Assert.True(water.Width > water.Height * 4, "a reaction on one line is wide");
            Assert.Equal(0, page.RefusedRequests);   // MathJax asked the network for nothing
        });

        [Fact]
        public void A_math_mistake_comes_back_as_the_mathjax_message() => WithPage(async page =>
        {
            Assert.Equal("Missing close brace", (await Draw(page, "math", @"\frac{1}{")).Error);

            // The page still draws after it.
            Assert.Null((await Draw(page, "math", "a + b")).Error);
        });
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~DiagramPageTests" 2>&1 | tail -15`
Expected: `MathJax_is_vendored_and_loads_before_the_page_script` fails (`mathjax-config.js, then tex-svg-full.js, then render.js`); the two math tests fail with the error `Unknown diagram kind.` The other page tests pass. (Where the WebView2 Runtime is missing, the page tests pass with a "Skipped" note: report that.)

- [ ] **Step 3: Vendor MathJax 3.2.2 and its license**

```bash
cd /c/AIProject/kil0bit-system-monitor
curl -sSfL -o "C:/AIProject/kil0bit-system-monitor/Pad/Diagrams/tex-svg-full.js" https://cdn.jsdelivr.net/npm/mathjax@3.2.2/es5/tex-svg-full.js
sha256sum Pad/Diagrams/tex-svg-full.js | cut -c1-64
curl -sSfL -o "$LOCALAPPDATA/Temp/mathjax-3.2.2-LICENSE" https://cdn.jsdelivr.net/npm/mathjax@3.2.2/LICENSE
sha256sum "$LOCALAPPDATA/Temp/mathjax-3.2.2-LICENSE" | cut -c1-64
```

Expected: `a4354ff94fd868aea0cc6eaaa79a57fda0588646fc46ee3700a349ee0a11cbe6` (2,275,113 bytes) and `cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30` (the Apache-2.0 text; MathJax 3.2.2 has no NOTICE file). Anything else: stop and report BLOCKED with what you got.

In `Pad/Diagrams/THIRD-PARTY.txt` (three edits, then an append):

1. After the line `markmap-lib.js    markmap-lib           0.18.12   MIT      https://github.com/markmap/markmap` add the line:

```text
tex-svg-full.js   mathjax               3.2.2     Apache-2.0 https://github.com/mathjax/MathJax
```

2. After the line `edfe321a3fe46e9c0f0af8370145aee180975f243f2242f0f8181ac4273e08e1  markmap-lib.js` add the line:

```text
a4354ff94fd868aea0cc6eaaa79a57fda0588646fc46ee3700a349ee0a11cbe6  tex-svg-full.js
```

3. Replace `render.html, frames.js and render.js are MicaStats' own (MIT, see LICENSE).` with `render.html, frames.js, mathjax-config.js and render.js are MicaStats' own (MIT, see LICENSE).`

4. Append MathJax's license:

```bash
T=/c/AIProject/kil0bit-system-monitor/Pad/Diagrams/THIRD-PARTY.txt
printf '\n\n==== %s ====\n\n' "MathJax 3.2.2 (Apache-2.0) - tex-svg-full.js, with mhchem by Martin Hensel" >> $T && cat "$LOCALAPPDATA/Temp/mathjax-3.2.2-LICENSE" >> $T
grep -c '^==== ' $T
grep -c $'\r' $T
```

Expected: `9`, then `0` (no CR: the file stays LF).

- [ ] **Step 4: The page draws math**

Create `Pad/Diagrams/mathjax-config.js` (the spike's, unchanged):

```js
"use strict";
// MathJax reads this before it loads: no menus, no accessibility extras (they would fetch from a CDN), no typesetting of the page.
window.MathJax = {
  startup: { typeset: false },
  options: { enableMenu: false, enableEnrichment: false, enableComplexity: false, enableExplorer: false, enableAssistiveMml: false },
  svg: { fontCache: "local" }
};
```

Replace `Pad/Diagrams/render.html` with (the Content-Security-Policy is unchanged; the config must load before MathJax, MathJax before `render.js`):

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
<script src="mathjax-config.js"></script>
<script src="tex-svg-full.js"></script>
<script src="render.js"></script>
</head>
<body><div id="stage"></div></body>
</html>
```

In `Pad/Diagrams/render.js` make five edits (do not touch `disarm`):

1. Replace the header lines

```js
// { id, ok: true, svg, png, width, height } or { id, ok: false, error }. kind is "mermaid", "dot",
// "markmap" or "svg" (a picture Kroki drew). Nothing here reaches the network: the page's
// Content-Security-Policy and the app's request filter both refuse it.
```

with

```js
// { id, ok: true, svg, png, width, height } or { id, ok: false, error }. kind is "mermaid", "dot",
// "markmap", "math" (TeX and \ce chemistry, drawn by MathJax) or "svg" (a picture Kroki drew, or an
// SVG image from a note). Nothing here reaches the network: the page's Content-Security-Policy and
// the app's request filter both refuse it.
```

2. Replace

```js
  // Gives the picture a fixed size in px (the viewBox's, else its width and height) and returns its markup.
  // The markup is parsed, never put into the page, so nothing in a picture runs or loads.
  function finish(svgText) {
```

with

```js
  // Gives the picture a fixed size in px (size when given, else the viewBox's, else its width and height)
  // and returns its markup. The markup is parsed, never put into the page, so nothing in a picture runs or loads.
  function finish(svgText, size) {
```

3. Replace

```js
    if (box.length === 4 && box[2] > 0 && box[3] > 0) { w = box[2]; h = box[3]; }
```

with

```js
    if (size) { w = size.width; h = size.height; }
    else if (box.length === 4 && box[2] > 0 && box[3] > 0) { w = box[2]; h = box[3]; }
```

4. Insert before the line `  // The picture as a PNG, SCALE times its size, its longest side at most MAX_SIDE pixels.` (the spike's `drawMath`, unchanged):

```js
  // TeX (with \ce chemistry) as an SVG made of glyph outlines: no fonts to load.
  async function drawMath(req) {
    await MathJax.startup.promise;
    const holder = document.createElement("div");
    holder.style.fontSize = "18px";
    holder.style.color = req.fg;
    stage().appendChild(holder);
    try {
      const node = MathJax.tex2svg(req.source, { display: true });
      holder.appendChild(node);
      const el = node.querySelector("svg");
      const err = el.querySelector("[data-mjx-error]");
      if (err) throw new Error(err.getAttribute("data-mjx-error"));
      const r = el.getBoundingClientRect();
      el.setAttribute("width", String(r.width));
      el.setAttribute("height", String(r.height));
      el.setAttribute("color", req.fg);
      el.style.color = req.fg;
      return finish(new XMLSerializer().serializeToString(el), { width: r.width, height: r.height });
    } finally {
      holder.remove();
    }
  }

```

5. Replace

```js
    const drawn = req.kind === "mermaid" ? await drawMermaid(req)
```

with

```js
    const drawn = req.kind === "math" ? await drawMath(req)
      : req.kind === "mermaid" ? await drawMermaid(req)
```

Check: `grep -c '\\u0000-\\u0020' Pad/Diagrams/render.js` prints `1` (the `disarm` regex is intact) and `grep -c $'\r' Pad/Diagrams/render.js Pad/Diagrams/render.html Pad/Diagrams/mathjax-config.js` prints `0` for each.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~DiagramPageTests" 2>&1 | tail -5`
Expected: all pass. Then the full suite.

- [ ] **Step 6: Commit**

```bash
git add Pad/Diagrams/tex-svg-full.js Pad/Diagrams/mathjax-config.js Pad/Diagrams/render.html Pad/Diagrams/render.js Pad/Diagrams/THIRD-PARTY.txt tests/Kil0bitSystemMonitor.Tests/DiagramPageTests.cs
git commit -m 'feat(pad): the diagram page draws math - MathJax 3.2.2 turns TeX and \ce chemistry into glyph outlines, offline' -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 2: `$$` blocks, math fences and the kroki form reach the diagram engine

**Files:**
- Modify: `Services/Pad/FenceTracker.cs` (`IsMathDelimiter`), `Services/Pad/DiagramKinds.cs` (replace), `Services/Pad/DiagramBlocks.cs` (replace), `GUIDE.md` (`### Diagrams` only)
- Test: `tests/Kil0bitSystemMonitor.Tests/DiagramKindsTests.cs`, `DiagramBlocksTests.cs`, `DiagramRendererTests.cs`, `DiagramBoardTests.cs`

**Interfaces:**
- Consumes: `FenceTracker.InfoWord`, the private `FenceTracker.MathRx` (Plan A), `DiagramBoard`/`DiagramGenerator` (unchanged: they call `DiagramKinds.FromFence` and `DiagramBlocks.Read`), `MarkdownDocumentCache.OpeningLineOf` (pairs `$$` lines).
- Produces:
  - `FenceTracker.IsMathDelimiter(string line) : bool` — the line is only `$$` (at most three spaces before it).
  - `DiagramEngine.Math`; `DiagramKind.EngineId`/`PageKind` give `"math"` for it.
  - `DiagramKinds.Math` (`"Math"`, words `math`, `latex`, `tex`), `DiagramKinds.KrokiForm` (word `kroki`, never drawn as it is), `DiagramKinds.FromKrokiType(string? line) : DiagramKind?`; `FromFence("$$")` is `Math`.
  - `DiagramBlocks.Read` reads the kroki form: kind from the first inside line, source from the lines after it.

- [ ] **Step 1: Write the failing tests**

In `tests/Kil0bitSystemMonitor.Tests/DiagramKindsTests.cs`, rename `There_are_34_words_all_lower_case` to `There_are_38_words_all_lower_case` and change `Assert.Equal(34, words.Count);` to `Assert.Equal(38, words.Count);` (nothing else in it). Then add:

```csharp
        [Theory]
        [InlineData("math")]
        [InlineData("latex")]
        [InlineData("TeX")]
        public void Math_words_name_the_math_engine(string word)
        {
            var kind = DiagramKinds.FromWord(word);

            Assert.Same(DiagramKinds.Math, kind);
            Assert.Equal(DiagramEngine.Math, kind!.Engine);
            Assert.Equal("Math", kind.Name);
            Assert.Equal("math", kind.EngineId);
            Assert.Equal("math", kind.PageKind);
            Assert.False(kind.NeedsKroki);
        }

        [Theory]
        [InlineData("$$")]
        [InlineData("   $$  ")]
        [InlineData("```math")]
        public void A_dollar_line_or_a_math_fence_opens_a_math_block(string line) => Assert.Same(DiagramKinds.Math, DiagramKinds.FromFence(line));

        [Theory]
        [InlineData("$$ x")]
        [InlineData("    $$")]
        [InlineData("$$$")]
        [InlineData("$")]
        public void Other_dollar_lines_open_nothing(string line) => Assert.Null(DiagramKinds.FromFence(line));

        [Theory]
        [InlineData("plantuml", "PlantUML", "plantuml", DiagramEngine.Kroki)]
        [InlineData("  D2 ", "D2", "d2", DiagramEngine.Kroki)]
        [InlineData("vega-lite", "Vega-Lite", "vegalite", DiagramEngine.Kroki)]
        [InlineData("mermaid", "Mermaid", null, DiagramEngine.Mermaid)]
        [InlineData("graphviz", "Graphviz", null, DiagramEngine.Graphviz)]
        [InlineData("svgbob2", "svgbob2", "svgbob2", DiagramEngine.Kroki)]
        public void A_kroki_type_line_names_its_kind(string line, string name, string? type, DiagramEngine engine)
        {
            var kind = DiagramKinds.FromKrokiType(line);

            Assert.NotNull(kind);
            Assert.Equal(name, kind!.Name);
            Assert.Equal(type, kind.KrokiType);
            Assert.Equal(engine, kind.Engine);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("Not a type")]
        [InlineData("Foo")]
        [InlineData("my-type")]
        [InlineData("kroki")]
        [InlineData("math")]
        public void Other_kroki_type_lines_name_no_kind(string? line) => Assert.Null(DiagramKinds.FromKrokiType(line));
```

In `tests/Kil0bitSystemMonitor.Tests/DiagramBlocksTests.cs` add:

```csharp
        [Fact]
        public void Math_blocks_are_dollar_blocks_and_math_fences()
        {
            var blocks = Find("$$\nx^2\n$$\n```tex\n\\sqrt{2}\n```\n$$ not alone");

            Assert.Equal(2, blocks.Count);
            Assert.Same(DiagramKinds.Math, blocks[0].Kind);
            Assert.Equal("x^2", blocks[0].Source);
            Assert.Equal(1, blocks[0].OpenLine);
            Assert.Equal(3, blocks[0].CloseLine);
            Assert.Same(DiagramKinds.Math, blocks[1].Kind);
            Assert.Equal("\\sqrt{2}", blocks[1].Source);
        }

        [Fact]
        public void The_kroki_form_reads_its_type_line()
        {
            var blocks = Find("```kroki\nd2\na -> b\n```\n```kroki\nbpmnx2\n<x/>\n```\n```kroki\nNot A Type\nx\n```\n```kroki\nplantuml\n```");

            Assert.Equal(2, blocks.Count);
            Assert.Equal("D2", blocks[0].Kind.Name);
            Assert.Equal("a -> b", blocks[0].Source);
            Assert.Equal(1, blocks[0].OpenLine);
            Assert.Equal(4, blocks[0].CloseLine);
            Assert.Equal("bpmnx2", blocks[1].Kind.KrokiType);
            Assert.Equal("<x/>", blocks[1].Source);
        }
```

In `tests/Kil0bitSystemMonitor.Tests/DiagramRendererTests.cs` add:

```csharp
        [Fact]
        public void A_math_block_is_drawn_on_the_page_as_math_in_the_text_color()
        {
            var page = new FakePage { Answer = _ => DiagramFakes.Drawn(80, 20) };
            using var renderer = Over(page);
            var request = DiagramFakes.Request("math", "E = mc^2", PadThemes.Dark);

            var result = Wait(renderer.RenderAsync(request, new object()));

            Assert.True(result.IsPicture);
            Assert.False(result.Paper);
            Assert.Equal(new PageRequest("math", "E = mc^2", true, "#EDEDF2", "#0E0E13"), Assert.Single(page.Requests));
        }
```

In `tests/Kil0bitSystemMonitor.Tests/DiagramBoardTests.cs` add:

```csharp
        [Fact]
        public void A_dollar_block_and_a_math_fence_get_math_pictures() => UiThread.Run(() =>
        {
            var board = new Board("Energy:\n$$\nE = mc^2\n$$\n```latex\n\\int_0^1 x\\,dx\n```");

            Assert.Null(board.PictureUnder(2));   // the opening $$ gets none
            Assert.True(board.PictureUnder(4)!.IsDrawing);
            Assert.NotNull(board.PictureUnder(7));
            Assert.Equal(2, board.Renderer.Calls.Count);
            Assert.All(board.Renderer.Calls, c => Assert.Same(DiagramKinds.Math, c.Request.Kind));
            Assert.Equal("E = mc^2", board.Renderer.Calls[0].Request.Source);
            Assert.Equal("\\int_0^1 x\\,dx", board.Renderer.Calls[1].Request.Source);
            Assert.Null(board.Renderer.Calls[0].Request.KrokiServer);
        });

        [Fact]
        public void The_kroki_form_takes_its_type_from_the_first_line() => UiThread.Run(() =>
        {
            var board = new Board("```kroki\nplantuml\n@startuml\na -> b\n@enduml\n```\n\n```kroki\nmermaid\nflowchart LR\n  a --> b\n```");

            Assert.Equal("PlantUML needs Kroki \u2014 turn it on in Settings \u2192 MicaPad.", board.PictureUnder(6)!.ErrorText!.Text);
            var offline = Assert.Single(board.Renderer.Calls);   // mermaid under kroki is drawn on this PC
            Assert.Equal(DiagramEngine.Mermaid, offline.Request.Kind.Engine);
            Assert.Equal("flowchart LR\n  a --> b", offline.Request.Source);
            Assert.Null(offline.Request.KrokiServer);

            board.Server = "https://kroki.io";
            board.Language.RefreshDiagrams();
            board.Render();

            Assert.Equal(2, board.Renderer.Calls.Count);
            var kroki = board.Renderer.Calls[1].Request;
            Assert.Equal("plantuml", kroki.Kind.KrokiType);
            Assert.Equal("@startuml\na -> b\n@enduml", kroki.Source);
            Assert.Equal("https://kroki.io", kroki.KrokiServer);
        });
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~DiagramKindsTests|FullyQualifiedName~DiagramBlocksTests|FullyQualifiedName~DiagramRendererTests|FullyQualifiedName~DiagramBoardTests" 2>&1 | tail -5`
Expected: build error, `DiagramKinds` has no `Math`.

- [ ] **Step 3: Implement**

In `Services/Pad/FenceTracker.cs`, add after `MayBeDelimiter`:

```csharp
        /// <summary>True for a line that is only <c>$$</c> (at most three spaces before it): it opens or closes a math block.</summary>
        public static bool IsMathDelimiter(string line) => MathRx.IsMatch(line ?? "");
```

Replace `Services/Pad/DiagramKinds.cs` with:

```csharp
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>Who draws a diagram: one of the engines built into MicaPad, or a Kroki server.</summary>
    public enum DiagramEngine
    {
        Mermaid,
        Graphviz,
        Markmap,
        Kroki,

        /// <summary>TeX formulas and <c>\ce{}</c> chemistry, drawn by MathJax on the page (Markdown spec 6.1).</summary>
        Math,
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

        /// <summary>The engine part of a cache key: "mermaid", "graphviz", "markmap", "math" or "kroki/&lt;type&gt;".</summary>
        public string EngineId => Engine switch
        {
            DiagramEngine.Mermaid => "mermaid",
            DiagramEngine.Graphviz => "graphviz",
            DiagramEngine.Markmap => "markmap",
            DiagramEngine.Math => "math",
            _ => "kroki/" + KrokiType,
        };

        /// <summary>What the drawing page is asked for: "mermaid", "dot", "markmap", "math", or "svg" for a picture drawn elsewhere (Kroki's).</summary>
        public string PageKind => Engine switch
        {
            DiagramEngine.Mermaid => "mermaid",
            DiagramEngine.Graphviz => "dot",
            DiagramEngine.Markmap => "markmap",
            DiagramEngine.Math => "math",
            _ => "svg",
        };
    }

    /// <summary>
    /// The fence words that make a fenced block a diagram (spec section 1; math and the Wiki.js
    /// kroki form, Markdown spec 6.1 and 6.2), compared without case, and the <c>$$</c> line that
    /// opens a math block. Any other word is ordinary code.
    /// </summary>
    public static class DiagramKinds
    {
        private static readonly Regex KrokiTypeRx = new("^[a-z0-9]+$", RegexOptions.CultureInvariant);

        /// <summary>Math: a <c>$$</c> block, or a fence whose word is math, latex or tex.</summary>
        public static DiagramKind Math { get; } = new("Math", DiagramEngine.Math, null);

        /// <summary>
        /// The Wiki.js kroki form: the block's first inside line names the type. Never drawn as it
        /// is: <see cref="DiagramBlocks.Read"/> replaces it by the kind <see cref="FromKrokiType"/> gives.
        /// </summary>
        public static DiagramKind KrokiForm { get; } = new("Kroki", DiagramEngine.Kroki, null);

        // After Math and KrokiForm: static initializers run in order, and Build adds both.
        private static readonly Dictionary<string, DiagramKind> ByWord = Build();

        /// <summary>Every fence word, lower case.</summary>
        public static IEnumerable<string> Words => ByWord.Keys;

        /// <summary>The kind a fence word names, or null for ordinary code.</summary>
        public static DiagramKind? FromWord(string? word) =>
            word != null && ByWord.TryGetValue(word, out var kind) ? kind : null;

        /// <summary>
        /// The kind an opening line names: a line that is only <c>$$</c> opens a math block; a fence
        /// names its kind by the first word of its info string (<c>```mermaid title</c> is Mermaid).
        /// At most three spaces before either; null for anything else.
        /// </summary>
        public static DiagramKind? FromFence(string openingLine) =>
            FenceTracker.IsMathDelimiter(openingLine) ? Math : FromWord(FenceTracker.InfoWord(openingLine));

        /// <summary>
        /// The kind the type line of a kroki block names (Markdown spec 6.2): a MicaPad word gives its
        /// kind, so mermaid and dot are drawn on this PC; any other lower-case word of letters and
        /// digits is sent to Kroki as that type, named by itself; math, kroki and anything else name none.
        /// </summary>
        public static DiagramKind? FromKrokiType(string? line)
        {
            string type = (line ?? "").Trim();
            if (type.Length == 0) return null;
            if (FromWord(type) is { } known)
                return ReferenceEquals(known, KrokiForm) || known.Engine == DiagramEngine.Math ? null : known;
            return KrokiTypeRx.IsMatch(type) ? new DiagramKind(type, DiagramEngine.Kroki, type) : null;
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
            Add(Math, "math", "latex", "tex");
            Add(KrokiForm, "kroki");
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

Replace `Services/Pad/DiagramBlocks.cs` with:

```csharp
using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// A diagram block: its kind, its opening and closing fence lines (1-based) and its source,
    /// the text between them joined with "\n" (in the kroki form, the lines after the type line).
    /// A source over <see cref="DiagramBlocks.MaxSourceLength"/> characters is not read:
    /// <see cref="TooLarge"/> is set and the source is empty.
    /// </summary>
    public sealed record DiagramBlock(DiagramKind Kind, int OpenLine, int CloseLine, string Source, bool TooLarge);

    /// <summary>
    /// Finds the diagram blocks of a document (spec section 1): a closed fenced block whose info
    /// string's first word is a diagram word, or a closed <c>$$</c> math block (Markdown spec 6.1),
    /// with something other than whitespace inside (R4). A kroki block takes its kind from its
    /// first inside line (Markdown spec 6.2).
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
        /// line names no diagram, a kroki block's type line names none, or the source is blank.
        /// </summary>
        public static DiagramBlock? Read(Func<int, string> lineText, int openLine, int closeLine)
        {
            if (openLine < 1 || closeLine <= openLine) return null;
            var kind = DiagramKinds.FromFence(lineText(openLine));
            if (kind == null) return null;

            int first = openLine + 1;
            if (ReferenceEquals(kind, DiagramKinds.KrokiForm))
            {
                if (first >= closeLine) return null;
                kind = DiagramKinds.FromKrokiType(lineText(first));
                if (kind == null) return null;
                first++;
            }

            var parts = new List<string>();
            int length = 0;
            bool blank = true;
            for (int n = first; n < closeLine; n++)
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

In `GUIDE.md`, `### Diagrams` section only (the guide test reads every fence word there):

1. Replace the section's first paragraph

```markdown
A fenced block whose first word names a diagram type gets a picture right under it in Markdown
notes. The code stays above the picture and stays editable; the picture follows a moment after
you stop typing.
```

with

```markdown
A fenced block whose first word names a diagram type — or a math block between two `$$` lines —
gets a picture right under it in Markdown notes. The code stays above the picture and stays
editable; the picture follows a moment after you stop typing.
```

2. After the table row `| Markmap — a mindmap from a Markdown outline | `markmap` |` add:

```markdown
| Math — TeX formulas and `\ce{}` chemistry, by MathJax | `math`, `latex`, `tex`, or a block between two `$$` lines |
```

3. After the paragraph that ends `pictures sit on a white card in both themes.` add a blank line and:

```markdown
Wiki.js's `kroki` form works too: in a ```` ```kroki ```` block the first line names the type
(`plantuml`, `d2`, …) and the rest is the diagram. A type MicaPad draws itself (`mermaid`, `dot`)
is drawn on this PC; any other type needs Kroki, as above.
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~DiagramKindsTests|FullyQualifiedName~DiagramBlocksTests|FullyQualifiedName~DiagramRendererTests|FullyQualifiedName~DiagramBoardTests|FullyQualifiedName~DiagramWindowTests|FullyQualifiedName~MarkdownStructureTests" 2>&1 | tail -5`
Expected: all pass (`DiagramWindowTests.The_guide_lists_every_fence_word_and_what_a_picture_offers` now finds `math`, `latex`, `tex` and `kroki`). Then the full suite.

- [ ] **Step 5: Commit**

```bash
git add Services/Pad/FenceTracker.cs Services/Pad/DiagramKinds.cs Services/Pad/DiagramBlocks.cs GUIDE.md tests/Kil0bitSystemMonitor.Tests/DiagramKindsTests.cs tests/Kil0bitSystemMonitor.Tests/DiagramBlocksTests.cs tests/Kil0bitSystemMonitor.Tests/DiagramRendererTests.cs tests/Kil0bitSystemMonitor.Tests/DiagramBoardTests.cs
git commit -m 'feat(pad): $$ blocks and math fences draw as math; the Wiki.js kroki form takes its type from its first line' -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 3: Image sources

**Files:**
- Create: `Services/Pad/ImageSources.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/ImageSourcesTests.cs` (also holds `FakeImageHandler`, used by Tasks 4 and 5)

**Interfaces:**
- Consumes: `DiagramFakes.Png`, `DiagramFakes.Svg`, `PadTempDir` (tests).
- Produces:
  - `sealed record ImageRef(int Start, int Length, string Alt, string Source, string? Title, double? Width, double? Height)`.
  - `enum ImageOrigin { File, Data, Web }`; `sealed record ImageLocation(ImageOrigin Origin, string Source, string Address, string Key, string? Error)`; `sealed record ImageLoad(byte[]? Bytes, bool Svg, string? Error, bool Lasting)`.
  - `static class ImageText` — `Loading`, `WebOff`, `CouldNotRead`, `NeedsSavedFile`, `NotFound(string source)`, `CouldNotDownload(string host)`.
  - `sealed class ImageSources : IDisposable` — `MaxFileBytes` (20 MB), `MaxWebBytes` (10 MB), `MaxLineLength` (200,000), `DefaultTimeout` (10 s); `ImageSources(HttpMessageHandler? handler = null, TimeSpan? timeout = null)`; `static IReadOnlyList<ImageRef> Find(string line)`; `static ImageLocation Resolve(string source, string? baseFolder, bool webAllowed)`; `Task<ImageLoad> LoadAsync(ImageLocation location, CancellationToken cancel = default)` (files read on the thread pool; never throws for a missing file or a failed download); `static bool LooksLikeSvg(byte[] bytes)`.

- [ ] **Step 1: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/ImageSourcesTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>A web server for image tests: answers as the test says (a 1x1 PNG by default) and records each request.</summary>
    internal sealed class FakeImageHandler : HttpMessageHandler
    {
        private readonly List<(HttpMethod Method, Uri Uri, bool HasBody)> _requests = new();

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _) => Task.FromResult(Bytes(DiagramFakes.Png));

        public IReadOnlyList<(HttpMethod Method, Uri Uri, bool HasBody)> Requests
        {
            get { lock (_requests) return _requests.ToArray(); }
        }

        public static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            lock (_requests) _requests.Add((request.Method, request.RequestUri!, request.Content != null));
            return Respond(request, cancel);
        }
    }

    /// <summary>Finding, resolving and loading images (spec 6.3 and "Testing": ImageSources), with temp files and a fake web.</summary>
    public class ImageSourcesTests
    {
        private static ImageLoad Load(ImageSources sources, ImageLocation location)
        {
            var task = sources.LoadAsync(location);
            Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "the load did not finish");
            return task.Result;
        }

        [Fact]
        public void An_image_has_its_alt_source_title_and_place()
        {
            var image = Assert.Single(ImageSources.Find("See ![a chart](img/chart.png \"Sales\") here"));

            Assert.Equal(4, image.Start);
            Assert.Equal("![a chart](img/chart.png \"Sales\")".Length, image.Length);
            Assert.Equal("a chart", image.Alt);
            Assert.Equal("img/chart.png", image.Source);
            Assert.Equal("Sales", image.Title);
            Assert.Null(image.Width);
            Assert.Null(image.Height);
        }

        [Theory]
        [InlineData("![x](a.png =200x)", 200.0, null)]
        [InlineData("![x](a.png =x120)", null, 120.0)]
        [InlineData("![x](a.png =200x120)", 200.0, 120.0)]
        [InlineData("![x](a.png 'T' =64x48)", 64.0, 48.0)]
        [InlineData("![x](a.png)", null, null)]
        public void The_wikijs_size_is_in_device_independent_pixels(string line, double? width, double? height)
        {
            var image = Assert.Single(ImageSources.Find(line));

            Assert.Equal("a.png", image.Source);
            Assert.Equal(width, image.Width);
            Assert.Equal(height, image.Height);
        }

        [Fact]
        public void Several_images_on_a_line_are_found_in_order()
        {
            var images = ImageSources.Find("![a](1.png) and ![b](2.png)");

            Assert.Equal(new[] { 0, 16 }, new[] { images[0].Start, images[1].Start });
            Assert.Equal(new[] { "1.png", "2.png" }, new[] { images[0].Source, images[1].Source });
        }

        [Fact]
        public void Spaces_go_in_angle_brackets_or_as_percent_twenty_and_parentheses_pair()
        {
            Assert.Equal(@"C:\My Pictures\a.png", Assert.Single(ImageSources.Find(@"![a](<C:\My Pictures\a.png>)")).Source);
            Assert.Equal("my%20pic.png", Assert.Single(ImageSources.Find("![a](my%20pic.png)")).Source);
            Assert.Equal("https://en.wikipedia.org/wiki/File:A_(b).png",
                         Assert.Single(ImageSources.Find("![w](https://en.wikipedia.org/wiki/File:A_(b).png)")).Source);
            Assert.Equal("a.png", Assert.Single(ImageSources.Find("[![a](a.png)](https://example.com)")).Source);
        }

        [Theory]
        [InlineData("[a link](a.png)")]
        [InlineData("\\![not](a.png)")]
        [InlineData("`![code](a.png)`")]
        [InlineData("![no source]()")]
        [InlineData("![open](a.png")]
        [InlineData("![x](a.png =abc)")]
        [InlineData("![x](a.png =0x10)")]
        [InlineData("![x](a.png \"t\" junk)")]
        public void Text_that_is_no_image_gives_none(string line) => Assert.Empty(ImageSources.Find(line));

        [Fact]
        public void Paths_resolve_absolute_relative_and_from_file_addresses()
        {
            using var dir = new PadTempDir();

            var absolute = ImageSources.Resolve(dir.PathOf("a.png"), null, webAllowed: false);
            Assert.Equal(ImageOrigin.File, absolute.Origin);
            Assert.Equal(dir.PathOf("a.png"), absolute.Address);
            Assert.Null(absolute.Error);

            Assert.Equal(dir.PathOf(Path.Combine("img", "b c.png")), ImageSources.Resolve("img/b%20c.png", dir.Root, false).Address);
            Assert.Equal(dir.PathOf("x.png"), ImageSources.Resolve("sub/../x.png", dir.Root, false).Address);
            Assert.Equal(dir.PathOf("a b.png"), ImageSources.Resolve(new Uri(dir.PathOf("a b.png")).AbsoluteUri, null, false).Address);
            Assert.NotEqual(ImageSources.Resolve("a.png", dir.Root, false).Key, ImageSources.Resolve("b.png", dir.Root, false).Key);
        }

        [Fact]
        public void A_relative_path_in_a_note_needs_a_saved_file() =>
            Assert.Equal("A relative path needs a saved file.", ImageSources.Resolve("pic.png", null, false).Error);

        [Theory]
        [InlineData("/uploads/a.png")]
        [InlineData("C:a.png")]
        [InlineData("ftp://host/a.png")]
        [InlineData(@"\\.\PhysicalDrive0")]
        public void Sources_that_are_no_local_file_or_web_address_are_not_found(string source) =>
            Assert.Equal("Image not found: " + source, ImageSources.Resolve(source, @"C:\notes", webAllowed: true).Error);

        [Fact]
        public void Web_images_wait_for_the_setting()
        {
            Assert.Equal("Web images are off \u2014 turn them on in Settings \u2192 MicaPad.",
                         ImageSources.Resolve("https://example.com/a.png", null, webAllowed: false).Error);

            var on = ImageSources.Resolve("https://example.com/a.png", null, webAllowed: true);
            Assert.Equal(ImageOrigin.Web, on.Origin);
            Assert.Equal("https://example.com/a.png", on.Address);
            Assert.Null(on.Error);
        }

        [Fact]
        public void A_data_address_is_read_from_the_note_itself()
        {
            using var sources = new ImageSources(new FakeImageHandler());
            string uri = "data:image/png;base64," + Convert.ToBase64String(DiagramFakes.Png);

            var location = ImageSources.Resolve(uri, null, false);
            var load = Load(sources, location);

            Assert.Equal(ImageOrigin.Data, location.Origin);
            Assert.Equal(DiagramFakes.Png, load.Bytes);
            Assert.False(load.Svg);
            Assert.True(location.Key.Length < 100);   // keyed by its hash, not its text
            Assert.Equal("The image could not be read.", ImageSources.Resolve("data:text/plain;base64,QQ==", null, false).Error);
            Assert.Equal("The image could not be read.", ImageSources.Resolve("data:image/svg+xml;utf8,<svg/>", null, false).Error);
        }

        [Fact]
        public void A_file_is_read_and_svg_text_is_known_by_its_content()
        {
            using var dir = new PadTempDir();
            File.WriteAllBytes(dir.PathOf("a.png"), DiagramFakes.Png);
            File.WriteAllText(dir.PathOf("b.png"), "\uFEFF<?xml version=\"1.0\"?>\n" + DiagramFakes.Svg);   // an SVG whatever its name
            using var sources = new ImageSources(new FakeImageHandler());

            var png = Load(sources, ImageSources.Resolve("a.png", dir.Root, false));
            var svg = Load(sources, ImageSources.Resolve("b.png", dir.Root, false));
            var missing = Load(sources, ImageSources.Resolve("gone.png", dir.Root, false));

            Assert.Equal(DiagramFakes.Png, png.Bytes);
            Assert.False(png.Svg);
            Assert.True(svg.Svg);
            Assert.Equal("Image not found: gone.png", missing.Error);
            Assert.True(missing.Lasting);
        }

        [Fact]
        public void Files_over_20_MB_and_downloads_over_10_MB_are_refused()
        {
            using var dir = new PadTempDir();
            using (var big = File.Create(dir.PathOf("big.png"))) big.SetLength(ImageSources.MaxFileBytes + 1);
            var handler = new FakeImageHandler { Respond = (_, _) => Task.FromResult(FakeImageHandler.Bytes(new byte[ImageSources.MaxWebBytes + 1])) };
            using var sources = new ImageSources(handler);

            Assert.Equal(20L * 1024 * 1024, ImageSources.MaxFileBytes);
            Assert.Equal(10 * 1024 * 1024, ImageSources.MaxWebBytes);
            Assert.Equal("The image could not be read.", Load(sources, ImageSources.Resolve("big.png", dir.Root, false)).Error);
            Assert.Equal("The image could not be downloaded (example.com).",
                         Load(sources, ImageSources.Resolve("https://example.com/big.png", null, true)).Error);
        }

        [Fact]
        public void A_download_is_one_get_of_that_address()
        {
            var handler = new FakeImageHandler();
            using var sources = new ImageSources(handler);

            var load = Load(sources, ImageSources.Resolve("https://example.com/pics/a.png?x=1", null, true));

            Assert.Equal(DiagramFakes.Png, load.Bytes);
            Assert.True(load.Lasting);
            var sent = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Get, sent.Method);
            Assert.Equal("https://example.com/pics/a.png?x=1", sent.Uri.AbsoluteUri);
            Assert.False(sent.HasBody);
            Assert.Equal(TimeSpan.FromSeconds(10), ImageSources.DefaultTimeout);
        }

        [Fact]
        public void A_failed_or_slow_download_says_so_and_passes()
        {
            var refusing = new FakeImageHandler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)) };
            using var sources = new ImageSources(refusing);
            var missing = Load(sources, ImageSources.Resolve("https://example.com/a.png", null, true));
            Assert.Equal("The image could not be downloaded (example.com).", missing.Error);
            Assert.False(missing.Lasting);

            var slow = new FakeImageHandler
            {
                Respond = async (_, cancel) =>
                {
                    await Task.Delay(5000, cancel);
                    return FakeImageHandler.Bytes(DiagramFakes.Png);
                },
            };
            using var impatient = new ImageSources(slow, TimeSpan.FromMilliseconds(100));
            Assert.Equal("The image could not be downloaded (example.com).",
                         Load(impatient, ImageSources.Resolve("https://example.com/a.png", null, true)).Error);
        }

        [Theory]
        [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/>", true)]
        [InlineData("  \n<?xml version=\"1.0\"?><!-- x --><svg/>", true)]
        [InlineData("<html><body/></html>", false)]
        [InlineData("GIF89a", false)]
        public void Svg_is_known_by_its_first_bytes(string text, bool svg) =>
            Assert.Equal(svg, ImageSources.LooksLikeSvg(System.Text.Encoding.UTF8.GetBytes(text)));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~ImageSourcesTests" 2>&1 | tail -5`
Expected: build error, `ImageSources` does not exist.

- [ ] **Step 3: Implement**

`Services/Pad/ImageSources.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// One <c>![alt](source "title" =WxH)</c> in a line (spec 6.3): where it starts, its length, its
    /// parts, and the size it asks for in device-independent pixels (either side may be absent).
    /// </summary>
    public sealed record ImageRef(int Start, int Length, string Alt, string Source, string? Title, double? Width, double? Height);

    /// <summary>Where an image's bytes come from.</summary>
    public enum ImageOrigin
    {
        File,
        Data,
        Web,
    }

    /// <summary>
    /// An image source resolved: a file's full path, a data URI or a web address, with the key the
    /// image cache uses (a data URI by its hash); or, in <see cref="Error"/>, why it cannot be shown.
    /// <see cref="Source"/> is the source as the note writes it.
    /// </summary>
    public sealed record ImageLocation(ImageOrigin Origin, string Source, string Address, string Key, string? Error);

    /// <summary>What loading gave: the bytes (SVG text when <see cref="Svg"/>), or an error that lasts (the same load gives it again) or passes.</summary>
    public sealed record ImageLoad(byte[]? Bytes, bool Svg, string? Error, bool Lasting);

    /// <summary>The words MicaPad shows about images (spec 6.3).</summary>
    public static class ImageText
    {
        public const string Loading = "Loading\u2026";
        public const string WebOff = "Web images are off \u2014 turn them on in Settings \u2192 MicaPad.";
        public const string CouldNotRead = "The image could not be read.";
        public const string NeedsSavedFile = "A relative path needs a saved file.";

        /// <summary>"Image not found: {source}", the source as the note writes it.</summary>
        public static string NotFound(string source) => "Image not found: " + source;

        /// <summary>"The image could not be downloaded ({host})."</summary>
        public static string CouldNotDownload(string host) => "The image could not be downloaded (" + host + ").";
    }

    /// <summary>
    /// Where image previews come from (spec 6.3): finding <c>![...](...)</c> in a line outside code
    /// spans, resolving a source against the tab's file folder (R12), and loading it — a file (20 MB
    /// at most, read on the thread pool), a <c>data:image/...;base64,</c> address, or a web address
    /// while web images are allowed (one GET, 10 s, 10 MB at most, no cookies; R14). Never throws for
    /// a missing file or a failed download: those are <see cref="ImageLoad"/> errors.
    /// </summary>
    public sealed class ImageSources : IDisposable
    {
        public const long MaxFileBytes = 20L * 1024 * 1024;
        public const int MaxWebBytes = 10 * 1024 * 1024;

        /// <summary>Longer lines are not searched (R15): a data address of about 150 KB still fits.</summary>
        public const int MaxLineLength = 200_000;

        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

        private const int SvgSniffBytes = 4096;
        private static readonly Regex DataRx = new(@"^data:image/[a-z0-9.+-]+(;[a-z0-9._=-]+)*;base64,", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex SchemeRx = new(@"^[A-Za-z][A-Za-z0-9+.-]+:", RegexOptions.CultureInvariant);

        private readonly HttpClient _http;

        /// <param name="handler">Tests pass a fake web; null uses <see cref="CreateHandler"/>.</param>
        public ImageSources(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
        {
            _http = handler == null ? new HttpClient(CreateHandler()) : new HttpClient(handler, disposeHandler: false);
            _http.Timeout = timeout ?? DefaultTimeout;
            _http.MaxResponseContentBufferSize = MaxWebBytes;
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("MicaPad");
        }

        /// <summary>The system's network settings (its proxy included), no cookies, at most 5 redirects (R14).</summary>
        internal static HttpMessageHandler CreateHandler() => new SocketsHttpHandler { UseCookies = false, MaxAutomaticRedirections = 5 };

        /// <summary>Every image of the line, in order. Escaped (<c>\!</c>) and code-span images are text.</summary>
        public static IReadOnlyList<ImageRef> Find(string line)
        {
            var found = new List<ImageRef>();
            string s = line ?? "";
            if (s.Length > MaxLineLength || !s.Contains("![", StringComparison.Ordinal)) return found;

            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\')
                {
                    i += 2;   // an escaped character, \! included, is text
                    continue;
                }
                if (c == '`')
                {
                    i = SkipCode(s, i);
                    continue;
                }
                if (c == '!' && i + 1 < s.Length && s[i + 1] == '[' && TryImage(s, i) is { } image)
                {
                    found.Add(image);
                    i += image.Length;
                    continue;
                }
                i++;
            }
            return found;
        }

        /// <summary>
        /// Where <paramref name="source"/> points (R12): a data address, a web address (only while
        /// <paramref name="webAllowed"/>), a <c>file:</c> address, a full path, or a path relative to
        /// <paramref name="baseFolder"/> (the file tab's folder; null in a note). Anything else is an error.
        /// </summary>
        public static ImageLocation Resolve(string source, string? baseFolder, bool webAllowed)
        {
            string text = (source ?? "").Trim();
            if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return DataRx.IsMatch(text) ? new ImageLocation(ImageOrigin.Data, text, text, "data\0" + Hash(text), null) : Refused(text, ImageText.CouldNotRead);

            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(text, UriKind.Absolute, out var web) || web.Host.Length == 0) return Refused(text, ImageText.NotFound(text));
                if (!webAllowed) return Refused(text, ImageText.WebOff);
                return new ImageLocation(ImageOrigin.Web, text, web.AbsoluteUri, "web\0" + web.AbsoluteUri, null);
            }

            string? path;
            if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                path = Uri.TryCreate(text, UriKind.Absolute, out var file) && file.IsFile ? file.LocalPath : null;
            }
            else
            {
                string decoded = Uri.UnescapeDataString(text);
                if (Path.IsPathFullyQualified(decoded)) path = decoded;
                else if (Path.IsPathRooted(decoded) || SchemeRx.IsMatch(decoded)) path = null;   // "/uploads/a.png" lives on a wiki's server
                else if (baseFolder == null) return Refused(text, ImageText.NeedsSavedFile);
                else path = Path.Combine(baseFolder, decoded);
            }

            if (path == null || path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal))
                return Refused(text, ImageText.NotFound(text));
            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
            {
                return Refused(text, ImageText.NotFound(text));
            }
            return new ImageLocation(ImageOrigin.File, text, path, "file\0" + path, null);
        }

        /// <summary>Loads the image's bytes: a file on the thread pool, a data address at once, a download through the HTTP client.</summary>
        public Task<ImageLoad> LoadAsync(ImageLocation location, CancellationToken cancel = default)
        {
            if (location.Error != null) return Task.FromResult(Failed(location.Error, lasting: true));
            return location.Origin switch
            {
                ImageOrigin.Data => Task.FromResult(ReadData(location.Address)),
                ImageOrigin.Web => DownloadAsync(location.Address, cancel),
                _ => Task.Run(() => ReadFile(location), cancel),
            };
        }

        /// <summary>True when the bytes are SVG text (R11): after a byte order mark and spaces they start with &lt;, and the first 4 KB hold &lt;svg.</summary>
        public static bool LooksLikeSvg(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return false;
            string head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, SvgSniffBytes)).TrimStart((char)0xFEFF, ' ', '\t', '\r', '\n');
            return head.StartsWith('<') && head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose() => _http.Dispose();

        private static ImageLocation Refused(string source, string error) => new(ImageOrigin.File, source, "", "", error);

        private static ImageLoad Loaded(byte[] bytes) => new(bytes, LooksLikeSvg(bytes), null, Lasting: true);

        private static ImageLoad Failed(string error, bool lasting) => new(null, false, error, lasting);

        private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        private static string HostOf(string address) =>
            Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Authority : address;

        private static ImageLoad ReadData(string uri)
        {
            try
            {
                return Loaded(Convert.FromBase64String(uri.Substring(uri.IndexOf(',') + 1)));
            }
            catch (FormatException)
            {
                return Failed(ImageText.CouldNotRead, lasting: true);
            }
        }

        private static ImageLoad ReadFile(ImageLocation location)
        {
            try
            {
                using var stream = new FileStream(location.Address, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaxFileBytes) return Failed(ImageText.CouldNotRead, lasting: true);
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                return Loaded(bytes);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return Failed(ImageText.NotFound(location.Source), lasting: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
            {
                return Failed(ImageText.CouldNotRead, lasting: false);   // locked or unreadable now; it may be readable later
            }
        }

        private async Task<ImageLoad> DownloadAsync(string address, CancellationToken cancel)
        {
            string failed = ImageText.CouldNotDownload(HostOf(address));
            try
            {
                // MaxResponseContentBufferSize refuses an answer over 10 MB while it is read.
                using var response = await _http.GetAsync(address, cancel).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return Failed(failed, lasting: false);
                byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
                return bytes.Length > MaxWebBytes ? Failed(failed, lasting: false) : Loaded(bytes);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
            {
                return Failed(failed, lasting: false);
            }
        }

        /// <summary>The image at <paramref name="start"/> (its <c>!</c>), or null when the text there is no image.</summary>
        private static ImageRef? TryImage(string s, int start)
        {
            var alt = new StringBuilder();
            int i = start + 2;
            while (i < s.Length && s[i] != ']')
            {
                if (s[i] == '[') return null;
                if (s[i] == '\\' && i + 1 < s.Length) i++;
                alt.Append(s[i]);
                i++;
            }
            if (i + 1 >= s.Length || s[i + 1] != '(') return null;
            i += 2;
            SkipSpaces(s, ref i);

            string source;
            if (i < s.Length && s[i] == '<')
            {
                int close = s.IndexOf('>', i + 1);
                if (close < 0) return null;
                source = s.Substring(i + 1, close - i - 1);
                i = close + 1;
            }
            else
            {
                // Up to a space or the ")" that closes the image; parentheses inside stay as pairs.
                int begin = i;
                int depth = 0;
                while (i < s.Length && !char.IsWhiteSpace(s[i]))
                {
                    if (s[i] == '(') depth++;
                    else if (s[i] == ')' && depth-- == 0) break;
                    i++;
                }
                source = s.Substring(begin, i - begin);
            }
            if (source.Trim().Length == 0) return null;

            string? title = null;
            double? width = null;
            double? height = null;
            bool sized = false;
            while (true)
            {
                int before = i;
                SkipSpaces(s, ref i);
                if (i >= s.Length) return null;
                if (s[i] == ')') break;
                if (i == before) return null;   // a title or a size follows a space
                if ((s[i] == '"' || s[i] == '\'') && title == null && !sized)
                {
                    int close = s.IndexOf(s[i], i + 1);
                    if (close < 0) return null;
                    title = s.Substring(i + 1, close - i - 1);
                    i = close + 1;
                }
                else if (s[i] == '=' && !sized && TrySize(s, ref i, out width, out height))
                {
                    sized = true;
                }
                else
                {
                    return null;
                }
            }
            return new ImageRef(start, i + 1 - start, alt.ToString(), source, title, width, height);
        }

        /// <summary>Wiki.js's <c>=WxH</c> at <paramref name="i"/>: <c>=200x</c>, <c>=x120</c>, <c>=200x120</c>, each side 1 to 99999.</summary>
        private static bool TrySize(string s, ref int i, out double? width, out double? height)
        {
            width = null;
            height = null;
            int j = i + 1;
            int w = Digits(s, ref j);
            if (j >= s.Length || s[j] != 'x') return false;
            j++;
            int h = Digits(s, ref j);
            if ((w < 0 && h < 0) || w == 0 || h == 0) return false;
            if (j < s.Length && s[j] != ')' && !IsSpace(s[j])) return false;
            if (w > 0) width = w;
            if (h > 0) height = h;
            i = j;
            return true;
        }

        /// <summary>A run of 1 to 5 digits as a number; -1 when there is none, 0 when it is longer (no size).</summary>
        private static int Digits(string s, ref int j)
        {
            int start = j;
            while (j < s.Length && s[j] is >= '0' and <= '9') j++;
            int count = j - start;
            if (count == 0) return -1;
            if (count > 5) return 0;
            return int.Parse(s.AsSpan(start, count), NumberStyles.None, CultureInfo.InvariantCulture);
        }

        private static bool IsSpace(char c) => c is ' ' or '\t';

        private static void SkipSpaces(string s, ref int i)
        {
            while (i < s.Length && IsSpace(s[i])) i++;
        }

        /// <summary>Past a backtick code span (a run closed by a run of the same length), or past the run when it never closes.</summary>
        private static int SkipCode(string s, int i)
        {
            int n = 0;
            while (i + n < s.Length && s[i + n] == '`') n++;
            int j = i + n;
            while (j < s.Length)
            {
                if (s[j] != '`')
                {
                    j++;
                    continue;
                }
                int m = 0;
                while (j + m < s.Length && s[j + m] == '`') m++;
                if (m == n) return j + m;
                j += m;
            }
            return i + n;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~ImageSourcesTests" 2>&1 | tail -5`
Expected: all pass. Then the full suite. Check escapes in `ImageSources.cs` and `ImageSourcesTests.cs` (`\u2026`, `\u2014`, `\u2192`, `\uFEFF` must stay escapes).

- [ ] **Step 5: Commit**

```bash
git add Services/Pad/ImageSources.cs tests/Kil0bitSystemMonitor.Tests/ImageSourcesTests.cs
git commit -m "feat(pad): image sources - Wiki.js =WxH, paths, data and web addresses, size limits, nothing fetched unless allowed" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 4: Image previews in the editor

**Files:**
- Modify: `Services/Pad/DiagramResult.cs`, `Services/Pad/DiagramKinds.cs`, `Pad/DiagramPicture.cs`, `Pad/EditorLanguage.cs`
- Create: `Pad/ImageServices.cs`, `Pad/ImageBoard.cs` (with `ImageRow`), `Pad/ImageGenerator.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/ImageBoardTests.cs` (with `ImageFakes`)

**Interfaces:**
- Consumes: `ImageSources`, `ImageRef`, `ImageLocation`, `ImageLoad`, `ImageText` (Task 3); `IDiagramRenderer`, `DiagramRequest`, `DiagramCache`, `DiagramResult`, `DiagramElement`, `DiagramPicture`, `DiagramView`, `DiagramPicture.BitmapOf` (existing); `MarkdownDocumentCache.FactsOf` (Plan A); `FakeRenderer`, `FakePage`, `DiagramFakes`, `FakeImageHandler`, `PadTempDir`, `UiPump`, `PadLanguageWindowTests.Pump` (tests).
- Produces:
  - `DiagramResult.Image(byte[] bytes, int pixelWidth, int pixelHeight)` (Lasting; `Png` holds the image's own bytes), `DiagramResult.PixelWidth` (0 for diagrams).
  - `DiagramEngine.Svg`; `DiagramKinds.SvgImage` (`"SVG image"`, `EngineId` `"image/svg"`, `PageKind` `"svg"`, not a fence word); `DiagramRequest.Key` ignores the theme for it.
  - `DiagramView.Width`, `Height` (`double?`), `Menu` (`bool`, default true), `WaitingText` (default `DiagramText.Drawing`); `DiagramPicture.SizeOf(DiagramView, DiagramResult, double room) : (double Width, double Height)`.
  - `internal sealed class ImageServices` — `required ImageSources Sources`, `IDiagramRenderer? Renderer`, `required Func<bool> Enabled`, `required Func<bool> WebImages`, `required Func<string?> BaseFolder`, `TimeSpan Pause` (600 ms), `DiagramCache Cache`, `Action<string> Warn`.
  - `internal sealed class ImageRow : WrapPanel` — `IReadOnlyList<DiagramPicture> Pictures`.
  - `internal sealed class ImageBoard` — `ImageBoard(TextEditor, ImageServices, Func<PadPalette>)`, `TextDocument Document`, `int Loads`, `Task Loading`, `const double Gap` (8), `Detach()`, `Refresh()`, `DrawDue()`, `WarnOnce(string)`, `UIElement RowFor(DocumentLine, IReadOnlyList<ImageRef>)`, `static DiagramResult Decode(byte[])`.
  - `internal sealed class ImageGenerator : VisualLineElementGenerator` — `ImageGenerator(MarkdownDocumentCache, ImageBoard)`.
  - `EditorLanguage.Images` (`ImageServices?`, set by the window), `EditorLanguage.ImageBoard`; `RefreshDiagrams()` also installs, removes or refreshes the previews.

- [ ] **Step 1: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/ImageBoardTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

// UseWindowsForms puts System.Windows.Forms and System.Drawing in scope; this name exists in both.
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Pictures for image tests, made on the UI thread.</summary>
    internal static class ImageFakes
    {
        /// <summary>A transparent PNG of <paramref name="width"/> x <paramref name="height"/> pixels.</summary>
        public static byte[] Png(int width, int height) =>
            Encode(new PngBitmapEncoder(), new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null));

        /// <summary>A black JPEG whose header claims <paramref name="dpi"/> (previews ignore it).</summary>
        public static byte[] Jpeg(int width, int height, double dpi = 96) =>
            Encode(new JpegBitmapEncoder(), new WriteableBitmap(width, height, dpi, dpi, PixelFormats.Bgr24, null));

        private static byte[] Encode(BitmapEncoder encoder, BitmapSource bitmap)
        {
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
    }

    /// <summary>
    /// Image previews in a Markdown editor (spec 6.3 and "Testing": an image line gets previews),
    /// over temp files, a fake web and a fake renderer. The 600 ms pause is the board's DrawDue,
    /// called by hand; its timer is set to an hour so it never fires inside a test.
    /// </summary>
    public class ImageBoardTests
    {
        private sealed class Fixture : IDisposable
        {
            public Fixture()
            {
                Folder = Dir.Root;
                Sources = new ImageSources(Handler);
                Editor = new TextEditor { Document = new TextDocument() };
                Language = new EditorLanguage(Editor, () => PadPalette.Dark, folds: true)
                {
                    Warn = Warnings.Add,
                    Images = new ImageServices
                    {
                        Sources = Sources,
                        Renderer = Renderer,
                        Enabled = () => Enabled,
                        WebImages = () => Web,
                        BaseFolder = () => Folder,
                        Pause = TimeSpan.FromHours(1),
                        Warn = Warnings.Add,
                    },
                };
            }

            public PadTempDir Dir { get; } = new();
            public FakeImageHandler Handler { get; } = new();
            public FakeRenderer Renderer { get; } = new();
            public List<string> Warnings { get; } = new();
            public ImageSources Sources { get; }
            public TextEditor Editor { get; }
            public EditorLanguage Language { get; }
            public bool Enabled { get; set; } = true;
            public bool Web { get; set; }
            public string? Folder { get; set; }

            public ImageBoard Board => Language.ImageBoard!;
            public TextView View => Editor.TextArea.TextView;

            /// <summary>A new document with <paramref name="text"/>, shown as Markdown and laid out.</summary>
            public void Show(string text)
            {
                Editor.Document = new TextDocument(text);
                Language.Apply(PadLanguages.Markdown);
                Render();
            }

            public void Render()
            {
                View.Measure(new Size(600, 400));
                View.Arrange(new Rect(0, 0, 600, 400));
                View.EnsureVisualLines();
            }

            /// <summary>Runs the redraws the dispatcher holds, then lays out again.</summary>
            public void PumpAndRender()
            {
                PadLanguageWindowTests.Pump();
                Render();
            }

            /// <summary>Waits for every load to end, then shows what they gave.</summary>
            public void Settle()
            {
                UiPump.Wait(Board.Loading);
                PumpAndRender();
            }

            public ImageRow? RowUnder(int line) =>
                View.GetVisualLine(line)?.Elements.OfType<DiagramElement>().SingleOrDefault()?.Picture as ImageRow;

            public DiagramPicture PictureUnder(int line, int index = 0) => RowUnder(line)!.Pictures[index];

            public void Dispose()
            {
                Sources.Dispose();
                Dir.Dispose();
            }
        }

        /// <summary>Pumps the UI thread until <paramref name="condition"/> holds (10 s at most).</summary>
        private static void PumpUntil(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for " + what);
                PadLanguageWindowTests.Pump();
                Thread.Sleep(5);
            }
        }

        [Fact]
        public void The_pause_after_typing_is_600_ms_as_for_diagrams()
        {
            using var sources = new ImageSources(new FakeImageHandler());
            var services = new ImageServices { Sources = sources, Enabled = () => true, WebImages = () => false, BaseFolder = () => null };

            Assert.Equal(TimeSpan.FromMilliseconds(600), services.Pause);
            Assert.Null(services.Renderer);
        }

        [Fact]
        public void A_line_with_an_image_gets_a_preview_under_it() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            File.WriteAllBytes(f.Dir.PathOf("chart.png"), ImageFakes.Png(40, 20));

            f.Show("Intro\n![chart](chart.png \"Sales\")\nafter");

            var waiting = f.PictureUnder(2);
            Assert.True(waiting.IsDrawing);
            Assert.Equal("Loading\u2026", Assert.IsType<TextBlock>(waiting.Child).Text);
            Assert.Null(f.RowUnder(1));
            Assert.Null(f.RowUnder(3));
            Assert.Equal(1, f.Board.Loads);

            f.Settle();

            var picture = f.PictureUnder(2);
            Assert.Equal(40, picture.Image!.Width);
            Assert.Equal(20, picture.Image.Height);
            Assert.Null(picture.ContextMenu);
            Assert.False(picture.View.Menu);
            Assert.Equal("Sales", picture.ToolTip);
            Assert.Equal(1, f.Board.Loads);
            Assert.Empty(f.Warnings);
        });

        [Fact]
        public void Images_on_one_line_sit_side_by_side_with_their_asked_sizes() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            File.WriteAllBytes(f.Dir.PathOf("a.png"), ImageFakes.Png(40, 20));
            File.WriteAllBytes(f.Dir.PathOf("c.jpg"), ImageFakes.Jpeg(40, 20, dpi: 72));

            f.Show("![a](a.png) ![b](a.png =80x) ![c](c.jpg) ![d](a.png =x10)");
            f.Settle();

            var row = f.RowUnder(1)!;
            Assert.Equal(576, row.MaxWidth);   // the 600 px text area less the margin diagrams use
            var sizes = row.Pictures.Select(p => (p.Image!.Width, p.Image.Height)).ToList();
            Assert.Equal(new[] { (40.0, 20.0), (80.0, 40.0), (40.0, 20.0), (20.0, 10.0) }, sizes);   // a 72 dpi JPEG still shows 40 wide
            Assert.All(row.Pictures, p => Assert.Equal(ImageBoard.Gap, p.Margin.Right));
            Assert.Equal(2, f.Board.Loads);   // a.png is read once for its three uses
        });

        [Fact]
        public void A_large_image_fits_the_width_and_is_decoded_at_that_size() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            File.WriteAllBytes(f.Dir.PathOf("wide.jpg"), ImageFakes.Jpeg(2000, 100));

            f.Show("![wide](wide.jpg)");
            f.Settle();

            var picture = f.PictureUnder(1);
            Assert.Equal(568, picture.Image!.Width);   // the row's 576 less the gap
            Assert.Equal(28.4, picture.Image.Height, 3);
            var bitmap = Assert.IsAssignableFrom<BitmapSource>(picture.Image.Source);
            Assert.Equal((int)Math.Ceiling(568 * picture.View.PixelsPerDip), bitmap.PixelWidth);
        });

        [Fact]
        public void An_image_preview_has_its_asked_size_fitted_to_the_width_and_no_menu() => UiThread.Run(() =>
        {
            var waiting = new DiagramPicture(new DiagramView { Palette = PadPalette.Dark, Menu = false, WaitingText = ImageText.Loading });
            var stretched = new DiagramPicture(new DiagramView
            {
                Result = DiagramResult.Image(DiagramFakes.Png, 1, 1),
                Palette = PadPalette.Dark,
                MaxWidth = 400,
                Width = 200,
                Height = 50,
                Menu = false,
            });
            var tooWide = new DiagramPicture(new DiagramView { Result = DiagramResult.Image(DiagramFakes.Png, 1, 1), Palette = PadPalette.Dark, MaxWidth = 100, Width = 300 });

            Assert.Equal("Loading\u2026", Assert.IsType<TextBlock>(waiting.Child).Text);
            Assert.Equal(200, stretched.Image!.Width);
            Assert.Equal(50, stretched.Image.Height);
            Assert.Equal(Stretch.Fill, stretched.Image.Stretch);
            Assert.Null(stretched.ContextMenu);
            Assert.Equal(100, tooWide.Image!.Width);   // =300x on a square image, fitted to 100
            Assert.Equal(100, tooWide.Image.Height);
            Assert.Equal(Stretch.Uniform, tooWide.Image.Stretch);
            Assert.NotNull(tooWide.ContextMenu);   // Menu is on unless a preview turns it off: diagrams keep theirs
        });

        [Fact]
        public void A_relative_path_in_a_note_and_a_missing_file_say_so() => UiThread.Run(() =>
        {
            using var f = new Fixture { Folder = null };
            string gone = f.Dir.PathOf("gone.png");

            f.Show("![a](pic.png)\n![b](<" + gone + ">)");
            Assert.Equal("A relative path needs a saved file.", f.PictureUnder(1).ErrorText!.Text);
            f.Settle();

            Assert.Equal("Image not found: " + gone, f.PictureUnder(2).ErrorText!.Text);
            Assert.Equal(1, f.Board.Loads);
        });

        [Fact]
        public void A_file_that_is_no_image_cannot_be_read() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            File.WriteAllText(f.Dir.PathOf("notes.png"), "hello");

            f.Show("![n](notes.png)");
            f.Settle();

            Assert.Equal("The image could not be read.", f.PictureUnder(1).ErrorText!.Text);
            Assert.Empty(f.Warnings);
        });

        [Fact]
        public void A_web_image_is_not_downloaded_while_web_images_are_off() => UiThread.Run(() =>
        {
            using var f = new Fixture();

            f.Show("![logo](https://example.com/logo.png)");

            Assert.Equal("Web images are off \u2014 turn them on in Settings \u2192 MicaPad.", f.PictureUnder(1).ErrorText!.Text);
            Assert.Empty(f.Handler.Requests);
            Assert.Equal(0, f.Board.Loads);

            f.Web = true;
            f.Language.RefreshDiagrams();   // Settings → MicaPad → Load images from the web
            f.PumpAndRender();
            f.Settle();

            Assert.Equal(1, f.PictureUnder(1).Image!.Width);
            Assert.Equal("https://example.com/logo.png", Assert.Single(f.Handler.Requests).Uri.AbsoluteUri);

            f.Language.RefreshDiagrams();   // another settings change: a web image that loaded is not fetched again
            f.PumpAndRender();
            f.Settle();
            Assert.Single(f.Handler.Requests);
        });

        [Fact]
        public void An_svg_image_is_drawn_by_the_diagram_page_as_it_is() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            string svg = "<?xml version=\"1.0\"?>\n" + DiagramFakes.Svg;
            File.WriteAllText(f.Dir.PathOf("logo.svg"), "\uFEFF" + svg);

            f.Show("![logo](logo.svg)");
            PumpUntil(() => f.Renderer.Calls.Count == 1, "the SVG to reach the renderer");

            var request = f.Renderer.Calls[0].Request;
            Assert.Same(DiagramKinds.SvgImage, request.Kind);
            Assert.Equal("svg", request.Kind.PageKind);
            Assert.Equal(svg, request.Source);   // without the byte order mark
            Assert.Null(request.KrokiServer);

            f.Renderer.Finish(0, DiagramFakes.Picture(100, 50));
            f.Settle();
            Assert.Equal(100, f.PictureUnder(1).Image!.Width);
        });

        [Fact]
        public void The_renderer_draws_an_svg_image_on_the_page_without_a_card()
        {
            var page = new FakePage { Answer = _ => DiagramFakes.Drawn() };
            using var renderer = new DiagramRenderer(() => Task.FromResult<IDiagramPage>(page));
            var request = new DiagramRequest(DiagramKinds.SvgImage, DiagramFakes.Svg, PadThemes.Dark, "#EDEDF2", "#0E0E13", null);

            var task = renderer.RenderAsync(request, new object());
            Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "the draw did not finish");

            Assert.True(task.Result.IsPicture);
            Assert.False(task.Result.Paper);
            Assert.Equal("svg", Assert.Single(page.Requests).Kind);
            Assert.Equal("image/svg", DiagramKinds.SvgImage.EngineId);
            Assert.Equal(request.Key, (request with { Theme = PadThemes.Light }).Key);   // the same picture in both themes
            Assert.NotEqual(request.Key, (request with { Source = "<svg/>" }).Key);
            Assert.DoesNotContain(DiagramKinds.Words, w => ReferenceEquals(DiagramKinds.FromWord(w), DiagramKinds.SvgImage));
        }

        [Fact]
        public void Typing_on_an_image_line_loads_nothing_until_the_pause_and_keeps_the_old_preview() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            File.WriteAllBytes(f.Dir.PathOf("a.png"), ImageFakes.Png(40, 20));
            File.WriteAllBytes(f.Dir.PathOf("b.png"), ImageFakes.Png(80, 20));
            f.Show("![pic](a.png)");
            f.Settle();
            var document = f.Editor.Document;

            document.Replace(document.Text.IndexOf("a.png", StringComparison.Ordinal), 1, "b");   // now b.png
            f.PumpAndRender();

            Assert.Equal(1, f.Board.Loads);                       // nothing while typing
            Assert.Equal(40, f.PictureUnder(1).Image!.Width);      // the old preview stays

            f.Board.DrawDue();                                     // typing paused
            f.PumpAndRender();
            Assert.Equal(2, f.Board.Loads);
            f.Settle();
            Assert.Equal(80, f.PictureUnder(1).Image!.Width);
        });

        [Fact]
        public void Fenced_and_code_images_and_other_languages_get_no_preview() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            File.WriteAllBytes(f.Dir.PathOf("a.png"), ImageFakes.Png(40, 20));

            f.Show("```\n![a](a.png)\n```\n`![b](a.png)` and \\![c](a.png)");

            Assert.Null(f.RowUnder(2));
            Assert.Null(f.RowUnder(4));
            Assert.Equal(0, f.Board.Loads);

            f.Language.Apply(PadLanguages.ById("json")!);
            Assert.Null(f.Language.ImageBoard);
            Assert.Empty(f.View.ElementGenerators.OfType<ImageGenerator>());

            f.Language.Apply(PadLanguages.Markdown);
            Assert.Single(f.View.ElementGenerators.OfType<ImageGenerator>());

            f.Enabled = false;   // Settings → MicaPad → Draw diagrams off
            f.Language.RefreshDiagrams();
            Assert.Null(f.Language.ImageBoard);
            Assert.Empty(f.View.ElementGenerators.OfType<ImageGenerator>());
        });

        [Fact]
        public void A_tab_shown_again_shows_its_images_at_once_and_reads_files_once_more() => UiThread.Run(() =>
        {
            using var f = new Fixture();
            File.WriteAllBytes(f.Dir.PathOf("a.png"), ImageFakes.Png(40, 20));
            f.Show("![a](a.png)");
            f.Settle();
            var first = f.Editor.Document;
            File.WriteAllBytes(f.Dir.PathOf("a.png"), ImageFakes.Png(60, 20));   // changed while the tab is away

            f.Show("other text");
            f.Editor.Document = first;
            f.Language.Apply(PadLanguages.Markdown);
            f.Render();

            Assert.False(f.PictureUnder(1).IsDrawing);
            Assert.Equal(40, f.PictureUnder(1).Image!.Width);   // at once, from the window's cache
            Assert.Equal(1, f.Board.Loads);                      // and read once more
            f.Settle();
            Assert.Equal(60, f.PictureUnder(1).Image!.Width);
        });
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~ImageBoardTests" 2>&1 | tail -5`
Expected: build error, `ImageServices` does not exist.

- [ ] **Step 3: Results, kinds and the picture**

In `Services/Pad/DiagramResult.cs`:

1. Replace

```csharp
        public byte[]? Png { get; private init; }
```

with

```csharp
        /// <summary>The picture: a PNG at twice a diagram's size, or an image preview's own bytes (any format Windows decodes).</summary>
        public byte[]? Png { get; private init; }

        /// <summary>An image preview's width in pixels; 0 for a diagram, whose PNG header says it.</summary>
        public int PixelWidth { get; private init; }
```

2. Insert before `        public static DiagramResult Failure(string error, bool lasting, Uri? helpLink = null) =>`:

```csharp
        /// <summary>An image preview (Markdown spec 6.3): one device-independent pixel per image pixel (Markdown ruling R10).</summary>
        public static DiagramResult Image(byte[] bytes, int pixelWidth, int pixelHeight) =>
            new() { Png = bytes, Width = pixelWidth, Height = pixelHeight, PixelWidth = pixelWidth, Lasting = true };

```

3. Replace

```csharp
        /// <summary>
        /// The cache key (spec 2.4): engine, Kroki server, theme and source. A Kroki picture is the
        /// same in both themes (it sits on a light card), so its theme part is "paper" (R3).
        /// </summary>
        public string Key => DiagramCacheKey.Of(Kind.EngineId, Kind.NeedsKroki ? KrokiServer : null, Kind.NeedsKroki ? "paper" : Theme, Source);
```

with

```csharp
        /// <summary>
        /// The cache key (spec 2.4): engine, Kroki server, theme and source. A Kroki picture is the
        /// same in both themes (it sits on a light card), and so is an SVG image (drawn as it is), so
        /// their theme part is "paper" (R3).
        /// </summary>
        public string Key => DiagramCacheKey.Of(Kind.EngineId, Kind.NeedsKroki ? KrokiServer : null,
                                                Kind.NeedsKroki || Kind.Engine == DiagramEngine.Svg ? "paper" : Theme, Source);
```

In `Services/Pad/DiagramKinds.cs`:

1. In `enum DiagramEngine`, after the `Math,` member add:

```csharp

        /// <summary>An SVG image from a note (spec 6.3): the page only turns it into a PNG, as it does Kroki's pictures.</summary>
        Svg,
```

2. In `EngineId`, insert before `            _ => "kroki/" + KrokiType,`:

```csharp
            DiagramEngine.Svg => "image/svg",
```

3. After the `KrokiForm` property add:

```csharp

        /// <summary>An SVG image (Markdown spec 6.3 and ruling R11). Not a fence word: image previews ask for it.</summary>
        public static DiagramKind SvgImage { get; } = new("SVG image", DiagramEngine.Svg, null);
```

In `Pad/DiagramPicture.cs`:

1. Replace

```csharp
        public Action<Uri>? OpenLink { get; init; }
    }
```

with

```csharp
        public Action<Uri>? OpenLink { get; init; }

        /// <summary>The width an image asks for (<c>=200x</c>, device-independent pixels), or null; diagrams leave it null.</summary>
        public double? Width { get; init; }

        /// <summary>The height an image asks for (<c>=x120</c>), or null.</summary>
        public double? Height { get; init; }

        /// <summary>False for image previews: no right-click menu, and the editor's own does not open over them (spec 6.3).</summary>
        public bool Menu { get; init; } = true;

        /// <summary>What shows until the first result: "Drawing..." for a diagram, "Loading..." for an image.</summary>
        public string WaitingText { get; init; } = DiagramText.Drawing;
    }
```

2. Replace

```csharp
            if (view.Result == null) Child = DrawingText(view.Palette);
```

with

```csharp
            if (!view.Menu) ContextMenuOpening += (s, e) => e.Handled = true;
            if (view.Result == null) Child = DrawingText(view.WaitingText, view.Palette);
```

3. Replace

```csharp
        private static int DecodeWidthOf(byte[] png, double shownWidth, double pixelsPerDip)
        {
            int pngWidth = PngWidth(png);
            if (pngWidth == 0) return 0;
            double wanted = Math.Ceiling(shownWidth * (pixelsPerDip > 0 ? pixelsPerDip : 1));
            return (int)Math.Clamp(wanted, 1, pngWidth);
        }
```

with

```csharp
        private static int DecodeWidthOf(DiagramResult result, double shownWidth, double pixelsPerDip)
        {
            // An image preview knows its pixel width (any format); a diagram's PNG header says its own.
            int fullWidth = result.PixelWidth > 0 ? result.PixelWidth : PngWidth(result.Png!);
            if (fullWidth == 0) return 0;
            double wanted = Math.Ceiling(shownWidth * (pixelsPerDip > 0 ? pixelsPerDip : 1));
            return (int)Math.Clamp(wanted, 1, fullWidth);
        }
```

4. Replace

```csharp
            return width > 0 ? width : 0;
        }
```

with

```csharp
            return width > 0 ? width : 0;
        }

        /// <summary>
        /// The size a picture is shown at: its natural size, or for an image the <c>=WxH</c> it asks
        /// for (one side alone keeps the proportions; Markdown ruling R10); then scaled down to
        /// <paramref name="room"/> when wider, never up.
        /// </summary>
        internal static (double Width, double Height) SizeOf(DiagramView view, DiagramResult result, double room)
        {
            if (view.Width == null && view.Height == null)
            {
                double fitted = Math.Max(1, Math.Min(result.Width, room));
                return (fitted, fitted * result.Height / result.Width);
            }
            double width = view.Width ?? result.Width * view.Height!.Value / result.Height;
            double height = view.Height ?? result.Height * view.Width!.Value / result.Width;
            if (width > room)
            {
                height = height * room / width;
                width = room;
            }
            return (Math.Max(1, width), Math.Max(1, height));
        }
```

5. Replace

```csharp
        private static UIElement DrawingText(PadPalette palette) => new TextBlock
        {
            Text = DiagramText.Drawing,
```

with

```csharp
        private static UIElement DrawingText(string text, PadPalette palette) => new TextBlock
        {
            Text = text,
```

6. Replace

```csharp
            double room = view.MaxWidth - (result.Paper ? 2 * PaperPadding : 0);
            double width = Math.Max(1, Math.Min(result.Width, room));
            Image = new Image
            {
                Source = BitmapOf(result, DecodeWidthOf(result.Png!, width, view.PixelsPerDip)),
                Width = width,
                Height = width * result.Height / result.Width,
                Stretch = Stretch.Uniform,
            };
```

with

```csharp
            double room = view.MaxWidth - (result.Paper ? 2 * PaperPadding : 0);
            var (width, height) = SizeOf(view, result, room);
            Image = new Image
            {
                Source = BitmapOf(result, DecodeWidthOf(result, width, view.PixelsPerDip)),
                Width = width,
                Height = height,
                // An image's =WxH with both sides is drawn at that size, as a browser does.
                Stretch = view.Width != null && view.Height != null ? Stretch.Fill : Stretch.Uniform,
            };
```

7. Replace

```csharp
            var menu = new ContextMenu();
```

with

```csharp
            if (!view.Menu) return grid;   // image previews have no menu (spec 6.3)

            var menu = new ContextMenu();
```

- [ ] **Step 4: The services, the board and the generator**

`Pad/ImageServices.cs`:

```csharp
using System;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>What the image previews of an editor need from the window and the app (spec 6.3). Tests pass fakes.</summary>
    internal sealed class ImageServices
    {
        /// <summary>Loads images; the app shares one between windows.</summary>
        public required ImageSources Sources { get; init; }

        /// <summary>Draws SVG images (the diagram page); null: an SVG image cannot be read.</summary>
        public IDiagramRenderer? Renderer { get; init; }

        /// <summary>True while Settings → MicaPad → Draw diagrams is on (R6).</summary>
        public required Func<bool> Enabled { get; init; }

        /// <summary>True while Settings → MicaPad → Load images from the web is on.</summary>
        public required Func<bool> WebImages { get; init; }

        /// <summary>The shown tab's file folder, where relative paths start; null for a note.</summary>
        public required Func<string?> BaseFolder { get; init; }

        /// <summary>How long typing must pause before an image is loaded (the diagrams' 600 ms). Tests lengthen it.</summary>
        public TimeSpan Pause { get; init; } = TimeSpan.FromMilliseconds(600);

        /// <summary>Loaded images by source (64, memory only), shared by the window's tabs (R13).</summary>
        public DiagramCache Cache { get; init; } = new();

        /// <summary>Logs a warning; it names an exception type, never an image's path or address.</summary>
        public Action<string> Warn { get; init; } = _ => { };
    }
}
```

`Pad/ImageBoard.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Services.Pad;

// UseWindowsForms puts System.Windows.Forms and System.Drawing in scope; this name exists in both.
using Orientation = System.Windows.Controls.Orientation;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>The previews under one line (spec 6.3, R9): one picture per image, side by side while they fit, wrapping otherwise.</summary>
    internal sealed class ImageRow : WrapPanel
    {
        public ImageRow(double maxWidth)
        {
            Orientation = Orientation.Horizontal;
            MaxWidth = maxWidth;
        }

        /// <summary>The previews, in the order of the images on the line.</summary>
        internal IReadOnlyList<DiagramPicture> Pictures => Children.OfType<DiagramPicture>().ToList();
    }

    /// <summary>
    /// The image previews of one Markdown document in one editor (spec 6.3): each image's load, kept
    /// by its resolved source; the pause after typing; and redrawing the lines that hold previews
    /// when a load ends or the width changes. Files and downloads are read off the UI thread and
    /// nothing here waits on them. Results go to the window's cache, so a tab shown again shows its
    /// images at once; a file is then read once more, a web or data image is not (R13).
    /// </summary>
    internal sealed class ImageBoard
    {
        /// <summary>The space right of each preview, so previews side by side do not touch.</summary>
        internal const double Gap = 8;

        private const double MinRowWidth = 120;
        private const double RowMargin = 24;
        private static readonly TimeSpan ResizePause = TimeSpan.FromMilliseconds(200);

        private readonly TextEditor _editor;
        private readonly ImageServices _services;
        private readonly Func<PadPalette> _palette;
        private readonly TextDocument _document;
        private readonly DispatcherTimer _pauseTimer;
        private readonly DispatcherTimer _resizeTimer;
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly ConditionalWeakTable<DocumentLine, DiagramResult?[]> _lastShown = new();
        private readonly List<Task> _running = new();
        private bool _attached = true;
        private bool _editing;
        private bool _warned;
        private int _generation;

        public ImageBoard(TextEditor editor, ImageServices services, Func<PadPalette> palette)
        {
            _editor = editor;
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
                RedrawRows();
            };

            _document.Changed += OnChanged;
            _editor.TextArea.TextView.SizeChanged += OnViewSizeChanged;
        }

        /// <summary>The document the previews belong to.</summary>
        internal TextDocument Document => _document;

        /// <summary>How many loads this board started; for tests.</summary>
        internal int Loads { get; private set; }

        /// <summary>Ends when every load started so far has ended; for tests.</summary>
        internal Task Loading => Task.WhenAll(_running.ToArray());

        /// <summary>Stops following the document; a load that ends later changes nothing on screen.</summary>
        public void Detach()
        {
            if (!_attached) return;
            _attached = false;
            _pauseTimer.Stop();
            _resizeTimer.Stop();
            _document.Changed -= OnChanged;
            _editor.TextArea.TextView.SizeChanged -= OnViewSizeChanged;
        }

        /// <summary>A setting changed: every preview is asked for again; files are read again, failed downloads tried again (R13).</summary>
        public void Refresh()
        {
            _generation++;
            _editor.TextArea.TextView.Redraw();
        }

        /// <summary>Typing paused: the previews in view load their new sources. The pause timer calls it; so do tests.</summary>
        internal void DrawDue()
        {
            _editing = false;
            RedrawRows();
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

        /// <summary>
        /// The row under <paramref name="line"/>: for each image its picture, its error, or
        /// "Loading..." (R15). Starts the loads that are due: not while typing, and not again for a
        /// source this board already loaded.
        /// </summary>
        internal UIElement RowFor(DocumentLine line, IReadOnlyList<ImageRef> images)
        {
            var palette = _palette();
            string? folder = _services.BaseFolder();
            bool web = _services.WebImages();
            var view = _editor.TextArea.TextView;
            double width = ((IScrollInfo)view).ViewportWidth;
            if (!(width > 0)) width = view.ActualWidth;
            double room = Math.Max(MinRowWidth, width - RowMargin);
            double pixelsPerDip = VisualTreeHelper.GetDpi(view).PixelsPerDip;
            var before = _lastShown.TryGetValue(line, out var last) ? last : Array.Empty<DiagramResult?>();
            var shown = new DiagramResult?[images.Count];

            var row = new ImageRow(room);
            for (int i = 0; i < images.Count; i++)
            {
                var image = images[i];
                var location = ImageSources.Resolve(image.Source, folder, web);
                var result = location.Error != null ? DiagramResult.Failure(location.Error, lasting: true) : ResultOf(location);
                // While a new source waits for the pause or its load, the line's previous preview stays.
                shown[i] = result ?? (i < before.Length ? before[i] : null);
                var picture = new DiagramPicture(new DiagramView
                {
                    Result = shown[i],
                    Palette = palette,
                    MaxWidth = Math.Max(1, room - Gap),
                    PixelsPerDip = pixelsPerDip,
                    Width = image.Width,
                    Height = image.Height,
                    Menu = false,
                    WaitingText = ImageText.Loading,
                });
                picture.Margin = new Thickness(0, 4, Gap, 8);
                if (!string.IsNullOrEmpty(image.Title)) picture.ToolTip = image.Title;
                row.Children.Add(picture);
            }
            _lastShown.AddOrUpdate(line, shown);
            return row;
        }

        /// <summary>
        /// A raster image as a preview result (R11): its pixel size read from its header; "The image
        /// could not be read." when Windows has no codec for it or it is no image.
        /// </summary>
        internal static DiagramResult Decode(byte[] bytes)
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                if (frame.PixelWidth > 0 && frame.PixelHeight > 0) return DiagramResult.Image(bytes, frame.PixelWidth, frame.PixelHeight);
            }
            catch (Exception ex) when (ex is NotSupportedException or FormatException or IOException or ArgumentException
                                           or InvalidOperationException or COMException or OverflowException)
            {
                // No codec, or not an image.
            }
            return DiagramResult.Failure(ImageText.CouldNotRead, lasting: true);
        }

        /// <summary>What this board shows for <paramref name="location"/>; starts its load when one is due.</summary>
        private DiagramResult? ResultOf(ImageLocation location)
        {
            if (!_entries.TryGetValue(location.Key, out var entry))
            {
                entry = new Entry();
                _entries[location.Key] = entry;
            }
            if (entry.Shown == null && _services.Cache.TryGet(location.Key, out var cached))
            {
                entry.Shown = cached;
                if (location.Origin != ImageOrigin.File)
                {
                    entry.Settled = true;
                    entry.SettledGeneration = _generation;
                }
            }
            // A file is read again once per board and after a setting changes; a web or data image that loaded is not.
            bool settled = entry.Settled
                           && (entry.SettledGeneration == _generation || (location.Origin != ImageOrigin.File && entry.Shown is { Lasting: true }));
            if (!_editing && !settled && !entry.Pending) Load(entry, location);
            return entry.Shown;
        }

        private void Load(Entry entry, ImageLocation location)
        {
            entry.Pending = true;
            Loads++;
            var run = new Run();
            var task = LoadAsync(entry, location, run);
            // A load that ended already (a data address) is read by the caller right after this;
            // one that ends later redraws its lines.
            run.Returned = true;
            _running.RemoveAll(t => t.IsCompleted);
            if (!task.IsCompleted) _running.Add(task);
        }

        private async Task LoadAsync(Entry entry, ImageLocation location, Run run)
        {
            int generation = _generation;
            DiagramResult result;
            try
            {
                var loaded = await _services.Sources.LoadAsync(location);
                if (loaded.Bytes == null) result = DiagramResult.Failure(loaded.Error ?? ImageText.CouldNotRead, loaded.Lasting);
                else if (loaded.Svg) result = await DrawSvgAsync(loaded.Bytes, entry);
                else result = Decode(loaded.Bytes);
            }
            catch (Exception ex)
            {
                WarnOnce("Image previews failed (" + ex.GetType().Name + ")");
                result = DiagramResult.Failure(ImageText.CouldNotRead, lasting: false);
            }

            entry.Pending = false;
            if (result.IsReplaced) return;
            entry.Shown = result;
            entry.Settled = true;
            entry.SettledGeneration = generation;
            if (result.Lasting) _services.Cache.Add(location.Key, result);
            if (run.Returned && _attached && ReferenceEquals(_editor.Document, _document)) RedrawRows();
        }

        /// <summary>An SVG image through the diagram page (R11): turned into a PNG as it is, in both themes.</summary>
        private async Task<DiagramResult> DrawSvgAsync(byte[] bytes, Entry entry)
        {
            if (_services.Renderer is not { } renderer) return DiagramResult.Failure(ImageText.CouldNotRead, lasting: false);
            var palette = _palette();
            string svg = Encoding.UTF8.GetString(bytes).TrimStart((char)0xFEFF);
            var request = new DiagramRequest(DiagramKinds.SvgImage, svg, palette.Name,
                                             DiagramRequest.Css(palette.Text), DiagramRequest.Css(palette.Background), KrokiServer: null);
            var drawn = renderer.TryGetCached(request.Key, out var cached) ? cached : await renderer.RenderAsync(request, entry);
            // The page's own failure names a picture; an image says image. A missing WebView2 keeps its message and link.
            return drawn.IsPicture || drawn.IsReplaced || drawn.HelpLink != null
                ? drawn
                : DiagramResult.Failure(ImageText.CouldNotRead, drawn.Lasting);
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

        /// <summary>Builds again the lines in view that hold previews, so each fits the width and shows its latest result.</summary>
        private void RedrawRows()
        {
            if (!_attached) return;
            var view = _editor.TextArea.TextView;
            if (!view.VisualLinesValid) return;
            foreach (var line in view.VisualLines.ToList())
                if (line.Elements.OfType<DiagramElement>().Any(e => e.Picture is ImageRow)) view.Redraw(line, DispatcherPriority.Normal);
        }

        /// <summary>One source's preview in this board: the result shown, a load running, and whether its load ended (in which settings generation).</summary>
        private sealed class Entry
        {
            public DiagramResult? Shown;
            public bool Pending;
            public bool Settled;
            public int SettledGeneration;
        }

        /// <summary>Set once <see cref="Load"/> has returned: a load ending after that redraws its lines.</summary>
        private sealed class Run
        {
            public bool Returned;
        }
    }
}
```

`Pad/ImageGenerator.cs`:

```csharp
using System;
using System.Collections.Generic;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Puts the image previews of a line (an <see cref="ImageRow"/> in a <see cref="DiagramElement"/>)
    /// at the end of each line with images outside fences and front matter (spec 6.3, R9). Only a line
    /// that starts its visual line gets them, so lines folded away show none. A failure is logged once
    /// by the board and that line simply gets no previews.
    /// </summary>
    internal sealed class ImageGenerator : VisualLineElementGenerator
    {
        private readonly MarkdownDocumentCache _cache;
        private readonly ImageBoard _board;
        private DocumentLine? _line;
        private IReadOnlyList<ImageRef> _images = Array.Empty<ImageRef>();

        public ImageGenerator(MarkdownDocumentCache cache, ImageBoard board)
        {
            _cache = cache;
            _board = board;
        }

        public override void StartGeneration(ITextRunConstructionContext context)
        {
            base.StartGeneration(context);
            _line = null;
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            try
            {
                var document = CurrentContext.Document;
                if (!ReferenceEquals(document, _board.Document)) return -1;
                var line = CurrentContext.VisualLine.FirstDocumentLine;
                if (line.EndOffset < startOffset) return -1;
                return ImagesOf(document, line).Count > 0 ? line.EndOffset : -1;
            }
            catch (Exception ex)
            {
                _board.WarnOnce("Image previews failed (" + ex.GetType().Name + ")");
                return -1;
            }
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            try
            {
                var document = CurrentContext.Document;
                var line = CurrentContext.VisualLine.FirstDocumentLine;
                if (line.EndOffset != offset) return null;
                var images = ImagesOf(document, line);
                return images.Count == 0 ? null : new DiagramElement(_board.RowFor(line, images));
            }
            catch (Exception ex)
            {
                _board.WarnOnce("Image previews failed (" + ex.GetType().Name + ")");
                return null;
            }
        }

        /// <summary>The images of <paramref name="line"/>, found once per visual line.</summary>
        private IReadOnlyList<ImageRef> ImagesOf(TextDocument document, DocumentLine line)
        {
            if (ReferenceEquals(line, _line)) return _images;
            _line = line;
            _images = Array.Empty<ImageRef>();
            if (line.Length > ImageSources.MaxLineLength) return _images;
            var facts = _cache.FactsOf(document, line.LineNumber);
            if (facts.Fence == MdFence.None && !facts.FrontMatter) _images = ImageSources.Find(document.GetText(line));
            return _images;
        }
    }
}
```

In `Pad/EditorLanguage.cs`:

1. In the class summary, change `Markdown formatting (colorizer, background, bullets, emoji, diagram pictures), and folding.` to `Markdown formatting (colorizer, background, bullets, emoji, diagram pictures, image previews), and folding.`
2. After `private DiagramGenerator? _diagramGenerator;` add:

```csharp
        private ImageBoard? _imageBoard;
        private ImageGenerator? _imageGenerator;
```

3. After `internal DiagramBoard? DiagramBoard => _diagramBoard;` add:

```csharp

        /// <summary>What image previews need (spec 6.3); set by the window. Null: no previews (the history preview).</summary>
        internal ImageServices? Images { get; set; }

        /// <summary>The image previews of the shown Markdown document, or null.</summary>
        internal ImageBoard? ImageBoard => _imageBoard;
```

4. In `Apply`, replace

```csharp
                view.ElementGenerators.Add(_emoji);
                InstallDiagrams();
```

with

```csharp
                view.ElementGenerators.Add(_emoji);
                InstallDiagrams();
                InstallImages();
```

5. Replace the whole `RefreshDiagrams` method (its summary included):

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
```

with

```csharp
        /// <summary>
        /// Draw diagrams, Kroki, the Kroki server or Load images from the web changed in Settings:
        /// puts the pictures and image previews in or takes them out, or asks every one again,
        /// without touching the rest of the formatting.
        /// </summary>
        internal void RefreshDiagrams()
        {
            if (_markdown == null) return;
            bool foldsChanged = false;
            if (!(Diagrams != null && Diagrams.Enabled()))
            {
                foldsChanged = _diagramBoard != null;
                RemoveDiagrams();
            }
            else if (_diagramBoard == null)
            {
                InstallDiagrams();
                foldsChanged = true;
            }
            else
            {
                _diagramBoard.Refresh();
            }

            if (!(Images != null && Images.Enabled())) RemoveImages();
            else if (_imageBoard == null) InstallImages();
            else _imageBoard.Refresh();

            if (foldsChanged) _folding?.Update();
            Redraw();
        }
```

6. After the `RemoveDiagrams` method add:

```csharp

        private void InstallImages()
        {
            if (Images is not { } services || !services.Enabled() || _markdownCache == null) return;
            _imageBoard = new ImageBoard(_editor, services, _palette);
            _imageGenerator = new ImageGenerator(_markdownCache, _imageBoard);
            _editor.TextArea.TextView.ElementGenerators.Add(_imageGenerator);
        }

        private void RemoveImages()
        {
            if (_imageBoard == null) return;
            _editor.TextArea.TextView.ElementGenerators.Remove(_imageGenerator!);
            _imageBoard.Detach();
            _imageBoard = null;
            _imageGenerator = null;
        }
```

7. In `Clear`, replace

```csharp
                RemoveDiagrams();
                view.LineTransformers.Remove(_markdown);
```

with

```csharp
                RemoveDiagrams();
                RemoveImages();
                view.LineTransformers.Remove(_markdown);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~ImageBoardTests|FullyQualifiedName~DiagramPictureTests|FullyQualifiedName~DiagramBoardTests|FullyQualifiedName~DiagramRendererTests|FullyQualifiedName~DiagramWindowTests|FullyQualifiedName~MarkdownRenderingTests|FullyQualifiedName~EmojiTests" 2>&1 | tail -5`
Expected: all pass. Then the full suite. Check escapes in `ImageBoardTests.cs` (`\u2026`, `\u2014`, `\u2192`, `\uFEFF` stay escapes).

- [ ] **Step 6: Commit**

```bash
git add Services/Pad/DiagramResult.cs Services/Pad/DiagramKinds.cs Pad/DiagramPicture.cs Pad/ImageServices.cs Pad/ImageBoard.cs Pad/ImageGenerator.cs Pad/EditorLanguage.cs tests/Kil0bitSystemMonitor.Tests/ImageBoardTests.cs
git commit -m "feat(pad): image previews under their line, side by side, with Wiki.js sizes; SVG through the diagram page" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 5: The window, the app and the Settings cards

**Files:**
- Modify: `Models/SystemMetrics.cs` (`PadWebImages`), `Pad/MicaPadWindow.Diagrams.cs`, `Pad/MicaPadWindow.xaml.cs` (`OnConfigChanged`), `App.xaml.cs`, `SettingsWindow.xaml`, `SettingsWindow.xaml.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/ImageWindowTests.cs` (new), `tests/Kil0bitSystemMonitor.Tests/PadConfigTests.cs` (one test added)

**Interfaces:**
- Consumes: `ImageServices`, `ImageBoard`, `ImageRow`, `ImageGenerator`, `EditorLanguage.Images`/`ImageBoard`/`RefreshDiagrams` (Task 4); `ImageSources` (Task 3); `MicaPadWindow._shown`, `OpenNote.Meta.SourcePath`, `MicaPadWindow.LanguageView`, `PreviewEditor`, `PadLanguageWindowTests.OpenFile/Render/Pump`, `PadWindowTests.RepoRoot()` (existing).
- Produces:
  - `AppConfig.PadWebImages` (bool, default false).
  - `internal static ImageSources? MicaPadWindow.ImageLoader` — set by the app before the first window; null (most tests) means no previews.
  - The note editor's `ImageServices`: `Enabled` = `PadDiagrams`, `WebImages` = `PadWebImages`, `BaseFolder` = the shown tab's file folder (null for a note), `Renderer` = `MicaPadWindow.DiagramRenderer`.
  - Settings toggles `PadReadingFontToggle` (`PadReadingFont`) and `PadWebImagesToggle` (`PadWebImages`).

- [ ] **Step 1: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/ImageWindowTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Image previews in the real MicaPad window (spec 6.3) and the two Settings cards (spec 7), built on the UI thread and never shown.</summary>
    public class ImageWindowTests
    {
        private static void WithImageWindow(Action<MicaPadWindow, PadTestEnv, AppConfig, FakeImageHandler> test) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var config = new AppConfig();
            var handler = new FakeImageHandler();
            using var sources = new ImageSources(handler);
            var before = MicaPadWindow.ImageLoader;
            MicaPadWindow.ImageLoader = sources;
            MicaPadWindow? window = null;
            try
            {
                window = new MicaPadWindow(env.Workspace, config);
                window.LoadSession();
                test(window, env, config, handler);
            }
            finally
            {
                window?.CloseForExit();
                MicaPadWindow.ImageLoader = before;
            }
        });

        /// <summary>Runs what is queued, lays the editor out and returns the previews under line <paramref name="line"/>, or null.</summary>
        private static ImageRow? RowUnder(MicaPadWindow window, int line)
        {
            PadLanguageWindowTests.Pump();
            PadLanguageWindowTests.Render(window);
            return window.Editor.TextArea.TextView.GetVisualLine(line)?.Elements.OfType<DiagramElement>().SingleOrDefault()?.Picture as ImageRow;
        }

        [Fact]
        public void A_relative_image_needs_a_saved_file_and_resolves_beside_one() => WithImageWindow((window, env, config, handler) =>
        {
            window.Editor.Document.Text = "![chart](chart.png)";
            Assert.Equal("A relative path needs a saved file.", RowUnder(window, 1)!.Pictures[0].ErrorText!.Text);

            File.WriteAllBytes(env.FileOf("chart.png"), DiagramFakes.Png);
            PadLanguageWindowTests.OpenFile(window, env, "doc.md", "# Doc\n![chart](chart.png)");
            window.LanguageView.ImageBoard!.DrawDue();
            RowUnder(window, 2);
            UiPump.Wait(window.LanguageView.ImageBoard!.Loading);

            Assert.Equal(1, RowUnder(window, 2)!.Pictures[0].Image!.Width);
            Assert.Empty(window.PreviewEditor.TextArea.TextView.ElementGenerators.OfType<ImageGenerator>());
        });

        [Fact]
        public void Load_images_from_the_web_and_draw_diagrams_follow_the_settings() => WithImageWindow((window, env, config, handler) =>
        {
            window.Editor.Document.Text = "![logo](https://example.com/logo.png)";
            Assert.Equal("Web images are off \u2014 turn them on in Settings \u2192 MicaPad.", RowUnder(window, 1)!.Pictures[0].ErrorText!.Text);
            Assert.Empty(handler.Requests);

            config.PadWebImages = true;
            window.LanguageView.ImageBoard!.DrawDue();   // as if typing had paused
            RowUnder(window, 1);
            UiPump.Wait(window.LanguageView.ImageBoard!.Loading);

            Assert.Equal(1, RowUnder(window, 1)!.Pictures[0].Image!.Width);
            Assert.Equal("https://example.com/logo.png", Assert.Single(handler.Requests).Uri.AbsoluteUri);

            config.PadDiagrams = false;
            Assert.Null(RowUnder(window, 1));
            Assert.Null(window.LanguageView.ImageBoard);
        });

        [Fact]
        public void Settings_has_the_reading_font_and_web_image_cards()
        {
            string xaml = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "SettingsWindow.xaml"));
            string code = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "SettingsWindow.xaml.cs"));

            Assert.Contains("x:Name=\"PadReadingFontToggle\"", xaml);
            Assert.Contains("Text=\"Reading font\"", xaml);
            Assert.Contains("Text=\"Prose in Markdown notes uses Segoe UI; code and tables stay in the editor font.\"", xaml);
            Assert.Contains("x:Name=\"PadWebImagesToggle\"", xaml);
            Assert.Contains("Text=\"Load images from the web\"", xaml);
            Assert.Contains("Text=\"Images with an http or https address are downloaded when the note is shown. Off: only images on this PC are shown.\"", xaml);
            Assert.Contains("markmap and math block in Markdown notes, drawn on this PC, and a preview under each image.", xaml);
            Assert.Contains("cfg.PadReadingFont = PadReadingFontToggle.IsOn;", code);
            Assert.Contains("cfg.PadWebImages = PadWebImagesToggle.IsOn;", code);
            Assert.Contains("PadReadingFontToggle.IsOn = cfg.PadReadingFont;", code);
            Assert.Contains("PadWebImagesToggle.IsOn = cfg.PadWebImages;", code);
        }

        [Fact]
        public void The_app_gives_micapad_its_image_loader_and_disposes_it()
        {
            string app = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "App.xaml.cs"));

            Assert.Contains("Kil0bitSystemMonitor.Pad.MicaPadWindow.ImageLoader = s_images;", app);
            Assert.Contains("s_images?.Dispose();", app);
        }
    }
}
```

In `tests/Kil0bitSystemMonitor.Tests/PadConfigTests.cs`, add after `The_reading_font_is_on_by_default_and_survives_a_round_trip`:

```csharp
        [Fact]
        public void Web_images_are_off_by_default_and_survive_a_round_trip()
        {
            Assert.False(new AppConfig().PadWebImages);
            Assert.False(JsonSerializer.Deserialize<AppConfig>("{\"PadMarkdown\": true}")!.PadWebImages);
            Assert.True(JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig { PadWebImages = true }))!.PadWebImages);
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~ImageWindowTests|FullyQualifiedName~PadConfigTests" 2>&1 | tail -5`
Expected: build error, `MicaPadWindow` has no `ImageLoader` (and `AppConfig` no `PadWebImages`).

- [ ] **Step 3: The setting, the window and the app**

In `Models/SystemMetrics.cs`:

1. After `private bool _padReadingFont = true;` add `private bool _padWebImages;`.
2. After the `PadReadingFont` property add:

```csharp

        /// <summary>
        /// Download images with an http or https address in Markdown notes (Wiki.js spec 6.3). Off by
        /// default: like Kroki, nothing leaves the PC until it is on, and a web image tells its server
        /// that the note was opened.
        /// </summary>
        public bool PadWebImages { get => _padWebImages; set { Set(ref _padWebImages, value); } }
```

In `Pad/MicaPadWindow.Diagrams.cs`:

1. After the `DiagramRenderer` property add:

```csharp

        /// <summary>Loads the images of every MicaPad window; set by the app before the first window opens. Null (most tests) means no image previews.</summary>
        internal static ImageSources? ImageLoader { get; set; }
```

2. Replace

```csharp
            AskSavePath = ShowSaveDialog;
            if (DiagramRenderer is not { } renderer) return;
```

with

```csharp
            AskSavePath = ShowSaveDialog;
            if (ImageLoader is { } images)
            {
                _language.Images = new ImageServices
                {
                    Sources = images,
                    Renderer = DiagramRenderer,
                    Enabled = () => _config.PadDiagrams,
                    WebImages = () => _config.PadWebImages,
                    BaseFolder = FolderOfShown,
                    Warn = message => Warn(message),
                };
            }
            if (DiagramRenderer is not { } renderer) return;
```

3. Change the summary of `ConfigureDiagrams` to `/// <summary>Gives the note editor its pictures and image previews; the history preview gets none.</summary>`, and the summary of `ApplyDiagramSettings` to `/// <summary>Settings → MicaPad changed Draw diagrams, Kroki, the Kroki server or Load images from the web.</summary>`.
4. After `ApplyDiagramSettings` add:

```csharp

        /// <summary>The folder of the shown tab's file, where relative image paths start; null for a note (R12).</summary>
        private string? FolderOfShown() => _shown?.Meta.SourcePath is string path ? Path.GetDirectoryName(path) : null;
```

In `Pad/MicaPadWindow.xaml.cs`, in `OnConfigChanged`, replace

```csharp
            else if (e.PropertyName is nameof(AppConfig.PadDiagrams) or nameof(AppConfig.PadKroki) or nameof(AppConfig.PadKrokiServer))
```

with

```csharp
            else if (e.PropertyName is nameof(AppConfig.PadDiagrams) or nameof(AppConfig.PadKroki) or nameof(AppConfig.PadKrokiServer)
                     or nameof(AppConfig.PadWebImages))
```

In `App.xaml.cs`:

1. After `private static Kil0bitSystemMonitor.Services.Pad.DiagramRenderer? s_diagrams;` add `private static Kil0bitSystemMonitor.Services.Pad.ImageSources? s_images;`.
2. Replace

```csharp
                    Kil0bitSystemMonitor.Pad.MicaPadWindow.DiagramRenderer = s_diagrams;
                }
```

with

```csharp
                    Kil0bitSystemMonitor.Pad.MicaPadWindow.DiagramRenderer = s_diagrams;
                }

                if (s_images == null)
                {
                    s_images = new Kil0bitSystemMonitor.Services.Pad.ImageSources();
                    Kil0bitSystemMonitor.Pad.MicaPadWindow.ImageLoader = s_images;   // image previews (MicaPadWindow.ConfigureDiagrams)
                }
```

3. In `OnExit`, after `s_diagrams?.Dispose();` add `s_images?.Dispose();`.

- [ ] **Step 4: The Settings cards**

In `SettingsWindow.xaml`:

1. Replace the end of the Markdown formatting card

```xml
                                <ui:ToggleSwitch Grid.Column="2" x:Name="PadMarkdownToggle" Toggled="OnPadToggled" VerticalAlignment="Center"/>
                            </Grid>
                        </Border>
```

with

```xml
                                <ui:ToggleSwitch Grid.Column="2" x:Name="PadMarkdownToggle" Toggled="OnPadToggled" VerticalAlignment="Center"/>
                            </Grid>
                        </Border>
                        <Border Background="{DynamicResource SystemControlBackgroundChromeMediumLowBrush}" BorderBrush="{DynamicResource SystemControlElevationBorderBrush}" BorderThickness="1" CornerRadius="8" Margin="0,0,0,12" Padding="20,16">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto" />
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="Auto" />
                                </Grid.ColumnDefinitions>
                                <ui:FontIcon Glyph="&#xE736;" FontSize="20" Foreground="{DynamicResource SystemAccentColorBrush}" Margin="0,0,20,0" VerticalAlignment="Center"/>
                                <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,16,0">
                                    <TextBlock Text="Reading font" FontWeight="SemiBold" FontSize="15"/>
                                    <TextBlock Text="Prose in Markdown notes uses Segoe UI; code and tables stay in the editor font." Opacity="0.6" FontSize="12.5" TextWrapping="Wrap"/>
                                </StackPanel>
                                <ui:ToggleSwitch Grid.Column="2" x:Name="PadReadingFontToggle" Toggled="OnPadToggled" VerticalAlignment="Center"/>
                            </Grid>
                        </Border>
```

2. In the Draw diagrams card, replace the text `A picture under each mermaid, dot (Graphviz) and markmap block in Markdown notes, drawn on this PC. Right-click a picture to copy or save it.` with `A picture under each mermaid, dot (Graphviz), markmap and math block in Markdown notes, drawn on this PC, and a preview under each image. Right-click a picture to copy or save it.` (R6).

3. Replace the end of the Kroki card

```xml
                                <ui:ToggleSwitch Grid.Column="2" x:Name="PadKrokiToggle" Toggled="OnPadToggled" VerticalAlignment="Top"/>
                            </Grid>
                        </Border>
```

with

```xml
                                <ui:ToggleSwitch Grid.Column="2" x:Name="PadKrokiToggle" Toggled="OnPadToggled" VerticalAlignment="Top"/>
                            </Grid>
                        </Border>
                        <Border Background="{DynamicResource SystemControlBackgroundChromeMediumLowBrush}" BorderBrush="{DynamicResource SystemControlElevationBorderBrush}" BorderThickness="1" CornerRadius="8" Margin="0,0,0,12" Padding="20,16">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto" />
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="Auto" />
                                </Grid.ColumnDefinitions>
                                <ui:FontIcon Glyph="&#xE91B;" FontSize="20" Foreground="{DynamicResource SystemAccentColorBrush}" Margin="0,0,20,0" VerticalAlignment="Center"/>
                                <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,16,0">
                                    <TextBlock Text="Load images from the web" FontWeight="SemiBold" FontSize="15"/>
                                    <TextBlock Text="Images with an http or https address are downloaded when the note is shown. Off: only images on this PC are shown." Opacity="0.6" FontSize="12.5" TextWrapping="Wrap"/>
                                </StackPanel>
                                <ui:ToggleSwitch Grid.Column="2" x:Name="PadWebImagesToggle" Toggled="OnPadToggled" VerticalAlignment="Center"/>
                            </Grid>
                        </Border>
```

In `SettingsWindow.xaml.cs`:

1. In `LoadPadSettings`, after `PadMarkdownToggle.IsOn = cfg.PadMarkdown;` add `PadReadingFontToggle.IsOn = cfg.PadReadingFont;`, and after `PadKrokiToggle.IsOn = cfg.PadKroki;` add `PadWebImagesToggle.IsOn = cfg.PadWebImages;`.
2. In `OnPadToggled`, after `cfg.PadMarkdown = PadMarkdownToggle.IsOn;` add `cfg.PadReadingFont = PadReadingFontToggle.IsOn;`, and after `cfg.PadKroki = PadKrokiToggle.IsOn;` add `cfg.PadWebImages = PadWebImagesToggle.IsOn;`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~ImageWindowTests|FullyQualifiedName~PadConfigTests|FullyQualifiedName~DiagramWindowTests|FullyQualifiedName~PadLanguageWindowTests|FullyQualifiedName~PadWindowTests" 2>&1 | tail -5`
Expected: all pass. Then the full suite. Check escapes in `ImageWindowTests.cs`.

- [ ] **Step 6: Commit**

```bash
git add Models/SystemMetrics.cs Pad/MicaPadWindow.Diagrams.cs Pad/MicaPadWindow.xaml.cs App.xaml.cs SettingsWindow.xaml SettingsWindow.xaml.cs tests/Kil0bitSystemMonitor.Tests/ImageWindowTests.cs tests/Kil0bitSystemMonitor.Tests/PadConfigTests.cs
git commit -m "feat(pad): MicaPad shows image previews; Settings cards for the reading font and web images" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

### Task 6: The guide's Markdown section

**Files:**
- Modify: `GUIDE.md` (`### Markdown` only)
- Test: `tests/Kil0bitSystemMonitor.Tests/MarkdownGuideTests.cs`

**Interfaces:**
- Consumes: `PadWindowTests.RepoRoot()`; the features of Plan A and Tasks 1–5 (Format table is Right-click → Format → Format table, Plan A Task 6).
- Produces: a `### Markdown` section listing each feature with its Wiki.js syntax, Format table and image previews; `### Diagrams` stays right after it.

- [ ] **Step 1: Write the failing test**

`tests/Kil0bitSystemMonitor.Tests/MarkdownGuideTests.cs`:

```csharp
using System;
using System.IO;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>GUIDE.md's Markdown section lists what MicaPad shows, with the Wiki.js syntax (spec 7).</summary>
    public class MarkdownGuideTests
    {
        private static string Section(string heading)
        {
            string guide = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "GUIDE.md"));
            int at = guide.IndexOf(heading + "\n", StringComparison.Ordinal);
            Assert.True(at >= 0, heading + " is missing");
            int next = guide.IndexOf("\n### ", at + 1, StringComparison.Ordinal);
            return guide.Substring(at, (next < 0 ? guide.Length : next) - at);
        }

        [Fact]
        public void The_markdown_section_lists_each_feature_with_its_wikijs_syntax()
        {
            string section = Section("### Markdown");

            foreach (string phrase in new[]
                     {
                         "Reading font", "`# Title`", "`===`", "`**bold**`", "H~2~O", "x^2^", "`cs`", "`yaml`",
                         "`- [x]`", "`>>`", "{.is-info}", "{.is-success}", "{.is-warning}", "{.is-danger}",
                         ":-:", "Format table", "Ctrl+Z", "Front matter", "[^1]", "[text][id]", "*[HTML]", ":smile:",
                         "<kbd>Ctrl</kbd>", "{#id}", "$x^2$", "$$", "$5 and $10",
                         "=200x", "=x120", "=200x120", "data:image/", "Load images from the web", "20 MB", "10 MB", "Draw diagrams",
                     })
                Assert.Contains(phrase, section);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~MarkdownGuideTests" 2>&1 | tail -5`
Expected: fails — `Assert.Contains() Failure` for "Reading font" (today's section is one paragraph).

- [ ] **Step 3: Rewrite the section**

In `GUIDE.md`, replace the `### Markdown` section — from its heading up to (not including) `### Diagrams` — with:

```markdown
### Markdown

Notes and `.md`/`.txt` files are shown the way Wiki.js shows Markdown, as you type. Every
character stays visible and editable — the markers are dimmed, not hidden — and the file never
changes unless you run a command such as Format table. Turn it all off with
**☰ → Markdown formatting** or in **Settings → MicaPad**.

**Reading font**: prose is shown in Segoe UI; fenced code, inline code, tables, front matter and
`<kbd>` keys stay in the editor font, so code and columns line up. Turn it off in
**Settings → MicaPad → Reading font** to see everything in the editor font.

- **Headings**: `# Title` to `###### Title`, or a line of `===` or `---` right under a line of
  text (after a blank line, `---` is a rule).
- **Emphasis**: `**bold**`, `*italic*`, `~~strike~~`, `H~2~O` (subscript) and `x^2^`
  (superscript).
- **Code**: `` `inline code` `` gets its own color. A fenced block (```` ``` ```` or `~~~`) is
  shaded, and when its first word names a language — `cs`, `js`, `ts`, `json`, `xml`, `html`,
  `css`, `powershell`, `python`, `sql`, `cpp`, `java`, `php`, `vb`, `diff`, `ini`, `yaml`, `bat`,
  `log` and their usual aliases — its code is colored as a file in that language would be.
- **Lists**: `- ` items get bullets and `- [x]` tasks are crossed off.
- **Quotes and callouts**: `>` quotes get a bar, one per level (`>>` is two). A quote followed by
  a line `{.is-info}`, `{.is-success}`, `{.is-warning}` or `{.is-danger}` is a Wiki.js callout: a
  tinted box with a blue, green, amber or red bar.
- **Tables**: a header line with `|`, then a line of dashes such as `|---|:-:|--:|`, then rows.
  The header is bold and the pipes are dimmed. Right-click → **Format** → **Format table** (with
  the caret in the table) lines the columns up: each as wide as its widest cell, padded as its
  `:--`, `:-:` or `--:` says, with Thai and Chinese text measured by how wide it shows. A single
  **Ctrl+Z** undoes it.
- **Front matter**: a `---` block on the note's first line (closed within 200 lines) is dimmed.
- **Footnotes and reference links**: `[^1]` shows as a raised mark and `[^1]: text` defines it;
  `[text][id]` with `[id]: https://…` is a link.
- **Abbreviations**: after `*[HTML]: Hyper Text Markup Language`, every `HTML` gets a dotted
  underline.
- **Emoji**: `:smile:` shows 😄 (in one color). The caret steps over it, Backspace removes the
  whole code, and copying copies `:smile:`.
- **Keys and HTML**: `<kbd>Ctrl</kbd>` looks like a key; other tags (`<br>`, `<sup>`,
  `<!-- … -->`) are dimmed, and so are `{.class}` or `{#id}` at a line's end and the `\` of `\*`.
- **Math**: `$x^2$` gets the math color, and a block between two `$$` lines is drawn as a formula
  (see Diagrams below). `$5 and $10` stays plain text.

**Images**: `![alt](picture.png)` anywhere on a line shows the picture under that line — several
side by side when they fit. Wiki.js's size goes after the address: `=200x` (width), `=x120`
(height) or `=200x120`, in pixels; without it a picture keeps its own size, made smaller to fit
the width. The address can be a full path or a `file:` address; a path relative to the file's
folder (a note has no folder, so it says "A relative path needs a saved file"); a
`data:image/png;base64,…` address; or an `http`/`https` address, downloaded only while
**Settings → MicaPad → Load images from the web** is on — it is off at first, because a web
picture tells its server that you opened the note. Write spaces as `%20` or put the address in
`<…>`. PNG, JPEG, GIF, BMP, TIFF, ICO, WebP and SVG are shown; files over 20 MB and downloads
over 10 MB are not. Previews have no right-click menu. **Settings → MicaPad → Draw diagrams**
turns them off together with diagrams.

Right-click → **Format** wraps the selection in bold, italic, strikethrough, code or a link, or
turns the selected lines into headings, lists, tasks, quotes or a code block — choose bold, italic,
strikethrough, code, a list, a task or a quote again to take it off; a heading item switches the level,
and choosing the level a line already has removes the heading. Each is a single **Ctrl+Z**.

```

(Keep one blank line between the section's last paragraph and `### Diagrams`.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~MarkdownGuideTests|FullyQualifiedName~DiagramWindowTests|FullyQualifiedName~VaultStatusTextTests" 2>&1 | tail -5`
Expected: all pass. Then the full suite.

- [ ] **Step 5: Commit**

```bash
git add GUIDE.md tests/Kil0bitSystemMonitor.Tests/MarkdownGuideTests.cs
git commit -m "docs(guide): MicaPad Markdown as Wiki.js shows it - every construct, Format table and image previews" -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"
```

---

## After the last task (controller)

1. **Final whole-branch review over both plans** (Plan A tasks 1–7 with their fixes, Plan B tasks 1–6) against the whole spec, with this plan's Review Focus and Plan A's; then **one fix wave** for what it finds (each fix test-first, full suite green, committed).
2. **Rulings doc** `docs/superpowers/plans/2026-10-01-micapad-markdown-rulings.md`, in the shape of `2026-10-01-micapad-diagrams-rulings.md`: Plan A's R1–R5 and this plan's R6–R15 with their costs; rulings made during execution; parked findings (real, deferred) and deferred minors; final suite count; and the owner's manual e2e checklist:
   - A Wiki.js page pasted into a note: headings, tables (and Format table), callouts, footnotes, emoji, `<kbd>`, in both themes; the reading font on and off.
   - Typing speed in a long note with fences, tables, math and images.
   - Math: a formula, `\ce{2H2 + O2 -> 2H2O}`, a mistake ("Missing close brace"), dark/light, Copy picture into Word (the light version).
   - The kroki form: `kroki` + `plantuml` with Kroki off (the message) and on; `kroki` + `mermaid` drawn offline.
   - Images: a `.md` file with a relative PNG and a JPEG photo, a note with a full path, `=200x`, two images side by side, an SVG; a web image with Load images from the web off (the message) and on; Draw diagrams off.
3. **Deploy for e2e** (standing rule): stop MicaStats, `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" build Kil0bitSystemMonitor.csproj -c Release` into `bin\Release\net8.0-windows` (check `Diagrams\tex-svg-full.js`, `Diagrams\mathjax-config.js` and `runtimes\win-x64\native\WebView2Loader.dll` are there), start `bin\Release\net8.0-windows\MicaStats.exe`, and report the deployed commit. The branch descends from the vault build, so the owner's encrypted notes stay readable.
