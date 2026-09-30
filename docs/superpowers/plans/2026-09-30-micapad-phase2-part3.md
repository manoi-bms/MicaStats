# MicaPad phase 2, Part 3: editing helpers — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** MicaPad gets the editing moves a notepad2/notepad4 user expects: auto-closing brackets and quotes, line operations (duplicate, move, join, sort, dedupe, trim), bookmarks, and marking every occurrence of the selected word.

**Architecture:** Every rule is a pure function in `Services/Pad` (`TextLines`, `AutoClosePolicy`, `LineOperations`, `OccurrenceFinder`) returning a decision or a single `TextEdit`, so it is tested without a UI thread and applied as one undoable edit. Thin adapters in `Pad/` (`AutoCloseHandler`, `BookmarkController` + `BookmarkMargin`, `OccurrenceHighlighter`) connect them to AvalonEdit. First, the text menus move out of the 1,500-line `MicaPadWindow` into `Pad/EditorMenus.cs` (parked Part 1 ruling), so this part's Lines group lands there.

**Tech Stack:** .NET 8 WPF, AvalonEdit 6.3.1.120 (`TextArea.TextEntering`, `TextArea.PerformTextInput`, `TextArea.SelectionChanged`, `TextAnchor`, `AbstractMargin`, `RectangleSelection`), xUnit.

**Spec:** `docs/superpowers/specs/2026-09-30-micapad-phase2-design.md` — Part 3 (sections 3.1–3.4), the `PadAutoClose` row and `Tabs[id].Bookmarks` of *Settings and storage*, Part 3 testing bullets and manual item 4.

## Global Constraints

- Auto-close: typing `(` `[` `{` `"` `'` `` ` `` inserts the closing character after the caret; typing a closing character when the same one is next moves over it; Backspace between an empty pair deletes both; with a selection, an opener wraps it; quotes only auto-close when the character before is not a letter or digit (`don't`); in Markdown `*` and `_` are never auto-closed. `AppConfig.PadAutoClose` default on; *Auto-close brackets and quotes* in `☰` and Settings.
- Line operations: `Ctrl+D` duplicate the line or selection; `Ctrl+Shift+↑` / `Ctrl+Shift+↓` move the selected lines; `Ctrl+J` join the selected lines (or this line with the next) with one space. Right-click **Lines ▸**: Duplicate, Move up, Move down, Join, Sort ascending, Sort descending (current culture, case-insensitive, stable), Remove duplicate lines (keeps the first), Trim trailing whitespace. Each works on the lines the selection touches (the whole document when nothing is selected, for sort, dedupe and trim), keeps the document's line endings, and is one undoable edit.
- Bookmarks: `Ctrl+F2` toggle on the caret line; `F2` / `Shift+F2` next / previous, wrapping; *Clear bookmarks* in `☰`; an accent dot in a narrow margin left of the line numbers; they move with the text while editing (anchored); saved per tab in `session.json` (`Tabs[id].Bookmarks`, line numbers) and restored; a restored bookmark past the end of the text is dropped.
- Mark occurrences: when the selection is exactly one whole word, every whole-word, case-sensitive occurrence gets a soft box (a different color from find matches, drawn below them); the status bar shows `5 matches`; updated 150 ms after the selection settles; counting stops at 10,000 (`10,000+ matches`); off above the 2 MB limit.
- Window shortcuts that edit text never fire while a text box (find, replace, go-to-line, rename) has the keyboard focus.
- Files written before this part load unchanged: `session.json` without `Bookmarks`, `config.json` without `PadAutoClose`.
- New code under `Services/Pad/` holds no WPF types. Only MicaPad changes.
- Tests never touch the real `%APPDATA%`, never launch MicaStats, never start Explorer, never use the network, and never create their own STA thread (use `UiThread.Run`; a second WPF thread deadlocked the suite once). Never build or publish into `bin\Release`.
- Build/test only with `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe"`; prefix full-suite runs with `timeout 300`. `CultureInfo.InvariantCulture` for number text.
- Commits: `git add` exact paths, `-m` messages (no heredoc), never amend, `Co-Authored-By:` trailer naming the model that wrote the commit.

## Review Focus

1. **Typing prose with auto-close on** — `don't`, `it's`, `"quoted"` words, a `(` typed in front of an existing word: nothing extra appears (Task 2: `Quotes_do_not_pair_after_a_letter`, `An_opener_before_a_word_does_not_pair`).
2. **Line operations at the edges** — the first or last line, a document without a trailing newline, a selection ending right after a line break, CRLF and mixed endings: nothing is lost, endings are kept, and one Ctrl+Z restores the text (Task 3: the edge-case theory and `Each_operation_is_one_undo_step`).
3. **Sort and dedupe on a whole note ending with a newline** — the final empty line stays last and blank lines are never removed (Task 3: `Sorting_keeps_a_trailing_newline_last`, `Removing_duplicates_keeps_blank_lines`).
4. **Bookmarks after editing and restart** — lines inserted or deleted above move the bookmark; a restart restores it on the moved line; a bookmark whose line was deleted does not duplicate another (Task 4: `Bookmarks_move_with_the_text`, `Bookmarks_survive_a_restart_on_their_moved_line`).
5. **Editing shortcuts while typing in the find box** — Ctrl+D or Ctrl+J in the find box must not duplicate or join editor lines (Task 3: `Editing_shortcuts_ignore_a_focused_text_box`).

## File structure

| File | Task | Responsibility |
|---|---|---|
| `Services/Pad/TextLines.cs` (new) | 1 | Whole-line blocks of a selection; split/join keeping each line break |
| `Services/Pad/MarkdownFormatter.cs` | 1 | Uses `TextLines` instead of private copies |
| `Pad/EditorMenus.cs` (new) | 1, 3 | Menu item helpers; the edit group; Format and Lines submenus; `ApplyEdit` |
| `Pad/MicaPadWindow.xaml.cs` | 1–5 | Wires the menus, handlers, keys and status text |
| `Pad/MicaPadWindow.xaml` | 5 | Occurrence count in the status bar |
| `Services/Pad/AutoClosePolicy.cs` (new), `Pad/AutoCloseHandler.cs` (new) | 2 | Auto-close rules and their AvalonEdit hook |
| `Models/SystemMetrics.cs` | 2 | `AppConfig.PadAutoClose` |
| `SettingsWindow.xaml`, `.xaml.cs` | 2 | Auto-close switch |
| `Services/Pad/LineOperations.cs` (new) | 3 | Duplicate, move, join, sort, dedupe, trim |
| `Services/Pad/NoteMeta.cs`, `Services/Pad/PadWorkspace.cs` | 4 | `TabViewState.Bookmarks`, `SetBookmarks`, view state keeps bookmarks |
| `Pad/BookmarkController.cs`, `Pad/BookmarkMargin.cs` (new) | 4 | Anchored bookmarks per document; the dot margin |
| `Services/Pad/OccurrenceFinder.cs` (new), `Pad/OccurrenceHighlighter.cs` (new) | 5 | Whole-word occurrences; their boxes |
| `Services/Pad/PadPalette.cs` | 5 | `Occurrence` color |
| `GUIDE.md`, `README.md` | 2–5 | User docs and the key table |
| tests: `TextLinesTests.cs`, `AutoCloseTests.cs`, `LineOperationsTests.cs`, `PadBookmarkTests.cs`, `OccurrenceTests.cs` (new); `PadPaletteTests.cs`, `PadConfigTests.cs`, `PadMenuTests.cs` (changed) | all | |

Focused test command (Git Bash, repo root):

```bash
DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests/Kil0bitSystemMonitor.Tests.csproj --filter "FullyQualifiedName~<TestClass>" -nologo
```

Whole suite: `timeout 300` + the same without `--filter`.

---

### Task 1: Menus out of the window, and shared line helpers

A behaviour-preserving refactor: no user-visible change, every existing test stays green.

**Files:**
- Create: `Services/Pad/TextLines.cs`
- Modify: `Services/Pad/MarkdownFormatter.cs` (delete its private `LineBlock`, `SplitLines`, `Join`, `NewlineOf`; call `TextLines`)
- Create: `Pad/EditorMenus.cs`
- Modify: `Pad/MicaPadWindow.xaml.cs` (`using static Kil0bitSystemMonitor.Pad.EditorMenus;`; remove `Item`, `Check`, `ClipboardHasText`, `BuildFormatMenu`, `ApplyEdit`; `FillEditorMenu` calls `EditorMenus`)
- Create: `tests/Kil0bitSystemMonitor.Tests/TextLinesTests.cs`

**Interfaces:**
- Produces:
  - `public static class TextLines` — `(int Start, int End) Block(string text, int start, int length)`, `(List<string> Lines, List<string> Breaks) Split(string block)`, `string Join(IReadOnlyList<string> lines, IReadOnlyList<string> breaks)`, `string NewlineOf(string text)`, `int LineStart(string text, int offset)`, `int LineEnd(string text, int offset)`, `int BreakLength(string text, int offset)`, `int BreakBefore(string text, int lineStart)`.
  - `internal static class EditorMenus` — `MenuItem Item(string header, string? gesture, Action action, bool enabled = true)`, `MenuItem Check(string header, string? gesture, bool isChecked, Action action)`, `void AddEditGroup(ContextMenu menu, TextEditor editor, bool readOnly)`, `MenuItem FormatMenu(TextEditor editor)`, `void ApplyEdit(TextEditor editor, TextEdit edit)`. Task 3 adds `LinesMenu`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/TextLinesTests.cs`:

```csharp
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Whole-line helpers shared by the Format menu and the line operations.</summary>
    public class TextLinesTests
    {
        [Theory]
        [InlineData("first\nsecond\nthird", 9, 0, 6, 12)]          // caret inside "second"
        [InlineData("one\r\ntwo\r\nthree", 0, 10, 0, 8)]          // ends right after a CRLF: "three" not taken
        [InlineData("a\nb", 0, 3, 0, 3)]                            // everything
        [InlineData("", 0, 0, 0, 0)]                                // empty text
        [InlineData("x\ny", 2, 0, 2, 3)]                            // caret at the start of the last line
        [InlineData("x\ry\rz", 2, 1, 2, 3)]                         // CR-only endings
        public void Block_takes_the_whole_lines_a_selection_touches(string text, int start, int length, int blockStart, int blockEnd)
        {
            Assert.Equal((blockStart, blockEnd), TextLines.Block(text, start, length));
        }

        [Theory]
        [InlineData("a\r\nb\nc\rd")]
        [InlineData("single")]
        [InlineData("trailing\n")]
        [InlineData("")]
        public void Split_then_join_gives_back_the_same_text(string text)
        {
            var (lines, breaks) = TextLines.Split(text);
            Assert.Equal(lines.Count - 1, breaks.Count);
            Assert.Equal(text, TextLines.Join(lines, breaks));
        }

        [Fact]
        public void Split_remembers_each_line_break_exactly()
        {
            var (lines, breaks) = TextLines.Split("a\r\nb\nc\rd");
            Assert.Equal(new[] { "a", "b", "c", "d" }, lines);
            Assert.Equal(new[] { "\r\n", "\n", "\r" }, breaks);
        }

        [Theory]
        [InlineData("a\r\nb", "\r\n")]
        [InlineData("a\nb", "\n")]
        [InlineData("a\rb", "\r")]
        [InlineData("none", "\r\n")]
        public void NewlineOf_is_the_first_break_or_crlf(string text, string expected)
        {
            Assert.Equal(expected, TextLines.NewlineOf(text));
        }

        [Fact]
        public void Line_bounds_and_breaks()
        {
            string text = "ab\r\ncd\nef";
            Assert.Equal(0, TextLines.LineStart(text, 1));
            Assert.Equal(4, TextLines.LineStart(text, 5));
            Assert.Equal(2, TextLines.LineEnd(text, 0));
            Assert.Equal(9, TextLines.LineEnd(text, 8));
            Assert.Equal(2, TextLines.BreakLength(text, 2));
            Assert.Equal(1, TextLines.BreakLength(text, 6));
            Assert.Equal(0, TextLines.BreakLength(text, 9));
            Assert.Equal(2, TextLines.BreakBefore(text, 4));   // the CRLF before "cd"
            Assert.Equal(1, TextLines.BreakBefore(text, 7));   // the LF before "ef"
            Assert.Equal(0, TextLines.BreakBefore(text, 0));
        }
    }
}
```

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~TextLinesTests"`). Expected: build FAILS (`TextLines` not found).

