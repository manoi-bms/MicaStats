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

        /// <summary>Runs what the window queued for after the current change (Normal priority and above).</summary>
        internal static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        /// <summary>Lays out the editor's text as a shown window would, so the language's colorizers run.</summary>
        internal static void Render(MicaPadWindow window)
        {
            var view = window.Editor.TextArea.TextView;
            view.Measure(new System.Windows.Size(600, 400));
            view.Arrange(new System.Windows.Rect(0, 0, 600, 400));
            view.EnsureVisualLines();
        }

        private static int SyntaxColorizers(MicaPadWindow window) =>
            window.Editor.TextArea.TextView.LineTransformers.OfType<ThemedHighlightingColorizer>().Count();

        [Fact]
        public void The_reading_font_is_for_markdown_tabs_only_and_follows_the_setting() => WithWindow((window, env, config) =>
        {
            Assert.Equal(MicaPadWindow.ReadingFont.Source, window.Editor.FontFamily.Source);
            Assert.StartsWith("Cascadia Mono", window.LanguageView.MonoFont!.Source);

            config.PadReadingFont = false;
            Assert.StartsWith("Cascadia Mono", window.Editor.FontFamily.Source);
            Assert.Null(window.LanguageView.MonoFont);

            config.PadReadingFont = true;
            Assert.Equal(MicaPadWindow.ReadingFont.Source, window.Editor.FontFamily.Source);
            OpenFile(window, env, "data.json", "{ \"a\": 1 }");
            Assert.StartsWith("Cascadia Mono", window.Editor.FontFamily.Source);
            Assert.Null(window.LanguageView.MonoFont);
        });

        [Fact]
        public void A_formatting_failure_puts_the_editor_back_in_its_monospace_font() => WithWindow((window, env, config) =>
        {
            var warnings = new System.Collections.Generic.List<string>();
            window.LanguageView.Warn = warnings.Add;
            Assert.Equal(MicaPadWindow.ReadingFont.Source, window.Editor.FontFamily.Source);

            window.LanguageView.ReportFailure(new InvalidOperationException("forced"));   // what a throwing colorizer reports
            Pump();

            Assert.Single(warnings);
            Assert.Same(PadLanguages.Plain, window.LanguageView.Current);
            Assert.StartsWith("Cascadia Mono", window.Editor.FontFamily.Source);
            Assert.Null(window.LanguageView.MonoFont);
        });

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
            Pump();
            Assert.Equal("Plain text (large)", window.LanguageButton.Content);
            Assert.False(window.LanguageView.HasSyntaxColors);

            document.Remove(0, PadLanguages.MaxFormattedChars);
            Pump();
            Assert.Equal("JSON", window.LanguageButton.Content);
            Assert.True(window.LanguageView.HasSyntaxColors);
        });

        [Fact]
        public void A_rendered_markdown_note_crossing_the_size_limit_turns_markdown_off_and_on() => WithWindow((window, env, config) =>
        {
            var view = window.Editor.TextArea.TextView;
            var document = window.Editor.Document;
            document.Text = "# Title\n```\ncode\n```\n- item";
            Render(window);   // the Markdown colorizer reads the fence cache, which starts following the document
            bool? markdownDuringChange = null;
            document.Changed += (s, e) => markdownDuringChange ??= window.LanguageView.HasMarkdown;

            // Short lines, so laying out the visible part stays quick.
            string paste = string.Concat(Enumerable.Repeat("\npasted line", PadLanguages.MaxFormattedChars / 12 + 1));
            document.Insert(document.TextLength, paste);
            Assert.True(markdownDuringChange);   // nothing is torn down inside the change itself
            Pump();

            Assert.True(window.ShownLanguage.TooLarge);
            Assert.False(window.LanguageView.HasMarkdown);
            Assert.Empty(view.LineTransformers.OfType<MarkdownColorizer>());
            Assert.Equal("Plain text (large)", window.LanguageButton.Content);

            document.Remove(document.TextLength - paste.Length, paste.Length);
            Pump();

            Assert.False(window.ShownLanguage.TooLarge);
            Assert.True(window.LanguageView.HasMarkdown);
            Assert.Single(view.LineTransformers.OfType<MarkdownColorizer>());
            Render(window);
        });

        [Fact]
        public void A_formatting_failure_warns_once_and_shows_that_note_as_plain_text() => WithWindow((window, env, config) =>
        {
            var warnings = new System.Collections.Generic.List<string>();
            window.LanguageView.Warn = warnings.Add;
            var failed = window.Editor.Document;
            Assert.True(window.LanguageView.HasMarkdown);

            window.LanguageView.ReportFailure(new InvalidOperationException("boom"));
            window.LanguageView.ReportFailure(new InvalidOperationException("boom again"));
            Assert.True(window.LanguageView.HasMarkdown);   // not torn down inside the failing hook
            Pump();

            Assert.Single(warnings);
            Assert.Contains("InvalidOperationException", warnings[0]);
            Assert.False(window.LanguageView.HasMarkdown);
            Assert.Same(PadLanguages.Plain, window.LanguageView.Current);
            Assert.Empty(window.Editor.TextArea.TextView.LineTransformers.OfType<MarkdownColorizer>());

            window.NewTab();                                  // another note still formats
            Assert.True(window.LanguageView.HasMarkdown);

            window.SelectTab(0);                              // the failed one stays plain
            Assert.Same(failed, window.Editor.Document);
            Assert.False(window.LanguageView.HasMarkdown);
            Assert.Single(warnings);
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

        [Fact]
        public void Saving_an_auto_note_as_json_switches_it_to_json() => WithWindow((window, env, config) =>
        {
            var note = env.Workspace.Active!;
            Assert.Equal("Markdown", window.LanguageButton.Content);

            window.SaveAsPath(note, env.FileOf("saved.json"));

            Assert.Equal("JSON", window.LanguageButton.Content);
            Assert.True(window.LanguageView.HasSyntaxColors);
        });

        [Fact]
        public void Keeping_a_file_as_a_note_returns_it_to_markdown() => WithWindow((window, env, config) =>
        {
            OpenFile(window, env, "b.json", "{}");
            Assert.Equal("JSON", window.LanguageButton.Content);

            window.KeepAsNote(env.Workspace.Active!);

            Assert.Equal("Markdown", window.LanguageButton.Content);
            Assert.False(window.LanguageView.HasSyntaxColors);
        });
    }
}
