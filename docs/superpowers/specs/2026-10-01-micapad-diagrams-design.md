# MicaPad: diagrams in Markdown notes

Approved in conversation with the owner on 2026-10-01. Decisions taken there:

- Pictures appear **inline in the editor, under each diagram block**; the code stays editable.
- **Built-in, offline engine** for Mermaid (mindmap included), Graphviz and Markmap; every other
  well-known type through a **Kroki server, off until the user switches it on**.

## Why

Notes often hold a flowchart, a sequence, a mindmap or an architecture sketch written as text
(Mermaid, PlantUML, Graphviz). MicaPad colors that text but never draws it, so the owner opens
another tool to see it. MicaPad should draw it where it is written, without sending note text
anywhere unless the user chose a server.

## What does not change

- Notes stay plain text (encrypted at rest as before). A picture is drawn from the block; nothing
  about the picture is saved in the note or anywhere on disk.
- Tabs that are not in Markdown mode, the history preview, Copy as RTF, Save As and Ctrl+S are
  unchanged: diagram blocks there are code, as today.

## Delivery: four parts, in order

1. The engine: a hidden WebView2 that draws Mermaid, Graphviz and Markmap offline (proved first
   by a spike), with the bundled scripts.
2. Kroki: the client, its settings, and drawing Kroki's SVG the same way.
3. The editor: pictures under blocks, updates, errors, theme, Hide code, the picture's menu.
4. Settings → MicaPad and GUIDE.md.

## 1. Which blocks are diagrams

A fenced block (as `FenceTracker` already finds them) whose info string's first word, compared
without case, is one of:

| Engine | Words | Kroki type sent |
|---|---|---|
| Mermaid (built-in) | `mermaid`, `mmd` | — |
| Graphviz (built-in) | `dot`, `graphviz`, `gv` | — |
| Markmap (built-in) | `markmap` | — |
| PlantUML (Kroki) | `plantuml`, `puml` | `plantuml` |
| C4 with PlantUML | `c4plantuml` | `c4plantuml` |
| D2 | `d2` | `d2` |
| BPMN | `bpmn` | `bpmn` |
| Excalidraw | `excalidraw` | `excalidraw` |
| Vega / Vega-Lite | `vega`, `vegalite`, `vega-lite` | `vega`, `vegalite` |
| WaveDrom | `wavedrom` | `wavedrom` |
| Ditaa | `ditaa` | `ditaa` |
| Structurizr | `structurizr` | `structurizr` |
| Nomnoml | `nomnoml` | `nomnoml` |
| Pikchr | `pikchr` | `pikchr` |
| Svgbob | `svgbob` | `svgbob` |
| DBML | `dbml` | `dbml` |
| ERD | `erd` | `erd` |
| Bytefield | `bytefield` | `bytefield` |
| BlockDiag family | `blockdiag`, `seqdiag`, `actdiag`, `nwdiag`, `packetdiag`, `rackdiag` | same |
| TikZ | `tikz` | `tikz` |
| UMLet | `umlet` | `umlet` |
| Symbolator | `symbolator` | `symbolator` |
| WireViz | `wireviz` | `wireviz` |

Any other word is ordinary code. The block's source is the text between its delimiter lines; an
unclosed block (no closing fence yet) is not drawn. A source over 50,000 characters is not drawn
("Too large to draw").

## 2. The engine (Part 1)

### 2.1 Hidden WebView2

- Package `Microsoft.Web.WebView2` (latest stable). One renderer per process, created on the first
  diagram (about a second), reused after. It uses `CoreWebView2Environment` with the user data
  folder `%LOCALAPPDATA%\MicaStats\WebView2` and a `CoreWebView2Controller` on a hidden window; it
  is never shown and never in the visual tree of a visible window.
