# MicaPad phase 2, Part 5: tools, history diff, more than one window — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** MicaPad gets a Tools menu (Base64, number bases, GUIDs, timestamps, a safe calculator), a history version can be compared line by line with the current text, and MicaPad can have more than one window — each with its own tabs, place and zoom — without ever closing a note by closing a window, and with a session file that older MicaPads still read.

**Architecture:** Pure rules in `Services/Pad` (`NumberConverter`, `ExpressionEvaluator`, `TextTools`, `HistoryDiff`, `SessionWindows`, `WindowTabs`) tested without WPF. `PadWorkspace.Open` stays the one list of open notes (autosave, snapshots and closing are unchanged); each `OpenNote` gains the id of the window showing it, and the workspace keeps one `WindowTabs` list per window in step with `Open`. `MicaPadWindow` becomes one of several windows over one workspace: it builds documents only for its own tabs, hands a tab's document (undo, bookmarks) to another window when the tab moves, and a small static registry finds windows by id and by activation order. Thin WPF adapters in `Pad/`: `EditorMenus.ToolsMenu`, `DiffPreview`.

**Tech Stack:** .NET 8 WPF, AvalonEdit 6.3.1.120 (`IBackgroundRenderer`, `AbstractMargin` — its `TextView` is set when added to `TextArea.LeftMargins`; `TextDocument.Changed` is `EventHandler<DocumentChangeEventArgs>` — verified by reflection), DiffPlex 1.9.0 (`Differ.Instance.CreateDiffs(string oldText, string newText, bool ignoreWhiteSpace, bool ignoreCase, IChunker chunker)` — not obsolete, unlike `IDiffer.CreateLineDiffs`; `LineChunker.Instance`; `DiffResult.PiecesOld`/`PiecesNew` are `IReadOnlyList<string>`, `DiffBlocks` are `DeleteStartA/DeleteCountA/InsertStartB/InsertCountB`; an empty text has **no** pieces; `"a\nb\n"` chunks to `a`, `b`, `""`; CRLF and LF lines compare equal — all verified by reflecting and probing the 1.9.0 package; its time grows with the lines times the changes: two 1 MB texts of ~30-character lines that differ on every line take about 2 s, 50,000 changed lines about 1 s, but 1 MB of short lines that differ throughout (500,000 one-character lines, or 200,000 shuffled numbers) did not finish in 60 s — hence `HistoryDiff.MaxChangedLines`), xUnit.

**Spec:** `docs/superpowers/specs/2026-09-30-micapad-phase2-design.md` — Part 5 (5.1 Tools, 5.2 History diff, 5.3 More than one window), *Settings and storage* (`session.json` gains `Windows`), *Architecture* (`NumberConverter`, `ExpressionEvaluator`, `TextTools`, `HistoryDiff`, `SessionWindows`, `DiffPreview`), *Error handling* (session v2 that cannot be read → phase 1 rebuild into one window), *Testing* Part 5 and manual item 6.

**Starts from:** Part 4 committed — `ShowStatus`, `TabDragController`, `BuildMainMenu()`, full screen, and *Copy as RTF* in both menus (this plan's menu tests list it) — including the Part 4 fix wave: `OnClosing`'s hide path leaves full screen (`if (IsFullScreen) ToggleFullScreen();`, commit 6c15e45) and `PadFullScreenTests` asserts on the saved `Maximized` in three tests (`The_saved_placement_is_the_one_before_full_screen`, `Entering_from_maximized_saves_maximized`, `Closing_hides_the_window_windowed`); `TrySetClipboard` is `Func<string, string, bool>` (ae39d95; nothing here uses it). Where a step replaces code the fix wave may still touch (the `TabDragController` construction, `OnClosing`), it says which line to change rather than retyping the call, so a later fix to the rest survives.

## Global Constraints

- **Tools** (`Tools ▸` in the right-click menu and `☰`): Base64 encode, Base64 decode, Convert number ▸ Decimal / Hex / Binary / Octal, Insert GUID, Insert timestamp ▸ ISO 8601 / Date / Unix seconds, Evaluate. Each is **one undoable edit** (through `EditorMenus.ApplyEdit`); a tool that cannot apply leaves the text alone and says why with `ShowStatus` (5 s). Selection tools work on the selection without the spaces and line breaks around it (those stay where they are); they are disabled with nothing selected and refuse a rectangular selection with a message. Insert GUID and Insert timestamp replace the selection, like typing.
- **Numbers**: one integer — decimal, `0x`, `0b` or `0o`, optional sign, underscores only between digits — from −2⁶³ to 2⁶⁴−1; output `255`, `0xFF`, `0b11111111`, `0o377`; a negative keeps its sign in every base (`-0x3`).
- **Timestamps**: `2026-09-30T18:05:12+07:00`, `2026-09-30`, `1790766312` — always Gregorian and `CultureInfo.InvariantCulture`, also under `th-TH` (whose calendar would write 2569). GUIDs lowercase `D` format.
- **Evaluate**: MicaPad's own recursive-descent parser — nothing is compiled or executed. `+ - * / % ^`, parentheses, unary minus, decimal and `0x` numbers, computed in `decimal`; `^` is right-associative and binds tighter than unary minus (`2^3^2` = 512, `-2^2` = −4); nesting deeper than 200 is refused (no stack overflow can ever take MicaStats down); division by zero, syntax errors and results too large are reported, never thrown. Appends ` = <result>` after the selection and selects the result.
- **History diff**: the preview banner's **Compare with current** toggles the read-only preview between the version and a line diff against the current text: unchanged lines plain, removed lines on a red tint, added lines on a green tint (the palette's `DiffRemoved`/`DiffAdded` at low alpha), a margin with the old and current line numbers and a `−`/`+` glyph, and `+12 −3 lines` (or *No changes*) in the banner. Computed with DiffPlex 1.9.0 off the UI thread and applied on the dispatcher only if the same version is still previewed. Either text over 1 MB (1,048,576 characters, like MicaPad's other MB limits) shows *Too large to compare* and keeps the version on screen; so do texts with more than 50,000 lines between their common first and last lines (`HistoryDiff.MaxChangedLines`: DiffPlex would otherwise keep a core busy for minutes on 1 MB of short lines that differ throughout — a long note with a few changes stays far below it). The continuation that shows the result runs on the dispatcher through `Guard`, so nothing it does can throw into MicaStats. **Restore** and **Copy all** always use the version itself, never the diff text.
- **Windows**: `Ctrl+Shift+N` and `☰ → New window` open a window with one new note. The tab menu gains **Move to new window** (disabled for a window's only tab) and **Move to ▸** (each other window, by its active tab's title; absent when there is no other window). Each window has its own tabs, active tab, placement, always-on-top, zoom and full screen; theme, font, wrap, line numbers, Markdown and auto-close stay global. `×` with another window open moves its tabs (documents, undo, bookmarks) to the end of the most recently active other window and closes for real — **no note is closed by closing a window**; the last window hides as today, keeping its tabs. The hotkey, the overlay and `--pad` bring the most recently active window forward; a file goes there unless it is already open in another window, which then comes forward on that tab. The first show in a run brings back every window of the session, each at its placement (so reopen at login restores every window). All windows share the MicaPad taskbar identity (already applied per window in `OnSourceInitialized`).
- **Session format**: `session.json` gains `Windows` (each: `Id`, `Open`, `Left`, `Top`, `Width`, `Height`, `Maximized`, `AlwaysOnTop`, `Zoom`, `NoteIds` in tab order, `ActiveNoteId`, `LastActiveUtc`); `Tabs` stays shared. A session without `Windows` (v1.12) loads as one window from the old fields. For downgrade safety every save also writes the old top-level fields: the first window's placement, zoom, on-top and active note, `OpenNoteIds` listing **every** window's notes (first window's first), and `WindowOpen` = any window open — so v1.12 opens every tab in its one window. A session v2 that cannot be read falls back to phase 1's rebuild (open notes by `modified`) into one window. Files written before this part load unchanged.
- New code under `Services/Pad/` holds no WPF types. Only MicaPad changes.
- Tests never touch the real `%APPDATA%`, never launch MicaStats, never start Explorer, never use the network (the DiffPlex restore at build time is the one exception, and not from tests), never show windows (the window tests replace `MicaPadWindow.ShowWindow`) and never create their own STA thread (use `UiThread.Run`; a second WPF thread deadlocked the suite once). Never build or publish into `bin\Release`.
- **UI tests interleave.** Every UI test runs on the one shared UI thread, and a test that pumps (`PadLanguageWindowTests.Pump()` pushes a nested dispatcher frame) runs other classes' queued tests inside it — observed: `PadWindowsTests` running inside `PadHistoryDiffTests.Switching_versions_while_comparing…` with four windows of two workspaces registered. So a test never asserts on, or acts through, a process-wide static that another test's windows can be in: it asks per workspace (`MicaPadWindow.WindowsOf(workspace)`, `CurrentOf(workspace)`, `PrepareAllForExit(workspace)`), and `ShowWindow` is replaced only by `PadWindowsTests` (one class, so its tests never run inside each other).
- Build/test only with `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe"`; prefix full-suite runs with `timeout 300`. `CultureInfo.InvariantCulture` for number and date text (the owner's machine runs th-TH, whose calendar is Buddhist: `DateTime.ToString` without InvariantCulture stamps 2569).
- Commits: `git add` exact paths, `-m` messages (no heredoc), never amend, `Co-Authored-By:` trailer naming the model that wrote the commit.

## Review Focus

1. **An old session.json after downgrade and upgrade** — a v1.12 file (no `Windows`) opens as one window with its tabs, place and zoom; a v2 file still carries every tab in the old fields, so v1.12 shows them all; a file v1.12 rewrote (Windows dropped) comes back as one window with every tab; a v2 file that cannot be read is rebuilt into one window (Task 3: `A_v1_session_becomes_one_window_from_the_old_fields`, `A_v2_file_keeps_the_v1_fields_an_older_micapad_reads`, `A_session_an_older_micapad_rewrote_loads_as_one_window_with_every_tab`, `An_unreadable_session_is_rebuilt_into_one_window`).
2. **Closing a window with unsaved-to-file tabs, and two windows never writing each other's text** — the tabs move with their edits, undo and unsaved-file state, nothing is closed, the real file is untouched; a tab created in one window is always saved from that window's editor (Task 3: `Closing_a_window_keeps_unsaved_file_edits_in_the_other_window`; Task 4: `A_new_tab_in_one_window_is_saved_from_that_windows_editor`; Task 5: `Closing_a_window_moves_its_tabs_documents_and_undo_to_the_most_recent_window`, `Closing_a_window_keeps_unsaved_file_edits`).
3. **`--pad` or Open with on a file already open in another window** — that window comes forward on that tab, the file is never opened twice, case differences in the path do not matter (Task 3: `A_file_goes_to_the_window_already_showing_it_else_to_the_most_recent`; Task 6: `Opening_a_file_open_in_another_window_brings_that_window_forward_on_its_tab`, `Ctrl_o_of_a_file_open_in_another_window_shows_it_there`).
4. **Tool inputs at the edges** — `2^3^2`, `-2^2`, `2^64`, `2^1000`, `2^-100` (0, not "too large") and `0.1^-100` (too large, not a division by zero), a 29-digit literal, 10,000 nested parentheses, `0xFFFFFFFFFFFFFFFF` and 2⁶⁴, Base64 of Thai text and emoji, timestamps under `th-TH` (Task 1: `Evaluates`, `Reports_problems_instead_of_throwing`, `Deep_nesting_is_an_error_not_a_crash`, `Converts`, `Refuses`, `Base64_round_trips_thai_and_emoji`, `Timestamps_are_gregorian_under_the_thai_culture`).
5. **A diff of two 1 MB texts, and a diff that finishes late** — exactly 1 MB is compared, one character more is *Too large to compare*, so are texts that differ on more than 50,000 lines (DiffPlex's worst case), while 300,000 short lines with one change still compare; a result for a version no longer previewed is dropped (Task 2: `Two_texts_of_exactly_1_MB_are_compared`, `Either_text_over_1_MB_is_too_large`, `Texts_that_differ_on_too_many_lines_are_too_large`, `A_long_note_of_short_lines_with_one_change_is_compared`, `A_compare_finishing_after_back_is_dropped`, `Switching_versions_while_comparing_shows_only_the_new_versions_diff`).

## File structure

| File | Task | Responsibility |
|---|---|---|
| `Services/Pad/NumberConverter.cs` (new) | 1 | One integer in any of four bases → another base |
| `Services/Pad/ExpressionEvaluator.cs` (new) | 1 | Recursive-descent arithmetic in `decimal`, problems as messages |
| `Services/Pad/TextTools.cs` (new) | 1 | Base64, GUID, timestamps; each tool as one `TextEdit` or a problem |
| `Pad/EditorMenus.cs` | 1 | `ToolsMenu`, `RunTool` |
| `Pad/MicaPadWindow.xaml(.cs)` | 1–6 | Tools in both menus; compare in the banner; one window per session window, registry, new/close/move/route |
| `Kil0bitSystemMonitor.csproj` | 2 | `DiffPlex` 1.9.0 |
| `Services/Pad/HistoryDiff.cs` (new) | 2 | DiffPlex → rows with kind and old/new line numbers, summary, 1 MB limit |
| `Pad/DiffPreview.cs` (new) | 2 | Rows into the read-only preview: tints and the −/+ margin |
| `Services/Pad/NoteMeta.cs` | 3 | `PadWindowState`, `SessionState.Windows` |
| `Services/Pad/SessionWindows.cs` (new) | 3 | v1 → v2, checks, downgrade mirror, routing, activation order, cascade |
| `Services/Pad/WindowTabs.cs` (new) | 3 | One window's tabs, kept in step with `Open` |
| `Services/Pad/OpenNote.cs` | 3 | `WindowId` |
| `Services/Pad/NoteStore.cs` | 3 | `LoadSession` normalizes windows |
| `Services/Pad/PadWorkspace.cs`, `PadWorkspace.Files.cs`, `PadWorkspace.Windows.cs` (new) | 3 | Per-window tabs, active tab, new/move/close window, routing, restore and save |
| `App.xaml.cs` | 5, 6 | Every window captured at exit; `--pad` routing; reopen when any window was open |
| `GUIDE.md`, `README.md` | 1, 2, 5, 6 | User docs, both MicaPad feature lists and both key tables |
| tests: `NumberConverterTests.cs`, `ExpressionEvaluatorTests.cs`, `TextToolsTests.cs`, `PadToolsTests.cs`, `HistoryDiffTests.cs`, `PadHistoryDiffTests.cs`, `SessionWindowsTests.cs`, `PadWorkspaceWindowsTests.cs`, `PadWindowTabsTests.cs`, `PadWindowsTests.cs` (new); `PadMenuTests.cs`, `PadFullScreenTests.cs` (changed) | all | |

Focused test command (Git Bash, repo root):

```bash
DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~<TestClass>" -nologo
```

Whole suite: `timeout 300` + the same without `--filter`.

**Why six tasks, not four:** the brief allows splitting the window work. It is split three ways so each review has one question: Task 4 *does one window over a shared workspace still behave, and never touch another window's documents?* (no new UI), Task 5 *are new and closed windows safe?* (registry, `Ctrl+Shift+N`, `×`, exit), Task 6 *does everything land in the right window?* (routing, Move to, reopen at login).

---

### Task 1: Tools

**Files:**
- Create: `Services/Pad/NumberConverter.cs`, `Services/Pad/ExpressionEvaluator.cs`, `Services/Pad/TextTools.cs`
- Modify: `Pad/EditorMenus.cs` (`ToolsMenu`, `RunTool`), `Pad/MicaPadWindow.xaml.cs` (both menus, `Now`), `GUIDE.md`, `README.md` (both MicaPad feature lists)
- Create: `tests/Kil0bitSystemMonitor.Tests/NumberConverterTests.cs`, `tests/Kil0bitSystemMonitor.Tests/ExpressionEvaluatorTests.cs`, `tests/Kil0bitSystemMonitor.Tests/TextToolsTests.cs`, `tests/Kil0bitSystemMonitor.Tests/PadToolsTests.cs`
- Modify: `tests/Kil0bitSystemMonitor.Tests/PadMenuTests.cs` (the editor menu gains Tools)

**Interfaces:**
- Consumes: `TextEdit(int Offset, int Length, string Text, int SelectionStart, int SelectionLength)` (`Services/Pad/MarkdownFormatter.cs`); `EditorMenus.Item(string header, string? gesture, Action action, bool enabled = true, string? icon = null)`, `EditorMenus.ApplyEdit(TextEditor, TextEdit)` (one update group = one undo step, selection clamped); `MicaPadWindow.ShowStatus(string)` (Part 4), `FillEditorMenu`, `BuildMainMenu()`, `PreviewPanel`.
- Produces:
  - `public enum NumberBase { Decimal, Hex, Binary, Octal }`; `public static class NumberConverter` — `const string NotANumber`, `const string TooLarge`, `bool TryParse(string text, out bool negative, out ulong magnitude, out string? problem)`, `string Format(bool negative, ulong magnitude, NumberBase target)`, `(string? Text, string? Problem) Convert(string text, NumberBase target)`.
  - `public static class ExpressionEvaluator` — `const int MaxDepth = 200`; `const string SyntaxError, DivisionByZero, ResultTooLarge, NumberTooLarge, NotReal, TooDeep`; `(decimal Value, string? Problem) Evaluate(string expression)`; `string Format(decimal value)`.
  - `public readonly record struct ToolOutcome(TextEdit? Edit, string? Problem)` with `Fail(string)`, `Apply(TextEdit)`; `public static class TextTools` — `const string SelectFirst, NotBase64, NotUtf8`; `string Base64Encode(string)`, `(string? Text, string? Problem) Base64Decode(string)`, `string FormatGuid(Guid)`, `string Iso8601(DateTimeOffset)`, `string Date(DateTimeOffset)`, `string UnixSeconds(DateTimeOffset)`, `ToolOutcome OnSelection(string text, int start, int length, Func<string, (string? Text, string? Problem)> transform)`, `ToolOutcome Insert(int start, int length, string inserted)`, `ToolOutcome Evaluate(string text, int start, int length)`.
  - `EditorMenus.ToolsMenu(TextEditor editor, Action<string> report, Func<DateTimeOffset> now)` → `MenuItem`; `EditorMenus.RunTool(TextEditor editor, Action<string> report, Func<string, int, int, ToolOutcome> tool)` → `bool`; `const string EditorMenus.RectangleRefused`.
  - Window: `internal Func<DateTimeOffset> Now { get; set; }` (tests fix the clock).

- [ ] **Step 1: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/NumberConverterTests.cs`:

```csharp
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Tools ▸ Convert number: one integer in any of four bases, into another.</summary>
    public class NumberConverterTests
    {
        [Theory]
        [InlineData("255", NumberBase.Hex, "0xFF")]
        [InlineData("255", NumberBase.Binary, "0b11111111")]
        [InlineData("255", NumberBase.Octal, "0o377")]
        [InlineData("0xFF", NumberBase.Decimal, "255")]
        [InlineData("0XfF", NumberBase.Decimal, "255")]
        [InlineData("0b1010", NumberBase.Decimal, "10")]
        [InlineData("0o17", NumberBase.Decimal, "15")]
        [InlineData("-3", NumberBase.Decimal, "-3")]
        [InlineData("-3", NumberBase.Hex, "-0x3")]
        [InlineData("-3", NumberBase.Binary, "-0b11")]
        [InlineData("+42", NumberBase.Hex, "0x2A")]
        [InlineData(" 42 ", NumberBase.Hex, "0x2A")]
        [InlineData("1_000_000", NumberBase.Hex, "0xF4240")]
        [InlineData("0xFFFF_FFFF", NumberBase.Decimal, "4294967295")]
        [InlineData("007", NumberBase.Decimal, "7")]
        [InlineData("0", NumberBase.Binary, "0b0")]
        [InlineData("-0", NumberBase.Hex, "0x0")]
        [InlineData("18446744073709551615", NumberBase.Hex, "0xFFFFFFFFFFFFFFFF")]      // 2^64 - 1
        [InlineData("0xFFFFFFFFFFFFFFFF", NumberBase.Decimal, "18446744073709551615")]
        [InlineData("-9223372036854775808", NumberBase.Hex, "-0x8000000000000000")]     // -2^63
        [InlineData("-0x8000000000000000", NumberBase.Decimal, "-9223372036854775808")]
        public void Converts(string text, NumberBase target, string expected)
        {
            var (result, problem) = NumberConverter.Convert(text, target);
            Assert.Null(problem);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("18446744073709551616", NumberConverter.TooLarge)]          // 2^64
        [InlineData("0x1_0000_0000_0000_0000", NumberConverter.TooLarge)]       // 2^64 in hex
        [InlineData("-9223372036854775809", NumberConverter.TooLarge)]          // -2^63 - 1
        [InlineData("12ab", NumberConverter.NotANumber)]
        [InlineData("0x", NumberConverter.NotANumber)]
        [InlineData("0b102", NumberConverter.NotANumber)]
        [InlineData("1.5", NumberConverter.NotANumber)]
        [InlineData("_1", NumberConverter.NotANumber)]
        [InlineData("1_", NumberConverter.NotANumber)]
        [InlineData("0x_FF", NumberConverter.NotANumber)]
        [InlineData("--5", NumberConverter.NotANumber)]
        [InlineData("1 000", NumberConverter.NotANumber)]
        [InlineData("๒๕๕", NumberConverter.NotANumber)]                          // Thai digits are not ASCII digits
        [InlineData("", NumberConverter.NotANumber)]
        public void Refuses(string text, string problem)
        {
            var (result, reason) = NumberConverter.Convert(text, NumberBase.Hex);
            Assert.Null(result);
            Assert.Equal(problem, reason);
        }
    }
}
```

`tests/Kil0bitSystemMonitor.Tests/ExpressionEvaluatorTests.cs`:

```csharp
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Tools ▸ Evaluate: MicaPad's own arithmetic, precedence first, every problem a message.</summary>
    public class ExpressionEvaluatorTests
    {
        [Theory]
        [InlineData("1+2*3", "7")]
        [InlineData("(1+2)*3", "9")]
        [InlineData("100-10-1", "89")]                  // left-associative
        [InlineData("64/4/2", "8")]
        [InlineData("2^3^2", "512")]                    // right-associative: 2^(3^2)
        [InlineData("-2^2", "-4")]                      // ^ binds tighter than unary minus
        [InlineData("(-2)^2", "4")]
        [InlineData("2^-1", "0.5")]
        [InlineData("2^-100", "0")]                     // tiny, not too large: nothing within 15 decimals
        [InlineData("10^-3", "0.001")]
        [InlineData("2*-3", "-6")]
        [InlineData("--3", "3")]
        [InlineData("-3+5", "2")]
        [InlineData("10%4", "2")]
        [InlineData("7/2", "3.5")]
        [InlineData("0.1+0.2", "0.3")]                  // decimal, not double
        [InlineData("1/3", "0.333333333333333")]        // at most 15 decimals
        [InlineData(".5+.5", "1")]
        [InlineData("1.5*1.07", "1.605")]
        [InlineData("0xFF+1", "256")]
        [InlineData("0x10*2", "32")]
        [InlineData("4^0.5", "2")]
        [InlineData("2^64", "18446744073709551616")]     // exact
        [InlineData("0^0", "1")]
        [InlineData(" 3 * ( 4 - 1 ) ", "9")]
        public void Evaluates(string expression, string expected)
        {
            var (value, problem) = ExpressionEvaluator.Evaluate(expression);
            Assert.Null(problem);
            Assert.Equal(expected, ExpressionEvaluator.Format(value));
        }

        [Theory]
        [InlineData("1/0", ExpressionEvaluator.DivisionByZero)]
        [InlineData("5%0", ExpressionEvaluator.DivisionByZero)]
        [InlineData("0^-1", ExpressionEvaluator.DivisionByZero)]
        [InlineData("1+", ExpressionEvaluator.SyntaxError)]
        [InlineData("(1+2", ExpressionEvaluator.SyntaxError)]
        [InlineData("1+2)", ExpressionEvaluator.SyntaxError)]
        [InlineData("2 3", ExpressionEvaluator.SyntaxError)]
        [InlineData("2x", ExpressionEvaluator.SyntaxError)]
        [InlineData("abc", ExpressionEvaluator.SyntaxError)]
        [InlineData("1,000+1", ExpressionEvaluator.SyntaxError)]
        [InlineData("0x", ExpressionEvaluator.SyntaxError)]
        [InlineData(".", ExpressionEvaluator.SyntaxError)]
        [InlineData("", ExpressionEvaluator.SyntaxError)]
        [InlineData("2+2 = 4", ExpressionEvaluator.SyntaxError)]
        [InlineData("2^1000", ExpressionEvaluator.ResultTooLarge)]
        [InlineData("0.1^-100", ExpressionEvaluator.ResultTooLarge)]                          // 10^100, not a division by zero
        [InlineData("79228162514264337593543950335+1", ExpressionEvaluator.ResultTooLarge)]   // decimal.MaxValue + 1
        [InlineData("99999999999999999999999999999", ExpressionEvaluator.NumberTooLarge)]     // 29 digits
        [InlineData("0x1FFFFFFFFFFFFFFFF", ExpressionEvaluator.NumberTooLarge)]
        [InlineData("(-8)^(1/3)", ExpressionEvaluator.NotReal)]
        public void Reports_problems_instead_of_throwing(string expression, string problem)
        {
            Assert.Equal(problem, ExpressionEvaluator.Evaluate(expression).Problem);
        }

        [Fact]
        public void Deep_nesting_is_an_error_not_a_crash()
        {
            string parentheses = new string('(', 10_000) + "1" + new string(')', 10_000);
            string minuses = new string('-', 10_000) + "1";
            string powers = string.Join("^", Enumerable.Repeat("1", 10_000));

            Assert.Equal(ExpressionEvaluator.TooDeep, ExpressionEvaluator.Evaluate(parentheses).Problem);
            Assert.Equal(ExpressionEvaluator.TooDeep, ExpressionEvaluator.Evaluate(minuses).Problem);
            Assert.Equal(ExpressionEvaluator.TooDeep, ExpressionEvaluator.Evaluate(powers).Problem);
            Assert.Equal("1", ExpressionEvaluator.Format(ExpressionEvaluator.Evaluate(new string('(', 50) + "1" + new string(')', 50)).Value));
        }

        [Fact]
        public void The_result_never_reads_minus_zero()
        {
            Assert.Equal("0", ExpressionEvaluator.Format(ExpressionEvaluator.Evaluate("-0").Value));
            Assert.Equal("0", ExpressionEvaluator.Format(-0.0000000000000001m));
        }
    }
}
```

`tests/Kil0bitSystemMonitor.Tests/TextToolsTests.cs`:

```csharp
using System;
using System.Globalization;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Tools: Base64, GUIDs, timestamps, and the one edit each tool makes.</summary>
    public class TextToolsTests
    {
        private static readonly DateTimeOffset Stamp = new(2026, 9, 30, 18, 5, 12, TimeSpan.FromHours(7));

        [Theory]
        [InlineData("hello", "aGVsbG8=")]
        [InlineData("สวัสดี", "4Liq4Lin4Lix4Liq4LiU4Li1")]
        [InlineData("ไทย 😀", "4LmE4LiX4LiiIPCfmIA=")]
        public void Base64_round_trips_thai_and_emoji(string text, string base64)
        {
            Assert.Equal(base64, TextTools.Base64Encode(text));
            var (decoded, problem) = TextTools.Base64Decode(base64);
            Assert.Null(problem);
            Assert.Equal(text, decoded);
        }

        [Theory]
        [InlineData("aGVs\r\nbG8=", "hello")]      // wrapped at a line break
        [InlineData("aGVsbG8", "hello")]           // padding left off
        [InlineData("Pz8-", "??>")]                // the URL-safe alphabet ("Pz8+")
        [InlineData("77u/aGk=", "hi")]             // a UTF-8 byte order mark is dropped
        public void Base64_decode_takes_wrapped_unpadded_and_url_safe_text(string base64, string text)
        {
            var (decoded, problem) = TextTools.Base64Decode(base64);
            Assert.Null(problem);
            Assert.Equal(text, decoded);
        }

        [Theory]
        [InlineData("hello!", TextTools.NotBase64)]
        [InlineData("a", TextTools.NotBase64)]
        [InlineData("////", TextTools.NotUtf8)]     // bytes FF FF FF
        [InlineData("AAAA", TextTools.NotUtf8)]     // three NULs: binary, not text
        public void Not_base64_or_not_text_is_reported(string text, string problem)
        {
            var (decoded, reason) = TextTools.Base64Decode(text);
            Assert.Null(decoded);
            Assert.Equal(problem, reason);
        }

        [Fact]
        public void A_guid_is_lowercase_d_format()
        {
            Assert.Equal("3f2504e0-4f89-11d3-9a0c-0305e82c3301",
                         TextTools.FormatGuid(new Guid("3F2504E0-4F89-11D3-9A0C-0305E82C3301")));
        }

        [Fact]
        public void Timestamps_are_gregorian_under_the_thai_culture()
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");     // its calendar would write 2569
            try
            {
                Assert.Equal("2026-09-30T18:05:12+07:00", TextTools.Iso8601(Stamp));
                Assert.Equal("2026-09-30", TextTools.Date(Stamp));
                Assert.Equal("1790766312", TextTools.UnixSeconds(Stamp));
                Assert.Equal("2026-09-30T11:05:12+00:00", TextTools.Iso8601(Stamp.ToUniversalTime()));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void A_selection_tool_replaces_what_is_between_the_spaces_and_selects_it()
        {
            // "say  255 \nnow", selection "  255 \n": only "255" is converted; the spaces and the break stay.
            var outcome = TextTools.OnSelection("say  255 \nnow", 3, 7, s => NumberConverter.Convert(s, NumberBase.Hex));
            Assert.Null(outcome.Problem);
            Assert.Equal(new TextEdit(5, 3, "0xFF", 5, 4), outcome.Edit);
        }

        [Fact]
        public void Nothing_but_spaces_selected_asks_for_a_selection()
        {
            Assert.Equal(TextTools.SelectFirst, TextTools.OnSelection("a   b", 1, 3, s => (s, null)).Problem);
            Assert.Equal(TextTools.SelectFirst, TextTools.OnSelection("abc", 1, 0, s => (s, null)).Problem);
            Assert.Null(TextTools.OnSelection("abc", 1, 0, s => (s, null)).Edit);
        }

        [Fact]
        public void A_problem_leaves_no_edit()
        {
            var outcome = TextTools.OnSelection("xyz", 0, 3, s => NumberConverter.Convert(s, NumberBase.Hex));
            Assert.Null(outcome.Edit);
            Assert.Equal(NumberConverter.NotANumber, outcome.Problem);
        }

        [Fact]
        public void Evaluate_appends_the_result_after_the_expression_and_selects_it()
        {
            // Selection "1500+230 \n" (offset 7, length 10): the result goes right after "230".
            var outcome = TextTools.Evaluate("total: 1500+230 \n", 7, 10);
            Assert.Equal(new TextEdit(15, 0, " = 1730", 18, 4), outcome.Edit);
        }

        [Fact]
        public void Evaluate_reports_a_bad_expression()
        {
            var outcome = TextTools.Evaluate("1/0", 0, 3);
            Assert.Null(outcome.Edit);
            Assert.Equal(ExpressionEvaluator.DivisionByZero, outcome.Problem);
        }

        [Fact]
        public void Insert_replaces_the_selection_and_leaves_the_caret_after_it()
        {
            Assert.Equal(new TextEdit(2, 3, "2026-09-30", 12, 0), TextTools.Insert(2, 3, "2026-09-30").Edit);
        }
    }
}
```

`tests/Kil0bitSystemMonitor.Tests/PadToolsTests.cs`:

```csharp
using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using MenuItem = System.Windows.Controls.MenuItem;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The Tools menu in the window: where it is, what it edits, and what it says when it cannot.</summary>
    public class PadToolsTests
    {
        private static string[] Headers(MenuItem item) =>
            item.Items.Cast<object>().Select(i => i is MenuItem m ? (string)m.Header : "-").ToArray();

        /// <summary>Tools, or an item under it, in a freshly built editor menu.</summary>
        private static MenuItem Tool(MicaPadWindow window, params string[] path)
        {
            window.RefreshEditorMenu();
            var item = PadMenuTests.ItemOf(window.EditorMenu, "Tools");
            foreach (string header in path) item = item.Items.OfType<MenuItem>().Single(m => (string)m.Header == header);
            return item;
        }

        [Fact]
        public void Both_menus_offer_the_tools() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            Assert.Equal(new[] { "Base64 encode", "Base64 decode", "Convert number", "Insert GUID", "Insert timestamp", "Evaluate" },
                         Headers(Tool(window)));
            Assert.Equal(new[] { "Decimal", "Hex", "Binary", "Octal" }, Headers(Tool(window, "Convert number")));
            Assert.Equal(new[] { "ISO 8601", "Date", "Unix seconds" }, Headers(Tool(window, "Insert timestamp")));
            Assert.Contains("Tools", PadMenuTests.Headers(window.BuildMainMenu()));
        });

        [Fact]
        public void Selection_tools_wait_for_a_selection() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "255";
            window.Editor.Select(0, 0);
            Assert.False(Tool(window, "Base64 encode").IsEnabled);
            Assert.False(Tool(window, "Base64 decode").IsEnabled);
            Assert.False(Tool(window, "Convert number").IsEnabled);
            Assert.False(Tool(window, "Evaluate").IsEnabled);
            Assert.True(Tool(window, "Insert GUID").IsEnabled);
            Assert.True(Tool(window, "Insert timestamp").IsEnabled);

            window.Editor.Select(0, 3);
            Assert.True(Tool(window, "Convert number").IsEnabled);
        });

        [Fact]
        public void Evaluate_is_one_undo_step_and_selects_the_result() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "2+3";
            window.Editor.Select(0, 3);

            PadMenuTests.Click(Tool(window, "Evaluate"));

            Assert.Equal("2+3 = 5", window.Editor.Document.Text);
            Assert.Equal("5", window.Editor.SelectedText);
            window.Editor.Undo();
            Assert.Equal("2+3", window.Editor.Document.Text);
        });

        [Fact]
        public void Convert_number_rewrites_the_selection() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "mask 255;";
            window.Editor.Select(5, 3);

            PadMenuTests.Click(Tool(window, "Convert number", "Hex"));

            Assert.Equal("mask 0xFF;", window.Editor.Document.Text);
            Assert.Equal("0xFF", window.Editor.SelectedText);
        });

        [Fact]
        public void A_tool_that_cannot_apply_leaves_the_text_and_says_why() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "hello";
            window.Editor.Select(0, 5);

            PadMenuTests.Click(Tool(window, "Convert number", "Hex"));

            Assert.Equal("hello", window.Editor.Document.Text);
            Assert.Equal(Visibility.Visible, window.StatusMessage.Visibility);
            Assert.Equal(NumberConverter.NotANumber, window.StatusMessage.Text);

            PadMenuTests.Click(Tool(window, "Base64 decode"));
            Assert.Equal("hello", window.Editor.Document.Text);
            Assert.Equal(TextTools.NotBase64, window.StatusMessage.Text);
        });

        [Fact]
        public void Base64_from_the_menu_round_trips_thai_text() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "สวัสดี";
            window.Editor.SelectAll();
            PadMenuTests.Click(Tool(window, "Base64 encode"));
            Assert.Equal("4Liq4Lin4Lix4Liq4LiU4Li1", window.Editor.Document.Text);

            window.Editor.SelectAll();
            PadMenuTests.Click(Tool(window, "Base64 decode"));
            Assert.Equal("สวัสดี", window.Editor.Document.Text);
        });

        [Fact]
        public void Timestamps_come_from_the_window_clock_in_the_gregorian_calendar() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            try
            {
                window.Now = () => new DateTimeOffset(2026, 9, 30, 18, 5, 12, TimeSpan.FromHours(7));
                window.Editor.Document.Text = "";

                PadMenuTests.Click(Tool(window, "Insert timestamp", "ISO 8601"));
                PadMenuTests.Click(Tool(window, "Insert timestamp", "Date"));

                Assert.Equal("2026-09-30T18:05:12+07:002026-09-30", window.Editor.Document.Text);
                Assert.Equal(window.Editor.Document.TextLength, window.Editor.CaretOffset);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        });

        [Fact]
        public void Insert_guid_writes_a_lowercase_guid() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "";
            PadMenuTests.Click(Tool(window, "Insert GUID"));
            Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", window.Editor.Document.Text);
        });

        [Fact]
        public void A_rectangle_is_refused_and_the_text_left_alone() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "123456\n789012";
            var area = window.Editor.TextArea;
            // An unshown window has no layout, and a rectangle is measured in visual columns.
            window.Measure(new System.Windows.Size(800, 600));
            window.Arrange(new Rect(0, 0, 800, 600));
            window.UpdateLayout();
            area.TextView.EnsureVisualLines();
            area.Selection = new ICSharpCode.AvalonEdit.Editing.RectangleSelection(
                area, new ICSharpCode.AvalonEdit.TextViewPosition(1, 2), new ICSharpCode.AvalonEdit.TextViewPosition(2, 4));

            Assert.False(EditorMenus.RunTool(window.Editor, window.ShowStatus, TextTools.Evaluate));
            Assert.Equal("123456\n789012", window.Editor.Document.Text);
            Assert.Equal(EditorMenus.RectangleRefused, window.StatusMessage.Text);
        });

        [Fact]
        public void The_main_menus_tools_are_off_while_the_history_preview_covers_the_note() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            Assert.True(PadMenuTests.ItemOf(window.BuildMainMenu(), "Tools").IsEnabled);
            window.PreviewPanel.Visibility = Visibility.Visible;
            Assert.False(PadMenuTests.ItemOf(window.BuildMainMenu(), "Tools").IsEnabled);
        });
    }
}
```

In `tests/Kil0bitSystemMonitor.Tests/PadMenuTests.cs`, `The_editor_menu_lists_edit_then_find_items`, the expected headers become:

```csharp
            Assert.Equal(new[] { "Undo", "Redo", "-", "Cut", "Copy", "Copy as RTF", "Paste", "Delete", "Select all", "-", "Format", "Lines", "Tools", "-", "Find", "Replace", "Go to line…" },
                         Headers(window.EditorMenu));
