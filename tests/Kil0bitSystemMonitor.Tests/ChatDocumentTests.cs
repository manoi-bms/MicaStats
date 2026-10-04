using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Services.Ai;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Xunit;

using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using List = System.Windows.Documents.List;
using TextBox = System.Windows.Controls.TextBox;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The FlowDocument an answer's Markdown becomes. Built on the UI test thread, never shown.</summary>
    public class ChatDocumentTests
    {
        /// <summary>
        /// The document, hosted in a box that carries the dark Ask.* brushes (as the window does), so
        /// the resource references the document makes resolve. Never shown.
        /// </summary>
        private static FlowDocument Build(string markdown, AskPalette? palette = null)
        {
            var host = new System.Windows.Controls.RichTextBox();
            AskThemeApplier.ApplyResources(host.Resources, palette ?? AskPalette.Dark);
            var document = ChatDocument.Build(ChatMarkdown.Parse(markdown));
            host.Document = document;
            return document;
        }

        private static List<T> All<T>(DependencyObject root) where T : DependencyObject =>
            AiAskWindowTests.Descendants<T>(root);

        private static Color ColorOf(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;

        [Fact]
        public void A_paragraph_keeps_its_line_breaks_and_the_document_reads_as_dark_chat_text() => UiThread.Run(() =>
        {
            var document = Build("one\ntwo");

            var paragraph = Assert.IsType<Paragraph>(Assert.Single(document.Blocks));
            Assert.Equal(new[] { typeof(Run), typeof(LineBreak), typeof(Run) }, paragraph.Inlines.Select(i => i.GetType()));
            Assert.Equal(14, document.FontSize);
            Assert.Equal(21, document.LineHeight);
            Assert.Equal(TextAlignment.Left, document.TextAlignment);
            Assert.Equal(Color.FromRgb(0xED, 0xED, 0xF2), ColorOf(document.Foreground));
            Assert.Equal(0, paragraph.Margin.Bottom);   // the last block adds no space below the answer
        });

        [Fact]
        public void The_light_palette_paints_the_document_too() => UiThread.Run(() =>
        {
            var document = Build("a [b](https://example.com) `c`", AskPalette.Light);

            Assert.Equal(Color.FromRgb(0x1B, 0x1B, 0x1F), ColorOf(document.Foreground));
            var link = All<Hyperlink>(document).First();
            Assert.Equal(Color.FromRgb(0x06, 0x70, 0x7C), ColorOf(link.Foreground));
        });

        [Fact]
        public void Paragraphs_are_spaced_eight_pixels_apart() => UiThread.Run(() =>
        {
            var blocks = Build("one\n\ntwo").Blocks.ToList();
            Assert.Equal(new Thickness(0, 0, 0, 8), blocks[0].Margin);
        });

        [Fact]
        public void Headings_are_semibold_at_18_16_and_15() => UiThread.Run(() =>
        {
            var headings = Build("# A\n## B\n### C\n#### D").Blocks.Cast<Paragraph>().ToList();
            Assert.Equal(new double[] { 18, 16, 15, 15 }, headings.Select(h => h.FontSize));
            Assert.All(headings, h => Assert.Equal(FontWeights.SemiBold, h.FontWeight));
        });

        [Fact]
        public void Inline_styles_become_run_properties() => UiThread.Run(() =>
        {
            var runs = All<Run>(Build("**b** *i* ~~s~~ `c`"));
            Run Find(string text) => runs.Single(r => r.Text == text);

            Assert.Equal(FontWeights.SemiBold, Find("b").FontWeight);
            Assert.Equal(FontStyles.Italic, Find("i").FontStyle);
            Assert.Same(TextDecorations.Strikethrough, Find("s").TextDecorations);
            var code = Find("c");
            Assert.Equal("Cascadia Mono, Consolas", code.FontFamily.Source);
            Assert.Equal(12.5, code.FontSize);
            Assert.Equal(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), ColorOf(code.Background));
        });

        [Fact]
        public void Lists_nest_by_depth_and_numbered_lists_start_at_their_number() => UiThread.Run(() =>
        {
            var document = Build("3. three\n   - a\n   - b\n4. four\n\nText\n\n- x");
            var blocks = document.Blocks.ToList();

            var numbered = Assert.IsType<List>(blocks[0]);
            Assert.Equal(TextMarkerStyle.Decimal, numbered.MarkerStyle);
            Assert.Equal(3, numbered.StartIndex);
            Assert.Equal(2, numbered.ListItems.Count);
            var nested = Assert.IsType<List>(numbered.ListItems.FirstListItem.Blocks.LastBlock);
            Assert.Equal(TextMarkerStyle.Disc, nested.MarkerStyle);   // small filled dots at every level; the indent shows nesting
            Assert.Equal(2, nested.ListItems.Count);

            Assert.IsType<Paragraph>(blocks[1]);
            var bullets = Assert.IsType<List>(blocks[2]);
            Assert.Equal(TextMarkerStyle.Disc, bullets.MarkerStyle);
        });

        /// <summary>How many lists sit inside one another, starting at <paramref name="block"/>.</summary>
        private static int ListNesting(Block? block) =>
            block is List list
                ? 1 + list.ListItems.SelectMany(item => item.Blocks).Select(ListNesting).DefaultIfEmpty(0).Max()
                : 0;

        [Fact]
        public void Bullets_use_small_filled_dots_at_every_depth() => UiThread.Run(() =>
        {
            var lists = All<List>(Build("- a\n  - b\n    - c\n      - d"));
            Assert.Equal(4, lists.Count);
            Assert.All(lists, l => Assert.Equal(TextMarkerStyle.Disc, l.MarkerStyle));
        });

        [Fact]
        public void Thousands_of_nested_bullets_build_at_most_seven_lists_deep() => UiThread.Run(() =>
        {
            string text = string.Join("\n", Enumerable.Range(0, 3000).Select(i => new string(' ', i) + "- a"));

            var document = ChatDocument.Build(ChatMarkdown.Parse(text));

            Assert.Equal(ChatMarkdown.MaxListDepth + 1, ListNesting(document.Blocks.FirstBlock));
        });

        [Fact]
        public void The_builder_clamps_a_depth_deeper_than_the_parser_allows() => UiThread.Run(() =>
        {
            var blocks = Enumerable.Range(0, 50).Select(depth => new ChatBlock(ChatBlockKind.Bullet, depth: depth)).ToList();

            var document = ChatDocument.Build(blocks);

            Assert.Equal(ChatMarkdown.MaxListDepth + 1, ListNesting(document.Blocks.FirstBlock));
            Assert.Equal(50, All<ListItem>(document).Count);
        });

        [Fact]
        public void A_list_numbered_from_zero_builds() => UiThread.Run(() =>
        {
            var list = Assert.IsType<List>(Assert.Single(Build("0. zero\n1. one").Blocks));
            Assert.Equal(1, list.StartIndex);   // WPF counts from 1 at the lowest
            Assert.Equal(2, list.ListItems.Count);
        });

        [Fact]
        public void A_bullet_after_a_numbered_item_at_the_same_depth_starts_a_new_list() => UiThread.Run(() =>
        {
            var blocks = Build("1. one\n- dash").Blocks.ToList();
            Assert.Equal(2, blocks.Count);
            Assert.Equal(TextMarkerStyle.Decimal, Assert.IsType<List>(blocks[0]).MarkerStyle);
            Assert.Equal(TextMarkerStyle.Disc, Assert.IsType<List>(blocks[1]).MarkerStyle);
        });

        [Fact]
        public void A_code_block_is_a_rounded_dark_box_of_selectable_monospace_text() => UiThread.Run(() =>
        {
            var container = Assert.IsType<BlockUIContainer>(Assert.Single(Build("```ps\nGet-Process\n  | Sort CPU\n```").Blocks));
            var box = Assert.IsType<Border>(container.Child);
            Assert.Equal(new CornerRadius(8), box.CornerRadius);
            Assert.Equal(new Thickness(10), box.Padding);
            Assert.Equal(Color.FromRgb(0x16, 0x16, 0x1C), ColorOf(box.Background));
            var inside = Assert.IsType<StackPanel>(box.Child);   // the header (language, Copy), then the code
            var text = Assert.IsType<TextBox>(inside.Children[1]);
            Assert.Equal("Get-Process\n  | Sort CPU", text.Text);
            Assert.True(text.IsReadOnly);
            Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
            Assert.Equal("Cascadia Mono, Consolas", text.FontFamily.Source);
        });

        [Fact]
        public void A_quote_has_an_accent_bar_and_muted_text() => UiThread.Run(() =>
        {
            var quote = Assert.IsType<Paragraph>(Assert.Single(Build("> quoted").Blocks));
            Assert.Equal(3, quote.BorderThickness.Left);
            Assert.Equal(Color.FromRgb(0x3F, 0xD2, 0xE4), ColorOf(quote.BorderBrush));
            Assert.Equal(Color.FromArgb(0x88, 0xED, 0xED, 0xF2), ColorOf(quote.Foreground));
        });

        [Fact]
        public void A_rule_is_a_one_pixel_line() => UiThread.Run(() =>
        {
            var rule = Assert.IsType<BlockUIContainer>(Build("a\n\n---\n\nb").Blocks.ElementAt(1));
            var line = Assert.IsType<Border>(rule.Child);
            Assert.Equal(1, line.Height);
            Assert.Equal(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), ColorOf(line.Background));
        });

        [Fact]
        public void A_link_is_one_accent_hyperlink_over_all_its_styled_runs() => UiThread.Run(() =>
        {
            var link = Assert.Single(All<Hyperlink>(Build("Read [**the** guide](https://example.com/g) now")));
            Assert.Equal(Color.FromRgb(0x3F, 0xD2, 0xE4), ColorOf(link.Foreground));
            Assert.Equal("https://example.com/g", link.ToolTip);
            Assert.Null(link.NavigateUri);   // opened only by our Click handler, never by WPF navigation
            Assert.Equal(new[] { "the", " guide" }, All<Run>(link).Select(r => r.Text));
        });

        [Fact]
        public void Only_safe_addresses_become_hyperlinks() => UiThread.Run(() =>
        {
            var document = Build("[a](file:///C:/x.exe) [b](javascript:alert(1)) [c](http://ok.example) mailto:me@example.com");
            Assert.Equal(new[] { "c", "mailto:me@example.com" }, All<Hyperlink>(document).Select(l => string.Concat(All<Run>(l).Select(r => r.Text))));
        });

        private const string TableWithLink =
            "| Name | Count |\n|:---|---:|\n| [site](https://example.com/a) | 2 |\n| plain | 3 |";

        private static string CellText(TableCell cell) => new TextRange(cell.ContentStart, cell.ContentEnd).Text.Trim();

        [Fact]
        public void A_table_block_builds_a_table_with_its_rows_cells_alignment_and_header_weight() => UiThread.Run(() =>
        {
            var document = Build("| Name | Count | Note |\n|:---|:---:|---:|\n| a | 1 | x |\n| b | 2 | y |");

            var table = Assert.IsType<Table>(Assert.Single(document.Blocks));
            Assert.Equal(3, table.Columns.Count);
            Assert.All(table.Columns, c => Assert.Equal(new GridLength(1, GridUnitType.Star), c.Width));
            var group = Assert.Single(table.RowGroups);
            Assert.Equal(3, group.Rows.Count);   // the header, then two body rows

            var header = group.Rows[0].Cells;
            Assert.Equal(new[] { "Name", "Count", "Note" }, header.Select(CellText));
            Assert.All(header, c => Assert.Equal(FontWeights.SemiBold, c.FontWeight));
            Assert.Equal(new[] { TextAlignment.Left, TextAlignment.Center, TextAlignment.Right }, header.Select(c => c.TextAlignment));
            Assert.Equal(new Thickness(8, 4, 8, 4), header[0].Padding);
            Assert.Equal(new Thickness(0, 0, 0, 1), header[0].BorderThickness);
            Assert.Equal(Color.FromRgb(0x16, 0x16, 0x1C), ColorOf(header[0].Background));

            var body = group.Rows[2].Cells;
            Assert.Equal(new[] { "b", "2", "y" }, body.Select(CellText));
            Assert.Equal(new[] { TextAlignment.Left, TextAlignment.Center, TextAlignment.Right }, body.Select(c => c.TextAlignment));
            Assert.NotEqual(FontWeights.SemiBold, body[0].FontWeight);
            Assert.Null(body[0].Background);
            Assert.Equal(new Thickness(0, 0, 0, 1), body[0].BorderThickness);
        });

        [Fact]
        public void A_table_with_no_body_rows_and_an_empty_cell_builds() => UiThread.Run(() =>
        {
            var document = Build("| a | b |\n|---|---|\n|  | x |");
            var table = Assert.IsType<Table>(Assert.Single(document.Blocks));
            Assert.Equal(2, Assert.Single(table.RowGroups).Rows.Count);

            var headerOnly = Build("| a | b |\n|---|---|");
            Assert.Single(Assert.IsType<Table>(Assert.Single(headerOnly.Blocks)).RowGroups[0].Rows);
        });

        [Fact]
        public void A_link_in_a_table_cell_is_found_and_RemoveLinks_turns_it_into_text() => UiThread.Run(() =>
        {
            var document = Build(TableWithLink);

            var link = Assert.Single(ChatDocument.All<Hyperlink>(document));   // the walk must reach into the table
            Assert.Equal("https://example.com/a", link.ToolTip);

            ChatDocument.RemoveLinks(document);

            Assert.Empty(ChatDocument.All<Hyperlink>(document));
            var table = Assert.IsType<Table>(Assert.Single(document.Blocks));
            string cell = CellText(table.RowGroups[0].Rows[1].Cells[0]);
            Assert.Contains("site", cell, StringComparison.Ordinal);
            Assert.Contains("https://example.com/a", cell, StringComparison.Ordinal);
        });

        [Fact]
        public void A_link_in_the_header_cell_and_in_a_table_after_a_list_goes_too() => UiThread.Run(() =>
        {
            var document = Build("1. item\n\n| [h](https://example.com/h) | b |\n|---|---|\n| [x](https://example.com/q) | y |\n\n- after [z](https://example.com/z)");

            Assert.Equal(3, ChatDocument.All<Hyperlink>(document).Count);
            ChatDocument.RemoveLinks(document);
            Assert.Empty(ChatDocument.All<Hyperlink>(document));
        });

        [Fact]
        public void A_table_of_100_rows_by_12_columns_builds() => UiThread.Run(() =>
        {
            var cells = Enumerable.Range(0, 12).Select(c => "c" + c.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            string row = "| " + string.Join(" | ", cells) + " |";
            string delimiter = "|" + string.Concat(Enumerable.Repeat("---|", 12));
            string markdown = row + "\n" + delimiter + "\n" + string.Join("\n", Enumerable.Repeat(row, 100));

            var document = Build(markdown);

            var table = Assert.IsType<Table>(Assert.Single(document.Blocks));
            Assert.Equal(101, table.RowGroups[0].Rows.Count);
            Assert.Equal(12, table.Columns.Count);
        });

        private static (Button Copy, TextBlock? Label) CodeHeader(FlowDocument document)
        {
            var copy = Assert.Single(ChatDocument.All<Button>(document));
            var label = ChatDocument.All<TextBlock>(document).FirstOrDefault();
            return (copy, label);
        }

        private static void RaiseClick(Button button) =>
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        /// <summary>Clicks with the shared hook replaced, so the real clipboard is never touched.</summary>
        private static List<string> Click(Button button)
        {
            var copied = new List<string>();
            var previous = ChatClipboard.SetText;
            ChatClipboard.SetText = copied.Add;
            try
            {
                RaiseClick(button);
            }
            finally
            {
                ChatClipboard.SetText = previous;
            }
            return copied;
        }

        [Fact]
        public void A_code_block_has_its_language_and_a_Copy_button_and_a_click_copies_the_code_exactly() => UiThread.Run(() =>
        {
            var document = Build("```csharp\nint x = 1;\n  return x;\n```");
            var (copy, label) = CodeHeader(document);

            Assert.Equal("csharp", label!.Text);
            Assert.Equal("Copy", copy.Content);
            Assert.Equal("Copy code", copy.ToolTip);
            Assert.Equal(new[] { "int x = 1;\n  return x;" }, Click(copy));
            Assert.Equal("Copied", copy.Content);

            var text = Assert.Single(ChatDocument.All<TextBox>(document));
            Assert.Equal("int x = 1;\n  return x;", text.Text);
        });

        [Fact]
        public void A_code_block_without_a_language_has_the_button_and_no_label() => UiThread.Run(() =>
        {
            var document = Build("```\nplain\n```");
            var (copy, label) = CodeHeader(document);

            Assert.Null(label);
            Assert.Equal("Copy", copy.Content);
        });

        [Fact]
        public void The_Copied_label_goes_back_to_Copy_when_the_timer_fires() => UiThread.Run(() =>
        {
            var previousFor = ChatDocument.CopiedFor;
            ChatDocument.CopiedFor = TimeSpan.FromMilliseconds(1);
            try
            {
                var (copy, _) = CodeHeader(Build("```\nx\n```"));
                Click(copy);
                Assert.Equal("Copied", copy.Content);

                // Event driven, not timed: pump until the label changes (the cap only stops a hang).
                var frame = new DispatcherFrame();
                var watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
                int polls = 0;
                watch.Tick += (s, e) =>
                {
                    if (!Equals(copy.Content, "Copied") || ++polls > 2000) frame.Continue = false;
                };
                watch.Start();
                Dispatcher.PushFrame(frame);
                watch.Stop();

                Assert.Equal("Copy", copy.Content);
            }
            finally
            {
                ChatDocument.CopiedFor = previousFor;
            }
        });

        [Fact]
        public void A_Copy_timer_that_fires_after_its_document_is_gone_does_not_throw_or_keep_it_alive() => UiThread.Run(() =>
        {
            var previousFor = ChatDocument.CopiedFor;
            ChatDocument.CopiedFor = TimeSpan.FromMilliseconds(1);
            try
            {
                WeakReference weak = ClickAndDrop();
                for (int i = 0; i < 3; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                Assert.False(weak.IsAlive);   // the pending timer does not hold the button

                // Let the orphaned timer fire: nothing is thrown.
                var frame = new DispatcherFrame();
                var watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                watch.Tick += (s, e) => frame.Continue = false;
                watch.Start();
                Dispatcher.PushFrame(frame);
                watch.Stop();
            }
            finally
            {
                ChatDocument.CopiedFor = previousFor;
            }
        });

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference ClickAndDrop()
        {
            var (copy, _) = CodeHeader(Build("```\nx\n```"));
            Click(copy);
            return new WeakReference(copy);
        }

        [Fact]
        public void A_failing_clipboard_hook_does_not_throw_out_of_the_click() => UiThread.Run(() =>
        {
            var (copy, _) = CodeHeader(Build("```\nx\n```"));
            var previous = ChatClipboard.SetText;
            ChatClipboard.SetText = _ => throw new InvalidOperationException("busy");
            try
            {
                RaiseClick(copy);
            }
            finally
            {
                ChatClipboard.SetText = previous;
            }

            Assert.Equal("Copy", copy.Content);   // a copy that failed does not claim "Copied"
        });

        [Fact]
        public void A_render_with_its_own_Copy_is_used_instead_of_the_shared_hook() => UiThread.Run(() =>
        {
            var copied = new List<string>();
            var host = new System.Windows.Controls.RichTextBox();
            AskThemeApplier.ApplyResources(host.Resources, AskPalette.Dark);
            var document = ChatDocument.Build(ChatMarkdown.Parse("```\ncode\n```"), new ChatRender { Copy = copied.Add });
            host.Document = document;

            Assert.Empty(Click(Assert.Single(ChatDocument.All<Button>(document))));   // the shared hook is not called
            Assert.Equal(new[] { "code" }, copied);
        });
    }
}
