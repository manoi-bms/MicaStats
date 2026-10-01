# MicaPad: Markdown the way Wiki.js shows it

Asked by the owner on 2026-10-01 ("code block has same font style with normal text … no code format
with color … check list with WikiJS markdown feature"). Decisions taken with the owner:

- **One spec** covering every gap on the checklist, delivered in parts.
- **Reading font, on by default:** prose in Markdown tabs uses a proportional reading font; code
  blocks, inline code and tables stay in the editor's monospace font. A switch in Settings.
- **Tables are lined up by a command** (Format → Format table), never automatically.
- The owner then asked to continue autonomously; the remaining choices are rulings marked **(R)**
  below, each with its reason.

MicaPad stays a styled-source editor: every character stays visible and editable, the file never
changes unless the user runs a command. "Wiki.js parity" means each Wiki.js construct is recognized
and shown so that its meaning is obvious — not a rendered preview.

## What does not change

- Notes and files keep their exact text; only how it is drawn changes. Copy, Save, Copy as RTF,
  history and the diff preview behave as today.
- Non-Markdown tabs (C#, JSON, logs…) are untouched, including their font.
- Markers stay visible (dimmed), as today.

## Delivery: seven parts, in order

1. Fonts and code: reading font, monospace parts, colors inside fenced code, inline code color.
2. Document structure: one scan that knows tables, setext headings, front matter, `$$` blocks,
   quote depth and callouts, and the definition lines.
3. Tables: their look and Format table.
4. Quotes and callouts.
5. Inline extras: footnotes, sub/superscript, emoji, `<kbd>` and HTML tags, abbreviations,
   attributes, escapes, inline math, reference links.
6. Pictures: math blocks, the Wiki.js ```` ```kroki ```` form, image previews.
7. Settings and guide.

## 1. Fonts and code

### 1.1 Reading font

- New `AppConfig.PadReadingFont` (bool, default true). While it is on, a tab whose effective
  language is Markdown shows prose in **Segoe UI Variable Text** (fallback Segoe UI) at the editor's
  font size; other languages keep the editor font (`PadFontFamily`, Cascadia Mono by default).
- These stay in the editor's monospace font: every line of a fenced or `$$` block (delimiters
  included), inline code spans, table lines, front matter, and the text inside `<kbd>`.
- Off: everything is monospace, as today; colors (1.3, 1.4) still apply.
- Rectangular selection (Alt+drag) keeps working by columns; with the reading font columns are not
  straight, which is expected.

### 1.2 Fence languages

The first word of a fence's info string picks a language, compared without case **(R: the existing
MicaPad languages and their usual aliases)**:

| Language | Words |
|---|---|
| C# | `cs`, `csharp`, `c#` |
| JavaScript (also TS) | `js`, `javascript`, `jsx`, `mjs`, `cjs`, `ts`, `typescript`, `tsx`, `node` |
| JSON | `json`, `jsonc`, `json5` |
| XML | `xml`, `xaml`, `svg`, `csproj`, `xsd`, `plist` |
| HTML | `html`, `htm`, `xhtml` |
| CSS | `css` |
| PowerShell | `powershell`, `ps`, `ps1`, `pwsh` |
| Python | `python`, `py` |
| SQL | `sql`, `tsql`, `mssql`, `mysql`, `postgres`, `postgresql`, `plsql` |
| C/C++ | `c`, `cpp`, `c++`, `h`, `hpp`, `cc` |
| Java | `java` |
| PHP | `php` |
| VB | `vb`, `vbnet`, `vba` |
| Diff | `diff`, `patch` |
| INI | `ini`, `cfg`, `conf`, `toml`, `properties` |
| YAML | `yaml`, `yml` |
| Batch | `bat`, `batch`, `cmd` |
| Log | `log` |

Any other word (including `bash`, `sh`, diagram words) gets monospace but no colors.

### 1.3 Colors inside fenced code

- The inside lines of a fenced block with a known language are colored with that language's
  existing MicaPad highlighting, painted from the palette exactly as a whole file in that language
  is (same categories, same theme colors).
- Multi-line constructs (block comments, multi-line strings) work: each block is highlighted from
  its first inside line with an empty state, independently of other blocks.
- A block over 2,000 lines, or a line over 4,000 characters, is not colored (monospace only).
- The info string stays dimmed; the shaded background stays.

### 1.4 Inline code

`` `code` ``: monospace (1.1), a new palette color `MdCode` (dark `#F2A97B`, light `#A33D1F`) and the
existing `MdCodeBackground`. Backticks stay dimmed.

## 2. Document structure

Today `MarkdownDocumentCache` keeps one fact per line (fenced or not). It becomes a per-document
scan, `MarkdownStructure` (pure, `Services/Pad`), recomputed on the same terms as now plus any edit
that inserts or removes `` ` ~ | $ = - > { [ * `` or a line break:

| Fact | Rule |
|---|---|
| Fence | as `FenceTracker` today, plus `$$` blocks: a line that is only `$$` (≤ 3 leading spaces) opens; the next such line closes. Unclosed runs to the end, like fences. |
| Table | a header line containing `|`, followed by a delimiter line `|? :?-+:? (| :?-+:?)* |?`; then every following line containing `|` until a blank or non-`|` line. Roles: Header, Delimiter, Row. Each line's column alignments come from the delimiter. Not inside fences. |
| Setext heading | a line of only `=` (level 1) or `-` (level 2), at least 2 characters, ≤ 3 leading spaces, directly after a paragraph line (non-blank, not a heading, list, quote, fence, table or rule). The text line is shown as that heading; the underline is dimmed. A `---` that is not under a paragraph stays a rule. |
| Front matter | the document's first line is `---`, and a later line within the first 200 is `---` or `...`: those lines and everything between are front matter (dimmed, monospace). |
| Quote depth | the count of `>` markers (spaces allowed between), 1–6. |
| Callout | a run of quote lines immediately followed by a line `{.is-info}`, `{.is-success}`, `{.is-warning}` or `{.is-danger}` (Wiki.js). The quote lines get that callout kind; the class line is dimmed. |
| Definitions | `[id]: url` (reference link), `[^id]: text` (footnote), `*[TERM]: text` (abbreviation) at the start of a line. Abbreviation terms are collected for the whole document. |

Everything is recomputed from text: no state is saved.

## 3. Tables

- Look: header cells bold; pipes and the delimiter line dimmed; all table lines monospace (1.1).
  Inline formatting inside cells works as elsewhere.
- **Format → Format table** (enabled when the caret is on a table line, one Ctrl+Z): rewrites the
  table so every column is as wide as its widest cell, cells padded with spaces according to the
  column's alignment (left by default, `:-:` center, `-:` right), every row starts and ends with
  `|`, the delimiter row becomes dashes of the column width with its colons kept. Cell text is
  trimmed; escaped pipes `\|` stay inside their cell. Rows with fewer cells get empty ones; extra
  cells are kept as extra columns. Width counts characters, East Asian wide characters as 2, so Thai
  combining marks count 0 **(R: monospace display width)**.
- Not supported: MultiMarkdown tables (colspan, multi-line cells) and pivot tables — shown as
  plain pipe tables where they parse as such.

## 4. Quotes and callouts

- Nested quotes: one 3 px bar per level, 8 px apart; every `>` dimmed; text in `MdQuoteText`.
- Callouts: the quote's lines get a tinted background across the text area and a colored bar —
  info blue, success green, warning amber, danger red (new palette entries `MdCalloutInfo`,
  `MdCalloutSuccess`, `MdCalloutWarning`, `MdCalloutDanger`; tint = the color at 15% over the
  background). The `{.is-…}` line is dimmed.

## 5. Inline extras

| Construct | Shown as |
|---|---|
| Footnote reference `[^id]` | link color, 0.75 size, raised (superscript baseline); brackets dimmed |
| Footnote definition `[^id]: text` | `[^id]:` dimmed, text normal |
| Reference link `[text][id]`, `[text][]` | text in link color, the rest dimmed |
| Reference definition `[id]: url "title"` | `[id]:` dimmed, url in link color |
| Subscript `H~2~O` (single `~`, no spaces inside) | 0.75 size, lowered; tildes dimmed |
| Superscript `x^2^` (no spaces inside) | 0.75 size, raised; carets dimmed |
| Emoji `:name:` | the emoji glyph in place of the code **(R)**, like a bullet: it stands for the whole code, the caret steps over it, Backspace removes it whole, copying copies the code. Names from GitHub's gemoji list (MIT), embedded. Unknown names stay text. Not in fences or inline code. |
| `<kbd>Ctrl</kbd>` | tags dimmed; the key text monospace on a key background (`MdKbdBackground`) |
| Other HTML tags `<br>`, `<sup>…</sup>`, `<!-- … -->` | the tags dimmed (no rendering) |
| Abbreviation use | a word equal to a defined term gets a dotted underline (whole words, case-sensitive) |
| Attributes `{.class}`, `{#id}`, `{.tabset}` at the end of a line | dimmed |
| Escape `\*` | the backslash dimmed |
| Inline math `$x^2$` | new palette color `MdMath`; the `$` dimmed. Rules: opening `$` not followed by a space, closing `$` not preceded by a space and not followed by a digit (so "$5 and $10" stays text). Not drawn as a picture **(R: a source editor keeps inline text editable)**. |

## 6. Pictures

All three reuse the diagram engine (picture under the line, cache, error box, light exports).

### 6.1 Math blocks

- `$$` blocks (2) and ```` ```math ```` / ```` ```latex ```` / ```` ```tex ```` fences are drawn as a picture under their
  closing line, offline, by **MathJax 3.2.2** (`tex-svg-full.js`, Apache-2.0) bundled in `Diagrams\`
  with the other scripts. TeX and `\ce{}` chemistry (mhchem) are supported. The page's new kind
  `math` returns an SVG of glyph outlines (no fonts to load) and its PNG, in the theme's text color.
  MathJax's menus, accessibility extras and any network loading are switched off.
- Errors show MathJax's message ("Missing close brace").
- Math is a built-in engine: no Kroki, always offline. **(R: MathJax instead of KaTeX — KaTeX's
  output needs its web fonts, which cannot load inside a picture; MathJax's SVG output has none.
  A spike on 2026-10-01 drew a formula, an integral and `\ce{2H2 + O2 -> 2H2O}` offline.)**

### 6.2 The Wiki.js ```` ```kroki ```` form

A fence whose word is `kroki` takes the diagram type from its first inside line (`plantuml`, `d2`,
…, lower case); the rest is the source. Known types use their name ("PlantUML"); any other type
made of letters and digits is sent as is (Kroki supports more than MicaPad lists). Everything else
works as the Kroki types in the diagrams spec (off until Kroki is on).

### 6.3 Image previews

- `![alt](source "title" =WxH)` anywhere on a line gives a preview under that line, one per image,
  side by side when they fit, wrapping otherwise. `=WxH` (Wiki.js): `=200x`, `=x120`, `=200x120` in
  device-independent pixels; without it the natural size, fitted to the text width (never enlarged).
- Sources:
  - an absolute path or `file:` URI;
  - a relative path, resolved against the tab's file folder (file-backed tabs only);
  - a `data:image/…;base64,` URI;
  - `http:`/`https:` only while the new **Load images from the web** setting is on
    (`AppConfig.PadWebImages`, default **false** **(R: like Kroki, nothing leaves the PC by
    default; a web image tells the server you opened the note)**): GET, 10 s, 10 MB at most.
- Formats: PNG, JPEG, GIF (first frame), BMP, TIFF, ICO, WebP where Windows has the codec, and SVG
  (drawn through the diagram page as Kroki SVGs are). Files over 20 MB are not read.
- Messages in the error box: "Image not found: {source}", "Web images are off — turn them on in
  Settings → MicaPad.", "The image could not be read.", "The image could not be downloaded ({host}).",
  "A relative path needs a saved file." (for notes).
- Previews have no right-click menu **(R: they are the user's own images)**; they follow the same
  pause after typing, cache (64, memory only) and fit-to-width rules as diagrams.

## 7. Settings and guide

Settings → MicaPad gains two cards:

| Property | Default | Card |
|---|---|---|
| `PadReadingFont` | true | **Reading font** — "Prose in Markdown notes uses Segoe UI; code and tables stay in the editor font." |
| `PadWebImages` | false | **Load images from the web** — "Images with an http or https address are downloaded when the note is shown. Off: only images on this PC are shown." |

GUIDE.md: the Markdown section is rewritten as a feature list matching this spec (with the Wiki.js
syntax for callouts, tables, footnotes, math, `=WxH`), plus "Format table".

## Architecture

- `Services/Pad` (pure, tested): `MarkdownStructure` (2), `FenceLanguages` (1.2),
  `MarkdownLineTokenizer` (extended for 4–5, takes the line's structure facts), `MarkdownStyles`
  (new looks), `TableFormatter` (3), `Emoji` (name → glyph, from an embedded TSV),
  `ImageSources` (6.3: parsing `![…]`, resolving, limits; `HttpMessageHandler` injectable),
  `DiagramKinds`/`DiagramBlocks` (math engine, `kroki` form, `$$` fences).
- `Pad`: `MarkdownColorizer` (fonts, sizes, baselines, decorations), `FenceHighlighter` (1.3,
  AvalonEdit's `HighlightingEngine` per block, cached per block and document version),
  `MarkdownBackgroundRenderer` (quote levels, callouts, front matter, tables), `EmojiGenerator`,
  `ImageBoard` + `ImageGenerator` (6.3, on the diagram picture element), `DiagramPage` (math kind),
  `EditorLanguage` (installs them; the reading font), the window (Format table, settings).

## Error handling

Every new colorizer, generator and renderer follows the existing rule: a failure is caught, logged
once under `pad` with the exception type only, and the line is shown unformatted (or the Markdown
falls back to plain text, as today for colorizer failures). No note text, image path or URL is
logged.

## Testing

- `MarkdownStructure`: tables (with and without outer pipes, alignment colons, a pipe line without
  a delimiter is not a table, tables inside fences ignored), setext vs rule, front matter limits,
  `$$` blocks, quote depth, callout runs, definitions and abbreviation terms.
- `FenceLanguages`: every word in 1.2; unknown words.
- Tokenizer: each construct in 5 with its negative cases ("$5 and $10", `a~b`, `x^ 2^`, unknown
  emoji, escaped markers, HTML-looking text inside code).
- `TableFormatter`: alignment, uneven rows, escaped pipes, wide characters, Thai combining marks,
  already-formatted tables unchanged, CRLF kept.
- `ImageSources`: parsing `=WxH`, titles, spaces; path/relative/data/web resolution; web off; size
  limits; fake handler for downloads (no network).
- Editor: reading font on Markdown only and off by the setting; monospace on code/table lines;
  fence colors (a C# keyword gets the keyword color); emoji element; callout background; a math
  block gets a picture (fake renderer); an image line gets previews (temp files); Format table is
  one undo step.
- Page (real WebView2): math formula, `\ce{}`, a math error.
- Manual on the owner's machine: a Wiki.js page pasted into a note looks right in both themes;
  typing speed in a long note.

## Out of scope

Rendered preview; tabsets as real tabs; MultiMarkdown/pivot tables; media embeds (YouTube…);
abbreviation tooltips; indented code blocks (4-space indented code is not recognized: in notes it
collides with nested list text **(R)**); typographer quotes; color emoji (WPF draws emoji in one
color).
