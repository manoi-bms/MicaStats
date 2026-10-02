# MicaPad code blocks closer to Wiki.js — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Close the visible gap between MicaPad's fenced code and Wiki.js's (Prism.js) code blocks:
more languages, real TypeScript, Markdown inside a fence, function names in their own color, and a
Copy button on every fenced block.

**Asked by the owner on 2026-10-02.** After a comparison with Wiki.js (Prism with the autoloader,
about 290 languages, a copy-to-clipboard toolbar button), the owner answered "yes implement all" to:
(1) bash/sh, Pascal/Delphi, Go, Dockerfile, Markdown, Rust, Ruby, Kotlin and real TypeScript;
(2) a Copy button on fenced code blocks; (3) finer token colors (function names). This plan is the
design; there is no separate spec (a bounded change to existing flows: the language table,
MicaPad's own `.xshd` definitions, the fence highlighter and the editor's layers).

**Architecture:** New languages are MicaPad's own AvalonEdit definitions (`Pad/Highlighting/*.xshd`,
color names only, painted from the palette by `SyntaxColors`), listed in `PadLanguages` (status-bar
menu, file types) and `FenceLanguages` (fence words). Function names are a new syntax category
painted by a small pass that marks `identifier(` where the definition left the text uncolored, for
whole files and fences alike. The Copy button is a layer over the editor's text view that follows
the mouse over fenced blocks.

**Tech Stack:** .NET 8 WPF; AvalonEdit 6.3.1.120 (`.xshd`, `IHighlighter`, `HighlightedLine`,
`TextView` layers); xUnit.

## Global Constraints

- Repo `C:\AIProject\kil0bit-system-monitor`, branch `feat/micapad-code-languages` (from main 37cdaa6, v1.13.0). Commit on it; never push, merge or tag.
- Build and test only with `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe"`. Full suite: `timeout 500 env DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests 2>&1 | tail -5`. Known flaky: `McpPipeTests.At_most_four_calls_run_at_once_and_the_rest_wait_their_turn` — re-run once if it alone fails.
- Never build or publish into `bin\Release` (the controller deploys). Never launch MicaStats. Tests use temp folders only, never `%APPDATA%`, no network; UI tests only on the shared `UiThread.Run`, never shown.
- New code under `Services/` holds no WPF types. Files use LF line endings; keep them.
- The text is never changed by anything here. The Copy button only copies; it never moves the caret or the selection.
- `.xshd` files name their colors only (no hex), as `Pad/Highlighting/Batch.xshd` does; every color name must land in a `SyntaxColors` category that reads at 4.5:1 (`PadHighlightingTests.Every_named_color_reads_well_in_both_themes`). Allowed names for new definitions: `Comment`, `String`, `Char`, `Regex`, `Keywords`, `Bool`, `Null`, `Number`, `Type`, `Directive`, `Variable`, `Operator`, and (Task 2 on) `Function`.
- Exact limits that already exist stay: text over 2 MB is plain; a fenced block over 2,000 inside lines or a line over 4,000 characters gets no colors.
- C# string escapes: write non-ASCII as `\uXXXX` escapes. The Write/Edit tools can decode them into raw characters; after writing a `.cs` file run `LC_ALL=C grep -n '[^[:print:][:space:]]' <file>` and turn any raw non-ASCII character inside a string literal back into its escape (PowerShell `[IO.File]::ReadAllText` / `.Replace` / `[IO.File]::WriteAllText(path, text, [Text.UTF8Encoding]::new($false))`).
- `UseWindowsForms` is on: `Brush`, `Color`, `FontFamily`, `Point`, `Size`, `Button`, `Clipboard` and others are ambiguous in WPF files and tests — add `using X = System.Windows...;` aliases as existing files do.
- Logging: a failure is caught and logged once under `pad` with the exception type only; never note text.
- Commits: `git add` exact paths, `git commit -m "..." -m "Co-Authored-By: <your model name> <noreply@anthropic.com>"`, never amend.
- Never weaken an existing assertion. Where a list or count grows because this plan adds languages or words, change only that number/list and the test's name; where `.ts` changes from JavaScript to TypeScript, change that expectation only (ruling R3).
- Do not dispatch subagents.

## Rulings