```

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~NumberConverterTests|FullyQualifiedName~ExpressionEvaluatorTests|FullyQualifiedName~TextToolsTests|FullyQualifiedName~PadToolsTests"`). Expected: build FAILS (`NumberConverter`, `ExpressionEvaluator`, `TextTools`, `ToolsMenu` not found).

- [ ] **Step 3: Write `Services/Pad/NumberConverter.cs`**

```csharp
using System;
using System.Globalization;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>The bases Tools ▸ Convert number writes.</summary>
    public enum NumberBase
    {
        Decimal,
        Hex,
        Binary,
        Octal,
    }

    /// <summary>
    /// Tools ▸ Convert number (spec 5.1): one integer in decimal, <c>0x</c> hex, <c>0b</c> binary or
    /// <c>0o</c> octal, with an optional sign and underscores between digits, written in another
    /// base. Any value that fits in 64 bits, signed or unsigned: −2⁶³ to 2⁶⁴−1. A negative number
    /// keeps its sign in every base (<c>-0x3</c>), never a two's complement. Only ASCII digits count.
    /// </summary>
    public static class NumberConverter
    {
        public const string NotANumber = "Not a whole number (try 255, 0xFF, 0b1010 or 0o17)";
        public const string TooLarge = "That number does not fit in 64 bits";

        /// <summary>Reads one integer; false with the reason when <paramref name="text"/> is not one that fits in 64 bits.</summary>
        public static bool TryParse(string text, out bool negative, out ulong magnitude, out string? problem)
        {
            negative = false;
            magnitude = 0;
            problem = NotANumber;

            string s = text.Trim();
            int i = 0;
            if (i < s.Length && (s[i] == '-' || s[i] == '+'))
            {
                negative = s[i] == '-';
                i++;
            }

            int radix = 10;
            if (i + 1 < s.Length && s[i] == '0')
            {
                radix = char.ToLowerInvariant(s[i + 1]) switch { 'x' => 16, 'b' => 2, 'o' => 8, _ => 10 };
                if (radix != 10) i += 2;
            }

            string digits = s.Substring(i);
            // Underscores only between digits: 1_000 and 0xFF_FF, not _1, 1_ or 0x_FF.
            if (digits.Length == 0 || digits[0] == '_' || digits[^1] == '_') return false;

            foreach (char c in digits)
            {
                if (c == '_') continue;
                int digit = DigitValue(c);
                if (digit < 0 || digit >= radix) return false;
                try
                {
                    magnitude = checked(magnitude * (ulong)radix + (ulong)digit);
                }
                catch (OverflowException)
                {
                    problem = TooLarge;
                    return false;
                }
            }

            if (negative && magnitude > 1UL << 63)
            {
                problem = TooLarge;
                return false;
            }
            if (magnitude == 0) negative = false;   // "-0" is 0
            problem = null;
            return true;
        }

        /// <summary>The number in <paramref name="target"/>: <c>255</c>, <c>0xFF</c>, <c>0b11111111</c>, <c>0o377</c>.</summary>
        public static string Format(bool negative, ulong magnitude, NumberBase target)
        {
            string body = target switch
            {
                NumberBase.Hex => "0x" + magnitude.ToString("X", CultureInfo.InvariantCulture),
                NumberBase.Binary => "0b" + InBase(magnitude, 2),
                NumberBase.Octal => "0o" + InBase(magnitude, 8),
                _ => magnitude.ToString(CultureInfo.InvariantCulture),
            };
            return negative ? "-" + body : body;
        }

        /// <summary>One number converted, or why it cannot be: for the Tools menu.</summary>
        public static (string? Text, string? Problem) Convert(string text, NumberBase target) =>
            TryParse(text, out bool negative, out ulong magnitude, out string? problem)
                ? (Format(negative, magnitude, target), null)
                : (null, problem);

        private static int DigitValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        private static string InBase(ulong value, int radix)
        {
            if (value == 0) return "0";
            var sb = new StringBuilder();
            while (value > 0)
            {
                sb.Insert(0, (char)('0' + (int)(value % (ulong)radix)));
                value /= (ulong)radix;
            }
            return sb.ToString();
        }
    }
}
```

- [ ] **Step 4: Write `Services/Pad/ExpressionEvaluator.cs`**

```csharp
using System;
using System.Globalization;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Tools ▸ Evaluate (spec 5.1): arithmetic on decimal and <c>0x</c> numbers with
    /// <c>+ - * / % ^</c>, parentheses and unary minus, computed in <see cref="decimal"/> so
    /// 0.1 + 0.2 is 0.3. MicaPad's own recursive-descent parser: nothing is compiled or executed,
    /// and every problem (a syntax error, division by zero, a number or result too large, nesting
    /// too deep) comes back as a message, never as an exception.
    ///
    /// <para>
    /// Precedence, loosest first: <c>+ -</c>; <c>* / %</c>; unary minus; <c>^</c>, which is
    /// right-associative and binds tighter than unary minus, as in mathematics: <c>2^3^2</c> is
    /// 512 and <c>-2^2</c> is −4. The exponent may itself be negative (<c>2^-1</c>).
    /// </para>
    /// </summary>
    public static class ExpressionEvaluator
    {
        /// <summary>Deeper nesting than this is refused: the recursion must never overflow the stack.</summary>
        public const int MaxDepth = 200;

        public const string SyntaxError = "Not a valid expression";
        public const string DivisionByZero = "Division by zero";
        public const string ResultTooLarge = "The result is too large";
        public const string NumberTooLarge = "A number is too large";
        public const string NotReal = "The result is not a real number";
        public const string TooDeep = "The expression is nested too deeply";

        /// <summary>The value of <paramref name="expression"/>, or why there is none.</summary>
        public static (decimal Value, string? Problem) Evaluate(string expression)
        {
            var parser = new Parser(expression);
            try
            {
                decimal value = parser.ParseExpression();
                parser.SkipSpaces();
                return parser.AtEnd ? (value, null) : (0m, SyntaxError);
            }
            catch (EvalException ex)
            {
                return (0m, ex.Message);
            }
            catch (OverflowException)
            {
                return (0m, ResultTooLarge);
            }
            catch (DivideByZeroException)
            {
                return (0m, DivisionByZero);
            }
        }

        /// <summary>The result as MicaPad writes it: invariant digits, at most 15 decimals, no trailing zeros, never "-0".</summary>
        public static string Format(decimal value)
        {
            decimal rounded = Math.Round(value, 15, MidpointRounding.AwayFromZero);
            if (rounded == 0m) rounded = 0m;
            return rounded.ToString("0.###############", CultureInfo.InvariantCulture);
        }

        private sealed class EvalException : Exception
        {
            public EvalException(string message) : base(message)
            {
            }
        }

        private sealed class Parser
        {
            private readonly string _text;
            private int _pos;
            private int _depth;

            public Parser(string text) => _text = text;

            public bool AtEnd => _pos >= _text.Length;

            public void SkipSpaces()
            {
                while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++;
            }

            // expression := term (('+' | '-') term)*
            public decimal ParseExpression()
            {
                Enter();
                decimal value = ParseTerm();
                while (true)
                {
                    SkipSpaces();
                    if (Accept('+')) value += ParseTerm();
                    else if (Accept('-')) value -= ParseTerm();
                    else break;
                }
                _depth--;
                return value;
            }

            // term := unary (('*' | '/' | '%') unary)*
            private decimal ParseTerm()
            {
                decimal value = ParseUnary();
                while (true)
                {
                    SkipSpaces();
                    if (Accept('*'))
                    {
                        value *= ParseUnary();
                    }
                    else if (Accept('/'))
                    {
                        decimal divisor = ParseUnary();
                        if (divisor == 0m) throw new EvalException(DivisionByZero);
                        value /= divisor;
                    }
                    else if (Accept('%'))
                    {
                        decimal divisor = ParseUnary();
                        if (divisor == 0m) throw new EvalException(DivisionByZero);
                        value %= divisor;
                    }
                    else
                    {
                        break;
                    }
                }
                return value;
            }

            // unary := ('-' | '+') unary | power
            private decimal ParseUnary()
            {
                SkipSpaces();
                if (Accept('-'))
                {
                    Enter();
                    decimal value = -ParseUnary();
                    _depth--;
                    return value;
                }
                if (Accept('+'))
                {
                    Enter();
                    decimal value = ParseUnary();
                    _depth--;
                    return value;
                }
                return ParsePower();
            }

            // power := primary ('^' unary)?   — the exponent is a unary, so 2^3^2 = 2^(3^2) and 2^-1 works
            private decimal ParsePower()
            {
                decimal value = ParsePrimary();
                SkipSpaces();
                if (!Accept('^')) return value;
                Enter();
                decimal exponent = ParseUnary();
                _depth--;
                return Power(value, exponent);
            }

            // primary := number | '(' expression ')'
            private decimal ParsePrimary()
            {
                SkipSpaces();
                if (Accept('('))
                {
                    decimal value = ParseExpression();
                    SkipSpaces();
                    if (!Accept(')')) throw new EvalException(SyntaxError);
                    return value;
                }
                return ParseNumber();
            }

            private decimal ParseNumber()
            {
                int start = _pos;
                if (_pos + 1 < _text.Length && _text[_pos] == '0' && (_text[_pos + 1] == 'x' || _text[_pos + 1] == 'X'))
                {
                    _pos += 2;
                    int digits = _pos;
                    while (_pos < _text.Length && Uri.IsHexDigit(_text[_pos])) _pos++;
                    if (_pos == digits) throw new EvalException(SyntaxError);
                    if (!ulong.TryParse(_text.AsSpan(digits, _pos - digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hex))
                        throw new EvalException(NumberTooLarge);
                    return hex;
                }

                while (_pos < _text.Length && char.IsAsciiDigit(_text[_pos])) _pos++;
                if (_pos < _text.Length && _text[_pos] == '.')
                {
                    _pos++;
                    while (_pos < _text.Length && char.IsAsciiDigit(_text[_pos])) _pos++;
                }
                string number = _text.Substring(start, _pos - start);
                if (number.Length == 0 || number == ".") throw new EvalException(SyntaxError);
                if (!decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value))
                    throw new EvalException(NumberTooLarge);
                return value;
            }

            private bool Accept(char c)
            {
                if (_pos < _text.Length && _text[_pos] == c)
                {
                    _pos++;
                    return true;
                }
                return false;
            }

            private void Enter()
            {
                if (++_depth > MaxDepth) throw new EvalException(TooDeep);
            }

            /// <summary>
            /// A whole exponent is exact (by squaring; decimal throws on overflow, which is reported
            /// as too large). A negative one raises the reciprocal, so 2^-100 is a tiny number (shown
            /// as 0), never "too large", and 0.1^-100 is too large, never a division by zero. Any other
            /// exponent goes through double: a negative base then has no real result.
            /// </summary>
            private static decimal Power(decimal value, decimal exponent)
            {
                if (exponent == decimal.Truncate(exponent) && Math.Abs(exponent) <= 10_000)
                {
                    long n = (long)exponent;
                    if (n < 0)
                    {
                        if (value == 0m) throw new EvalException(DivisionByZero);
                        value = 1m / value;
                        n = -n;
                    }
                    decimal result = 1m;
                    decimal factor = value;
                    while (n > 0)
                    {
                        if ((n & 1) == 1) result *= factor;
                        n >>= 1;
                        if (n > 0) factor *= factor;   // only while a higher bit is left, so it overflows only when the result would
                    }
                    return result;
                }

                double d = Math.Pow((double)value, (double)exponent);
                if (double.IsNaN(d)) throw new EvalException(NotReal);
                if (double.IsInfinity(d) || Math.Abs(d) > (double)decimal.MaxValue) throw new EvalException(ResultTooLarge);
                return (decimal)d;
            }
        }
    }
}
```