- [ ] **Step 3: Write `Services/Pad/TextLines.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Whole-line helpers shared by the Format menu and the line operations: which lines a
    /// selection covers, and splitting and joining lines while keeping every line break exactly
    /// as it was (CRLF, LF or CR).
    /// </summary>
    public static class TextLines
    {
        private static readonly char[] Breaks = { '\r', '\n' };

        /// <summary>
        /// The whole lines a selection touches (or the caret's line), as [Start, End) without the
        /// final line break. A selection that ends right after a line break does not take the next line.
        /// </summary>
        public static (int Start, int End) Block(string text, int start, int length)
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
            int blockStart = LineStart(text, start);
            int lineEnd = text.IndexOfAny(Breaks, Math.Max(end, blockStart));
            return (blockStart, lineEnd < 0 ? text.Length : lineEnd);
        }

        /// <summary>Splits text into lines and the exact break after each; there is one more line than breaks.</summary>
        public static (List<string> Lines, List<string> Breaks) Split(string block)
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

        /// <summary>Joins lines back with the breaks <see cref="Split"/> returned (or any list one shorter).</summary>
        public static string Join(IReadOnlyList<string> lines, IReadOnlyList<string> breaks)
        {
            var sb = new StringBuilder(lines.Count == 0 ? "" : lines[0]);
            for (int i = 1; i < lines.Count; i++) sb.Append(breaks[i - 1]).Append(lines[i]);
            return sb.ToString();
        }

        /// <summary>The text's line ending: its first line break, or CRLF for a text without one yet.</summary>
        public static string NewlineOf(string text)
        {
            int lf = text.IndexOf('\n');
            if (lf < 0) return text.Contains('\r') ? "\r" : "\r\n";
            return lf > 0 && text[lf - 1] == '\r' ? "\r\n" : "\n";
        }

        /// <summary>The start of the line containing <paramref name="offset"/>.</summary>
        public static int LineStart(string text, int offset) =>
            offset <= 0 ? 0 : text.LastIndexOfAny(Breaks, offset - 1) + 1;

        /// <summary>The end of the line containing <paramref name="offset"/>, before its line break.</summary>
        public static int LineEnd(string text, int offset)
        {
            int i = text.IndexOfAny(Breaks, Math.Min(offset, text.Length));
            return i < 0 ? text.Length : i;
        }

        /// <summary>The length of the line break starting at <paramref name="offset"/>: 2 for CRLF, 1, or 0 at the end of the text.</summary>
        public static int BreakLength(string text, int offset)
        {
            if (offset >= text.Length) return 0;
            return text[offset] == '\r' && offset + 1 < text.Length && text[offset + 1] == '\n' ? 2 : 1;
        }

        /// <summary>The length of the line break that ends just before <paramref name="lineStart"/>, or 0 on the first line.</summary>
        public static int BreakBefore(string text, int lineStart)
        {
            if (lineStart <= 0) return 0;
            return lineStart >= 2 && text[lineStart - 1] == '\n' && text[lineStart - 2] == '\r' ? 2 : 1;
        }
    }
}
```

- [ ] **Step 4: `MarkdownFormatter` uses `TextLines`**

In `Services/Pad/MarkdownFormatter.cs`: delete the private `LineBlock`, `SplitLines`, `Join` and `NewlineOf` methods (keep the `LineBreaks` field: `Wrap` still uses it to spot a multi-line selection). Replace their calls: `LineBlock(` → `TextLines.Block(`, `SplitLines(` → `TextLines.Split(`, `Join(` → `TextLines.Join(`, `NewlineOf(` → `TextLines.NewlineOf(`. Behaviour is unchanged (`TextLines.Block` computes the block start through `LineStart`, which is the same expression). Remove usings that become unused.

- [ ] **Step 5: Write `Pad/EditorMenus.cs`**

```csharp
using System;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using Kil0bitSystemMonitor.Services.Pad;

using Clipboard = System.Windows.Clipboard;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// MicaPad's menu pieces: item helpers, the right-click edit group, and the Format (and, from
    /// Part 3, Lines) submenus. The window decides which groups a menu gets; the groups themselves
    /// live here so the window stays about windows, tabs and files.
    /// </summary>
    internal static class EditorMenus
    {
        public static MenuItem Item(string header, string? gesture, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
            item.Click += (s, e) => action();
            return item;
        }

        public static MenuItem Check(string header, string? gesture, bool isChecked, Action action)
        {
            var item = Item(header, gesture, action);
            item.IsCheckable = true;
            item.IsChecked = isChecked;
            return item;
        }

        /// <summary>
        /// Undo, Redo, Cut, Copy, Paste, Delete, Select all — each disabled when it cannot apply.
        /// A read-only editor (the history preview) gets Copy and Select all only.
        /// </summary>
        public static void AddEditGroup(ContextMenu menu, TextEditor editor, bool readOnly)
        {
            bool hasSelection = editor.SelectionLength > 0;
            bool hasText = editor.Document != null && editor.Document.TextLength > 0;

            if (!readOnly)
            {
                menu.Items.Add(Item("Undo", "Ctrl+Z", () => editor.Undo(), editor.CanUndo));
                menu.Items.Add(Item("Redo", "Ctrl+Y", () => editor.Redo(), editor.CanRedo));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Cut", "Ctrl+X", () => editor.Cut(), hasSelection));
            }
            menu.Items.Add(Item("Copy", "Ctrl+C", () => editor.Copy(), hasSelection));
            if (!readOnly)
            {
                menu.Items.Add(Item("Paste", "Ctrl+V", () => editor.Paste(), ClipboardHasText()));
                // The Del key's own command: it respects a rectangular selection.
                menu.Items.Add(Item("Delete", "Del", () => ApplicationCommands.Delete.Execute(null, editor.TextArea), hasSelection));
            }
            menu.Items.Add(Item("Select all", "Ctrl+A", () => editor.SelectAll(), hasText));
        }

        /// <summary>Format ▸ for a Markdown tab (spec 2.4): each item is one undoable edit; no new shortcuts.</summary>
        public static MenuItem FormatMenu(TextEditor editor)
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
        public static void ApplyEdit(TextEditor editor, TextEdit edit)
        {
            editor.Document.Replace(edit.Offset, edit.Length, edit.Text);
            editor.Select(edit.SelectionStart, edit.SelectionLength);
        }

        /// <summary>True when Paste has something to paste. A busy clipboard counts as yes: Paste itself then tries.</summary>
        private static bool ClipboardHasText()
        {
            try
            {
                return Clipboard.ContainsText();
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return true;
            }
        }
    }
}
```

- [ ] **Step 6: The window uses `EditorMenus`**

In `Pad/MicaPadWindow.xaml.cs`:

1. Add `using static Kil0bitSystemMonitor.Pad.EditorMenus;` with the other usings, so the existing `Item(...)` and `Check(...)` calls (☰ menu, tab menu, language menu, encoding and line-ending menus) resolve to `EditorMenus`.
2. Delete the window's own `Item`, `Check`, `ClipboardHasText`, `BuildFormatMenu` and `ApplyEdit` members.
3. Replace the body of `FillEditorMenu` with:

```csharp
            menu.Items.Clear();
            ModernWpf.ThemeManager.SetRequestedTheme(menu, _palette.IsDark ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);

            AddEditGroup(menu, editor, readOnly);
            if (readOnly) return;

            if (ReferenceEquals(editor, Editor) && ReferenceEquals(_resolved.Effective, PadLanguages.Markdown))
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(FormatMenu(editor));
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Find", "Ctrl+F", () => FindBar.Open(replace: false)));
            menu.Items.Add(Item("Replace", "Ctrl+H", () => FindBar.Open(replace: true)));
            menu.Items.Add(Item("Go to line…", "Ctrl+G", ShowGoToLine));
```

Keep the summary comment of `FillEditorMenu` ("Later parts add their groups (Format, Lines, Tools) here, before the Find group.").

- [ ] **Step 7: Run the new tests, then the whole suite** — expected PASS, with every existing test (PadMenuTests, MarkdownFormatterTests, PadThemeTests, PadWindowTests, PadLanguageWindowTests, MarkdownRenderingTests, PadFoldingTests) unchanged and green.

- [ ] **Step 8: Commit**

```bash
git add Services/Pad/TextLines.cs Services/Pad/MarkdownFormatter.cs Pad/EditorMenus.cs Pad/MicaPadWindow.xaml.cs tests/Kil0bitSystemMonitor.Tests/TextLinesTests.cs
git commit -m "refactor(pad): menus out of the window, and shared whole-line helpers" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

### Task 2: Auto-close brackets and quotes

**Files:**
- Create: `Services/Pad/AutoClosePolicy.cs`, `Pad/AutoCloseHandler.cs`
- Modify: `Models/SystemMetrics.cs` (`PadAutoClose`), `Pad/MicaPadWindow.xaml.cs` (handler, `☰` item), `SettingsWindow.xaml`, `SettingsWindow.xaml.cs`, `GUIDE.md`, `README.md`
- Create: `tests/Kil0bitSystemMonitor.Tests/AutoCloseTests.cs`
- Modify: `tests/Kil0bitSystemMonitor.Tests/PadConfigTests.cs`

**Interfaces:**
- Produces: `public enum AutoCloseAction { Insert, Pair, SkipOver, Wrap }`; `AutoClosePolicy.CloserOf(char)` → `char?`; `AutoClosePolicy.OnType(char typed, char? before, char? after, bool hasSelection)`; `AutoClosePolicy.DeletesPair(char? before, char? after)`; `internal sealed class AutoCloseHandler` (ctor `(TextEditor editor, Func<bool> enabled)`, `internal bool TryDeletePair()`); `AppConfig.PadAutoClose` (default true).

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/AutoCloseTests.cs`:

```csharp
using System;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Auto-closing brackets and quotes: the rules, and typing through the real AvalonEdit path.</summary>
    public class AutoCloseTests
    {
        [Theory]
        [InlineData('(', null, null, AutoCloseAction.Pair)]
        [InlineData('[', ' ', '\n', AutoCloseAction.Pair)]
        [InlineData('{', 'x', ')', AutoCloseAction.Pair)]
        [InlineData('"', ' ', null, AutoCloseAction.Pair)]
        [InlineData('`', '(', ')', AutoCloseAction.Pair)]
        [InlineData(')', 'a', ')', AutoCloseAction.SkipOver)]
        [InlineData('"', 'a', '"', AutoCloseAction.SkipOver)]
        [InlineData('a', null, null, AutoCloseAction.Insert)]
        [InlineData('*', ' ', null, AutoCloseAction.Insert)]
        [InlineData('_', ' ', null, AutoCloseAction.Insert)]
        [InlineData(')', 'a', null, AutoCloseAction.Insert)]
        public void The_rules(char typed, char? before, char? after, AutoCloseAction expected)
        {
            Assert.Equal(expected, AutoClosePolicy.OnType(typed, before, after, hasSelection: false));
        }

        [Theory]
        [InlineData('\'', 'n')]
        [InlineData('"', '3')]
        [InlineData('`', 'z')]
        public void Quotes_do_not_pair_after_a_letter(char quote, char before)
        {
            Assert.Equal(AutoCloseAction.Insert, AutoClosePolicy.OnType(quote, before, null, hasSelection: false));
        }

        [Theory]
        [InlineData('(', 'w')]
        [InlineData('"', 'w')]
        [InlineData('[', '1')]
        public void An_opener_before_a_word_does_not_pair(char opener, char after)
        {
            Assert.Equal(AutoCloseAction.Insert, AutoClosePolicy.OnType(opener, ' ', after, hasSelection: false));
        }

        [Fact]
        public void A_selection_is_wrapped_by_an_opener_and_replaced_by_anything_else()
        {
            Assert.Equal(AutoCloseAction.Wrap, AutoClosePolicy.OnType('(', null, null, hasSelection: true));
            Assert.Equal(AutoCloseAction.Wrap, AutoClosePolicy.OnType('"', 'a', 'b', hasSelection: true));
            Assert.Equal(AutoCloseAction.Insert, AutoClosePolicy.OnType('x', null, null, hasSelection: true));
            Assert.Equal(AutoCloseAction.Insert, AutoClosePolicy.OnType(')', null, null, hasSelection: true));
        }

        [Fact]
        public void Backspace_deletes_an_empty_pair_only()
        {
            Assert.True(AutoClosePolicy.DeletesPair('(', ')'));
            Assert.True(AutoClosePolicy.DeletesPair('"', '"'));
            Assert.False(AutoClosePolicy.DeletesPair('(', ']'));
            Assert.False(AutoClosePolicy.DeletesPair('a', 'a'));
            Assert.False(AutoClosePolicy.DeletesPair(null, ')'));
        }

        private static void WithWindow(bool autoClose, Action<MicaPadWindow> test) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var window = new MicaPadWindow(env.Workspace, new AppConfig { PadAutoClose = autoClose });
            try
            {
                window.LoadSession();
                test(window);
            }
            finally
            {
                window.CloseForExit();
            }
        });

        [Fact]
        public void Typing_an_opener_inserts_the_pair_and_the_closer_steps_over() => WithWindow(true, window =>
        {
            window.Editor.TextArea.PerformTextInput("f");
            window.Editor.TextArea.PerformTextInput("(");
            Assert.Equal("f()", window.Editor.Document.Text);
            Assert.Equal(2, window.Editor.CaretOffset);

            window.Editor.TextArea.PerformTextInput("x");
            window.Editor.TextArea.PerformTextInput(")");
            Assert.Equal("f(x)", window.Editor.Document.Text);
            Assert.Equal(4, window.Editor.CaretOffset);
        });

        [Fact]
        public void An_apostrophe_in_prose_types_normally() => WithWindow(true, window =>
        {
            foreach (char c in "don't") window.Editor.TextArea.PerformTextInput(c.ToString());
            Assert.Equal("don't", window.Editor.Document.Text);
        });

        [Fact]
        public void An_opener_wraps_the_selection() => WithWindow(true, window =>
        {
            window.Editor.Document.Text = "word";
            window.Editor.Select(0, 4);
            window.Editor.TextArea.PerformTextInput("\"");
            Assert.Equal("\"word\"", window.Editor.Document.Text);
            Assert.Equal("word", window.Editor.SelectedText);
        });

        [Fact]
        public void Backspace_between_an_empty_pair_removes_both() => WithWindow(true, window =>
        {
            window.Editor.TextArea.PerformTextInput("[");
            Assert.Equal("[]", window.Editor.Document.Text);

            Assert.True(window.AutoClose.TryDeletePair());
            Assert.Equal("", window.Editor.Document.Text);

            window.Editor.Document.Text = "a]";
            window.Editor.CaretOffset = 1;
            Assert.False(window.AutoClose.TryDeletePair());
        });

        [Fact]
        public void With_auto_close_off_nothing_is_added() => WithWindow(false, window =>
        {
            window.Editor.TextArea.PerformTextInput("(");
            Assert.Equal("(", window.Editor.Document.Text);
        });

        [Fact]
        public void A_pair_is_one_undo_step() => WithWindow(true, window =>
        {
            window.Editor.TextArea.PerformTextInput("{");
            window.Editor.Undo();
            Assert.Equal("", window.Editor.Document.Text);
        });
    }
}
```

In `PadConfigTests`, add:

```csharp
        [Fact]
        public void Auto_close_is_on_by_default_and_round_trips()
        {
            Assert.True(new AppConfig().PadAutoClose);
            Assert.True(JsonSerializer.Deserialize<AppConfig>("{\"ShowCpu\": false}")!.PadAutoClose);
            Assert.False(JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig { PadAutoClose = false }))!.PadAutoClose);
        }
```

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~AutoCloseTests|FullyQualifiedName~PadConfigTests"`). Expected: build FAILS.

- [ ] **Step 3: Write `Services/Pad/AutoClosePolicy.cs`**

```csharp
namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>What typing a character does with auto-close on.</summary>
    public enum AutoCloseAction
    {
        /// <summary>Type it normally.</summary>
        Insert,
        /// <summary>Type it and its closer, caret between.</summary>
        Pair,
        /// <summary>The same closer is already next: move over it instead of typing a second.</summary>
        SkipOver,
        /// <summary>Put it before the selection and its closer after.</summary>
        Wrap,
    }

    /// <summary>
    /// The auto-close rules (spec 3.1). Pure: the handler passes the characters around the caret.
    /// Brackets and quotes pair only in front of whitespace, the end of the line or a closing
    /// character, never in front of a word; quotes also never right after a letter or digit, so
    /// "don't" types normally. <c>*</c> and <c>_</c> are never paired (Markdown).
    /// </summary>
    public static class AutoClosePolicy
    {
        /// <summary>The closing character of an opener, or null.</summary>
        public static char? CloserOf(char opener) => opener switch
        {
            '(' => ')',
            '[' => ']',
            '{' => '}',
            '"' => '"',
            '\'' => '\'',
            '`' => '`',
            _ => null,
        };

        public static AutoCloseAction OnType(char typed, char? before, char? after, bool hasSelection)
        {
            bool quote = typed is '"' or '\'' or '`';
            bool openBracket = typed is '(' or '[' or '{';
            bool closeBracket = typed is ')' or ']' or '}';

            if (hasSelection) return openBracket || quote ? AutoCloseAction.Wrap : AutoCloseAction.Insert;
            if ((closeBracket || quote) && after == typed) return AutoCloseAction.SkipOver;
            if (!openBracket && !quote) return AutoCloseAction.Insert;
            if (after is char a && !char.IsWhiteSpace(a) && a is not (')' or ']' or '}' or ',' or ';' or ':' or '.'))
                return AutoCloseAction.Insert;
            if (quote && before is char b && char.IsLetterOrDigit(b)) return AutoCloseAction.Insert;
            return AutoCloseAction.Pair;
        }

        /// <summary>True when Backspace sits between an opener and its closer: both go.</summary>
        public static bool DeletesPair(char? before, char? after) =>
            before is char b && CloserOf(b) is char closer && after == closer;
    }
}
```

- [ ] **Step 4: Write `Pad/AutoCloseHandler.cs`**

```csharp
using System;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using Kil0bitSystemMonitor.Services.Pad;

using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Connects <see cref="AutoClosePolicy"/> to an editor: it looks at each typed character before
    /// AvalonEdit inserts it, and at Backspace. Each change it makes is one undoable edit. A
    /// rectangular selection is left to AvalonEdit.
    /// </summary>
    internal sealed class AutoCloseHandler
    {
        private readonly TextEditor _editor;
        private readonly Func<bool> _enabled;

        public AutoCloseHandler(TextEditor editor, Func<bool> enabled)
        {
            _editor = editor;
            _enabled = enabled;
            editor.TextArea.TextEntering += OnTextEntering;
            editor.TextArea.PreviewKeyDown += OnPreviewKeyDown;
        }

        private bool Active => _enabled() && !_editor.IsReadOnly && _editor.TextArea.Selection is not RectangleSelection;

        private void OnTextEntering(object? sender, TextCompositionEventArgs e)
        {
            if (!Active || e.Text.Length != 1) return;

            var document = _editor.Document;
            int caret = _editor.CaretOffset;
            bool hasSelection = _editor.SelectionLength > 0;
            char typed = e.Text[0];
            char? before = !hasSelection && caret > 0 ? document.GetCharAt(caret - 1) : null;
            char? after = !hasSelection && caret < document.TextLength ? document.GetCharAt(caret) : null;

            switch (AutoClosePolicy.OnType(typed, before, after, hasSelection))
            {
                case AutoCloseAction.Pair:
                    document.Insert(caret, typed.ToString() + AutoClosePolicy.CloserOf(typed));
                    _editor.CaretOffset = caret + 1;
                    e.Handled = true;
                    break;
                case AutoCloseAction.SkipOver:
                    _editor.CaretOffset = caret + 1;
                    e.Handled = true;
                    break;
                case AutoCloseAction.Wrap:
                    int start = _editor.SelectionStart;
                    int length = _editor.SelectionLength;
                    document.Replace(start, length, typed + _editor.SelectedText + AutoClosePolicy.CloserOf(typed));
                    _editor.Select(start + 1, length);
                    e.Handled = true;
                    break;
            }
        }

        private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None && TryDeletePair()) e.Handled = true;
        }

        /// <summary>Backspace between an empty pair: removes both halves as one edit. Returns false when it does not apply.</summary>
        internal bool TryDeletePair()
        {
            if (!Active || _editor.SelectionLength > 0) return false;
            var document = _editor.Document;
            int caret = _editor.CaretOffset;
            if (caret == 0 || caret >= document.TextLength) return false;
            if (!AutoClosePolicy.DeletesPair(document.GetCharAt(caret - 1), document.GetCharAt(caret))) return false;
            document.Remove(caret - 1, 2);
            return true;
        }
    }
}
```

- [ ] **Step 5: `AppConfig.PadAutoClose`**

In `Models/SystemMetrics.cs`, MicaPad block: field `private bool _padAutoClose = true;` and, after `PadMarkdown`:

```csharp
        /// <summary>Auto-close brackets and quotes in MicaPad.</summary>
        public bool PadAutoClose { get => _padAutoClose; set { Set(ref _padAutoClose, value); } }
```

- [ ] **Step 6: Wire it into the window**

In `Pad/MicaPadWindow.xaml.cs`:

1. Field `private AutoCloseHandler _autoClose = null!;` and, in the constructor after the `_previewLanguage = …` line: `_autoClose = new AutoCloseHandler(Editor, () => _config.PadAutoClose);`
2. `internal AutoCloseHandler AutoClose => _autoClose;` (for tests), next to `LanguageView`.
3. In `OnMenuButtonClick`, after the *Markdown formatting* item:

```csharp
            menu.Items.Add(Check("Auto-close brackets and quotes", null, _config.PadAutoClose, () => _config.PadAutoClose = !_config.PadAutoClose));
