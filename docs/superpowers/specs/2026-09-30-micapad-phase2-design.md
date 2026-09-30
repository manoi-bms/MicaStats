# MicaPad phase 2: themes, Markdown, languages, editing parity

**Date:** 2026-09-30
**Status:** draft for the owner's review
**Builds on:** `docs/superpowers/specs/2026-09-29-micapad-design.md` (phase 1, shipped in v1.12.0).
Phase 1's *Out of scope* list was the phase 2 backlog; this spec covers all of it, plus three
requests made on 2026-09-30: inline Markdown formatting, a light/dark theme switch for MicaPad
only, and right-click menus.

## Why

MicaPad replaces the owner's notepad2 windows. Phase 1 made text impossible to lose; phase 2 makes
MicaPad pleasant enough to be the only editor the owner reaches for: readable notes (Markdown),
readable code and logs (syntax colors), a theme that suits the room, and the editing moves a
notepad2/notepad4 user expects.

## What does not change

Everything phase 1 promises still holds and is not reopened here: autosave within seconds, no
prompts, shadow mode for real files, history, closed notes, encodings and line endings. Every
feature below only changes how text is **shown** or makes an **explicit, undoable edit** the user
asked for. Nothing here writes to a real file except the existing Ctrl+S path.

## Delivery: five parts, in order

Too large for one plan. Each part gets its own implementation plan, is implemented and reviewed,
and is deployed to `bin\Release\net8.0-windows` for the owner's testing before the next begins.

| Part | Contents | Depends on |
|---|---|---|
| 1 | Light/dark theme, MicaPad palette, right-click menus (edit, tab) | — |
| 2 | Languages: syntax highlighting, Markdown styled source, Format menu, folding | 1 (palette) |
| 3 | Editing helpers: auto-close, line operations, bookmarks, mark occurrences | 1 (menus) |
| 4 | Links, drag-to-reorder tabs, full screen, Copy as RTF | 2 (RTF colors) |
| 5 | Tools, history diff, more than one MicaPad window | 1–4 |

---

## Part 1 — Theme, palette, right-click menus

### 1.1 Theme switch

- A button in the tab strip, left of the *Closed notes* `▾` button: a sun glyph (`E706`) in the
  dark theme, a moon glyph (`E708`) in the light theme. Tooltip: *Switch to light theme* /
  *Switch to dark theme*. One click switches every open MicaPad window at once; no restart.
