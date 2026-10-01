using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Format → Format table (spec 3).</summary>
    public class TableFormatterTests
    {
        private static string Apply(string text, int offset)
        {
            var edit = TableFormatter.Format(text, offset);
            Assert.NotNull(edit);
            return text.Substring(0, edit!.Value.Offset) + edit.Value.Text + text.Substring(edit.Value.Offset + edit.Value.Length);
        }

        [Fact]
        public void Columns_line_up_by_their_alignment()
        {
            string text = "| Name | Qty |\n|:-|-:|\n| apple | 3 |\n| kiwi | 12 |";

            Assert.Equal("| Name  | Qty |\n| :---- | --: |\n| apple |   3 |\n| kiwi  |  12 |", Apply(text, 20));
        }

        [Fact]
        public void Centered_columns_and_tables_without_outer_pipes()
        {
            string text = "a|b\n:-:|---\nlong text|x";

            Assert.Equal("|     a     | b   |\n| :-------: | --- |\n| long text | x   |", Apply(text, 0));
        }

        [Fact]
        public void Uneven_rows_get_empty_cells_and_extra_cells_become_columns()
        {
            string text = "| a | b |\n|---|---|\n| 1 |\n| 1 | 2 | 3 |";

            Assert.Equal("| a   | b   |     |\n| --- | --- | --- |\n| 1   |     |     |\n| 1   | 2   | 3   |", Apply(text, 0));
        }

        [Fact]
        public void Wide_and_combining_characters_and_escaped_pipes_line_up_and_survive()
        {
            string thai = "\u0E1C\u0E25\u0E44\u0E21\u0E49";   // 4 columns: the tone mark takes none
            string han = "\u679C\u7269";                       // 4 columns: two wide characters
            string text = "| " + thai + " | " + han + " |\n|---|---|\n| a \\| b | x |";

            string result = Apply(text, 0);

            Assert.Equal("| " + thai + "   | " + han + " |\n| ------ | ---- |\n| a \\| b | x    |", result);
            var pipeColumns = result.Split('\n').Select(line =>
                TableCells.Pipes(line).Select(p => TableFormatter.DisplayWidth(line.Substring(0, p))).ToArray()).ToList();
            Assert.All(pipeColumns, columns => Assert.Equal(pipeColumns[0], columns));
        }

        [Theory]
        [InlineData("abc", 3)]
        [InlineData("\u0E2A\u0E27\u0E31\u0E2A\u0E14\u0E35", 4)]
        [InlineData("\u65E5\u672C", 4)]
        [InlineData("", 0)]
        public void Display_width_counts_wide_characters_twice_and_marks_never(string text, int width) =>
            Assert.Equal(width, TableFormatter.DisplayWidth(text));

        [Fact]
        public void Line_endings_and_the_text_around_the_table_are_kept()
        {
            string text = "before\r\n| a | b |\r\n|-|-|\r\n| 1 | 2 |\r\nafter";

            Assert.Equal("before\r\n| a   | b   |\r\n| --- | --- |\r\n| 1   | 2   |\r\nafter", Apply(text, 12));
        }

        [Theory]
        [InlineData("\t")]
        [InlineData("    ")]
        [InlineData("  \t ")]
        public void A_table_under_a_list_item_keeps_its_indent(string indent)
        {
            string text = "- item\n" + indent + "| a | bb |\n" + indent + "|-|-|\n" + indent + "| 1 | 2 |";

            Assert.Equal("- item\n" + indent + "| a   | bb  |\n" + indent + "| --- | --- |\n" + indent + "| 1   | 2   |", Apply(text, text.IndexOf('a')));
        }

        [Fact]
        public void A_formatted_table_stays_the_same_and_other_text_is_no_table()
        {
            string formatted = "| a   | b   |\n| --- | --- |\n| 1   | 2   |";

            Assert.Equal(formatted, Apply(formatted, 30));
            Assert.Null(TableFormatter.Format("just text\nmore", 3));
            Assert.Null(TableFormatter.TableAt("a | b\nc | d", 0));
            Assert.Equal((1, 3), TableFormatter.TableAt("x\n| a | b |\n|---|---|\n| 1 | 2 |\n\ny", 30));
        }

        [Fact]
        public void The_menu_item_formats_in_one_undo_step_and_is_off_outside_tables() => UiThread.Run(() =>
        {
            string text = "| a | bb |\n|-|-|\n| 1 | 2 |\n\nprose";
            var editor = new TextEditor { Document = new TextDocument(text) };

            editor.CaretOffset = 2;
            var item = EditorMenus.FormatMenu(editor).Items.OfType<MenuItem>().Single(i => (string)i.Header == "Format table");
            Assert.True(item.IsEnabled);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.StartsWith("| a   | bb  |\n| --- | --- |", editor.Document.Text);

            editor.Undo();
            Assert.Equal(text, editor.Document.Text);

            editor.CaretOffset = text.Length - 1;
            Assert.False(EditorMenus.FormatMenu(editor).Items.OfType<MenuItem>().Single(i => (string)i.Header == "Format table").IsEnabled);
        });
    }
}
