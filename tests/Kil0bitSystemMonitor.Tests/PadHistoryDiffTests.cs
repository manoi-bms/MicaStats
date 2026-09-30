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
