# MicaPad Markdown like Wiki.js — execution rulings and deferred findings

Spec: `docs/superpowers/specs/2026-10-01-micapad-markdown-wikijs-design.md`. Plans:
`docs/superpowers/plans/2026-10-01-micapad-markdown-a.md` (parts 1–5, styling) and
`docs/superpowers/plans/2026-10-01-micapad-markdown-b.md` (parts 6–7, pictures, settings, guide).
Branch `feat/micapad-markdown`, from main 9cb6ee4. Executed subagent-driven on 2026-10-01/02:
Plan A's seven tasks, each with a task review, then a whole-branch review and one fix wave; then
Plan B's six tasks the same way, with a final review of Plan B and its integration with Plan A and
one fix wave. Final suite: 3024 tests passing (twice in a row).

## Rulings on the spec (from the plans)

Plan A:

- **R1 — Copy as RTF follows the new inline looks** (inline code color, smaller sub/superscript). The copied text is unchanged. Cost if wrong: one color in pasted RTF.
- **R2 — monospace by block, not by span.** Fence, table and front-matter lines get the editor font for the whole line. Cost if wrong: none seen.
- **R3 — when the structure is rescanned** — *revised during execution, see ruling 6 below.*
- **R4 — inline extras only outside code.** Inside inline code, math and `<kbd>` nothing else is styled.
- **R5 — emoji glyphs are monochrome** (WPF has no color-font support).

Plan B:

- **R6 — one switch for pictures.** Math pictures and image previews follow **Draw diagrams**.
- **R7 — `$$` blocks reach the diagram engine through `DiagramKinds.FromFence`.**
- **R8 — the kroki form.** Built-in types (`mermaid`, `dot`, `markmap`) are drawn offline, never sent to Kroki; `math` and `kroki` are no types; any other type must be lower-case letters and digits. Cost if wrong: an upper-case type shows as code.
- **R9 — one preview row per line**, at the line's end; none inside fences, front matter or folded lines.
- **R10 — sizes.** Natural size is one pixel per image pixel; `=WxH` as a browser; any picture wider than the text area is shrunk to fit, an asked-for size included.
- **R11 — formats.** SVG by content, drawn through the diagram page as it is (no white card); everything else decoded by WPF at the shown size.
- **R12 — resolving a source** — *re-ruled during execution, see ruling 9 below.*
- **R13 — one 64-entry memory cache per window**; files re-read when a tab is shown again; web and data images once per session; reads and downloads off the UI thread.
- **R14 — web requests:** one GET through the system proxy, no cookies, at most 5 redirects, User-Agent "MicaPad", 10 s, 10 MB.
- **R15 — preview details:** "Loading…", the old preview stays while typing, the title is the tooltip, no menu, lines up to 200,000 characters searched.

## Rulings made during execution

Each with what it costs if wrong.

1. **Plan A Task 2 — the structure scan in the AI chat.** `FenceTracker.Classify(lines, mathBlocks)`; the chat passes `false`, so `$$` stays text in replies. `$$` closes only on exactly `$$`; front matter must close within the first 200 lines. Cost if wrong: none.
2. **Plan A Task 2 — rescans wait for the end of an update group** (AvalonEdit wraps every edit in one); detection stays per edit. Cost if wrong: a reader inside another `Changed` handler would see the previous structure (none exists).
3. **Plan A Task 4 — the history preview keeps the editor font** even when the reading font is on; the monospace family is cached and no-op font changes skip the redraw. Cost if wrong: none.
4. **Plan A Task 5 — fence highlighting fails alone.** A throw costs only that block its colors (logged once with the exception type), never the note's Markdown look; a block over 2,000 inside lines gets no colors at all (the global constraint), a block of exactly 2,000 does. Cost if wrong: long blocks lose colors entirely.
5. **Plan A Task 6 — Format table's range includes the delimiter row** when the caret is on the header (a plan defect). Cost if wrong: none.
6. **Plan A final review — R3 revised.** A whole-note rescan now happens only on a line-count change; on text holding `` ` ~ | $ ``; when the edited line's old or new text can be structural (setext/rule, `---`/`...` near the top, a fence, `$$`, a callout class, `*[`, a delimiter row, a pipe line next to a table); or when the next line is a full setext underline. Typing in list items, quotes and prose rescans nothing (measured 8.5 ms per keystroke before at 12k lines). A 240,000-edit fuzz against a fresh scan found no difference. Cost if wrong: a structure change shows late.
7. **Plan A final review — fence colors follow edits.** An edit inside a block with a language redraws the rest of the block; the highlighter keeps the states above the edit (a keystroke at line 1,980 of a 1,990-line block: 20 ms → 1.9 ms). Cost if wrong: none.
8. **Plan A final review — emoji codes need room.** A `:code:` counts only when no letter or digit touches it (`user:id:42`, MAC addresses, ratios stay text), unlike markdown-it-emoji: in an editor the caret cannot enter a glyph, so a false glyph inside a word is worse than in Wiki.js's view. "Letter or digit" is ASCII only, so Thai text glued to a code still shows the emoji (Thai has no spaces between words). An emoji failure leaves codes as text and the note Markdown. `[Note]: text` is no reference definition (CommonMark's shape). Cost if wrong: `word:smile:` shows text.
9. **Plan B Task 3 — R12 re-ruled for privacy.** A security review found that UNC, protocol-relative (`//host/x.png`), percent-encoded and `file://host` sources reached other machines over SMB with web images off (credential-hash exposure, an "opened" beacon). Now, after normalization: device paths (`\\.\`, `\\?\`, `\??\`, any slash spelling) are always "Image not found"; any other `\\` path is a web image (needs **Load images from the web**) unless it is on the same `\\host\share` as the note's own file. The Settings card keeps the spec's text; the guide says so. Image search is linear (parenthesis nesting capped at 32, as cmark). Cost if wrong: an image on another share needs the web switch.
10. **Plan B Task 4 — image rows redraw only for their own source** (a gallery of more than 24 pictures used to decode them all again on every finished load); images claiming more than 100 megapixels are refused; the stand-in preview while typing is used only when the line's image count is unchanged. Cost if wrong: none.
11. **Plan B final review — one fix wave.**
   - Every image line shows its picture when several loads end together (they used to stay "Loading…" after the first): each row redraws its own document line, also after the typing pause and a resize.
   - The resize pause is injectable, so tests aren't redrawn by it (it was the suite's intermittent failure).
   - An SVG image is sized by its px `width`/`height` before its viewBox (Material-style icons show at their size); Kroki pictures are unchanged.
   - `\require` of an unknown TeX package says so instead of "MathJax retry".
   - Web downloads start on the thread pool, not inside layout; `file:///uploads/a.png` (no drive) is "Image not found".
   - A phone photo is shown upright as its EXIF orientation says, as browsers and Wiki.js show it.
   - Cost if wrong: a larger fix diff.

