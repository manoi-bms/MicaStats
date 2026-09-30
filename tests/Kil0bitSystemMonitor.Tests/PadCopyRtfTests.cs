using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;
using DataFormats = System.Windows.DataFormats;
using IDataObject = System.Windows.IDataObject;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Copy as RTF from the window: what is copied, in which colors, and a busy clipboard.</summary>
    public class PadCopyRtfTests
    {
        [Fact]
        public void The_selection_is_copied_as_rtf_and_text() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            IDataObject? copied = null;
            window.TrySetClipboard = data => { copied = data; return true; };
            window.Editor.Document.Text = "# Title\n**bold** rest";
            window.Editor.Select(8, 8);

            window.CopyAsRtf();

            Assert.Equal("**bold**", copied!.GetData(DataFormats.UnicodeText));
            string rtf = (string)copied.GetData(DataFormats.Rtf);
            Assert.Matches(@"\\b\\ab\\fs(\d+)\\afs\1 (\*\*bold\*\*|bold)", rtf);
            Assert.Contains("bold", rtf);
        });

        [Fact]
        public void Nothing_selected_copies_the_whole_note() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            IDataObject? copied = null;
            window.TrySetClipboard = data => { copied = data; return true; };
            window.Editor.Document.Text = "one\ntwo";
            window.Editor.Select(0, 0);

            window.CopyAsRtf();

            Assert.Equal("one\ntwo", copied!.GetData(DataFormats.UnicodeText));
        });

        [Fact]
        public void Colors_come_from_the_light_palette_even_in_the_dark_theme() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            IDataObject? copied = null;
            window.TrySetClipboard = data => { copied = data; return true; };
            PadLanguageWindowTests.OpenFile(window, env, "a.json", "{ \"key\": \"value\" }");
            window.Editor.Select(0, 0);
            Assert.True(window.Palette.IsDark);

            window.CopyAsRtf();

            string rtf = (string)copied!.GetData(DataFormats.Rtf);
            var light = PadPalette.Light.SyntaxString;
            Assert.Contains($@"\red{light.R}\green{light.G}\blue{light.B};", rtf);
            var dark = PadPalette.Dark.SyntaxString;
            Assert.DoesNotContain($@"\red{dark.R}\green{dark.G}\blue{dark.B};", rtf);
        });

        [Fact]
        public void A_busy_clipboard_is_retried_then_reported() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            int attempts = 0;
            window.TrySetClipboard = data => { attempts++; return false; };
            window.Editor.Document.Text = "x";

            window.CopyAsRtf();

            Assert.Equal(4, attempts);                          // the first try and three retries
            Assert.Equal("Clipboard busy, try again", window.StatusMessage.Text);
        });

        [Fact]
        public void A_clipboard_that_frees_up_is_used() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            int attempts = 0;
            window.TrySetClipboard = data => ++attempts >= 2;
            window.Editor.Document.Text = "x";

            window.CopyAsRtf();

            Assert.Equal(2, attempts);
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
        public void A_copy_over_the_size_cap_is_refused() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            int calls = 0;
            window.TrySetClipboard = data => { calls++; return true; };
            window.Editor.Document.Text = new string('a', MicaPadWindow.MaxRtfChars + 1);

            window.CopyAsRtf();

            Assert.Equal(0, calls);
            Assert.Equal("Too large to copy as RTF", window.StatusMessage.Text);
        });

        [Fact]
        public void An_unexpected_failure_is_logged_and_reported() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var warnings = new List<string>();
            window.Warn = warnings.Add;
            window.TrySetClipboard = data => throw new System.InvalidOperationException("boom");
            window.Editor.Document.Text = "x";

            window.CopyAsRtf();

            Assert.Equal("Copy as RTF failed", window.StatusMessage.Text);
            Assert.Single(warnings);
            Assert.Contains("InvalidOperationException", warnings[0]);
        });
    }
}