- New `AppConfig.PadTheme`: `"Dark"` (default, today's look) or `"Light"`. Any other value reads
  as `"Dark"`. Also offered in Settings → MicaPad as a Dark/Light choice.
- **Only MicaPad changes.** Settings, Ask MicaStats, the overlay, the panels and toasts keep their
  current look.
- What follows the theme: the window background, tab strip, tabs, info bar, find/replace bar,
  go-to-line box, history pane and its preview, the rename and closed-notes popups, every
  MicaPad menu, the editor (background, text, selection, caret, current line, line numbers, find
  highlights, and every color added by later parts), and the title bar (through
  `DWMWA_USE_IMMERSIVE_DARK_MODE`, attribute 20, set on `SourceInitialized` and on every switch).
- ModernWpf controls inside MicaPad follow through `ThemeManager.RequestedTheme` on the MicaPad
  window and on each MicaPad `ContextMenu` (today hard-coded to `Dark` in `NewMenu`).
- The WinForms Font dialog is drawn by Windows and is not themed.

### 1.2 Palette

`PadPalette` (pure, no WPF types beyond `Color`) holds every MicaPad color as a named value, in two
instances, `PadPalette.Dark` and `PadPalette.Light`. Nothing in MicaPad keeps a literal color any
more: the ~33 literals in `MicaPadWindow.xaml`, `FindReplaceBar.xaml`, `HistoryPane.xaml`, the
brushes in `FindReplaceBar.xaml.cs`, `ConfigureEditor` and `MatchHighlighter` all move to the
palette.

- XAML reads brushes with `DynamicResource` keys (`Pad.Background`, `Pad.Chrome`, `Pad.Text`,
  `Pad.Muted`, `Pad.Accent`, …). `PadTheme.Apply(window, palette)` replaces those brushes in the
  window's resources, so a switch repaints live.
- Code that paints directly (editor properties, background renderers, colorizers) reads the
  current `PadPalette` and is re-applied on a switch; renderers redraw through
  `TextView.Redraw()`.
- Light values: background `#FBFBFD`, chrome `#F0F0F4`, text `#1B1B1F`, accent `#06707C` (today's
  `#3FD2E4` is unreadable on white; `#087E8B` misses the 4.5:1 rule on the chrome, info bar and banner), alert red `#C62828`. Dark values are today's colors.
- **Contrast rules, checked by tests for both palettes** (WCAG relative luminance): body text on
  background ≥ 7:1; muted text, link text and accent text ≥ 4.5:1; dimmed Markdown markers and line
  numbers ≥ 3:1.

### 1.3 Right-click menus

**Editor.** Right-click moves the caret to the click point unless the click is inside the current
selection. Items, each disabled when it cannot apply:

- Undo `Ctrl+Z`, Redo `Ctrl+Y`
- Cut `Ctrl+X`, Copy `Ctrl+C`, Paste `Ctrl+V`, Delete `Del`, Select all `Ctrl+A`
- Find `Ctrl+F`, Replace `Ctrl+H`, Go to line… `Ctrl+G`

Later parts add groups to this menu (Format, Lines, Tools, Copy as RTF); the menu is built by one
method that each part extends.

**Tab.** Right-click on a tab (without switching to it first):

- Rename… (the existing rename popup), Close (no shortcut shown: `Ctrl+W` closes the active tab, not the one right-clicked), Close other tabs
- For a file-backed note only: Copy file path, Show in folder (`explorer.exe /select,"<path>"`)

Closing through the menu is the ordinary close: flushed, snapshotted, moved to Closed notes,
nothing deleted, no prompt. *Close other tabs* closes each other tab the same way.

---

## Part 2 — Languages: syntax colors, Markdown, folding

### 2.1 Which language a tab uses

Each note has a language, shown as a new clickable item in the status bar between the line ending
and the save state (`Markdown`, `JSON`, `Plain text`, …). Clicking it opens a menu: *Auto (by file
type)*, *Plain text*, *Markdown*, then the languages below. The choice is stored per note in
`meta.json` as `language` (a language id, or null for Auto) and survives restart.

**Auto** resolves by the file extension of a file-backed note:

| Language | Extensions | Colors from |
|---|---|---|
| Markdown | scratch notes, `.md .markdown .txt` | MicaPad's own formatter (2.3) |
| JSON | `.json .jsonc` | AvalonEdit `Json` |
| XML | `.xml .xaml .csproj .props .targets .config .svg .resx .xsd` | AvalonEdit `XML` |
| HTML | `.html .htm` | AvalonEdit `HTML` |
| C# | `.cs` | AvalonEdit `C#` |
| JavaScript | `.js .mjs .cjs .ts .tsx .jsx` | AvalonEdit `JavaScript` |
| CSS | `.css` | AvalonEdit `CSS` |
| PowerShell | `.ps1 .psm1 .psd1` | AvalonEdit `PowerShell` |
| Python | `.py .pyw` | AvalonEdit `Python` |
| SQL | `.sql` | AvalonEdit `TSQL` |
| C/C++ | `.c .h .cpp .hpp .cc` | AvalonEdit `C++` |
| Java | `.java` | AvalonEdit `Java` |
| PHP | `.php` | AvalonEdit `PHP` |
| VB | `.vb .bas` | AvalonEdit `VB` |
| Diff | `.diff .patch` | AvalonEdit `Patch` |
| INI | `.ini .cfg .conf .inf .editorconfig .gitconfig` | MicaPad `Ini.xshd` |
| YAML | `.yml .yaml` | MicaPad `Yaml.xshd` |
| Batch | `.bat .cmd` | MicaPad `Batch.xshd` |
| Log | `.log` | MicaPad `Log.xshd` (timestamps; ERROR/FATAL, WARN, INFO, DEBUG levels) |

Any other extension is *Plain text*. When `PadMarkdown` is off, notes and `.md .markdown .txt`
resolve to *Plain text* instead of Markdown; an explicit per-note choice still wins.

**Large text.** Above 2 MB of text, highlighting, Markdown formatting and folding are off and the
status item reads *Plain text (large)*. Phase 1's 10 MB snapshot limit and 50 MB open limit are
unchanged.

### 2.2 Syntax colors that work in both themes

AvalonEdit's definitions carry colors chosen for a white background. MicaPad loads a private copy
of each definition (`HighlightingLoader` over the embedded `.xshd` stream, never the shared
`HighlightingManager.Instance` objects) per theme, and replaces each named color's foreground by
category. Rows are tried top to bottom and the first match wins, so `KeywordX` is a keyword before
`Key` can make it an attribute:

| Category | Matched color names (case-insensitive substring) |
|---|---|
| Comment | `Comment` |
| String | `String`, `Char`, `Verbatim` |
| Keyword | `Keyword`, `Modifier`, `Visibility`, `Access`, `This`, `Null`, `True`, `False`, `Bool`, `Value` |
| Number | `Number`, `Digit` |
| Type | `Type`, `Class`, `Reference` |
| Preprocessor | `Preprocessor`, `Directive`, `Region` |
| Tag | `Tag`, `Element` |
| Attribute | `Attribute`, `Property`, `Key` |
| Operator | `Operator`, `Punctuation`, `Brace` |
| Error / Warning / Info / Debug | the four level names in `Log.xshd` |
| Anything else | the palette's text color |

Font weight and style from the definition are kept; backgrounds are dropped. Every category color
meets 4.5:1 on its theme's background (tested). A definition that fails to load falls back to
Plain text and logs a warning under `pad`.

### 2.3 Markdown styled source

What the owner approved in design section 1: text is formatted in place, and the Markdown markers
stay visible but dimmed. The document is never changed by formatting.

| Element | Shown as |
|---|---|
| `#`…`######` heading | 1.6, 1.4, 1.25, 1.15, 1.05, 1.0 × the font size, semibold, heading color |
| `**x**`, `__x__` | bold |
| `*x*`, `_x_` | italic (`_` only at word boundaries, so `snake_case_name` is untouched) |
| `***x***` | bold italic |
| `~~x~~` | strikethrough |
| `` `x` `` | monospace, shaded background |
| `[text](url)` | text in the link color, `(url)` dimmed |
| `- ` `* ` `+ ` list item | the marker drawn as `•` in the accent color (one-character element, so caret and selection are unaffected) |
| `1. ` list item | number in the accent color |
| `- [ ] ` / `- [x] ` | checkbox dimmed; a done item's text dimmed and struck through |
| `> ` quote | bar at the left edge, muted text |
| ```` ``` ```` / `~~~` fenced block | shaded background across the lines, monospace, no inline formatting inside |
| `---`, `***`, `___` alone on a line | a horizontal rule drawn through the line |

Markers (`#`, `**`, `` ` ``, `>`, `[`, `](…)`) use the palette's dim color. Backslash escapes
(`\*`) are not formatted. Unclosed markers are left as plain text. No tables, images or HTML.

`PadMarkdown` (default on) is the menu check item *Markdown formatting* in `☰` and a switch in
Settings → MicaPad.

### 2.4 Format menu

When the tab's language is Markdown, the editor's right-click menu gains **Format ▸**: Bold,
Italic, Strikethrough, Code, Link, Heading 1, Heading 2, Heading 3, Bullet list, Numbered list,
Task, Quote, Code block. No new shortcuts.

- Inline items wrap the selection (`**sel**`); with no selection they insert the pair and put the
  caret between. Applied to a selection already wrapped in that marker, they remove it (toggle).
- Link wraps the selection as `[sel](url)` and selects `url`.
- Line items (headings, lists, task, quote) toggle the prefix on every line the selection touches;
  a heading item replaces an existing heading level.
- Code block puts ```` ``` ```` lines around the selected lines.
- Each item is one undoable edit.

### 2.5 Folding

A fold margin appears only for languages that fold:

- Brace folding (`{ }` and `[ ]`, ignoring braces inside strings and comments by a simple lexer):
  JSON, C#, JavaScript, CSS, C/C++, Java, PHP, PowerShell.
- `XmlFoldingStrategy` (AvalonEdit): XML, HTML.
- Heading folding: Markdown — a heading folds to the next heading of the same or higher level; a
  fenced block folds as one.

Folds update 500 ms after the last edit, and immediately on tab switch. Fold state is not saved
(a restarted tab opens unfolded). Find, Go to line and history restore unfold what they reveal.

---

## Part 3 — Editing helpers

### 3.1 Auto-close

- Typing `(` `[` `{` `"` `'` `` ` `` inserts the closing character after the caret.
- Typing a closing character when the same character is already next just moves over it.
- Backspace between an empty pair deletes both.
- With a selection, typing an opener wraps the selection instead of replacing it.
- Quotes only auto-close when the character before the caret is not a letter or digit, so
  `don't` types normally. In Markdown, `*` and `_` are never auto-closed.
- `AppConfig.PadAutoClose`, default on; *Auto-close brackets and quotes* in `☰` and Settings.

### 3.2 Line operations

| Keys | Action |
|---|---|
| `Ctrl+D` | Duplicate the line, or the selection |
| `Ctrl+Shift+↑` / `Ctrl+Shift+↓` | Move the selected lines up / down |
| `Ctrl+J` | Join the selected lines (or this line with the next) with one space |

Also in the right-click menu under **Lines ▸**: Duplicate, Move up, Move down, Join, Sort
ascending, Sort descending (current culture, case-insensitive, stable), Remove duplicate lines
(keeps the first), Trim trailing whitespace. Each works on the lines the selection touches (the
whole document when nothing is selected, for sort, dedupe and trim), keeps the document's line
endings, and is one undoable edit.

### 3.3 Bookmarks

- `Ctrl+F2` toggles a bookmark on the caret line; `F2` / `Shift+F2` jump to the next / previous
  one, wrapping around. *Clear bookmarks* is in `☰`.
- Shown as an accent dot in a narrow margin left of the line numbers.
- They move with the text while editing (anchored), and are saved per tab in `session.json`
  (`Tabs[id].Bookmarks`, line numbers) so they survive restart. A restored bookmark past the end
  of the text is dropped.

### 3.4 Mark occurrences

When the selection is exactly one whole word, every other whole-word, case-sensitive occurrence is
highlighted with a soft box (a different color from find matches, drawn below them), and the
status bar shows `5 matches`. Updated 150 ms after the selection settles; counting stops at
10,000 (`10,000+ matches`). Off above the 2 MB limit.

---

## Part 4 — Links, tabs, view, Copy as RTF

### 4.1 Links

`http://`, `https://` and `mailto:` addresses are underlined in the link color; **Ctrl+Click**
opens them in the default browser or mail program. The tooltip reads *Ctrl+Click to open*. No
other scheme is ever opened — not `file:`, not paths, not custom protocols — so text in a note can
never launch a program. Implemented as a `LinkElementGenerator` subclass whose
`GetUriFromMatch` returns null for any other scheme; AvalonEdit's own hyperlink option stays off.

### 4.2 Drag to reorder tabs

Dragging a tab past the system drag threshold moves it; the other tabs make room while dragging;
dropping sets the new order, which is saved to the session. The strip scrolls when dragging past
its edge.

### 4.3 Full screen

`F11` (and *Full screen* in `☰`) hides the title bar and fills the current monitor, keeping tabs,
find bar and status bar. `F11` again restores the previous placement. Not saved: MicaPad always
reopens windowed.

### 4.4 Copy as RTF

In the right-click menu and `☰`: copies the selection (or the whole note when nothing is
selected) to the clipboard as RTF and as plain text, with the colors, bold, italic, strike and
heading sizes currently shown. The RTF always uses the **light** palette, because it is pasted
onto white pages (Word, Outlook). The font is the editor font. A busy clipboard is retried three
times over 300 ms; failing that, the status bar says *Clipboard busy, try again*.

---

## Part 5 — Tools, history diff, windows

### 5.1 Tools

**Tools ▸** in the right-click menu and `☰`. Each acts on the selection, is one undoable edit, and
when it cannot apply leaves the text alone and says why in the status bar for 5 s.

| Tool | Behaviour |
|---|---|
| Base64 encode | UTF-8 bytes of the selection → Base64 |
| Base64 decode | Base64 → UTF-8 text; *Not valid Base64* or *Not UTF-8 text* otherwise |
| Convert number ▸ Decimal / Hex / Binary / Octal | The selection is one integer (`255`, `-3`, `0xFF`, `0b1010`, `0o17`, underscores allowed), within 64 bits; output `255`, `0xFF`, `0b11111111`, `0o377` |
| Insert GUID | lowercase `D` format at the caret |
| Insert timestamp ▸ ISO 8601 / Date / Unix seconds | `2026-09-30T18:05:12+07:00`, `2026-09-30`, `1790766312`; always Gregorian and invariant, even under the Thai culture |
| Evaluate | The selection is arithmetic (`+ - * / % ^`, parentheses, unary minus, decimal and `0x` numbers); appends ` = <result>` after it. Parsed by MicaPad's own evaluator: nothing is compiled or executed. Division by zero or a syntax error is reported, not thrown |

### 5.2 History diff

The history preview banner gains **Compare with current**. It toggles the read-only preview
between the old version and a line diff against the current text: unchanged lines plain, removed
lines on a red-tinted background, added lines on a green-tinted background, each with a `−`/`+`
glyph in the margin, and a `+12 −3 lines` summary in the banner. Computed with DiffPlex 1.9.0
(Apache-2.0, no dependencies) off the UI thread; either text over 1 MB shows *Too large to
compare*.

### 5.3 More than one MicaPad window

- **New window** (`Ctrl+Shift+N`, `☰`) opens a window with one new note. A tab's right-click menu
  gains **Move to new window** and **Move to ▸** (each other window, by its active tab's title).
- Each window has its own tabs, active tab, placement, always-on-top, zoom and full-screen state.
  Theme, font, wrap, line numbers, Markdown and auto-close stay global.
- **Closing a window** (×) while another MicaPad window is open moves its tabs to the most recently
  active remaining window: no note is closed by closing a window. Closing the last window hides it
  as today, keeping its tabs.
- The hotkey, the overlay menu and `--pad` activate the most recently active window; a file
  opened with `--pad` or *Open with* goes there, unless it is already open in another window, in
  which case that window comes forward on that tab.
- Reopen at login restores every window that was open, with its placement.
- All windows share the MicaPad taskbar identity, so they group together.
- **Session format.** `session.json` gains `Windows` (each: id, open flag, placement, maximized,
  always-on-top, zoom, note ids in tab order, active note id); `Tabs` stays shared. A session
  without `Windows` loads as one window from the old fields. For downgrade safety the first
  window's state is also written to the old top-level fields, so v1.12 still opens its tabs.
- `PadWorkspace.Open` stays the single list of open notes, so autosave, snapshots and closing are
  unchanged; each `OpenNote` gains the id of the window showing it, and each window shows its own
  notes through a filtered view.

This is the riskiest item (session format, the open path, the single-window assumptions in
`MicaPadWindow`), which is why it is last.

---

## Settings and storage

New `AppConfig` properties (Settings → MicaPad gains the three switches and the theme choice):

| Property | Default | Part |
|---|---|---|
| `PadTheme` | `"Dark"` | 1 |
| `PadMarkdown` | `true` | 2 |
| `PadAutoClose` | `true` | 3 |

`meta.json` gains `language` (string or null, part 2). `session.json` gains
`Tabs[id].Bookmarks` (part 3) and `Windows` (part 5). Every new field is optional when reading, so
files written by v1.12 load unchanged, and unknown values fall back to the default.

## Architecture

New units under `Services/Pad/` hold no WPF types and are unit-tested directly:

| Unit | Part | Responsibility |
|---|---|---|
| `PadPalette` | 1 | Named colors for both themes; contrast helper |
| `MarkdownLineTokenizer` | 2 | One line (plus whether it is inside a fence) → styled spans |
| `FenceTracker` | 2 | Which lines are inside fenced blocks; recomputed from the text on change |
| `MarkdownFormatter` | 2 | The edit each Format item makes: wrap, unwrap, line prefixes |
| `PadLanguages` | 2 | Language ids, names, extension map, Auto resolution |
| `SyntaxColorMap` | 2 | Color name → category → palette color |
| `BraceFolding`, `HeadingFolding` | 2 | Fold ranges from text |
| `AutoClosePolicy` | 3 | What a typed character or Backspace does next to the caret |
| `LineOperations` | 3 | Duplicate, move, join, sort, dedupe, trim over lines, preserving endings |
| `OccurrenceFinder` | 3 | Whole-word matches of a word, capped |
| `SafeLinks` | 4 | Allowed schemes |
| `RtfWriter` | 4 | Text plus styled runs → RTF |
| `NumberConverter`, `ExpressionEvaluator`, `TextTools` | 5 | The Tools menu (Base64, GUID, timestamps) |
| `HistoryDiff` | 5 | DiffPlex wrapper → line rows with kind and numbers |
| `SessionWindows` | 5 | Session v2 read/write and migration |

WPF adapters under `Pad/`, each thin: `PadTheme` (applies a palette to a window), `EditorMenus`
(builds both context menus), `MarkdownColorizer`, `MarkdownBackgroundRenderer`, `BulletGenerator`,
`SyntaxHighlighting` (loads and recolors definitions), `FoldingController`, `AutoCloseHandler`,
`BookmarkMargin`, `OccurrenceHighlighter`, `SafeLinkGenerator`, `TabDragController`,
`DiffPreview`. `MicaPadWindow` wires them; its `ConfigureEditor` and `NewMenu` lose their literal
colors.

Embedded resources: `Pad/Highlighting/Ini.xshd`, `Yaml.xshd`, `Batch.xshd`, `Log.xshd`.
New package: `DiffPlex` 1.9.0 (part 5 only).

## Error handling

- Formatting, highlighting, folding and occurrence marking never throw into the UI: a failure
  logs a warning under `pad` once per note and shows that note as Plain text.
- Tools and Format items validate before editing; on a problem the text is untouched and the
  status bar explains.
- Clipboard writes are retried (4.4). *Show in folder* and link opening log failures and show a
  status message.
- Session v2 that cannot be read falls back to phase 1's rebuild (open notes by `modified`) into
  one window.

## Testing

**Automated** (xUnit, one file per unit, no real `%APPDATA%`, temp folders only):

- Part 1: both palettes complete and meeting every contrast rule; `PadTheme` values and fallback;
  config round trip; the window applies a switch to its resources and editor (built on the shared
  STA thread, not shown); both context menus build with the right enabled states for no
  selection, a selection, read-only preview and a file-backed vs scratch tab.
- Part 2: extension map and Auto resolution, including `PadMarkdown` off and an explicit choice;
  every AvalonEdit definition and the four MicaPad `.xshd` files load and every named color maps to
  a category; Markdown tokenizer cases (each element, nesting, unclosed markers, escapes,
  `snake_case`, Thai text, a line inside a fence); fence tracking across edits; each Format item
  on selection / no selection / toggle-off, as one undo step; brace folding ignores braces in
  strings and comments; heading folding levels; the 2 MB limit.
- Part 3: auto-close decision table; each line operation incl. CRLF/LF preservation, last line
  without a newline, and undo as one step; bookmark anchoring through edits and session round
  trip; occurrence counting and the cap.
- Part 4: allowed and refused link schemes; tab reorder saves the order; RTF output for runs,
  escaping of `\ { }` and non-ASCII (Thai) text.
- Part 5: number conversion table incl. overflow and negatives; evaluator precedence, power,
  unary minus, errors; Base64 round trip and invalid input; timestamps under `th-TH` are Gregorian;
  diff rows and summary; session v1 → v2 migration, v2 round trip, downgrade fields written,
  closing a window moves its tabs, `--pad` routing to the right window.

**Manual**, per part, on the owner's machine after each deploy:

1. Theme: switch with several tabs, find bar, history and popups open; restart; Settings and
   other MicaStats windows unchanged; title bar follows.
2. Right-click: every item on a scratch note and on a real file; Show in folder selects the file.
3. Markdown: a real note with headings, lists, tasks, code, quotes, links; typing stays smooth in a
   2 MB note; `.json`, `.cs`, `.ps1`, `.log`, `.ini` files open colored in both themes; folding.
4. Editing helpers: notepad4 key habits (Ctrl+D, Ctrl+Shift+↑/↓, Ctrl+J, F2) behave; auto-close
   does not get in the way while typing prose.
5. Ctrl+Click a link; a `file:` link does nothing; drag tabs; F11; paste Copy as RTF into Word.
6. Tools on real selections; compare a history version; two windows across a restart and
   *Open with* while two windows are open.

## Out of scope

Tables, images and HTML rendering in Markdown; clickable task checkboxes; saving fold state;
spell checking; auto-completion; a Markdown preview pane; themes for the rest of MicaStats;
dragging a tab out of a window to create one (the menu item covers it); per-window theme.