(The depth counter is raised once per parenthesis level, per unary sign and per `^`; 200 levels is about 1,000 stack frames — far from the 1 MB stack's limit.)

- [ ] **Step 5: Write `Services/Pad/TextTools.cs`**

```csharp
using System;
using System.Globalization;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>What a tool does to the note: one edit, or why it cannot apply (the text is then left alone).</summary>
    public readonly record struct ToolOutcome(TextEdit? Edit, string? Problem)
    {
        public static ToolOutcome Fail(string problem) => new(null, problem);

        public static ToolOutcome Apply(TextEdit edit) => new(edit, null);
    }

    /// <summary>
    /// The Tools menu (spec 5.1) as edits over the note's text: Base64, GUIDs, timestamps, and the
    /// shapes the number and evaluate tools take. Pure: each tool returns one <see cref="TextEdit"/>
    /// for the window to apply as a single undoable change, or a problem for the status bar.
    /// Selection tools leave the spaces and line breaks around the selection where they are and work
    /// on what lies between (a line selected with its line break keeps the break).
    /// </summary>
    public static class TextTools
    {
        public const string SelectFirst = "Select some text first";
        public const string NotBase64 = "Not valid Base64";
        public const string NotUtf8 = "Not UTF-8 text";

        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>The UTF-8 bytes of <paramref name="text"/> in Base64.</summary>
        public static string Base64Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

        /// <summary>
        /// Base64 back to UTF-8 text. Line breaks and spaces inside are ignored (Base64 is often
        /// wrapped), missing padding is added, and the URL-safe alphabet (<c>-</c>, <c>_</c>) is read
        /// too. A leading byte order mark is dropped. Bytes that are not UTF-8, or that decode to
        /// control characters other than tab and line breaks (binary data), are not text.
        /// </summary>
        public static (string? Text, string? Problem) Base64Decode(string text)
        {
            var sb = new StringBuilder(text.Length + 3);
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) continue;
                sb.Append(c switch { '-' => '+', '_' => '/', _ => c });
            }
            while (sb.Length % 4 != 0) sb.Append('=');

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(sb.ToString());
            }
            catch (FormatException)
            {
                return (null, NotBase64);
            }

            string decoded;
            try
            {
                decoded = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return (null, NotUtf8);
            }

            if (decoded.Length > 0 && decoded[0] == '\uFEFF') decoded = decoded.Substring(1);
            foreach (char c in decoded)
                if (char.IsControl(c) && c != '\t' && c != '\r' && c != '\n') return (null, NotUtf8);
            return (decoded, null);
        }

        /// <summary>A GUID the way MicaPad inserts it: lowercase, <c>D</c> format.</summary>
        public static string FormatGuid(Guid guid) => guid.ToString("D", CultureInfo.InvariantCulture);

        /// <summary><c>2026-09-30T18:05:12+07:00</c>: Gregorian and invariant whatever the culture (th-TH would write 2569).</summary>
        public static string Iso8601(DateTimeOffset now) => now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

        /// <summary><c>2026-09-30</c>.</summary>
        public static string Date(DateTimeOffset now) => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary><c>1790766312</c>.</summary>
        public static string UnixSeconds(DateTimeOffset now) => now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Runs <paramref name="transform"/> on the selection without the whitespace around it and
        /// replaces just that part; the result is selected. Nothing but whitespace selected:
        /// <see cref="SelectFirst"/>.
        /// </summary>
        public static ToolOutcome OnSelection(string text, int start, int length, Func<string, (string? Text, string? Problem)> transform)
        {
            var (from, count) = Core(text, start, length);
            if (count == 0) return ToolOutcome.Fail(SelectFirst);
            var (result, problem) = transform(text.Substring(from, count));
            if (result == null) return ToolOutcome.Fail(problem ?? SelectFirst);
            return ToolOutcome.Apply(new TextEdit(from, count, result, from, result.Length));
        }

        /// <summary>Puts <paramref name="inserted"/> at the caret, replacing any selection, and leaves the caret after it.</summary>
        public static ToolOutcome Insert(int start, int length, string inserted) =>
            ToolOutcome.Apply(new TextEdit(start, length, inserted, start + inserted.Length, 0));

        /// <summary>Evaluate: appends <c> = result</c> right after the selected expression and selects the result.</summary>
        public static ToolOutcome Evaluate(string text, int start, int length)
        {
            var (from, count) = Core(text, start, length);
            if (count == 0) return ToolOutcome.Fail(SelectFirst);
            var (value, problem) = ExpressionEvaluator.Evaluate(text.Substring(from, count));
            if (problem != null) return ToolOutcome.Fail(problem);
            string result = ExpressionEvaluator.Format(value);
            int end = from + count;
            return ToolOutcome.Apply(new TextEdit(end, 0, " = " + result, end + 3, result.Length));
        }

        /// <summary>The selection without the whitespace at either end, as (start, length).</summary>
        private static (int Start, int Length) Core(string text, int start, int length)
        {
            int from = Math.Clamp(start, 0, text.Length);
            int end = Math.Clamp(start + length, from, text.Length);
            while (from < end && char.IsWhiteSpace(text[from])) from++;
            while (end > from && char.IsWhiteSpace(text[end - 1])) end--;
            return (from, end - from);
        }
    }
}
```

- [ ] **Step 6: The Tools menu in `Pad/EditorMenus.cs`**

Add to `EditorMenus` (after `LinesMenu`):

```csharp
        /// <summary>What a tool says about a rectangular selection: its flat span would be garbage.</summary>
        public const string RectangleRefused = "Tools work on an ordinary selection, not a rectangle";

        /// <summary>
        /// Tools (spec 5.1): each item is one undoable edit on the selection, or at the caret for the
        /// inserts; when a tool cannot apply, the text is left alone and <paramref name="report"/>
        /// says why. The selection tools wait for a selection.
        /// </summary>
        public static MenuItem ToolsMenu(TextEditor editor, Action<string> report, Func<DateTimeOffset> now)
        {
            bool selected = editor.SelectionLength > 0;
            var tools = new MenuItem { Header = "Tools", Icon = "\uE90F" };
            MenuItem Tool(string header, Func<string, int, int, ToolOutcome> tool, bool enabled = true) =>
                Item(header, null, () => RunTool(editor, report, tool), enabled);

            tools.Items.Add(Tool("Base64 encode", (t, s, l) => TextTools.OnSelection(t, s, l, x => (TextTools.Base64Encode(x), null)), selected));
            tools.Items.Add(Tool("Base64 decode", (t, s, l) => TextTools.OnSelection(t, s, l, TextTools.Base64Decode), selected));

            var convert = new MenuItem { Header = "Convert number", IsEnabled = selected };
            foreach (var (header, target) in new[] { ("Decimal", NumberBase.Decimal), ("Hex", NumberBase.Hex), ("Binary", NumberBase.Binary), ("Octal", NumberBase.Octal) })
                convert.Items.Add(Tool(header, (t, s, l) => TextTools.OnSelection(t, s, l, x => NumberConverter.Convert(x, target))));
            tools.Items.Add(convert);

            tools.Items.Add(Tool("Insert GUID", (t, s, l) => TextTools.Insert(s, l, TextTools.FormatGuid(Guid.NewGuid()))));

            var stamp = new MenuItem { Header = "Insert timestamp" };
            stamp.Items.Add(Tool("ISO 8601", (t, s, l) => TextTools.Insert(s, l, TextTools.Iso8601(now()))));
            stamp.Items.Add(Tool("Date", (t, s, l) => TextTools.Insert(s, l, TextTools.Date(now()))));
            stamp.Items.Add(Tool("Unix seconds", (t, s, l) => TextTools.Insert(s, l, TextTools.UnixSeconds(now()))));
            tools.Items.Add(stamp);

            tools.Items.Add(Tool("Evaluate", TextTools.Evaluate, selected));
            return tools;
        }

        /// <summary>Runs a tool: its edit is one undoable change; a problem is reported and the text left alone. True when it edited.</summary>
        public static bool RunTool(TextEditor editor, Action<string> report, Func<string, int, int, ToolOutcome> tool)
        {
            if (editor.IsReadOnly) return false;
            if (editor.TextArea.Selection is RectangleSelection)
            {
                report(RectangleRefused);
                return false;
            }
            var outcome = tool(editor.Document.Text, editor.SelectionStart, editor.SelectionLength);
            if (outcome.Edit is not TextEdit edit)
            {
                report(outcome.Problem ?? TextTools.SelectFirst);
                return false;
            }
            ApplyEdit(editor, edit);
            return true;
        }
```

(`RectangleSelection` is already imported via `ICSharpCode.AvalonEdit.Editing`. The foreach variable `target` is a fresh variable per iteration, so each item converts to its own base.)

- [ ] **Step 7: Wire both menus in `Pad/MicaPadWindow.xaml.cs`**

1. A clock the timestamp tools read (next to `ShowStatus`):

```csharp
        /// <summary>The time the timestamp tools insert. Tests replace it.</summary>
        internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.Now;
```

2. `FillEditorMenu`: right after `menu.Items.Add(LinesMenu(editor, MoveLines));` add

```csharp
            menu.Items.Add(ToolsMenu(editor, ShowStatus, () => Now()));
```

3. `BuildMainMenu`: right after the *Clear bookmarks* item add

```csharp
            var tools = ToolsMenu(Editor, ShowStatus, () => Now());
            tools.IsEnabled = PreviewPanel.Visibility != Visibility.Visible;   // never edit a note hidden under the history preview
            menu.Items.Add(tools);
```

- [ ] **Step 8: Run the focused tests, then the whole suite** — expected PASS (the changed `PadMenuTests.The_editor_menu_lists_edit_then_find_items` included).

- [ ] **Step 9: Document it** — `GUIDE.md`, `## 📝 MicaPad`, a new section right before `### Where notes live`:

```markdown
### Tools

Right-click → **Tools** (also in **☰**):

* **Base64 encode** / **Base64 decode** — UTF-8, so Thai text comes back exactly.
* **Convert number** to decimal, hex, binary or octal — `255` ↔ `0xFF` ↔ `0b11111111` ↔ `0o377`,
  any 64-bit number, with `_` allowed between digits (`1_000`).
* **Insert GUID** and **Insert timestamp** — `2026-09-30T18:05:12+07:00`, `2026-09-30` or Unix
  seconds, always in the Western calendar.
* **Evaluate** — select a sum such as `(1500 + 230) * 1.07` and ` = 1851.1` is added after it.
  It knows `+ - * / % ^`, brackets and `0x` numbers, and nothing else: text in a note is never run.

The selection tools work on the selected text; when one cannot (not a number, not Base64,
division by zero), the text is left alone and the status bar says why. Each is a single **Ctrl+Z**.

```

`README.md`, the MicaPad feature list in English, right before the bullet `* Notes are plain text files in …`:

```markdown
* **Tools**: Base64, number bases, GUIDs, timestamps, and a calculator that works the sum out itself — nothing in a note is ever run; each is one **Ctrl+Z**
```

and in the Thai list, right before `* โน้ตเก็บเป็นไฟล์ข้อความธรรมดาใน …`:

```markdown
* **เครื่องมือ**: Base64 แปลงเลขฐาน GUID เวลาปัจจุบัน และเครื่องคิดเลขที่คำนวณเอง ไม่มีสิ่งใดในโน้ตถูกรันเลย ทุกคำสั่งย้อนกลับได้ด้วย **Ctrl+Z** ครั้งเดียว
```

- [ ] **Step 10: Commit**

```bash
git add Services/Pad/NumberConverter.cs Services/Pad/ExpressionEvaluator.cs Services/Pad/TextTools.cs Pad/EditorMenus.cs Pad/MicaPadWindow.xaml.cs GUIDE.md README.md tests/Kil0bitSystemMonitor.Tests/NumberConverterTests.cs tests/Kil0bitSystemMonitor.Tests/ExpressionEvaluatorTests.cs tests/Kil0bitSystemMonitor.Tests/TextToolsTests.cs tests/Kil0bitSystemMonitor.Tests/PadToolsTests.cs tests/Kil0bitSystemMonitor.Tests/PadMenuTests.cs
git commit -m "feat(pad): Tools - Base64, number bases, GUID, timestamps and a safe evaluator" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

### Task 2: History diff

**Files:**
- Modify: `Kil0bitSystemMonitor.csproj` (`DiffPlex` 1.9.0)
- Create: `Services/Pad/HistoryDiff.cs`, `Pad/DiffPreview.cs`
- Modify: `Pad/MicaPadWindow.xaml` (banner: *Compare with current*, summary), `Pad/MicaPadWindow.xaml.cs`, `GUIDE.md`, `README.md` (both MicaPad feature lists)
- Create: `tests/Kil0bitSystemMonitor.Tests/HistoryDiffTests.cs`, `tests/Kil0bitSystemMonitor.Tests/PadHistoryDiffTests.cs`

**Interfaces:**
- Consumes: `PadPalette.DiffAdded`, `PadPalette.DiffRemoved`, `PadPalette.LineNumbers`, `PadPalette.TextSoft`, `PadPalette.Background` (names checked in `Services/Pad/PadPalette.cs`); `PadColor.Over`, `PadColor.Contrast`; `PadThemeApplier.ToBrush`/`ToColor`; the window's `OnVersionSelected`, `EndPreview`, `OnPreviewRestoreClick`, `OnPreviewCopyClick`, `_previewLanguage`, `PreviewEditor`, `PreviewPanel`, `RestoreButton`; `PadLanguageWindowTests.WithWindow`/`Pump`; `NoteStore.WriteSnapshot(string id, string text, DateTime localTime)`.
- Produces:
  - `public enum DiffKind { Unchanged, Added, Removed }`; `public readonly record struct DiffRow(DiffKind Kind, string Text, int? OldLine, int? NewLine)`; `public sealed record DiffOutcome(IReadOnlyList<DiffRow> Rows, int Added, int Removed, bool TooLarge)` with `string Summary`; `public static class HistoryDiff` — `const int MaxChars = 1024 * 1024`, `const int MaxChangedLines = 50_000`, `const string TooLargeText`, `DiffOutcome Compare(string oldText, string newText)`, `string Describe(int added, int removed)`.
  - `internal sealed class DiffPreview : IBackgroundRenderer` — ctor `(TextEditor editor, Func<PadPalette> palette)`; `IReadOnlyList<DiffRow> Rows`, `bool IsShown`, `AbstractMargin Margin`, `void Show(IReadOnlyList<DiffRow>)`, `void Hide()`, `static PadColor? TintOf(DiffKind, PadPalette)`, `static string GlyphOf(DiffKind)`, `static string NumbersOf(DiffRow, int digits)`, `internal void DrawMargin(DrawingContext)`; `const byte TintAlpha = 0x38`.
  - Window: `CompareButton`, `DiffSummary` (XAML), `internal void ToggleCompare()`, `internal Task CompareTask`, `internal DiffPreview Diff`.

- [ ] **Step 1: Add the package** — in `Kil0bitSystemMonitor.csproj`, in the `ItemGroup` with `AvalonEdit`, after it:

```xml
        <PackageReference Include="DiffPlex" Version="1.9.0" />
```

The next build restores it from nuget.org once (the one network use of this part; tests never touch the network). DiffPlex is Apache-2.0 with no dependencies. No installer change is needed: `installer.iss` ships `release-output\*`, as it does AvalonEdit.

- [ ] **Step 2: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/HistoryDiffTests.cs`:

```csharp
using System.Globalization;
using System.Linq;
using System.Text;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>History ▸ Compare with current: line rows with both numbers, the summary, and the 1 MB limit.</summary>
    public class HistoryDiffTests
    {
        private static DiffRow Row(DiffKind kind, string text, int? oldLine, int? newLine) => new(kind, text, oldLine, newLine);

        [Fact]
        public void Rows_show_removed_then_added_lines_with_both_numbers()
        {
            var outcome = HistoryDiff.Compare("one\ntwo\nthree", "zero\none\nthree\nfour");

            Assert.False(outcome.TooLarge);
            Assert.Equal(new[]
            {
                Row(DiffKind.Added, "zero", null, 1),
                Row(DiffKind.Unchanged, "one", 1, 2),
                Row(DiffKind.Removed, "two", 2, null),
                Row(DiffKind.Unchanged, "three", 3, 3),
                Row(DiffKind.Added, "four", null, 4),
            }, outcome.Rows);
            Assert.Equal(2, outcome.Added);
            Assert.Equal(1, outcome.Removed);
            Assert.Equal("+2 −1 lines", outcome.Summary);
        }

        [Fact]
        public void A_changed_line_is_a_removal_then_an_addition()
        {
            Assert.Equal(new[]
            {
                Row(DiffKind.Unchanged, "a", 1, 1),
                Row(DiffKind.Removed, "b", 2, null),
                Row(DiffKind.Added, "B", null, 2),
                Row(DiffKind.Unchanged, "c", 3, 3),
            }, HistoryDiff.Compare("a\nb\nc", "a\nB\nc").Rows);
        }

        [Fact]
        public void Identical_texts_have_no_changes()
        {
            var outcome = HistoryDiff.Compare("same\ntext", "same\ntext");
            Assert.Equal(2, outcome.Rows.Count);
            Assert.All(outcome.Rows, r => Assert.Equal(DiffKind.Unchanged, r.Kind));
            Assert.Equal("No changes", outcome.Summary);
        }

        [Fact]
        public void Line_endings_do_not_count_but_spaces_and_letter_case_do()
        {
            Assert.Equal("No changes", HistoryDiff.Compare("a\r\nb", "a\nb").Summary);
            Assert.Equal("+1 −1 lines", HistoryDiff.Compare("a  b", "a b").Summary);
            Assert.Equal("+1 −1 lines", HistoryDiff.Compare("Word", "word").Summary);
        }

        [Fact]
        public void Empty_texts_compare_too()
        {
            Assert.Equal(new[] { Row(DiffKind.Added, "x", null, 1) }, HistoryDiff.Compare("", "x").Rows);
            Assert.Equal(new[] { Row(DiffKind.Removed, "x", 1, null) }, HistoryDiff.Compare("x", "").Rows);
            Assert.Empty(HistoryDiff.Compare("", "").Rows);
            Assert.Equal("No changes", HistoryDiff.Compare("", "").Summary);
        }

        [Fact]
        public void A_final_line_break_is_a_line_of_its_own()
        {
            var outcome = HistoryDiff.Compare("a\nb\n", "a\nb");
            Assert.Equal(Row(DiffKind.Removed, "", 3, null), outcome.Rows[outcome.Rows.Count - 1]);
            Assert.Equal("+0 −1 lines", outcome.Summary);
        }

        [Fact]
        public void Two_texts_of_exactly_1_MB_are_compared()
        {
            // 32,768 lines of 32 characters (31 + the line break) = 1,048,576 characters exactly.
            var sb = new StringBuilder(HistoryDiff.MaxChars);
            for (int i = 0; i < 32_768; i++)
                sb.Append("line ").Append(i.ToString("D5", CultureInfo.InvariantCulture)).Append(' ').Append('.', 20).Append('\n');
            string old = sb.ToString();
            string current = old.Replace("line 00100 ", "LINE 00100 ");
            Assert.Equal(HistoryDiff.MaxChars, old.Length);
            Assert.Equal(HistoryDiff.MaxChars, current.Length);

            var outcome = HistoryDiff.Compare(old, current);

            Assert.False(outcome.TooLarge);
            Assert.Equal("+1 −1 lines", outcome.Summary);
            Assert.Equal(Row(DiffKind.Removed, "line 00100 " + new string('.', 20), 101, null), outcome.Rows[100]);
            Assert.Equal(Row(DiffKind.Added, "LINE 00100 " + new string('.', 20), null, 101), outcome.Rows[101]);
            Assert.Equal(32_770, outcome.Rows.Count);   // 32,768 unchanged (the last is the empty line after the final break) + 2
        }

        [Fact]
        public void Either_text_over_1_MB_is_too_large()
        {
            string big = new string('x', HistoryDiff.MaxChars + 1);

            foreach (var outcome in new[] { HistoryDiff.Compare(big, "x"), HistoryDiff.Compare("x", big) })
            {
                Assert.True(outcome.TooLarge);
                Assert.Empty(outcome.Rows);
                Assert.Equal("Too large to compare", outcome.Summary);
            }
        }

        [Fact]
        public void Texts_that_differ_on_too_many_lines_are_too_large()
        {
            // 30,000 lines on each side, every one changed: 60,000 lines to compare. With shorter
            // lines a 1 MB note holds far more, and DiffPlex would run for minutes.
            var old = new StringBuilder();
            var current = new StringBuilder();
            for (int i = 0; i < 30_000; i++)
            {
                old.Append("old ").Append(i.ToString(CultureInfo.InvariantCulture)).Append('\n');
                current.Append("new ").Append(i.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            var outcome = HistoryDiff.Compare(old.ToString(), current.ToString());

            Assert.True(outcome.TooLarge);
            Assert.Empty(outcome.Rows);
            Assert.Equal("Too large to compare", outcome.Summary);
        }

        [Fact]
        public void A_long_note_of_short_lines_with_one_change_is_compared()
        {
            // 300,000 lines, far more than HistoryDiff.MaxChangedLines, but only one of them differs.
            string old = string.Concat(Enumerable.Repeat("1\n2\n3\n", 100_000));
            string current = old.Substring(0, 300_000) + "x" + old.Substring(300_001);   // line 150,001: "1" becomes "x"

            var outcome = HistoryDiff.Compare(old, current);

            Assert.False(outcome.TooLarge);
            Assert.Equal("+1 −1 lines", outcome.Summary);
            Assert.Equal(Row(DiffKind.Removed, "1", 150_001, null), outcome.Rows[150_000]);
            Assert.Equal(Row(DiffKind.Added, "x", null, 150_001), outcome.Rows[150_001]);
        }

        [Theory]
        [InlineData(12, 3, "+12 −3 lines")]
        [InlineData(1, 0, "+1 −0 lines")]
        [InlineData(0, 0, "No changes")]
        public void The_summary_counts_lines(int added, int removed, string expected)
        {
            Assert.Equal(expected, HistoryDiff.Describe(added, removed));
        }
    }
}
```

`tests/Kil0bitSystemMonitor.Tests/PadHistoryDiffTests.cs`:

```csharp
using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Compare with current in the history preview: the diff, its summary, and results that come too late.</summary>
    public class PadHistoryDiffTests
    {
        private const string Older = "one\ntwo\nthree";
        private const string Newer = "one\nthree";
        private const string Current = "zero\none\nthree\nfour";

        /// <summary>A note with two versions (Rows[0] the newer, Rows[1] the older), the current text, and the history open.</summary>
        private static void WithVersions(Action<MicaPadWindow, PadTestEnv> test) => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var note = env.Workspace.Active!;
            env.Store.WriteSnapshot(note.Id, Older, new DateTime(2026, 9, 29, 10, 0, 0));
            env.Store.WriteSnapshot(note.Id, Newer, new DateTime(2026, 9, 29, 11, 0, 0));
            window.Editor.Document.Text = Current;
            Assert.True(window.HandleShortcut(Key.H, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.Equal(2, window.HistoryPanel.Rows.Count);
            test(window, env);
        });

        private static void Select(MicaPadWindow window, int row) =>
            window.HistoryPanel.Versions.SelectedItem = window.HistoryPanel.Rows[row];

        /// <summary>Waits for the compare off the UI thread, then runs what it queued on the dispatcher.</summary>
        private static void Finish(MicaPadWindow window)
        {
            Assert.True(window.CompareTask.Wait(TimeSpan.FromSeconds(10)));
            PadLanguageWindowTests.Pump();
        }

        [Fact]
        public void Compare_shows_the_diff_and_its_summary() => WithVersions((window, env) =>
        {
            Select(window, 1);
            Assert.Equal(Older, window.PreviewEditor.Text);

            window.ToggleCompare();
            Assert.Equal("Comparing…", window.DiffSummary.Text);          // the result waits for the dispatcher
            Finish(window);

            Assert.Equal("+2 −1 lines", window.DiffSummary.Text);
            Assert.Equal(new[] { DiffKind.Added, DiffKind.Unchanged, DiffKind.Removed, DiffKind.Unchanged, DiffKind.Added },
                         window.Diff.Rows.Select(r => r.Kind));
            Assert.Equal("zero\none\ntwo\nthree\nfour", window.PreviewEditor.Text);
            Assert.Equal("Show this version", window.CompareButton.Content);
            Assert.False(window.PreviewEditor.ShowLineNumbers);            // the diff margin numbers the lines instead
            Assert.Contains(window.Diff.Margin, window.PreviewEditor.TextArea.LeftMargins);
        });

        [Fact]
        public void Comparing_again_shows_the_version_itself() => WithVersions((window, env) =>
        {
            Select(window, 1);
            window.ToggleCompare();
            Finish(window);

            window.ToggleCompare();

            Assert.Equal(Older, window.PreviewEditor.Text);
            Assert.False(window.Diff.IsShown);
            Assert.True(window.PreviewEditor.ShowLineNumbers);
            Assert.DoesNotContain(window.Diff.Margin, window.PreviewEditor.TextArea.LeftMargins);
            Assert.Equal(Visibility.Collapsed, window.DiffSummary.Visibility);
            Assert.Equal("Compare with current", window.CompareButton.Content);
        });

        [Fact]
        public void Restore_while_comparing_restores_the_version_not_the_diff() => WithVersions((window, env) =>
        {
            Select(window, 1);
            window.ToggleCompare();
            Finish(window);

            window.RestoreButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(Older, window.Editor.Document.Text);
            window.Editor.Undo();
            Assert.Equal(Current, window.Editor.Document.Text);
        });

        [Fact]
        public void A_compare_finishing_after_back_is_dropped() => WithVersions((window, env) =>
        {
            Select(window, 1);
            window.ToggleCompare();
            var pending = window.CompareTask;

            Assert.True(window.HandleShortcut(Key.H, ModifierKeys.Control | ModifierKeys.Shift));   // closes the history and its preview
            Assert.True(pending.Wait(TimeSpan.FromSeconds(10)));
            PadLanguageWindowTests.Pump();

            Assert.Equal(Visibility.Collapsed, window.PreviewPanel.Visibility);
            Assert.False(window.Diff.IsShown);
            Assert.Equal("", window.PreviewEditor.Text);
        });

        [Fact]
        public void Switching_versions_while_comparing_shows_only_the_new_versions_diff() => WithVersions((window, env) =>
        {
            Select(window, 1);
            window.ToggleCompare();
            var first = window.CompareTask;

            Select(window, 0);                                              // the newer version; compare stays on
            var second = window.CompareTask;
            Assert.True(first.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(second.Wait(TimeSpan.FromSeconds(10)));
            PadLanguageWindowTests.Pump();                                  // both results arrive; only the second is shown

            Assert.Equal("+2 −0 lines", window.DiffSummary.Text);
            Assert.DoesNotContain(window.Diff.Rows, r => r.Kind == DiffKind.Removed);
            Assert.Equal("zero\none\nthree\nfour", window.PreviewEditor.Text);
        });

        [Fact]
        public void Too_large_to_compare_keeps_the_version_on_screen() => WithVersions((window, env) =>
        {
            Select(window, 1);
            window.Editor.Document.Text = new string('x', HistoryDiff.MaxChars + 1);

            window.ToggleCompare();
            Finish(window);

            Assert.Equal("Too large to compare", window.DiffSummary.Text);
            Assert.Equal(Older, window.PreviewEditor.Text);
            Assert.False(window.Diff.IsShown);
        });

        [Fact]
        public void Added_and_removed_lines_are_tinted_and_marked_in_the_margin() => WithVersions((window, env) =>
        {
            Select(window, 1);
            window.ToggleCompare();
            Finish(window);

            var view = window.PreviewEditor.TextArea.TextView;
            view.Measure(new System.Windows.Size(600, 400));
            view.Arrange(new Rect(0, 0, 600, 400));
            view.EnsureVisualLines();
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen()) window.Diff.Draw(view, context);
            var fills = visual.Drawing.Children.OfType<GeometryDrawing>().Select(d => ((SolidColorBrush)d.Brush).Color).ToList();

            var added = PadThemeApplier.ToColor(DiffPreview.TintOf(DiffKind.Added, window.Palette)!.Value);
            var removed = PadThemeApplier.ToColor(DiffPreview.TintOf(DiffKind.Removed, window.Palette)!.Value);
            Assert.Equal(2, fills.Count(c => c == added));
            Assert.Equal(1, fills.Count(c => c == removed));
            Assert.Equal(3, fills.Count);                                   // unchanged lines are not tinted

            using (var context = new DrawingVisual().RenderOpen()) window.Diff.DrawMargin(context);   // draws, and does not throw
        });

        [Theory]
        [InlineData("Dark")]
        [InlineData("Light")]
        public void The_tints_keep_the_preview_text_readable(string theme)
        {
            var palette = PadPalette.For(theme);
            foreach (var kind in new[] { DiffKind.Added, DiffKind.Removed })
            {
                var tint = DiffPreview.TintOf(kind, palette)!.Value.Over(palette.Background);
                Assert.True(PadColor.Contrast(palette.TextSoft, tint) >= 4.5, theme + " " + kind);
            }
            Assert.Null(DiffPreview.TintOf(DiffKind.Unchanged, palette));
        }

        [Fact]
        public void The_margin_shows_both_line_numbers_and_the_glyph()
        {
            Assert.Equal(" 2   ", DiffPreview.NumbersOf(new DiffRow(DiffKind.Removed, "two", 2, null), 2));
            Assert.Equal("12 14", DiffPreview.NumbersOf(new DiffRow(DiffKind.Unchanged, "x", 12, 14), 2));
            Assert.Equal("−", DiffPreview.GlyphOf(DiffKind.Removed));
            Assert.Equal("+", DiffPreview.GlyphOf(DiffKind.Added));
            Assert.Equal(" ", DiffPreview.GlyphOf(DiffKind.Unchanged));
        }
    }
}
```

(The two snapshots are written straight to the store with fixed local times, so the history lists them newest first without waiting for pause snapshots. `Finish` blocks the UI thread only on the pure computation, which never needs it; the result it posted then runs in `Pump`.)

- [ ] **Step 3: Run to verify they fail** (`--filter "FullyQualifiedName~HistoryDiffTests|FullyQualifiedName~PadHistoryDiffTests"`). Expected: build FAILS (`HistoryDiff`, `DiffPreview` not found).

- [ ] **Step 4: Write `Services/Pad/HistoryDiff.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using DiffPlex;
using DiffPlex.Chunkers;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>What happened to one line between a version and the current text.</summary>
    public enum DiffKind
    {
        Unchanged,
        Added,
        Removed,
    }

    /// <summary>
    /// One line of a compare: its text (without the line break), what happened to it, and its
    /// 1-based number in the old version and in the current text (null where it is not in that text).
    /// </summary>
    public readonly record struct DiffRow(DiffKind Kind, string Text, int? OldLine, int? NewLine);

    /// <summary>A compare: the rows in reading order and the counts, or <see cref="TooLarge"/> with no rows.</summary>
    public sealed record DiffOutcome(IReadOnlyList<DiffRow> Rows, int Added, int Removed, bool TooLarge)
    {
        /// <summary><c>+12 −3 lines</c>, <c>No changes</c> or <c>Too large to compare</c>, for the banner.</summary>
        public string Summary => TooLarge ? HistoryDiff.TooLargeText : HistoryDiff.Describe(Added, Removed);
    }

    /// <summary>
    /// History ▸ Compare with current (spec 5.2): a line diff of a version against the current
    /// text, by DiffPlex 1.9.0 (Apache-2.0). Spaces and letter case count; line endings do not (a
    /// CRLF line and an LF line compare equal). Within a change, removed lines come before added
    /// ones. Pure and thread-safe (DiffPlex's <see cref="Differ"/> keeps no state), so the window
    /// runs it off the UI thread; with the two limits below a compare takes a second or two at most.
    /// </summary>
    public static class HistoryDiff
    {
        /// <summary>Either text longer than this (1 MB of characters, like MicaPad's other MB limits) is not compared.</summary>
        public const int MaxChars = 1024 * 1024;

        /// <summary>
        /// More lines than this between the texts' common first and last lines, counted over both,
        /// are not compared either. DiffPlex's time grows with the lines times the changes: two
        /// 1 MB notes of short lines that differ throughout would keep a core busy for minutes. At
        /// this limit a compare takes about a second; a long note with a few changes stays well under it.
        /// </summary>
        public const int MaxChangedLines = 50_000;

        public const string TooLargeText = "Too large to compare";

        public static DiffOutcome Compare(string oldText, string newText)
        {
            if (oldText.Length > MaxChars || newText.Length > MaxChars || LinesBetweenCommonEnds(oldText, newText) > MaxChangedLines)
                return new DiffOutcome(Array.Empty<DiffRow>(), 0, 0, TooLarge: true);

            var result = Differ.Instance.CreateDiffs(oldText, newText, ignoreWhiteSpace: false, ignoreCase: false, chunker: LineChunker.Instance);
            var oldLines = result.PiecesOld;     // an empty text has no lines at all
            var newLines = result.PiecesNew;
            var rows = new List<DiffRow>(Math.Max(oldLines.Count, newLines.Count));
            int a = 0, b = 0, added = 0, removed = 0;

            foreach (var block in result.DiffBlocks)
            {
                // The lines before a block are the same in both texts.
                while (a < block.DeleteStartA && b < block.InsertStartB)
                {
                    rows.Add(new DiffRow(DiffKind.Unchanged, newLines[b], a + 1, b + 1));
                    a++;
                    b++;
                }
                for (int i = 0; i < block.DeleteCountA; i++, a++, removed++)
                    rows.Add(new DiffRow(DiffKind.Removed, oldLines[a], a + 1, null));
                for (int i = 0; i < block.InsertCountB; i++, b++, added++)
                    rows.Add(new DiffRow(DiffKind.Added, newLines[b], null, b + 1));
            }
            while (a < oldLines.Count && b < newLines.Count)
            {
                rows.Add(new DiffRow(DiffKind.Unchanged, newLines[b], a + 1, b + 1));
                a++;
                b++;
            }
            return new DiffOutcome(rows, added, removed, TooLarge: false);
        }

        /// <summary>
        /// The lines of both texts left once their common first and last lines are set aside, split
        /// as the diff splits them: an upper bound on the changes, so on the diff's work.
        /// </summary>
        private static int LinesBetweenCommonEnds(string oldText, string newText)
        {
            var a = LineChunker.Instance.Chunk(oldText);
            var b = LineChunker.Instance.Chunk(newText);
            int start = 0;
            while (start < a.Count && start < b.Count && string.Equals(a[start], b[start], StringComparison.Ordinal)) start++;
            int end = 0;
            while (end < a.Count - start && end < b.Count - start
                   && string.Equals(a[a.Count - 1 - end], b[b.Count - 1 - end], StringComparison.Ordinal)) end++;
            return (a.Count - start - end) + (b.Count - start - end);
        }

        /// <summary><c>+12 −3 lines</c> (U+2212 minus), or <c>No changes</c>.</summary>
        public static string Describe(int added, int removed) =>
            added == 0 && removed == 0
                ? "No changes"
                : string.Format(CultureInfo.InvariantCulture, "+{0} −{1} lines", added, removed);
    }
}
```

- [ ] **Step 5: Write `Pad/DiffPreview.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

using Brush = System.Windows.Media.Brush;
using FlowDirection = System.Windows.FlowDirection;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Shows a <see cref="HistoryDiff"/> in the read-only history preview (spec 5.2): each row as a
    /// line, removed lines on a faint red and added lines on a faint green (the palette's
    /// DiffRemoved and DiffAdded at <see cref="TintAlpha"/>), and a margin with the line's number in
    /// the old version and in the current text and its − or + glyph. The preview's own line numbers
    /// are hidden while it shows: they would count rows, not lines of either text.
    /// </summary>
    internal sealed class DiffPreview : IBackgroundRenderer
    {
        /// <summary>How strong a tint is: the palette's diff color at this alpha (tested to keep the text at 4.5:1).</summary>
        internal const byte TintAlpha = 0x38;

        private readonly TextEditor _editor;
        private readonly Func<PadPalette> _palette;
        private readonly DiffMargin _margin;
        private int _digits = 1;

        public DiffPreview(TextEditor editor, Func<PadPalette> palette)
        {
            _editor = editor;
            _palette = palette;
            _margin = new DiffMargin(this);
        }

        /// <summary>The rows on screen; empty while the preview shows a version itself.</summary>
        public IReadOnlyList<DiffRow> Rows { get; private set; } = Array.Empty<DiffRow>();

        /// <summary>True while a compare is on screen.</summary>
        public bool IsShown { get; private set; }

        public KnownLayer Layer => KnownLayer.Background;

        /// <summary>The −/+ margin; for tests.</summary>
        internal AbstractMargin Margin => _margin;

        /// <summary>Puts the rows in the editor, with their tints and margin.</summary>
        public void Show(IReadOnlyList<DiffRow> rows)
        {
            Rows = rows;
            int widest = 1;
            foreach (var row in rows) widest = Math.Max(widest, Math.Max(row.OldLine ?? 0, row.NewLine ?? 0));
            _digits = widest.ToString(CultureInfo.InvariantCulture).Length;

            _editor.Text = string.Join("\n", rows.Select(r => r.Text));
            var area = _editor.TextArea;
            if (!area.TextView.BackgroundRenderers.Contains(this)) area.TextView.BackgroundRenderers.Insert(0, this);
            _editor.ShowLineNumbers = false;
            if (!area.LeftMargins.Contains(_margin)) area.LeftMargins.Insert(0, _margin);
            IsShown = true;
            _margin.InvalidateMeasure();
            _margin.InvalidateVisual();
            area.TextView.InvalidateLayer(KnownLayer.Background);
        }

        /// <summary>Takes the tints and the margin away and gives the preview its line numbers back.</summary>
        public void Hide()
        {
            if (!IsShown) return;
            var area = _editor.TextArea;
            area.TextView.BackgroundRenderers.Remove(this);
            area.LeftMargins.Remove(_margin);
            _editor.ShowLineNumbers = true;
            Rows = Array.Empty<DiffRow>();
            IsShown = false;
            area.TextView.InvalidateLayer(KnownLayer.Background);
        }

        /// <summary>The tint behind a row, or null for an unchanged one.</summary>
        public static PadColor? TintOf(DiffKind kind, PadPalette palette) => kind switch
        {
            DiffKind.Added => palette.DiffAdded with { A = TintAlpha },
            DiffKind.Removed => palette.DiffRemoved with { A = TintAlpha },
            _ => null,
        };

        /// <summary><c>+</c>, <c>−</c> (U+2212) or a space.</summary>
        public static string GlyphOf(DiffKind kind) => kind switch
        {
            DiffKind.Added => "+",
            DiffKind.Removed => "−",
            _ => " ",
        };

        /// <summary>The margin's numbers for a row: its old and its current line, each right-aligned to <paramref name="digits"/>.</summary>
        public static string NumbersOf(DiffRow row, int digits) => Number(row.OldLine, digits) + " " + Number(row.NewLine, digits);

        private static string Number(int? line, int digits) =>
            (line?.ToString(CultureInfo.InvariantCulture) ?? "").PadLeft(digits);

        /// <summary>The row a document line shows, or null past the rows.</summary>
        private DiffRow? RowAt(int lineNumber) =>
            lineNumber >= 1 && lineNumber <= Rows.Count ? Rows[lineNumber - 1] : null;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!IsShown || !textView.VisualLinesValid) return;
            var palette = _palette();
            var addedBrush = PadThemeApplier.ToBrush(palette.DiffAdded with { A = TintAlpha });
            var removedBrush = PadThemeApplier.ToBrush(palette.DiffRemoved with { A = TintAlpha });
            double width = Math.Max(textView.ActualWidth, 1);
            foreach (var visual in textView.VisualLines)
            {
                if (RowAt(visual.FirstDocumentLine.LineNumber) is not { } row || row.Kind == DiffKind.Unchanged) continue;
                double top = visual.VisualTop - textView.VerticalOffset;
                drawingContext.DrawRectangle(row.Kind == DiffKind.Added ? addedBrush : removedBrush, null,
                                             new Rect(0, top, width, visual.Height));
            }
        }

        /// <summary>The margin's width: two numbers and the glyph at the preview's font.</summary>
        internal double MarginWidth()
        {
            var sample = Text(new string('9', _digits * 2 + 1) + " +", PadThemeApplier.ToBrush(_palette().LineNumbers));
            return Math.Ceiling(sample.WidthIncludingTrailingWhitespace) + 12;
        }

        /// <summary>Draws the numbers and glyphs of the visible lines (the margin's OnRender; tests call it directly).</summary>
        internal void DrawMargin(DrawingContext drawingContext)
        {
            var view = _margin.TextView;
            if (!IsShown || view == null || !view.VisualLinesValid) return;
            var palette = _palette();
            var numbers = PadThemeApplier.ToBrush(palette.LineNumbers);
            var addedGlyph = PadThemeApplier.ToBrush(palette.DiffAdded);
            var removedGlyph = PadThemeApplier.ToBrush(palette.DiffRemoved);
            foreach (var visual in view.VisualLines)
            {
                if (RowAt(visual.FirstDocumentLine.LineNumber) is not { } row) continue;
                double y = visual.VisualTop - view.VerticalOffset;
                var text = Text(NumbersOf(row, _digits) + " ", numbers);
                drawingContext.DrawText(text, new Point(4, y));
                if (row.Kind == DiffKind.Unchanged) continue;
                drawingContext.DrawText(Text(GlyphOf(row.Kind), row.Kind == DiffKind.Added ? addedGlyph : removedGlyph),
                                        new Point(4 + text.WidthIncludingTrailingWhitespace, y));
            }
        }

        private FormattedText Text(string text, Brush brush) =>
            new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(_editor.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                _editor.FontSize * 0.85, brush, VisualTreeHelper.GetDpi(_margin).PixelsPerDip);

        /// <summary>The margin itself: sized and drawn by its <see cref="DiffPreview"/>.</summary>
        private sealed class DiffMargin : AbstractMargin
        {
            private readonly DiffPreview _owner;

            public DiffMargin(DiffPreview owner) => _owner = owner;

            protected override Size MeasureOverride(Size availableSize) => new(_owner.MarginWidth(), 0);

            protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
            {
                if (oldTextView != null) oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
                base.OnTextViewChanged(oldTextView, newTextView);
                if (newTextView != null) newTextView.VisualLinesChanged += OnVisualLinesChanged;
                InvalidateVisual();
            }

            private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

            protected override void OnRender(DrawingContext drawingContext) => _owner.DrawMargin(drawingContext);
        }
    }
}
```

(`System.Windows.Forms` is a global using: `Brush`, `FlowDirection`, `Point` and `Size` exist there or in `System.Drawing` too, hence the aliases. The margin copies `BookmarkMargin`'s `OnTextViewChanged`.)

- [ ] **Step 6: The banner in `Pad/MicaPadWindow.xaml`** — in `PreviewPanel`'s banner `DockPanel`, after the `RestoreButton` and before `PreviewText`:

```xml
                        <Button x:Name="CompareButton" DockPanel.Dock="Right" Style="{StaticResource FlatButton}" Content="Compare with current"
                                ToolTip="Show what changed between this version and the note as it is now" Click="OnCompareClick" />
                        <TextBlock x:Name="DiffSummary" DockPanel.Dock="Right" VerticalAlignment="Center" FontSize="12"
                                   Margin="8,0" Visibility="Collapsed" />
```

(Right-docked children line up right to left in declaration order: Back, Copy all, Restore, Compare, then the summary; `PreviewText` fills the rest. The summary inherits the window text color, like `PreviewText`.)

- [ ] **Step 7: The window, `Pad/MicaPadWindow.xaml.cs`**

1. A new region after `OnPreviewRestoreClick`:

```csharp
        // ---- history compare -----------------------------------------------------------------

        private DiffPreview _diff = null!;

        /// <summary>The previewed version's own text: Restore and Copy all use it, whatever the preview shows.</summary>
        private string _previewVersion = "";

        /// <summary>Bumped whenever what the preview should show changes, so a compare that finishes late is dropped.</summary>
        private int _previewGeneration;

        private bool _comparing;

        /// <summary>The compare running off the UI thread, or a finished task; for tests.</summary>
        internal Task CompareTask { get; private set; } = Task.CompletedTask;

        /// <summary>The compare view of the preview; for tests.</summary>
        internal DiffPreview Diff => _diff;

        private void OnCompareClick(object sender, RoutedEventArgs e) => ToggleCompare();

        /// <summary>Compare with current (spec 5.2): the preview shows the version, or its line diff against the note now.</summary>
        internal void ToggleCompare()
        {
            if (PreviewPanel.Visibility != Visibility.Visible) return;
            _comparing = !_comparing;
            if (_comparing) StartCompare();
            else ShowPreviewVersion();
        }

        /// <summary>The preview shows the version itself, and the banner offers the compare.</summary>
        private void ShowPreviewVersion()
        {
            _previewGeneration++;
            ShowVersionText();
            CompareButton.Content = "Compare with current";
            DiffSummary.Visibility = Visibility.Collapsed;
        }

        /// <summary>The version's own text in the preview, in the note's language.</summary>
        private void ShowVersionText()
        {
            _diff.Hide();
            PreviewEditor.Text = _previewVersion;
            _previewLanguage.Apply(PadLanguages.Resolve(_shown?.Meta.Language, _shown?.Meta.SourcePath, _config.PadMarkdown, _previewVersion.Length).Effective);
        }

        /// <summary>
        /// Diffs the version against the note off the UI thread (up to a second for 1 MB) and shows
        /// the result on the dispatcher, unless the preview moved on meanwhile: another version,
        /// Back, compare turned off, or the window closing.
        /// </summary>
        private void StartCompare()
        {
            int generation = ++_previewGeneration;
            ShowVersionText();                        // the version stays on screen until the diff is ready
            CompareButton.Content = "Show this version";
            DiffSummary.Text = "Comparing…";
            DiffSummary.Visibility = Visibility.Visible;

            string version = _previewVersion;
            string current = Editor.Document.Text;
            var dispatcher = Dispatcher;
            CompareTask = Task.Run(() =>
            {
                DiffOutcome? outcome = null;
                string? failure = null;
                try
                {
                    outcome = HistoryDiff.Compare(version, current);
                }
                catch (Exception ex)
                {
                    failure = ex.GetType().Name + ": " + ex.Message;
                }
                // Guarded: MicaStats has no dispatcher exception handler, so nothing here may throw.
                dispatcher.BeginInvoke(new Action(() => Guard("Showing a compare", () => ShowCompare(generation, outcome, failure))));
            });
        }

        private void ShowCompare(int generation, DiffOutcome? outcome, string? failure)
        {
            if (generation != _previewGeneration || !_comparing || PreviewPanel.Visibility != Visibility.Visible) return;
            if (outcome == null)
            {
                DiagnosticsLog.Warn("pad", "Comparing a version failed (" + failure + ")");
                DiffSummary.Text = "Could not compare";
                return;
            }
            DiffSummary.Text = outcome.Summary;
            if (outcome.TooLarge) return;             // the version itself stays on screen
            _previewLanguage.Apply(PadLanguages.Plain);
            _diff.Show(outcome.Rows);
        }
```

2. Constructor, right after `_previewLanguage = new EditorLanguage(PreviewEditor, ...)`: `_diff = new DiffPreview(PreviewEditor, () => _palette);`.

3. `OnVersionSelected`:

```csharp
        private void OnVersionSelected(SnapshotInfo snapshot)
        {
            string? text = _workspace.ReadSnapshot(snapshot);
            if (text == null)
            {
                ShowInfo("That version could not be read.", _shown);
                return;
            }

            _previewVersion = text;
            PreviewEditor.FontFamily = Editor.FontFamily;
            PreviewEditor.FontSize = Editor.FontSize;
            PreviewEditor.WordWrap = Editor.WordWrap;
            PreviewText.Text = "Viewing " + HistoryRows.When(snapshot.Stamp, DateTime.Now);
            PreviewPanel.Visibility = Visibility.Visible;
            // Compare stays on while the user walks through versions.
            if (_comparing) StartCompare();
            else ShowPreviewVersion();
        }
```

(The old `PreviewEditor.Text = text;` and `_previewLanguage.Apply(...)` lines are gone: `ShowVersionText` does both.)

4. `EndPreview` becomes:

```csharp
        private void EndPreview()
        {
            _previewGeneration++;
            _comparing = false;
            _diff.Hide();
            CompareButton.Content = "Compare with current";
            DiffSummary.Visibility = Visibility.Collapsed;
            PreviewPanel.Visibility = Visibility.Collapsed;
            _previewVersion = "";
            PreviewEditor.Text = "";
            HistoryPanel.ClearSelection();
        }
```

5. Restore and Copy all take the version itself, never the diff's text:

```csharp
        private void OnPreviewCopyClick(object sender, RoutedEventArgs e)
        {
            if (_previewVersion.Length == 0) return;
            try
            {
                Clipboard.SetText(_previewVersion);
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                DiagnosticsLog.Warn("pad", "Copying a version failed: " + ex.Message);
            }
        }

        /// <summary>Snapshots the current text, then puts the old version in as one undoable edit.</summary>
        private void OnPreviewRestoreClick(object sender, RoutedEventArgs e)
        {
            if (_shown == null || PreviewPanel.Visibility != Visibility.Visible) return;

            string text = _previewVersion;
            _workspace.SnapshotNow(_shown, SnapshotReason.BeforeReplace);
            ReplaceText(_shown, text, markUnsaved: true);
            ShowHistory();
            Editor.Focus();
        }
```

6. `Detach()`: add `_previewGeneration++;` so a compare finishing after the window closed does nothing.

(`Guard` in `StartCompare` is `EditorMenus.Guard`, already in scope through `using static Kil0bitSystemMonitor.Pad.EditorMenus;`.)

- [ ] **Step 8: Run the focused tests, then the whole suite** — expected PASS (`PadWindowTests.History_lists_versions_and_restoring_one_can_be_undone` still passes: without compare the preview shows the version as before).

- [ ] **Step 9: Document it** — `GUIDE.md`, `### History`, after "Restoring is one step: **Ctrl+Z** undoes it.":

```markdown
**Compare with current** shows the version against the note as it is now: removed lines on red,
added lines on green, with both line numbers and − or + in the margin, and a count such as
`+12 −3 lines`. Click it again (**Show this version**) for the version itself; **Restore** always
restores the version. Notes or versions over 1 MB, or that differ on tens of thousands of lines,
say *Too large to compare*.
```

`README.md`, both MicaPad feature lists, right before the notes-folder bullet (after the **Tools** bullet Task 1 added):

```markdown
* **Compare with current**: see what changed since a history version, line by line — removed lines in red, added in green
```

```markdown
* **Compare with current**: ดูว่าอะไรเปลี่ยนไปจากเวอร์ชันในประวัติทีละบรรทัด บรรทัดที่ถูกลบเป็นสีแดง บรรทัดที่เพิ่มเป็นสีเขียว
```

- [ ] **Step 10: Commit**

```bash
git add Kil0bitSystemMonitor.csproj Services/Pad/HistoryDiff.cs Pad/DiffPreview.cs Pad/MicaPadWindow.xaml Pad/MicaPadWindow.xaml.cs GUIDE.md README.md tests/Kil0bitSystemMonitor.Tests/HistoryDiffTests.cs tests/Kil0bitSystemMonitor.Tests/PadHistoryDiffTests.cs
git commit -m "feat(pad): compare a history version with the current text" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

### Task 3: More than one window — the model

No UI in this task: the session format, and the workspace knowing which window shows which tab. Every existing test keeps passing unchanged — with one window, `Open`'s order is that window's tab order and `Active` is that window's active tab.

**Files:**
- Modify: `Services/Pad/NoteMeta.cs` (`PadWindowState`, `SessionState.Windows`)
- Create: `Services/Pad/SessionWindows.cs`, `Services/Pad/WindowTabs.cs`, `Services/Pad/PadWorkspace.Windows.cs`
- Modify: `Services/Pad/OpenNote.cs` (`WindowId`), `Services/Pad/NoteStore.cs` (`LoadSession`), `Services/Pad/PadWorkspace.cs`, `Services/Pad/PadWorkspace.Files.cs`
- Create: `tests/Kil0bitSystemMonitor.Tests/SessionWindowsTests.cs`, `tests/Kil0bitSystemMonitor.Tests/PadWorkspaceWindowsTests.cs`

**Interfaces:**
- Consumes: `NoteStore.LoadSession()` (filters `OpenNoteIds` by folder, rebuilds a corrupt or missing file from open notes by `ModifiedUtc`), `NoteStore.SerializeSession`, `NoteStore.SaveSession`; `PadWorkspace` internals `_byId`, `_unreadable`, `_clock`, `AddOpen`, `EnqueueSave`, `TryLoadInitialText`, `SamePath`; `PadTestEnv` (`Workspace`, `NewWorkspace`, `Store`, `Clock`, `FileOf`, `DiskText`, `Type`).
- Produces:
  - `public sealed class PadWindowState` — `Id`, `Open`, `Left`, `Top`, `Width` (900), `Height` (640), `Maximized`, `AlwaysOnTop`, `Zoom` (1.0), `NoteIds`, `ActiveNoteId`, `LastActiveUtc`; `SessionState.Windows` (`List<PadWindowState>?`).
  - `public static class SessionWindows` — `const double CascadeOffset = 32`; `string NewId()`; `SessionState Normalize(SessionState session, Func<string, bool>? noteExists = null)`; `void WriteMirror(SessionState session)`; `bool AnyOpen(SessionState session)`; `string Route(IReadOnlyList<(string WindowId, string? SourcePath)> notes, IReadOnlyList<string> mostRecentFirst, string? fullPath)`; `IReadOnlyList<string> ByLastActive(IReadOnlyList<PadWindowState> windows)`; `PadWindowState Cascade(PadWindowState? from)`; `double ClampZoom(double zoom)`.
  - `public sealed class WindowTabs : ReadOnlyObservableCollection<OpenNote>` — `internal void Sync(IReadOnlyList<OpenNote> wanted)`.
  - `OpenNote.WindowId` (`string`, internal set, raises `PropertyChanged`).
  - `PadWorkspace`: `IReadOnlyList<PadWindowState> Windows`, `IReadOnlyList<string> ActivationOrder`, `string MostRecentWindowId`, `OpenNote? Active` (now: the most recently active window's), `PadWindowState? WindowStateOf(string windowId)`, `WindowTabs TabsOf(string windowId)`, `OpenNote? ActiveIn(string windowId)`, `void ActivateWindow(string windowId)`, `PadWindowState NewWindow(string? fromWindowId = null)`, `void MoveToWindow(OpenNote note, string windowId)`, `string? CloseWindow(string windowId, string? into = null)`, `string RouteFile(string? path)`, `OpenNote NewNote(string windowId)`, `OpenFileResult OpenFile(string path, string windowId)`, `OpenNote? Reopen(string id, string windowId)`, `OpenNote? ReopenLastClosed(string windowId)`. `MoveTab(note, index)` now takes an index **within the note's window**. The parameterless `NewNote()`, `OpenFile(path)`, `Reopen(id)`, `ReopenLastClosed()` keep working and use `MostRecentWindowId`.

- [ ] **Step 1: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/SessionWindowsTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>session.json with windows: older files, files older MicaPads can read, bad files, and where a file goes.</summary>
    public class SessionWindowsTests : IDisposable
    {
        private readonly PadTempDir _dir = new();
        private readonly NoteStore _store;

        public SessionWindowsTests() => _store = new NoteStore(_dir.Root, warn: _ => { });

        public void Dispose() => _dir.Dispose();

        /// <summary>A note on disk, so the session may list it.</summary>
        private string Note(string text)
        {
            var meta = NoteStore.NewMeta(new DateTime(2026, 9, 30, 5, 0, 0, DateTimeKind.Utc), 1, null);
            Assert.True(_store.SaveNote(meta, text, _store.NextVersion()));
            return meta.Id;
        }

        [Fact]
        public void A_v1_session_becomes_one_window_from_the_old_fields()
        {
            string a = Note("a"), b = Note("b");
            // Exactly the shape v1.12 writes: no Windows.
            File.WriteAllText(_store.SessionPath,
                "{ \"WindowOpen\": true, \"Left\": 120, \"Top\": 80, \"Width\": 700, \"Height\": 500, \"Maximized\": true, " +
                "\"AlwaysOnTop\": true, \"Zoom\": 1.5, \"OpenNoteIds\": [\"" + a + "\", \"" + b + "\"], \"ActiveNoteId\": \"" + b + "\", \"Tabs\": {} }");

            var window = Assert.Single(_store.LoadSession().Windows!);

            Assert.False(string.IsNullOrEmpty(window.Id));
            Assert.True(window.Open);
            Assert.Equal(120, window.Left);
            Assert.Equal(80, window.Top);
            Assert.Equal(700, window.Width);
            Assert.Equal(500, window.Height);
            Assert.True(window.Maximized);
            Assert.True(window.AlwaysOnTop);
            Assert.Equal(1.5, window.Zoom);
            Assert.Equal(new[] { a, b }, window.NoteIds);
            Assert.Equal(b, window.ActiveNoteId);
        }

        [Fact]
        public void An_empty_windows_list_is_read_as_v1()
        {
            string a = Note("a");
            File.WriteAllText(_store.SessionPath, "{ \"OpenNoteIds\": [\"" + a + "\"], \"Windows\": [] }");

            Assert.Equal(new[] { a }, Assert.Single(_store.LoadSession().Windows!).NoteIds);
        }

        [Fact]
        public void A_v2_session_round_trips()
        {
            string a = Note("a"), b = Note("b"), c = Note("c");
            var lastActive = new DateTime(2026, 9, 30, 6, 0, 0, DateTimeKind.Utc);
            var session = new SessionState
            {
                Windows = new List<PadWindowState>
                {
                    new() { Id = "w1", Open = true, Left = 10, Top = 20, Width = 800, Height = 600, Zoom = 1.2, NoteIds = { a, b }, ActiveNoteId = b },
                    new() { Id = "w2", Open = true, Left = 300, Top = 200, Width = 640, Height = 480, Maximized = true, AlwaysOnTop = true,
                            NoteIds = { c }, ActiveNoteId = c, LastActiveUtc = lastActive },
                },
            };
            SessionWindows.WriteMirror(session);
            _store.SaveSession(session);

            var back = new NoteStore(_dir.Root, warn: _ => { }).LoadSession().Windows!;

            Assert.Equal(new[] { "w1", "w2" }, back.Select(w => w.Id));
            Assert.Equal(new[] { a, b }, back[0].NoteIds);
            Assert.Equal(1.2, back[0].Zoom);
            var w2 = back[1];
            Assert.Equal(300, w2.Left);
            Assert.Equal(200, w2.Top);
            Assert.Equal(640, w2.Width);
            Assert.Equal(480, w2.Height);
            Assert.True(w2.Maximized);
            Assert.True(w2.AlwaysOnTop);
            Assert.Equal(new[] { c }, w2.NoteIds);
            Assert.Equal(c, w2.ActiveNoteId);
            Assert.Equal(lastActive, w2.LastActiveUtc);
        }

        [Fact]
        public void A_v2_file_keeps_the_v1_fields_an_older_micapad_reads()
        {
            string a = Note("a"), b = Note("b"), c = Note("c");
            var session = new SessionState
            {
                Windows = new List<PadWindowState>
                {
                    new() { Id = "w1", Open = false, Left = 10, Top = 20, Width = 800, Height = 600, Zoom = 1.2, NoteIds = { a, b }, ActiveNoteId = b },
                    new() { Id = "w2", Open = true, Left = 300, NoteIds = { c }, ActiveNoteId = c },
                },
            };

            SessionWindows.WriteMirror(session);
            using var json = JsonDocument.Parse(NoteStore.SerializeSession(session));
            var root = json.RootElement;

            // What v1.12 reads: every tab (none vanishes after a downgrade), and the first window's place, zoom and active tab.
            Assert.Equal(new[] { a, b, c }, root.GetProperty("OpenNoteIds").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(b, root.GetProperty("ActiveNoteId").GetString());
            Assert.Equal(10, root.GetProperty("Left").GetDouble());
            Assert.Equal(800, root.GetProperty("Width").GetDouble());
            Assert.Equal(1.2, root.GetProperty("Zoom").GetDouble());
            Assert.True(root.GetProperty("WindowOpen").GetBoolean());       // some window was open: v1.12 reopens at login
        }

        [Fact]
        public void A_session_an_older_micapad_rewrote_loads_as_one_window_with_every_tab()
        {
            string a = Note("a"), b = Note("b"), c = Note("c");
            var session = new SessionState
            {
                Windows = new List<PadWindowState> { new() { Id = "w1", NoteIds = { a, b } }, new() { Id = "w2", NoteIds = { c } } },
            };
            SessionWindows.WriteMirror(session);
            session.Windows = null;                          // v1.12 does not know Windows and writes the old fields only
            _store.SaveSession(session);

            Assert.Equal(new[] { a, b, c }, Assert.Single(_store.LoadSession().Windows!).NoteIds);
        }

        [Fact]
        public void Bad_windows_are_dropped_and_a_note_in_two_windows_stays_in_the_first()
        {
            string a = Note("a"), b = Note("b");
            string gone = Guid.NewGuid().ToString("N");
            var session = new SessionState
            {
                OpenNoteIds = { a, b },
                Windows = new List<PadWindowState>
                {
                    new() { Id = "w1", NoteIds = { a, gone }, ActiveNoteId = gone },
                    new() { Id = "", NoteIds = { b } },              // no id
                    new() { Id = "w1", NoteIds = { b } },            // the same id again
                    new() { Id = "w3", NoteIds = { a, b } },         // a is w1's already
                    new() { Id = "w4", NoteIds = { gone } },         // nothing left to show
                },
            };
            _store.SaveSession(session);

            var back = _store.LoadSession().Windows!;

            Assert.Equal(new[] { "w1", "w3" }, back.Select(w => w.Id));
            Assert.Equal(new[] { a }, back[0].NoteIds);
            Assert.Null(back[0].ActiveNoteId);
            Assert.Equal(new[] { b }, back[1].NoteIds);
        }

        [Theory]
        [InlineData("garbage")]
        [InlineData("{ \"Windows\": 5 }")]
        [InlineData("{ \"Windows\": [ { \"Id\": 7 } ] }")]
        public void An_unreadable_session_is_rebuilt_into_one_window(string json)
        {
            string a = Note("a");
            File.WriteAllText(_store.SessionPath, json);

            var window = Assert.Single(_store.LoadSession().Windows!);

            Assert.Equal(new[] { a }, window.NoteIds);
            Assert.False(window.Open);
        }

        [Theory]
        [InlineData(0.0, 0.5)]
        [InlineData(9.0, 4.0)]
        [InlineData(1.25, 1.25)]
        public void Zoom_is_kept_in_range(double saved, double expected)
        {
            var session = new SessionState { Windows = new List<PadWindowState> { new() { Id = "w", Zoom = saved, NoteIds = { "n" } } } };
            Assert.Equal(expected, SessionWindows.Normalize(session).Windows![0].Zoom);
        }

        [Fact]
        public void A_file_goes_to_the_window_already_showing_it_else_to_the_most_recent()
        {
            var notes = new List<(string WindowId, string? SourcePath)> { ("w1", null), ("w2", @"C:\Logs\app.log"), ("w1", @"C:\a.txt") };
            var order = new[] { "w1", "w2" };

            Assert.Equal("w2", SessionWindows.Route(notes, order, @"c:\LOGS\App.log"));     // Windows paths ignore case
            Assert.Equal("w1", SessionWindows.Route(notes, order, @"C:\other.txt"));
            Assert.Equal("w1", SessionWindows.Route(notes, order, null));
            Assert.Equal("w2", SessionWindows.Route(notes, new[] { "w2", "w1" }, null));
        }

        [Fact]
        public void After_a_restart_the_most_recently_active_window_comes_first()
        {
            var t = new DateTime(2026, 9, 30, 6, 0, 0, DateTimeKind.Utc);
            var windows = new List<PadWindowState>
            {
                new() { Id = "w1", LastActiveUtc = t },
                new() { Id = "w2", LastActiveUtc = t.AddMinutes(5) },
                new() { Id = "w3" },                                  // never activated (an older session)
                new() { Id = "w4", LastActiveUtc = t },
            };

            Assert.Equal(new[] { "w2", "w1", "w4", "w3" }, SessionWindows.ByLastActive(windows));
        }

        [Fact]
        public void Reopen_at_login_is_wanted_when_any_window_was_open()
        {
            Assert.True(SessionWindows.AnyOpen(new SessionState { Windows = new List<PadWindowState> { new() { Id = "a" }, new() { Id = "b", Open = true } } }));
            Assert.False(SessionWindows.AnyOpen(new SessionState { Windows = new List<PadWindowState> { new() { Id = "a" } } }));
            Assert.True(SessionWindows.AnyOpen(new SessionState { WindowOpen = true }));   // v1: the old flag
        }

        [Fact]
        public void A_new_window_opens_offset_from_the_one_it_came_from()
        {
            var from = new PadWindowState { Id = "w1", Left = 100, Top = 50, Width = 700, Height = 500, Zoom = 2, AlwaysOnTop = true, Maximized = true };

            var next = SessionWindows.Cascade(from);

            Assert.NotEqual("w1", next.Id);
            Assert.False(string.IsNullOrEmpty(next.Id));
            Assert.Equal(132, next.Left);
            Assert.Equal(82, next.Top);
            Assert.Equal(700, next.Width);
            Assert.Equal(500, next.Height);
            Assert.Equal(1.0, next.Zoom);
            Assert.False(next.AlwaysOnTop);
            Assert.False(next.Maximized);
            Assert.True(next.Open);
            Assert.Empty(next.NoteIds);
            Assert.Null(SessionWindows.Cascade(null).Left);         // no window to start from: Windows places it
        }
    }
}
```

`tests/Kil0bitSystemMonitor.Tests/PadWorkspaceWindowsTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>More than one MicaPad window, in the workspace: whose tabs are whose, moving, closing, routing, restarting.</summary>
    public class PadWorkspaceWindowsTests : IDisposable
    {
        private readonly PadTestEnv _env = new();

        private PadWorkspace Ws => _env.Workspace;

        private string First => Ws.Windows[0].Id;

        public void Dispose() => _env.Dispose();

        [Fact]
        public void A_workspace_starts_with_one_window_that_new_notes_go_to()
        {
            var note = Ws.NewNote();

            var window = Assert.Single(Ws.Windows);
            Assert.Equal(window.Id, note.WindowId);
            Assert.Equal(new[] { note }, Ws.TabsOf(window.Id));
            Assert.Same(note, Ws.ActiveIn(window.Id));
            Assert.Same(note, Ws.Active);
        }

        [Fact]
        public void Each_window_has_its_own_tabs_and_active_tab()
        {
            var a = Ws.NewNote();
            var second = Ws.NewWindow(First);
            var b = Ws.NewNote(second.Id);
            var c = Ws.NewNote(second.Id);

            Assert.Equal(new[] { a }, Ws.TabsOf(First));
            Assert.Equal(new[] { b, c }, Ws.TabsOf(second.Id));
            Assert.Same(a, Ws.ActiveIn(First));
            Assert.Same(c, Ws.ActiveIn(second.Id));
            Assert.True(a.IsActive);                    // one active tab per window
            Assert.True(c.IsActive);
            Assert.False(b.IsActive);
            Assert.Equal(3, Ws.Open.Count);             // still one list of open notes
            Assert.Equal(second.Id, Ws.MostRecentWindowId);
        }

        [Fact]
        public void Move_tab_reorders_within_its_window_only()
        {
            var a = Ws.NewNote();
            var second = Ws.NewWindow(First);
            var x = Ws.NewNote(second.Id);
            var b = Ws.NewNote(First);                  // after a: Open is a, b, x
            var y = Ws.NewNote(second.Id);              // after x: a, b, x, y
            var c = Ws.NewNote(First);                  // after b: a, b, c, x, y

            Ws.MoveTab(c, 0);

            Assert.Equal(new[] { c, a, b }, Ws.TabsOf(First));
            Assert.Equal(new[] { x, y }, Ws.TabsOf(second.Id));
            Ws.MoveTab(c, 99);                          // clamped to this window's last tab
            Assert.Equal(new[] { a, b, c }, Ws.TabsOf(First));
        }

        [Fact]
        public void Moving_a_note_to_another_window_keeps_it_open_and_makes_it_active_there()
        {
            var a = Ws.NewNote();
            var b = Ws.NewNote();
            var second = Ws.NewWindow(First);
            var x = Ws.NewNote(second.Id);
            PadTestEnv.Type(Ws, b, "moving");

            Ws.MoveToWindow(b, second.Id);

            Assert.Equal(second.Id, b.WindowId);
            Assert.Equal(new[] { a }, Ws.TabsOf(First));
            Assert.Equal(new[] { x, b }, Ws.TabsOf(second.Id));
            Assert.Same(a, Ws.ActiveIn(First));         // the neighbour took over
            Assert.True(a.IsActive);
            Assert.Same(b, Ws.ActiveIn(second.Id));
            Assert.False(x.IsActive);
            Assert.Equal(3, Ws.Open.Count);
            Assert.Equal("moving", b.TextProvider());
            Assert.Empty(Ws.ClosedNotes());
        }

        [Fact]
        public void Closing_a_window_moves_its_tabs_to_the_most_recently_active_other_window()
        {
            var a = Ws.NewNote();
            var second = Ws.NewWindow(First);
            var x = Ws.NewNote(second.Id);
            var y = Ws.NewNote(second.Id);
            var third = Ws.NewWindow(First);
            Ws.NewNote(third.Id);
            Ws.ActivateWindow(First);
            Ws.ActivateWindow(second.Id);               // most recent first: second, first, third

            string? into = Ws.CloseWindow(second.Id);

            Assert.Equal(First, into);
            Assert.Equal(new[] { First, third.Id }, Ws.Windows.Select(w => w.Id));
            Assert.Equal(new[] { a, x, y }, Ws.TabsOf(First));
            Assert.Same(a, Ws.ActiveIn(First));         // the tabs arrive at the end; the active tab stays
            Assert.False(y.IsActive);
            Assert.Equal(4, Ws.Open.Count);
            Assert.Empty(Ws.ClosedNotes());             // no note is closed by closing a window
            Assert.Equal(new[] { First, third.Id }, Ws.ActivationOrder);
        }

        [Fact]
        public void Closing_a_window_keeps_unsaved_file_edits_in_the_other_window()
        {
            string path = _env.FileOf("draft.txt");
            File.WriteAllText(path, "on disk");
            Ws.NewNote();
            var second = Ws.NewWindow(First);
            var file = Ws.OpenFile(path, second.Id).Note!;
            PadTestEnv.Type(Ws, file, "on disk plus my edit");
            Assert.True(file.HasUnsavedEdits);

            Ws.CloseWindow(second.Id);
            Assert.True(Ws.FlushAll(TimeSpan.FromSeconds(5)));

            Assert.Equal(First, file.WindowId);
            Assert.True(file.HasUnsavedEdits);
            Assert.Equal("on disk plus my edit", file.TextProvider());
            Assert.Equal("on disk plus my edit", _env.DiskText(file));      // MicaPad's copy has the edit
            Assert.Equal("on disk", File.ReadAllText(path));                // the file itself is untouched

            var restored = _env.NewWorkspace();
            restored.Restore();
            Assert.Contains(restored.TabsOf(restored.Windows[0].Id), n => n.Id == file.Id && n.TextProvider() == "on disk plus my edit");
        }

        [Fact]
        public void The_only_window_is_not_closed_this_way()
        {
            var a = Ws.NewNote();

            Assert.Null(Ws.CloseWindow(First));
            Assert.Single(Ws.Windows);
            Assert.Equal(new[] { a }, Ws.TabsOf(First));
        }

        [Fact]
        public void Windows_come_back_after_a_restart_with_their_tabs_active_tab_and_place()
        {
            var a = Ws.NewNote();
            PadTestEnv.Type(Ws, a, "a");
            var b = Ws.NewNote();
            PadTestEnv.Type(Ws, b, "b");
            var second = Ws.NewWindow(First);
            var x = Ws.NewNote(second.Id);
            PadTestEnv.Type(Ws, x, "x");
            Ws.SetActive(a);
            second.Left = 300;
            second.Top = 200;
            second.Width = 640;
            second.Height = 480;
            second.Zoom = 1.5;
            second.AlwaysOnTop = true;
            Ws.ActivateWindow(First);
            _env.Clock.Advance(1);
            Ws.ActivateWindow(second.Id);               // the second was used last
            Assert.True(Ws.FlushAll(TimeSpan.FromSeconds(5)));

            var restored = _env.NewWorkspace();
            restored.Restore();

            Assert.Equal(new[] { First, second.Id }, restored.Windows.Select(w => w.Id));
            Assert.Equal(new[] { a.Id, b.Id }, restored.TabsOf(First).Select(n => n.Id));
            Assert.Equal(new[] { x.Id }, restored.TabsOf(second.Id).Select(n => n.Id));
            Assert.Equal(a.Id, restored.ActiveIn(First)!.Id);
            var place = restored.WindowStateOf(second.Id)!;
            Assert.Equal(300, place.Left);
            Assert.Equal(200, place.Top);
            Assert.Equal(640, place.Width);
            Assert.Equal(480, place.Height);
            Assert.Equal(1.5, place.Zoom);
            Assert.True(place.AlwaysOnTop);
            Assert.Equal(second.Id, restored.MostRecentWindowId);
        }

        [Fact]
        public void A_note_that_cannot_be_read_at_restore_returns_to_its_own_window()
        {
            var a = Ws.NewNote();
            PadTestEnv.Type(Ws, a, "a");
            var second = Ws.NewWindow(First);
            var x = Ws.NewNote(second.Id);
            PadTestEnv.Type(Ws, x, "x");
            var y = Ws.NewNote(second.Id);
            PadTestEnv.Type(Ws, y, "y");
            Assert.True(Ws.FlushAll(TimeSpan.FromSeconds(5)));

            var restored = _env.NewWorkspace();
            using (new FileStream(_env.Store.CurrentPath(y.Id), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                restored.Restore();
                Assert.Equal(new[] { x.Id }, restored.TabsOf(second.Id).Select(n => n.Id));
                Assert.True(restored.FlushAll(TimeSpan.FromSeconds(5)));
            }

            Assert.Equal(new[] { x.Id, y.Id }, _env.Store.LoadSession().Windows![1].NoteIds);
        }

        [Fact]
        public void Opening_a_file_goes_to_the_given_window_and_stays_there()
        {
            string path = _env.FileOf("notes.txt");
            File.WriteAllText(path, "text");
            Ws.NewNote();
            var second = Ws.NewWindow(First);

            var opened = Ws.OpenFile(path, second.Id);
            var again = Ws.OpenFile(path, First);

            Assert.Equal(second.Id, opened.Note!.WindowId);
            Assert.Equal(OpenFileStatus.AlreadyOpen, again.Status);
            Assert.Same(opened.Note, again.Note);
            Assert.Equal(second.Id, again.Note!.WindowId);   // the window layer brings that window forward
        }

        [Fact]
        public void A_file_is_routed_to_the_window_showing_it()
        {
            string path = _env.FileOf("app.log");
            File.WriteAllText(path, "log");
            Ws.NewNote();
            var second = Ws.NewWindow(First);
            Ws.OpenFile(path, second.Id);
            Ws.ActivateWindow(First);

            Assert.Equal(second.Id, Ws.RouteFile(path.ToUpperInvariant()));
            Assert.Equal(First, Ws.RouteFile(_env.FileOf("other.txt")));
            Assert.Equal(First, Ws.RouteFile(null));
        }

        [Fact]
        public void Reopening_a_closed_note_goes_to_the_window_that_asked()
        {
            var a = Ws.NewNote();
            PadTestEnv.Type(Ws, a, "keep");
            var second = Ws.NewWindow(First);
            Ws.NewNote(second.Id);
            Ws.Close(a);

            var back = Ws.Reopen(a.Id, second.Id)!;

            Assert.Equal(second.Id, back.WindowId);
            Assert.Same(back, Ws.ActiveIn(second.Id));
            Assert.Empty(Ws.TabsOf(First));
        }

        [Fact]
        public void A_window_tab_list_follows_with_moves_not_rebuilds()
        {
            var meta = NoteStore.NewMeta(DateTime.UtcNow, 1, null);
            var a = new OpenNote(meta, "");
            var b = new OpenNote(NoteStore.NewMeta(DateTime.UtcNow, 2, null), "");
            var c = new OpenNote(NoteStore.NewMeta(DateTime.UtcNow, 3, null), "");
            var tabs = new WindowTabs();
            tabs.Sync(new[] { a, b, c });
            var actions = new List<NotifyCollectionChangedAction>();
            ((INotifyCollectionChanged)tabs).CollectionChanged += (s, e) => actions.Add(e.Action);

            tabs.Sync(new[] { c, a });

            Assert.Equal(new[] { c, a }, tabs);
            Assert.Equal(new[] { NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Move }, actions);
        }
    }
}
```

(`OpenNote`'s constructor is internal; the test project sees internals. The last test pins why a tab strip keeps its tab containers — and a drag in progress — when a tab moves.)

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~SessionWindowsTests|FullyQualifiedName~PadWorkspaceWindowsTests"`). Expected: build FAILS (`PadWindowState`, `SessionWindows`, `WindowTabs`, `TabsOf` not found).

- [ ] **Step 3: `PadWindowState` and `SessionState.Windows` in `Services/Pad/NoteMeta.cs`**

Change `SessionState`'s summary to `/// <summary>MicaPad's windows and their tabs, persisted as <c>session.json</c>.</summary>` and add, after `Tabs`:

```csharp
        /// <summary>
        /// Every MicaPad window (spec 5.3), in a stable order. The first one is also written to the
        /// fields above, and <see cref="OpenNoteIds"/> lists every window's notes, so an older
        /// MicaPad still opens every tab. Null in sessions written before windows existed: those
        /// load as one window made from the fields above.
        /// </summary>
        public List<PadWindowState>? Windows { get; set; }
```

and, after `SessionState`:

```csharp
    /// <summary>One MicaPad window in <c>session.json</c> (spec 5.3). Changed on the UI thread only.</summary>
    public sealed class PadWindowState
    {
        /// <summary>The window's id; each open note names its window by it (<see cref="OpenNote.WindowId"/>).</summary>
        public string Id { get; set; } = "";

        /// <summary>The window was showing when this was written; drives reopen at login.</summary>
        public bool Open { get; set; }

        /// <summary>Null until the window has been placed once.</summary>
        public double? Left { get; set; }

        /// <summary>Null until the window has been placed once.</summary>
        public double? Top { get; set; }

        /// <summary>Width in device-independent pixels; default 900.</summary>
        public double Width { get; set; } = 900;

        /// <summary>Height in device-independent pixels; default 640.</summary>
        public double Height { get; set; } = 640;

        public bool Maximized { get; set; }

        public bool AlwaysOnTop { get; set; }

        /// <summary>Editor zoom factor, 0.5 to 4.</summary>
        public double Zoom { get; set; } = 1.0;

        /// <summary>The window's notes, in tab order.</summary>
        public List<string> NoteIds { get; set; } = new();

        /// <summary>The window's selected tab, or null.</summary>
        public string? ActiveNoteId { get; set; }

        /// <summary>When the window was last active, so the most recently active one is known after a restart. Null in older sessions.</summary>
        public DateTime? LastActiveUtc { get; set; }
    }
```

- [ ] **Step 4: Write `Services/Pad/SessionWindows.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// <c>session.json</c>'s windows (spec 5.3): sessions from before windows existed, checking
    /// what a file says, the fields older MicaPads read, which window a file goes to, and where a
    /// new window opens. Pure: the store and the workspace call it.
    /// </summary>
    public static class SessionWindows
    {
        /// <summary>How far a new window opens below and to the right of the one it came from, in DIPs.</summary>
        public const double CascadeOffset = 32;

        public static string NewId() => Guid.NewGuid().ToString("N");

        /// <summary>
        /// Makes <see cref="SessionState.Windows"/> usable, in place. A session without windows
        /// (written by v1.12, rebuilt from the notes, or brand new) becomes one window made from its
        /// old top-level fields. Windows without an id, with an id seen before, or with no note left
        /// are dropped; a note listed twice stays in the first window listing it; notes
        /// <paramref name="noteExists"/> rejects are dropped; zoom is kept between 0.5 and 4. If no
        /// window is left, one is made from the old fields.
        /// </summary>
        public static SessionState Normalize(SessionState session, Func<string, bool>? noteExists = null)
        {
            noteExists ??= _ => true;
            session.OpenNoteIds ??= new List<string>();
            session.Tabs ??= new Dictionary<string, TabViewState>();

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var seenNotes = new HashSet<string>(StringComparer.Ordinal);
            var windows = new List<PadWindowState>();
            foreach (var window in session.Windows ?? new List<PadWindowState>())
            {
                if (window == null || string.IsNullOrWhiteSpace(window.Id) || !ids.Add(window.Id)) continue;

                var kept = new List<string>();
                foreach (string id in window.NoteIds ?? new List<string>())
                    if (!string.IsNullOrEmpty(id) && noteExists(id) && seenNotes.Add(id)) kept.Add(id);
                if (kept.Count == 0) continue;

                window.NoteIds = kept;
                if (window.ActiveNoteId != null && !kept.Contains(window.ActiveNoteId)) window.ActiveNoteId = null;
                window.Zoom = ClampZoom(window.Zoom);
                windows.Add(window);
            }

            if (windows.Count == 0) windows.Add(FromOldFields(session, noteExists));
            session.Windows = windows;
            return session;
        }

        /// <summary>
        /// Writes the fields an older MicaPad reads (downgrade safety): the first window's place,
        /// zoom, on-top and active tab; every window's notes in <see cref="SessionState.OpenNoteIds"/>
        /// (the first window's first), so no tab disappears; <see cref="SessionState.WindowOpen"/>
        /// when any window was open.
        /// </summary>
        public static void WriteMirror(SessionState session)
        {
            var windows = session.Windows;
            if (windows == null || windows.Count == 0) return;
            var first = windows[0];
            session.WindowOpen = windows.Any(w => w.Open);
            session.Left = first.Left;
            session.Top = first.Top;
            session.Width = first.Width;
            session.Height = first.Height;
            session.Maximized = first.Maximized;
            session.AlwaysOnTop = first.AlwaysOnTop;
            session.Zoom = first.Zoom;
            session.OpenNoteIds = windows.SelectMany(w => w.NoteIds).Distinct().ToList();
            session.ActiveNoteId = first.ActiveNoteId;
        }

        /// <summary>True when MicaPad should reopen at login: any window was open (or, in a v1 session, the window was).</summary>
        public static bool AnyOpen(SessionState session) =>
            session.Windows is { Count: > 0 } windows ? windows.Any(w => w.Open) : session.WindowOpen;

        /// <summary>
        /// The window a file goes to (spec 5.3): the one already showing it (paths compared as
        /// Windows does, ignoring case), otherwise the most recently active. With no path, the most
        /// recently active.
        /// </summary>
        public static string Route(IReadOnlyList<(string WindowId, string? SourcePath)> notes, IReadOnlyList<string> mostRecentFirst, string? fullPath)
        {
            if (fullPath != null)
            {
                foreach (var (windowId, source) in notes)
                {
                    if (source != null && string.Equals(source, fullPath, StringComparison.OrdinalIgnoreCase) && mostRecentFirst.Contains(windowId))
                        return windowId;
                }
            }
            return mostRecentFirst[0];
        }

        /// <summary>Window ids, the most recently active first; windows never activated keep their session order, last.</summary>
        public static IReadOnlyList<string> ByLastActive(IReadOnlyList<PadWindowState> windows) =>
            windows.Select((window, index) => (window, index))
                   .OrderByDescending(x => x.window.LastActiveUtc ?? DateTime.MinValue)
                   .ThenBy(x => x.index)
                   .Select(x => x.window.Id)
                   .ToList();

        /// <summary>A new window: the size of <paramref name="from"/>, a little below and right of it, zoom 1, not on top.</summary>
        public static PadWindowState Cascade(PadWindowState? from) => new()
        {
            Id = NewId(),
            Open = true,
            Left = from?.Left + CascadeOffset,
            Top = from?.Top + CascadeOffset,
            Width = from?.Width ?? 900,
            Height = from?.Height ?? 640,
        };

        /// <summary>Zoom within 0.5 to 4; anything unreadable is 1.</summary>
        public static double ClampZoom(double zoom) => double.IsFinite(zoom) ? Math.Clamp(zoom, 0.5, 4.0) : 1.0;

        private static PadWindowState FromOldFields(SessionState session, Func<string, bool> noteExists)
        {
            var noteIds = session.OpenNoteIds.Where(id => !string.IsNullOrEmpty(id) && noteExists(id)).Distinct().ToList();
            return new PadWindowState
            {
                Id = NewId(),
                Open = session.WindowOpen,
                Left = session.Left,
                Top = session.Top,
                Width = session.Width,
                Height = session.Height,
                Maximized = session.Maximized,
                AlwaysOnTop = session.AlwaysOnTop,
                Zoom = ClampZoom(session.Zoom),
                NoteIds = noteIds,
                ActiveNoteId = session.ActiveNoteId != null && noteIds.Contains(session.ActiveNoteId) ? session.ActiveNoteId : null,
            };
        }
    }
}
```

- [ ] **Step 5: `NoteStore.LoadSession` normalizes windows** — in `Services/Pad/NoteStore.cs`, `LoadSession`:

1. After `session.Tabs ??= new Dictionary<string, TabViewState>();` replace `return session;` with

```csharp
                            return SessionWindows.Normalize(session, id => Directory.Exists(NoteDir(id)));
```

2. Replace the final `return RebuildSession();` with

```csharp
                // Phase 1's rebuild, into one window (spec "Error handling"): also for a v2 file that cannot be read.
                return SessionWindows.Normalize(RebuildSession());
```

Update its summary: "…ids whose folder is gone are dropped. Always returns at least one window (<see cref="SessionWindows.Normalize"/>)." A `"Windows"` of the wrong JSON type throws `JsonException`, which the existing catch turns into the rebuild.

- [ ] **Step 6: Write `Services/Pad/WindowTabs.cs`**

```csharp
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// One window's tabs, in tab order: the notes of <see cref="PadWorkspace.Open"/> whose
    /// <see cref="OpenNote.WindowId"/> is that window's (spec 5.3). A tab strip binds to it; the
    /// workspace keeps it in step. Read-only to everyone else, so there is one truth: <c>Open</c>
    /// and each note's window id.
    /// </summary>
    public sealed class WindowTabs : ReadOnlyObservableCollection<OpenNote>
    {
        private readonly ObservableCollection<OpenNote> _items;

        public WindowTabs() : this(new ObservableCollection<OpenNote>())
        {
        }

        private WindowTabs(ObservableCollection<OpenNote> items) : base(items) => _items = items;

        /// <summary>
        /// Makes the list equal <paramref name="wanted"/> with removals, moves and inserts only — never
        /// a reset — so a tab strip keeps its tab containers, and a tab drag its tab.
        /// </summary>
        internal void Sync(IReadOnlyList<OpenNote> wanted)
        {
            var keep = new HashSet<OpenNote>(wanted);
            for (int i = _items.Count - 1; i >= 0; i--)
                if (!keep.Contains(_items[i])) _items.RemoveAt(i);

            for (int i = 0; i < wanted.Count; i++)
            {
                int at = _items.IndexOf(wanted[i]);
                if (at == i) continue;
                if (at < 0) _items.Insert(i, wanted[i]);
                else _items.Move(at, i);        // at > i: every place before i already holds a wanted note
            }
        }
    }
}
```

(`OpenNote` does not override `Equals`, so the set and `IndexOf` compare references.)

- [ ] **Step 7: `OpenNote.WindowId`** — in `Services/Pad/OpenNote.cs`, add a field `private string _windowId = "";` and, after `IsActive`:

```csharp
        /// <summary>The MicaPad window showing this tab (spec 5.3); changed by the workspace when the tab moves.</summary>
        public string WindowId
        {
            get => _windowId;
            internal set => Set(ref _windowId, value);
        }
```

- [ ] **Step 8: Write `Services/Pad/PadWorkspace.Windows.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Pad
{
    public sealed partial class PadWorkspace
    {
        /// <summary>Each window's tab list, kept in step with <see cref="Open"/> by <see cref="SyncTabs"/>.</summary>
        private readonly Dictionary<string, WindowTabs> _tabs = new();

        /// <summary>Each window's active tab.</summary>
        private readonly Dictionary<string, OpenNote> _active = new();

        /// <summary>Window ids, most recently active first. Ids of windows since closed are skipped when read.</summary>
        private readonly List<string> _activation = new();

        /// <summary>The window each note skipped at <see cref="Restore"/> belongs to, so it returns there.</summary>
        private readonly Dictionary<string, string> _unreadableWindow = new();

        /// <summary>Every MicaPad window, in session order (spec 5.3); the first is the one older MicaPads see.</summary>
        public IReadOnlyList<PadWindowState> Windows => Session.Windows!;

        /// <summary>Window ids, the most recently active first; windows never activated follow in session order.</summary>
        public IReadOnlyList<string> ActivationOrder
        {
            get
            {
                var order = _activation.Where(id => WindowStateOf(id) != null).ToList();
                order.AddRange(Windows.Select(w => w.Id).Where(id => !order.Contains(id)));
                return order;
            }
        }

        /// <summary>Where the hotkey, the overlay and a note opened without a window go: the most recently active window.</summary>
        public string MostRecentWindowId => ActivationOrder[0];

        /// <summary>The active tab of the most recently active window, or null when it has none.</summary>
        public OpenNote? Active => ActiveIn(MostRecentWindowId);

        /// <summary>A window's saved state, or null when there is no such window.</summary>
        public PadWindowState? WindowStateOf(string windowId) => Windows.FirstOrDefault(w => w.Id == windowId);

        /// <summary>A window's tabs, in tab order; a tab strip binds to it.</summary>
        public WindowTabs TabsOf(string windowId)
        {
            if (!_tabs.TryGetValue(windowId, out var tabs))
            {
                tabs = new WindowTabs();
                tabs.Sync(NotesIn(windowId));
                _tabs[windowId] = tabs;
            }
            return tabs;
        }

        /// <summary>A window's active tab, or null.</summary>
        public OpenNote? ActiveIn(string windowId) => _active.TryGetValue(windowId, out var note) ? note : null;

        /// <summary>A window came to the front: it is now the most recently active, also after a restart.</summary>
        public void ActivateWindow(string windowId)
        {
            if (WindowStateOf(windowId) is not { } state) return;
            _activation.Remove(windowId);
            _activation.Insert(0, windowId);
            state.LastActiveUtc = _clock();
        }

        /// <summary>
        /// A new window (spec 5.3), open and most recently active, cascaded from
        /// <paramref name="fromWindowId"/> (or the most recently active window). It has no tabs yet:
        /// the caller adds a note or moves one in.
        /// </summary>
        public PadWindowState NewWindow(string? fromWindowId = null)
        {
            var state = SessionWindows.Cascade(WindowStateOf(fromWindowId ?? MostRecentWindowId));
            Session.Windows!.Add(state);
            ActivateWindow(state.Id);
            return state;
        }

        /// <summary>
        /// Moves an open tab to the end of another window's tabs, where it becomes the active tab;
        /// its old window's active tab falls to a neighbour. The note itself, its text and its
        /// unsaved edits are untouched, and nothing is closed.
        /// </summary>
        public void MoveToWindow(OpenNote note, string windowId)
        {
            if (!_byId.ContainsKey(note.Id) || note.WindowId == windowId || WindowStateOf(windowId) == null) return;
            string from = note.WindowId;
            int index = TabsOf(from).IndexOf(note);
            bool wasActive = ReferenceEquals(ActiveIn(from), note);

            MoveToEndOf(note, windowId);

            if (wasActive)
            {
                _active.Remove(from);
                var rest = TabsOf(from);
                if (rest.Count > 0) SetActive(rest[Math.Min(index, rest.Count - 1)]);
                else if (WindowStateOf(from) is { } state) state.ActiveNoteId = null;
            }
            SetActive(note);
        }

        /// <summary>
        /// Closes a window (spec 5.3): its tabs move, in order, to the end of <paramref name="into"/>,
        /// or else of the most recently active other window, whose active tab stays; no note is
        /// closed. Returns the window that took them, or null (nothing done) when this is the only
        /// window or the target is not another window.
        /// </summary>
        public string? CloseWindow(string windowId, string? into = null)
        {
            var state = WindowStateOf(windowId);
            if (state == null || Windows.Count < 2) return null;
            string? target = into ?? ActivationOrder.FirstOrDefault(id => id != windowId);
            if (target == null || target == windowId || WindowStateOf(target) == null) return null;

            if (ActiveIn(windowId) is { } active) active.IsActive = false;
            _active.Remove(windowId);
            foreach (var note in TabsOf(windowId).ToList()) MoveToEndOf(note, target);
            foreach (string id in _unreadableWindow.Where(p => p.Value == windowId).Select(p => p.Key).ToList())
                _unreadableWindow[id] = target;

            Session.Windows!.Remove(state);
            _activation.Remove(windowId);
            _tabs.Remove(windowId);
            if (ActiveIn(target) == null && TabsOf(target).Count > 0) SetActive(TabsOf(target)[0]);
            SaveSession();
            return target;
        }

        /// <summary>The window a file opened from outside (<c>--pad</c>, Open with) goes to; see <see cref="SessionWindows.Route"/>.</summary>
        public string RouteFile(string? path)
        {
            string? full = null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    full = Path.GetFullPath(path);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    full = null;
                }
            }
            return SessionWindows.Route(Open.Select(n => (n.WindowId, n.Meta.SourcePath)).ToList(), ActivationOrder, full);
        }

        /// <summary>Gives a note to a window, after that window's last tab in <see cref="Open"/>.</summary>
        private void MoveToEndOf(OpenNote note, string windowId)
        {
            var targetTabs = TabsOf(windowId);          // read before the note joins it
            note.WindowId = windowId;
            if (targetTabs.Count > 0)
            {
                int current = Open.IndexOf(note);
                int last = Open.IndexOf(targetTabs[targetTabs.Count - 1]);
                int to = current < last ? last : last + 1;
                if (to != current) Open.Move(current, to);
            }
            SyncTabs();
        }

        /// <summary>Brings every window's tab list in step with <see cref="Open"/> and the notes' window ids.</summary>
        private void SyncTabs()
        {
            foreach (var pair in _tabs.ToList()) pair.Value.Sync(NotesIn(pair.Key));
        }

        private List<OpenNote> NotesIn(string windowId) => Open.Where(n => n.WindowId == windowId).ToList();

        /// <summary>Where a new tab of a window goes in <see cref="Open"/>: after its active tab, else after its last one.</summary>
        private int InsertIndexAfterActive(string windowId)
        {
            if (ActiveIn(windowId) is { } active) return Open.IndexOf(active) + 1;
            var tabs = TabsOf(windowId);
            return tabs.Count == 0 ? Open.Count : Open.IndexOf(tabs[tabs.Count - 1]) + 1;
        }

        /// <summary>The window a note skipped at restore belongs to; the first window when its own is gone.</summary>
        private string UnreadableWindowOf(string id) =>
            _unreadableWindow.TryGetValue(id, out var windowId) && WindowStateOf(windowId) != null ? windowId : Windows[0].Id;
    }
}
```

- [ ] **Step 9: `Services/Pad/PadWorkspace.cs`**

1. Constructor, last line: `Open.CollectionChanged += (s, e) => SyncTabs();` — first subscriber, so every window's tab list is in step before a window's own handler runs.
2. `Session`:

```csharp
        /// <summary>Windows, placement, zoom and per-tab view state, saved with <see cref="SaveSession"/>. One window until <see cref="Restore"/> loads the saved ones.</summary>
        public SessionState Session { get; private set; } = SessionWindows.Normalize(new SessionState());