```

- [ ] **Step 7: Settings switch**

In `SettingsWindow.xaml`, MicaPad section, insert after the *Markdown formatting* card (the one with `x:Name="PadMarkdownToggle"`):

```xml
                        <Border Background="{DynamicResource SystemControlBackgroundChromeMediumLowBrush}" BorderBrush="{DynamicResource SystemControlElevationBorderBrush}" BorderThickness="1" CornerRadius="8" Margin="0,0,0,12" Padding="20,16">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto" />
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="Auto" />
                                </Grid.ColumnDefinitions>
                                <ui:FontIcon Glyph="&#xE943;" FontSize="20" Foreground="{DynamicResource SystemAccentColorBrush}" Margin="0,0,20,0" VerticalAlignment="Center"/>
                                <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,16,0">
                                    <TextBlock Text="Auto-close brackets and quotes" FontWeight="SemiBold" FontSize="15"/>
                                    <TextBlock Text="Typing ( [ { or a quote adds the closing one; typing it again steps over it. Never in the middle of a word." Opacity="0.6" FontSize="12.5" TextWrapping="Wrap"/>
                                </StackPanel>
                                <ui:ToggleSwitch Grid.Column="2" x:Name="PadAutoCloseToggle" Toggled="OnPadToggled" VerticalAlignment="Center"/>
                            </Grid>
                        </Border>
```

In `SettingsWindow.xaml.cs`: `LoadPadSettings` gains `PadAutoCloseToggle.IsOn = cfg.PadAutoClose;` after the `PadMarkdownToggle` line; `OnPadToggled` gains `cfg.PadAutoClose = PadAutoCloseToggle.IsOn;` after the `PadMarkdown` line.

- [ ] **Step 8: Run the focused tests, then the whole suite** — expected PASS.

- [ ] **Step 9: Document it**

`GUIDE.md`, `## 📝 MicaPad`, insert before `### Where notes live`:

```markdown
### Editing helpers

**Auto-close**: typing `(`, `[`, `{` or a quote adds the closing one after the caret; typing it
again steps over it, and Backspace right after the opening one removes both. With text selected,
an opening bracket or quote wraps the selection. It stays out of the way in prose — `don't` types
normally, and nothing is added in front of a word. Turn it off in **☰** or **Settings → MicaPad**.

```

`README.md`, English MicaPad list, after the *Folding* bullet:

```markdown
* **Editing helpers**: auto-closing brackets and quotes, notepad4-style line operations, bookmarks, and every occurrence of the selected word marked
```

Thai list, after the *ย่อ/ขยายโค้ด* bullet:

```markdown
* **ตัวช่วยแก้ไข**: ปิดวงเล็บและเครื่องหมายคำพูดอัตโนมัติ คำสั่งจัดการบรรทัดแบบ notepad4 บุ๊กมาร์ก และไฮไลต์คำเดียวกันทั้งหมดเมื่อเลือกคำ
```

- [ ] **Step 10: Commit**

```bash
git add Services/Pad/AutoClosePolicy.cs Pad/AutoCloseHandler.cs Models/SystemMetrics.cs Pad/MicaPadWindow.xaml.cs SettingsWindow.xaml SettingsWindow.xaml.cs GUIDE.md README.md tests/Kil0bitSystemMonitor.Tests/AutoCloseTests.cs tests/Kil0bitSystemMonitor.Tests/PadConfigTests.cs
git commit -m "feat(pad): auto-close brackets and quotes, out of the way in prose" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

### Task 3: Line operations

**Files:**
- Create: `Services/Pad/LineOperations.cs`
- Modify: `Pad/EditorMenus.cs` (`LinesMenu`), `Pad/MicaPadWindow.xaml.cs` (`FillEditorMenu`, `HandleShortcut`, `RunLineOperation`), `GUIDE.md`, `README.md`
- Create: `tests/Kil0bitSystemMonitor.Tests/LineOperationsTests.cs`
- Modify: `tests/Kil0bitSystemMonitor.Tests/PadMenuTests.cs`

**Interfaces:**
- Consumes: `TextLines`, `TextEdit`, `EditorMenus.ApplyEdit` (Task 1).
- Produces: `public static class LineOperations` — `TextEdit Duplicate(string text, int start, int length)`, `TextEdit? MoveUp(...)`, `TextEdit? MoveDown(...)`, `TextEdit? Join(...)`, `TextEdit? Sort(string text, int start, int length, bool descending, CultureInfo culture)`, `TextEdit? RemoveDuplicates(...)`, `TextEdit? TrimTrailing(...)` (null: nothing to change); `EditorMenus.LinesMenu(TextEditor editor)`; `MicaPadWindow.RunLineOperation(Func<string, int, int, TextEdit?> operation)` (internal).

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/LineOperationsTests.cs`:

```csharp
using System.Globalization;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Duplicate, move, join, sort, dedupe and trim over lines, keeping line endings.</summary>
    public class LineOperationsTests
    {
        private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

        private static string Apply(string text, TextEdit? edit) =>
            edit is TextEdit e ? text.Remove(e.Offset, e.Length).Insert(e.Offset, e.Text) : text;

        [Fact]
        public void Duplicate_copies_the_caret_line_below_and_keeps_the_column()
        {
            string text = "one\r\ntwo\r\nthree";
            var edit = LineOperations.Duplicate(text, 6, 0);            // caret in "two"
            Assert.Equal("one\r\ntwo\r\ntwo\r\nthree", Apply(text, edit));
            Assert.Equal(11, edit.SelectionStart);                      // same column on the copy
        }

        [Fact]
        public void Duplicate_of_the_last_line_uses_the_notes_line_ending()
        {
            Assert.Equal("a\nb\nb", Apply("a\nb", LineOperations.Duplicate("a\nb", 2, 0)));
            Assert.Equal("solo\r\nsolo", Apply("solo", LineOperations.Duplicate("solo", 0, 0)));
        }

        [Fact]
        public void Duplicate_of_a_selection_inserts_a_copy_after_it_and_selects_the_copy()
        {
            var edit = LineOperations.Duplicate("abc", 0, 2);
            Assert.Equal("ababc", Apply("abc", edit));
            Assert.Equal((2, 2), (edit.SelectionStart, edit.SelectionLength));
        }

        [Theory]
        [InlineData("a\nb\nc", 2, 0, "b\na\nc", 0)]
        [InlineData("a\r\nb\r\nc", 3, 0, "b\r\na\r\nc", 0)]
        [InlineData("a\nb\nc", 4, 0, "a\nc\nb", 2)]
        [InlineData("a\nb", 0, 0, null, 0)]
        public void Move_up(string text, int caret, int length, string? expected, int newCaret)
        {
            var edit = LineOperations.MoveUp(text, caret, length);
            if (expected == null) { Assert.Null(edit); return; }
            Assert.Equal(expected, Apply(text, edit));
            Assert.Equal(newCaret, edit!.Value.SelectionStart);
        }

        [Theory]
        [InlineData("a\nb\nc", 0, 0, "b\na\nc", 2)]
        [InlineData("a\r\nb", 0, 0, "b\r\na", 3)]
        [InlineData("a\nb", 2, 0, null, 0)]
        public void Move_down(string text, int caret, int length, string? expected, int newCaret)
        {
            var edit = LineOperations.MoveDown(text, caret, length);
            if (expected == null) { Assert.Null(edit); return; }
            Assert.Equal(expected, Apply(text, edit));
            Assert.Equal(newCaret, edit!.Value.SelectionStart);
        }

        [Fact]
        public void Moving_a_multi_line_selection_moves_the_whole_block()
        {
            string text = "1\n2\n3\n4";
            var edit = LineOperations.MoveDown(text, 2, 3);             // "2\n3" selected
            Assert.Equal("1\n4\n2\n3", Apply(text, edit));
        }

        [Fact]
        public void Join_joins_selected_lines_or_this_line_with_the_next()
        {
            Assert.Equal("a b c", Apply("a\n  b  \nc", LineOperations.Join("a\n  b  \nc", 0, 9)));
            Assert.Equal("a b\nc", Apply("a\nb\nc", LineOperations.Join("a\nb\nc", 0, 0)));
            Assert.Null(LineOperations.Join("last", 0, 0));
        }

        [Fact]
        public void Sort_is_case_insensitive_stable_and_keeps_line_endings()
        {
            string text = "b\r\nA\r\na\r\nC";
            Assert.Equal("A\r\na\r\nb\r\nC", Apply(text, LineOperations.Sort(text, 0, 0, false, En)));
            Assert.Equal("C\r\nb\r\nA\r\na", Apply(text, LineOperations.Sort(text, 0, 0, true, En)));
        }

        [Fact]
        public void Sorting_keeps_a_trailing_newline_last()
        {
            Assert.Equal("a\nb\n", Apply("b\na\n", LineOperations.Sort("b\na\n", 0, 0, false, En)));
        }

        [Fact]
        public void Sort_only_touches_the_selected_lines_and_says_when_nothing_changes()
        {
            string text = "z\nb\na\ny";
            Assert.Equal("z\na\nb\ny", Apply(text, LineOperations.Sort(text, 2, 3, false, En)));
            Assert.Null(LineOperations.Sort("a\nb", 0, 0, false, En));
        }

        [Fact]
        public void Remove_duplicates_keeps_the_first_and_no_extra_newline()
        {
            Assert.Equal("a\nb", Apply("a\nb\na", LineOperations.RemoveDuplicates("a\nb\na", 0, 0)));
            Assert.Equal("a\n", Apply("a\na\n", LineOperations.RemoveDuplicates("a\na\n", 0, 0)));
            Assert.Null(LineOperations.RemoveDuplicates("a\nb", 0, 0));
        }

        [Fact]
        public void Removing_duplicates_keeps_blank_lines()
        {
            string text = "p\n\nq\n\nq";
            Assert.Equal("p\n\nq\n", Apply(text, LineOperations.RemoveDuplicates(text, 0, 0)));
        }

        [Fact]
        public void Trim_removes_trailing_spaces_and_tabs_only()
        {
            string text = "a  \r\n\tb\t\r\nc";
            Assert.Equal("a\r\n\tb\r\nc", Apply(text, LineOperations.TrimTrailing(text, 0, 0)));
            Assert.Null(LineOperations.TrimTrailing("clean\nlines", 0, 0));
        }

        [Fact]
        public void A_whole_document_operation_keeps_the_caret_on_its_line()
        {
            string text = "b  \na  \nc  ";
            var edit = LineOperations.TrimTrailing(text, 5, 0).GetValueOrDefault();   // caret on line 2, column 1
            Assert.Equal(0, edit.SelectionLength);
            Assert.Equal(3, edit.SelectionStart);                                     // "b\na\nc": line 2 starts at 2, column 1
        }
    }
}
```

In `tests/Kil0bitSystemMonitor.Tests/PadMenuTests.cs`:

1. Update the expected header arrays: the edit menu gains a *Lines* group after *Select all* (and after *Format* for a Markdown note). `The_editor_menu_lists_edit_then_find_items` expects:

```csharp
            Assert.Equal(new[] { "Undo", "Redo", "-", "Cut", "Copy", "Paste", "Delete", "Select all", "-", "Format", "Lines", "-", "Find", "Replace", "Go to line…" },
                         Headers(window.EditorMenu));
```

   (the preview menu is unchanged; any other test asserting the full header list gets `"Lines"` in the same place.)

2. Add:

```csharp
        [Fact]
        public void The_lines_menu_has_every_operation() => WithWindow((window, env) =>
        {
            window.RefreshEditorMenu();
            var lines = ItemOf(window.EditorMenu, "Lines");
            var headers = lines.Items.Cast<object>().Select(i => i is MenuItem m ? (string)m.Header : "-").ToArray();
            Assert.Equal(new[] { "Duplicate", "Move up", "Move down", "Join lines", "-", "Sort ascending", "Sort descending",
                                 "Remove duplicate lines", "Trim trailing whitespace" }, headers);
            Assert.Equal("Ctrl+D", lines.Items.OfType<MenuItem>().First().InputGestureText);
        });

        [Fact]
        public void Each_operation_is_one_undo_step() => WithWindow((window, env) =>
        {
            window.Editor.Document.Text = "b\na\nb\n";
            foreach (var (key, modifiers) in new[] { (Key.D, ModifierKeys.Control), (Key.Down, ModifierKeys.Control | ModifierKeys.Shift), (Key.J, ModifierKeys.Control) })
            {
                window.Editor.CaretOffset = 0;          // first line: every operation has something to do
                string before = window.Editor.Document.Text;
                Assert.True(window.HandleShortcut(key, modifiers));
                Assert.NotEqual(before, window.Editor.Document.Text);
                window.Editor.Undo();
                Assert.Equal(before, window.Editor.Document.Text);
            }
        });

        [Fact]
        public void Editing_shortcuts_ignore_a_focused_text_box() => WithWindow((window, env) =>
        {
            window.Editor.Document.Text = "one";
            window.FindBar.Open(replace: false);
            System.Windows.Input.Keyboard.Focus(window.FindBar.FindBox);

            if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox)
            {
                Assert.False(window.HandleShortcut(Key.D, ModifierKeys.Control));
                Assert.Equal("one", window.Editor.Document.Text);
            }
        });
```

(`Editing_shortcuts_ignore_a_focused_text_box` only asserts when WPF actually gave the unshown window's text box the keyboard focus; the rule itself is in `HandleShortcut` below. `PadMenuTests` has no `using System.Windows.Input;` yet: add it for `Key`/`ModifierKeys`. The Markdown check in `A_markdown_note_has_the_format_menu_and_a_json_file_does_not` stays as it is; a JSON tab's menu still has *Lines*.)

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~LineOperationsTests|FullyQualifiedName~PadMenuTests"`). Expected: build FAILS.