- **R1 — own definitions, not a Prism port.** AvalonEdit cannot run Prism grammars; each new language is a hand-written `.xshd` covering comments, strings, numbers, keywords, built-in types and the language's one distinctive construct (Pascal `{$…}` directives, shell `$VAR`, Rust `#[…]`, Kotlin/TypeScript `@annotations`, Dockerfile instructions). Cost if wrong: a rare construct is uncolored.
- **R2 — Markdown inside a fence** (` ```md `, ` ```markdown `) uses a small own definition (`MarkdownCode.xshd`: headings, emphasis, inline code, links, list markers, quotes) and a fence-only language id `markdown-fence`, not listed in the status-bar menu: MicaPad's real Markdown formatting cannot nest inside a fence. Cost if wrong: Markdown in a fence is colored more simply than a Markdown note.
- **R3 — `.ts`, `.tsx`, `.mts`, `.cts` files and the fence words `ts`, `typescript`, `tsx` move from JavaScript to the new TypeScript language.** Cost if wrong: none (TypeScript colors are a superset).
- **R4 — function names**: an identifier directly followed (after optional spaces) by `(` is a function call or declaration, colored with the new Function color, only where the definition left that text uncolored (never inside a string, comment, keyword or other colored section), and only for languages that call functions that way (flag `PadLanguage.FunctionCalls`). AvalonEdit's C# `MethodCall` color now maps to Function too. Cost if wrong: an indexer or macro looks like a call in a flagged language.
- **R5 — Copy button**: shown while the mouse is over any line of a fenced block (fences, `$$` math and diagram source included) in a Markdown tab of the main editor; never in the history preview, never in other languages. It copies the inside lines exactly as they are in the note (line breaks kept, no trailing break), not the fence lines. It sits at the top-right of the block's first visible line, takes no focus, and never moves the caret or selection. Cost if wrong: layout only.

## Review Focus

1. A `foo(` inside a string or comment, and `if (`/`for (`/`while (`, never get the Function color (Task 2 test `Function_color_never_enters_strings_comments_or_keywords`).
2. Pascal: `{$IFDEF DEBUG}` is a directive, `{ note }` and `(* note *)` are comments, `'it''s'` is one string (Task 1 test rows in `Own_definitions_color_what_they_should`).
3. Shell: `$#`, `${#name}` and `"a # b"` are not comments; `# note` is (Task 1 test rows).
4. Clicking Copy changes neither the text, the caret nor the selection, and copies exactly the inside lines (Task 3 test `Copy_copies_the_inside_lines_and_leaves_the_editor_alone`).
5. Typing in a long C# file or fence costs no more than a regex scan of the drawn line for the function pass (Task 2 test `Lines_over_the_inline_limit_get_no_function_pass` plus review).

---

## File Structure

| File | Task | Responsibility |
|---|---|---|
| Create `Pad/Highlighting/{TypeScript,Shell,Pascal,Go,Dockerfile,Rust,Ruby,Kotlin,MarkdownCode}.xshd` | 1 | Definitions (embedded by the existing `Pad\Highlighting\*.xshd` item) |
| Modify `Services/Pad/PadLanguages.cs` | 1, 2 | New languages, file types and names, `FenceOnly`/`ForFence`; `FunctionCalls` flag (2) |
| Modify `Services/Pad/FenceLanguages.cs`, `Pad/FenceHighlighter.cs` | 1 | New words; fence lookup through `ForFence` |
| Modify `Services/Pad/SyntaxColors.cs`, `Services/Pad/PadPalette.cs` | 2 | `SyntaxCategory.Function`, `SyntaxFunction` colors |
| Create `Services/Pad/FunctionCalls.cs`, `Pad/FunctionCallHighlighter.cs`; modify `Pad/ThemedHighlightingColorizer.cs`, `Pad/FenceHighlighter.cs`, `Pad/EditorLanguage.cs` | 2 | The function pass |
| Create `Pad/CodeCopyLayer.cs`; modify `Pad/EditorLanguage.cs`, `Pad/MicaPadWindow.xaml.cs` | 3 | The Copy button |
| Modify `GUIDE.md`, `README.md` | 1, 3 | Languages; Copy button |

---

### Task 1: New languages, real TypeScript, Markdown inside a fence

**Files:**
- Create: `Pad/Highlighting/TypeScript.xshd`, `Shell.xshd`, `Pascal.xshd`, `Go.xshd`, `Dockerfile.xshd`, `Rust.xshd`, `Ruby.xshd`, `Kotlin.xshd`, `MarkdownCode.xshd`
- Modify: `Services/Pad/PadLanguages.cs`, `Services/Pad/FenceLanguages.cs`, `Pad/FenceHighlighter.cs` (`DefaultDefinition` uses `PadLanguages.ForFence`), `GUIDE.md`, `README.md` (EN and TH bullets on code colors)
- Test: `PadLanguagesTests.cs`, `FenceLanguagesTests.cs`, `PadHighlightingTests.cs`, `FenceColorsTests.cs`

