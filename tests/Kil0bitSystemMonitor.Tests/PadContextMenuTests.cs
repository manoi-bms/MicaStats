using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    public sealed class PadContextMenuTests
    {
        [Theory]
        [InlineData(1280, 800, 1, 1, 380, 776)]
        [InlineData(640, 480, 1.5, 1.5, 380, 296)]
        [InlineData(480, 320, 2, 2, 216, 136)]
        public void Popup_limits_follow_the_chosen_monitor_and_display_scaling(
            double pixelsWide, double pixelsHigh, double scaleX, double scaleY, double expectedWidth, double expectedHeight)
        {
            EditorMenus.MenuPopupBounds bounds = EditorMenus.FitMenuToWorkArea(pixelsWide, pixelsHigh, scaleX, scaleY);
            Assert.Equal(expectedWidth, bounds.MenuMaxWidth, 2);
            Assert.Equal(expectedHeight, bounds.MenuMaxHeight, 2);
            Assert.True(bounds.ViewportMaxWidth < bounds.MenuMaxWidth);
            Assert.True(bounds.ViewportMaxHeight < bounds.MenuMaxHeight);
        }

        [Fact]
        public void Explicit_popup_caps_still_apply_on_a_larger_monitor()
        {
            EditorMenus.MenuPopupBounds bounds = EditorMenus.FitMenuToWorkArea(1920, 1080, 1.5, 1.5, 280, 180);
            Assert.Equal(280, bounds.MenuMaxWidth);
            Assert.Equal(180, bounds.MenuMaxHeight);
        }

        [Fact]
        public void Selection_context_explains_the_scope_without_repeating_selected_text() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                const string privateText = "synthetic private text that belongs only in the editor";
                window.Editor.Text = privateText;
                window.Editor.SelectAll();
                window.RefreshEditorMenu();

                Separator caption = Assert.IsType<Separator>(window.EditorMenu.Items[0]);
                Assert.False(caption.Focusable);
                string context = caption.DataContext.ToString()!;
                Assert.Contains("Selected", context, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(privateText.Length.ToString(), context);
                Assert.DoesNotContain(privateText, context);
                Assert.Contains("Edit", SectionLabels(window.EditorMenu));
                Assert.Contains("Write & tools", SectionLabels(window.EditorMenu));

                MenuItem navigate = PadMenuTests.ItemOf(window.EditorMenu, "Find & navigate");
                Assert.Equal(new[] { "Find", "Replace", "Go to line…" }, navigate.Items.OfType<MenuItem>().Select(i => i.Header));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(navigate)));
                Assert.NotNull(navigate.Icon);
                Assert.DoesNotContain(window.EditorMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Find"));
            });

        [Fact]
        public void Task_commands_precede_editing_and_describe_the_separately_stored_dates() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                window.Editor.Text = "- [] Prepare the release";
                window.Editor.CaretOffset = 7;
                window.RefreshEditorMenu();

                var actions = window.EditorMenu.Items.OfType<MenuItem>().ToArray();
                Assert.Equal(new[] { "Complete task", "Edit task dates…", "Undo" }, actions.Take(3).Select(i => i.Header));
                Assert.Contains("Pending", Assert.IsType<Separator>(window.EditorMenu.Items[0]).DataContext.ToString());
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(actions[1])));
                Assert.Equal("- [] Prepare the release", window.Editor.Text);

                PadMenuTests.Click(actions[0]);
                window.RefreshEditorMenu();
                Assert.Equal("Reopen task", window.EditorMenu.Items.OfType<MenuItem>().First().Header);
            });

        [Fact]
        public void Tab_closing_is_last_and_explains_note_recovery() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                OpenNote note = env.Workspace.ActiveIn(window.WindowId)!;
                ContextMenu menu = window.BuildTabMenu(note, null);
                Assert.Equal(new[] { "Personalize", "Move & locate", "Close" }, SectionLabels(menu));
                Assert.Equal(new[] { "Close", "Close other tabs" }, menu.Items.OfType<MenuItem>().TakeLast(2).Select(i => i.Header));
                Assert.Contains("Closed notes", AutomationProperties.GetHelpText(PadMenuTests.ItemOf(menu, "Close")));
                Assert.False(PadMenuTests.ItemOf(menu, "Close other tabs").IsEnabled);
                Assert.True(ToolTipService.GetShowOnDisabled(PadMenuTests.ItemOf(menu, "Close other tabs")));
            });

        [Fact]
        public void History_and_credential_captions_preserve_read_only_and_secret_boundaries() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                window.PreviewEditor.Text = "historic text";
                window.RefreshPreviewMenu();
                Assert.Contains("Read-only", Assert.IsType<Separator>(window.PreviewMenu.Items[0]).DataContext.ToString());
                Assert.Equal(new[] { "Copy", "Select all" }, window.PreviewMenu.Items.OfType<MenuItem>().Select(i => i.Header));

                env.Vault.Load();
                env.Vault.Create("246810");
                const string secret = "synthetic-private-credential";
                string id = env.Vault.Add(secret, "Synthetic account", null);
                window.PreviewEditor.Text = SecretTokens.Format(id);
                window.PreviewEditor.CaretOffset = 0;
                window.RefreshPreviewMenu();
                Assert.DoesNotContain(secret, Assert.IsType<Separator>(window.PreviewMenu.Items[0]).DataContext.ToString());
                Assert.DoesNotContain(window.PreviewMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Unmask") || Equals(item.Header, "Delete credential…"));
                Assert.Contains("Reference & security", SectionLabels(window.PreviewMenu));
            });

        [Theory]
        [InlineData("Dark", 600)]
        [InlineData("Light", 600)]
        [InlineData("Dark", 260)]
        [InlineData("Light", 260)]
        public void Contextual_popups_fit_the_height_and_scroll_to_all_commands(string theme, int height) =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                config.PadTheme = theme;
                window.Editor.Text = "# Synthetic menu fixture\n\nSelect and edit Markdown.";
                window.RefreshEditorMenu();
                ContextMenu menu = window.EditorMenu;
                menu.MaxHeight = height;
                menu.MaxWidth = 380;
                menu.ApplyTemplate();
                menu.Measure(new Size(380, height));
                menu.Arrange(new Rect(0, 0, menu.DesiredSize.Width, menu.DesiredSize.Height));
                menu.UpdateLayout();
                PadLanguageWindowTests.Pump();
                // Shared keyboard-gesture columns settle after item containers first materialize.
                menu.Measure(new Size(380, height));
                menu.Arrange(new Rect(0, 0, menu.DesiredSize.Width, menu.DesiredSize.Height));
                menu.UpdateLayout();

                Assert.InRange(menu.ActualHeight, 100, height);
                Assert.InRange(menu.ActualWidth, 200, 380);
                ScrollViewer scroll = Assert.Single(Descendants<ScrollViewer>(menu));
                if (height == 260) Assert.True(scroll.ScrollableHeight > 100, "A short popup must offer scrolling to its remaining commands.");
                foreach (Separator caption in menu.Items.OfType<Separator>().Where(i => i.DataContext != null))
                {
                    Assert.False(caption.Focusable);
                    Assert.Contains(Descendants<TextBlock>(caption), t => !string.IsNullOrWhiteSpace(t.Text));
                }
                foreach (Border card in Descendants<Border>(menu).Where(b => b.CornerRadius.TopLeft == 8))
                {
                    Rect bounds = card.TransformToAncestor(menu).TransformBounds(new Rect(new Point(), card.RenderSize));
                    Assert.InRange(bounds.Right, 0, menu.ActualWidth + 1);
                    Assert.InRange(bounds.Bottom, 0, menu.ActualHeight + 1);
                }
                SaveFixture(menu, theme, height, "top");
                scroll.ScrollToEnd();
                menu.UpdateLayout();
                PadLanguageWindowTests.Pump();
                MenuItem last = menu.Items.OfType<MenuItem>().Last();
                double lastY = last.TranslatePoint(new Point(), scroll).Y;
                Assert.InRange(lastY, -1, scroll.ViewportHeight);
                SaveFixture(menu, theme, height, "end");
            });

        private static string[] SectionLabels(ContextMenu menu) => menu.Items.OfType<Separator>()
            .Where(i => i.DataContext is string).Select(i => (string)i.DataContext).ToArray();

        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (T descendant in Descendants<T>(child)) yield return descendant;
            }
        }

        private static void SaveFixture(FrameworkElement menu, string theme, int height, string position)
        {
            string? folder = Environment.GetEnvironmentVariable("MICAPAD_MENU_SCREENSHOTS");
            if (string.IsNullOrWhiteSpace(folder)) return;
            Directory.CreateDirectory(folder);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(menu);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(folder, $"context-menu-{theme.ToLowerInvariant()}-{height}-{position}.png"));
            encoder.Save(output);
        }
    }
}