```

3. Delete `public OpenNote? Active { get; private set; }` (it is now computed in `PadWorkspace.Windows.cs`) and the old parameterless `InsertIndexAfterActive()`.
4. `Restore()` becomes:

```csharp
        public IReadOnlyList<OpenNote> Restore()
        {
            if (_restored) return Open.ToList();
            _restored = true;

            Session = _store.LoadSession();   // always at least one window (SessionWindows.Normalize)

            // Notes opened before the restore (only tests do that) join the first window.
            _active.Clear();
            foreach (var note in Open)
            {
                note.IsActive = false;
                if (WindowStateOf(note.WindowId) == null) note.WindowId = Windows[0].Id;
            }
            SyncTabs();

            foreach (var window in Windows)
            {
                foreach (string id in window.NoteIds.ToList())
                {
                    if (_byId.ContainsKey(id) || _unreadable.Contains(id)) continue;
                    var meta = _store.LoadMeta(id);
                    if (meta == null)
                    {
                        _warn("The session lists note " + id + " but it could not be loaded");
                        continue;
                    }
                    if (meta.IsClosed) continue;
                    if (!TryLoadInitialText(meta, out string text))
                    {
                        // Opening it empty (or from an older snapshot) would let the next autosave
                        // overwrite the newer text that could not be read.
                        _warn("The text of note " + id + " could not be read; it was left as it is and will be tried again next time");
                        _unreadable.Add(id);
                        _unreadableWindow[id] = window.Id;
                        continue;
                    }
                    AddOpen(meta, text, Open.Count, window.Id);
                }
            }

            // A window none of whose notes came back is not reopened, unless it is the only one;
            // its unreadable notes are kept for the first window (UnreadableWindowOf).
            foreach (var empty in Windows.Where(w => TabsOf(w.Id).Count == 0).ToList())
            {
                if (Windows.Count == 1) break;
                Session.Windows!.Remove(empty);
                _tabs.Remove(empty.Id);
            }

            foreach (var window in Windows)
            {
                var tabs = TabsOf(window.Id);
                if (tabs.Count > 0) SetActive(tabs.FirstOrDefault(n => n.Id == window.ActiveNoteId) ?? tabs[tabs.Count - 1]);
            }
            _activation.Clear();
            _activation.AddRange(SessionWindows.ByLastActive(Windows));
            return Open.ToList();
        }