- It loads `https://micapad-diagrams.invalid/render.html` mapped with
  `SetVirtualHostNameToFolderMapping` to the app's `Diagrams\` folder. Every other request
  (`WebResourceRequested` filter `*`) gets a 403: the page cannot reach the network. Dev tools,
  default context menus, autofill, password saving and status bar are off.
- WebView2 runs in its own processes, so MicaStats' software-drawing default (the D3D freeze fix)
  is unaffected.
- Runtime missing (`WebView2RuntimeNotFoundException`): every diagram shows "Diagrams need the
  Microsoft Edge WebView2 Runtime." with a link to its download page (opened through the existing
  safe-link path).

### 2.2 Bundled scripts

In the repo under `Pad/Diagrams/` and copied to the output `Diagrams\` folder (so the installer,
which ships `release-output\*`, carries them): `render.html`, `render.js` (the bridge),
`mermaid.min.js`, the `@viz-js/viz` standalone build (Graphviz as WebAssembly), `markmap-lib`,
`markmap-view` and `d3` builds, each at the latest stable version at implementation time, listed
with its version and license in `Diagrams\THIRD-PARTY.txt` (license texts included). Nothing is
loaded from a CDN.

### 2.3 Drawing

`render(kind, source, theme)` returns either an SVG text plus a PNG (2× scale, transparent
background) with its size, or an error message. Mermaid uses its `dark` theme in MicaPad's dark
theme and `default` in light; Graphviz gets default node, edge and font colors from the palette
and a transparent background; Markmap colors follow the theme. The PNG is made inside the page; the
mechanism (canvas, or the DevTools `Page.captureScreenshot` of the element for SVGs with
`foreignObject`) is settled by the Part 1 spike, which must show a Mermaid flowchart, sequence and
mindmap, a dot graph and a Markmap all becoming PNGs while the controller is hidden. Largest side
4,096 px (scaled down beyond). A draw that takes over 15 s is abandoned ("Took too long to
draw.").

### 2.4 Queue and cache

Draws run one at a time on the UI thread's async flow. A newer request for the same block
replaces a waiting older one. Results are kept in memory only, least recently used first out, 64
pictures, keyed by a SHA-256 of (engine, Kroki server for Kroki types, theme, source). Nothing is
written to disk.

## 3. Kroki (Part 2)

- Used only for the Kroki types in the table, only when **Draw other types with Kroki** is on.
- `POST {server}/{type}/svg`, body = the block's source as `text/plain; charset=utf-8`, from a
  shared `HttpClient` with a 10 s timeout; the SVG it returns is drawn to a PNG by the same page
  (Kroki's colors cannot follow the theme, so Kroki pictures sit on a light "paper" card in both
  themes).
- Server: `https://kroki.io` by default, or the user's own (http or https URL only; anything else
  is refused in Settings).
- Errors: HTTP 400 → the first line of the response body ("PlantUML: Syntax Error? (line 3)");
  other failures → "The Kroki server could not be reached (kroki.io)."; Kroki off → "PlantUML
  needs Kroki — turn it on in Settings → MicaPad." (the type's name in place of PlantUML).
- Only the block's text is sent. A credential in a diagram is its `{{secret:ID}}` reference text,
  never the value.

## 4. In the editor (Part 3)

- Only in tabs whose effective language is Markdown and only while **Draw diagrams** is on.
- The picture sits under the block's closing fence line, as part of that line's visual line; the
  fence text itself stays ordinary, editable text. The picture is scaled to fit the text area's
  width (never enlarged past its natural size).
- Drawn when the block first comes into view; redrawn 600 ms after the last change inside it,
  keeping the previous picture until the new one is ready; redrawn on a theme switch (from the
  cache when possible).
- While the first draw of a block runs: a small "Drawing…" line. An error: a box in the palette's
  alert color with the message (selectable text), in place of the picture.
- Controls on the picture (shown on hover, top-right): **Hide code** / **Show code** — folds the
  block's inner lines through the existing folding (not saved across restarts).