- [ ] **Step 3: Write `Services/Pad/LineOperations.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// notepad4-style line operations (spec 3.2). Each returns one <see cref="TextEdit"/> for the
    /// window to apply as a single undoable change, or null when there is nothing to change. They
    /// work on the whole lines the selection touches (the whole text for sort, dedupe and trim when
    /// nothing is selected) and keep every line break exactly as it was.
    /// </summary>
    public static class LineOperations
    {
        /// <summary>Duplicates the selection after itself (selecting the copy), or the caret's line below it (same column).</summary>
        public static TextEdit Duplicate(string text, int start, int length)
        {
            if (length > 0)
            {
                string selected = text.Substring(start, length);
                return new TextEdit(start + length, 0, selected, start + length, length);
            }
            var (lineStart, lineEnd) = TextLines.Block(text, start, 0);
            string line = text.Substring(lineStart, lineEnd - lineStart);
            int breakLength = TextLines.BreakLength(text, lineEnd);
            string newline = breakLength > 0 ? text.Substring(lineEnd, breakLength) : TextLines.NewlineOf(text);
            return new TextEdit(lineEnd, 0, newline + line, lineEnd + newline.Length + (start - lineStart), 0);
        }

        /// <summary>Moves the selected lines above the line before them; null on the first line.</summary>
        public static TextEdit? MoveUp(string text, int start, int length)
        {
            var (blockStart, blockEnd) = TextLines.Block(text, start, length);
            if (blockStart == 0) return null;
            int prevEnd = blockStart - TextLines.BreakBefore(text, blockStart);
            int prevStart = TextLines.LineStart(text, prevEnd);
            string block = text.Substring(blockStart, blockEnd - blockStart);
            string separator = text.Substring(prevEnd, blockStart - prevEnd);
            string previous = text.Substring(prevStart, prevEnd - prevStart);
            int shift = blockStart - prevStart;
            return new TextEdit(prevStart, blockEnd - prevStart, block + separator + previous, start - shift, length);
        }

        /// <summary>Moves the selected lines below the line after them; null on the last line.</summary>
        public static TextEdit? MoveDown(string text, int start, int length)
        {
            var (blockStart, blockEnd) = TextLines.Block(text, start, length);
            if (blockEnd >= text.Length) return null;
            int nextStart = blockEnd + TextLines.BreakLength(text, blockEnd);
            int nextEnd = TextLines.LineEnd(text, nextStart);
            string block = text.Substring(blockStart, blockEnd - blockStart);
            string separator = text.Substring(blockEnd, nextStart - blockEnd);
            string next = text.Substring(nextStart, nextEnd - nextStart);
            int shift = next.Length + separator.Length;
            return new TextEdit(blockStart, nextEnd - blockStart, next + separator + block, start + shift, length);
        }

        /// <summary>
        /// Joins the selected lines (or this line with the next) with one space, trimming the spaces
        /// around each join; empty lines disappear. Null when there is no next line.
        /// </summary>
        public static TextEdit? Join(string text, int start, int length)
        {
            var (blockStart, blockEnd) = TextLines.Block(text, start, length);
            var (lines, _) = TextLines.Split(text.Substring(blockStart, blockEnd - blockStart));
            if (lines.Count < 2)
            {
                if (blockEnd >= text.Length) return null;
                blockEnd = TextLines.LineEnd(text, blockEnd + TextLines.BreakLength(text, blockEnd));
                (lines, _) = TextLines.Split(text.Substring(blockStart, blockEnd - blockStart));
            }
            string first = lines[0].TrimEnd();
            string joined = first;
            foreach (string line in lines.Skip(1))
            {
                string part = line.Trim();
                if (part.Length > 0) joined += " " + part;
            }
            return length > 0
                ? new TextEdit(blockStart, blockEnd - blockStart, joined, blockStart, joined.Length)
                : new TextEdit(blockStart, blockEnd - blockStart, joined, blockStart + first.Length, 0);
        }

        /// <summary>
        /// Sorts lines, case-insensitive in <paramref name="culture"/>, stable (equal lines keep their
        /// order). A final empty line (the text's last newline) stays last. Null when already sorted.
        /// </summary>
        public static TextEdit? Sort(string text, int start, int length, bool descending, CultureInfo culture)
        {
            var comparer = StringComparer.Create(culture, ignoreCase: true);
            return Rewrite(text, start, length, lines =>
            {
                bool keepLast = lines.Count > 1 && lines[^1].Length == 0;
                var body = keepLast ? lines.Take(lines.Count - 1) : lines;
                var sorted = (descending ? body.OrderByDescending(l => l, comparer) : body.OrderBy(l => l, comparer)).ToList();
                if (keepLast) sorted.Add("");
                return sorted;
            });
        }

        /// <summary>Removes repeated lines, keeping the first of each; blank lines are never removed.</summary>
        public static TextEdit? RemoveDuplicates(string text, int start, int length)
        {
            var (blockStart, blockEnd, whole) = Range(text, start, length);
            var (lines, breaks) = TextLines.Split(text.Substring(blockStart, blockEnd - blockStart));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var keptLines = new List<string>();
            var keptBreaks = new List<string>();
            for (int i = 0; i < lines.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]) && !seen.Add(lines[i])) continue;
                keptLines.Add(lines[i]);
                keptBreaks.Add(i < breaks.Count ? breaks[i] : "");
            }
            if (keptLines.Count == lines.Count) return null;
            // The block never ended with a break: neither may the kept lines.
            keptBreaks[^1] = "";
            string block = string.Concat(keptLines.Select((l, i) => l + keptBreaks[i]));
            return Result(text, start, length, blockStart, blockEnd, block, whole);
        }

        /// <summary>Removes spaces and tabs at the end of each line.</summary>
        public static TextEdit? TrimTrailing(string text, int start, int length) =>
            Rewrite(text, start, length, lines => lines.Select(l => l.TrimEnd(' ', '\t')).ToList());

        /// <summary>The lines to work on: the selection's lines, or the whole text when nothing is selected.</summary>
        private static (int Start, int End, bool Whole) Range(string text, int start, int length)
        {
            if (length == 0) return (0, text.Length, true);
            var (s, e) = TextLines.Block(text, start, length);
            return (s, e, false);
        }

        /// <summary>Applies a per-line rewrite that keeps the number of lines, then builds the edit.</summary>
        private static TextEdit? Rewrite(string text, int start, int length, Func<List<string>, List<string>> rewrite)
        {
            var (blockStart, blockEnd, whole) = Range(text, start, length);
            var (lines, breaks) = TextLines.Split(text.Substring(blockStart, blockEnd - blockStart));
            var rewritten = rewrite(lines);
            if (rewritten.SequenceEqual(lines, StringComparer.Ordinal)) return null;
            string block = TextLines.Join(rewritten, breaks);
            return Result(text, start, length, blockStart, blockEnd, block, whole);
        }

        /// <summary>
        /// The edit replacing the block. A selection selects the new block; with nothing selected the
        /// caret stays on the same line number, at the same column where that line is long enough.
        /// </summary>
        private static TextEdit Result(string text, int start, int length, int blockStart, int blockEnd, string block, bool whole)
        {
            if (!whole) return new TextEdit(blockStart, blockEnd - blockStart, block, blockStart, block.Length);

            int lineIndex = 0;
            for (int i = 0; i < start && i < text.Length; i++)
                if (text[i] == '\n' || (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'))) lineIndex++;
            int column = start - TextLines.LineStart(text, start);

            var (lines, breaks) = TextLines.Split(block);
            lineIndex = Math.Min(lineIndex, lines.Count - 1);
            int offset = 0;
            for (int i = 0; i < lineIndex; i++) offset += lines[i].Length + breaks[i].Length;
            int caret = blockStart + offset + Math.Min(column, lines[lineIndex].Length);
            return new TextEdit(blockStart, blockEnd - blockStart, block, caret, 0);
        }
    }
}
```

Note: `A_whole_document_operation_keeps_the_caret_on_its_line`: "b  \na  \nc  " with the caret at offset 5 (line 2, column 1) becomes "b\na\nc"; line 2 starts at offset 2, so the caret goes to 3.

- [ ] **Step 4: Lines submenu in `Pad/EditorMenus.cs`** (after `FormatMenu`):

```csharp
        /// <summary>Lines ▸ (spec 3.2): the line operations, each one undoable edit.</summary>
        public static MenuItem LinesMenu(TextEditor editor)
        {
            var lines = new MenuItem { Header = "Lines" };
            void Add(string header, string? gesture, Func<string, int, int, TextEdit?> operation) =>
                lines.Items.Add(Item(header, gesture, () => Run(editor, operation)));

            Add("Duplicate", "Ctrl+D", (t, s, l) => LineOperations.Duplicate(t, s, l));
            Add("Move up", "Ctrl+Shift+Up", LineOperations.MoveUp);
            Add("Move down", "Ctrl+Shift+Down", LineOperations.MoveDown);
            Add("Join lines", "Ctrl+J", LineOperations.Join);
            lines.Items.Add(new Separator());
            Add("Sort ascending", null, (t, s, l) => LineOperations.Sort(t, s, l, false, System.Globalization.CultureInfo.CurrentCulture));
            Add("Sort descending", null, (t, s, l) => LineOperations.Sort(t, s, l, true, System.Globalization.CultureInfo.CurrentCulture));
            Add("Remove duplicate lines", null, LineOperations.RemoveDuplicates);
            Add("Trim trailing whitespace", null, LineOperations.TrimTrailing);
            return lines;
        }

        /// <summary>Runs a line operation on the editor's text and selection; a null result changes nothing.</summary>
        public static void Run(TextEditor editor, Func<string, int, int, TextEdit?> operation)
        {
            if (editor.IsReadOnly) return;
            if (operation(editor.Document.Text, editor.SelectionStart, editor.SelectionLength) is TextEdit edit)
                ApplyEdit(editor, edit);
        }
```

- [ ] **Step 5: The window**

In `Pad/MicaPadWindow.xaml.cs`:

1. In `FillEditorMenu`, after the Format block (and before the Find separator), add `menu.Items.Add(LinesMenu(editor));` — directly after the Format item when there is one; otherwise after its own separator. Concretely:

