using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// MicaPad's answer box on the shared UI thread, never shown and never inside an Ask window:
    /// Markdown or plain text, read-only, in the pad's light or dark theme.
    /// </summary>
    public class PadAnswerBoxTests
    {
        private static string Rendered(PadAnswerBox box)
        {
            var document = box.Document;
            return new TextRange(document.ContentStart, document.ContentEnd).Text.TrimEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        private static Color BrushColor(object? brush) => Assert.IsAssignableFrom<SolidColorBrush>(brush).Color;

        private static Color Wpf(PadColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

        [Fact]
        public void Markdown_is_rendered_and_the_raw_text_is_kept() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();

            box.ShowMarkdown("**bold** and `code`");

            Assert.Equal("bold and code", Rendered(box));
            Assert.Equal("**bold** and `code`", box.Shown);
        });

        [Fact]
        public void Plain_text_keeps_its_markers_and_its_lines() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();

            box.ShowPlain("**bold** and `code`\n# not a heading");

            Assert.Equal("**bold** and `code`\n# not a heading", Rendered(box));
            Assert.Equal("**bold** and `code`\n# not a heading", box.Shown);
        });

        [Fact]
        public void A_new_text_replaces_the_old_one() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();
            box.ShowMarkdown("first");

            box.ShowPlain("second");
            Assert.Equal("second", Rendered(box));
            Assert.Equal("second", box.Shown);

            box.ShowMarkdown("");
            Assert.Equal("", Rendered(box));
            Assert.Equal("", box.Shown);
        });

        [Fact]
        public void The_box_is_read_only_and_offers_only_Copy_and_Select_all() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();
            box.ShowMarkdown("text");

            Assert.True(box.IsReadOnly);
            Assert.True(box.IsDocumentEnabled);   // links can be clicked
            Assert.True(box.IsTabStop);           // keyboard users can reach it to select and copy
            var menu = Assert.IsType<ContextMenu>(box.ContextMenu);
            Assert.Equal(new[] { ApplicationCommands.Copy, ApplicationCommands.SelectAll },
                menu.Items.OfType<MenuItem>().Select(item => item.Command));
            Assert.All(menu.Items.OfType<MenuItem>(), item => Assert.Same(box, item.CommandTarget));
        });

        [Fact]
        public void A_new_box_is_dark_and_the_theme_changes_its_text_colour() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();
            box.ShowMarkdown("text");
            Assert.Equal(Wpf(AskPalette.Dark.Ink), BrushColor(box.Resources["Ask.Ink"]));

            box.ApplyTheme(true);
            Assert.Equal(Wpf(AskPalette.Dark.Ink), BrushColor(box.Resources["Ask.Ink"]));
            Assert.Equal(Wpf(AskPalette.Dark.Ink), BrushColor(box.Foreground));
            Assert.Equal(Wpf(AskPalette.Dark.Ink), BrushColor(box.Document.Foreground));

            box.ApplyTheme(false);
            Assert.Equal(Wpf(AskPalette.Light.Ink), BrushColor(box.Resources["Ask.Ink"]));
            Assert.Equal(Wpf(AskPalette.Light.Ink), BrushColor(box.Foreground));
            Assert.Equal(Wpf(AskPalette.Light.Ink), BrushColor(box.Document.Foreground));   // the text already shown repaints
            Assert.Equal(Wpf(AskPalette.Light.Selection), BrushColor(box.SelectionBrush));
            Assert.NotEqual(Wpf(AskPalette.Dark.Ink), Wpf(AskPalette.Light.Ink));
        });

        [Fact]
        public void Every_menu_in_the_box_follows_the_pad_theme_without_an_ask_window() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();
            box.ApplyTheme(false);
            box.ShowMarkdown("Run this:\n\n```\ncode\n```");
            var code = Assert.Single(AiAskWindowTests.Descendants<TextBox>(box.Document));
            var menus = new[] { Assert.IsType<ContextMenu>(box.ContextMenu), Assert.IsType<ContextMenu>(code.ContextMenu) };

            foreach (var menu in menus)
            {
                Assert.Equal(ModernWpf.ElementTheme.Light, ModernWpf.ThemeManager.GetRequestedTheme(menu));
                Assert.Equal(Wpf(PadPalette.Light.Popup), BrushColor(menu.Resources["Pad.Popup"]));
            }
            Assert.Equal(new[] { ApplicationCommands.Copy, ApplicationCommands.SelectAll },
                code.ContextMenu!.Items.OfType<MenuItem>().Select(item => item.Command));
            Assert.All(code.ContextMenu.Items.OfType<MenuItem>(), item => Assert.Same(code, item.CommandTarget));

            box.ApplyTheme(true);

            foreach (var menu in menus)
            {
                Assert.Equal(ModernWpf.ElementTheme.Dark, ModernWpf.ThemeManager.GetRequestedTheme(menu));
                Assert.Equal(Wpf(PadPalette.Dark.Popup), BrushColor(menu.Resources["Pad.Popup"]));
            }
        });

        [Fact]
        public void The_text_has_no_page_padding_before_and_after_the_box_builds_its_view() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();

            box.ShowMarkdown("text");
            Assert.Equal(new Thickness(0), box.Document.PagePadding);

            Assert.True(box.ApplyTemplate());
            Assert.Equal(new Thickness(0), box.Document.PagePadding);

            box.ShowPlain("more");
            Assert.Equal(new Thickness(0), box.Document.PagePadding);
        });

        [Fact]
        public void Markdown_that_cannot_be_rendered_is_shown_as_plain_text_and_reported_once_by_type_only() => UiThread.Run(() =>
        {
            var warnings = new List<string>();
            var box = new PadAnswerBox
            {
                BuildDocument = _ => throw new InvalidOperationException("the note says hunter2"),
                Warn = warnings.Add,
            };

            box.ShowMarkdown("**bold**\nline two");
            box.ShowMarkdown("**bold**\nline two and more");

            Assert.Equal("**bold**\nline two and more", Rendered(box));
            Assert.Equal("**bold**\nline two and more", box.Shown);
            string warning = Assert.Single(warnings);
            Assert.Contains("InvalidOperationException", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", warning, StringComparison.Ordinal);   // never the message: it could quote the answer
            Assert.DoesNotContain("bold", warning, StringComparison.Ordinal);
        });

        [Fact]
        public void A_credential_marker_is_shown_as_it_is() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();

            box.ShowPlain("user: admin\npassword: {{secret:K7Q2M9XD}}");
            Assert.Equal("user: admin\npassword: {{secret:K7Q2M9XD}}", Rendered(box));

            box.ShowMarkdown("The password is {{secret:K7Q2M9XD}}.");
            Assert.Equal("The password is {{secret:K7Q2M9XD}}.", Rendered(box));
        });

        [Fact]
        public void The_box_lays_out_on_its_own_and_wraps_to_the_width_it_is_given() => UiThread.Run(() =>
        {
            var box = new PadAnswerBox();
            box.ShowMarkdown("# Title\n\nSome **text** with a [link](https://example.com), long enough that it has to wrap onto a second line in a narrow pane.\n\n- one\n- two\n\n```\ncode\n```");

            box.Measure(new Size(300, double.PositiveInfinity));
            box.Arrange(new Rect(box.DesiredSize));
            box.UpdateLayout();

            Assert.InRange(box.DesiredSize.Width, 1, 300);
            Assert.True(box.DesiredSize.Height > 100, "the height follows the text: " + box.DesiredSize.Height);
        });
    }
}
