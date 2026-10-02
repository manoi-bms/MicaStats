using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using ICSharpCode.AvalonEdit.Editing;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Copy as RTF from the window: what is copied, in which colors, and a busy clipboard.</summary>
    public class PadCopyRtfTests
    {
        /// <summary>What reached the (fake) clipboard.</summary>
        private sealed class Copied
        {
            public string? Rtf;
            public string? Text;
            public int Calls;
        }

        private static Copied CaptureClipboard(MicaPadWindow window)
        {
            var copied = new Copied();
            window.TrySetClipboard = (rtf, text) => { copied.Rtf = rtf; copied.Text = text; copied.Calls++; return true; };
            return copied;
        }

        /// <summary>The \cf / \chcbpat number of a color in the RTF's color table (1-based after the "auto" entry).</summary>
        private static int ColorNumber(string rtf, PadColor color)
        {
            string table = Regex.Match(rtf, @"\{\\colortbl ;([^}]*)\}").Groups[1].Value;
            var entries = table.Split(';');
            int i = System.Array.IndexOf(entries, $@"\red{color.R}\green{color.G}\blue{color.B}");
            Assert.True(i >= 0, "the color " + color + " is not in the table");
            return i + 1;
        }

        [Fact]
        public void The_selection_is_copied_as_rtf_and_text() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var copied = CaptureClipboard(window);
            window.Editor.Document.Text = "# Title\n**bold** rest";
            window.Editor.Select(8, 8);

            window.CopyAsRtf();

            Assert.Equal("**bold**", copied.Text);
            Assert.Matches(@"\\b\\ab\\fs(\d+)\\afs\1 (\*\*bold\*\*|bold)", copied.Rtf);
            Assert.Contains("bold", copied.Rtf);
        });

        [Fact]
        public void Nothing_selected_copies_the_whole_note() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var copied = CaptureClipboard(window);
            window.Editor.Document.Text = "one\ntwo";
            window.Editor.Select(0, 0);

            window.CopyAsRtf();

            Assert.Equal("one\ntwo", copied.Text);
        });

        [Fact]
        public void A_rectangular_selection_copies_the_rectangle_without_styles() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var copied = CaptureClipboard(window);
            PadLanguageWindowTests.OpenFile(window, env, "a.cs", "int ab = 1;\nint cd = 2;");
            var area = window.Editor.TextArea;
            area.Selection = new RectangleSelection(area, new ICSharpCode.AvalonEdit.TextViewPosition(1, 1),
                                                    new ICSharpCode.AvalonEdit.TextViewPosition(2, 6));

            window.CopyAsRtf();

            Assert.Equal("int a\r\nint c", copied.Text);
            Assert.Contains(@"int a\par" + "\r\n" + "int c}", copied.Rtf);
            Assert.Single(Regex.Matches(copied.Rtf!, @"\\plain"));             // the keyword color is not applied
        });

        [Fact]
        public void Colors_come_from_the_light_palette_even_in_the_dark_theme() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var copied = CaptureClipboard(window);
            PadLanguageWindowTests.OpenFile(window, env, "a.json", "{ \"key\": \"value\" }");
            window.Editor.Select(0, 0);
            Assert.True(window.Palette.IsDark);

            window.CopyAsRtf();

            string rtf = copied.Rtf!;
            var light = PadPalette.Light.SyntaxString;
            Assert.Contains($@"\red{light.R}\green{light.G}\blue{light.B};", rtf);
            var dark = PadPalette.Dark.SyntaxString;
            Assert.DoesNotContain($@"\red{dark.R}\green{dark.G}\blue{dark.B};", rtf);
        });

        [Theory]
        [InlineData("a.go", "func main() { }", "main", "func")]
        [InlineData("a.js", "if (x) { foo(1); }", "foo", "if")]
        [InlineData("a.php", "<?php if ($x) { f($x); }", "f", "if")]    // PHP colors "if (" as a call; the guard makes it a keyword
        public void Function_names_have_the_light_function_color_as_on_screen(string file, string code, string function, string keyword) =>
            PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var copied = CaptureClipboard(window);
            PadLanguageWindowTests.OpenFile(window, env, file, code);
            window.Editor.Select(0, 0);

            window.CopyAsRtf();

            string rtf = copied.Rtf!;
            int call = ColorNumber(rtf, PadPalette.Light.SyntaxFunction);
            int word = ColorNumber(rtf, PadPalette.Light.SyntaxKeyword);
            Assert.Matches($@"\\cf{call}(\\[a-z]+)*\\fs(\d+)\\afs\2 {function}\\plain", rtf);   // bold where the definition is
            Assert.Matches($@"\\cf{word}(\\[a-z]+)*\\fs(\d+)\\afs\2 {keyword}\\plain", rtf);
        });

        [Fact]
        public void A_powershell_argument_is_copied_without_the_function_color() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var copied = CaptureClipboard(window);
            PadLanguageWindowTests.OpenFile(window, env, "a.ps1", "$t = \"{0}\" -f ($x); $t.Trim()");
            window.Editor.Select(0, 0);

            window.CopyAsRtf();

            string rtf = copied.Rtf!;
            int call = ColorNumber(rtf, PadPalette.Light.SyntaxFunction);
            Assert.Matches($@"\\cf{call}(\\[a-z]+)*\\fs(\d+)\\afs\2 Trim\\plain", rtf);
            Assert.DoesNotMatch($@"\\cf{call}(\\[a-z]+)*\\fs(\d+)\\afs\2 f\\plain", rtf);
        });

        [Theory]
        [InlineData(null)]                 // a note: Markdown
        [InlineData("a.txt")]              // plain text
        [InlineData("a.json")]
        public void A_bare_link_has_the_light_link_color_in_every_language(string? file) => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var copied = CaptureClipboard(window);
            const string text = "see https://example.com/a now";
            if (file == null) window.Editor.Document.Text = text;
            else PadLanguageWindowTests.OpenFile(window, env, file, text);
            window.Editor.Select(0, 0);

            window.CopyAsRtf();

            int link = ColorNumber(copied.Rtf!, PadPalette.Light.MdLink);
            Assert.Matches($@"\\cf{link}\\fs(\d+)\\afs\1 https://example\.com/a\\plain", copied.Rtf);
            var dark = PadPalette.Dark.MdLink;
            Assert.DoesNotContain($@"\red{dark.R}\green{dark.G}\blue{dark.B};", copied.Rtf);
        });

        [Fact]
        public void Fenced_code_lines_are_shaded_like_the_editor_shows_them() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var copied = CaptureClipboard(window);
            window.Editor.Document.Text = "before\n```\ncode\n```\nafter";
            window.Editor.Select(0, 0);

            window.CopyAsRtf();

            string rtf = copied.Rtf!;
            int shade = ColorNumber(rtf, PadPalette.Light.MdCodeBackground);
            Assert.Matches($@"\\chcbpat{shade}\\fs(\d+)\\afs\1 code\\par", rtf);
            Assert.Matches($@"\\chcbpat{shade}\\fs(\d+)\\afs\1 ```", rtf);
            Assert.DoesNotMatch(@"\\chcbpat\d+\\fs(\d+)\\afs\1 before", rtf);
            Assert.DoesNotMatch(@"\\chcbpat\d+\\fs(\d+)\\afs\1 after", rtf);
        });

        [Fact]
        public void A_busy_clipboard_is_tried_once_then_reported() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            int attempts = 0;
            window.TrySetClipboard = (rtf, text) => { attempts++; return false; };
            window.Editor.Document.Text = "x";

            window.CopyAsRtf();

            Assert.Equal(1, attempts);                          // the clipboard call retries on its own, 3 x 100 ms
            Assert.Equal(Visibility.Visible, window.StatusMessage.Visibility);
            Assert.Equal("Clipboard busy, try again", window.StatusMessage.Text);
        });

        [Fact]
        public void A_free_clipboard_is_used_once_without_a_message() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var copied = CaptureClipboard(window);
            window.Editor.Document.Text = "x";

            window.CopyAsRtf();

            Assert.Equal(1, copied.Calls);
            Assert.NotEqual(Visibility.Visible, window.StatusMessage.Visibility);
        });

        [Fact]
        public void Both_menus_offer_copy_as_rtf() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.RefreshEditorMenu();
            Assert.Contains("Copy as RTF", PadMenuTests.Headers(window.EditorMenu));
            Assert.Contains("Copy as RTF", PadMenuTests.Headers(window.BuildMainMenu()));
        });

        [Fact]
        public void The_menu_item_is_disabled_while_a_history_version_is_shown() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            Assert.True(PadMenuTests.ItemOf(window.BuildMainMenu(), "Copy as RTF").IsEnabled);

            // Set directly: showing a real version needs a snapshot and the history panel's selection.
            window.PreviewPanel.Visibility = Visibility.Visible;

            Assert.False(PadMenuTests.ItemOf(window.BuildMainMenu(), "Copy as RTF").IsEnabled);
            window.RefreshPreviewMenu();
            Assert.DoesNotContain("Copy as RTF", PadMenuTests.Headers(window.PreviewMenu));
        });

        [Fact]
        public void A_copy_over_the_size_cap_is_refused() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var copied = CaptureClipboard(window);
            window.Editor.Document.Text = new string('a', MicaPadWindow.MaxRtfChars + 1);

            window.CopyAsRtf();

            Assert.Equal(0, copied.Calls);
            Assert.Equal("Too large to copy as RTF", window.StatusMessage.Text);
        });

        [Fact]
        public void A_copy_whose_rtf_would_be_too_large_is_refused() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var copied = CaptureClipboard(window);
            // Under the character cap, but each Thai letter is an 8-character escape in the RTF.
            int letters = (int)(MicaPadWindow.MaxRtfEstimate / 8) + 1;
            Assert.True(letters < MicaPadWindow.MaxRtfChars);
            window.Editor.Document.Text = new string((char)0x0E01, letters);

            window.CopyAsRtf();

            Assert.Equal(0, copied.Calls);
            Assert.Equal("Too large to copy as RTF", window.StatusMessage.Text);
        });

        [Fact]
        public void An_unexpected_failure_is_logged_and_reported() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var warnings = new List<string>();
            window.Warn = warnings.Add;
            window.TrySetClipboard = (rtf, text) => throw new System.InvalidOperationException("boom");
            window.Editor.Document.Text = "x";

            window.CopyAsRtf();

            Assert.Equal("Copy as RTF failed", window.StatusMessage.Text);
            Assert.Single(warnings);
            Assert.Contains("InvalidOperationException", warnings[0]);
        });
    }
}
