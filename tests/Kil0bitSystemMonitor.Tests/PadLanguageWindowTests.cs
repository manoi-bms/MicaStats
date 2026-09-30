using System;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The MicaPad window shows each tab in its language and says which in the status bar.</summary>
    public class PadLanguageWindowTests
    {
        internal static void WithWindow(Action<MicaPadWindow, PadTestEnv, AppConfig> test) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var config = new AppConfig();
            var window = new MicaPadWindow(env.Workspace, config);
            try
            {
                window.LoadSession();
                test(window, env, config);
            }
            finally
            {
                window.CloseForExit();
            }
        });

        internal static void OpenFile(MicaPadWindow window, PadTestEnv env, string name, string text)
        {
            string path = env.FileOf(name);
            File.WriteAllText(path, text);
            window.OpenPath(path);
        }

        private static int SyntaxColorizers(MicaPadWindow window) =>
            window.Editor.TextArea.TextView.LineTransformers.OfType<ThemedHighlightingColorizer>().Count();

        [Fact]
        public void A_note_is_markdown_and_says_so() => WithWindow((window, env, config) =>
        {
            Assert.Equal("Markdown", window.LanguageButton.Content);
            Assert.Same(PadLanguages.Markdown, window.LanguageView.Current);
            Assert.False(window.LanguageView.HasSyntaxColors);
        });

        [Fact]
        public void A_json_file_gets_json_colors() => WithWindow((window, env, config) =>
        {
            OpenFile(window, env, "a.json", "{\n  \"a\": 1\n}");

            Assert.Equal("JSON", window.LanguageButton.Content);
            Assert.True(window.LanguageView.HasSyntaxColors);
            Assert.Equal(1, SyntaxColorizers(window));
        });

        [Fact]
        public void Choosing_a_language_repaints_and_is_remembered() => WithWindow((window, env, config) =>
        {
            OpenFile(window, env, "a.json", "{}");
            var note = env.Workspace.Active!;

            window.ChooseLanguage(note, "plain");
            Assert.Equal("Plain text", window.LanguageButton.Content);
            Assert.False(window.LanguageView.HasSyntaxColors);
            env.Flush();
            Assert.Equal("plain", env.Store.LoadMeta(note.Id)!.Language);

            window.ChooseLanguage(note, null);
            Assert.Equal("JSON", window.LanguageButton.Content);
            Assert.Equal(1, SyntaxColorizers(window));
        });

        [Fact]
        public void Switching_tabs_switches_the_language_without_piling_up_colorizers() => WithWindow((window, env, config) =>
        {
            OpenFile(window, env, "a.json", "{}");
            int json = env.Workspace.Open.Count - 1;
            window.NewTab();
            Assert.Equal("Markdown", window.LanguageButton.Content);

            window.SelectTab(json);
            window.SelectTab(json + 1);
            window.SelectTab(json);

            Assert.Equal("JSON", window.LanguageButton.Content);
            Assert.Equal(1, SyntaxColorizers(window));
        });

        [Fact]
        public void Turning_markdown_off_shows_notes_as_plain_text() => WithWindow((window, env, config) =>
        {
            config.PadMarkdown = false;
            Assert.Equal("Plain text", window.LanguageButton.Content);

            config.PadMarkdown = true;
            Assert.Equal("Markdown", window.LanguageButton.Content);
        });

        [Fact]
        public void Crossing_the_size_limit_turns_formatting_off_and_on() => WithWindow((window, env, config) =>
        {
            OpenFile(window, env, "a.json", "{}");
            var document = window.Editor.Document;

            document.Insert(0, new string('x', PadLanguages.MaxFormattedChars));
            Assert.Equal("Plain text (large)", window.LanguageButton.Content);
            Assert.False(window.LanguageView.HasSyntaxColors);

            document.Remove(0, PadLanguages.MaxFormattedChars);
            Assert.Equal("JSON", window.LanguageButton.Content);
            Assert.True(window.LanguageView.HasSyntaxColors);
        });

        [Fact]
        public void The_language_menu_lists_auto_then_every_language() => WithWindow((window, env, config) =>
        {
            var menu = window.BuildLanguageMenu(env.Workspace.Active!);
            var headers = menu.Items.Cast<object>().Select(i => i is MenuItem m ? (string)m.Header : "-").ToList();

            Assert.Equal("Auto (by file type)", headers[0]);
            Assert.Equal("-", headers[1]);
            Assert.Equal(PadLanguages.All.Select(l => l.Name), headers.Skip(2));
            Assert.True(((MenuItem)menu.Items[0]).IsChecked);
        });

        [Fact]
        public void A_theme_switch_keeps_the_colors() => WithWindow((window, env, config) =>
        {
            OpenFile(window, env, "a.log", "2026-09-30 10:00:00 ERROR boom");

            window.ToggleTheme();

            Assert.Equal("Log", window.LanguageButton.Content);
            Assert.True(window.LanguageView.HasSyntaxColors);
        });
    }
}
