using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Size = System.Windows.Size;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Path = System.IO.Path;

namespace Kil0bitSystemMonitor.Tests
{
    public sealed class TabColorWindowTests
    {
        [Fact]
        public void Tab_color_menu_names_every_choice_checks_the_current_color_and_repaints_real_note_views() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                config.PadTheme = "Dark";
                OpenNote note = Assert.IsType<OpenNote>(env.Workspace.ActiveIn(window.WindowId));
                var root = Assert.IsType<Grid>(window.Content);
                Layout(root, 900, 640);

                MenuItem colorMenu = Assert.IsType<MenuItem>(window.BuildTabMenu(note, null).Items[1]);
                Assert.Equal("Tab color", colorMenu.Header);
                Assert.Equal(new[] { "Blue", "Orange", "Green", "Purple", "Teal", "Amber", "Rose", "Violet", "Leaf", "Coral", "Pink", "Indigo" },
                    colorMenu.Items.Cast<MenuItem>().Select(item => Assert.IsType<string>(item.Header)));
                Assert.Equal(Wpf(NoteTabColors.Accent(note.TabColorHue, PadPalette.Dark)),
                    BrushColor(Assert.IsType<Ellipse>(colorMenu.Icon).Fill));
                Assert.All(colorMenu.Items.Cast<MenuItem>(), item =>
                {
                    Assert.True(item.IsCheckable);
                    var swatch = Assert.IsType<Ellipse>(item.Icon);
                    Assert.Equal(10, swatch.Width);
                    Assert.Equal(10, swatch.Height);
                    int hue = NoteTabColors.Choices.Single(choice => choice.Name == (string)item.Header).Hue;
                    Assert.Equal(Wpf(NoteTabColors.Accent(hue, PadPalette.Dark)), BrushColor(swatch.Fill));
                });
                Assert.Equal(note.TabColorHue,
                    NoteTabColors.Choices.Single(choice => colorMenu.Items.Cast<MenuItem>()
                        .Single(item => (string)item.Header == choice.Name).IsChecked).Hue);

