using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;

// UseWindowsForms puts System.Windows.Forms and System.Drawing in scope; these names exist in both.
using Cursors = System.Windows.Input.Cursors;
using List = System.Windows.Documents.List;
using TextBox = System.Windows.Controls.TextBox;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// Turns parsed answer Markdown (<see cref="ChatMarkdown"/>) into the FlowDocument the Ask
    /// window shows: paragraphs with line breaks, headings, nested lists, quotes, code blocks,
    /// rules and styled runs. Links open only through <see cref="Open"/>, which allows http, https
    /// and mailto and nothing else, as MicaPad does.
    /// </summary>
    internal static class ChatDocument
    {
        private static readonly Thickness ParagraphSpacing = new(0, 0, 0, 8);

        /// <summary>
        /// Opens an allowed link in the default browser or mail program. Failures are logged,
        /// never thrown. Tests replace it so nothing is ever launched.
        /// </summary>
        internal static Action<Uri> OpenLink { get; set; } = uri =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                // The type only: the message could quote the address.
                DiagnosticsLog.Warn("ai", "A link in an answer could not be opened (" + ex.GetType().Name + ")");
            }
        };

        /// <summary>A link was clicked. Anything but http, https or mailto is ignored, whatever asked for it.</summary>
        internal static void Open(Uri uri)
        {
            if (!SafeLinks.IsAllowed(uri)) return;
            try
            {
                OpenLink(uri);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Warn("ai", "A link in an answer could not be opened (" + ex.GetType().Name + ")");
            }
        }

        /// <summary>The document for <paramref name="blocks"/>, styled for the dark chat.</summary>
        public static FlowDocument Build(IReadOnlyList<ChatBlock> blocks)
        {
            var document = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = ChatPalette.TextFont,
                FontSize = ChatPalette.TextSize,
                Foreground = ChatPalette.Ink,
                LineHeight = ChatPalette.LineHeight,
                TextAlignment = TextAlignment.Left,
            };

            var lists = new List<(List List, int Depth, ChatBlockKind Kind)>();
            foreach (ChatBlock block in blocks)
            {
                if (block.Kind is ChatBlockKind.Bullet or ChatBlockKind.Numbered)
                {
                    AddItem(document, lists, block);
                    continue;
                }

                lists.Clear();
                document.Blocks.Add(block.Kind switch
                {
                    ChatBlockKind.Heading => Heading(block),
                    ChatBlockKind.Quote => Quote(block),
                    ChatBlockKind.Code => CodeBlock(block),
                    ChatBlockKind.Rule => Rule(),
                    _ => Paragraph(block.Runs, ParagraphSpacing),
                });
            }

            // The answer's own spacing ends with its last line, not 8 px below it.
            if (document.Blocks.LastBlock is { } last) last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
            return document;
        }

        private static Paragraph Paragraph(IReadOnlyList<ChatRun> runs, Thickness margin)
        {
            var paragraph = new Paragraph { Margin = margin };
            AddInlines(paragraph.Inlines, runs);
            return paragraph;
        }

        private static Paragraph Heading(ChatBlock block)
        {
            double size = block.Level switch { 1 => 18, 2 => 16, _ => 15 };
            var heading = Paragraph(block.Runs, new Thickness(0, 4, 0, 6));
            heading.FontSize = size;
            heading.FontWeight = FontWeights.SemiBold;
            heading.LineHeight = Math.Round(size * 1.35);
            return heading;
        }

        private static Paragraph Quote(ChatBlock block)
        {
            var quote = Paragraph(block.Runs, ParagraphSpacing);
            quote.BorderBrush = ChatPalette.Accent;
            quote.BorderThickness = new Thickness(3, 0, 0, 0);
            quote.Padding = new Thickness(10, 2, 0, 2);
            quote.Foreground = ChatPalette.Muted;
            return quote;
        }

        /// <summary>A rounded dark box with the code, selectable and wrapped, in a monospace font.</summary>
        private static BlockUIContainer CodeBlock(ChatBlock block)
        {
            var text = new TextBox
            {
                Style = ChatStyles.Get("ChatReadOnlyText"),
                Text = block.Code,
                FontFamily = ChatPalette.MonoFont,
                FontSize = ChatPalette.CodeSize,
            };
            var box = new Border
            {
                Background = ChatPalette.CodeBack,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Child = text,
            };
            return new BlockUIContainer(box) { Margin = ParagraphSpacing };
        }

        private static BlockUIContainer Rule() =>
            new(new Border { Height = 1, Background = ChatPalette.Divider, Margin = new Thickness(0, 4, 0, 4) })
            {
                Margin = ParagraphSpacing,
            };

        /// <summary>
        /// Adds a list item, opening, nesting and closing lists by depth. A numbered list starts at
        /// its first item's own number, so a list split by a paragraph or code keeps counting.
        /// </summary>
        private static void AddItem(FlowDocument document, List<(List List, int Depth, ChatBlockKind Kind)> lists, ChatBlock item)
        {
            while (lists.Count > 0 && lists[^1].Depth > item.Depth) lists.RemoveAt(lists.Count - 1);
            if (lists.Count > 0 && lists[^1].Depth == item.Depth && lists[^1].Kind != item.Kind) lists.RemoveAt(lists.Count - 1);

            if (lists.Count == 0 || lists[^1].Depth < item.Depth)
            {
                bool numbered = item.Kind == ChatBlockKind.Numbered;
                var list = new List
                {
                    MarkerStyle = numbered ? TextMarkerStyle.Decimal : (lists.Count % 3) switch
                    {
                        0 => TextMarkerStyle.Disc,
                        1 => TextMarkerStyle.Circle,
                        _ => TextMarkerStyle.Square,
                    },
                    Padding = new Thickness(numbered ? 28 : 22, 0, 0, 0),
                    Margin = lists.Count == 0 ? ParagraphSpacing : new Thickness(0, 2, 0, 2),
                };
                if (numbered) list.StartIndex = Math.Max(1, item.Number);

                if (lists.Count == 0) document.Blocks.Add(list);
                else lists[^1].List.ListItems.LastListItem.Blocks.Add(list);
                lists.Add((list, item.Depth, item.Kind));
            }

            lists[^1].List.ListItems.Add(new ListItem(Paragraph(item.Runs, new Thickness(0, 0, 0, 3))));
        }

        /// <summary>Adds styled runs; consecutive runs of one link share one Hyperlink.</summary>
        private static void AddInlines(InlineCollection target, IReadOnlyList<ChatRun> runs)
        {
            int k = 0;
            while (k < runs.Count)
            {
                if (runs[k].Link is not { } uri)
                {
                    AddRun(target, runs[k]);
                    k++;
                    continue;
                }

                var link = new Hyperlink
                {
                    Foreground = ChatPalette.Accent,
                    Cursor = Cursors.Hand,
                    ToolTip = uri.AbsoluteUri,
                };
                link.Click += (s, e) => Open(uri);
                while (k < runs.Count && ReferenceEquals(runs[k].Link, uri))
                {
                    AddRun(link.Inlines, runs[k]);
                    k++;
                }
                target.Add(link);
            }
        }

        private static void AddRun(InlineCollection target, ChatRun run)
        {
            string[] lines = run.Text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) target.Add(new LineBreak());
                if (lines[i].Length > 0) target.Add(Styled(new Run(lines[i]), run));
            }
        }

        private static Run Styled(Run text, ChatRun run)
        {
            if (run.Bold) text.FontWeight = FontWeights.SemiBold;
            if (run.Italic) text.FontStyle = FontStyles.Italic;
            if (run.Strike) text.TextDecorations = TextDecorations.Strikethrough;
            if (run.Code)
            {
                text.FontFamily = ChatPalette.MonoFont;
                text.FontSize = ChatPalette.CodeSize;
                text.Background = ChatPalette.InlineCodeBack;
            }
            return text;
        }
    }
}