- Right-click on the picture: **Copy picture** (PNG to the clipboard), **Save as PNG…**,
  **Save as SVG…** (a Save dialog; default name `diagram.png` / `diagram.svg`).
- Typing stays smooth: nothing waits on a draw; a note with 50 diagram blocks scrolls without
  drawing those out of view.

## 5. Settings and guide (Part 4)

New `AppConfig` properties (Settings → MicaPad gains a "Diagrams" card and a "Kroki" card):

| Property | Default | Meaning |
|---|---|---|
| `PadDiagrams` | `true` | Draw diagrams under diagram blocks |
| `PadKroki` | `false` | Draw the other types through Kroki |
| `PadKrokiServer` | `"https://kroki.io"` | The Kroki server |

The Kroki card says: "Sends the diagram's text (only that block) to this server. Use your own
Kroki server for private notes." Every new field is optional when reading; unknown or invalid
values fall back to the default.

GUIDE.md, MicaPad section: a "Diagrams" subsection listing the fence words, what is offline and
what goes to Kroki, Hide code, the picture's menu, and how to point Kroki at a self-hosted server.

## Architecture

Pure units under `Services/Pad/` (no WPF, unit-tested): `DiagramKinds` (fence word → engine and
Kroki type, display names), `DiagramBlocks` (the diagram blocks of a document from its lines and
fence classification: kind, source, line range), `DiagramCacheKey`, `KrokiClient` (request and
error mapping; `HttpMessageHandler` injectable). WPF side under `Pad/`: `DiagramRenderer` (the
hidden WebView2, queue, cache), `DiagramGenerator` (the visual-line element under a closing fence),
`DiagramPicture` (the picture/error/drawing control with its controls and menu). `MicaPadWindow`
wires them; `EditorLanguage` decides when they are active.

## Error handling

- No draw ever throws into the UI; a failure shows in the block's box and logs a warning under
  `pad` with the engine and the exception type only — never the diagram's text.
- A WebView2 process crash (`ProcessFailed`) drops the renderer; the next draw creates a new one.
- The renderer is disposed at exit.

## Testing

Automated (xUnit, never a visible window, temp folders, no network except where stated):

- `DiagramKinds`: every word in the table maps to its engine and Kroki type; case-insensitive; an
  unknown word is code.
- `DiagramBlocks`: blocks found with their sources and line ranges; unclosed and oversized blocks
  skipped; a tilde fence; nested backticks inside the source.
- `KrokiClient` with a fake handler: the URL, method, body and content type; 400 → first body line;
  timeout and connection failure → the "could not be reached" text; an invalid server URL refused.
- Cache key: changes with engine, theme, server and source; same inputs → same key.
- Renderer (real WebView2 on the shared UI thread; skipped with a clear message if the runtime is
  absent): Mermaid flowchart, sequence and mindmap, a dot graph and a Markmap each return a PNG
  with a non-zero size and an SVG; a Mermaid syntax error returns its message; a page request to
  an outside URL is refused.
- Editor: a Markdown tab with a mermaid block gets one diagram element under the closing fence; a
  non-Markdown tab and **Draw diagrams** off get none; an edit inside the block redraws after the
  delay (fake renderer, fake clock); an error result shows the error box; Hide code folds the inner
  lines; Kroki off shows the "needs Kroki" text for a plantuml block.

Manual, on the owner's machine after the deploy: a real note with a Mermaid mindmap, a flowchart,
a dot graph and a Markmap; dark/light switch; Hide code; Copy picture into Word; Save as SVG opened
in a browser; Kroki on with kroki.io for a PlantUML mindmap and a D2 diagram; Kroki off message;
typing speed in a note with many diagrams.

## Out of scope

A full Markdown preview (tables, images, HTML); editing a diagram by dragging; pictures in the
history preview; saving fold state; drawing diagrams in Copy as RTF or exports; a local PlantUML
(Java) or D2 engine.
