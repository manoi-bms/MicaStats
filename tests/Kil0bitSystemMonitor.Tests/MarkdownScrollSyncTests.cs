using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Ai;
using Xunit;

using Point = System.Windows.Point;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Event-level regression coverage for source/preview semantic scroll synchronization.</summary>
    public sealed class MarkdownScrollSyncTests
    {
        [Fact]
        public void Scrolling_the_source_brings_the_matching_rendered_section_into_view() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                Fixture note = StructuredNote();
                window.Editor.Document.Text = note.Text;
                window.ToggleMarkdownPreview();
                window.FlushMarkdownPreview();
                Grid root = Assert.IsType<Grid>(window.Content);
                Layout(root);
                Assert.True(window.MarkdownPreviewPanel.PreviewScroller.ViewportHeight > 0,
                            "the rendered preview has an active viewport");

                ScrollSourceToLine(window, note.TargetLine);
                Assert.True(window.Editor.TextArea.TextView.VerticalOffset > 0, "the initiating source viewport moved");
                PadLanguageWindowTests.Pump();
                Relayout(root);
                window.Editor.TextArea.TextView.EnsureVisualLines();

                Paragraph target = FindParagraph(window, note.TargetHeading);
                Rect targetRect = target.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                Point inPreview = window.MarkdownPreviewPanel.PreviewBox.TranslatePoint(
                    new Point(targetRect.Left, targetRect.Top), window.MarkdownPreviewPanel.PreviewScroller);

                Assert.InRange(inPreview.Y, -24, window.MarkdownPreviewPanel.PreviewScroller.ViewportHeight * 0.45);
            });

        [Fact]
        public void Source_scroll_bursts_coalesce_without_rebuilding_the_preview() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                Fixture note = StructuredNote();
                window.Editor.Document.Text = note.Text;
                window.ToggleMarkdownPreview();
                window.FlushMarkdownPreview();
                Layout(Assert.IsType<Grid>(window.Content));
                int transfers = window.MarkdownScrollTransferCount;
                int renders = window.MarkdownPreviewPanel.RenderCount;
                var view = window.Editor.TextArea.TextView;

                SetSourceOffset(view, view.GetVisualTopByDocumentLine(note.TargetLine - 12));
                SetSourceOffset(view, view.GetVisualTopByDocumentLine(note.TargetLine - 6));
                SetSourceOffset(view, view.GetVisualTopByDocumentLine(note.TargetLine));

                Assert.Equal(transfers, window.MarkdownScrollTransferCount);
                Assert.Equal(renders, window.MarkdownPreviewPanel.RenderCount);
                PadLanguageWindowTests.Pump();
                Assert.Equal(transfers + 1, window.MarkdownScrollTransferCount);
                Assert.Equal(renders, window.MarkdownPreviewPanel.RenderCount);
            });

        [Fact]
        public void Editing_the_visible_section_rebuilds_once_and_retains_its_semantic_anchor() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                Fixture note = StructuredNote();
                window.Editor.Document.Text = note.Text;
                window.ToggleMarkdownPreview();
                window.FlushMarkdownPreview();
                Grid root = Assert.IsType<Grid>(window.Content);
                Layout(root);
                ScrollSourceToLine(window, note.TargetLine);
                PadLanguageWindowTests.Pump();
                Relayout(root);
                int renders = window.MarkdownPreviewPanel.RenderCount;

                var line = window.Editor.Document.GetLineByNumber(note.TargetLine + 2);
                window.Editor.CaretOffset = line.EndOffset;
                window.Editor.Document.Insert(line.EndOffset, " updated here");
                window.FlushMarkdownPreview();
                PadLanguageWindowTests.Pump();

                Assert.Equal(renders + 1, window.MarkdownPreviewPanel.RenderCount);
                Paragraph target = FindParagraph(window, note.TargetHeading);
                Rect targetRect = target.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                Point inPreview = window.MarkdownPreviewPanel.PreviewBox.TranslatePoint(
                    new Point(targetRect.Left, targetRect.Top), window.MarkdownPreviewPanel.PreviewScroller);
                Assert.InRange(inPreview.Y, -24, window.MarkdownPreviewPanel.PreviewScroller.ViewportHeight * 0.45);
            });

        [Fact]
        public void Editing_after_the_bounded_prefix_updates_limit_state_without_rebuilding_the_same_preview() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                string source = string.Join("\n", Enumerable.Range(1, 2_300).Select(i => "paragraph " + i));
                window.Editor.Document.Text = source;
                window.ToggleMarkdownPreview();
                window.FlushMarkdownPreview();
                Layout(Assert.IsType<Grid>(window.Content), attachWindow: false);
                Assert.True(window.MarkdownPreviewPanel.IsLimited);
                int renders = window.MarkdownPreviewPanel.RenderCount;

                window.Editor.Document.Insert(window.Editor.Document.TextLength, "\nchange beyond preview");
                window.FlushMarkdownPreview();

                Assert.Equal(renders, window.MarkdownPreviewPanel.RenderCount);
                Assert.Contains(window.Editor.Document.TextLength.ToString("N0"),
                                window.MarkdownPreviewPanel.LimitText.Text, StringComparison.Ordinal);
            });

        [Fact]
        public void Ordered_source_anchors_disambiguate_repeated_text_and_ignore_fenced_examples() =>
            UiThread.Run(() =>
            {
                const string source = "## Same\n- repeated\n\n```md\n## Same\n- repeated\n```\n\n## Same\n- repeated";
                var pane = new MarkdownPreviewPane();

                pane.Show(source);

                int[] listLines = ChatDocument.All<ListItem>(pane.PreviewBox.Document)
                    .Select(MarkdownPreviewDocument.SourceLine).Where(line => line > 0).OrderBy(line => line).ToArray();
                int[] codeLines = ChatDocument.All<BlockUIContainer>(pane.PreviewBox.Document)
                    .Select(MarkdownPreviewDocument.SourceLine).Where(line => line > 0).ToArray();
                Assert.Equal(new[] { 2, 10 }, listLines);
                Assert.Equal(new[] { 4 }, codeLines);
            });

        [Fact]
        public void Repeating_the_same_source_retries_a_failed_render_and_reports_recovery_honestly() =>
            UiThread.Run(() =>
            {
                var pane = new MarkdownPreviewPane();
                var build = pane.PreviewBox.MarkdownDocumentFactory;
                int attempts = 0;
                pane.PreviewBox.MarkdownDocumentFactory = (source, render) =>
                    ++attempts == 1 ? throw new InvalidOperationException("synthetic") : build(source, render);

                pane.Show("# Retry me");
                Assert.Equal("Error · shown as text", pane.StatusText.Text);

                pane.Show("# Retry me");

                Assert.Equal(2, attempts);
                Assert.Equal("Live", pane.StatusText.Text);
                Assert.Contains("Retry me", new TextRange(pane.PreviewBox.Document.ContentStart,
                                                           pane.PreviewBox.Document.ContentEnd).Text,
                                StringComparison.Ordinal);
            });

        [Fact]
        public void A_source_line_inside_tall_code_aligns_that_rendered_line_instead_of_the_fence_start() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                CodeFixture note = TallCodeNote();
                window.Editor.Document.Text = note.Text;
                window.ToggleMarkdownPreview();
                window.FlushMarkdownPreview();
                Grid root = Assert.IsType<Grid>(window.Content);
                Layout(root);

                double requestedSource = window.Editor.TextArea.TextView.GetVisualTopByDocumentLine(note.TargetLine);
                ScrollSourceToLine(window, note.TargetLine);
                Assert.InRange(Math.Abs(window.Editor.TextArea.TextView.VerticalOffset - requestedSource), 0, 1);
                PadLanguageWindowTests.Pump();
                Relayout(root);

                Point target = CodeLinePoint(window, note.TargetText);
                Assert.InRange(target.Y, -24, window.MarkdownPreviewPanel.PreviewScroller.ViewportHeight * 0.5);
            });

        [Fact]
        public void A_preview_line_inside_tall_code_aligns_the_same_source_line_and_does_not_bounce() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                CodeFixture note = TallCodeNote();
                window.Editor.Document.Text = note.Text;
                window.ToggleMarkdownPreview();
                window.FlushMarkdownPreview();
                Grid root = Assert.IsType<Grid>(window.Content);
                Layout(root);

                Point target = CodeLinePoint(window, note.TargetText);
                var preview = window.MarkdownPreviewPanel.PreviewScroller;
                double requested = Math.Clamp(preview.VerticalOffset + target.Y - 12, 0, preview.ScrollableHeight);
                preview.ScrollToVerticalOffset(requested);
                Relayout(root);
                Assert.InRange(Math.Abs(preview.VerticalOffset - requested), 0, 1);
                PadLanguageWindowTests.Pump();
                Relayout(root);

                int topLine = window.Editor.TextArea.TextView.VisualLines.First().FirstDocumentLine.LineNumber;
                Assert.InRange(topLine, note.TargetLine - 2, note.TargetLine + 2);
                double initiatingOffset = preview.VerticalOffset;
                for (int i = 0; i < 3; i++)
                {
                    PadLanguageWindowTests.Pump();
                    Relayout(root);
                }
                Assert.InRange(Math.Abs(preview.VerticalOffset - initiatingOffset), 0, 1);
            });

        [Fact]
        public void Divider_reflow_and_a_theme_document_replacement_keep_the_semantic_section() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                Fixture note = StructuredNote();
                window.MarkdownPreviewPanel.PreviewBox.Diagrams = new FakeChatDiagrams
                {
                    Answer = (_, _) => ChatDiagramFakes.Drawn(),
                };
                window.Editor.Document.Text = note.Text + "\n" + ChatDiagramFakes.Block();
                window.ToggleMarkdownPreview();
                window.FlushMarkdownPreview();
                Grid root = Assert.IsType<Grid>(window.Content);
                Layout(root);
                ScrollSourceToLine(window, note.TargetLine);
                PadLanguageWindowTests.Pump();
                Relayout(root);
                FlowDocument beforeTheme = window.MarkdownPreviewPanel.PreviewBox.Document;

                double usable = window.EditorArea.ActualWidth - window.PaneSplitter.ActualWidth;
                window.PaneDragEnded(usable * 0.38);
                Relayout(root, 760, 640);
                window.MarkdownPreviewPanel.ApplyTheme(false);
                PadLanguageWindowTests.Pump();
                Relayout(root, 760, 640);

                Assert.NotSame(beforeTheme, window.MarkdownPreviewPanel.PreviewBox.Document);
                Paragraph target = FindParagraph(window, note.TargetHeading);
                Rect targetRect = target.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                Point inPreview = window.MarkdownPreviewPanel.PreviewBox.TranslatePoint(
                    new Point(targetRect.Left, targetRect.Top), window.MarkdownPreviewPanel.PreviewScroller);
                Assert.InRange(inPreview.Y, -24, window.MarkdownPreviewPanel.PreviewScroller.ViewportHeight * 0.5);
            });

        [Fact]
        public void Parser_spans_cover_multiline_paragraphs_and_tables_for_intra_block_progress()
        {
            IReadOnlyList<ChatBlock> blocks = ChatMarkdown.Parse(
                "first line\nsecond line\nthird line\n\n| A | B |\n| --- | --- |\n| one | two |\n| three | four |");

            Assert.Equal((1, 3), (blocks[0].SourceLine, blocks[0].SourceEndLine));
            Assert.Equal(ChatBlockKind.Table, blocks[1].Kind);
            Assert.Equal((5, 8), (blocks[1].SourceLine, blocks[1].SourceEndLine));
        }

        [Fact]
        public void Repeated_scrolls_over_the_maximum_rendered_lines_reuse_anchor_references_and_geometry() =>
            UiThread.Run(() =>
            {
                string source = string.Join("\n", Enumerable.Range(1, 1_950).Select(i => "## Heading " + i));
                var pane = new MarkdownPreviewPane();
                pane.Show(source);
                pane.Measure(new Size(480, 520));
                pane.Arrange(new Rect(0, 0, 480, 520));
                pane.UpdateLayout();
                pane.ScrollToSourceLine(900);
                int collections = pane.AnchorCollectionBuildCount;
                int geometry = pane.AnchorGeometryBuildCount;
                Assert.True(collections > 0, "source anchors were collected for the rendered document");
                Assert.True(geometry > 0, "rendered anchor geometry was measured before checking its reuse");

                foreach (int line in new[] { 100, 500, 1_000, 1_500, 1_900, 750, 250 })
                {
                    pane.ScrollToSourceLine(line);
                    _ = pane.SourceLineAtViewport();
                }

                Assert.Equal(collections, pane.AnchorCollectionBuildCount);
                Assert.Equal(geometry, pane.AnchorGeometryBuildCount);
                Assert.Equal(1, pane.RenderCount);
            });

        [Fact]
        public void A_single_wrapped_source_line_keeps_within_line_progress_in_both_directions() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                WrappedFixture note = WrappedLineNote();
                window.Editor.Document.Text = note.Text;
                window.ToggleMarkdownPreview();
                window.FlushMarkdownPreview();
                Grid root = Assert.IsType<Grid>(window.Content);
                Layout(root);
                var view = window.Editor.TextArea.TextView;
                Assert.True(window.MarkdownPreviewPanel.PreviewScroller.ViewportHeight > 0,
                            WrappedLayout(window, root));
                double sourceTop = view.GetVisualTopByDocumentLine(note.Line);
                double sourceBottom = view.GetVisualTopByDocumentLine(note.Line + 1);

                SetSourceOffset(view, sourceTop + (sourceBottom - sourceTop) * note.LaterFraction);
                Assert.True(view.VerticalOffset > 0, "the initiating source viewport moved within the wrapped line");
                PadLanguageWindowTests.Pump();
                Relayout(root);
                Point later = RunPoint(window, note.LaterMarker);
                Assert.InRange(later.Y, -24, window.MarkdownPreviewPanel.PreviewScroller.ViewportHeight * 0.5);

                Point earlier = RunPoint(window, note.EarlierMarker);
                var preview = window.MarkdownPreviewPanel.PreviewScroller;
                double previewBefore = preview.VerticalOffset;
                double requested = Math.Clamp(preview.VerticalOffset + earlier.Y - 12, 0, preview.ScrollableHeight);
                preview.ScrollToVerticalOffset(requested);
                Relayout(root);
                Assert.True(Math.Abs(preview.VerticalOffset - previewBefore) > 1,
                            "the initiating preview viewport moved to the earlier marker");
                PadLanguageWindowTests.Pump();
                Relayout(root);
                double actualFraction = (view.VerticalOffset - sourceTop) / (sourceBottom - sourceTop);
                Assert.InRange(actualFraction, note.EarlierFraction - 0.08, note.EarlierFraction + 0.08);
                double initiatingOffset = preview.VerticalOffset;
                for (int i = 0; i < 3; i++) { PadLanguageWindowTests.Pump(); Relayout(root); }
                Assert.InRange(Math.Abs(preview.VerticalOffset - initiatingOffset), 0, 1);
            });

        [Fact]
        public void Preview_scroll_at_the_end_uses_the_achievable_source_offset_without_bouncing_back() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                CodeFixture note = ClampedCodeNote();
                window.Editor.Document.Text = note.Text;
                window.ToggleMarkdownPreview();
                window.FlushMarkdownPreview();
                Grid root = Assert.IsType<Grid>(window.Content);
                Layout(root);
                var preview = window.MarkdownPreviewPanel.PreviewScroller;
                var sourceView = window.Editor.TextArea.TextView;
                var sourceScroll = (IScrollInfo)sourceView;
                double sourceMaximum = Math.Max(0, sourceScroll.ExtentHeight - sourceScroll.ViewportHeight);
                Assert.True(sourceView.GetVisualTopByDocumentLine(note.TargetLine) > sourceMaximum,
                            "the semantic source target must require end clamping");

                Point target = CodeLinePoint(window, note.TargetText);
                double requested = Math.Clamp(preview.VerticalOffset + target.Y - 12, 0, preview.ScrollableHeight);
                preview.ScrollToVerticalOffset(requested);
                Relayout(root);
                Assert.True(preview.VerticalOffset > 0, "the initiating preview viewport moved to its end");
                double initiatingOffset = preview.VerticalOffset;
                PadLanguageWindowTests.Pump();
                Relayout(root);
                Assert.InRange(Math.Abs(window.Editor.VerticalOffset - sourceMaximum), 0, 1);

                for (int i = 0; i < 3; i++) { PadLanguageWindowTests.Pump(); Relayout(root); }
                Assert.InRange(Math.Abs(preview.VerticalOffset - initiatingOffset), 0, 1);
            });

        [Fact]
        public void Scrolling_the_preview_brings_the_matching_source_section_into_view_without_feedback_jump() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                Fixture note = StructuredNote();
                window.Editor.Document.Text = note.Text;
                window.ToggleMarkdownPreview();
                window.FlushMarkdownPreview();
                Layout(Assert.IsType<Grid>(window.Content));

                Paragraph target = FindParagraph(window, note.TargetHeading);
                Rect targetRect = target.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                Point inPreview = window.MarkdownPreviewPanel.PreviewBox.TranslatePoint(
                    new Point(targetRect.Left, targetRect.Top), window.MarkdownPreviewPanel.PreviewScroller);
                double requested = Math.Clamp(
                    window.MarkdownPreviewPanel.PreviewScroller.VerticalOffset + inPreview.Y - 12,
                    0,
                    window.MarkdownPreviewPanel.PreviewScroller.ScrollableHeight);

                window.MarkdownPreviewPanel.PreviewScroller.ScrollToVerticalOffset(requested);
                Relayout(Assert.IsType<Grid>(window.Content));
                Assert.True(window.MarkdownPreviewPanel.PreviewScroller.VerticalOffset > 0,
                            "the initiating preview viewport moved");
                PadLanguageWindowTests.Pump();
                Relayout(Assert.IsType<Grid>(window.Content));
                window.Editor.TextArea.TextView.EnsureVisualLines();

                int topLine = Assert.IsType<ICSharpCode.AvalonEdit.Rendering.VisualLine>(
                    window.Editor.TextArea.TextView.VisualLines.First()).FirstDocumentLine.LineNumber;
                Assert.InRange(topLine, note.TargetLine - 3, note.TargetLine + 4);

                double previewAfterSync = window.MarkdownPreviewPanel.PreviewScroller.VerticalOffset;
                PadLanguageWindowTests.Pump();
                Assert.InRange(Math.Abs(window.MarkdownPreviewPanel.PreviewScroller.VerticalOffset - previewAfterSync), 0, 1);
            });

        private static Paragraph FindParagraph(MicaPadWindow window, string text) =>
            ChatDocument.All<Paragraph>(window.MarkdownPreviewPanel.PreviewBox.Document)
                .Single(block => new TextRange(block.ContentStart, block.ContentEnd).Text.Contains(text, StringComparison.Ordinal));

        private static Point CodeLinePoint(MicaPadWindow window, string text)
        {
            TextBox code = Assert.Single(ChatDocument.All<TextBox>(window.MarkdownPreviewPanel.PreviewBox.Document));
            int index = code.Text.IndexOf(text, StringComparison.Ordinal);
            Assert.True(index >= 0, "target code line is rendered");
            Rect rect = code.GetRectFromCharacterIndex(index);
            return code.TranslatePoint(new Point(rect.Left, rect.Top), window.MarkdownPreviewPanel.PreviewScroller);
        }

        private static Point RunPoint(MicaPadWindow window, string marker)
        {
            Run run = ChatDocument.All<Run>(window.MarkdownPreviewPanel.PreviewBox.Document)
                .Single(candidate => candidate.Text.Contains(marker, StringComparison.Ordinal));
            int index = run.Text.IndexOf(marker, StringComparison.Ordinal);
            TextPointer position = run.ContentStart.GetPositionAtOffset(index, LogicalDirection.Forward)!;
            Rect rect = position.GetCharacterRect(LogicalDirection.Forward);
            return window.MarkdownPreviewPanel.PreviewBox.TranslatePoint(
                new Point(rect.Left, rect.Top), window.MarkdownPreviewPanel.PreviewScroller);
        }

        private static void Layout(FrameworkElement root, bool attachWindow = true)
        {
            if (attachWindow && Window.GetWindow(root) is { IsVisible: false } window)
            {
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32_000;
                window.Top = -32_000;
                window.Show();
            }
            Relayout(root);
            PadLanguageWindowTests.Pump();
            Relayout(root);
            PadLanguageWindowTests.Pump();
        }

        private static void Relayout(FrameworkElement root, double width = 980, double height = 640)
        {
            if (Window.GetWindow(root) is { IsVisible: true } window)
            {
                window.Width = width;
                window.Height = height;
                window.UpdateLayout();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
                    System.Windows.Threading.DispatcherPriority.ContextIdle);
                window.UpdateLayout();
                return;
            }
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
        }

        private static void ScrollSourceToLine(MicaPadWindow window, int line)
        {
            var view = window.Editor.TextArea.TextView;
            SetSourceOffset(view, view.GetVisualTopByDocumentLine(line));
        }

        private static void SetSourceOffset(ICSharpCode.AvalonEdit.Rendering.TextView view, double offset)
        {
            var scroll = (IScrollInfo)view;
            scroll.CanVerticallyScroll = true;
            scroll.SetVerticalOffset(offset);
            view.EnsureVisualLines();
        }

        private static Fixture StructuredNote()
        {
            var text = new StringBuilder();
            var starts = new Dictionary<int, int>();
            int line = 1;
            for (int section = 1; section <= 24; section++)
            {
                starts[section] = line;
                text.Append("## Section ").Append(section).AppendLine();
                text.AppendLine();
                text.Append("Paragraph ").Append(section).AppendLine(" has enough words to wrap differently from its Markdown source when the two panes have different typography and widths.");
                text.AppendLine("- first list item");
                text.AppendLine("- second list item with **emphasis**");
                if (section % 3 == 0)
                {
                    text.AppendLine();
                    text.AppendLine("| Kind | Value |");
                    text.AppendLine("| --- | --- |");
                    text.Append("| section | ").Append(section).AppendLine(" |");
                }
                if (section % 4 == 0)
                {
                    text.AppendLine();
                    text.AppendLine("```text");
                    text.Append("code for section ").Append(section).AppendLine();
                    text.AppendLine("with a second line");
                    text.AppendLine("```");
                }
                text.AppendLine();
                line = text.ToString().Count(character => character == '\n') + 1;
            }

            const int target = 18;
            return new Fixture(text.ToString(), starts[target], "Section " + target);
        }

        private sealed record Fixture(string Text, int TargetLine, string TargetHeading);

        private static CodeFixture TallCodeNote() => BuildTallCodeNote(trailingContent: true);

        private static CodeFixture ClampedCodeNote() => BuildTallCodeNote(trailingContent: false);

        private static CodeFixture BuildTallCodeNote(bool trailingContent)
        {
            var source = new StringBuilder("# Before code\n\n");
            for (int i = 1; i <= 18; i++) source.Append("Intro paragraph ").Append(i).AppendLine(".\n");
            source.AppendLine("```text");
            int openingLine = source.ToString().Count(character => character == '\n');
            for (int i = 1; i <= 64; i++) source.Append("code line ").Append(i.ToString("00")).AppendLine();
            source.AppendLine("```");
            if (trailingContent)
                for (int i = 1; i <= 40; i++) source.Append("Trailing paragraph ").Append(i).AppendLine(".\n");
            const int targetInside = 43;
            return new CodeFixture(source.ToString(), openingLine + targetInside, "code line 43");
        }

        private static string WrappedLayout(MicaPadWindow window, FrameworkElement root)
        {
            MarkdownPreviewPane pane = window.MarkdownPreviewPanel;
            var scroller = pane.PreviewScroller;
            var presenter = scroller.Template.FindName("PART_ScrollContentPresenter", scroller) as ScrollContentPresenter;
            var scrollInfo = (IScrollInfo?)presenter;
            return $"preview viewport must be active; root={root.ActualHeight:0.##}/{root.DesiredSize.Height:0.##}, "
                + $"editorArea={window.EditorArea.ActualHeight:0.##}/{window.EditorArea.DesiredSize.Height:0.##}, "
                + $"pane={pane.ActualHeight:0.##}/{pane.DesiredSize.Height:0.##}, "
                + $"scroller={scroller.ActualHeight:0.##}/{scroller.DesiredSize.Height:0.##}, "
                + $"viewport={scroller.ViewportHeight:0.##}, extent={scroller.ExtentHeight:0.##}, scrollable={scroller.ScrollableHeight:0.##}, "
                + $"body={pane.PreviewBox.ActualHeight:0.##}/{pane.PreviewBox.DesiredSize.Height:0.##}, "
                + $"loaded={scroller.IsLoaded}, valid={scroller.IsMeasureValid}/{scroller.IsArrangeValid}, logical={scroller.CanContentScroll}, "
                + $"presenter={presenter?.ActualWidth:0.##}x{presenter?.ActualHeight:0.##}, valid={presenter?.IsMeasureValid}/{presenter?.IsArrangeValid}, "
                + $"info={scrollInfo?.ViewportHeight:0.##}/{scrollInfo?.ExtentHeight:0.##}, owner={ReferenceEquals(scrollInfo?.ScrollOwner, scroller)}";
        }

        private sealed record CodeFixture(string Text, int TargetLine, string TargetText);

        private static WrappedFixture WrappedLineNote()
        {
            var words = Enumerable.Range(1, 520).Select(i => i switch
            {
                180 => "EARLIER_MARKER",
                390 => "LATER_MARKER",
                _ => "word" + i,
            }).ToArray();
            string line = string.Join(" ", words);
            const string prefix = "# Wrapped progress\n\nBefore.\n\n";
            string text = prefix + line + "\n\n## After\n"
                + string.Concat(Enumerable.Range(1, 40).Select(i => $"Trailing paragraph {i}.\n\n"));
            int lineNumber = prefix.Count(character => character == '\n') + 1;
            return new WrappedFixture(text, lineNumber,
                (double)line.IndexOf("EARLIER_MARKER", StringComparison.Ordinal) / line.Length,
                (double)line.IndexOf("LATER_MARKER", StringComparison.Ordinal) / line.Length,
                "EARLIER_MARKER", "LATER_MARKER");
        }

        private sealed record WrappedFixture(string Text, int Line, double EarlierFraction, double LaterFraction,
                                             string EarlierMarker, string LaterMarker);
    }
}