```csharp
            menu.Items.Add(new Separator());
            if (ReferenceEquals(editor, Editor) && ReferenceEquals(_resolved.Effective, PadLanguages.Markdown))
                menu.Items.Add(FormatMenu(editor));
            menu.Items.Add(LinesMenu(editor));
```

   replacing the Format block from Task 1.

2. In `HandleShortcut`, before the final `else return false;`, add the editing keys, which do nothing while a text box has the focus:

```csharp
            else if (EditingKeysAllowed && ctrl && key == Key.D) Run(Editor, (t, s, l) => LineOperations.Duplicate(t, s, l));
            else if (EditingKeysAllowed && ctrlShift && key == Key.Up) Run(Editor, LineOperations.MoveUp);
            else if (EditingKeysAllowed && ctrlShift && key == Key.Down) Run(Editor, LineOperations.MoveDown);
            else if (EditingKeysAllowed && ctrl && key == Key.J) Run(Editor, LineOperations.Join);
```

   and the property:

```csharp
        /// <summary>
        /// Shortcuts that edit the note run only while no text box (find, replace, go to line,
        /// rename) has the keyboard focus, so Ctrl+D there never duplicates a line of the note.
        /// </summary>
        private static bool EditingKeysAllowed => Keyboard.FocusedElement is not System.Windows.Controls.TextBox;
```

- [ ] **Step 6: Run the focused tests, then the whole suite** — expected PASS.

- [ ] **Step 7: Document it**

In `GUIDE.md`, `### Editing helpers` (Task 2), add after the Auto-close paragraph:

```markdown
**Lines**: **Ctrl+D** duplicates the line (or the selection), **Ctrl+Shift+↑/↓** moves the
selected lines, **Ctrl+J** joins them with one space. Right-click → **Lines** also sorts
(ascending or descending, ignoring case), removes duplicate lines (keeping the first; blank lines
stay) and trims trailing spaces — on the selected lines, or the whole note when nothing is
selected. Line endings are kept, and each is a single **Ctrl+Z**.

```

In `README.md`, the *Inside MicaPad* key table, add a row after the **Ctrl+G** row:

```markdown
| **Ctrl+D** · **Ctrl+Shift+↑/↓** · **Ctrl+J** | Duplicate line · move lines · join lines |
```

and in the Thai key table (`ภายใน MicaPad:`), the matching row after its **Ctrl+G** row:

```markdown
| **Ctrl+D** · **Ctrl+Shift+↑/↓** · **Ctrl+J** | ทำซ้ำบรรทัด · ย้ายบรรทัด · รวมบรรทัด |
```

- [ ] **Step 8: Commit**

```bash
git add Services/Pad/LineOperations.cs Pad/EditorMenus.cs Pad/MicaPadWindow.xaml.cs GUIDE.md README.md tests/Kil0bitSystemMonitor.Tests/LineOperationsTests.cs tests/Kil0bitSystemMonitor.Tests/PadMenuTests.cs
git commit -m "feat(pad): line operations - duplicate, move, join, sort, dedupe, trim" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

### Task 4: Bookmarks

**Files:**
- Modify: `Services/Pad/NoteMeta.cs` (`TabViewState.Bookmarks`), `Services/Pad/PadWorkspace.cs` (`SetTabViewState` keeps bookmarks; `SetBookmarks`)
- Create: `Pad/BookmarkController.cs`, `Pad/BookmarkMargin.cs`
- Modify: `Pad/MicaPadWindow.xaml.cs` (controller, margin, keys, `☰` item, save and restore), `GUIDE.md`, `README.md`
- Create: `tests/Kil0bitSystemMonitor.Tests/PadBookmarkTests.cs`

**Interfaces:**
- Produces: `TabViewState.Bookmarks` (`List<int>?`, 1-based line numbers); `PadWorkspace.SetBookmarks(OpenNote note, IReadOnlyList<int> lines)`; `internal sealed class BookmarkController` (`Toggle(TextDocument, int line)`, `Clear(TextDocument)`, `Lines(TextDocument)` → sorted distinct `IReadOnlyList<int>`, `Load(TextDocument, IEnumerable<int>)`, `Next(TextDocument, int line)` / `Previous(...)` → `int?`, `event Action? Changed`); `internal sealed class BookmarkMargin : AbstractMargin`; `MicaPadWindow`: `ToggleBookmark()`, `NextBookmark()`, `PreviousBookmark()`, `ClearBookmarks()`, `BookmarkLines` (internal, for tests).

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/PadBookmarkTests.cs`:

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
    /// <summary>Bookmarks: toggling, jumping, following edits, and surviving a restart.</summary>
    public class PadBookmarkTests
    {
        private static void WithEnv(Action<PadTestEnv, Func<MicaPadWindow>> test) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var windows = new System.Collections.Generic.List<MicaPadWindow>();
            MicaPadWindow Open()
            {
                var window = new MicaPadWindow(env.Workspace, new AppConfig());
                window.LoadSession();
                windows.Add(window);
                return window;
            }
            try
            {
                test(env, Open);
            }
            finally
            {
                foreach (var w in windows) w.CloseForExit();
            }
        });

        private static void GoToLine(MicaPadWindow window, int line) =>
            window.Editor.CaretOffset = window.Editor.Document.GetLineByNumber(line).Offset;

        [Fact]
        public void Ctrl_f2_toggles_a_bookmark_on_the_caret_line() => WithEnv((env, open) =>
        {
            var window = open();
            window.Editor.Document.Text = "a\nb\nc";
            GoToLine(window, 2);

            Assert.True(window.HandleShortcut(Key.F2, ModifierKeys.Control));
            Assert.Equal(new[] { 2 }, window.BookmarkLines);

            window.HandleShortcut(Key.F2, ModifierKeys.Control);
            Assert.Empty(window.BookmarkLines);
        });

        [Fact]
        public void F2_and_shift_f2_jump_and_wrap_around() => WithEnv((env, open) =>
        {
            var window = open();
            window.Editor.Document.Text = "1\n2\n3\n4\n5";
            foreach (int line in new[] { 2, 4 })
            {
                GoToLine(window, line);
                window.ToggleBookmark();
            }
            GoToLine(window, 1);

            window.HandleShortcut(Key.F2, ModifierKeys.None);
            Assert.Equal(2, window.Editor.TextArea.Caret.Line);
            window.HandleShortcut(Key.F2, ModifierKeys.None);
            Assert.Equal(4, window.Editor.TextArea.Caret.Line);
            window.HandleShortcut(Key.F2, ModifierKeys.None);
            Assert.Equal(2, window.Editor.TextArea.Caret.Line);        // wrapped
            window.HandleShortcut(Key.F2, ModifierKeys.Shift);
            Assert.Equal(4, window.Editor.TextArea.Caret.Line);        // wrapped backwards
        });

        [Fact]
        public void Bookmarks_move_with_the_text() => WithEnv((env, open) =>
        {
            var window = open();
            window.Editor.Document.Text = "a\nb\nc";
            GoToLine(window, 3);
            window.ToggleBookmark();

            window.Editor.Document.Insert(0, "new 1\nnew 2\n");
            Assert.Equal(new[] { 5 }, window.BookmarkLines);

            window.Editor.Document.Remove(0, "new 1\n".Length);
            Assert.Equal(new[] { 4 }, window.BookmarkLines);
        });

        [Fact]
        public void A_deleted_bookmarked_line_does_not_duplicate_another() => WithEnv((env, open) =>
        {
            var window = open();
            window.Editor.Document.Text = "a\nb\nc";
            GoToLine(window, 2);
            window.ToggleBookmark();
            GoToLine(window, 3);
            window.ToggleBookmark();

            var line2 = window.Editor.Document.GetLineByNumber(2);
            window.Editor.Document.Remove(line2.Offset, line2.TotalLength);   // delete "b\n"

            Assert.Equal(new[] { 2 }, window.BookmarkLines);
        });

        [Fact]
        public void Clear_removes_them_all() => WithEnv((env, open) =>
        {
            var window = open();
            window.Editor.Document.Text = "a\nb";
            window.ToggleBookmark();
            window.ClearBookmarks();
            Assert.Empty(window.BookmarkLines);
        });

        [Fact]
        public void Bookmarks_survive_a_restart_on_their_moved_line() => WithEnv((env, open) =>
        {
            var first = open();
            first.Editor.Document.Text = "a\nb\nc";
            GoToLine(first, 2);
            first.ToggleBookmark();
            first.Editor.Document.Insert(0, "top\n");       // the bookmark moves to line 3
            first.PrepareForExit();

            Assert.Equal(new[] { 3 }, env.Workspace.Session.Tabs[env.Workspace.Active!.Id].Bookmarks);

            // A second window over the same workspace rebuilds its documents from the session, as a
            // restart does. (The helper closes both at the end; a window must not be closed twice.)
            var second = open();
            Assert.Equal(new[] { 3 }, second.BookmarkLines);
        });

        [Fact]
        public void A_saved_bookmark_past_the_end_is_dropped() => WithEnv((env, open) =>
        {
            var window = open();
            var note = env.Workspace.Active!;
            window.Editor.Document.Text = "only line";
            env.Workspace.SetBookmarks(note, new[] { 1, 7 });

            var again = open();                          // rebuilds the document and loads the saved lines
            Assert.Equal(new[] { 1 }, again.BookmarkLines);
        });

        [Fact]
        public void The_view_state_keeps_bookmarks() => WithEnv((env, open) =>
        {
            open();
            var note = env.Workspace.Active!;
            env.Workspace.SetBookmarks(note, new[] { 3 });
            env.Workspace.SetTabViewState(note, 5, 10.0);
            Assert.Equal(new[] { 3 }, env.Workspace.Session.Tabs[note.Id].Bookmarks);
        });
    }
}
```

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~PadBookmarkTests"`). Expected: build FAILS.

- [ ] **Step 3: Session and workspace**

In `Services/Pad/NoteMeta.cs`, `TabViewState`, add:

```csharp
        /// <summary>Bookmarked lines (1-based), or null when there are none. Older sessions have none.</summary>
        public List<int>? Bookmarks { get; set; }
```

In `Services/Pad/PadWorkspace.cs`, replace `SetTabViewState` with:

```csharp
        public void SetTabViewState(OpenNote note, int caretOffset, double verticalOffset)
        {
            Session.Tabs.TryGetValue(note.Id, out var previous);
            Session.Tabs[note.Id] = new TabViewState
            {
                CaretOffset = caretOffset,
                VerticalOffset = verticalOffset,
                Bookmarks = previous?.Bookmarks,
            };
        }

        /// <summary>Records a tab's bookmarked lines (1-based) for the session; none clears them.</summary>
        public void SetBookmarks(OpenNote note, IReadOnlyList<int> lines)
        {
            if (!Session.Tabs.TryGetValue(note.Id, out var view))
                Session.Tabs[note.Id] = view = new TabViewState();
            view.Bookmarks = lines.Count == 0 ? null : lines.ToList();
        }
```

(`System.Linq` is already used in this file; add the using if not.)

