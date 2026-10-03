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

        /// <summary>The document for <paramref name="blocks"/>, styled for the chat.</summary>
        public static FlowDocument Build(IReadOnlyList<ChatBlock> blocks)
        {
            var document = NewDocument();
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

        /// <summary>
        /// <paramref name="text"/> as it is, one paragraph with its line breaks and no Markdown:
        /// what an answer shows when building its styled document failed.
        /// </summary>
        public static FlowDocument Plain(string text)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) paragraph.Inlines.Add(new LineBreak());
                if (lines[i].Length > 0) paragraph.Inlines.Add(new Run(lines[i]));
            }
            var document = NewDocument();
            document.Blocks.Add(paragraph);
            return document;
        }

        /// <summary>
        /// Turns every link in <paramref name="document"/> into plain text in its place: its label
        /// as it was styled, then its address in parentheses. A label that is the address itself
        /// (a bare address in the answer) is shown once. Whatever built the document, no
        /// <see cref="Hyperlink"/> is left in it, so nothing can be clicked open and no address
        /// hides behind a label.
        ///
        /// <para>
        /// For answers that note text may have steered: text pasted into a note from the web can
        /// make the model write a link that looks like a citation and carries other passages in
        /// its address. One click would send them. MicaPad's answers always go through this; an
        /// Ask MicaStats answer does once a note tool was used.
        /// </para>
        /// </summary>
        internal static void RemoveLinks(FlowDocument document)
        {
            foreach (Hyperlink link in All<Hyperlink>(document))
            {
                // Thrown, not skipped: a link that cannot be taken out must not be shown (the caller falls back to plain text).
                InlineCollection around = link.SiblingInlines
                    ?? throw new InvalidOperationException("A link in an answer has no place to put its text");
                string label = new TextRange(link.ContentStart, link.ContentEnd).Text.Trim();
                string? address = AddressOf(link);

                var inside = new List<Inline>(link.Inlines);
                foreach (Inline inline in inside)
                {
                    link.Inlines.Remove(inline);
                    around.InsertBefore(link, inline);
                }
                if (address != null && !SameAddress(label, address))
                    around.InsertBefore(link, new Run(label.Length == 0 ? address : " (" + address + ")"));
                around.Remove(link);
            }
        }

        /// <summary>Every <typeparamref name="T"/> inside a document: its links, or its text boxes (the code blocks of a rendered answer).</summary>
        internal static List<T> All<T>(DependencyObject root) where T : DependencyObject
        {
            var found = new List<T>();
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is not DependencyObject element) continue;
                if (element is T match) found.Add(match);
                found.AddRange(All<T>(element));
            }
            return found;
        }

        /// <summary>
        /// Where a link goes. <see cref="AddInlines"/> keeps the address in the link's tool tip and
        /// opens it from a click handler; a link built any other way names it in <c>NavigateUri</c>.
        /// </summary>
        private static string? AddressOf(Hyperlink link)
        {
            if (link.NavigateUri is { } uri) return uri.IsAbsoluteUri ? uri.AbsoluteUri : uri.OriginalString;
            return link.ToolTip is string { Length: > 0 } tip ? tip : null;
        }

        /// <summary>True when the label is the address: the same text, or an address that is the same once both are written out in full.</summary>
        private static bool SameAddress(string label, string address) =>
            string.Equals(label, address, StringComparison.Ordinal)
            || (SafeLinks.TryCreate(label) is { } written && string.Equals(written.AbsoluteUri, address, StringComparison.Ordinal));

        private static FlowDocument NewDocument()
        {
            var document = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = ChatPalette.TextFont,
                FontSize = ChatPalette.TextSize,
                LineHeight = ChatPalette.LineHeight,
                TextAlignment = TextAlignment.Left,
            };
            document.SetResourceReference(TextElement.ForegroundProperty, "Ask.Ink");
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
            quote.BorderThickness = new Thickness(3, 0, 0, 0);
            quote.Padding = new Thickness(10, 2, 0, 2);
            quote.SetResourceReference(TextElement.ForegroundProperty, "Ask.Muted");
            quote.SetResourceReference(Block.BorderBrushProperty, "Ask.Accent");
            return quote;
        }

        /// <summary>A rounded box with the code, selectable and wrapped, in a monospace font.</summary>
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
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Child = text,
            };
            box.SetResourceReference(Border.BackgroundProperty, "Ask.CodeBack");
            AskMenus.Install(text, editable: false);
            return new BlockUIContainer(box) { Margin = ParagraphSpacing };
        }

        private static BlockUIContainer Rule()
        {
            var line = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4) };
            line.SetResourceReference(Border.BackgroundProperty, "Ask.Divider");
            return new BlockUIContainer(line) { Margin = ParagraphSpacing };
        }

        /// <summary>
        /// Adds a list item, opening, nesting and closing lists by depth. A numbered list starts at
        /// its first item's own number, so a list split by a paragraph or code keeps counting.
        /// Depth is clamped again here, whatever the blocks say: every level is one more nested
        /// List, and WPF cannot lay out thousands of them. Bullets are small filled dots at every
        /// level; the indent shows the nesting.
        /// </summary>
        private static void AddItem(FlowDocument document, List<(List List, int Depth, ChatBlockKind Kind)> lists, ChatBlock item)
        {
            int depth = Math.Clamp(item.Depth, 0, ChatMarkdown.MaxListDepth);
            while (lists.Count > 0 && lists[^1].Depth > depth) lists.RemoveAt(lists.Count - 1);
            if (lists.Count > 0 && lists[^1].Depth == depth && lists[^1].Kind != item.Kind) lists.RemoveAt(lists.Count - 1);

            if (lists.Count == 0 || lists[^1].Depth < depth)
            {
                bool numbered = item.Kind == ChatBlockKind.Numbered;
                var list = new List
                {
                    MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Padding = new Thickness(numbered ? 28 : 22, 0, 0, 0),
                    Margin = lists.Count == 0 ? ParagraphSpacing : new Thickness(0, 2, 0, 2),
                };
                if (numbered) list.StartIndex = Math.Max(1, item.Number);

                if (lists.Count == 0) document.Blocks.Add(list);
                else lists[^1].List.ListItems.LastListItem.Blocks.Add(list);
                lists.Add((list, depth, item.Kind));
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
                    Cursor = Cursors.Hand,
                    ToolTip = uri.AbsoluteUri,
                };
                link.SetResourceReference(TextElement.ForegroundProperty, "Ask.Accent");
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
                text.SetResourceReference(TextElement.BackgroundProperty, "Ask.InlineCodeBack");
            }
            return text;
        }
    }
}
