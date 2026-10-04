using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;

// UseWindowsForms puts System.Windows.Forms and System.Drawing in scope; these names exist in both.
using Cursors = System.Windows.Input.Cursors;
using List = System.Windows.Documents.List;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Control = System.Windows.Controls.Control;
using Image = System.Windows.Controls.Image;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// Turns parsed answer Markdown (<see cref="ChatMarkdown"/>) into the FlowDocument the Ask
    /// window shows: paragraphs with line breaks, headings, nested lists, quotes, code blocks,
    /// rules and styled runs. Links open only through <see cref="Open"/>, which allows http, https
    /// and mailto and nothing else, as MicaPad does. A closed <c>mermaid</c> block becomes a
    /// picture when the view's <see cref="ChatRender"/> can get one; nothing is ever fetched to
    /// draw an answer.
    /// </summary>
    internal static class ChatDocument
    {
        private static readonly Thickness ParagraphSpacing = new(0, 0, 0, 8);

        /// <summary>How long a code block's Copy button reads "Copied". Tests shorten it.</summary>
        internal static TimeSpan CopiedFor { get; set; } = TimeSpan.FromSeconds(1.5);

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
        public static FlowDocument Build(IReadOnlyList<ChatBlock> blocks, ChatRender? render = null)
        {
            render ??= ChatRender.Default;
            var document = NewDocument();
            var lists = new List<(List List, int Depth, ChatBlockKind Kind)>();
            int diagrams = 0;
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
                    ChatBlockKind.Code => CodeOrDiagram(block, render, ref diagrams),
                    ChatBlockKind.Table when block.Table is { } table => TableBlock(table),
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

        /// <summary>
        /// A rounded box with a header (the language on the left, a flat Copy button on the right)
        /// above the code, which is selectable and wrapped, in a monospace font.
        /// </summary>
        private static BlockUIContainer CodeBlock(ChatBlock block, ChatRender render) =>
            new(CodeBox(block, render)) { Margin = ParagraphSpacing };

        /// <summary>A code block's box, without the block around it: also what a diagram shows while it is drawn, and when it cannot be.</summary>
        private static Border CodeBox(ChatBlock block, ChatRender render)
        {
            var header = new Grid { Margin = new Thickness(0, -4, -4, 4) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (block.Language.Length > 0) header.Children.Add(HeaderLabel(block.Language));
            Button copy = CopyButton(render, block.Code, "Copy code");
            Grid.SetColumn(copy, 1);
            header.Children.Add(copy);
            return Box(header, CodeText(block.Code));
        }

        /// <summary>The rounded box of a code block or a diagram: its header, then what it holds. The brush is a reference, so a theme switch repaints.</summary>
        private static Border Box(UIElement header, params UIElement[] content)
        {
            var inside = new StackPanel();
            inside.Children.Add(header);
            foreach (UIElement part in content) inside.Children.Add(part);
            var box = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Child = inside,
            };
            box.SetResourceReference(Border.BackgroundProperty, "Ask.CodeBack");
            return box;
        }

        /// <summary>Code as selectable, wrapped, monospace text with the Ask window's Copy and Select all menu.</summary>
        private static TextBox CodeText(string code)
        {
            var text = new TextBox
            {
                Style = ChatStyles.Get("ChatReadOnlyText"),
                Text = code,
                FontFamily = ChatPalette.MonoFont,
                FontSize = ChatPalette.CodeSize,
            };
            AskMenus.Install(text, editable: false);
            return text;
        }

        /// <summary>The small muted word at the left of a box's header: a code block's language, or "Diagram".</summary>
        private static TextBlock HeaderLabel(string text)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Ask.Muted");
            return label;
        }

        private static Button FlatButton(string content) => new()
        {
            Style = ChatStyles.Get("ChatFlatButton"),
            Content = content,
            FontSize = 11.5,
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        /// <summary>A header's Copy button for <paramref name="text"/>, with the "Copied" feedback.</summary>
        private static Button CopyButton(ChatRender render, string text, string toolTip)
        {
            Button copy = FlatButton(CopyLabel);
            copy.ToolTip = toolTip;
            CopyTimer? timer = null;
            copy.Click += (s, e) => timer = Copied(copy, render, text, timer);
            return copy;
        }

        private const string CopyLabel = "Copy";

        /// <summary>
        /// A Copy click: the code goes to <see cref="ChatRender.Copy"/>, the button reads "Copied"
        /// and a one-shot timer sets it back. A hook that throws is logged by type and the button
        /// keeps its label. The timer holds the button only weakly: the answer is rebuilt ten times
        /// a second while it streams, and a timer still pending must not keep the old document
        /// alive, or throw when the button is gone.
        /// </summary>
        private static CopyTimer? Copied(Button copy, ChatRender render, string code, CopyTimer? previous)
        {
            if (!CopyText(render, code)) return previous;

            previous?.Stop();
            copy.Content = "Copied";
            return CopyTimer.Start(copy, CopiedFor);
        }

        /// <summary>Hands text to <see cref="ChatRender.Copy"/>. False when the hook threw, which is logged by type.</summary>
        private static bool CopyText(ChatRender render, string text)
        {
            try
            {
                render.Copy(text);
                return true;
            }
            catch (Exception ex)
            {
                // The type only: the message could quote the code.
                DiagnosticsLog.Warn("ai", "Copying code failed (" + ex.GetType().Name + ")");
                return false;
            }
        }

        /// <summary>The one-shot timer behind "Copied". It references its button through a <see cref="WeakReference{T}"/> only.</summary>
        private sealed class CopyTimer
        {
            private readonly DispatcherTimer _timer;

            private CopyTimer(DispatcherTimer timer) => _timer = timer;

            public static CopyTimer Start(Button button, TimeSpan after)
            {
                var weak = new WeakReference<Button>(button);
                var timer = new DispatcherTimer(DispatcherPriority.Normal, button.Dispatcher) { Interval = after };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    if (weak.TryGetTarget(out Button? target)) target.Content = CopyLabel;
                };
                timer.Start();
                return new CopyTimer(timer);
            }

            public void Stop() => _timer.Stop();
        }

        /// <summary>The one fence word drawn as a picture in an answer, in any letter case. Every other word is code.</summary>
        private const string DiagramWord = "mermaid";

        private const string DrawingText = "Drawing the diagram…";
        private const string NotDrawnText = "This diagram could not be drawn: ";

        /// <summary>
        /// A code block, or the picture of a Mermaid block: only when the view draws diagrams, the
        /// fence is closed (an answer still streaming asks for nothing until then) and fewer than
        /// <see cref="ChatDiagrams.MaxPerAnswer"/> diagrams come before it. The block is complete
        /// when this returns, and everything it says is plain text; a picture that arrives later
        /// comes with the next build of the document, never into this one.
        /// </summary>
        private static Block CodeOrDiagram(ChatBlock block, ChatRender render, ref int diagrams)
        {
            if (render.Diagrams is not { } pictures || !block.Closed || diagrams >= ChatDiagrams.MaxPerAnswer
                || !string.Equals(block.Language, DiagramWord, StringComparison.OrdinalIgnoreCase))
                return CodeBlock(block, render);

            diagrams++;
            ChatDiagramState state;
            try
            {
                state = pictures.Get(block.Code, render.Dark, render.Invalidate);
            }
            catch (Exception ex)
            {
                // The type only: the message could quote the source.
                DiagnosticsLog.Warn("ai", "Asking for a diagram in an answer failed (" + ex.GetType().Name + ")");
                state = new ChatDiagramState(ChatDiagramStatus.Failed, CanRetry: true);
            }

            return state.Status switch
            {
                ChatDiagramStatus.Drawn when state.Picture != null => DiagramBlock(block, state, render),
                ChatDiagramStatus.Drawing => Noted(CodeBox(block, render), above: DrawingText),
                ChatDiagramStatus.Failed => Noted(CodeBox(block, render), below: NotDrawnText + (state.Error ?? DiagramText.Failed),
                                                  under: state.CanRetry ? RetryButton(pictures, block.Code, render) : null),
                _ => CodeBlock(block, render),
            };
        }

        /// <summary>A code box with one muted line above or below it, and under that line what can be done about it.</summary>
        private static BlockUIContainer Noted(Border code, string? above = null, string? below = null, UIElement? under = null)
        {
            var parts = new StackPanel();
            if (above != null) parts.Children.Add(Note(above, new Thickness(0, 0, 0, 4)));
            parts.Children.Add(code);
            if (below != null) parts.Children.Add(Note(below, new Thickness(0, 4, 0, 0)));
            if (under != null) parts.Children.Add(under);
            return new BlockUIContainer(parts) { Margin = ParagraphSpacing };
        }

        /// <summary>
        /// Try again, under the message of a failure that may pass (the engine was still starting,
        /// a draw took too long). A press forgets how that one draw ended and asks the view to
        /// draw: the view's next build then asks for the picture, which starts one draw. Nothing
        /// here assumes the view draws at once, and nothing is drawn without a press, so it cannot
        /// go round by itself. A failure the same source gives again has no such button.
        /// </summary>
        private static Button RetryButton(IChatDiagrams pictures, string source, ChatRender render)
        {
            Button retry = FlatButton("Try again");
            retry.ToolTip = "Draw this diagram again";
            retry.HorizontalAlignment = HorizontalAlignment.Left;
            retry.Margin = new Thickness(-6, 2, 0, 0);   // its text lines up with the message above it
            retry.Click += (s, e) =>
            {
                try
                {
                    pictures.Forget(source, render.Dark);
                }
                catch (Exception ex)
                {
                    // The type only: the message could quote the source.
                    DiagnosticsLog.Warn("ai", "Forgetting a diagram to draw it again failed (" + ex.GetType().Name + ")");
                }

                try
                {
                    render.Invalidate?.Invoke();
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Warn("ai", "Drawing an answer again for its diagram failed (" + ex.GetType().Name + ")");
                }
            };
            return retry;
        }

        /// <summary>
        /// A muted, wrapping line of plain text. A renderer's message goes here as it is: it can
        /// quote the diagram's source, which the model wrote, so it is never read as Markdown and
        /// can hold no link.
        /// </summary>
        private static TextBlock Note(string text, Thickness margin)
        {
            var note = new TextBlock
            {
                Text = text,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = margin,
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "Ask.Muted");
            return note;
        }

        /// <summary>
        /// A drawn diagram: the box of a code block, with "Diagram", the Source toggle and Copy in
        /// its header, and under it the picture or, while Source is on, the Mermaid text. The
        /// picture is shown at its own size (<see cref="ChatDiagramState.Width"/> by
        /// <see cref="ChatDiagramState.Height"/>, as MicaPad's <c>DiagramPicture.SizeOf</c> takes
        /// it), made smaller when the answer is narrower and never enlarged. Both the picture and
        /// the text are in the block from the start; the toggle only shows one of them.
        /// </summary>
        private static BlockUIContainer DiagramBlock(ChatBlock block, ChatDiagramState state, ChatRender render)
        {
            string source = block.Code;

            var picture = new Image
            {
                Source = state.Picture,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Arrow,   // a picture, not text: the answer's text cursor stops at its edge
            };
            if (state.Width > 0) picture.MaxWidth = state.Width;
            if (state.Height > 0) picture.MaxHeight = state.Height;
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            picture.ContextMenu = DiagramMenu(state.Picture as BitmapSource, source, render);

            TextBox text = CodeText(source);

            Button toggle = FlatButton("Source");
            void Show(bool sourceShown)
            {
                picture.Visibility = sourceShown ? Visibility.Collapsed : Visibility.Visible;
                text.Visibility = sourceShown ? Visibility.Visible : Visibility.Collapsed;
                // The accent marks the toggle as on; off, the style's muted color is back.
                if (sourceShown) toggle.SetResourceReference(Control.ForegroundProperty, "Ask.Accent");
                else toggle.ClearValue(Control.ForegroundProperty);
            }
            Show(render.SourceShown.Contains(source));
            toggle.Click += (s, e) =>
            {
                // The choice lives in the view's set, not on this button: the next build makes a new one.
                bool sourceShown = text.Visibility != Visibility.Visible;
                if (sourceShown) render.SourceShown.Add(source);
                else render.SourceShown.Remove(source);
                Show(sourceShown);
            };

            Button copy = CopyButton(render, source, "Copy the diagram's source");

            var header = new Grid { Margin = new Thickness(0, -4, -4, 4) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(HeaderLabel("Diagram"));
            Grid.SetColumn(toggle, 1);
            header.Children.Add(toggle);
            Grid.SetColumn(copy, 2);
            header.Children.Add(copy);

            return new BlockUIContainer(Box(header, picture, text)) { Margin = ParagraphSpacing };
        }

        /// <summary>
        /// A picture's right-click menu: Copy image and Copy source, in the Ask window's look for the
        /// view's theme (a menu opens in its own popup, outside the view's brushes). A view with
        /// another look, MicaPad's answer box, moves the entries into a menu of its own.
        /// </summary>
        private static ContextMenu DiagramMenu(BitmapSource? picture, string source, ChatRender render)
        {
            var copyImage = new MenuItem { Header = "Copy image", IsEnabled = picture != null };
            copyImage.Click += (s, e) =>
            {
                if (picture != null) CopyPicture(picture, render.Dark);
            };
            var copySource = new MenuItem { Header = "Copy source" };
            copySource.Click += (s, e) => CopyText(render, source);

            // The tag is how a theme switch finds an Ask menu (AskMenus.Retheme).
            var menu = new ContextMenu { Tag = typeof(AskMenus) };
            menu.Items.Add(copyImage);
            menu.Items.Add(copySource);
            AskMenus.Apply(menu, render.Dark ? AskPalette.Dark : AskPalette.Light);
            return menu;
        }

        /// <summary>Hands a diagram's picture to <see cref="ChatClipboard.SetImage"/> as it is at the time of the click. Never throws.</summary>
        private static void CopyPicture(BitmapSource picture, bool dark)
        {
            try
            {
                ChatClipboard.SetImage(OnBoxColor(picture, dark));
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Warn("ai", "Copying a diagram failed (" + ex.GetType().Name + ")");
            }
        }

        /// <summary>
        /// The picture on the color of the box it is shown in. A diagram is drawn on nothing, and
        /// most programs paste a transparent bitmap on black, where a light theme's dark lines
        /// cannot be seen.
        /// </summary>
        private static BitmapSource OnBoxColor(BitmapSource picture, bool dark)
        {
            var area = new Rect(0, 0, picture.PixelWidth, picture.PixelHeight);
            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                context.DrawRectangle(Kil0bitSystemMonitor.Pad.PadThemeApplier.ToBrush((dark ? AskPalette.Dark : AskPalette.Light).CodeBack), null, area);
                context.DrawImage(picture, area);
            }
            var flat = new RenderTargetBitmap(picture.PixelWidth, picture.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            flat.Render(visual);
            flat.Freeze();
            return flat;
        }

        /// <summary>
        /// A table: a header row, then the body rows, in one row group. The columns share the
        /// width equally and the cells wrap. A cell is built like a paragraph, so a link in it works
        /// (or is taken out) like any other. The brushes are Ask.* references, so a theme switch repaints.
        /// </summary>
        private static Table TableBlock(ChatTable chat)
        {
            var table = new Table { Margin = ParagraphSpacing, CellSpacing = 0 };
            for (int c = 0; c < chat.Aligns.Count; c++)
                table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });

            var group = new TableRowGroup();
            table.RowGroups.Add(group);
            group.Rows.Add(TableRowOf(chat.Header, chat.Aligns, header: true));
            foreach (IReadOnlyList<ChatCell> row in chat.Rows)
                group.Rows.Add(TableRowOf(row, chat.Aligns, header: false));
            return table;
        }

        private static TableRow TableRowOf(IReadOnlyList<ChatCell> cells, IReadOnlyList<ChatAlign> aligns, bool header)
        {
            var row = new TableRow();
            for (int c = 0; c < cells.Count; c++)
            {
                var content = new Paragraph { Margin = new Thickness(0) };
                AddInlines(content.Inlines, cells[c].Runs);
                var cell = new TableCell(content)
                {
                    Padding = new Thickness(8, 4, 8, 4),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    TextAlignment = c < aligns.Count ? aligns[c] switch
                    {
                        ChatAlign.Center => TextAlignment.Center,
                        ChatAlign.Right => TextAlignment.Right,
                        _ => TextAlignment.Left,
                    } : TextAlignment.Left,
                };
                cell.SetResourceReference(Block.BorderBrushProperty, "Ask.Divider");
                if (header)
                {
                    cell.FontWeight = FontWeights.SemiBold;
                    cell.SetResourceReference(TextElement.BackgroundProperty, "Ask.CodeBack");
                }
                row.Cells.Add(cell);
            }
            return row;
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