- [ ] **Step 4: Write `Pad/BookmarkController.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.AvalonEdit.Document;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Bookmarks per document, as text anchors at line starts so they move with the text. A line
    /// that is deleted takes its anchor to the neighbouring line; two anchors on one line count once.
    /// </summary>
    internal sealed class BookmarkController
    {
        private readonly Dictionary<TextDocument, List<TextAnchor>> _anchors = new();

        /// <summary>Raised after a toggle, clear or load, so the margin repaints and the session saves.</summary>
        public event Action? Changed;

        /// <summary>Bookmarked lines of a document, sorted, each once.</summary>
        public IReadOnlyList<int> Lines(TextDocument document) =>
            _anchors.TryGetValue(document, out var list)
                ? list.Select(a => a.Line).Distinct().OrderBy(l => l).ToList()
                : Array.Empty<int>();

        public void Toggle(TextDocument document, int line)
        {
            var list = ListOf(document);
            int removed = list.RemoveAll(a => a.Line == line);
            if (removed == 0) list.Add(AnchorAt(document, line));
            Changed?.Invoke();
        }

        public void Clear(TextDocument document)
        {
            _anchors.Remove(document);
            Changed?.Invoke();
        }

        /// <summary>Loads saved lines; any past the end of the text are dropped.</summary>
        public void Load(TextDocument document, IEnumerable<int> lines)
        {
            var list = ListOf(document);
            list.Clear();
            foreach (int line in lines.Distinct())
                if (line >= 1 && line <= document.LineCount) list.Add(AnchorAt(document, line));
            Changed?.Invoke();
        }

        /// <summary>The next bookmarked line after <paramref name="line"/>, wrapping to the first; null without bookmarks.</summary>
        public int? Next(TextDocument document, int line)
        {
            var lines = Lines(document);
            if (lines.Count == 0) return null;
            foreach (int l in lines) if (l > line) return l;
            return lines[0];
        }

        /// <summary>The previous bookmarked line before <paramref name="line"/>, wrapping to the last; null without bookmarks.</summary>
        public int? Previous(TextDocument document, int line)
        {
            var lines = Lines(document);
            if (lines.Count == 0) return null;
            for (int i = lines.Count - 1; i >= 0; i--) if (lines[i] < line) return lines[i];
            return lines[^1];
        }

        /// <summary>Forgets a document whose tab closed.</summary>
        public void Forget(TextDocument document) => _anchors.Remove(document);

        private List<TextAnchor> ListOf(TextDocument document)
        {
            if (!_anchors.TryGetValue(document, out var list)) _anchors[document] = list = new List<TextAnchor>();
            return list;
        }

        private static TextAnchor AnchorAt(TextDocument document, int line)
        {
            var anchor = document.CreateAnchor(document.GetLineByNumber(line).Offset);
            anchor.MovementType = AnchorMovementType.BeforeInsertion;
            anchor.SurviveDeletion = true;
            return anchor;
        }
    }
}
```

- [ ] **Step 5: Write `Pad/BookmarkMargin.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>A narrow margin left of the line numbers with an accent dot on each bookmarked line.</summary>
    internal sealed class BookmarkMargin : AbstractMargin
    {
        private readonly Func<IReadOnlyList<int>> _lines;
        private readonly Func<PadPalette> _palette;

        public BookmarkMargin(Func<IReadOnlyList<int>> lines, Func<PadPalette> palette)
        {
            _lines = lines;
            _palette = palette;
        }

        protected override Size MeasureOverride(Size availableSize) => new(12, 0);

        protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
        {
            if (oldTextView != null) oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView != null) newTextView.VisualLinesChanged += OnVisualLinesChanged;
            InvalidateVisual();
        }

        private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

        protected override void OnRender(DrawingContext drawingContext)
        {
            var view = TextView;
            if (view == null || !view.VisualLinesValid) return;
            var lines = new HashSet<int>(_lines());
            if (lines.Count == 0) return;

            var brush = PadThemeApplier.ToBrush(_palette().Accent);
            foreach (var visual in view.VisualLines)
            {
                if (!lines.Contains(visual.FirstDocumentLine.LineNumber)) continue;
                double y = visual.VisualTop - view.VerticalOffset + visual.Height / 2;
                drawingContext.DrawEllipse(brush, null, new Point(6, y), 3.5, 3.5);
            }
        }
    }
}
```

- [ ] **Step 6: The window**

In `Pad/MicaPadWindow.xaml.cs`:

1. Fields: `private readonly BookmarkController _bookmarks = new();` and `private BookmarkMargin _bookmarkMargin = null!;`.
2. In the constructor, after the `_autoClose = …` line:

```csharp
            _bookmarkMargin = new BookmarkMargin(() => _bookmarks.Lines(Editor.Document), () => _palette);
            Editor.TextArea.LeftMargins.Insert(0, _bookmarkMargin);
            _bookmarks.Changed += OnBookmarksChanged;
```

3. At the end of `ApplyTheme()`: `_bookmarkMargin?.InvalidateVisual();` (the field is assigned after the first `ApplyTheme` call, hence `?.`).
4. In `EnsureDocument`, after `_docs[note.Id] = document;` restore saved bookmarks:

```csharp
            if (_workspace.Session.Tabs.TryGetValue(note.Id, out var view) && view.Bookmarks != null)
                _bookmarks.Load(document, view.Bookmarks);
```

5. In `OnOpenChanged`, when a note leaves (`e.OldItems`), before `_docs.Remove(note.Id)`: `if (_docs.TryGetValue(note.Id, out var gone)) _bookmarks.Forget(gone);`.
6. In `SaveViewState(OpenNote note)`, after the `SetTabViewState` call (make it a block body): `_workspace.SetBookmarks(note, _bookmarks.Lines(EnsureDocument(note)));` — and make sure `CaptureViewState` (used by `PrepareForExit`) goes through `SaveViewState` for the shown note; if it calls `SetTabViewState` directly, add the same `SetBookmarks` line there.
7. Members:

```csharp
        // ---- bookmarks -----------------------------------------------------------------------

        /// <summary>The shown tab's bookmarked lines; for tests.</summary>
        internal IReadOnlyList<int> BookmarkLines => _bookmarks.Lines(Editor.Document);

        /// <summary>Ctrl+F2: bookmark the caret line, or remove its bookmark.</summary>
        internal void ToggleBookmark() => _bookmarks.Toggle(Editor.Document, Editor.TextArea.Caret.Line);

        /// <summary>F2: the next bookmark, wrapping.</summary>
        internal void NextBookmark() => GoToBookmark(_bookmarks.Next(Editor.Document, Editor.TextArea.Caret.Line));

        /// <summary>Shift+F2: the previous bookmark, wrapping.</summary>
        internal void PreviousBookmark() => GoToBookmark(_bookmarks.Previous(Editor.Document, Editor.TextArea.Caret.Line));

        /// <summary>☰ Clear bookmarks, for the shown tab.</summary>
        internal void ClearBookmarks() => _bookmarks.Clear(Editor.Document);

        private void GoToBookmark(int? line)
        {
            if (line is int l) GoToLine(l);
        }

        private void OnBookmarksChanged()
        {
            _bookmarkMargin.InvalidateVisual();
            if (_shown == null) return;
            _workspace.SetBookmarks(_shown, _bookmarks.Lines(Editor.Document));
            _workspace.SaveSession();
        }
```

   (`GoToLine(int)` already exists: it moves the caret to the line start and scrolls there.)

7b. `ReplaceText` (reload from the file, line-ending conversion, restoring a history version) replaces the whole text, which would pile every anchor onto line 1. Keep the line numbers instead — wrap the replace:

```csharp
            var marked = _bookmarks.Lines(document);
            // ... the existing try/finally that replaces the text ...
            if (marked.Count > 0) _bookmarks.Load(document, marked);   // same line numbers; past the end dropped
```

   and add to `PadBookmarkTests`:

```csharp
        [Fact]
        public void Replacing_the_whole_text_keeps_bookmark_line_numbers() => WithEnv((env, open) =>
        {
            var window = open();
            window.Editor.Document.Text = "a\nb\nc";
            GoToLine(window, 2);
            window.ToggleBookmark();

            window.ReplaceShownText("x\ny\nz\nw");
            Assert.Equal(new[] { 2 }, window.BookmarkLines);
        });
```

   with, in the window, `internal void ReplaceShownText(string text) { if (_shown != null) ReplaceText(_shown, text, markUnsaved: true); }` (for tests).

8. In `HandleShortcut`, before the editing keys from Task 3:

```csharp
            else if (ctrl && key == Key.F2) ToggleBookmark();
            else if (modifiers == ModifierKeys.None && key == Key.F2) NextBookmark();
            else if (modifiers == ModifierKeys.Shift && key == Key.F2) PreviousBookmark();
```

9. In `OnMenuButtonClick`, after the *History* item: `menu.Items.Add(Item("Clear bookmarks", null, ClearBookmarks));`

Note: `_bookmarks.Load` in `EnsureDocument` raises `Changed` while `_shown` may still be another note; `OnBookmarksChanged` writes the *shown* note's lines, which have not changed, so this is harmless. It must not call `EnsureDocument` recursively — it only reads `Editor.Document`.

- [ ] **Step 7: Run the focused tests, then the whole suite** — expected PASS.

- [ ] **Step 8: Document it**

In `GUIDE.md`, `### Editing helpers`, add:

```markdown
**Bookmarks**: **Ctrl+F2** marks the caret line with a dot in the margin (again to remove it);
**F2** / **Shift+F2** jump to the next / previous one, wrapping around. They move with the text as
you edit, and each tab keeps its own across restarts. **☰ → Clear bookmarks** removes them.

```

In `README.md`, the *Inside MicaPad* key table, after the row added in Task 3:

```markdown
| **Ctrl+F2** · **F2** / **Shift+F2** | Toggle bookmark · next / previous bookmark |
```

Thai table, after its Task 3 row:

```markdown
| **Ctrl+F2** · **F2** / **Shift+F2** | เพิ่ม/ลบบุ๊กมาร์ก · บุ๊กมาร์กถัดไป / ก่อนหน้า |
```

- [ ] **Step 9: Commit**

