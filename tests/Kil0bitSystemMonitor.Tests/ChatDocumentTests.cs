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
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using List = System.Windows.Documents.List;
using Size = System.Windows.Size;
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

        // ---- Mermaid diagrams: a fake source of pictures, never the drawing page -------------------

        private const string Flow = ChatDiagramFakes.Flow;

        /// <summary>The document for <paramref name="markdown"/> built with <paramref name="render"/>, in a host that carries the Ask.* brushes.</summary>
        private static FlowDocument BuildWith(string markdown, ChatRender render, AskPalette? palette = null)
        {
            var host = new System.Windows.Controls.RichTextBox();
            AskThemeApplier.ApplyResources(host.Resources, palette ?? AskPalette.Dark);
            var document = ChatDocument.Build(ChatMarkdown.Parse(markdown), render);
            host.Document = document;
            return document;
        }

        /// <summary>One Mermaid block whose picture is in <paramref name="state"/>.</summary>
        private static (FlowDocument Document, FakeChatDiagrams Diagrams) Diagram(ChatDiagramState state, ISet<string>? shown = null)
        {
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => state };
            var render = shown == null ? new ChatRender { Diagrams = diagrams } : new ChatRender { Diagrams = diagrams, SourceShown = shown };
            return (BuildWith(ChatDiagramFakes.Block(), render), diagrams);
        }

        private static Button ButtonNamed(FlowDocument document, string content) =>
            Assert.Single(ChatDocument.All<Button>(document), b => Equals(b.Content, content));

        private static List<string> Texts(FlowDocument document) => ChatDocument.All<TextBlock>(document).Select(t => t.Text).ToList();

        [Fact]
        public void A_mermaid_fence_that_is_still_open_is_code_and_no_picture_is_asked_for() => UiThread.Run(() =>
        {
            var diagrams = new FakeChatDiagrams();

            var document = BuildWith("Here it is:\n\n```mermaid\n" + Flow, new ChatRender { Diagrams = diagrams });

            Assert.Empty(diagrams.Gets);   // nothing is drawn, or even asked for, until the closing fence arrives
            Assert.Equal(Flow, Assert.Single(ChatDocument.All<TextBox>(document)).Text);
            Assert.Empty(ChatDocument.All<Image>(document));
            Assert.Equal(new[] { "mermaid" }, Texts(document));   // the code header's language, and no "Drawing…"
        });

        [Theory]
        [InlineData("mermaid")]
        [InlineData("Mermaid")]
        [InlineData("MERMAID")]
        public void A_closed_mermaid_fence_asks_for_its_picture_whatever_the_letter_case(string word) => UiThread.Run(() =>
        {
            var diagrams = new FakeChatDiagrams();
            Action invalidate = () => { };

            BuildWith(ChatDiagramFakes.Block(word: word), new ChatRender { Diagrams = diagrams, Dark = false, Invalidate = invalidate });

            var get = Assert.Single(diagrams.Gets);
            Assert.Equal(Flow, get.Source);
            Assert.False(get.Dark);                    // the view's theme
            Assert.Same(invalidate, get.WhenDone);     // and the view's own redraw
        });

        [Theory]
        [InlineData("dot")]
        [InlineData("graphviz")]
        [InlineData("markmap")]
        [InlineData("svg")]
        [InlineData("plantuml")]
        [InlineData("d2")]
        [InlineData("kroki")]
        [InlineData("math")]
        [InlineData("mmd")]
        [InlineData("mermaids")]
        [InlineData("")]
        public void Every_other_fence_word_is_code_and_no_picture_is_asked_for(string word) => UiThread.Run(() =>
        {
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => ChatDiagramFakes.Drawn() };

            var document = BuildWith(ChatDiagramFakes.Block(word: word), new ChatRender { Diagrams = diagrams });

            Assert.Empty(diagrams.Gets);
            Assert.Empty(ChatDocument.All<Image>(document));
            Assert.Equal(Flow, Assert.Single(ChatDocument.All<TextBox>(document)).Text);
            Assert.Equal("Copy code", Assert.Single(ChatDocument.All<Button>(document)).ToolTip);
        });

        [Fact]
        public void A_render_without_pictures_shows_a_mermaid_block_as_code() => UiThread.Run(() =>
        {
            var document = Build(ChatDiagramFakes.Block());

            Assert.Empty(ChatDocument.All<Image>(document));
            Assert.Equal(new[] { "mermaid" }, Texts(document));
            Assert.Equal("Copy code", Assert.Single(ChatDocument.All<Button>(document)).ToolTip);
        });

        [Fact]
        public void Of_fifty_diagrams_eight_are_asked_for_and_the_rest_are_code() => UiThread.Run(() =>
        {
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => ChatDiagramFakes.Drawn() };
            static string Source(int i) => "pie\n  \"a\" : " + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string markdown = string.Join("\n\n", Enumerable.Range(0, 50).Select(i => ChatDiagramFakes.Block(Source(i))));

            var document = BuildWith(markdown, new ChatRender { Diagrams = diagrams });

            Assert.Equal(8, ChatDiagrams.MaxPerAnswer);
            Assert.Equal(Enumerable.Range(0, 8).Select(Source), diagrams.Gets.Select(g => g.Source));
            var blocks = document.Blocks.ToList();
            Assert.Equal(50, blocks.Count);
            Assert.All(blocks.Take(8), b => Assert.Single(ChatDocument.All<Image>(b)));
            Assert.All(blocks.Skip(8), b =>
            {
                Assert.Empty(ChatDocument.All<Image>(b));
                Assert.Equal("Copy code", Assert.Single(ChatDocument.All<Button>(b)).ToolTip);
            });
            Assert.Equal(Source(8), ChatDocument.All<TextBox>(blocks[8]).Single().Text);   // the ninth keeps its text, as code
        });

        [Fact]
        public void A_diagram_that_is_off_is_a_code_block() => UiThread.Run(() =>
        {
            var (document, diagrams) = Diagram(new ChatDiagramState(ChatDiagramStatus.Off));

            Assert.Single(diagrams.Gets);
            var container = Assert.IsType<BlockUIContainer>(Assert.Single(document.Blocks));
            var inside = Assert.IsType<StackPanel>(Assert.IsType<Border>(container.Child).Child);   // exactly what a code block is
            Assert.Equal(Flow, Assert.IsType<TextBox>(inside.Children[1]).Text);
            Assert.Equal(new[] { "mermaid" }, Texts(document));
            Assert.Equal("Copy code", Assert.Single(ChatDocument.All<Button>(document)).ToolTip);
            Assert.Empty(ChatDocument.All<Image>(document));
        });

        [Fact]
        public void A_diagram_being_drawn_says_so_above_its_source() => UiThread.Run(() =>
        {
            var (document, _) = Diagram(new ChatDiagramState(ChatDiagramStatus.Drawing));

            var container = Assert.IsType<BlockUIContainer>(Assert.Single(document.Blocks));
            var parts = Assert.IsType<StackPanel>(container.Child).Children;
            var note = Assert.IsType<TextBlock>(parts[0]);
            Assert.Equal("Drawing the diagram…", note.Text);
            Assert.Equal(Color.FromArgb(0x88, 0xED, 0xED, 0xF2), ColorOf(note.Foreground));   // muted
            var code = Assert.IsType<Border>(parts[1]);
            Assert.Equal(Flow, Assert.Single(ChatDocument.All<TextBox>(code)).Text);
            Assert.Equal("Copy", ButtonNamed(document, "Copy").Content);
            Assert.Empty(ChatDocument.All<Image>(document));
        });

        [Fact]
        public void A_drawn_diagram_is_a_box_with_Diagram_Source_and_Copy_above_the_picture_at_most_its_own_size() => UiThread.Run(() =>
        {
            var state = ChatDiagramFakes.Drawn(width: 320, height: 120);

            var (document, _) = Diagram(state);

            var container = Assert.IsType<BlockUIContainer>(Assert.Single(document.Blocks));
            Assert.Equal(new Thickness(0), container.Margin);   // the last block of the answer
            var box = Assert.IsType<Border>(container.Child);
            Assert.Equal(new CornerRadius(8), box.CornerRadius);
            Assert.Equal(Color.FromRgb(0x16, 0x16, 0x1C), ColorOf(box.Background));
            Assert.Equal(new[] { "Diagram" }, Texts(document));

            var source = ButtonNamed(document, "Source");
            var copy = ButtonNamed(document, "Copy");
            Assert.Equal("Copy the diagram's source", copy.ToolTip);
            Assert.Equal(2, ChatDocument.All<Button>(document).Count);

            var picture = Assert.Single(ChatDocument.All<Image>(document));
            Assert.Same(state.Picture, picture.Source);
            Assert.Equal(Stretch.Uniform, picture.Stretch);
            Assert.Equal(StretchDirection.DownOnly, picture.StretchDirection);   // made smaller for a narrow answer, never enlarged
            Assert.Equal(320, picture.MaxWidth);
            Assert.Equal(120, picture.MaxHeight);
            Assert.Equal(HorizontalAlignment.Left, picture.HorizontalAlignment);
            Assert.Equal(Visibility.Visible, picture.Visibility);

            var text = Assert.Single(ChatDocument.All<TextBox>(document));
            Assert.Equal(Flow, text.Text);
            Assert.Equal(Visibility.Collapsed, text.Visibility);   // the source waits behind the toggle
            Assert.True(text.IsReadOnly);

            var menu = Assert.IsType<System.Windows.Controls.ContextMenu>(picture.ContextMenu);
            Assert.Equal(new object[] { "Copy image", "Copy source" }, menu.Items.OfType<System.Windows.Controls.MenuItem>().Select(i => i.Header));
            Assert.Empty(ChatDocument.All<Hyperlink>(document));   // a diagram is a picture: nothing in it can be clicked open
        });

        [Fact]
        public void The_box_around_a_picture_follows_the_theme() => UiThread.Run(() =>
        {
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => ChatDiagramFakes.Drawn() };

            var document = BuildWith(ChatDiagramFakes.Block(), new ChatRender { Diagrams = diagrams, Dark = false }, AskPalette.Light);

            var box = Assert.IsType<Border>(Assert.IsType<BlockUIContainer>(Assert.Single(document.Blocks)).Child);
            Assert.Equal(Color.FromRgb(0xF0, 0xF0, 0xF4), ColorOf(box.Background));
            var label = Assert.Single(ChatDocument.All<TextBlock>(document));
            Assert.Equal(Color.FromRgb(0x66, 0x66, 0x70), ColorOf(label.Foreground));
        });

        [Fact]
        public void A_diagram_that_failed_is_its_source_with_the_reason_under_it_as_plain_text() => UiThread.Run(() =>
        {
            const string reason = "Parse error on line 2: see [the docs](https://evil.example/x) or https://evil.example/y";

            var (document, _) = Diagram(ChatDiagramFakes.Failed(reason));

            var container = Assert.IsType<BlockUIContainer>(Assert.Single(document.Blocks));
            var parts = Assert.IsType<StackPanel>(container.Child).Children;
            var code = Assert.IsType<Border>(parts[0]);
            Assert.Equal(Flow, Assert.Single(ChatDocument.All<TextBox>(code)).Text);
            var note = Assert.IsType<TextBlock>(parts[1]);
            Assert.Equal("This diagram could not be drawn: " + reason, note.Text);   // exactly as the renderer said it
            Assert.Equal(TextWrapping.Wrap, note.TextWrapping);
            Assert.Equal(Color.FromArgb(0x88, 0xED, 0xED, 0xF2), ColorOf(note.Foreground));
            Assert.Empty(ChatDocument.All<Image>(document));

            // The message is never read as Markdown, so there is no link for RemoveLinks to find or to miss.
            Assert.Empty(ChatDocument.All<Hyperlink>(document));
            ChatDocument.RemoveLinks(document);
            Assert.Equal("This diagram could not be drawn: " + reason, note.Text);
        });

        [Fact]
        public void A_failure_without_a_message_still_says_something() => UiThread.Run(() =>
        {
            var (document, _) = Diagram(new ChatDiagramState(ChatDiagramStatus.Failed));

            Assert.Contains("This diagram could not be drawn: " + Kil0bitSystemMonitor.Services.Pad.DiagramText.Failed, Texts(document));
        });

        [Fact]
        public void The_Source_toggle_swaps_the_picture_and_the_code_and_keeps_the_choice_in_the_views_set() => UiThread.Run(() =>
        {
            var shown = new HashSet<string>(StringComparer.Ordinal);
            var (document, _) = Diagram(ChatDiagramFakes.Drawn(), shown);
            var toggle = ButtonNamed(document, "Source");
            var picture = Assert.Single(ChatDocument.All<Image>(document));
            var text = Assert.Single(ChatDocument.All<TextBox>(document));

            RaiseClick(toggle);

            Assert.Equal(Visibility.Collapsed, picture.Visibility);
            Assert.Equal(Visibility.Visible, text.Visibility);
            Assert.Equal(new[] { Flow }, shown);

            RaiseClick(toggle);

            Assert.Equal(Visibility.Visible, picture.Visibility);
            Assert.Equal(Visibility.Collapsed, text.Visibility);
            Assert.Empty(shown);
        });

        [Fact]
        public void A_document_built_again_with_the_same_set_shows_the_source_of_that_diagram_only() => UiThread.Run(() =>
        {
            var shown = new HashSet<string>(StringComparer.Ordinal) { Flow };
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => ChatDiagramFakes.Drawn() };
            string markdown = ChatDiagramFakes.Block() + "\n\n" + ChatDiagramFakes.Block("pie\n  \"a\" : 1");

            var document = BuildWith(markdown, new ChatRender { Diagrams = diagrams, SourceShown = shown });

            var blocks = document.Blocks.ToList();
            Assert.Equal(Visibility.Collapsed, ChatDocument.All<Image>(blocks[0]).Single().Visibility);
            Assert.Equal(Visibility.Visible, ChatDocument.All<TextBox>(blocks[0]).Single().Visibility);
            Assert.Equal(Visibility.Visible, ChatDocument.All<Image>(blocks[1]).Single().Visibility);
            Assert.Equal(Visibility.Collapsed, ChatDocument.All<TextBox>(blocks[1]).Single().Visibility);

            RaiseClick(ChatDocument.All<Button>(blocks[0]).Single(b => Equals(b.Content, "Source")));   // and back to the picture
            Assert.Equal(Visibility.Visible, ChatDocument.All<Image>(blocks[0]).Single().Visibility);
            Assert.Empty(shown);
        });

        [Fact]
        public void Two_renders_keep_their_Source_choices_apart() => UiThread.Run(() =>
        {
            var first = Diagram(ChatDiagramFakes.Drawn(), new HashSet<string>(StringComparer.Ordinal));
            var second = Diagram(ChatDiagramFakes.Drawn(), new HashSet<string>(StringComparer.Ordinal));

            RaiseClick(ButtonNamed(first.Document, "Source"));

            Assert.Equal(Visibility.Collapsed, ChatDocument.All<Image>(first.Document).Single().Visibility);
            Assert.Equal(Visibility.Visible, ChatDocument.All<Image>(second.Document).Single().Visibility);
        });

        [Fact]
        public void A_diagrams_Copy_and_Copy_source_hand_the_mermaid_text_to_the_one_clipboard_hook() => UiThread.Run(() =>
        {
            var (document, _) = Diagram(ChatDiagramFakes.Drawn());
            var copy = ButtonNamed(document, "Copy");

            Assert.Equal(new[] { Flow }, Click(copy));
            Assert.Equal("Copied", copy.Content);   // the same feedback as a code block's Copy

            var menu = ChatDocument.All<Image>(document).Single().ContextMenu!;
            var copySource = menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(i => Equals(i.Header, "Copy source"));
            var copied = new List<string>();
            var previous = ChatClipboard.SetText;
            ChatClipboard.SetText = copied.Add;
            try
            {
                copySource.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            }
            finally
            {
                ChatClipboard.SetText = previous;
            }
            Assert.Equal(new[] { Flow }, copied);
        });

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Copy_image_hands_the_picture_on_the_color_of_its_box_to_the_image_hook(bool dark) => UiThread.Run(() =>
        {
            var state = ChatDiagramFakes.Drawn(width: 4, height: 3);   // 8 by 6 pixels, every one transparent
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => state };
            var document = BuildWith(ChatDiagramFakes.Block(), new ChatRender { Diagrams = diagrams, Dark = dark }, dark ? AskPalette.Dark : AskPalette.Light);
            var menu = ChatDocument.All<Image>(document).Single().ContextMenu!;
            var copyImage = menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(i => Equals(i.Header, "Copy image"));

            var images = new List<System.Windows.Media.Imaging.BitmapSource>();
            var texts = new List<string>();
            var previousImage = ChatClipboard.SetImage;
            var previousText = ChatClipboard.SetText;
            ChatClipboard.SetImage = images.Add;
            ChatClipboard.SetText = texts.Add;
            try
            {
                copyImage.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            }
            finally
            {
                ChatClipboard.SetImage = previousImage;
                ChatClipboard.SetText = previousText;
            }

            Assert.Empty(texts);
            var image = Assert.Single(images);
            Assert.Equal(8, image.PixelWidth);
            Assert.Equal(6, image.PixelHeight);
            // A transparent picture pastes as black in most programs: it goes out on the color of the box it is shown in.
            var pixel = new byte[4];
            new System.Windows.Media.Imaging.FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(3, 2, 1, 1), pixel, 4, 0);
            var back = (dark ? AskPalette.Dark : AskPalette.Light).CodeBack;
            Assert.Equal(new[] { back.B, back.G, back.R, (byte)0xFF }, pixel);
        });

        [Fact]
        public void Clipboard_hooks_that_fail_do_not_throw_out_of_a_diagrams_menu_or_buttons() => UiThread.Run(() =>
        {
            var (document, _) = Diagram(ChatDiagramFakes.Drawn());
            var menu = ChatDocument.All<Image>(document).Single().ContextMenu!;
            var copy = ButtonNamed(document, "Copy");

            var previousImage = ChatClipboard.SetImage;
            var previousText = ChatClipboard.SetText;
            ChatClipboard.SetImage = _ => throw new InvalidOperationException("busy");
            ChatClipboard.SetText = _ => throw new InvalidOperationException("busy");
            try
            {
                foreach (var item in menu.Items.OfType<System.Windows.Controls.MenuItem>())
                    item.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                RaiseClick(copy);
            }
            finally
            {
                ChatClipboard.SetImage = previousImage;
                ChatClipboard.SetText = previousText;
            }

            Assert.Equal("Copy", copy.Content);   // a copy that failed does not claim "Copied"
        });

        private sealed class BrokenDiagrams : IChatDiagrams
        {
            public ChatDiagramState Get(string source, bool dark, Action? whenDone) => throw new InvalidOperationException("the source was " + source);

            public void Clear()
            {
            }
        }

        [Fact]
        public void A_source_of_pictures_that_throws_leaves_a_code_block_with_a_reason_and_the_rest_of_the_answer() => UiThread.Run(() =>
        {
            var document = BuildWith("Before.\n\n" + ChatDiagramFakes.Block() + "\n\nAfter.", new ChatRender { Diagrams = new BrokenDiagrams() });

            var blocks = document.Blocks.ToList();
            Assert.Equal(3, blocks.Count);
            Assert.Equal(Flow, ChatDocument.All<TextBox>(blocks[1]).Single().Text);
            Assert.Contains("This diagram could not be drawn: " + Kil0bitSystemMonitor.Services.Pad.DiagramText.Failed, Texts(document));
        });

        /// <summary>The size the picture of one drawn diagram is laid out at, in an answer in a window <paramref name="width"/> wide (off screen).</summary>
        private static Size PictureSizeIn(double width, ChatDiagramState state)
        {
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => state };
            var document = ChatDocument.Build(ChatMarkdown.Parse(ChatDiagramFakes.Block()), new ChatRender { Diagrams = diagrams });
            var box = new AnswerBox { Style = ChatStyles.Get("ChatAnswer") };
            AskThemeApplier.ApplyResources(box.Resources, AskPalette.Dark);
            box.Show(document);
            var window = new Window { Width = width, Height = 400, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, Content = box };
            try
            {
                window.Show();
                window.UpdateLayout();
                var picture = Assert.Single(ChatDocument.All<Image>(document));
                return new Size(picture.ActualWidth, picture.ActualHeight);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }

        [Fact]
        public void A_picture_is_shown_at_its_own_size_and_made_smaller_only_when_the_answer_is_narrower() => UiThread.Run(() =>
        {
            // 200 by 100 device-independent pixels, drawn with 400 by 200 pixels as the engine does.
            var size = PictureSizeIn(700, ChatDiagramFakes.Drawn(width: 200, height: 100));
            Assert.Equal(200, size.Width, 0.5);   // its own size: not the bitmap's 400, and not stretched to the answer's width
            Assert.Equal(100, size.Height, 0.5);

            size = PictureSizeIn(160, ChatDiagramFakes.Drawn(width: 200, height: 100));
            Assert.InRange(size.Width, 40, 140);                    // it fits inside the narrow answer
            Assert.Equal(size.Width / 2, size.Height, 0.5);         // and keeps its proportions
        });
    }
}