```

5. `MoveTab`:

```csharp
        /// <summary>
        /// Moves a tab to <paramref name="index"/> among its own window's tabs (clamped); other
        /// windows' tabs keep their places. The caller saves the session when the drag ends.
        /// </summary>
        public void MoveTab(OpenNote note, int index)
        {
            var tabs = TabsOf(note.WindowId);
            int from = tabs.IndexOf(note);
            if (from < 0) return;
            int to = Math.Clamp(index, 0, tabs.Count - 1);
            if (to == from) return;
            // Moving onto the Open position of the tab now at "to" lands it before that tab when
            // moving left and after it when moving right: exactly "to" among this window's tabs.
            Open.Move(Open.IndexOf(note), Open.IndexOf(tabs[to]));
        }
```

6. `NewNote`:

```csharp
        /// <summary>A new empty scratch note in the most recently active window.</summary>
        public OpenNote NewNote() => NewNote(MostRecentWindowId);

        /// <summary>A new empty scratch note, opened after the window's active tab and made active there.</summary>
        public OpenNote NewNote(string windowId)
        {
            if (WindowStateOf(windowId) == null) windowId = MostRecentWindowId;
            int number = Open
                .Where(n => !n.Meta.IsFileBacked)
                .Select(n => n.Meta.UntitledNumber)
                .DefaultIfEmpty(0)
                .Max() + 1;

            var note = AddOpen(NoteStore.NewMeta(_clock(), number, null), "", InsertIndexAfterActive(windowId), windowId);
            SetActive(note);
            EnqueueSave(note);
            SaveSession();
            return note;
        }
