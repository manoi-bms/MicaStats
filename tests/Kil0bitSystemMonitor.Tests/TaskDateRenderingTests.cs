using System;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Task dates are visible beside Markdown while remaining outside its editable text.</summary>
    public class TaskDateRenderingTests
    {
        private static readonly TaskDateRecord Dates = new(
            "task-1",
            0,
            "write tests",
            new DateTimeOffset(2026, 10, 6, 8, 15, 0, TimeSpan.FromHours(7)),
            new DateTimeOffset(2026, 10, 6, 9, 45, 0, TimeSpan.FromHours(7)));

        private static TextEditor Render(
            string text,
            PadPalette palette,
            double width = 800,
            TaskDateRecord? dates = null,
            Action? changed = null)
        {
            var document = new TextDocument(text);
            if (changed != null) document.Changed += (_, _) => changed();
            var editor = new TextEditor
            {
                Document = document,
                WordWrap = true,
            };
            editor.TextArea.TextView.ElementGenerators.Add(new TaskDateGenerator(
                (_, line) => line == 1 ? dates ?? Dates : null,
                () => palette));
            // The editor is intentionally never shown. Lay out its TextView directly, as the
            // repository's other rendering fixtures do; arranging only the unrealized editor
            // container does not give the view a viewport or construct visual lines.
            var view = editor.TextArea.TextView;
            view.Measure(new Size(width, 300));
            view.Arrange(new Rect(0, 0, width, 300));
            view.EnsureVisualLines();
            return editor;
        }

        private static TaskDateElement DateElement(TextEditor editor) =>
            editor.TextArea.TextView.GetVisualLine(1)!.Elements.OfType<TaskDateElement>().Single();

        private static Color ColorOf(PadColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

        [Fact]
        public void A_task_date_is_muted_inline_text_with_no_document_length_or_strike() => UiThread.Run(() =>
        {
            var editor = Render("- [x] write tests\nafter", PadPalette.Dark);
            var element = DateElement(editor);

            Assert.Equal(" (created: 2026-10-06 08:15; finished: 2026-10-06 09:45)", element.DisplayText);
            Assert.Equal(0, element.DocumentLength);
            Assert.Equal(element.DisplayText.Length, element.VisualLength);
            Assert.Equal(ColorOf(PadPalette.Dark.Muted), ((SolidColorBrush)element.TextRunProperties.ForegroundBrush).Color);
            Assert.Empty(element.TextRunProperties.TextDecorations!);

            var wholeRun = Assert.IsType<TextCharacters>(element.CreateTextRun(element.VisualColumn, null!));
            var remainingRun = Assert.IsType<TextCharacters>(element.CreateTextRun(element.VisualColumn + 11, null!));
            Assert.Equal(element.DisplayText.Length, wholeRun.Length);
            Assert.Equal(element.DisplayText.Length - 11, remainingRun.Length);
        });

        [Fact]
        public void Rendering_and_selecting_dates_never_changes_or_copies_them_with_the_note() => UiThread.Run(() =>
        {
            int changes = 0;
            const string markdown = "- [ ] write tests\nafter";
            var editor = Render(markdown, PadPalette.Dark, changed: () => changes++);

            editor.Select(0, editor.Document.TextLength);

            Assert.Equal(markdown, editor.SelectedText);
            Assert.DoesNotContain("created:", editor.SelectedText, StringComparison.Ordinal);
            Assert.Equal(markdown, editor.Document.Text);
            Assert.Equal(0, changes);
        });

        [Fact]
        public void The_caret_stops_at_the_real_line_end_and_backspace_edits_only_markdown() => UiThread.Run(() =>
        {
            var editor = Render("- [ ] write tests", PadPalette.Light);
            var element = DateElement(editor);
            int end = editor.Document.GetLineByNumber(1).EndOffset;

            Assert.Equal(element.VisualColumn, element.GetNextCaretPosition(element.VisualColumn - 1, LogicalDirection.Forward, CaretPositioningMode.Normal));
            Assert.Equal(-1, element.GetNextCaretPosition(element.VisualColumn, LogicalDirection.Forward, CaretPositioningMode.Normal));
            Assert.Equal(element.VisualColumn, element.GetNextCaretPosition(element.VisualColumn + element.VisualLength, LogicalDirection.Backward, CaretPositioningMode.Normal));
            Assert.True(element.HandlesLineBorders);
            Assert.Equal(element.VisualColumn, element.GetVisualColumn(element.RelativeTextOffset));
            Assert.Equal(element.RelativeTextOffset, element.GetRelativeOffset(element.VisualColumn + element.VisualLength));

            editor.CaretOffset = 0;
            EditingCommands.MoveToLineEnd.Execute(null, editor.TextArea);
            Assert.Equal(end, editor.CaretOffset);

            editor.CaretOffset = end;
            EditingCommands.Backspace.Execute(null, editor.TextArea);

            Assert.Equal("- [ ] write test", editor.Document.Text);
            Assert.Equal(end - 1, editor.CaretOffset);
            Assert.Equal(new DateTimeOffset(2026, 10, 6, 8, 15, 0, TimeSpan.FromHours(7)), Dates.Created);
            Assert.Equal(new DateTimeOffset(2026, 10, 6, 9, 45, 0, TimeSpan.FromHours(7)), Dates.Finished);
        });

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void The_annotation_reserves_wrapping_space_in_both_themes(bool dark) => UiThread.Run(() =>
        {
            var palette = dark ? PadPalette.Dark : PadPalette.Light;
            var editor = Render("- [ ] task", palette, width: 190);
            var line = editor.TextArea.TextView.GetVisualLine(1)!;
            var element = DateElement(editor);

            Assert.True(line.TextLines.Count > 1);
            Assert.All(line.TextLines, textLine =>
                Assert.True(
                    textLine.WidthIncludingTrailingWhitespace <= editor.TextArea.TextView.ActualWidth + 1,
                    $"Wrapped line width {textLine.WidthIncludingTrailingWhitespace:0.##} exceeded viewport {editor.TextArea.TextView.ActualWidth:0.##}"));
            Assert.Equal(element.DisplayText.Length, element.VisualLength);
            Assert.Equal(ColorOf(palette.Muted), ((SolidColorBrush)element.TextRunProperties.ForegroundBrush).Color);
            Assert.Equal("- [ ] task", editor.Document.Text);
        });
    }
}
