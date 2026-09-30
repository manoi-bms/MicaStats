using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using Color = System.Windows.Media.Color;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>MicaPad's theme: no literal colors left, every key the XAML uses exists, and a switch repaints at once.</summary>
    public class PadThemeTests
    {
        private static readonly string[] PadXaml =
        {
            Path.Combine("Pad", "MicaPadWindow.xaml"),
            Path.Combine("Pad", "FindReplaceBar.xaml"),
            Path.Combine("Pad", "HistoryPane.xaml"),
        };

        private static void WithWindow(string theme, Action<MicaPadWindow, AppConfig> test) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var config = new AppConfig { PadTheme = theme };
            var window = new MicaPadWindow(env.Workspace, config);
            try
            {
                window.LoadSession();
                test(window, config);
            }
            finally
            {
                window.CloseForExit();
            }
        });

        private static Color BrushColor(object? brush) => Assert.IsAssignableFrom<SolidColorBrush>(brush).Color;

        private static Color Wpf(PadColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

        [Fact]
        public void The_micapad_xaml_files_have_no_literal_colors()
        {
            var literal = new Regex("\"#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\"");
            foreach (string file in PadXaml)
            {
                string xaml = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), file));
                var found = literal.Matches(xaml).Select(m => m.Value).ToList();
                Assert.True(found.Count == 0, file + " still has literal colors: " + string.Join(", ", found));
            }
        }

        [Fact]
        public void Every_pad_resource_the_ui_uses_is_in_the_palette()
        {
            var keys = PadPalette.Dark.Resources().Select(r => r.Key).ToHashSet();
            var used = new Regex(@"Pad\.[A-Za-z]+");
            var sources = PadXaml.Concat(new[]
            {
                Path.Combine("Pad", "MicaPadWindow.xaml.cs"),
                Path.Combine("Pad", "FindReplaceBar.xaml.cs"),
            });
            int count = 0;
            foreach (string file in sources)
            {
                string text = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), file));
                foreach (Match match in used.Matches(text))
                {
                    // Namespaces (Kil0bitSystemMonitor.Pad.X, Services.Pad.X) and words ending in
                    // "Pad" (MicaPad.X) are not resource keys: a key starts after a quote or space.
                    char before = match.Index > 0 ? text[match.Index - 1] : ' ';
                    if (before == '.' || char.IsLetterOrDigit(before)) continue;
                    count++;
                    Assert.True(keys.Contains(match.Value), file + " uses " + match.Value + ", which the palette does not have");
                }
            }
            Assert.True(count > 20, "expected the MicaPad UI to read its colors from Pad.* keys, found " + count);
        }

        [Fact]
        public void The_window_opens_in_the_configured_theme() => WithWindow("Light", (window, config) =>
        {
            var light = PadPalette.Light;
            Assert.Same(light, window.Palette);
            Assert.Equal(Wpf(light.Background), BrushColor(window.Resources["Pad.Background"]));
            Assert.Equal(Wpf(light.Background), BrushColor(window.Editor.Background));
            Assert.Equal(Wpf(light.Text), BrushColor(window.Editor.Foreground));
            Assert.Equal(Wpf(light.Selection), BrushColor(window.Editor.TextArea.SelectionBrush));
            Assert.Equal(Wpf(light.LineNumbers), BrushColor(window.Editor.LineNumbersForeground));
            Assert.Equal(ModernWpf.ElementTheme.Light, ModernWpf.ThemeManager.GetRequestedTheme(window));
            Assert.Equal("",window.ThemeButton.Content);
            Assert.Equal("Switch to dark theme", window.ThemeButton.ToolTip);
        });

        [Fact]
        public void The_theme_button_switches_live_and_remembers_the_choice() => WithWindow("Dark", (window, config) =>
        {
            Assert.Equal("",window.ThemeButton.Content);
            Assert.Equal("Switch to light theme", window.ThemeButton.ToolTip);

            window.ToggleTheme();

            Assert.Equal("Light", config.PadTheme);
            Assert.Same(PadPalette.Light, window.Palette);
            Assert.Equal(Wpf(PadPalette.Light.Background), BrushColor(window.Editor.Background));
            Assert.Equal(Wpf(PadPalette.Light.Caret), BrushColor(window.Editor.TextArea.Caret.CaretBrush));
            Assert.Equal(ModernWpf.ElementTheme.Light, ModernWpf.ThemeManager.GetRequestedTheme(window));

            window.ToggleTheme();

            Assert.Equal("Dark", config.PadTheme);
            Assert.Equal(Wpf(PadPalette.Dark.Background), BrushColor(window.Editor.Background));
        });

        [Fact]
        public void A_theme_set_elsewhere_repaints_an_open_window() => WithWindow("Dark", (window, config) =>
        {
            config.PadTheme = "Light";   // as Settings does

            Assert.Same(PadPalette.Light, window.Palette);
            Assert.Equal(Wpf(PadPalette.Light.Chrome), BrushColor(window.Resources["Pad.Chrome"]));
        });

        [Fact]
        public void A_switch_repaints_the_find_bar_and_the_preview_too() => WithWindow("Dark", (window, config) =>
        {
            window.Editor.Document.Text = "ab ab";
            window.FindBar.FindBox.Text = "ab";
            window.FindBar.Open(replace: false);
            Assert.Equal(Wpf(PadPalette.Dark.Muted), BrushColor(window.FindBar.CountText.Foreground));

            window.ToggleTheme();

            Assert.Equal(Wpf(PadPalette.Light.Muted), BrushColor(window.FindBar.CountText.Foreground));
            Assert.Equal(Wpf(PadPalette.Light.FindMatch), BrushColor(window.FindBar.MatchFill));
            Assert.Equal(Wpf(PadPalette.Light.Background), BrushColor(window.PreviewEditor.Background));
            Assert.Equal(Wpf(PadPalette.Light.TextSoft), BrushColor(window.PreviewEditor.Foreground));
            Assert.Equal(Wpf(PadPalette.Light.Muted), BrushColor(window.SaveText.Foreground));
        });

        [Fact]
        public void Menus_follow_the_theme() => WithWindow("Light", (window, config) =>
        {
            var menu = window.NewMenu(window.MenuButton, System.Windows.Controls.Primitives.PlacementMode.Bottom);
            Assert.Equal(ModernWpf.ElementTheme.Light, ModernWpf.ThemeManager.GetRequestedTheme(menu));
        });

        [Fact]
        public void Settings_offers_the_theme_choice()
        {
            string xaml = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "SettingsWindow.xaml"));
            Assert.Contains("x:Name=\"PadThemeBox\"", xaml);
            Assert.Contains("SelectionChanged=\"OnPadThemeChanged\"", xaml);
        }
    }
}