**Interfaces:**
- Produces: `PadLanguages.All` gains, in this menu order: Plain, Markdown, JSON, XML, HTML, C#, JavaScript, **TypeScript** (`typescript`, Braces), CSS, PowerShell, **Shell** (`shell`, None), Python, SQL, C/C++, Java, **Kotlin** (`kotlin`, Braces), **Go** (`go`, Braces), **Rust** (`rust`, Braces), PHP, **Ruby** (`ruby`, None), **Pascal** (`pascal`, None; display name "Pascal/Delphi"), VB, Diff, **Dockerfile** (`dockerfile`, None), INI, YAML, Batch, Log — 28 languages. All new ones are `OwnDefinition: true` with `Definition` = the `.xshd` file name without extension.
- Produces: `public static IReadOnlyList<PadLanguage> FenceOnly` holding `new PadLanguage("markdown-fence", "Markdown", "MarkdownCode", true, PadFoldKind.None)`, and `public static PadLanguage? ForFence(string? id)` = `ById(id)` or a `FenceOnly` match (ignoring case). `ById` is unchanged (the menu and saved choices never see `markdown-fence`).
- Produces: file types — `.ts .tsx .mts .cts` → typescript; `.sh .bash .zsh .ksh` and the dot-names `.bashrc .bash_profile .zshrc .profile` → shell; `.pas .dpr .dpk .lpr .pp` → pascal; `.go` → go; `.rs` → rust; `.rb .rake .gemspec` → ruby; `.kt .kts` → kotlin; `.dockerfile` → dockerfile. `ForPath` also matches file names without a useful extension, ignoring case: `Dockerfile`, `Containerfile`, `Dockerfile.<anything>` → dockerfile; `Gemfile`, `Rakefile` → ruby.
- Produces: fence words — typescript: `ts typescript tsx mts cts` (the first three move from javascript); shell: `bash sh shell zsh ksh shellscript`; pascal: `pascal delphi pas objectpascal dpr`; go: `go golang`; dockerfile: `dockerfile docker containerfile`; rust: `rust rs`; ruby: `ruby rb`; kotlin: `kotlin kt kts`; markdown-fence: `md markdown`. 89 words in all.

**Required behavior and tests** (add each as a test first, see it fail, then implement):

- [ ] `PadLanguagesTests.Auto_picks_by_file_type` rows: `a.ts`→typescript (changed from javascript, R3), `a.tsx`→typescript, `a.mts`→typescript, `deploy.sh`→shell, `.bashrc`→shell, `Unit1.pas`→pascal, `Project1.dpr`→pascal, `main.go`→go, `lib.rs`→rust, `app.rb`→ruby, `Gemfile`→ruby, `Main.kt`→kotlin, `build.gradle.kts`→kotlin, `Dockerfile`→dockerfile, `dockerfile`→dockerfile, `Dockerfile.dev`→dockerfile, `app.dockerfile`→dockerfile, `Containerfile`→dockerfile; `a.jsx` stays javascript; `README` stays plain.
- [ ] `Languages_are_listed_once_with_names_and_fold_kinds`: 28 languages; the new fold kinds above; `PadLanguages.ById("markdown-fence")` is null and `PadLanguages.ForFence("markdown-fence")` is not.
- [ ] `FenceLanguagesTests`: every new word maps as listed (Theory rows), `ts`→typescript, `js` still javascript; the count test becomes 89 words and checks each word's language through `ForFence` has a definition that loads.
- [ ] `PadHighlightingTests.Own_definitions_color_what_they_should` rows (language, line, part, color name) — at least:
  - typescript: `interface A { b: string }` `interface` Keywords; `string` Type; `` let s = `x${y}` `` the template string String; `@Component()` `@Component` Directive.
  - shell: `# note` Comment; `echo $HOME` `$HOME` Variable; `echo ${#name}` `${#name}` Variable (not a comment); `echo $#` `$#` Variable; `echo "a # b"` the string String (no Comment inside); `if [ -f x ]; then` `if` and `then` Keywords; `#!/bin/bash` Directive.
  - pascal: `{$IFDEF DEBUG}` Directive; `{ note }` Comment; `(* note *)` Comment; `// note` Comment; `s := 'it''s';` `'it''s'` String; `BEGIN` Keywords (case-insensitive); `x: Integer;` `Integer` Type; `$FF` Number; `#13` Char.
  - go: `func main() {` `func` Keywords; `` s := `raw` `` the raw string String; `var x error` `error` Type; `x := nil` `nil` Null or Keywords.
  - dockerfile: `FROM node:20 AS build` `FROM` and `AS` Keywords; `RUN echo $HOME` `$HOME` Variable; `# note` Comment; `# syntax=docker/dockerfile:1` Directive.
  - rust: `fn main() {` `fn` Keywords; `#[derive(Debug)]` Directive; `let c = 'a';` `'a'` Char; `fn f<'a>(x: &'a str)` — `'a` is not a Char span (assert no Char section starts at the lifetime); `let v: Vec<u8>` `u8` Type; `println!("x")` `println!` Directive.
  - ruby: `def greet(name)` `def` Keywords; `# note` Comment; `@name = :sym` `@name` Variable and `:sym` Variable; `x = nil` `nil` Null or Keywords; `=begin` … block comment (one-line check: the `=begin` line is Comment).
  - kotlin: `fun main() {` `fun` Keywords; `val s = "a ${b}"` the string String; `@Test` Directive; `val n: Int = 1` `Int` Type.
  - markdown-fence (via `ForFence`): `# Title` Keywords-category color (name must categorize as Keyword, e.g. `HeadingKeywords`); `` `code` `` String-category; `[a](b)` link part Tag-category or Attribute-category; `> quote` Comment-category.