```bash
git add Services/Pad/NoteMeta.cs Services/Pad/PadWorkspace.cs Pad/BookmarkController.cs Pad/BookmarkMargin.cs Pad/MicaPadWindow.xaml.cs GUIDE.md README.md tests/Kil0bitSystemMonitor.Tests/PadBookmarkTests.cs
git commit -m "feat(pad): bookmarks that follow the text and survive a restart" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

### Task 5: Mark occurrences

**Files:**
- Create: `Services/Pad/OccurrenceFinder.cs`, `Pad/OccurrenceHighlighter.cs`
- Modify: `Services/Pad/PadPalette.cs` (`Occurrence`), `Pad/MicaPadWindow.xaml` (status text), `Pad/MicaPadWindow.xaml.cs`, `GUIDE.md`
- Create: `tests/Kil0bitSystemMonitor.Tests/OccurrenceTests.cs`
- Modify: `tests/Kil0bitSystemMonitor.Tests/PadPaletteTests.cs` (count 49 → 50)

**Interfaces:**
- Produces: `OccurrenceFinder.Cap` (10,000), `OccurrenceFinder.IsWholeWordSelection(string text, int start, int length)`, `OccurrenceFinder.FindAll(string text, string word)` → `(IReadOnlyList<int> Offsets, bool Capped)`, `OccurrenceFinder.Describe(int count, bool capped)`; `PadPalette.Occurrence`; `internal sealed class OccurrenceHighlighter : IBackgroundRenderer` (`Offsets`, `Length`, `Fill`); `MicaPadWindow.RefreshOccurrences()` (internal), `OccurrenceText` (XAML).

- [ ] **Step 1: Write the failing tests**

Create `tests/Kil0bitSystemMonitor.Tests/OccurrenceTests.cs`:

```csharp
using System;
using System.Linq;
using System.Windows;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Marking every occurrence of the selected word.</summary>
    public class OccurrenceTests
    {
        [Theory]
        [InlineData("cat cat category cat", 0, 3, true)]
        [InlineData("cat cat category cat", 8, 3, false)]     // "cat" inside "category"
        [InlineData("cat", 0, 2, false)]                       // part of a word
        [InlineData("a b", 0, 3, false)]                       // not one word
        [InlineData("snake_case x", 0, 10, true)]
        [InlineData("x", 0, 0, false)]
        public void Only_exactly_one_whole_word_counts(string text, int start, int length, bool expected)
        {
            Assert.Equal(expected, OccurrenceFinder.IsWholeWordSelection(text, start, length));
        }

        [Fact]
        public void Occurrences_are_whole_words_and_case_sensitive()
        {
            var (offsets, capped) = OccurrenceFinder.FindAll("cat Cat cat_x cat cats cat", "cat");
            Assert.Equal(new[] { 0, 14, 23 }, offsets);
            Assert.False(capped);
        }

        [Fact]
        public void Counting_stops_at_ten_thousand()
        {
            string text = string.Join(" ", Enumerable.Repeat("w", OccurrenceFinder.Cap + 5));
            var (offsets, capped) = OccurrenceFinder.FindAll(text, "w");
            Assert.Equal(OccurrenceFinder.Cap, offsets.Count);
            Assert.True(capped);
        }

        [Theory]
        [InlineData(1, false, "1 match")]
        [InlineData(5, false, "5 matches")]
        [InlineData(1234, false, "1,234 matches")]
        [InlineData(10000, true, "10,000+ matches")]
        public void Status_text(int count, bool capped, string expected)
        {
            Assert.Equal(expected, OccurrenceFinder.Describe(count, capped));
        }

        [Fact]
        public void Selecting_a_word_marks_it_and_counts_in_the_status_bar() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "cat cat category cat";
            window.Editor.Select(0, 3);
            window.RefreshOccurrences();

            Assert.Equal(Visibility.Visible, window.OccurrenceText.Visibility);
            Assert.Equal("3 matches", window.OccurrenceText.Text);
            Assert.Equal(new[] { 0, 4, 17 }, window.OccurrenceMarks.Offsets);

            window.Editor.Select(8, 3);        // inside "category": not a whole word
            window.RefreshOccurrences();
            Assert.Equal(Visibility.Collapsed, window.OccurrenceText.Visibility);
            Assert.Empty(window.OccurrenceMarks.Offsets);
        });

        [Fact]
        public void A_note_over_the_size_limit_marks_nothing() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "cat " + new string('x', PadLanguages.MaxFormattedChars);
            window.Editor.Select(0, 3);
            window.RefreshOccurrences();
            Assert.Empty(window.OccurrenceMarks.Offsets);
            Assert.Equal(Visibility.Collapsed, window.OccurrenceText.Visibility);
        });

        [Fact]
        public void The_marks_follow_the_theme() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.ToggleTheme();
            var fill = Assert.IsAssignableFrom<System.Windows.Media.SolidColorBrush>(window.OccurrenceMarks.Fill).Color;
            var expected = PadPalette.Light.Occurrence;
            Assert.Equal(System.Windows.Media.Color.FromArgb(expected.A, expected.R, expected.G, expected.B), fill);
        });
    }
}
```

In `PadPaletteTests.Every_color_is_listed_once_under_its_resource_key`, change `Assert.Equal(49, properties.Count);` to `Assert.Equal(50, properties.Count);`.

- [ ] **Step 2: Run to verify they fail** (`--filter "FullyQualifiedName~OccurrenceTests|FullyQualifiedName~PadPaletteTests"`). Expected: build FAILS.

- [ ] **Step 3: Write `Services/Pad/OccurrenceFinder.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Every whole-word, case-sensitive occurrence of a selected word (spec 3.4). A word is letters,
    /// digits and underscores; the selection must be exactly one whole word.
    /// </summary>
    public static class OccurrenceFinder
    {
        /// <summary>Counting stops here.</summary>
        public const int Cap = 10_000;

        /// <summary>Longer selections are not treated as a word.</summary>
        public const int MaxWordLength = 100;

        /// <summary>True when the selection is one whole word: word characters only, with no word character just outside it.</summary>
        public static bool IsWholeWordSelection(string text, int start, int length)
        {
            if (length <= 0 || length > MaxWordLength || start < 0 || start + length > text.Length) return false;
            for (int i = start; i < start + length; i++) if (!IsWordChar(text[i])) return false;
            bool leftOk = start == 0 || !IsWordChar(text[start - 1]);
            bool rightOk = start + length >= text.Length || !IsWordChar(text[start + length]);
            return leftOk && rightOk;
        }

        /// <summary>Offsets of every whole-word occurrence, stopping at <see cref="Cap"/>.</summary>
        public static (IReadOnlyList<int> Offsets, bool Capped) FindAll(string text, string word)
        {
            var offsets = new List<int>();
            int i = 0;
            while ((i = text.IndexOf(word, i, StringComparison.Ordinal)) >= 0)
            {
                int end = i + word.Length;
                bool whole = (i == 0 || !IsWordChar(text[i - 1])) && (end >= text.Length || !IsWordChar(text[end]));
                if (whole)
                {
                    if (offsets.Count == Cap) return (offsets, true);
                    offsets.Add(i);
                }
                i = end;
            }
            return (offsets, false);
        }

        /// <summary>"1 match", "5 matches", "10,000+ matches".</summary>
        public static string Describe(int count, bool capped)
        {
            if (capped) return Cap.ToString("N0", CultureInfo.InvariantCulture) + "+ matches";
            return count == 1 ? "1 match" : count.ToString("N0", CultureInfo.InvariantCulture) + " matches";
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
```

- [ ] **Step 4: The `Occurrence` color**

In `Services/Pad/PadPalette.cs`: property

```csharp
        /// <summary>The soft box behind each occurrence of the selected word (below find matches).</summary>
        public PadColor Occurrence { get; private init; }
```

`Dark`: `Occurrence = PadColor.Parse("#2E3FD2E4"),` — `Light`: `Occurrence = PadColor.Parse("#2406707C"),` — and `Pair(nameof(Occurrence), Occurrence),` in `Resources()`.

- [ ] **Step 5: Write `Pad/OccurrenceHighlighter.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

using Brush = System.Windows.Media.Brush;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Soft boxes behind every occurrence of the selected word, in the background layer so find
    /// matches and the selection draw on top. Only occurrences in the visible lines are drawn.
    /// </summary>
    internal sealed class OccurrenceHighlighter : IBackgroundRenderer
    {
        /// <summary>Occurrence offsets, sorted.</summary>
        public IReadOnlyList<int> Offsets { get; set; } = Array.Empty<int>();

        /// <summary>The word's length.</summary>
        public int Length { get; set; }

        public Brush Fill { get; set; } = PadThemeApplier.ToBrush(PadPalette.Dark.Occurrence);

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Offsets.Count == 0 || !textView.VisualLinesValid || textView.Document == null) return;
            var lines = textView.VisualLines;
            if (lines.Count == 0) return;

            int start = lines[0].FirstDocumentLine.Offset;
            int end = lines[lines.Count - 1].LastDocumentLine.EndOffset;
            int textLength = textView.Document.TextLength;

            int first = BinarySearchFirstAtOrAfter(start);
            for (int i = first; i < Offsets.Count && Offsets[i] <= end; i++)
            {
                if (Offsets[i] + Length > textLength) break;
                var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
                builder.AddSegment(textView, new TextSegment { StartOffset = Offsets[i], Length = Length });
                var geometry = builder.CreateGeometry();
                if (geometry != null) drawingContext.DrawGeometry(Fill, null, geometry);
            }
        }

        private int BinarySearchFirstAtOrAfter(int offset)
        {
            int lo = 0, hi = Offsets.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Offsets[mid] < offset) lo = mid + 1; else hi = mid;
            }
            return lo;
        }
    }
}
```

- [ ] **Step 6: Status text**

In `Pad/MicaPadWindow.xaml`, status bar, after the `CharsText` TextBlock:

```xml
                <TextBlock x:Name="OccurrenceText" VerticalAlignment="Center" FontSize="11.5" Foreground="{DynamicResource Pad.Muted}"
                           Margin="16,0,0,0" Visibility="Collapsed" />
```

- [ ] **Step 7: The window**

In `Pad/MicaPadWindow.xaml.cs`:

1. Fields: `private readonly OccurrenceHighlighter _occurrences = new();` and `private DispatcherTimer _occurrenceTimer = null!;`.
2. In the constructor, after the bookmark lines (Task 4):

```csharp
            Editor.TextArea.TextView.BackgroundRenderers.Add(_occurrences);
            _occurrenceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
            _occurrenceTimer.Tick += (s, e) =>
            {
                _occurrenceTimer.Stop();
                RefreshOccurrences();
            };
            Editor.TextArea.SelectionChanged += (s, e) =>
            {
                _occurrenceTimer.Stop();
                _occurrenceTimer.Start();
            };
```

3. In `ApplyTheme()`, after `FindBar.ApplyPalette(_palette);`: `_occurrences.Fill = PadThemeApplier.ToBrush(_palette.Occurrence); Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);` (add `using ICSharpCode.AvalonEdit.Rendering;` if needed).
4. In `ShowNote`, after `ApplyLanguage();`: `RefreshOccurrences();`.
5. In `Detach()`: `_occurrenceTimer?.Stop();`.
6. Members:

```csharp
        // ---- occurrences ---------------------------------------------------------------------

        /// <summary>The occurrence boxes; for tests.</summary>
        internal OccurrenceHighlighter OccurrenceMarks => _occurrences;

        /// <summary>
        /// Marks every occurrence of the selected word and counts them in the status bar (spec 3.4),
        /// or clears both when the selection is not exactly one whole word or the note is too large.
        /// </summary>
        internal void RefreshOccurrences()
        {
            var document = Editor.Document;
            string word = "";
            (IReadOnlyList<int> Offsets, bool Capped) found = (Array.Empty<int>(), false);

            if (document != null && document.TextLength <= PadLanguages.MaxFormattedChars && Editor.SelectionLength > 0)
            {
                string text = document.Text;
                if (OccurrenceFinder.IsWholeWordSelection(text, Editor.SelectionStart, Editor.SelectionLength))
                {
                    word = Editor.SelectedText;
                    found = OccurrenceFinder.FindAll(text, word);
                }
            }

            _occurrences.Offsets = found.Offsets;
            _occurrences.Length = word.Length;
            Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);

            if (found.Offsets.Count > 0)
            {
                OccurrenceText.Text = OccurrenceFinder.Describe(found.Offsets.Count, found.Capped);
                OccurrenceText.Visibility = Visibility.Visible;
            }
            else
            {
                OccurrenceText.Visibility = Visibility.Collapsed;
            }
        }
```

- [ ] **Step 8: Run the focused tests, then the whole suite** — expected PASS.

- [ ] **Step 9: Document it**

In `GUIDE.md`, `### Editing helpers`, add:

```markdown
**Occurrences**: select a whole word and every other place it appears (same case, whole words
only) gets a soft box; the status bar counts them (`5 matches`, up to `10,000+`).

```

- [ ] **Step 10: Commit**

```bash
git add Services/Pad/OccurrenceFinder.cs Services/Pad/PadPalette.cs Pad/OccurrenceHighlighter.cs Pad/MicaPadWindow.xaml Pad/MicaPadWindow.xaml.cs GUIDE.md tests/Kil0bitSystemMonitor.Tests/OccurrenceTests.cs tests/Kil0bitSystemMonitor.Tests/PadPaletteTests.cs
git commit -m "feat(pad): mark every occurrence of the selected word, counted in the status bar" -m "Co-Authored-By: <your model> <noreply@anthropic.com>"
```

---

## After the plan (controller)

1. Whole suite green (three consecutive runs, no hang); build with 0 warnings.
2. Deploy for the owner's e2e (standing rule): stop MicaStats, build Release into `bin\Release\net8.0-windows`, relaunch.
3. Owner's manual checks for Part 3 (spec manual item 4): notepad4 key habits (Ctrl+D, Ctrl+Shift+↑/↓, Ctrl+J, F2) behave; auto-close does not get in the way while typing prose.