                var rose = colorMenu.Items.Cast<MenuItem>().Single(item => Equals(item.Header, "Rose"));
                rose.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, rose));
                Layout(root, 900, 640);

                Assert.Equal(345, note.TabColorHue);
                Border tab = TabPart<Border>(window, note, "TabBorder");
                Ellipse marker = TabPart<Ellipse>(window, note, "TabColorMarker");
                Assert.Equal(Wpf(NoteTabColors.Background(345, PadPalette.Dark, active: true, hover: false)), BrushColor(tab.Background));
                Assert.Equal(Wpf(NoteTabColors.Accent(345, PadPalette.Dark)), BrushColor(tab.BorderBrush));
                Assert.Equal(Wpf(NoteTabColors.Accent(345, PadPalette.Dark)), BrushColor(marker.Fill));

                WithPickerOverlay(window, root, 900, 640, () =>
                {
                    ListBoxItem row = Assert.IsType<ListBoxItem>(window.NotesPicker.NotesList.ItemContainerGenerator.ContainerFromIndex(0));
                    Ellipse pickerMarker = Assert.Single(Descendants<Ellipse>(row));
                    Assert.Equal(Wpf(NoteTabColors.Accent(345, PadPalette.Dark)), BrushColor(pickerMarker.Fill));
                });
            });

        [Theory]
        [InlineData(7)]
        [InlineData(24)]
        public void Automatic_colors_outside_presets_have_a_checked_swatch_and_can_be_changed(int count) =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                for (int i = 1; i < count; i++) window.NewTab();
                OpenNote note = env.Workspace.Open.First(candidate =>
                    !NoteTabColors.Choices.Any(choice => choice.Hue == candidate.TabColorHue));
                int originalHue = note.TabColorHue;
                MenuItem colorMenu = Assert.IsType<MenuItem>(window.BuildTabMenu(note, null).Items[1]);
                MenuItem automatic = Assert.Single(colorMenu.Items.OfType<MenuItem>(), item => item.IsChecked);
                Assert.Equal("Automatic color", automatic.Header);
                Assert.False(automatic.IsEnabled);
                Assert.Equal(Wpf(NoteTabColors.Accent(originalHue, window.TabPalette)),
                    BrushColor(Assert.IsType<Ellipse>(automatic.Icon).Fill));
                Assert.Equal(originalHue, note.TabColorHue);

                MenuItem blue = colorMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Blue"));
                blue.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, blue));
                Assert.Equal(210, note.TabColorHue);
                MenuItem updated = Assert.IsType<MenuItem>(window.BuildTabMenu(note, null).Items[1]);
                Assert.DoesNotContain(updated.Items.OfType<MenuItem>(), item => Equals(item.Header, "Automatic color"));
                Assert.Equal("Blue", Assert.Single(updated.Items.OfType<MenuItem>(), item => item.IsChecked).Header);
            });

        [Theory]
        [InlineData(210)]
        [InlineData(75)]
        public void Current_color_keeps_its_swatch_visible_beside_the_selection_check(int hue) =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                OpenNote note = Assert.IsType<OpenNote>(env.Workspace.ActiveIn(window.WindowId));
                Assert.True(env.Workspace.SetTabColorHue(note, hue));
                ContextMenu menu = window.BuildTabMenu(note, null);
                EditorMenus.Style(menu, window.TabPalette);
                MenuItem colors = Assert.IsType<MenuItem>(menu.Items[1]);
                MenuItem selected = Assert.Single(colors.Items.OfType<MenuItem>(), item => item.IsChecked);
                selected.Style = (System.Windows.Style)menu.FindResource(typeof(MenuItem));
                selected.ApplyTemplate();
                Layout(selected, 240, 30);

                var glyph = Assert.IsType<ContentPresenter>(selected.Template.FindName("Glyph", selected));
                var check = Assert.IsType<TextBlock>(selected.Template.FindName("Check", selected));
                Assert.Equal(Visibility.Visible, glyph.Visibility);
                Assert.Equal(Visibility.Visible, check.Visibility);
                Assert.NotEqual(Grid.GetColumn(glyph), Grid.GetColumn(check));
                Ellipse swatch = Assert.Single(Descendants<Ellipse>(glyph));
                Assert.Equal(10, swatch.ActualWidth);
                Assert.Equal(Wpf(NoteTabColors.Accent(hue, window.TabPalette)), BrushColor(swatch.Fill));
            });

        [Fact]
        public void Theme_switch_reprojects_saved_hues_and_active_borders_without_changing_note_identity() =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                config.PadTheme = "Dark";
                OpenNote first = Assert.IsType<OpenNote>(env.Workspace.ActiveIn(window.WindowId));
                env.Workspace.Rename(first, "Project plan");
                Assert.True(env.Workspace.SetTabColorHue(first, 175));
                window.NewTab();
                OpenNote active = Assert.IsType<OpenNote>(env.Workspace.ActiveIn(window.WindowId));
                env.Workspace.Rename(active, "Daily review");
                Assert.True(env.Workspace.SetTabColorHue(active, 30));
                var root = Assert.IsType<Grid>(window.Content);
                Layout(root, 900, 640);

                Color darkBackground = BrushColor(TabPart<Border>(window, active, "TabBorder").Background);
                Color darkAccent = BrushColor(TabPart<Ellipse>(window, active, "TabColorMarker").Fill);
                Assert.Equal(Wpf(NoteTabColors.Background(30, PadPalette.Dark, active: true, hover: false)), darkBackground);
                Assert.Equal(Wpf(NoteTabColors.Accent(30, PadPalette.Dark)), darkAccent);

                config.PadTheme = "Light";
                Layout(root, 900, 640);

                Assert.Equal(175, first.TabColorHue);
                Assert.Equal(30, active.TabColorHue);
                Border activeBorder = TabPart<Border>(window, active, "TabBorder");
                Assert.Equal(Wpf(NoteTabColors.Background(30, PadPalette.Light, active: true, hover: false)), BrushColor(activeBorder.Background));
                Assert.Equal(Wpf(NoteTabColors.Accent(30, PadPalette.Light)), BrushColor(activeBorder.BorderBrush));
                Assert.Equal(Wpf(NoteTabColors.Accent(30, PadPalette.Light)), BrushColor(TabPart<Ellipse>(window, active, "TabColorMarker").Fill));
                Assert.Equal(Wpf(NoteTabColors.Background(175, PadPalette.Light, active: false, hover: false)),
                    BrushColor(TabPart<Border>(window, first, "TabBorder").Background));
                Assert.NotEqual(darkBackground, BrushColor(activeBorder.Background));
                Assert.NotEqual(darkAccent, BrushColor(TabPart<Ellipse>(window, active, "TabColorMarker").Fill));
            });

        [Theory]
        [InlineData("Dark", 900, 640)]
        [InlineData("Light", 900, 640)]
        [InlineData("Dark", 420, 260)]
        [InlineData("Light", 420, 260)]
        public void Colored_tabs_and_picker_rows_remain_readable_at_normal_and_minimum_sizes(string theme, int width, int height) =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
            {
                config.PadTheme = theme;
                string[] titles =
                {
                    "Weekly priorities", "Project roadmap", "Research references", "Meeting decisions",
                    "Customer follow-up", "Ideas for next week", "Launch checklist", "Reading notes",
                    "Design review", "Release plan", "Team updates", "Personal reminders",
                };
                for (int i = 0; i < titles.Length; i++)
                {
                    OpenNote note;
                    if (i == 0) note = Assert.IsType<OpenNote>(env.Workspace.ActiveIn(window.WindowId));
                    else
                    {
                        window.NewTab();
                        note = Assert.IsType<OpenNote>(env.Workspace.ActiveIn(window.WindowId));
                    }
                    env.Workspace.Rename(note, titles[i]);
                    Assert.True(env.Workspace.SetTabColorHue(note, NoteTabColors.Choices[i].Hue));
                }
                window.SelectTab(0);
                var root = Assert.IsType<Grid>(window.Content);
                Layout(root, width, height);

                Assert.True(window.TabScroller.ScrollableWidth > 0);
                Assert.True(window.TabScroller.ViewportWidth >= 120);
                Assert.InRange(window.TabHeaderWidth, 100, window.TabScroller.ViewportWidth);
                Assert.True(window.NewNoteButton.ActualWidth > 20);
                Assert.True(window.OpenNotesButton.ActualWidth > 60);
                Assert.All(env.Workspace.Open, note =>
                {
                    Assert.True(TabPart<Border>(window, note, "TabBorder").ActualWidth >= 100);
                    Assert.Equal(7, TabPart<Ellipse>(window, note, "TabColorMarker").ActualWidth);
                });

                WithPickerOverlay(window, root, width, height, () =>
                {
                    Assert.Equal(titles.Length, window.NotesPicker.NotesList.Items.Count);
                    Assert.True(window.NotesPicker.SearchInput.ActualWidth > 200);
                    Assert.True(window.NotesPicker.NotesList.ActualHeight >= 40);
                    var visibleRows = Descendants<ListBoxItem>(window.NotesPicker.NotesList).ToList();
                    Assert.NotEmpty(visibleRows);
                    Assert.All(visibleRows, row =>
                    {
                        Ellipse marker = Assert.Single(Descendants<Ellipse>(row));
                        Assert.Equal(7, marker.ActualWidth);
                        Assert.True(BrushColor(marker.Fill).A > 0);
                    });
                    SaveFixture(root, width, height, theme);
                });
            });

        private static T TabPart<T>(MicaPadWindow window, OpenNote note, string name) where T : FrameworkElement
        {
            var presenter = Assert.IsType<ContentPresenter>(window.TabStrip.ItemContainerGenerator.ContainerFromItem(note));
            return Assert.IsType<T>(Descendants<FrameworkElement>(presenter).Single(element => element.Name == name));
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

        private static void WithPickerOverlay(MicaPadWindow window, Grid root, int width, int height, Action inspect)
        {
            window.PrepareOpenNotes();
            window.UpdateOpenNotesBounds();
            FrameworkElement panel = window.OpenNotesPanel;
            window.OpenNotesPopup.Child = null;
            root.Children.Add(panel);
            Grid.SetRow(panel, 0);
            Grid.SetRowSpan(panel, 5);
            panel.HorizontalAlignment = HorizontalAlignment.Right;
            panel.VerticalAlignment = VerticalAlignment.Top;
            panel.Margin = new Thickness(12, 48, 12, 0);
            try
            {
                Layout(root, width, height);
                Assert.InRange(panel.ActualWidth, 240, width - 24);
                Assert.InRange(panel.ActualHeight, 100, height - 48);
                inspect();
            }
            finally
            {
                root.Children.Remove(panel);
                window.OpenNotesPopup.Child = panel;
                window.NotesPicker.Clear();
            }
        }

        private static void Layout(FrameworkElement root, int width, int height)
        {
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            PadLanguageWindowTests.Pump();
        }

        private static Color BrushColor(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;

        private static Color Wpf(PadColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

        private static void SaveFixture(Visual root, int width, int height, string theme)
        {
            string? folder = Environment.GetEnvironmentVariable("MICAPAD_COLOR_SCREENSHOTS");
            if (string.IsNullOrEmpty(folder)) return;
            Directory.CreateDirectory(folder);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(folder, $"tab-colors-{theme.ToLowerInvariant()}-{width}.png"));
            encoder.Save(output);
        }
    }
}