```

7. `SetActive`:

```csharp
        /// <summary>Makes <paramref name="note"/> its window's shown tab and remembers it for the next launch.</summary>
        public void SetActive(OpenNote note)
        {
            if (_active.TryGetValue(note.WindowId, out var previous) && !ReferenceEquals(previous, note)) previous.IsActive = false;
            _active[note.WindowId] = note;
            note.IsActive = true;
            if (WindowStateOf(note.WindowId) is { } state) state.ActiveNoteId = note.Id;
        }
```

8. `Close` — the neighbour that takes over is in the same window:

```csharp
        /// <summary>
        /// Closes a tab without asking anything. The note is snapshotted and kept as a closed note;
        /// a scratch note that never held text is deleted instead. Its window's active tab falls to
        /// the neighbour in that window.
        /// </summary>
        public void Close(OpenNote note)
        {
            if (!_byId.ContainsKey(note.Id)) return;

            string windowId = note.WindowId;
            int index = TabsOf(windowId).IndexOf(note);
            _scheduler.Forget(note.Id);

            if (!note.Meta.IsFileBacked && !note.EverHadText)
            {
                string id = note.Id;
                // Same key as its saves, so it replaces any still waiting and runs after one in progress.
                _unconfirmed.TryRemove(id, out _);   // being deleted, not saved
                _writer.Enqueue(id, Logged(id + "#delete", "Removing empty note " + id + " failed; it will be retried",
                    () => _store.DeleteEmptyNote(id)));
            }
            else
            {
                SnapshotNow(note, SnapshotReason.Closing);
                note.Meta.ClosedAtUtc = _clock();
                EnqueueSave(note);
                _recentlyClosed[note.Id] = note.Meta.Clone();
            }

            _byId.Remove(note.Id);
            Open.Remove(note);
            Session.Tabs.Remove(note.Id);
            note.IsActive = false;

            if (ReferenceEquals(ActiveIn(windowId), note))
            {
                _active.Remove(windowId);
                var tabs = TabsOf(windowId);
                if (tabs.Count > 0) SetActive(tabs[Math.Min(index, tabs.Count - 1)]);
                else if (WindowStateOf(windowId) is { } state) state.ActiveNoteId = null;
            }
            SaveSession();
        }
```

9. `ReopenLastClosed` and `Reopen`:

```csharp
        /// <summary>Ctrl+Shift+T, into the most recently active window.</summary>
        public OpenNote? ReopenLastClosed() => ReopenLastClosed(MostRecentWindowId);

        /// <summary>Ctrl+Shift+T in a window: the last closed note comes back there.</summary>
        public OpenNote? ReopenLastClosed(string windowId)
        {
            var last = ClosedNotes().FirstOrDefault();
            return last == null ? null : Reopen(last.Id, windowId);
        }

        /// <summary>Reopens a closed note in the most recently active window; see <see cref="Reopen(string, string)"/>.</summary>
        public OpenNote? Reopen(string id) => Reopen(id, MostRecentWindowId);

        /// <summary>
        /// Reopens a closed note after the active tab of <paramref name="windowId"/>; an open one is
        /// just activated, in the window that shows it. Null when the note cannot be loaded,
        /// including when its text exists but cannot be read right now: then nothing is written and
        /// the note stays in the closed list. A file note whose file another tab already holds comes
        /// back as a note of its own.
        /// </summary>
        public OpenNote? Reopen(string id, string windowId)
        {
            if (_byId.TryGetValue(id, out var open))
            {
                SetActive(open);
                return open;
            }
            if (WindowStateOf(windowId) == null) windowId = MostRecentWindowId;

            // Its close may still be queued; normally this lets it land first.
            _writer.FlushAll(TimeSpan.FromSeconds(2));

            NoteMeta? meta;
            string text;
            if (_unconfirmed.TryGetValue(id, out var pending))
            {
                // The writer has not caught up: the newest state is the one still queued, not the
                // one on disk. Reopening from disk here would bring back older text.
                meta = pending.Meta.Clone();
                if (pending.Text != null) text = pending.Text;
                else if (!TryLoadInitialText(meta, out text)) return UnreadableOnReopen(id);
            }
            else
            {
                meta = _store.LoadMeta(id);
                if (meta == null) return null;
                // Reopening writes the text straight back, so text that cannot be read must stop it here.
                if (!TryLoadInitialText(meta, out text)) return UnreadableOnReopen(id);
            }

            if (meta.IsFileBacked && Open.Any(n => SamePath(n.Meta.SourcePath, meta.SourcePath!)))
            {
                // Another tab holds this file now (a Save As onto it while this note was closed).
                // Two tabs saving one file would overwrite each other, so this note comes back on
                // its own, under the file's name, keeping its text and history.
                meta.TitleIsCustom = true;
                meta.SourcePath = null;
                meta.SourceStamp = null;
                meta.HasUnsavedEdits = false;
            }

            _recentlyClosed.Remove(id);
            meta.ClosedAtUtc = null;

            // Written now, not queued: the daily purge re-reads closedAt on disk under the note lock,
            // so a reopened note must stop looking closed at once.
            try
            {
                bool writeText = !meta.IsFileBacked || meta.HasUnsavedEdits;
                _store.SaveNote(meta.Clone(), writeText ? text : null, _store.NextVersion());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _error("Reopening note " + id + " could not be written yet; autosave will retry", ex);
            }

            var note = AddOpen(meta, text, InsertIndexAfterActive(windowId), windowId);
            SetActive(note);
            EnqueueSave(note);
            SaveSession();
            return note;
        }
```

10. `PrepareSession`:

```csharp
        private SessionState PrepareSession()
        {
            foreach (var window in Windows)
            {
                window.NoteIds = TabsOf(window.Id).Select(n => n.Id)
                    // Notes that could not be read at restore go last in their window, so they return at the next launch.
                    .Concat(_unreadable.Where(id => !_byId.ContainsKey(id) && UnreadableWindowOf(id) == window.Id))
                    .ToList();
                window.ActiveNoteId = ActiveIn(window.Id)?.Id;
            }
            SessionWindows.WriteMirror(Session);      // the old fields, for older MicaPads
            foreach (string stale in Session.Tabs.Keys.Where(k => !_byId.ContainsKey(k)).ToList())
                Session.Tabs.Remove(stale);
            return Session;
        }
```

11. `AddOpen`:

```csharp
        private OpenNote AddOpen(NoteMeta meta, string text, int index, string windowId)
        {
            var note = new OpenNote(meta, text)
            {
                SnapshotClockUtc = _clock(),
                EverHadText = text.Length > 0 || meta.LastSnapshotHash != null,
                WindowId = windowId,                  // before the insert: the tab lists sync on it
            };
            _byId[meta.Id] = note;
            Open.Insert(Math.Clamp(index, 0, Open.Count), note);
            return note;
        }
```

- [ ] **Step 10: `Services/Pad/PadWorkspace.Files.cs`**

1. `OpenFile` — the existing method becomes the two below:

```csharp
        /// <summary>Opens a file in the most recently active window; see <see cref="OpenFile(string, string)"/>.</summary>
        public OpenFileResult OpenFile(string path) => OpenFile(path, MostRecentWindowId);

        /// <summary>
        /// Opens a file as a tab of <paramref name="windowId"/>. A file already open in any window
        /// switches to its tab there (the window layer brings that window forward); a file with a
        /// closed note, or with a note skipped at <see cref="Restore"/>, reopens that note in
        /// <paramref name="windowId"/>, so its history and any unsaved edits continue.
        /// </summary>
        public OpenFileResult OpenFile(string path, string windowId)
        {
            if (WindowStateOf(windowId) == null) windowId = MostRecentWindowId;
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return new OpenFileResult(OpenFileStatus.NotFound, null);
            }

            var open = Open.FirstOrDefault(n => SamePath(n.Meta.SourcePath, full));
            if (open != null)
            {
                SetActive(open);
                return new OpenFileResult(OpenFileStatus.AlreadyOpen, open);
            }

            var skipped = OpenSkippedNote(full, windowId);
            if (skipped != null) return skipped;

            var closed = ClosedNotes().FirstOrDefault(m => SamePath(m.SourcePath, full));
            if (closed != null)
            {
                bool lossy = false;
                if (!closed.HasUnsavedEdits)
                {
                    // The file itself is what will be shown: check it exactly as a first open would.
                    var probe = ReadSource(full, out var probeStatus);
                    if (probe == null) return new OpenFileResult(probeStatus, null);
                    lossy = !probe.Lossless;
                }

                var reopened = Reopen(closed.Id, windowId);
                if (reopened != null) return new OpenFileResult(OpenFileStatus.Opened, reopened, lossy);
                // Its unsaved edits are in text that cannot be read right now (Reopen logged why).
                // A fresh note beside it would be a second note for this file.
                if (closed.HasUnsavedEdits) return new OpenFileResult(OpenFileStatus.ClosedNoteUnreadable, null);
            }

            var stamp = SourceStamp.Read(full);
            var decoded = ReadSource(full, out var status);
            if (decoded == null) return new OpenFileResult(status, null);

            var meta = NoteStore.NewMeta(_clock(), 0, full);
            meta.Encoding = decoded.Encoding;
            meta.CodePage = decoded.CodePage;
            meta.LineEnding = decoded.LineEnding;
            meta.SourceStamp = stamp;

            var note = AddOpen(meta, decoded.Text, InsertIndexAfterActive(windowId), windowId);
            SetActive(note);
            EnqueueSave(note);
            SaveSession();
            return new OpenFileResult(OpenFileStatus.Opened, note, Lossy: !decoded.Lossless);
        }
```

2. `OpenSkippedNote` opens into the given window (its summary unchanged):

```csharp
        private OpenFileResult? OpenSkippedNote(string full, string windowId)
        {
            foreach (string id in _unreadable.ToList())
            {
                var meta = _store.LoadMeta(id);
                if (meta == null || !SamePath(meta.SourcePath, full)) continue;

                if (!TryLoadInitialText(meta, out string text))
                    return new OpenFileResult(OpenFileStatus.ClosedNoteUnreadable, null);

                _unreadable.Remove(id);
                _unreadableWindow.Remove(id);
                var note = AddOpen(meta, text, InsertIndexAfterActive(windowId), windowId);
                SetActive(note);
                SaveSession();
                return new OpenFileResult(OpenFileStatus.Opened, note);
            }
            return null;
        }
```

- [ ] **Step 11: Run the focused tests, then the whole suite** — expected PASS. Existing tests that pin the single-window behaviour: `PadWorkspaceTests.Closing_the_active_tab_activates_its_neighbour`, `Restore_brings_back_the_open_notes_their_text_and_the_active_tab`, `A_note_whose_text_cannot_be_read_is_skipped_at_restore_kept_in_the_session_and_left_untouched` (the mirrored `OpenNoteIds`), `PadStoreTests.The_session_round_trips`, `A_corrupt_session_is_rebuilt_from_open_notes_by_modified_time`, `PadTabDragTests.*` (`Session.OpenNoteIds` is the mirror), `PadWorkspaceFileTests.Opening_the_file_of_a_note_skipped_at_restore_brings_that_note_back_rather_than_a_second_one`.

- [ ] **Step 12: Commit**

```bash
git add Services/Pad/NoteMeta.cs Services/Pad/SessionWindows.cs Services/Pad/WindowTabs.cs Services/Pad/OpenNote.cs Services/Pad/NoteStore.cs Services/Pad/PadWorkspace.cs Services/Pad/PadWorkspace.Files.cs Services/Pad/PadWorkspace.Windows.cs tests/Kil0bitSystemMonitor.Tests/SessionWindowsTests.cs tests/Kil0bitSystemMonitor.Tests/PadWorkspaceWindowsTests.cs
git commit -m "feat(pad): session windows - old sessions load as one window, every tab mirrored for older MicaPads" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

### Task 4: One MicaPad window per session window

`MicaPadWindow` shows one window of the session: its own tabs, placement, zoom and always-on-top, and documents for its own tabs only. No new UI yet (Task 5 adds new and closed windows): existing single-window behaviour is unchanged, and two windows can already be built over one workspace in tests.

**Files:**
- Modify: `Pad/MicaPadWindow.xaml.cs`
- Modify: `tests/Kil0bitSystemMonitor.Tests/PadFullScreenTests.cs` (the saved placement is the window's)
- Create: `tests/Kil0bitSystemMonitor.Tests/PadWindowTabsTests.cs`

**Interfaces:**
- Consumes (Task 3): `PadWorkspace.Windows`, `WindowStateOf`, `TabsOf`, `ActiveIn`, `ActivateWindow`, `NewWindow`, `MoveToWindow`, `NewNote(string)`, `OpenFile(string, string)`, `Reopen(string, string)`, `ReopenLastClosed(string)`, `MoveTab` (index within the window); `PadWindowState`; `BookmarkController.Lines/Load/Forget`; `EditorLanguage.Apply`.
- Produces: `public MicaPadWindow(PadWorkspace workspace, AppConfig config, string? windowId)` (the two-argument constructor stays and means the first window); `internal string WindowId`; `internal TextDocument? ReleaseDocument(OpenNote note)`; `internal void AdoptDocument(OpenNote note, TextDocument document)`. `PrepareForExit` records the window's `Open` from its own hidden flag.

- [ ] **Step 1: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/PadWindowTabsTests.cs`:

```csharp
using System;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Two MicaPad windows over one workspace: each shows, edits and saves only its own tabs.</summary>
    public class PadWindowTabsTests
    {
        /// <summary>
        /// A window over the workspace's first window, and a second one over a window made in the
        /// model with one note (placed by <paramref name="place"/> first). Never shown; both closed at the end.
        /// </summary>
        private static void WithTwoWindows(Action<MicaPadWindow, MicaPadWindow, PadTestEnv> test, Action<PadWindowState>? place = null) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var config = new AppConfig();
            var first = new MicaPadWindow(env.Workspace, config);
            MicaPadWindow? second = null;
            try
            {
                first.LoadSession();
                var state = env.Workspace.NewWindow(first.WindowId);
                place?.Invoke(state);
                env.Workspace.NewNote(state.Id);
                second = new MicaPadWindow(env.Workspace, config, state.Id);
                second.LoadSession();
                test(first, second, env);
            }
            finally
            {
                second?.CloseForExit();
                first.CloseForExit();
            }
        });

        [Fact]
        public void Each_window_shows_only_its_own_tabs() => WithTwoWindows((first, second, env) =>
        {
            first.NewTab();

            Assert.Same(env.Workspace.TabsOf(first.WindowId), first.TabStrip.ItemsSource);
            Assert.Equal(2, first.TabStrip.Items.Count);
            Assert.Single(second.TabStrip.Items);
            Assert.Equal(3, env.Workspace.Open.Count);
            Assert.NotSame(first.Editor.Document, second.Editor.Document);
        });

        [Fact]
        public void A_new_tab_in_one_window_is_saved_from_that_windows_editor() => WithTwoWindows((first, second, env) =>
        {
            // Both windows hear every change to the open notes; only the tab's own window may build
            // its document, or the other window would take over what the tab saves.
            first.NewTab();
            first.Editor.Document.Insert(0, "typed in the first window");
            second.NewTab();
            second.Editor.Document.Insert(0, "typed in the second window");

            Assert.Equal("typed in the first window", env.Workspace.ActiveIn(first.WindowId)!.TextProvider());
            Assert.Equal("typed in the second window", env.Workspace.ActiveIn(second.WindowId)!.TextProvider());
        });

        [Fact]
        public void Zoom_and_always_on_top_belong_to_each_window() => WithTwoWindows((first, second, env) =>
        {
            first.HandleShortcut(Key.OemPlus, ModifierKeys.Control);
            PadMenuTests.Click(PadMenuTests.ItemOf(second.BuildMainMenu(), "Always on top"));

            Assert.Equal(14 * 1.1, first.Editor.FontSize, 3);
            Assert.Equal(14, second.Editor.FontSize, 3);
            Assert.False(first.Topmost);
            Assert.True(second.Topmost);
            Assert.Equal(1.1, env.Workspace.WindowStateOf(first.WindowId)!.Zoom, 3);
            Assert.Equal(1.0, env.Workspace.WindowStateOf(second.WindowId)!.Zoom, 3);
            Assert.False(env.Workspace.WindowStateOf(first.WindowId)!.AlwaysOnTop);
            Assert.True(env.Workspace.WindowStateOf(second.WindowId)!.AlwaysOnTop);
        });

        [Fact]
        public void Each_window_opens_at_its_own_size() => WithTwoWindows((first, second, env) =>
        {
            Assert.Equal(640, second.Width);
            Assert.Equal(480, second.Height);
            Assert.Equal(900, first.Width);
        }, place: state =>
        {
            state.Width = 640;
            state.Height = 480;
        });

        [Fact]
        public void Tab_keys_walk_this_windows_tabs_only() => WithTwoWindows((first, second, env) =>
        {
            first.Editor.Document.Insert(0, "one");
            first.NewTab();
            first.Editor.Document.Insert(0, "two");

            first.SelectTab(0);
            Assert.Equal("one", first.Editor.Document.Text);
            Assert.True(first.HandleShortcut(Key.Tab, ModifierKeys.Control));
            Assert.Equal("two", first.Editor.Document.Text);
            Assert.True(first.HandleShortcut(Key.Tab, ModifierKeys.Control));
            Assert.Equal("one", first.Editor.Document.Text);          // wrapped within this window
            first.SelectTab(2);                                         // past this window's tabs: ignored
            Assert.Equal("one", first.Editor.Document.Text);
        });

        [Fact]
        public void Closing_a_windows_last_tab_leaves_a_fresh_note_in_that_window() => WithTwoWindows((first, second, env) =>
        {
            var only = env.Workspace.ActiveIn(second.WindowId)!;
            var firstTabs = env.Workspace.TabsOf(first.WindowId).ToList();

            Assert.True(second.HandleShortcut(Key.W, ModifierKeys.Control));

            var fresh = Assert.Single(env.Workspace.TabsOf(second.WindowId));
            Assert.NotSame(only, fresh);
            Assert.Equal(firstTabs, env.Workspace.TabsOf(first.WindowId));
        });

        [Fact]
        public void A_document_handed_to_another_window_keeps_its_undo_and_bookmarks() => WithTwoWindows((first, second, env) =>
        {
            var note = env.Workspace.ActiveIn(first.WindowId)!;
            var document = first.Editor.Document;
            document.Insert(0, "a\nb\nc");
            first.Editor.CaretOffset = document.GetLineByNumber(2).Offset;
            first.ToggleBookmark();
            first.NewTab();                                             // the note is no longer the one shown

            var released = first.ReleaseDocument(note);
            env.Workspace.MoveToWindow(note, second.WindowId);
            second.AdoptDocument(note, released!);
            second.SelectTab(env.Workspace.TabsOf(second.WindowId).IndexOf(note));

            Assert.Same(document, released);
            Assert.Same(document, second.Editor.Document);
            Assert.True(second.Editor.CanUndo);
            Assert.Equal(new[] { 2 }, second.BookmarkLines);
            second.Editor.Document.Insert(0, "typed ");
            Assert.StartsWith("typed a", note.TextProvider());
            Assert.True(env.Workspace.HasPendingChanges(note));        // the second window's edits reach the workspace
        });
    }
}
```

In `tests/Kil0bitSystemMonitor.Tests/PadFullScreenTests.cs`, every `env.Workspace.Session.Maximized` becomes `env.Workspace.Windows[0].Maximized` — today three asserts: `The_saved_placement_is_the_one_before_full_screen` (`Assert.False(env.Workspace.Windows[0].Maximized);`), `Entering_from_maximized_saves_maximized` (`Assert.True(...)`, which fails otherwise: `PrepareForExit` records the window's state, and the old top-level field is only its mirror, written at the next save) and `Closing_hides_the_window_windowed` (`Assert.False(...)`). Its `Assert.False(env.Workspace.Session.WindowOpen);` stays: the hide path saves the session, which writes the mirror.

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~PadWindowTabsTests"`). Expected: build FAILS (no three-argument constructor, no `WindowId`, `ReleaseDocument`, `AdoptDocument`).

