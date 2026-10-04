using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Media;
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using Border = System.Windows.Controls.Border;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using ToolTip = System.Windows.Controls.ToolTip;
using Color = System.Windows.Media.Color;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The Ask window's own light/dark theme: no literal colors left, and a switch repaints at once. Windows are never shown.</summary>
    public class AskThemeTests
    {
        private static readonly string[] AskXaml =
        {
            Path.Combine("Ai", "AskWindow.xaml"),
            Path.Combine("Ai", "ChatStyles.xaml"),
        };

        private static void WithWindow(string theme, Action<AskWindow, AppConfig> test) => UiThread.Run(() =>
        {
            var config = new AppConfig { AskTheme = theme };
            var window = new AskWindow(() => new AskSetup(null, "not set up"), () => { }, _ => "", null, config);
            try
            {
                test(window, config);
            }
            finally
            {
                window.Close();
            }
        });

        private static Color BrushColor(object? brush) => Assert.IsAssignableFrom<SolidColorBrush>(brush).Color;

        private static Color Wpf(PadColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

        /// <summary>A turn with a bubble, a chip, a link, inline code, a note and a time, added to the transcript.</summary>
        private static AskTurnView AddFullTurn(AskWindow window)
        {
            var turn = new AskTurnView("How is the CPU?");
            window.TranscriptPanel.Children.Add(turn.Root);
            turn.AddTool("get_live_status", null);
            turn.AppendText("It is **fine**, see [docs](https://example.com) and `top`.\n\n> quoted");
            turn.ShowNote("Stopped.");
            turn.Complete(new DateTime(2026, 9, 30, 10, 0, 0), TimeSpan.Zero);
            return turn;
        }

        [Fact]
        public void The_ask_xaml_files_have_no_literal_colors_but_the_avatar_gradient()
        {
            var literal = new Regex("\"#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\"");
            foreach (string file in AskXaml)
            {
                string xaml = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), file));
                var found = xaml.Split('\n').Where(line => !line.Contains("GradientStop", StringComparison.Ordinal))
                    .SelectMany(line => literal.Matches(line).Select(m => m.Value)).ToList();
                Assert.True(found.Count == 0, file + " still has literal colors: " + string.Join(", ", found));
            }
        }

        [Fact]
        public void Every_ask_resource_the_ui_uses_is_in_the_palette()
        {
            var used = new Regex("Ask\\.[A-Za-z]+");
            var known = AskPalette.Dark.Resources().Select(r => r.Key).ToHashSet();
            string root = PadWindowTests.RepoRoot();
            var files = AskXaml.Concat(new[]
            {
                Path.Combine("Ai", "AskTurnView.cs"), Path.Combine("Ai", "ChatDocument.cs"),
            });
            foreach (string file in files)
            {
                string text = File.ReadAllText(Path.Combine(root, file));
                foreach (string key in used.Matches(text).Select(m => m.Value).Distinct())
                    Assert.True(known.Contains(key), file + " uses " + key + ", which the palette does not define");
            }
        }

        [Fact]
        public void The_window_opens_in_the_theme_the_config_names() => WithWindow("Light", (window, config) =>
        {
            Assert.Same(AskPalette.Light, window.Palette);
            Assert.Equal(Wpf(AskPalette.Light.Background), BrushColor(window.Resources["Ask.Background"]));
            Assert.Equal("\uE708", window.ThemeButton.Content);
            Assert.Equal("Switch to dark theme", window.ThemeButton.ToolTip);
            Assert.Equal(ModernWpf.ElementTheme.Light, ModernWpf.ThemeManager.GetRequestedTheme(window));
        });

        [Fact]
        public void A_window_without_a_config_opens_dark() => UiThread.Run(() =>
        {
            var window = new AskWindow(() => new AskSetup(null, "x"), () => { }, _ => "");
            try
            {
                Assert.Same(AskPalette.Dark, window.Palette);
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public void The_theme_button_switches_live_and_remembers_the_choice() => WithWindow("Dark", (window, config) =>
        {
            Assert.Equal("\uE706", window.ThemeButton.Content);
            Assert.Equal("Switch to light theme", window.ThemeButton.ToolTip);
            Assert.Equal(Wpf(AskPalette.Dark.Background), BrushColor(window.Resources["Ask.Background"]));

            window.ThemeButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.Equal("Light", config.AskTheme);
            Assert.Same(AskPalette.Light, window.Palette);
            Assert.Equal(Wpf(AskPalette.Light.Background), BrushColor(window.Resources["Ask.Background"]));
            Assert.Equal(Wpf(AskPalette.Light.Background), BrushColor(window.Background));
            Assert.Equal("\uE708", window.ThemeButton.Content);
            Assert.Equal("Switch to dark theme", window.ThemeButton.ToolTip);
            Assert.Equal(ModernWpf.ElementTheme.Light, ModernWpf.ThemeManager.GetRequestedTheme(window));

            window.ToggleTheme();

            Assert.Equal("Dark", config.AskTheme);
            Assert.Equal(Wpf(AskPalette.Dark.Background), BrushColor(window.Background));
            Assert.Equal("\uE706", window.ThemeButton.Content);
            Assert.Equal(ModernWpf.ElementTheme.Dark, ModernWpf.ThemeManager.GetRequestedTheme(window));
        });

        [Fact]
        public void A_turn_built_in_dark_repaints_when_the_window_goes_light() => WithWindow("Dark", (window, config) =>
        {
            var turn = AddFullTurn(window);
            var bubble = Assert.IsType<Border>(turn.Question.Parent);
            var chip = turn.ToolChips[0].Element;
            var link = AskThemeTestsLinks.First(turn.Answer.Document);

            Assert.Equal(Wpf(AskPalette.Dark.Bubble), BrushColor(bubble.Background));
            Assert.Equal(Wpf(AskPalette.Dark.ChipBack), BrushColor(chip.Background));
            Assert.Equal(Wpf(AskPalette.Dark.NoteBack), BrushColor(turn.Note.Background));
            Assert.Equal(Wpf(AskPalette.Dark.Ink), BrushColor(turn.Answer.Document.Foreground));
            Assert.Equal(Wpf(AskPalette.Dark.Accent), BrushColor(link.Foreground));
            Assert.Equal(Wpf(AskPalette.Dark.Muted), BrushColor(turn.TimeText.Foreground));

            config.AskTheme = "Light";

            Assert.Equal(Wpf(AskPalette.Light.Bubble), BrushColor(bubble.Background));
            Assert.Equal(Wpf(AskPalette.Light.ChipBack), BrushColor(chip.Background));
            Assert.Equal(Wpf(AskPalette.Light.NoteBack), BrushColor(turn.Note.Background));
            Assert.Equal(Wpf(AskPalette.Light.Ink), BrushColor(turn.NoteText.Foreground));
            Assert.Equal(Wpf(AskPalette.Light.Ink), BrushColor(turn.Answer.Document.Foreground));
            Assert.Equal(Wpf(AskPalette.Light.Accent), BrushColor(link.Foreground));
            Assert.Equal(Wpf(AskPalette.Light.Muted), BrushColor(turn.TimeText.Foreground));
            Assert.Equal(Wpf(AskPalette.Light.Muted), BrushColor(turn.ToolChips[0].Label.Foreground));
        });


        [Fact]
        public void Text_box_menus_follow_the_ask_theme_both_ways() => WithWindow("Dark", (window, config) =>
        {
            var turn = AddFullTurn(window);
            var boxes = new System.Windows.Controls.Primitives.TextBoxBase[] { window.QuestionBox, turn.Question, turn.Answer };

            foreach (var box in boxes)
            {
                var menu = Assert.IsType<ContextMenu>(box.ContextMenu);
                Assert.Equal(ModernWpf.ElementTheme.Dark, ModernWpf.ThemeManager.GetRequestedTheme(menu));
                Assert.Equal(Wpf(AskPalette.Dark.Surface), BrushColor(menu.Background));
            }
            Assert.Contains(window.QuestionBox.ContextMenu!.Items.OfType<MenuItem>(), i => i.Command == System.Windows.Input.ApplicationCommands.Paste);
            Assert.DoesNotContain(turn.Answer.ContextMenu!.Items.OfType<MenuItem>(), i => i.Command == System.Windows.Input.ApplicationCommands.Paste);

            config.AskTheme = "Light";

            foreach (var box in boxes)
            {
                var menu = box.ContextMenu!;
                Assert.Equal(ModernWpf.ElementTheme.Light, ModernWpf.ThemeManager.GetRequestedTheme(menu));
                Assert.Equal(Wpf(AskPalette.Light.Surface), BrushColor(menu.Background));
                Assert.Equal(Wpf(AskPalette.Light.Ink), BrushColor(menu.Foreground));
            }

            config.AskTheme = "Dark";

            Assert.Equal(ModernWpf.ElementTheme.Dark, ModernWpf.ThemeManager.GetRequestedTheme(turn.Question.ContextMenu!));
        });

        [Fact]
        public void A_code_block_menu_follows_the_theme_too() => WithWindow("Dark", (window, config) =>
        {
            var turn = new AskTurnView("q");
            window.TranscriptPanel.Children.Add(turn.Root);
            turn.AppendText("```\ncode\n```");
            turn.Complete(DateTime.Now, TimeSpan.Zero);
            var code = AiAskWindowTests.Descendants<System.Windows.Controls.TextBox>(turn.Answer.Document)
                .First(t => t.ContextMenu != null);

            config.AskTheme = "Light";

            Assert.Equal(ModernWpf.ElementTheme.Light, ModernWpf.ThemeManager.GetRequestedTheme(code.ContextMenu));
        });

        [Fact]
        public void Tooltips_use_an_ask_themed_style() => WithWindow("Light", (window, config) =>
        {
            var style = Assert.IsType<System.Windows.Style>(window.FindResource(typeof(ToolTip)));
            Assert.Contains(style.Setters.OfType<System.Windows.Setter>(), s => s.Property == System.Windows.Controls.Control.BackgroundProperty);
            var chip = new AskTurnView("q");
            window.TranscriptPanel.Children.Add(chip.Root);
            chip.AddTool("get_live_status", null);
            Assert.Equal(Wpf(AskPalette.Light.Surface), BrushColor(window.FindResource("Ask.Surface")));
        });
        [Fact]
        public void A_theme_set_elsewhere_reaches_an_open_window() => WithWindow("Dark", (window, config) =>
        {
            config.AskTheme = "Light";   // as Settings does

            Assert.Same(AskPalette.Light, window.Palette);
            Assert.Equal(Wpf(AskPalette.Light.Chrome), BrushColor(window.Resources["Ask.Chrome"]));
            Assert.Equal("\uE708", window.ThemeButton.Content);
        });

        [Fact]
        public void The_ask_and_micapad_themes_do_not_follow_each_other() => WithWindow("Dark", (window, config) =>
        {
            config.PadTheme = "Light";
            Assert.Equal("Dark", config.AskTheme);
            Assert.Same(AskPalette.Dark, window.Palette);

            window.ToggleTheme();
            Assert.Equal("Light", config.AskTheme);
            Assert.Equal("Light", config.PadTheme);

            config.PadTheme = "Dark";
            Assert.Equal("Light", config.AskTheme);
            Assert.Same(AskPalette.Light, window.Palette);

            window.ToggleTheme();
            Assert.Equal("Dark", config.PadTheme);   // the Ask toggle left MicaPad's choice alone
        });

        [Fact]
        public void A_closed_window_stops_listening() => UiThread.Run(() =>
        {
            var config = new AppConfig();
            var window = new AskWindow(() => new AskSetup(null, "x"), () => { }, _ => "", null, config);
            window.Close();

            config.AskTheme = "Light";

            Assert.Same(AskPalette.Dark, window.Palette);
        });

        private static class AskThemeTestsLinks
        {
            public static System.Windows.Documents.Hyperlink First(System.Windows.Documents.FlowDocument document) =>
                document.Blocks.OfType<System.Windows.Documents.Paragraph>()
                    .SelectMany(p => p.Inlines.OfType<System.Windows.Documents.Hyperlink>()).First();
        }
    }
}
