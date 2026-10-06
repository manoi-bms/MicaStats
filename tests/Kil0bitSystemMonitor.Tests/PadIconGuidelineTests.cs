using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;
using Button = System.Windows.Controls.Button;
using Path = System.IO.Path;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    public sealed class PadIconGuidelineTests
    {
        [Fact]
        public void Command_menus_cover_actions_and_nested_choices_with_icons() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                OpenNote note = Assert.IsType<OpenNote>(env.Workspace.ActiveIn(window.WindowId));
                ContextMenu main = window.BuildMainMenu();
                AssertCovered(main.Items);
                Assert.Equal("\uEA37", main.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Line numbers")).Icon);
                Assert.Equal("\uEA37", EditorMenus.LinesMenu(window.Editor, _ => { }).Icon);
                MenuItem format = EditorMenus.FormatMenu(window.Editor);
                Assert.Equal("\uEA37", format.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Numbered list")).Icon);
                Assert.Equal("\uE8FD", format.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Bullet list")).Icon);
                window.RefreshEditorMenu();
                AssertCovered(window.EditorMenu.Items);
                AssertCovered(window.BuildTabMenu(note, null).Items);
                AssertCovered(window.BuildLanguageMenu(note).Items);
                MenuItem ai = EditorMenus.AiMenu(window.Editor, true, _ => { }, () => { }, () => { });
                AssertCovered(ai.Items);
            });

        [Theory]
        [InlineData("Dark", 900, 640)]
        [InlineData("Light", 900, 640)]
        [InlineData("Dark", 420, 260)]
        [InlineData("Light", 420, 260)]
        public void Window_action_icons_keep_labels_and_fit_normal_and_minimum_widths(string theme, int width, int height) =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                config.PadTheme = theme;
                var root = Assert.IsType<Grid>(window.Content);
                Layout(root, width, height);
                foreach (Button button in new[] { window.HistoryButton, window.LanguageButton, window.EncodingButton, window.EolButton })
                    AssertFits(button, root, width);
                foreach (TextBlock status in new[] { window.CaretText, window.CharsText })
                {
                    var natural = new TextBlock { Text = status.Text, FontFamily = status.FontFamily, FontSize = status.FontSize };
                    natural.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Assert.True(status.ActualWidth + 1 >= natural.DesiredSize.Width, $"Clipped status text: {status.Text}");
                }
                SaveFixture(root, width, height, $"micapad-actions-{theme.ToLowerInvariant()}-{width}");

                window.PreviewText.Text = "Saved version";
                window.PreviewPanel.Visibility = Visibility.Visible;
                window.CompareButton.Visibility = Visibility.Visible;
                window.RestoreButton.Visibility = Visibility.Visible;
                Layout(root, width, height);
                foreach (Button button in Descendants<Button>(window.PreviewPanel)) AssertFits(button, root, width);
                SaveFixture(root, width, height, $"micapad-history-{theme.ToLowerInvariant()}-{width}");

                window.PreviewPanel.Visibility = Visibility.Collapsed;
                window.ShowInfo("This file has changed outside MicaPad. Choose which version to keep.", null,
                    "Reload", () => { }, "Keep mine", () => { });
                Layout(root, width, height);
                AssertFits(window.InfoPrimary, root, width);
                AssertFits(window.InfoSecondary, root, width);
                SaveFixture(root, width, height, $"micapad-notice-{theme.ToLowerInvariant()}-{width}");

                window.InfoBar.Visibility = Visibility.Collapsed;
                window.StatusMessage.Visibility = Visibility.Visible;
                string[] statuses =
                {
                    "The text this ran on is gone; select text and run the action again",
                    "Stored as ABCDEFGH — older versions are still being saved; they will be cleaned when that finishes — save the file (Ctrl+S) to remove it there",
                };
                foreach (string status in statuses)
                {
                    window.StatusMessage.Text = status;
                    Layout(root, width, height);
                    Rect statusBounds = window.StatusMessage.TransformToAncestor(root)
                        .TransformBounds(new Rect(new Point(), window.StatusMessage.RenderSize));
                    Assert.InRange(statusBounds.Right, 0, width);
                    if (width == 420 && status == statuses[1])
                        Assert.True(window.StatusMessage.ActualHeight > 20, "Long status messages must wrap.");
                }
                SaveFixture(root, width, height, $"micapad-status-{theme.ToLowerInvariant()}-{width}");
            });

        [Theory]
        [InlineData("Dark")]
        [InlineData("Light")]
        public void Popup_menu_rows_render_icons_labels_and_selection_checks(string theme) =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                config.PadTheme = theme;
                ContextMenu menu = window.BuildMainMenu();
                var panel = new StackPanel { Background = PadThemeApplier.ToBrush(window.TabPalette.Popup) };
                PadThemeApplier.ApplyResources(panel.Resources, window.TabPalette);
                var style = (System.Windows.Style)menu.FindResource(typeof(MenuItem));
                foreach (MenuItem item in menu.Items.OfType<MenuItem>().ToArray())
                {
                    menu.Items.Remove(item);
                    item.Style = style;
                    panel.Children.Add(item);
                }
                Layout(panel, 360, 780);
                foreach (MenuItem item in panel.Children.OfType<MenuItem>())
                {
                    var glyph = Assert.IsType<ContentPresenter>(item.Template.FindName("Glyph", item));
                    Assert.Equal(Visibility.Visible, glyph.Visibility);
                    Assert.NotNull(glyph.Content);
                    Assert.True(item.ActualHeight >= 30);
                    if (item.IsChecked)
                        Assert.Equal(Visibility.Visible, Assert.IsType<TextBlock>(item.Template.FindName("Check", item)).Visibility);
                }
                SaveFixture(panel, 360, 780, $"micapad-menu-{theme.ToLowerInvariant()}");
            });

        private static void AssertCovered(ItemCollection items)
        {
            Assert.NotEmpty(items.OfType<MenuItem>());
            foreach (MenuItem item in items.OfType<MenuItem>())
            {
                Assert.NotNull(item.Icon);
                if (item.Icon is string glyph) Assert.False(string.IsNullOrWhiteSpace(glyph), $"Missing icon: {item.Header}");
                if (item.Items.Count > 0) AssertCovered(item.Items);
            }
        }

        private static void AssertFits(Button button, FrameworkElement root, int width)
        {
            Assert.False(string.IsNullOrWhiteSpace(ButtonIcon.GetGlyph(button)));
            var label = Assert.Single(Descendants<ContentPresenter>(button), element => element.Name == "ActionLabel");
            Assert.Equal(button.Content, label.Content);
            Rect bounds = button.TransformToAncestor(root).TransformBounds(new Rect(new Point(), button.RenderSize));
            Assert.InRange(bounds.Left, 0, width);
            Assert.InRange(bounds.Right, 0, width + 1);
            Assert.True(label.ActualWidth > 0, $"Hidden action label: {button.Content}");
            var naturalLabel = new TextBlock
            {
                Text = button.Content?.ToString(), FontFamily = button.FontFamily, FontSize = button.FontSize,
                FontWeight = button.FontWeight, FontStyle = button.FontStyle,
            };
            naturalLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(label.ActualWidth + 1 >= naturalLabel.DesiredSize.Width,
                $"Clipped action label: {button.Content} ({label.ActualWidth:F1} < {naturalLabel.DesiredSize.Width:F1})");
        }

        private static void Layout(FrameworkElement element, int width, int height)
        {
            element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();
            PadLanguageWindowTests.Pump();
        }

        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (T descendant in Descendants<T>(child)) yield return descendant;
            }
        }

        private static void SaveFixture(Visual root, int width, int height, string name)
        {
            string? folder = Environment.GetEnvironmentVariable("MICAPAD_ICON_SCREENSHOTS");
            if (string.IsNullOrEmpty(folder)) return;
            Directory.CreateDirectory(folder);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(folder, name + ".png"));
            encoder.Save(stream);
        }
    }
}