- [ ] `FenceColorsTests`: a ` ```bash ` fence colors `# note` like the Shell file does; a ` ```md ` fence colors `# Title`; a ` ```ts ` fence colors `interface`.
- [ ] `Every_language_with_colors_has_a_definition` and `Every_named_color_reads_well_in_both_themes` pass for the new languages (and for `FenceOnly`).
- [ ] GUIDE.md: the Markdown section's **Code** bullet lists the new words; "Colors for code, logs and settings files" lists the new languages and that TypeScript, Kotlin, Go and Rust fold at braces. README.md (EN and TH) "Colors for code and logs" bullet lists them.
- [ ] Full suite green; commit `feat(pad): TypeScript, shell, Pascal/Delphi, Go, Dockerfile, Rust, Ruby and Kotlin colors for files and fences; Markdown inside a fence`.

### Task 2: Function names in their own color

**Files:**
- Modify: `Services/Pad/SyntaxColors.cs`, `Services/Pad/PadPalette.cs`, `Services/Pad/PadLanguages.cs`, `Pad/ThemedHighlightingColorizer.cs`, `Pad/FenceHighlighter.cs`, `Pad/EditorLanguage.cs`
- Create: `Services/Pad/FunctionCalls.cs` (pure: finds the spans), `Pad/FunctionCallHighlighter.cs` (adds sections to a `HighlightedLine`; an `IHighlighter` decorator for whole files)
- Test: `SyntaxColorsTests.cs`, `PadPaletteTests.cs`, new `FunctionCallsTests.cs`, `FenceColorsTests.cs`

