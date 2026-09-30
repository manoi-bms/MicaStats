using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using MenuItem = System.Windows.Controls.MenuItem;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The Tools menu in the window: where it is, what it edits, and what it says when it cannot.</summary>
    public class PadToolsTests
    {
        private static string[] Headers(MenuItem item) =>
            item.Items.Cast<object>().Select(i => i is MenuItem m ? (string)m.Header : "-").ToArray();

        /// <summary>Tools, or an item under it, in a freshly built editor menu.</summary>
        private static MenuItem Tool(MicaPadWindow window, params string[] path)
        {
            window.RefreshEditorMenu();
            var item = PadMenuTests.ItemOf(window.EditorMenu, "Tools");
            foreach (string header in path) item = item.Items.OfType<MenuItem>().Single(m => (string)m.Header == header);
            return item;
        }

        [Fact]
        public void Both_menus_offer_the_tools() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            Assert.Equal(new[] { "Base64 encode", "Base64 decode", "Convert number", "Insert GUID", "Insert timestamp", "Evaluate" },
                         Headers(Tool(window)));
            Assert.Equal(new[] { "Decimal", "Hex", "Binary", "Octal" }, Headers(Tool(window, "Convert number")));
            Assert.Equal(new[] { "ISO 8601", "Date", "Unix seconds" }, Headers(Tool(window, "Insert timestamp")));
            Assert.Contains("Tools", PadMenuTests.Headers(window.BuildMainMenu()));
        });

        [Fact]
        public void Selection_tools_wait_for_a_selection() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "255";
            window.Editor.Select(0, 0);
            Assert.False(Tool(window, "Base64 encode").IsEnabled);
            Assert.False(Tool(window, "Base64 decode").IsEnabled);
            Assert.False(Tool(window, "Convert number").IsEnabled);
            Assert.False(Tool(window, "Evaluate").IsEnabled);
            Assert.True(Tool(window, "Insert GUID").IsEnabled);
            Assert.True(Tool(window, "Insert timestamp").IsEnabled);

            window.Editor.Select(0, 3);
            Assert.True(Tool(window, "Convert number").IsEnabled);
        });

        [Fact]
        public void Evaluate_is_one_undo_step_and_selects_the_result() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "2+3";
            window.Editor.Select(0, 3);

            PadMenuTests.Click(Tool(window, "Evaluate"));

            Assert.Equal("2+3 = 5", window.Editor.Document.Text);
            Assert.Equal("5", window.Editor.SelectedText);
            window.Editor.Undo();
            Assert.Equal("2+3", window.Editor.Document.Text);
        });

        [Fact]
        public void Convert_number_rewrites_the_selection() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "mask 255;";
            window.Editor.Select(5, 3);

            PadMenuTests.Click(Tool(window, "Convert number", "Hex"));

            Assert.Equal("mask 0xFF;", window.Editor.Document.Text);
            Assert.Equal("0xFF", window.Editor.SelectedText);
        });

        [Fact]
        public void A_tool_that_cannot_apply_leaves_the_text_and_says_why() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "hello";
            window.Editor.Select(0, 5);

            PadMenuTests.Click(Tool(window, "Convert number", "Hex"));

            Assert.Equal("hello", window.Editor.Document.Text);
            Assert.Equal(Visibility.Visible, window.StatusMessage.Visibility);
            Assert.Equal(NumberConverter.NotANumber, window.StatusMessage.Text);

            PadMenuTests.Click(Tool(window, "Base64 decode"));
            Assert.Equal("hello", window.Editor.Document.Text);
            Assert.Equal(TextTools.NotBase64, window.StatusMessage.Text);
        });

        [Fact]
        public void Base64_that_decodes_to_nothing_leaves_the_selection_and_says_so() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "bom: 77u/";
            window.Editor.Select(5, 4);

            PadMenuTests.Click(Tool(window, "Base64 decode"));

            Assert.Equal("bom: 77u/", window.Editor.Document.Text);
            Assert.Equal(Visibility.Visible, window.StatusMessage.Visibility);
            Assert.Equal(TextTools.DecodesToNothing, window.StatusMessage.Text);
        });

        [Fact]
        public void Base64_from_the_menu_round_trips_thai_text() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "สวัสดี";
            window.Editor.SelectAll();
            PadMenuTests.Click(Tool(window, "Base64 encode"));
            Assert.Equal("4Liq4Lin4Lix4Liq4LiU4Li1", window.Editor.Document.Text);

            window.Editor.SelectAll();
            PadMenuTests.Click(Tool(window, "Base64 decode"));
            Assert.Equal("สวัสดี", window.Editor.Document.Text);
        });

        [Fact]
        public void Timestamps_come_from_the_window_clock_in_the_gregorian_calendar() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            try
            {
                window.Now = () => new DateTimeOffset(2026, 9, 30, 18, 5, 12, TimeSpan.FromHours(7));
                window.Editor.Document.Text = "";

                PadMenuTests.Click(Tool(window, "Insert timestamp", "ISO 8601"));
                PadMenuTests.Click(Tool(window, "Insert timestamp", "Date"));

                Assert.Equal("2026-09-30T18:05:12+07:002026-09-30", window.Editor.Document.Text);
                Assert.Equal(window.Editor.Document.TextLength, window.Editor.CaretOffset);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        });

        [Fact]
        public void Insert_guid_writes_a_lowercase_guid() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "";
            PadMenuTests.Click(Tool(window, "Insert GUID"));
            Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", window.Editor.Document.Text);
        });

        [Fact]
        public void A_rectangle_is_refused_and_the_text_left_alone() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "123456\n789012";
            var area = window.Editor.TextArea;
            // An unshown window has no layout, and a rectangle is measured in visual columns.
            window.Measure(new System.Windows.Size(800, 600));
            window.Arrange(new Rect(0, 0, 800, 600));
            window.UpdateLayout();
            area.TextView.EnsureVisualLines();
            area.Selection = new ICSharpCode.AvalonEdit.Editing.RectangleSelection(
                area, new ICSharpCode.AvalonEdit.TextViewPosition(1, 2), new ICSharpCode.AvalonEdit.TextViewPosition(2, 4));

            Assert.False(EditorMenus.RunTool(window.Editor, window.ShowStatus, TextTools.Evaluate));
            Assert.Equal("123456\n789012", window.Editor.Document.Text);
            Assert.Equal(EditorMenus.RectangleRefused, window.StatusMessage.Text);
        });

        [Fact]
        public void The_main_menus_tools_are_off_while_the_history_preview_covers_the_note() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            Assert.True(PadMenuTests.ItemOf(window.BuildMainMenu(), "Tools").IsEnabled);
            window.PreviewPanel.Visibility = Visibility.Visible;
            Assert.False(PadMenuTests.ItemOf(window.BuildMainMenu(), "Tools").IsEnabled);
        });
    }
}
