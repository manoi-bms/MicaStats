using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;

namespace Kil0bitSystemMonitor.Tests
{
    public class MarkdownPreviewPaneTests
    {
        private static string Rendered(MarkdownPreviewPane pane) =>
            new TextRange(pane.PreviewBox.Document.ContentStart, pane.PreviewBox.Document.ContentEnd)
                .Text.TrimEnd().Replace("\r\n", "\n", StringComparison.Ordinal);

        private static Color BrushColor(object? brush) => Assert.IsAssignableFrom<SolidColorBrush>(brush).Color;
        private static Color Wpf(PadColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

        [Fact]
        public void Show_renders_shared_markdown_and_keeps_the_source_unchanged() => UiThread.Run(() =>
        {
            const string source = "# Plan\n\n- [ ] first\n- [x] second\n\n| Name | Value |\n|---|---|\n| one | **two** |\n\n```cs\n- [ ] sample\n```";
            var pane = new MarkdownPreviewPane();

            pane.Show(source);

            Assert.Equal(source, pane.Shown);
            Assert.Equal(source, pane.PreviewBox.Shown);
            string rendered = Rendered(pane);
            Assert.Contains("Plan", rendered, StringComparison.Ordinal);
            Assert.Contains("☐ first", rendered, StringComparison.Ordinal);
            Assert.Contains("☑ second", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("[ ] first", rendered, StringComparison.Ordinal);
            Assert.Single(ChatDocument.All<System.Windows.Documents.Table>(pane.PreviewBox.Document));
            Assert.Equal("- [ ] sample", Assert.Single(ChatDocument.All<TextBox>(pane.PreviewBox.Document)).Text);
            var copy = Assert.Single(ChatDocument.All<System.Windows.Controls.Button>(pane.PreviewBox.Document));
            Assert.Equal("\uE8C8", Kil0bitSystemMonitor.Helpers.ButtonIcon.GetGlyph(copy));
            Assert.Equal("Live", pane.StatusText.Text);
            Assert.False(pane.IsLimited);
        });

        [Fact]
        public void Task_glyph_transformation_preserves_line_endings_and_structural_examples()
        {
            const string source = "---\r\n- [x] front matter\r\n---\r\n- [ ] open\r\n| task |\r\n|---|\r\n| - [ ] cell |";

            string transformed = MarkdownPreviewDocument.WithTaskGlyphs(source);

            Assert.Contains("---\r\n- ☐ open\r\n", transformed, StringComparison.Ordinal);
            Assert.Contains("- [x] front matter", transformed, StringComparison.Ordinal);
            Assert.Contains("- [ ] cell", transformed, StringComparison.Ordinal);
            Assert.Equal(source.Count(c => c == '\r'), transformed.Count(c => c == '\r'));
            Assert.Equal(source.Count(c => c == '\n'), transformed.Count(c => c == '\n'));
        }

        [Fact]
        public void Long_preview_is_bounded_without_splitting_a_surrogate_pair() => UiThread.Run(() =>
        {
            string source = new string('a', MarkdownPreviewPane.MaxPreviewChars - 1) + "😀" + "tail";
            var pane = new MarkdownPreviewPane();

            pane.Show(source);

            Assert.True(pane.IsLimited);
            Assert.Equal(MarkdownPreviewPane.MaxPreviewChars - 1, pane.Shown.Length);
            Assert.False(char.IsHighSurrogate(pane.Shown[^1]));
            Assert.Equal(pane.Shown, pane.PreviewBox.Shown);
            Assert.Equal("Limited", pane.StatusText.Text);
            Assert.Equal(Visibility.Visible, pane.LimitText.Visibility);
            Assert.Contains(source.Length.ToString("N0"), pane.LimitText.Text, StringComparison.Ordinal);
        });

        [Fact]
        public void A_bounded_prefix_uses_the_original_document_length_in_its_limit_message() => UiThread.Run(() =>
        {
            string prefix = new string('a', MarkdownPreviewPane.MaxPreviewChars + 1);
            var pane = new MarkdownPreviewPane();

            pane.Show(prefix, originalLength: 2_000_000);

            Assert.True(pane.IsLimited);
            Assert.Equal(MarkdownPreviewPane.MaxPreviewChars, pane.Shown.Length);
            Assert.Contains("2,000,000", pane.LimitText.Text, StringComparison.Ordinal);
        });

        [Fact]
        public void Thousands_of_expensive_blocks_are_bounded_and_reported() => UiThread.Run(() =>
        {
            string source = string.Join("\n", Enumerable.Range(1, 10_000).Select(i => "- item " + i));
            var pane = new MarkdownPreviewPane();

            pane.Show(source, originalLength: source.Length);

            Assert.True(pane.IsLimited);
            Assert.Equal("Limited", pane.StatusText.Text);
            Assert.True(pane.Shown.Count(c => c == '\n') < 2_000);
            Assert.Contains("The source is unchanged", pane.LimitText.Text, StringComparison.Ordinal);
        });

        [Fact]
        public void Updating_clear_and_render_failure_have_truthful_states() => UiThread.Run(() =>
        {
            var pane = new MarkdownPreviewPane();
            pane.SetUpdating();
            Assert.Equal("Updating…", pane.StatusText.Text);

            pane.PreviewBox.MarkdownDocumentFactory = (_, _) => throw new InvalidOperationException("private note text");
            pane.Show("**source fallback**");
            Assert.Equal("Error · shown as text", pane.StatusText.Text);
            Assert.Equal("**source fallback**", Rendered(pane));

            pane.Clear();
            Assert.Equal("", pane.Shown);
            Assert.Equal("", pane.PreviewBox.Shown);
            Assert.False(pane.IsLimited);
            Assert.Equal(Visibility.Visible, pane.EmptyState.Visibility);
            Assert.Equal(Visibility.Collapsed, pane.PreviewScroller.Visibility);
            Assert.Equal("Live", pane.StatusText.Text);
        });

        [Fact]
        public void Every_failed_render_reports_error_after_recovery_while_logging_only_once() => UiThread.Run(() =>
        {
            var pane = new MarkdownPreviewPane();
            var build = pane.PreviewBox.MarkdownDocumentFactory;
            int warnings = 0;
            bool fail = true;
            pane.PreviewBox.Warn = _ => warnings++;
            pane.PreviewBox.MarkdownDocumentFactory = (source, render) => fail
                ? throw new InvalidOperationException("synthetic failure") : build(source, render);

            pane.Show("# First failure");
            Assert.Equal("Error · shown as text", pane.StatusText.Text);
            fail = false;
            pane.Show("# Recovered");
            Assert.Equal("Live", pane.StatusText.Text);
            fail = true;
            pane.Show("# Later failure");
            Assert.Equal("Error · shown as text", pane.StatusText.Text);
            Assert.Equal("# Later failure", Rendered(pane));
            Assert.Equal(1, warnings);
        });

        [Fact]
        public void Buttons_raise_requests_and_the_theme_reaches_the_rendered_body() => UiThread.Run(() =>
        {
            var pane = new MarkdownPreviewPane();
            int refreshed = 0;
            int closed = 0;
            pane.RefreshRequested += () => refreshed++;
            pane.CloseRequested += () => closed++;

            pane.RefreshButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            pane.CloseButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            pane.ApplyTheme(false);

            Assert.Equal(1, refreshed);
            Assert.Equal(1, closed);
            Assert.Equal(ModernWpf.ElementTheme.Light, ModernWpf.ThemeManager.GetRequestedTheme(pane));
            Assert.Equal(Wpf(PadPalette.Light.Chrome), BrushColor(pane.Background));
            Assert.Equal(Wpf(Kil0bitSystemMonitor.Services.Ai.AskPalette.Light.Ink), BrushColor(pane.PreviewBox.Foreground));
        });

        [Fact]
        public void Mermaid_uses_the_local_diagram_provider_and_clear_releases_its_state() => UiThread.Run(() =>
        {
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => ChatDiagramFakes.Drawn() };
            var pane = new MarkdownPreviewPane();
            pane.PreviewBox.Diagrams = diagrams;

            pane.Show(ChatDiagramFakes.Block());

            Assert.Equal((ChatDiagramFakes.Flow, true), (Assert.Single(diagrams.Gets).Source, diagrams.Gets[0].Dark));
            Assert.Single(ChatDocument.All<System.Windows.Controls.Image>(pane.PreviewBox.Document));

            pane.Clear();

            Assert.Equal(1, diagrams.Cleared);
            Assert.Equal("", pane.PreviewBox.Shown);
        });

        [Fact]
        public void Narrow_pane_wraps_content_and_a_long_preview_scrolls_inside() => UiThread.Run(() =>
        {
            var pane = new MarkdownPreviewPane();
            pane.Show(string.Join("\n\n", Enumerable.Range(1, 100).Select(i => "Paragraph " + i)));
            pane.Measure(new Size(200, 260));
            pane.Arrange(new Rect(0, 0, 200, 260));
            pane.UpdateLayout();

            Assert.Equal(200, pane.ActualWidth);
            Assert.Equal(260, pane.ActualHeight);
            Assert.True(pane.PreviewScroller.ScrollableHeight > 0, "the preview scrolls independently");
            Assert.InRange(pane.PreviewBox.ActualWidth, 100, 200);
            foreach (var button in new[] { pane.RefreshButton, pane.CloseButton })
            {
                Point corner = button.TranslatePoint(new Point(button.ActualWidth, button.ActualHeight), pane);
                Assert.True(button.ActualWidth > 0 && corner.X <= 200 && corner.Y <= 260, button.Content + " is inside the pane");
            }
        });

        [Fact]
        public void Xaml_uses_only_pad_palette_resources_and_labels_actions()
        {
            string xaml = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "Pad", "MarkdownPreviewPane.xaml"));
            Assert.Empty(new Regex("\"#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\"").Matches(xaml).Select(m => m.Value));

            var known = PadPalette.Dark.Resources().Select(resource => resource.Key).ToHashSet();
            var used = new Regex(@"Resource (Pad\.[A-Za-z]+)\}").Matches(xaml)
                .Select(match => match.Groups[1].Value).Distinct().ToList();
            Assert.True(used.Count >= 5, "expected Pad.* theme resources");
            Assert.All(used, key => Assert.Contains(key, known));
            var markup = UiMarkup.Load("Pad/MarkdownPreviewPane.xaml");
            UiMarkup.Localized(markup, "AutomationProperties.Name", "Refresh Markdown preview");
            UiMarkup.Localized(markup, "AutomationProperties.Name", "Close Markdown preview");
        }
    }
}