## Parked (real, deferred)

- **Image cache bytes.** The per-window cache holds 64 entries of up to 20 MB each with no byte budget; each tab's board keeps its shown images' bytes until the tab switches. Follow-up: a byte budget.
- **Plan A inline edge cases.** R4 leaks at span boundaries (`*a $x*y$ b*`); `arr[i][j]` and `[^abc]` read as a reference link / footnote without checking defined labels (fix: collect labels in the structure scan, as abbreviations are); `$HOME/$USER` dims as math; `{# x #}` dims as attributes; emoji inside `$math$`, `<kbd>` and URLs.
- **Contrived long lines.** Quadratic scans on 4,000-character lines of unclosed `<kbd>` (49 ms) and 200,000-character backtick staircases in the image search (about 230 ms).

## Deferred minors (left as they are)

- Format table: the caret lands at column 0 of its line; mixed line endings become the header's; emoji codes count by their text width; the structure is scanned on each menu open.
- Small text (footnote references, sub/superscript) is 0.75 of the editor size, not of the surrounding heading.
- `LetGoOfShown` leaves the empty editor in the reading font until the next tab.
- Per-element `Typeface`/decoration allocations in the colorizer; a `Pen` per rule line.
- `FromWord("kroki")` returns a placeholder kind outside `DiagramBlocks.Read` (no caller does that).
- The C1 regression test waits 300 ms for file reads (a deterministic renderer-release version exists); a misleading `PumpUntil` message in the resize test.
- Test gaps: the Callout and `.` rescan triggers, quotes deeper than 6, a lone `$$` at board level, the image generator's catch paths, menu suppression over a preview; the Settings card tests check source text.
- A missing image created later keeps "Image not found" until a tab switch or setting change (R13).

## Owner's manual e2e checklist

- **A Wiki.js page pasted into a note:** headings (also `===`/`---` underlines), tables and Format → Format table (with Thai), callouts (`> text` then `{.is-info}` / `{.is-warning}`…), nested quotes, footnotes, `:emoji:` (also right after Thai text), `<kbd>Ctrl</kbd>`, `H~2~O` / `x^2^`, front matter — in both themes, with **Reading font** on and off; nested quote bars against the `>` markers.
- **Code:** a ```` ```csharp ````, ```` ```json ```` and ```` ```sql ```` block are colored; type `/*` inside one and the lines below turn comment-colored; type a language word after a bare ```` ``` ````.
- **Typing speed** in a long note with fences, tables, lists, math and images.
- **Math:** a formula in `$$` lines, ```` ```math ````, `\ce{2H2 + O2 -> 2H2O}`, a mistake (`\frac{1}{2` → "Missing close brace"), dark/light, Copy picture into Word (light version).
- **The kroki form:** ```` ```kroki ```` + `plantuml` with Kroki off (the message) and on; ```` ```kroki ```` + `mermaid` drawn offline.
- **Images:** a `.md` file with a relative PNG and a JPEG phone photo (upright), a note with a full path, `=200x`, two images side by side, several images on separate lines (all load, also after a tab switch), an SVG icon (its own size); a web image with **Load images from the web** off (the message) and on; a `\\server\share` image with it off (the message); **Draw diagrams** off; resize the window with a full-width photo (no scrollbar flicker).
