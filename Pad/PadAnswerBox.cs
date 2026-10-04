using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;

// UseWindowsForms puts System.Windows.Forms in scope; these names exist in both.
using ContextMenu = System.Windows.Controls.ContextMenu;
using Image = System.Windows.Controls.Image;
using MenuItem = System.Windows.Controls.MenuItem;
using RichTextBox = System.Windows.Controls.RichTextBox;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// A read-only box that shows an AI answer in MicaPad: Markdown rendered by the Ask chat
    /// renderer (<see cref="ChatDocument"/>), or plain text exactly as it is. It carries its own
    /// <c>Ask.*</c> brushes for the pad's light or dark theme, so it works anywhere, not only inside
    /// an Ask window. The text can be selected and copied, and nothing else: the box and each code
    /// block in it get a Copy and Select all menu in the pad's look.
    ///
    /// <para>
    /// Nothing in the box navigates or opens anything. A link in an answer is shown as plain text,
    /// its label and then its address (<see cref="ChatDocument.RemoveLinks"/>): an answer is written from notes that
    /// may hold text pasted from the web, and such text can steer the model into a link that looks
    /// like a citation and carries other passages in its address. One click would send them.
    /// </para>
    /// </summary>
    internal sealed class PadAnswerBox : RichTextBox
    {
        private static readonly string CopyGlyph = ((char)0xE8C8).ToString();
        private static readonly string SelectAllGlyph = ((char)0xE8B3).ToString();

        /// <summary>The sources of the diagrams whose Source toggle is on: kept here, because every draw of the text builds new buttons.</summary>
        private readonly HashSet<string> _sourceShown = new(StringComparer.Ordinal);

        /// <summary>This box's redraw, the same delegate at every draw, so a diagram tells it once.</summary>
        private readonly Action _invalidate;
        private bool _dark = true;
        private bool _markdown;

        /// <summary>True when the document last built asked for a diagram's picture: only then does a theme change mean building it again.</summary>
        private bool _picturesAsked;
        private bool _warned;

        /// <summary>Builds the box empty and dark; <see cref="ApplyTheme"/> switches it.</summary>
        public PadAnswerBox()
        {
            _invalidate = RedrawOf(this);
            BuildDocument = raw =>
            {
                ChatRender render = NewRender();
                try
                {
                    return ChatDocument.Build(ChatMarkdown.Parse(raw), render);
                }
                finally
                {
                    _picturesAsked = render.PicturesAsked;
                }
            };
            Style = ChatStyles.Get("ChatAnswer");
            GiveMenu(this);
            ApplyTheme(dark: true);
        }

        /// <summary>The raw text last shown: the Markdown source, or the plain text. For tests and Copy.</summary>
        public string Shown { get; private set; } = "";

        /// <summary>Turns Markdown into the document shown. Tests replace it to make rendering fail.</summary>
        internal Func<string, FlowDocument> BuildDocument { get; set; }

        /// <summary>Where a render failure is reported, once per box. Tests replace it so nothing reaches the real log.</summary>
        internal Action<string> Warn { get; set; } = message => DiagnosticsLog.Warn("pad", message);

        /// <summary>
        /// Draws the answer's Mermaid blocks; null leaves them as code. The app's own by default
        /// (<see cref="ChatDiagrams.Current"/>, null in tests); the pictures are shared, the redraw
        /// and the Source choices are this box's.
        /// </summary>
        internal IChatDiagrams? Diagrams { get; set; } = ChatDiagrams.Current;

        /// <summary>
        /// Shows <paramref name="raw"/> rendered from Markdown. Never throws: it runs on a timer
        /// tick while an answer streams, where an exception would take MicaStats down. Markdown
        /// that cannot be rendered is shown as plain text, and the failure is reported once.
        /// </summary>
        public void ShowMarkdown(string raw) => Display(raw, markdown: true);

        /// <summary>Shows <paramref name="text"/> as it is, with its line breaks and no Markdown. Never throws.</summary>
        public void ShowPlain(string text) => Display(text, markdown: false);

        /// <summary>
        /// Leaves nothing of the result: the box is empty, its diagrams' Source choices are gone,
        /// and the pictures kept for drawing answers again are forgotten
        /// (<see cref="IChatDiagrams.Clear"/>). A picture still being drawn changes nothing when it
        /// arrives. Never throws.
        /// </summary>
        public void Clear()
        {
            Display("", markdown: false);
            _sourceShown.Clear();
            try
            {
                Diagrams?.Clear();
            }
            catch (Exception ex)
            {
                try
                {
                    Warn("Forgetting an AI answer's diagrams failed (" + ex.GetType().Name + ")");
                }
                catch (Exception)
                {
                    // Logging is best effort.
                }
            }
        }

        /// <summary>
        /// Paints the box for the pad's dark or light theme: the <c>Ask.*</c> brushes the chat
        /// renderer reads go into this box's own resources, so the text already shown repaints,
        /// and the menus follow. For an answer without a diagram that is all. A diagram is a
        /// bitmap drawn for one theme, so when the theme really changes an answer whose last build
        /// asked for a picture is built again: its pictures are asked for in the new theme, and
        /// its menus are new ones in the new look.
        /// </summary>
        public void ApplyTheme(bool dark)
        {
            bool changed = dark != _dark;
            _dark = dark;
            AskThemeApplier.ApplyResources(Resources, dark ? AskPalette.Dark : AskPalette.Light);
            if (ContextMenu is { } own) Paint(own);
            if (changed && _markdown && _picturesAsked)
            {
                Display(Shown, markdown: true);
                return;
            }
            foreach (TextBoxBase code in ChatDocument.All<TextBoxBase>(Document))
                if (code.ContextMenu is { } menu) Paint(menu);
        }

        /// <summary>WPF gives the document a 5 px page padding when the box builds its view; the answer has none.</summary>
        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            Document.PagePadding = new Thickness(0);
        }

        /// <summary>
        /// What one build of the answer's document needs: this box's theme, Source choices and
        /// redraw. Never <see cref="ChatRender.Default"/>, which every view would share.
        /// </summary>
        private ChatRender NewRender() => new()
        {
            Diagrams = Diagrams,
            Dark = _dark,
            Invalidate = _invalidate,
            SourceShown = _sourceShown,
        };

        /// <summary>
        /// The redraw a draw is given to call when it ends. It holds the box only weakly: the draw
        /// may outlive the box (a MicaPad window that closed), and must not keep it alive until then.
        /// </summary>
        private static Action RedrawOf(PadAnswerBox box)
        {
            var weak = new WeakReference<PadAnswerBox>(box);
            return () =>
            {
                if (weak.TryGetTarget(out PadAnswerBox? target)) target.Redraw();
            };
        }

        /// <summary>
        /// A picture the box was waiting for has arrived, or failed. The pane skips a draw whose
        /// text is unchanged, so the box draws itself: the text it shows now, whatever it showed
        /// when the picture was asked for. Plain text, and a box that was cleared, have no picture
        /// to show and stay as they are.
        /// </summary>
        private void Redraw()
        {
            if (_markdown) Display(Shown, markdown: true);
        }

        private void Display(string? text, bool markdown)
        {
            string raw = text ?? "";
            // A text that does not continue the one shown is another result: the Source choices made for the old one go with it.
            if (!raw.StartsWith(Shown, StringComparison.Ordinal)) _sourceShown.Clear();
            Shown = raw;
            _markdown = markdown;
            _picturesAsked = false;   // a Markdown build says so again; plain text asks for none
            if (markdown)
            {
                try
                {
                    Put(BuildDocument(raw));
                    return;
                }
                catch (Exception ex)
                {
                    Report(ex);
                }
            }

            try
            {
                Put(ChatDocument.Plain(raw));
            }
            catch (Exception ex)
            {
                // Nothing simpler is left to show it with; the box keeps what it showed.
                Report(ex);
            }
        }

        /// <summary>
        /// Shows <paramref name="document"/> with its links turned into text and with no page
        /// padding (WPF adds one to a document handed to a box that has a view). The links go
        /// first: a document that still holds one is never shown.
        /// </summary>
        private void Put(FlowDocument document)
        {
            ChatDocument.RemoveLinks(document);
            Document = document;
            document.PagePadding = new Thickness(0);
            // The chat renderer gives a code block the Ask window's menu, which follows the theme only inside that window.
            foreach (TextBoxBase code in ChatDocument.All<TextBoxBase>(document)) GiveMenu(code);
            // A diagram's picture too; its entries (Copy image, Copy source) are the renderer's own.
            foreach (Image picture in ChatDocument.All<Image>(document))
                if (picture.ContextMenu is { } chat) picture.ContextMenu = InPadLook(chat);
        }

        /// <summary>The entries of a menu the chat renderer built, moved into a menu in the pad's look and current theme.</summary>
        private ContextMenu InPadLook(ContextMenu chat)
        {
            var entries = new List<object>();
            foreach (object entry in chat.Items) entries.Add(entry);
            chat.Items.Clear();

            var menu = new ContextMenu();
            foreach (object entry in entries)
            {
                if (entry is MenuItem { Icon: null } item) item.Icon = CopyGlyph;   // both entries copy something
                menu.Items.Add(entry);
            }
            Paint(menu);
            return menu;
        }

        /// <summary>Reports a render failure once, by its type only: the message could quote the answer.</summary>
        private void Report(Exception ex)
        {
            if (_warned) return;
            _warned = true;
            try
            {
                Warn("Rendering an AI answer failed (" + ex.GetType().Name + "); it is shown as plain text");
            }
            catch (Exception)
            {
                // Logging is best effort; it must not throw into a timer tick either.
            }
        }

        /// <summary>Copy and Select all for <paramref name="box"/>, in the pad's look and current theme.</summary>
        private void GiveMenu(TextBoxBase box)
        {
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "Copy", Icon = CopyGlyph, Command = ApplicationCommands.Copy, CommandTarget = box });
            menu.Items.Add(new MenuItem { Header = "Select all", Icon = SelectAllGlyph, Command = ApplicationCommands.SelectAll, CommandTarget = box });
            Paint(menu);
            box.ContextMenu = menu;
        }

        /// <summary>A menu opens in its own popup, outside this box's resources, so it carries the pad's brushes itself.</summary>
        private void Paint(ContextMenu menu)
        {
            EditorMenus.Style(menu, _dark ? PadPalette.Dark : PadPalette.Light);
            ModernWpf.ThemeManager.SetRequestedTheme(menu, _dark ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);
        }
    }
}