- [ ] **Step 3: Bind the window to its session window** — in `Pad/MicaPadWindow.xaml.cs`:

1. Fields, after `_config`:

```csharp
        /// <summary>The session window this window shows, as asked for at construction; null means the first.</summary>
        private readonly string? _requestedWindowId;

        /// <summary>This window's id in the session (spec 5.3); set by <see cref="LoadSession"/>.</summary>
        private string _windowId = "";

        /// <summary>This window's placement, zoom and on-top state in the session; set by <see cref="LoadSession"/>.</summary>
        private PadWindowState? _state;

        /// <summary>True while the close button has hidden this window (it was the last one).</summary>
        private bool _hidden;

        /// <summary>Each document's change handler, so a document can be handed to another window.</summary>
        private readonly Dictionary<TextDocument, EventHandler<DocumentChangeEventArgs>> _docHandlers = new();

        /// <summary>This window's id in the session; for tests and the window registry.</summary>
        internal string WindowId => _windowId;
```

2. Constructors: the existing one becomes

```csharp
        /// <summary>
        /// Builds the window for one of the workspace's windows (spec 5.3); null means the first.
        /// Call <see cref="LoadSession"/> before showing it.
        /// </summary>
        public MicaPadWindow(PadWorkspace workspace, AppConfig config, string? windowId)
```

   with `_requestedWindowId = windowId;` after `_config = config;`, and above it

```csharp
        /// <summary>Builds the window over the workspace's first window; call <see cref="LoadSession"/> before showing it.</summary>
        public MicaPadWindow(PadWorkspace workspace, AppConfig config) : this(workspace, config, null)
        {
        }
```

   In its body: delete `TabStrip.ItemsSource = _workspace.Open;` (LoadSession binds the window's own tabs); the drag controller reads this window's tabs — in the `new TabDragController(...)` call only the tab-list argument changes, `() => _workspace.Open` becoming `() => _workspace.TabsOf(_windowId)` (a `WindowTabs` is an `IReadOnlyList<OpenNote>`, the parameter's type); keep every other argument as the file has it. With today's constructor the call reads:

```csharp
            _tabDrag = new TabDragController(TabStrip, TabScroller, () => _workspace.TabsOf(_windowId),
                (note, index) => _workspace.MoveTab(note, index),
                () => _workspace.SaveSession());
```

   and activation records the window as the most recently active:

```csharp
            Activated += (s, e) =>
            {
                if (_windowId.Length > 0) _workspace.ActivateWindow(_windowId);
                CheckShownNoteOnDisk();
            };
```

3. `LoadSession`:

```csharp
        /// <summary>
        /// Binds the window to its session window, builds the documents of its tabs and shows its
        /// active tab. Called once, before the window is first shown. The workspace restore is
        /// idempotent, so every window finds the same notes.
        /// </summary>
        public void LoadSession()
        {
            _workspace.Restore();
            _windowId = _requestedWindowId ?? _workspace.Windows[0].Id;
            _state = _workspace.WindowStateOf(_windowId)
                     ?? throw new InvalidOperationException("MicaPad has no window " + _windowId);
            var tabs = _workspace.TabsOf(_windowId);
            TabStrip.ItemsSource = tabs;
            if (tabs.Count == 0) _workspace.NewNote(_windowId);
            foreach (var note in tabs.ToList()) EnsureDocument(note);

            Topmost = _state.AlwaysOnTop;
            ApplyPlacement();
            ApplyEditorSettings();
            ShowNote(_workspace.ActiveIn(_windowId) ?? tabs[0]);
            _tick.Start();
        }
```

4. `ShowOrActivate`: replace `workspace.Session.WindowOpen = true;` with

```csharp
            window._hidden = false;
            window._state!.Open = true;
```

5. `PrepareForExit`: replace `_workspace.Session.WindowOpen = IsVisible;` with `if (_state != null) _state.Open = !_hidden;` (a window hidden by its close button reopens hidden; any other was showing).

6. `OnClosing` (the hide path): replace `_workspace.Session.WindowOpen = false;` with

```csharp
                _hidden = true;
                if (_state != null) _state.Open = false;
```

   The lines around it stay as they are: `CaptureViewState();` before and the fix wave's `if (IsFullScreen) ToggleFullScreen();` (MicaPad comes back windowed), then `FlushPending`, `SaveSession` and `Hide()`.

7. Tabs are this window's tabs:

```csharp
        /// <summary>A new scratch note in a new tab of this window.</summary>
        internal void NewTab() => ShowNote(_workspace.NewNote(_windowId));

        /// <summary>Closes a tab without asking. Closing this window's last one leaves a fresh empty note here.</summary>
        internal void CloseTab(OpenNote note)
        {
            bool wasShown = ReferenceEquals(_shown, note);
            if (wasShown) _shown = null;

            _workspace.Close(note);
            var tabs = _workspace.TabsOf(_windowId);
            if (tabs.Count == 0) _workspace.NewNote(_windowId);
            if (wasShown || _shown == null) ShowNote(_workspace.ActiveIn(_windowId) ?? tabs[0]);
        }

        /// <summary>Shows this window's tab at a zero-based position; an out-of-range index is ignored.</summary>
        internal void SelectTab(int index)
        {
            var tabs = _workspace.TabsOf(_windowId);
            if (index >= 0 && index < tabs.Count) ShowNote(tabs[index]);
        }
```

```csharp
        private void CycleTab(int delta)
        {
            var tabs = _workspace.TabsOf(_windowId);
            int count = tabs.Count;
            if (count < 2 || _shown == null) return;
            int index = tabs.IndexOf(_shown);
            ShowNote(tabs[((index + delta) % count + count) % count]);
        }
```

   `CloseOtherTabs`: `foreach (var other in _workspace.TabsOf(_windowId).ToList())`. `BuildTabMenu`: *Close other tabs* is enabled when `_workspace.TabsOf(_windowId).Count > 1`. `OpenPath`: `_workspace.OpenFile(path, _windowId)`. `ReopenClosed`: `_workspace.ReopenLastClosed(_windowId)`. `OnClosedReopenClick`: `_workspace.Reopen(row.Id, _windowId)`.

8. `ShowNote`, first line: `if (note.WindowId != _windowId) return;   // another window's tab (Task 6 brings that window forward)`.

9. Placement, on-top and zoom are the window's (`_state`), not the session's top-level fields. The methods below are today's with `_workspace.Session` replaced by the window's state (`_state`, named `place` inside); if the Part 4 fix wave has changed one of them since, keep its logic and make only that replacement:

```csharp
        private void ToggleTopmost()
        {
            Topmost = !Topmost;
            if (_state != null) _state.AlwaysOnTop = Topmost;
            _workspace.SaveSession();
        }

        private void Zoom(double delta) => SetZoom((_state?.Zoom ?? 1.0) + delta);

        private void SetZoom(double zoom)
        {
            if (_state == null) return;
            _state.Zoom = Math.Clamp(Math.Round(zoom, 2), 0.5, 4.0);
            ApplyEditorSettings();
        }
```

   `ApplyEditorSettings`: `Editor.FontSize = _config.PadFontSize * (_state?.Zoom ?? 1.0);` (a config change before `LoadSession` uses zoom 1). `ApplyPlacement`:

```csharp
        private void ApplyPlacement()
        {
            var place = _state!;
            Width = Math.Max(MinWidth, place.Width);
            Height = Math.Max(MinHeight, place.Height);

            if (place.Left is double left && place.Top is double top &&
                PadPlacement.IsReachable(left, top, Width, Height,
                    SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }

            if (place.Maximized) WindowState = WindowState.Maximized;
        }
```

   `CaptureViewState`:

```csharp
        /// <summary>Records the shown tab's caret and scroll, and this window's placement.</summary>
        private void CaptureViewState()
        {
            if (_shown != null) SaveViewState(_shown);
            if (_state is not { } place) return;

            place.AlwaysOnTop = Topmost;
            // Full screen is never saved: the placement from before it is what the session keeps.
            if (_beforeFullScreen is { } fs) place.Maximized = fs.State == WindowState.Maximized;
            if (!IsLoaded) return;

            if (_beforeFullScreen == null) place.Maximized = WindowState == WindowState.Maximized;
            Rect bounds = _beforeFullScreen is { } saved ? saved.Bounds
                : WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            if (!bounds.IsEmpty && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top))
            {
                place.Left = bounds.Left;
                place.Top = bounds.Top;
                place.Width = bounds.Width;
                place.Height = bounds.Height;
            }
        }
```

- [ ] **Step 4: Documents belong to their window** — replace `EnsureDocument` with:

```csharp
        private TextDocument EnsureDocument(OpenNote note)
        {
            if (_docs.TryGetValue(note.Id, out var existing)) return existing;

            var document = new TextDocument(note.TextProvider());
            document.UndoStack.ClearAll();
            Attach(note, document);
            return document;
        }

        /// <summary>
        /// Makes <paramref name="document"/> this window's document of <paramref name="note"/>: its
        /// edits reach the workspace, the note's text is read from it (autosave, snapshots), and its
        /// saved bookmarks come back.
        /// </summary>
        private void Attach(OpenNote note, TextDocument document)
        {
            EventHandler<DocumentChangeEventArgs> changed = (s, e) =>
            {
                _workspace.NotifyChanged(note, markUnsaved: !_suppressDirty);
                if (ReferenceEquals(_shown, note)) UpdateCharsText();
                // A big paste crosses the 2 MB limit: formatting switches off (and back on) once the
                // change is done. Never inside it: AvalonEdit still calls the document's other
                // handlers for this change, among them the formatting a re-apply would tear down.
                if (ReferenceEquals(_shown, note) && CrossesSizeLimit(document))
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (ReferenceEquals(_shown, note) && ReferenceEquals(Editor.Document, document) && CrossesSizeLimit(document))
                            ApplyLanguage();
                    }));
                }
            };
            document.Changed += changed;
            _docHandlers[document] = changed;
            note.TextProvider = () => document.Text;
            _docs[note.Id] = document;
            if (_workspace.Session.Tabs.TryGetValue(note.Id, out var view) && view.Bookmarks != null)
                _bookmarks.Load(document, view.Bookmarks);
        }

        /// <summary>
        /// Lets go of a note's document because its tab is moving to another window: its bookmarks
        /// go to the session, and its change handler and bookmark anchors come off. Returns the
        /// document, undo history and all, for <see cref="AdoptDocument"/>; null when this window
        /// never built one.
        /// </summary>
        internal TextDocument? ReleaseDocument(OpenNote note)
        {
            if (!_docs.TryGetValue(note.Id, out var document)) return null;
            if (ReferenceEquals(_shown, note)) LetGoOfShown();
            _workspace.SetBookmarks(note, _bookmarks.Lines(document));
            _bookmarks.Forget(document);
            if (_docHandlers.Remove(document, out var changed)) document.Changed -= changed;
            _docs.Remove(note.Id);
            return document;
        }

        /// <summary>Takes over a document another window released, undo history and all.</summary>
        internal void AdoptDocument(OpenNote note, TextDocument document)
        {
            if (!_docs.ContainsKey(note.Id)) Attach(note, document);
        }

        /// <summary>
        /// The editor stops showing any note (its tab is leaving this window): caret and scroll are
        /// saved, and formatting and folding come off that document first, so nothing here keeps
        /// listening to it.
        /// </summary>
        private void LetGoOfShown()
        {
            if (_shown != null) SaveViewState(_shown);
            _shown = null;
            _language.Apply(PadLanguages.Plain);
            Editor.Document = new TextDocument();
        }
```

   and `OnOpenChanged` builds documents for this window's new tabs only, and unhooks closed ones:

```csharp
        private void OnOpenChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Move) return;   // a moved tab keeps its document, bookmarks and folds
            if (e.NewItems != null)
                foreach (OpenNote note in e.NewItems)
                    if (note.WindowId == _windowId) EnsureDocument(note);   // never another window's: its TextProvider is that window's
            if (e.OldItems != null)
                foreach (OpenNote note in e.OldItems)
                {
                    if (_docs.TryGetValue(note.Id, out var gone))
                    {
                        _bookmarks.Forget(gone);
                        if (_docHandlers.Remove(gone, out var changed)) gone.Changed -= changed;
                    }
                    _docs.Remove(note.Id);
                }
        }
```

(Before this task every window built a document for every new note and pointed the note's `TextProvider` at it: with two windows the last one to hear of a note would own what it saves, whichever window the user types in. `A_new_tab_in_one_window_is_saved_from_that_windows_editor` pins that.)

- [ ] **Step 5: Run the focused tests, then the whole suite** — expected PASS. `PadBookmarkTests` builds two windows over the same (first) window, as a restart would; both still build documents for every tab of that window, as before.

- [ ] **Step 6: Commit**

```bash
git add Pad/MicaPadWindow.xaml.cs tests/Kil0bitSystemMonitor.Tests/PadWindowTabsTests.cs tests/Kil0bitSystemMonitor.Tests/PadFullScreenTests.cs
git commit -m "feat(pad): each MicaPad window shows and saves only its own tabs, with its own place and zoom" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

### Task 5: New window, closing a window, exit

A registry of loaded windows replaces the single `s_current`; `Ctrl+Shift+N` and `☰ → New window` open a window; `×` merges a window into the most recently active other one (or hides the last); exit and session end capture every window; the first show brings every window back.

**Files:**
- Modify: `Pad/MicaPadWindow.xaml.cs`, `App.xaml.cs` (`FlushPad`, `Quit`), `GUIDE.md`, `README.md`
- Create: `tests/Kil0bitSystemMonitor.Tests/PadWindowsTests.cs`

**Interfaces:**
- Consumes (Tasks 3–4): `PadWorkspace.NewWindow(string?)`, `NewNote(string)`, `CloseWindow(string, string?)`, `ActivateWindow`, `ActivationOrder`, `MostRecentWindowId`, `WindowStateOf`, `TabsOf`, `ActiveIn`; `MicaPadWindow(workspace, config, windowId)`, `WindowId`, `ReleaseDocument`, `AdoptDocument`, `_hidden`, `_state`; `EditorMenus.Guard(string, Action)`.
- Produces: `internal static Action<MicaPadWindow> ShowWindow { get; set; }` (the one place a window is shown; tests replace it); `internal static IReadOnlyList<MicaPadWindow> WindowsOf(PadWorkspace)`; `public static MicaPadWindow? Current` (the most recently active loaded window) and `internal static MicaPadWindow? CurrentOf(PadWorkspace)` (tests ask per workspace); `public static MicaPadWindow ShowOrActivate(PadWorkspace, AppConfig, Action?)` (first show brings back every window); `public static void PrepareAllForExit(PadWorkspace? only = null)` (the app passes nothing; tests their own workspace); `internal MicaPadWindow NewWindow()`; `internal void CloseByUser()`; `internal bool IsHiddenByClose`; `CloseForExit()` is safe to call twice. Registration happens at the end of `LoadSession`; `Detach` unregisters.

- [ ] **Step 1: Write the failing tests**

`tests/Kil0bitSystemMonitor.Tests/PadWindowsTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using MenuItem = System.Windows.Controls.MenuItem;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>More than one MicaPad window: opening, closing, exiting, coming back, moving tabs and routing files.</summary>
    public class PadWindowsTests
    {
        /// <summary>
        /// A window over a fresh workspace. Showing is replaced — tests never show windows — and the
        /// windows MicaPad would have shown are listed in order. Every window loaded over the
        /// workspace is closed at the end.
        /// </summary>
        internal static void WithWindows(Action<MicaPadWindow, PadTestEnv, List<MicaPadWindow>> test) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var shown = new List<MicaPadWindow>();
            var previous = MicaPadWindow.ShowWindow;
            MicaPadWindow.ShowWindow = shown.Add;
            try
            {
                var window = new MicaPadWindow(env.Workspace, new AppConfig());
                window.LoadSession();
                test(window, env, shown);
            }
            finally
            {
                MicaPadWindow.ShowWindow = previous;
                foreach (var open in MicaPadWindow.WindowsOf(env.Workspace).ToList()) open.CloseForExit();
            }
        });

        [Fact]
        public void Ctrl_shift_n_opens_a_second_window_with_one_new_note() => WithWindows((first, env, shown) =>
        {
            var firstTabs = env.Workspace.TabsOf(first.WindowId).ToList();

            Assert.True(first.HandleShortcut(Key.N, ModifierKeys.Control | ModifierKeys.Shift));

            var second = Assert.Single(shown);
            Assert.NotSame(first, second);
            Assert.Equal(2, env.Workspace.Windows.Count);
            Assert.Equal(2, MicaPadWindow.WindowsOf(env.Workspace).Count);
            var note = Assert.Single(env.Workspace.TabsOf(second.WindowId));
            Assert.Equal("", note.TextProvider());
            Assert.Equal(firstTabs, env.Workspace.TabsOf(first.WindowId));
            Assert.Equal(second.WindowId, env.Workspace.MostRecentWindowId);
            Assert.True(env.Workspace.WindowStateOf(second.WindowId)!.Open);
            Assert.Same(second, MicaPadWindow.CurrentOf(env.Workspace));
        });

        [Fact]
        public void The_main_menu_offers_a_new_window() => WithWindows((window, env, shown) =>
        {
            var item = PadMenuTests.ItemOf(window.BuildMainMenu(), "New window");
            Assert.Equal("Ctrl+Shift+N", item.InputGestureText);

            PadMenuTests.Click(item);

            Assert.Single(shown);
        });

        [Fact]
        public void Closing_a_window_moves_its_tabs_documents_and_undo_to_the_most_recent_window() => WithWindows((first, env, shown) =>
        {
            var second = first.NewWindow();
            second.Editor.Document.Insert(0, "draft");
            var draft = env.Workspace.ActiveIn(second.WindowId)!;
            var document = second.Editor.Document;
            var third = first.NewWindow();
            env.Workspace.ActivateWindow(first.WindowId);
            env.Workspace.ActivateWindow(second.WindowId);             // most recent first: second, first, third

            second.CloseByUser();

            Assert.DoesNotContain(second, MicaPadWindow.WindowsOf(env.Workspace));
            Assert.Equal(new[] { first.WindowId, third.WindowId }, env.Workspace.Windows.Select(w => w.Id));
            Assert.Contains(draft, env.Workspace.TabsOf(first.WindowId));
            Assert.Same(first, shown[shown.Count - 1]);                 // the window that took the tabs comes forward
            first.SelectTab(env.Workspace.TabsOf(first.WindowId).IndexOf(draft));
            Assert.Same(document, first.Editor.Document);               // the same document: its undo came along
            Assert.True(first.Editor.CanUndo);
            first.Editor.Undo();
            Assert.Equal("", first.Editor.Document.Text);
            Assert.Empty(env.Workspace.ClosedNotes());
        });

        [Fact]
        public void Closing_a_window_keeps_unsaved_file_edits() => WithWindows((first, env, shown) =>
        {
            var second = first.NewWindow();
            PadLanguageWindowTests.OpenFile(second, env, "report.txt", "on disk");
            second.Editor.Document.Insert(second.Editor.Document.TextLength, " plus mine");
            var file = env.Workspace.ActiveIn(second.WindowId)!;

            second.CloseByUser();
            Assert.True(env.Workspace.FlushAll(TimeSpan.FromSeconds(5)));

            Assert.Equal(first.WindowId, file.WindowId);
            Assert.True(file.HasUnsavedEdits);
            Assert.Equal("on disk plus mine", file.TextProvider());
            Assert.Equal("on disk plus mine", env.DiskText(file));      // MicaPad's copy has the edit
            Assert.Equal("on disk", File.ReadAllText(env.FileOf("report.txt")));   // the file itself is untouched
        });

        [Fact]
        public void Closing_the_last_window_hides_it_and_keeps_its_tabs() => WithWindows((window, env, shown) =>
        {
            window.Editor.Document.Insert(0, "stay");
            var tabs = env.Workspace.TabsOf(window.WindowId).ToList();

            window.CloseByUser();

            Assert.Contains(window, MicaPadWindow.WindowsOf(env.Workspace));
            Assert.True(window.IsHiddenByClose);
            Assert.False(env.Workspace.WindowStateOf(window.WindowId)!.Open);
            Assert.Equal(tabs, env.Workspace.TabsOf(window.WindowId));
            Assert.Equal("stay", window.Editor.Document.Text);
        });

        [Fact]
        public void A_hidden_window_comes_back_on_the_next_show() => WithWindows((window, env, shown) =>
        {
            window.CloseByUser();

            var front = MicaPadWindow.ShowOrActivate(env.Workspace, new AppConfig(), null);

            Assert.Same(window, front);
            Assert.Equal(new[] { window }, shown);
            Assert.False(window.IsHiddenByClose);
            Assert.True(env.Workspace.WindowStateOf(window.WindowId)!.Open);
        });

        [Fact]
        public void The_hotkey_brings_the_most_recently_active_window_forward() => WithWindows((first, env, shown) =>
        {
            first.NewWindow();
            env.Workspace.ActivateWindow(first.WindowId);
            shown.Clear();

            var front = MicaPadWindow.ShowOrActivate(env.Workspace, new AppConfig(), null);

            Assert.Same(first, front);
            Assert.Equal(new[] { first }, shown);
        });

        [Fact]
        public void Preparing_for_exit_keeps_every_window_in_the_session() => WithWindows((first, env, shown) =>
        {
            first.NewWindow();

            MicaPadWindow.PrepareAllForExit(env.Workspace);                  // what Quit and session end do, for this test's windows only
            foreach (var window in MicaPadWindow.WindowsOf(env.Workspace).ToList()) window.CloseForExit();   // what shutdown does next
            env.Workspace.SaveSession();
            env.Flush();

            var session = env.Store.LoadSession();
            Assert.Equal(2, session.Windows!.Count);
            Assert.All(session.Windows, w => Assert.True(w.Open));
        });

        [Fact]
        public void The_first_show_brings_back_every_window_where_it_was() => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var before = env.Workspace;
            PadTestEnv.Type(before, before.NewNote(), "one");
            var second = before.NewWindow(before.Windows[0].Id);
            PadTestEnv.Type(before, before.NewNote(second.Id), "two");
            second.Width = 640;
            second.Height = 480;
            env.Clock.Advance(1);
            before.ActivateWindow(before.Windows[0].Id);                // the first window was used last
            Assert.True(before.FlushAll(TimeSpan.FromSeconds(5)));

            var restarted = env.NewWorkspace(post: action => dispatcher.BeginInvoke(action));
            var shown = new List<MicaPadWindow>();
            var previous = MicaPadWindow.ShowWindow;
            MicaPadWindow.ShowWindow = shown.Add;
            try
            {
                var front = MicaPadWindow.ShowOrActivate(restarted, new AppConfig(), null);

                Assert.Equal(2, MicaPadWindow.WindowsOf(restarted).Count);
                Assert.Equal(restarted.Windows[0].Id, front.WindowId);   // the most recently active one is in front
                Assert.Same(front, shown[shown.Count - 1]);
                var other = MicaPadWindow.WindowsOf(restarted).Single(w => !ReferenceEquals(w, front));
                Assert.Equal(640, other.Width);
                Assert.Equal(480, other.Height);
                Assert.Equal("two", other.Editor.Document.Text);
                Assert.Equal("one", front.Editor.Document.Text);
            }
            finally
            {
                MicaPadWindow.ShowWindow = previous;
                foreach (var window in MicaPadWindow.WindowsOf(restarted).ToList()) window.CloseForExit();
            }
        });
    }
}
```

(`PadTestEnv.Type` stands in for a document, as in the workspace tests; `FlushAll` writes the texts and the session with both windows. The restarted workspace has not been restored: `ShowOrActivate` restores it. The tests ask `CurrentOf(env.Workspace)` and call `PrepareAllForExit(env.Workspace)`, never the process-wide `Current` or `PrepareAllForExit()`: in the whole suite these tests run inside other classes' tests' nested dispatcher frames, with those tests' windows registered too — `Assert.Same(second, MicaPadWindow.Current)` failed in whole-suite runs while passing alone, and a process-wide `PrepareAllForExit()` would mark another test's windows as exiting.)

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~PadWindowsTests"`). Expected: build FAILS (`ShowWindow`, `WindowsOf`, `CurrentOf`, `NewWindow`, `CloseByUser`, `PrepareAllForExit`, `IsHiddenByClose` not found).

- [ ] **Step 3: The registry, showing and new windows** — in `Pad/MicaPadWindow.xaml.cs`, delete `private static MicaPadWindow? s_current;`, the old `Current` property and the old `ShowOrActivate`, and add a region:

```csharp
        // ---- windows (spec 5.3) --------------------------------------------------------------

        /// <summary>Every loaded MicaPad window, shown or hidden, in load order. Changed on the UI thread only.</summary>
        private static readonly List<MicaPadWindow> s_windows = new();

        /// <summary>True once <see cref="CloseForExit"/> ran: a window closes once.</summary>
        private bool _closedForExit;

        /// <summary>Shows a window and brings it to the front. Tests replace it: tests never show windows.</summary>
        internal static Action<MicaPadWindow> ShowWindow { get; set; } = window =>
        {
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            window.Editor.Focus();
        };

        /// <summary>The loaded windows over <paramref name="workspace"/>, in load order.</summary>
        internal static IReadOnlyList<MicaPadWindow> WindowsOf(PadWorkspace workspace) =>
            s_windows.Where(w => ReferenceEquals(w._workspace, workspace)).ToList();

        /// <summary>The loaded window showing <paramref name="windowId"/>, or null.</summary>
        private static MicaPadWindow? Registered(PadWorkspace workspace, string windowId) =>
            s_windows.FirstOrDefault(w => ReferenceEquals(w._workspace, workspace) && w._windowId == windowId);

        /// <summary>The most recently active MicaPad window (shown or hidden), or null when none is loaded.</summary>
        public static MicaPadWindow? Current => s_windows.Count == 0 ? null : CurrentOf(s_windows[s_windows.Count - 1]._workspace);

        /// <summary>
        /// The most recently active loaded window over <paramref name="workspace"/>, or null. Tests
        /// ask per workspace: the shared UI test thread can run another test's windows in between.
        /// </summary>
        internal static MicaPadWindow? CurrentOf(PadWorkspace workspace)
        {
            foreach (string id in workspace.ActivationOrder)
                if (Registered(workspace, id) is { } window) return window;
            return WindowsOf(workspace).FirstOrDefault();
        }

        /// <summary>True while this window is hidden by its close button (it was the last one); for tests.</summary>
        internal bool IsHiddenByClose => _hidden;

        /// <summary>
        /// Shows MicaPad (the hotkey, the overlay menu, the Start menu): the most recently active
        /// window comes to the front. The first show in a run brings back every window of the
        /// session, each where it was (spec 5.3: reopen at login restores every window), so no tab
        /// can wait in a window nobody shows; the most recently active ends up in front.
        /// </summary>
        public static MicaPadWindow ShowOrActivate(PadWorkspace workspace, AppConfig config, Action? openSettings)
        {
            workspace.Restore();
            string targetId = workspace.MostRecentWindowId;
            if (WindowsOf(workspace).Count == 0)
            {
                // Least recently active first, so each later one comes up in front of it.
                foreach (string id in workspace.ActivationOrder.Reverse().Where(id => id != targetId).ToList())
                    Create(workspace, config, id, openSettings).Present();
            }
            var window = Registered(workspace, targetId) ?? Create(workspace, config, targetId, openSettings);
            window.Present();
            return window;
        }

        /// <summary>Builds and loads a window for a session window. A half-loaded one is closed, never reused.</summary>
        private static MicaPadWindow Create(PadWorkspace workspace, AppConfig config, string windowId, Action? openSettings,
                                            Action<MicaPadWindow>? beforeLoad = null)
        {
            var window = new MicaPadWindow(workspace, config, windowId) { OpenSettingsRequested = openSettings };
            try
            {
                beforeLoad?.Invoke(window);
                window.LoadSession();
            }
            catch
            {
                window.CloseForExit();
                throw;
            }
            return window;
        }

        /// <summary>Shows this window in front: it counts as open for the next login and as the most recently active.</summary>
        private void Present()
        {
            _hidden = false;
            if (_state != null) _state.Open = true;
            _workspace.ActivateWindow(_windowId);
            _workspace.SaveSession();
            ShowWindow(this);
        }

        /// <summary>This workspace's other loaded windows, most recently active first; none that is closing.</summary>
        private List<MicaPadWindow> OtherWindows()
        {
            var others = new List<MicaPadWindow>();
            foreach (string id in _workspace.ActivationOrder)
            {
                if (id == _windowId) continue;
                if (Registered(_workspace, id) is { } window && !window._exiting) others.Add(window);
            }
            return others;
        }

        /// <summary>Ctrl+Shift+N and ☰ → New window: another window with one new note (spec 5.3), a little below and right of this one.</summary>
        internal MicaPadWindow NewWindow()
        {
            CaptureViewState();                   // the cascade starts from where this window is now
            var state = _workspace.NewWindow(_windowId);
            _workspace.NewNote(state.Id);
            MicaPadWindow window;
            try
            {
                window = Create(_workspace, _config, state.Id, OpenSettingsRequested);
            }
            catch
            {
                // No window shows it: its note comes back here rather than wait unseen for the next start.
                _workspace.CloseWindow(state.Id, _windowId);
                throw;
            }
            window.Present();
            return window;
        }

        /// <summary>
        /// The close button (spec 5.3). With another MicaPad window open, this window's tabs move to
        /// the most recently active of them — documents, undo history and bookmarks with them — and
        /// this window closes for real: no note is closed. The last window hides instead, keeping its tabs.
        /// </summary>
        internal void CloseByUser()
        {
            // Posted from OnClosing: by now the window may be exiting, or closed for good (Detach).
            if (_exiting || !s_windows.Contains(this)) return;
            var target = OtherWindows().FirstOrDefault();
            if (target == null)
            {
                HideKeepingTabs();
                return;
            }
            MergeInto(target);
            CloseForExit();
            target.Present();
        }

        /// <summary>Hides the last window, keeping its tabs for the next show.</summary>
        private void HideKeepingTabs()
        {
            CaptureViewState();                    // the placement from before full screen, if any
            if (IsFullScreen) ToggleFullScreen();  // so MicaPad comes back windowed (GUIDE; Part 4 fix wave)
            _hidden = true;
            if (_state != null) _state.Open = false;
            _workspace.FlushPending();
            _workspace.SaveSession();
            if (IsVisible) Hide();
        }

        /// <summary>Hands every tab of this window to <paramref name="target"/> and takes this window out of the session.</summary>
        private void MergeInto(MicaPadWindow target)
        {
            CaptureViewState();
            _workspace.FlushPending();
            var moving = new List<(OpenNote Note, TextDocument? Document)>();
            foreach (var note in _workspace.TabsOf(_windowId).ToList()) moving.Add((note, ReleaseDocument(note)));
            _workspace.CloseWindow(_windowId, target._windowId);
            foreach (var (note, document) in moving)
                if (document != null) target.AdoptDocument(note, document);
            _workspace.SaveSession();
        }

        /// <summary>
        /// Records every window's tabs, placement and open state and lets them all close for real:
        /// before application exit or session end. After this, WPF closing them merges nothing.
        /// <paramref name="only"/> limits it to one workspace's windows (tests: the shared UI test
        /// thread can run another test's windows in between).
        /// </summary>
        public static void PrepareAllForExit(PadWorkspace? only = null)
        {
            foreach (var window in s_windows.ToList())
                if (only == null || ReferenceEquals(window._workspace, only)) window.PrepareForExit();
        }
```

- [ ] **Step 4: Loading, closing and keys**

1. `LoadSession`, before `_tick.Start();`: `if (!s_windows.Contains(this)) s_windows.Add(this);` — registered only once fully loaded.
2. `Detach`: replace `if (ReferenceEquals(s_current, this)) s_current = null;` with `s_windows.Remove(this);`.
3. `CloseForExit`:

```csharp
        /// <summary>Closes the window for real: tests, a window merged into another, application exit. Safe to call twice.</summary>
        public void CloseForExit()
        {
            if (_closedForExit) return;
            _closedForExit = true;
            PrepareForExit();
            // Detach first: a window that was never shown may not raise Closed.
            Detach();
            Close();
        }
```

4. `OnClosing` — the whole method becomes the one below; the hide path it had (capture, leave full screen, not open, flush, save, hide) now lives in `HideKeepingTabs`, full-screen exit included, so `PadFullScreenTests.Closing_hides_the_window_windowed` keeps passing:

```csharp
        /// <summary>The close button becomes <see cref="CloseByUser"/> unless the window is really exiting.</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_exiting)
            {
                e.Cancel = true;
                // Closing for real cannot start inside Closing: the merge runs right after it.
                if (OtherWindows().Count > 0) Dispatcher.BeginInvoke(new Action(() => Guard("Closing a MicaPad window", CloseByUser)));
                else HideKeepingTabs();
            }
            base.OnClosing(e);
        }
```

5. `HandleShortcut`, after `if (ctrl && key == Key.N) NewTab();`: `else if (ctrlShift && key == Key.N) NewWindow();`.
6. `BuildMainMenu`, after *New note*: `menu.Items.Add(Item("New window", "Ctrl+Shift+N", () => NewWindow(), icon: "\uE8A7"));`.

(All windows share the MicaPad taskbar identity already: `OnSourceInitialized` applies the same AppUserModelID to every window, so they group on the taskbar. Nothing to change.)

- [ ] **Step 5: Exit captures every window** — `App.xaml.cs`: in `FlushPad` and in `Quit`, replace `Kil0bitSystemMonitor.Pad.MicaPadWindow.Current?.PrepareForExit();` with `Kil0bitSystemMonitor.Pad.MicaPadWindow.PrepareAllForExit();`. (`ReopenPadIfItWasOpen`'s `MicaPadWindow.Current != null` keeps its meaning: some window is loaded.)

- [ ] **Step 6: Run the focused tests, then the whole suite** — expected PASS.

- [ ] **Step 7: Document it** — `GUIDE.md`, `## 📝 MicaPad`, a new section right before `### Where notes live`:

```markdown
### More than one window

**Ctrl+Shift+N** (or **☰ → New window**) opens another MicaPad window with a new note. Each window
has its own tabs, size and place, zoom, **Always on top** and full screen; the theme, the font and
the other switches are shared. Closing a window while another one is open moves its tabs into the
window you used last — no note is closed. Closing the last window only hides it, as before, and
every window comes back after you sign in.

```

README English *Inside MicaPad* table, after the row that starts `| **Ctrl+N** · **Ctrl+W** · **Ctrl+Shift+T** |`: `| **Ctrl+Shift+N** | New window |`; Thai table (under `ภายใน MicaPad:`), after its row that starts the same way: `| **Ctrl+Shift+N** | หน้าต่างใหม่ |`.

README, both MicaPad feature lists, right before the notes-folder bullet (after the **Tools** and **Compare with current** bullets of Tasks 1 and 2):

```markdown
* **More than one window** (**Ctrl+Shift+N**), each with its own tabs, place and zoom; closing one moves its tabs into another, so no note is ever closed that way
```

```markdown
* **หลายหน้าต่าง** (**Ctrl+Shift+N**) แต่ละหน้าต่างมีแท็บ ตำแหน่ง และการซูมของตัวเอง เมื่อปิดหน้าต่างหนึ่ง แท็บจะย้ายไปอยู่อีกหน้าต่าง จึงไม่มีโน้ตใดถูกปิดไปด้วย
```

- [ ] **Step 8: Commit**

```bash
git add Pad/MicaPadWindow.xaml.cs App.xaml.cs GUIDE.md README.md tests/Kil0bitSystemMonitor.Tests/PadWindowsTests.cs
git commit -m "feat(pad): Ctrl+Shift+N opens a window; closing one hands its tabs to the last used" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

### Task 6: Files land in the right window; moving tabs between windows

`--pad`, Open with, the hotkey and the overlay go to the right window; a tab's menu moves it to a new window or another one; reopen at login asks whether any window was open.

**Files:**
- Modify: `Pad/MicaPadWindow.xaml.cs`, `App.xaml.cs` (`OpenPad`, `ReopenPadIfItWasOpen`), `GUIDE.md`
- Modify: `tests/Kil0bitSystemMonitor.Tests/PadWindowsTests.cs` (more tests), `tests/Kil0bitSystemMonitor.Tests/PadMenuTests.cs` (the tab menu gains its Move items)

**Interfaces:**
- Consumes (Tasks 3–5): `PadWorkspace.RouteFile(string?)`, `MoveToWindow`, `NewWindow`, `TabsOf`, `ActiveIn`; `SessionWindows.AnyOpen`; `MicaPadWindow.Registered`, `Create(..., beforeLoad)`, `Present`, `OtherWindows`, `MergeInto`, `ReleaseDocument`, `AdoptDocument`, `CaptureViewState`, `CloseForExit`, `ShowWindow`.
- Produces: `ShowOrActivate(PadWorkspace, AppConfig, Action?, string? path = null)` (routes by path); `public static MicaPadWindow Open(PadWorkspace workspace, AppConfig config, Action? openSettings, string? path)` (what `App.OpenPad` calls); `internal void MoveToNewWindow(OpenNote note)`; `internal void MoveToWindow(OpenNote note, MicaPadWindow target)`; `ShowNote` of another window's tab brings that window forward on it; the tab menu's *Move to new window* and *Move to ▸*.

- [ ] **Step 1: Write the failing tests**

Add to `PadWindowsTests`:

```csharp
        [Fact]
        public void Move_to_new_window_takes_the_tab_with_its_undo() => WithWindows((first, env, shown) =>
        {
            first.Editor.Document.Insert(0, "stays");
            first.NewTab();
            first.Editor.Document.Insert(0, "leaves");
            var leaving = env.Workspace.ActiveIn(first.WindowId)!;
            var document = first.Editor.Document;

            PadMenuTests.Click(PadMenuTests.ItemOf(first.BuildTabMenu(leaving, null), "Move to new window"));

            var second = Assert.Single(shown);
            Assert.Equal(new[] { leaving }, env.Workspace.TabsOf(second.WindowId));
            Assert.DoesNotContain(leaving, env.Workspace.TabsOf(first.WindowId));
            Assert.Same(document, second.Editor.Document);
            Assert.True(second.Editor.CanUndo);
            Assert.Equal("stays", first.Editor.Document.Text);        // the neighbour is shown here now
        });

        [Fact]
        public void The_only_tab_cannot_move_to_a_new_window() => WithWindows((window, env, shown) =>
        {
            var only = env.Workspace.ActiveIn(window.WindowId)!;

            var menu = window.BuildTabMenu(only, null);

            Assert.False(PadMenuTests.ItemOf(menu, "Move to new window").IsEnabled);
            Assert.DoesNotContain("Move to", PadMenuTests.Headers(menu));    // no other window to move to
        });

        [Fact]
        public void Move_to_lists_each_other_window_by_its_active_tab() => WithWindows((first, env, shown) =>
        {
            first.Editor.Document.Insert(0, "Groceries");
            var second = first.NewWindow();
            second.Editor.Document.Insert(0, "Meeting notes");
            env.Workspace.FlushPending();                             // a note's title follows its first line when saved
            var note = env.Workspace.ActiveIn(first.WindowId)!;

            var moveTo = PadMenuTests.ItemOf(first.BuildTabMenu(note, null), "Move to");

            Assert.Equal(new[] { "Meeting notes" }, moveTo.Items.OfType<MenuItem>().Select(m => (string)m.Header));

            env.Workspace.Rename(env.Workspace.ActiveIn(second.WindowId)!, "error_log.txt");
            moveTo = PadMenuTests.ItemOf(first.BuildTabMenu(note, null), "Move to");
            Assert.Equal("error__log.txt", (string)moveTo.Items.OfType<MenuItem>().Single().Header);   // shows as error_log.txt, no access key
        });

        [Fact]
        public void Move_to_another_window_shows_the_tab_there() => WithWindows((first, env, shown) =>
        {
            first.Editor.Document.Insert(0, "keep");
            first.NewTab();
            first.Editor.Document.Insert(0, "Travel");
            var travel = env.Workspace.ActiveIn(first.WindowId)!;
            var second = first.NewWindow();
            shown.Clear();

            PadMenuTests.Click(PadMenuTests.ItemOf(first.BuildTabMenu(travel, null), "Move to").Items.OfType<MenuItem>().Single());

            Assert.Same(travel, env.Workspace.ActiveIn(second.WindowId));
            Assert.Equal("Travel", second.Editor.Document.Text);
            Assert.Equal("keep", first.Editor.Document.Text);
            Assert.Equal(new[] { second }, shown);
        });

        [Fact]
        public void Moving_the_only_tab_closes_its_window_into_the_other() => WithWindows((first, env, shown) =>
        {
            var second = first.NewWindow();
            second.Editor.Document.Insert(0, "Travel");
            var travel = env.Workspace.ActiveIn(second.WindowId)!;

            PadMenuTests.Click(PadMenuTests.ItemOf(second.BuildTabMenu(travel, null), "Move to").Items.OfType<MenuItem>().Single());

            Assert.DoesNotContain(second, MicaPadWindow.WindowsOf(env.Workspace));
            Assert.Single(env.Workspace.Windows);
            Assert.Same(travel, env.Workspace.ActiveIn(first.WindowId));
            Assert.Equal("Travel", first.Editor.Document.Text);
            Assert.True(first.Editor.CanUndo);
        });

        [Fact]
        public void Opening_a_file_open_in_another_window_brings_that_window_forward_on_its_tab() => WithWindows((first, env, shown) =>
        {
            string path = env.FileOf("app.log");
            File.WriteAllText(path, "started");
            var second = first.NewWindow();
            second.OpenPath(path);
            var log = env.Workspace.ActiveIn(second.WindowId)!;
            second.NewTab();                                          // the log is no longer the tab shown there
            env.Workspace.ActivateWindow(first.WindowId);             // and the user went back to the first window
            shown.Clear();

            var window = MicaPadWindow.Open(env.Workspace, new AppConfig(), null, path.ToUpperInvariant());   // what --pad and Open with do

            Assert.Same(second, window);
            Assert.Same(second, shown[shown.Count - 1]);
            Assert.Same(log, env.Workspace.ActiveIn(second.WindowId));
            Assert.Equal("started", second.Editor.Document.Text);
            Assert.Single(env.Workspace.Open, n => string.Equals(n.Meta.SourcePath, path, StringComparison.OrdinalIgnoreCase));
        });

        [Fact]
        public void A_file_open_nowhere_goes_to_the_most_recently_active_window() => WithWindows((first, env, shown) =>
        {
            string path = env.FileOf("new.txt");
            File.WriteAllText(path, "fresh");
            first.NewWindow();
            env.Workspace.ActivateWindow(first.WindowId);

            var window = MicaPadWindow.Open(env.Workspace, new AppConfig(), null, path);

            Assert.Same(first, window);
            Assert.Equal(first.WindowId, env.Workspace.Open.Single(n => n.Meta.SourcePath == path).WindowId);
            Assert.Equal("fresh", first.Editor.Document.Text);
        });

        [Fact]
        public void Ctrl_o_of_a_file_open_in_another_window_shows_it_there() => WithWindows((first, env, shown) =>
        {
            string path = env.FileOf("shared.txt");
            File.WriteAllText(path, "one copy");
            var second = first.NewWindow();
            second.OpenPath(path);
            second.NewTab();
            shown.Clear();

            first.OpenPath(path);                                     // Ctrl+O and the Open dialog end here

            Assert.Same(second, shown[shown.Count - 1]);
            Assert.Equal("one copy", second.Editor.Document.Text);
            Assert.DoesNotContain(env.Workspace.TabsOf(first.WindowId), n => n.Meta.SourcePath == path);
        });
```

In `tests/Kil0bitSystemMonitor.Tests/PadMenuTests.cs`: `A_note_tab_menu_has_rename_close_and_close_others` expects `new[] { "Rename…", "Close", "Close other tabs", "-", "Move to new window" }` and adds `Assert.False(ItemOf(menu, "Move to new window").IsEnabled);`; `A_file_tab_menu_adds_copy_path_and_show_in_folder` expects `new[] { "Rename…", "Close", "Close other tabs", "-", "Move to new window", "-", "Copy file path", "Show in folder" }`.

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~PadWindowsTests|FullyQualifiedName~PadMenuTests"`). Expected: build FAILS (`MicaPadWindow.Open` not found).

- [ ] **Step 3: Route by path** — in `Pad/MicaPadWindow.xaml.cs`:

1. `ShowOrActivate` gains the path, and its target comes from the workspace's routing:

```csharp
        /// <summary>
        /// Shows MicaPad (the hotkey, the overlay menu, the Start menu): the most recently active
        /// window comes to the front — or, with <paramref name="path"/>, the window already showing
        /// that file. The first show in a run brings back every window of the session, each where it
        /// was (spec 5.3: reopen at login restores every window), so no tab can wait in a window
        /// nobody shows; the target ends up in front.
        /// </summary>
        public static MicaPadWindow ShowOrActivate(PadWorkspace workspace, AppConfig config, Action? openSettings, string? path = null)
        {
            workspace.Restore();
            // A file already open in a window goes there; anything else to the most recently active window.
            string targetId = workspace.RouteFile(path);
            if (WindowsOf(workspace).Count == 0)
            {
                // Least recently active first, so each later one comes up in front of it.
                foreach (string id in workspace.ActivationOrder.Reverse().Where(id => id != targetId).ToList())
                    Create(workspace, config, id, openSettings).Present();
            }
            var window = Registered(workspace, targetId) ?? Create(workspace, config, targetId, openSettings);
            window.Present();
            return window;
        }
```

   (`RouteFile(null)` is `MostRecentWindowId`, so the hotkey and the overlay behave exactly as in Task 5.)

2. Next to it:

```csharp
        /// <summary>
        /// MicaPad for the hotkey, the overlay, <c>--pad</c> and Open with (spec 5.3), opening
        /// <paramref name="path"/> when given: a file already open in a window brings that window
        /// forward on its tab; any other file opens in the most recently active window.
        /// </summary>
        public static MicaPadWindow Open(PadWorkspace workspace, AppConfig config, Action? openSettings, string? path)
        {
            var window = ShowOrActivate(workspace, config, openSettings, path);
            if (!string.IsNullOrWhiteSpace(path)) window.OpenPath(path);
            return window;
        }
```

3. `ShowNote`: the Task 4 guard becomes

```csharp
            if (note.WindowId != _windowId)
            {
                // Another window shows this tab (a file already open there): that window comes forward on it (spec 5.3).
                if (Registered(_workspace, note.WindowId) is { } owner && !ReferenceEquals(owner, this) && !owner._exiting)
                {
                    owner.ShowNote(note);
                    owner.Present();
                }
                return;
            }
```

   so `OpenPath` of a file open elsewhere (`OpenFileStatus.AlreadyOpen` from `OpenFile(path, _windowId)`) shows it in its own window — Ctrl+O and `Open` alike.

- [ ] **Step 4: Move to** — in `Pad/MicaPadWindow.xaml.cs`:

1. `BuildTabMenu`, after *Close other tabs* (before the file-backed items):

```csharp
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Move to new window", null, () => MoveToNewWindow(note), _workspace.TabsOf(_windowId).Count > 1, icon: "\uE8A7"));
            var others = OtherWindows();
            if (others.Count > 0)
            {
                var moveTo = new MenuItem { Header = "Move to" };
                // Doubled: a menu header reads "_" as an access key, and file names are full of them.
                foreach (var other in others) moveTo.Items.Add(Item(other.ActiveTitle.Replace("_", "__"), null, () => MoveToWindow(note, other)));
                menu.Items.Add(moveTo);
            }
```

2. The moves:

```csharp
        /// <summary>A window's name in Move to ▸: the title of its active tab.</summary>
        private string ActiveTitle => _workspace.ActiveIn(_windowId)?.Title ?? "MicaPad";

        /// <summary>Tab menu → Move to new window: the tab moves to a new window of its own, with its undo history and bookmarks.</summary>
        internal void MoveToNewWindow(OpenNote note)
        {
            if (note.WindowId != _windowId || _workspace.TabsOf(_windowId).Count < 2) return;
            CaptureViewState();                   // the new window cascades from where this one is
            var document = LetGoOf(note);
            var state = _workspace.NewWindow(_windowId);
            _workspace.MoveToWindow(note, state.Id);
            MicaPadWindow window;
            try
            {
                window = Create(_workspace, _config, state.Id, OpenSettingsRequested,
                    beforeLoad: w => { if (document != null) w.AdoptDocument(note, document); });
            }
            catch
            {
                // No window shows it: the tab comes back here, undo history and all.
                _workspace.CloseWindow(state.Id, _windowId);
                if (document != null) AdoptDocument(note, document);
                throw;
            }
            window.Present();
        }

        /// <summary>
        /// Tab menu → Move to ▸ (spec 5.3): the tab moves to <paramref name="target"/> and is shown
        /// there. Moving this window's only tab closes this window into that one.
        /// </summary>
        internal void MoveToWindow(OpenNote note, MicaPadWindow target)
        {
            // The menu was built earlier: the target may have closed since.
            if (note.WindowId != _windowId || ReferenceEquals(target, this) || target._exiting || !s_windows.Contains(target)) return;
            if (_workspace.TabsOf(_windowId).Count == 1)
            {
                MergeInto(target);
                CloseForExit();
            }
            else
            {
                var document = LetGoOf(note);
                _workspace.MoveToWindow(note, target._windowId);
                if (document != null) target.AdoptDocument(note, document);
                _workspace.SaveSession();
            }
            target.ShowNote(note);
            target.Present();
        }

        /// <summary>A tab leaving this window: a neighbour is shown first if it was the shown one, then its document is released.</summary>
        private TextDocument? LetGoOf(OpenNote note)
        {
            if (ReferenceEquals(_shown, note))
            {
                var tabs = _workspace.TabsOf(_windowId);
                int index = tabs.IndexOf(note);
                ShowNote(tabs[index + 1 < tabs.Count ? index + 1 : index - 1]);
            }
            return ReleaseDocument(note);
        }
```

(`LetGoOf` is only reached with two or more tabs in the window. `Create`'s `beforeLoad` hands the document over before `LoadSession`, so the new window shows it instead of building a fresh one.)

- [ ] **Step 5: The app** — `App.xaml.cs`:

1. `OpenPad`: replace the `ShowOrActivate` line and the `if (!string.IsNullOrWhiteSpace(path)) window.OpenPath(path);` line after it with

```csharp
                Kil0bitSystemMonitor.Pad.MicaPadWindow.Open(s_pad, config, () => ShowSettingsSection("MicaPad"), path);
```

   and its summary: "…From the overlay menu, the hotkey, <c>--pad</c> and the settings page. A file already open in a MicaPad window brings that window forward; anything else goes to the most recently active window."

2. `ReopenPadIfItWasOpen`: `if (PadStore.LoadSession().WindowOpen) OpenPad(null);` → `if (Kil0bitSystemMonitor.Services.Pad.SessionWindows.AnyOpen(PadStore.LoadSession())) OpenPad(null);` — and the first show brings every window back (Task 5).

- [ ] **Step 6: Run the focused tests, then the whole suite** — expected PASS.

- [ ] **Step 7: Document it** — `GUIDE.md`:

1. `### More than one window`, append:

```markdown
Right-click a tab → **Move to new window**, or **Move to** one of the other windows (listed by the
tab each is showing); moving a window's last tab closes that window into the other. The hotkey
and the overlay bring back the window you used last. Opening a file — **Open with**, the Start
menu, **Ctrl+O** — that is already open in another window brings that window forward on its tab.
```

2. `### How saving works`, second bullet: "If MicaPad was open, it reopens with the same tabs after you sign in" → "If MicaPad was open, it reopens with the same windows and tabs after you sign in".
3. `### Right-click menus`, the tab paragraph: "Right-click a tab for **Rename**, **Close** and **Close other tabs**" → "Right-click a tab for **Rename**, **Close**, **Close other tabs** and **Move to new window** or **Move to** another window".

- [ ] **Step 8: Commit**

```bash
git add Pad/MicaPadWindow.xaml.cs App.xaml.cs GUIDE.md tests/Kil0bitSystemMonitor.Tests/PadWindowsTests.cs tests/Kil0bitSystemMonitor.Tests/PadMenuTests.cs
git commit -m "feat(pad): open files in the window showing them; move tabs between windows" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

## After the plan (controller)

1. Whole suite green (three consecutive runs); build 0 warnings.
2. Deploy for the owner's e2e: stop MicaStats, build Release into `bin\Release\net8.0-windows`, relaunch.
3. Owner's manual checks (spec manual item 6): Tools on real selections (a Thai Base64 round trip, a hex mask, a price sum); compare a history version of a long note; two windows across a restart (each at its place, the right one in front); *Open with* on a file open in the second window while the first is in front; close a window holding an edited real file and see the tab arrive in the other; downgrade check if wanted: a v2 `session.json` opened by v1.12 shows every tab.