**Interfaces:**
- Produces: `SyntaxCategory.Function` (appended last); `PadPalette.SyntaxFunction` — Dark `#DCDCAA`, Light `#795E26` (both ≥ 4.5:1 on their backgrounds), included in the palette's property list.
- Produces: `SyntaxColors` row `(Function, "Function", "Method")`, placed so no existing name changes category except names containing Function/Method (C#'s `MethodCall`).
- Produces: `PadLanguage` gains `bool FunctionCalls = false` as its last positional parameter; true for csharp, javascript, typescript, java, cpp, php, python, powershell, kotlin, go, rust, ruby, pascal.
- Produces: `FunctionCalls.Find(string line, int maxLength)` → the (start, length) of each identifier (`[A-Za-z_$][A-Za-z0-9_$]*`, not starting with a digit) followed by optional spaces/tabs and `(`; empty for lines longer than `maxLength` (pass `MarkdownLineTokenizer.MaxInlineLength`).
- Produces: a pass that, given a `HighlightedLine`, adds a section with a shared `HighlightingColor { Name = "Function" }` for each found span that overlaps no existing section, keeping `Sections` sorted by offset (`HighlightedLine.ValidateInvariants` must hold). Used by `ThemedHighlightingColorizer` (via an `IHighlighter` decorator returned from `CreateHighlighter`, only when the language has `FunctionCalls`) and by `FenceHighlighter` (for a fence whose language has `FunctionCalls`). A throw in the pass costs only that line its function colors (logged once).

**Required behavior and tests:**
- [ ] `SyntaxColors.Categorize("Function")`, `("MethodCall")` → Function; every existing categorize test unchanged.
- [ ] Palette test: `SyntaxFunction` exists in both palettes at ≥ 4.5:1 (update the property-count assertion by one).
- [ ] `FunctionCallsTests`: `Console.WriteLine("x");` finds `WriteLine` (and `Console` is not found: not followed by `(`); `foo (1)` finds `foo`; `x = a[1](2)` finds nothing for `]`; `1foo(` finds nothing; `$(document)` finds `$`; a 4,001-character line finds nothing.
- [ ] `Function_color_never_enters_strings_comments_or_keywords` (C# whole file through `ThemedHighlightingColorizer`'s highlighter): `if (x) Foo("bar(") // baz(` → only `Foo` gets Function; `if`, `bar(` and `baz(` keep their own colors.
- [ ] Whole-file and fence: a Go ` ```go ` fence `func main() {` colors `main` Function; a ` ```sql ` fence `SELECT COUNT(*)` gets no Function sections from the pass (sql is not flagged); a C# file `void Run()` colors `Run`.
- [ ] `Lines_over_the_inline_limit_get_no_function_pass`.
- [ ] Full suite green; commit `feat(pad): function names get their own color in code files and fences`.

### Task 3: A Copy button on fenced code blocks

**Files:**
- Create: `Pad/CodeCopyLayer.cs`
- Modify: `Pad/EditorLanguage.cs` (installs it in the Markdown branch when `CodeCopy` is set; removes it in `Clear`), `Pad/MicaPadWindow.xaml.cs` (sets `_language.CodeCopy` for the main editor only), `GUIDE.md`, `README.md`
- Test: new `CodeCopyTests.cs`

**Interfaces:**
- Produces: `EditorLanguage.CodeCopy` — `Action<string>?`; null means no button (the history preview's language never sets it). The window sets it to copy through its existing `SetClipboardText` seam and then `ShowStatus("Copied")` (or "Copied N lines").
- Produces: `CodeCopyLayer` — a layer inserted into the `TextView` above the text (`TextView.InsertLayer(..., KnownLayer.Text, LayerInsertionPosition.Above)`), holding one small button (Segoe Fluent Icons copy glyph `\uE8C8` and the word "Copy", tooltip "Copy code", `AutomationProperties.Name` "Copy code", `Focusable=false`), colored from the MicaPad palette like the diagram pictures' hover buttons, following theme changes. Exposes for tests: `internal bool ButtonShown`, `internal Rect ButtonBounds` (in text-view coordinates), `internal int BlockOpeningLine`, `internal void ShowFor(int lineNumber)` (what a mouse move over that line does) and `internal void Click()`.

**Required behavior and tests:**
- [ ] `The_button_follows_the_mouse_over_fenced_blocks`: in a Markdown note with prose, a ` ```cs ` block and more prose, `ShowFor(insideLine)` shows it with `BlockOpeningLine` = the fence line, its bounds at the right edge of the text view and the top of the block's first visible line; `ShowFor(proseLine)` hides it; the opening and closing fence lines show it too.
- [ ] `Copy_copies_the_inside_lines_and_leaves_the_editor_alone`: with a selection elsewhere and the caret set, `Click()` passes exactly the inside text (a CRLF document keeps its `\r\n` between inside lines; no trailing break) to `CodeCopy`, and the document text, caret offset and selection are unchanged.
- [ ] An unclosed block copies to the document end; a block with no inside lines hides the button; a `$$` math block and a ` ```mermaid ` block show it too.
- [ ] A JSON tab never shows it; switching a Markdown tab to JSON removes the layer; the history preview editor has no `CodeCopyLayer`.
- [ ] Scrolling, an edit or the mouse leaving the text view hides the button until the next mouse move.
- [ ] GUIDE.md: the Markdown **Code** bullet says a **Copy** button appears at the top-right of a fenced block under the mouse and copies its code; README.md (EN and TH) MicaPad Markdown bullet mentions it.
- [ ] Full suite green; commit `feat(pad): a Copy button on fenced code blocks, as Wiki.js has`.

## After the last task (controller)

Final whole-branch review, one fix wave, a short rulings section appended to this plan (execution rulings, parked findings, the owner's e2e checklist: a `.sh`, `.pas`, `.go`, `.ts`, `Dockerfile` file and the same as fences in both themes; function names in C#/JS/Go; the Copy button over code, math and a diagram), then the deploy for e2e (standing rule).
