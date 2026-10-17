using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kil0bitSystemMonitor.Pad;
using Xunit;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Hardware-free layout fixtures for the two-pane Markdown workspace.</summary>
    public sealed class MarkdownSplitLayoutTests
    {
        private const string Fixture = """
            # Release checklist

            - [ ] Verify the package
            - [x] Review the notes

            | Area | State |
            | --- | --- |
            | Editor | Ready |
            | Preview | Live |

            ```csharp
            var message = "synthetic fixture";
            ```
            """;

        [Theory]
        [InlineData("Dark", 900, 640)]
        [InlineData("Light", 900, 640)]
        [InlineData("Dark", 420, 260)]
        [InlineData("Light", 420, 260)]
        public void Source_preview_headers_divider_and_read_only_result_fit_the_client(string theme, int width, int height) =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                config.PadTheme = theme;
                window.Editor.Document.Text = Fixture;
                window.ToggleMarkdownPreview();
                window.FlushMarkdownPreview();
                Grid root = Assert.IsType<Grid>(window.Content);

                Layout(root, width, height);

                Assert.Equal(Visibility.Visible, window.MarkdownSourceHeader.Visibility);
                Assert.Equal(Visibility.Visible, window.MarkdownPreviewPanel.Visibility);
                Assert.Equal(Visibility.Visible, window.PaneSplitter.Visibility);
                Assert.True(window.PaneSplitter.Focusable);
                Assert.Equal("Drag to resize; double-click to reset", window.PaneSplitter.ToolTip);
                Assert.Equal("Split preview", window.MarkdownSplitButton.Content);
                Assert.Equal("Markdown split preview", AutomationProperties.GetName(window.MarkdownSplitButton));
                Assert.Equal("Note source editor", AutomationProperties.GetName(window.Editor));
                Assert.True(window.MarkdownPreviewPanel.PreviewBox.IsReadOnly);
                Assert.Equal(Fixture, window.Editor.Document.Text);
                Assert.Equal(Fixture, window.MarkdownPreviewPanel.Shown);

                AssertInside(window.EditorArea, window.MarkdownSourceHeader);
                AssertInside(window.EditorArea, window.PaneSplitter);
                AssertInside(window.EditorArea, window.MarkdownPreviewPanel);
                Assert.True(window.Editor.ActualWidth >= 100, $"source width was {window.Editor.ActualWidth:0.#}");
                Assert.True(window.MarkdownPreviewPanel.ActualWidth >= 180, $"preview width was {window.MarkdownPreviewPanel.ActualWidth:0.#}");
                Assert.InRange(window.PaneSplitter.ActualWidth, 4, 8);
                Assert.Equal(0, window.MarkdownPreviewPanel.PreviewScroller.ScrollableWidth);

                SaveFixture(root, width, height, theme);
            });

        [Fact]
        public void The_markdown_ratio_is_independent_of_the_utility_pane_width_and_follows_resize_drag_and_reset() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                config.PadPaneWidth = 280;
                config.PadMarkdownPreviewRatio = 0.68;
                window.Editor.Document.Text = Fixture;
                window.ToggleMarkdownPreview();
                window.FlushMarkdownPreview();
                Grid root = Assert.IsType<Grid>(window.Content);
                Layout(root, 1000, 640);

                double usable = window.EditorArea.ActualWidth - window.PaneSplitter.ActualWidth;
                double previewShare = window.MarkdownPreviewPanel.ActualWidth / usable;
                Assert.InRange(previewShare, 0.66, 0.70);
                Assert.Equal(280, config.PadPaneWidth);
                Assert.Equal(0.68, config.PadMarkdownPreviewRatio);

                Layout(root, 1200, 640);
                usable = window.EditorArea.ActualWidth - window.PaneSplitter.ActualWidth;
                previewShare = window.MarkdownPreviewPanel.ActualWidth / usable;
                Assert.InRange(previewShare, 0.66, 0.70);
                Assert.Equal(280, config.PadPaneWidth);
                Assert.Equal(0.68, config.PadMarkdownPreviewRatio);

                window.PaneDragEnded(usable * 0.4);
                Layout(root, 1200, 640);
                Assert.Equal(0.4, config.PadMarkdownPreviewRatio, 3);
                Assert.Equal(280, config.PadPaneWidth);
                Assert.InRange(window.MarkdownPreviewPanel.ActualWidth / usable, 0.39, 0.41);

                window.ResetPaneWidth();
                Layout(root, 1200, 640);
                Assert.Equal(0.5, config.PadMarkdownPreviewRatio);
                Assert.Equal(280, config.PadPaneWidth);
                Assert.InRange(window.MarkdownPreviewPanel.ActualWidth / usable, 0.49, 0.51);
            });

        private static void Layout(FrameworkElement root, int width, int height)
        {
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            PadLanguageWindowTests.Pump();
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
        }

        private static void AssertInside(FrameworkElement root, FrameworkElement child)
        {
            Point topLeft = child.TranslatePoint(new Point(0, 0), root);
            Point bottomRight = child.TranslatePoint(new Point(child.ActualWidth, child.ActualHeight), root);
            Assert.InRange(topLeft.X, 0, root.ActualWidth);
            Assert.InRange(topLeft.Y, 0, root.ActualHeight);
            Assert.InRange(bottomRight.X, 0, root.ActualWidth + 0.5);
            Assert.InRange(bottomRight.Y, 0, root.ActualHeight + 0.5);
        }

        private static void SaveFixture(Visual root, int width, int height, string theme)
        {
            string? artifactRoot = Environment.GetEnvironmentVariable("MICAPAD_SPLIT_SCREENSHOTS");
            if (string.IsNullOrWhiteSpace(artifactRoot)) return;
            string folder = Path.GetFullPath(artifactRoot);
            Directory.CreateDirectory(folder);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(folder, $"markdown-split-{theme.ToLowerInvariant()}-{width}x{height}.png"));
            encoder.Save(output);
        }
    }
}
