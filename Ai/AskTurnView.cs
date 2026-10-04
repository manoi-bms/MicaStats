using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Ai.Tools;

// UseWindowsForms puts System.Windows.Forms in scope; these names exist in both.
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using RichTextBox = System.Windows.Controls.RichTextBox;
using TextBox = System.Windows.Controls.TextBox;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// One question and its answer in the Ask window, built in code: the question as a bubble on
    /// the right; on the left the assistant's avatar with a chip per tool used, a typing indicator
    /// until the answer starts, the answer rendered from Markdown, a note (limited mode, stopped,
    /// an error), the suggestion pills and, when it is done, Copy and the time.
    /// </summary>
    internal sealed class AskTurnView
    {
        /// <summary>
        /// Puts text on the clipboard; Copy calls it with the answer's Markdown. It is
        /// <see cref="ChatClipboard.SetText"/>: one hook for every Copy in an answer.
        /// </summary>
        internal static Action<string> SetClipboard
        {
            get => ChatClipboard.SetText;
            set => ChatClipboard.SetText = value;
        }

        private readonly StringBuilder _raw = new();
        private readonly Dictionary<string, ToolChip> _chipsByTool = new(StringComparer.Ordinal);
        private readonly List<ToolChip> _chips = new();
        private readonly DispatcherTimer _renderTimer;
        private readonly Stopwatch _sinceRender = new();

        /// <summary>The sources of the diagrams whose Source toggle is on: kept here, because every render builds new buttons.</summary>
        private readonly HashSet<string> _sourceShown = new(StringComparer.Ordinal);

        /// <summary>This turn's redraw, the same delegate at every render, so a draw tells it once.</summary>
        private readonly Action _invalidate;
        private bool _dark = true;

        /// <summary>True when the document last built asked for a diagram's picture: only then does a theme change mean building it again.</summary>
        private bool _picturesAsked;
        private bool _released;
        private bool _rendered;
        private bool _renderFailed;

        /// <summary>Builds the visuals for one question.</summary>
        public AskTurnView(string question)
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

            Question = new TextBox
            {
                Style = ChatStyles.Get("ChatReadOnlyText"),
                Text = question,
                FontSize = ChatPalette.TextSize,
            };
            AskMenus.Install(Question, editable: false);
            var bubble = new Border
            {
                CornerRadius = new CornerRadius(16, 16, 4, 16),
                Padding = new Thickness(14, 10, 14, 10),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = Question,
            };
            bubble.SetResourceReference(Border.BackgroundProperty, "Ask.Bubble");
            // The bubble may take 78% of the width, and only what its text needs.
            var questionRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            questionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22, GridUnitType.Star) });
            questionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78, GridUnitType.Star) });
            Grid.SetColumn(bubble, 1);
            questionRow.Children.Add(bubble);

            Tools = new WrapPanel { Margin = new Thickness(0, 3, 0, 2), Visibility = Visibility.Collapsed };

            var typing = new TypingDots
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(2, 11, 0, 11),
            };
            typing.SetResourceReference(TypingDots.FillProperty, "Ask.Muted");
            Typing = typing;

            Answer = new AnswerBox
            {
                Style = ChatStyles.Get("ChatAnswer"),
                Margin = new Thickness(0, 3, 0, 0),
                Visibility = Visibility.Collapsed,
            };

            AskMenus.Install(Answer, editable: false);
            NoteText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            NoteText.SetResourceReference(TextBlock.ForegroundProperty, "Ask.Ink");
            var noteGlyph = new TextBlock
            {
                Text = ChatPalette.WarningGlyph,
                FontFamily = ChatPalette.IconFont,
                FontSize = 14,
                Margin = new Thickness(0, 1, 9, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            noteGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Ask.Amber");
            var noteRow = new Grid();
            noteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            noteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(NoteText, 1);
            noteRow.Children.Add(noteGlyph);
            noteRow.Children.Add(NoteText);
            Note = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 7, 12, 7),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = Visibility.Collapsed,
                Child = noteRow,
            };
            Note.SetResourceReference(Border.BackgroundProperty, "Ask.NoteBack");

            Actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };

            var copyContent = new StackPanel { Orientation = Orientation.Horizontal };
            copyContent.Children.Add(new TextBlock
            {
                Text = ChatPalette.CopyGlyph,
                FontFamily = ChatPalette.IconFont,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            });
            copyContent.Children.Add(new TextBlock { Text = "Copy", VerticalAlignment = VerticalAlignment.Center });
            CopyButton = new Button
            {
                Style = ChatStyles.Get("ChatFlatButton"),
                Content = copyContent,
                ToolTip = "Copy this answer (Markdown)",
                Margin = new Thickness(-8, 0, 0, 0),
            };
            CopyButton.Click += (s, e) => SetClipboard(RawText);
            TimeText = new TextBlock
            {
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            };
            TimeText.SetResourceReference(TextBlock.ForegroundProperty, "Ask.Muted");
            Footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            Footer.Children.Add(CopyButton);
            Footer.Children.Add(TimeText);

            var content = new StackPanel();
            content.Children.Add(Tools);
            content.Children.Add(Typing);
            content.Children.Add(Answer);
            content.Children.Add(Note);
            content.Children.Add(Actions);
            content.Children.Add(Footer);

            var avatar = new ContentControl
            {
                Style = ChatStyles.Get("ChatAvatar"),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 10, 0),
            };
            var answerRow = new Grid();
            answerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            answerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(content, 1);
            answerRow.Children.Add(avatar);
            answerRow.Children.Add(content);

            var root = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
            root.Children.Add(questionRow);
            root.Children.Add(answerRow);
            Root = root;

            _renderTimer = new DispatcherTimer(DispatcherPriority.Background);
            _renderTimer.Tick += (s, e) => RenderNow();
            StartTyping();
        }

        /// <summary>The element added to the transcript.</summary>
        public FrameworkElement Root { get; }

        /// <summary>The question as asked, selectable, in its bubble.</summary>
        public TextBox Question { get; }

        /// <summary>The answer rendered from <see cref="RawText"/>; collapsed until text arrives.</summary>
        public AnswerBox Answer { get; }

        /// <summary>The answer's Markdown as streamed, without the blank lines some servers start with.</summary>
        internal string RawText => _raw.ToString();

        /// <summary>The dots shown from Send until the first answer text, a note or the end.</summary>
        public FrameworkElement Typing { get; }

        /// <summary>Holds a chip per tool used.</summary>
        public WrapPanel Tools { get; }

        /// <summary>The tool chips, in the order the tools were first used.</summary>
        internal IReadOnlyList<ToolChip> ToolChips => _chips;

        /// <summary>The note banner: limited mode, Stopped, or an error sentence; collapsed until used.</summary>
        public Border Note { get; }

        /// <summary>The note's text.</summary>
        public TextBlock NoteText { get; }

        /// <summary>Holds the suggestion pills.</summary>
        public WrapPanel Actions { get; }

        /// <summary>The suggestion buttons, in the order they arrived.</summary>
        public List<Button> ActionButtons { get; } = new();

        /// <summary>Copy and the time, shown once the answer is complete.</summary>
        public StackPanel Footer { get; }

        /// <summary>Copies <see cref="RawText"/> through <see cref="SetClipboard"/>.</summary>
        public Button CopyButton { get; }

        /// <summary>When the answer finished, HH:mm.</summary>
        public TextBlock TimeText { get; }

        /// <summary>Turns the answer's Markdown into the document shown. Tests replace it to make rendering fail.</summary>
        internal Func<string, FlowDocument> BuildDocument { get; set; }

        /// <summary>Where a render failure is reported, once per answer. Tests replace it so nothing reaches the real log.</summary>
        internal Action<string> Warn { get; set; } = message => DiagnosticsLog.Warn("ai", message);

        /// <summary>
        /// True once note text may have steered this answer: a note tool ran in this turn, or in
        /// an earlier turn of the same conversation, whose results are sent again with every later
        /// question (<see cref="ShowLinksAsText"/>; the window also sets it for a new turn). Every
        /// document shown from then on has its links turned into text
        /// (<see cref="ChatDocument.RemoveLinks"/>).
        /// </summary>
        internal bool PlainLinks { get; set; }

        /// <summary>The shortest time between two renders of a streaming answer.</summary>
        internal TimeSpan RenderInterval { get; set; } = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// Draws the answer's Mermaid blocks; null leaves them as code. The app's own by default
        /// (<see cref="ChatDiagrams.Current"/>, null in tests); the pictures are shared, the redraw
        /// and the Source choices are this turn's.
        /// </summary>
        internal IChatDiagrams? Diagrams { get; set; } = ChatDiagrams.Current;

        /// <summary>
        /// The window's theme changed (or, for a new turn, is told for the first time). The brushes
        /// repaint by themselves, and for an answer without a diagram that is all: the theme is
        /// only recorded, for whatever is rendered next. A diagram is a bitmap drawn for one theme,
        /// so an answer whose last build asked for a picture is built again and asks for its
        /// pictures in the new theme.
        /// </summary>
        internal void ApplyTheme(bool dark)
        {
            if (_dark == dark) return;
            _dark = dark;
            if (_rendered && !_released && _picturesAsked) RenderNow();
        }

        /// <summary>
        /// The window dropped this turn (New conversation, or it closed). A picture that arrives
        /// later, or a theme change, renders it no more, and if a cancelled stream still ends it,
        /// it asks for no picture.
        /// </summary>
        internal void Release()
        {
            _released = true;
            _renderTimer.Stop();
        }

        /// <summary>
        /// What one build of the answer's document needs: this turn's theme, Source choices and
        /// redraw. Never <see cref="ChatRender.Default"/>, which every view would share.
        /// </summary>
        private ChatRender NewRender() => new()
        {
            Diagrams = _released ? null : Diagrams,
            Dark = _dark,
            Invalidate = _invalidate,
            SourceShown = _sourceShown,
        };

        /// <summary>
        /// The redraw a draw is given to call when it ends. It holds the turn only weakly: the draw
        /// may outlive the turn (New conversation, a closed window), and must neither keep it alive
        /// until then nor render it when it is gone.
        /// </summary>
        private static Action RedrawOf(AskTurnView turn)
        {
            var weak = new WeakReference<AskTurnView>(turn);
            return () =>
            {
                if (weak.TryGetTarget(out AskTurnView? target) && !target._released) target.RenderNow();
            };
        }

        /// <summary>
        /// Adds streamed text to the answer. Whitespace before the first visible character is
        /// dropped: some OpenAI-compatible servers (vLLM with a reasoning parser) start every
        /// answer with blank lines. The answer re-renders at most every <see cref="RenderInterval"/>.
        /// </summary>
        public void AppendText(string text)
        {
            if (_raw.Length == 0) text = text.TrimStart();
            if (text.Length == 0) return;
            _raw.Append(text);
            StopTyping();
            ScheduleRender();
        }

        /// <summary>
        /// Records one tool call: a chip per tool, whose tooltip lists every call of it. A note
        /// tool also turns the answer's links into text, in what is shown already and in
        /// everything rendered after.
        /// </summary>
        public void AddTool(string name, string? args)
        {
            if (!_chipsByTool.TryGetValue(name, out ToolChip? chip))
            {
                chip = new ToolChip(name, ToolLabel(name));
                _chipsByTool.Add(name, chip);
                _chips.Add(chip);
                Tools.Children.Add(chip.Element);
                Tools.Visibility = Visibility.Visible;
            }
            chip.AddCall(Describe(name, args));

            if (name is ToolNames.SearchNotes or ToolNames.GetNote) ShowLinksAsText();
        }

        /// <summary>
        /// Note text may steer this answer from now on: its links are text, in what is shown
        /// already (text streamed before the tool ran may hold a link) and in everything rendered
        /// after. Called for a note tool's chip, and by the window once the conversation says a
        /// note tool handed notes to the model (<see cref="AiConversation.NotesEverRead"/>), which
        /// does not depend on the tool names a provider's call ids let through.
        /// </summary>
        internal void ShowLinksAsText()
        {
            if (PlainLinks) return;
            PlainLinks = true;
            if (_rendered) RenderNow();
        }

        /// <summary>Shows a note under the answer, replacing any earlier one.</summary>
        public void ShowNote(string text)
        {
            NoteText.Text = text;
            Note.Visibility = Visibility.Visible;
            StopTyping();
        }

        /// <summary>
        /// Adds a suggestion pill; <paramref name="onClick"/> runs only when it is clicked. An
        /// end-process button shows its process and PID, never the model's label.
        /// </summary>
        public Button AddAction(SuggestedAction action, Action onClick)
        {
            var button = new Button
            {
                Style = ChatStyles.Get("ChatPill"),
                Content = action.ButtonText,
                ToolTip = action.Reason,
                Margin = new Thickness(0, 0, 8, 8),
            };
            button.Click += (s, e) => onClick();
            ActionButtons.Add(button);
            Actions.Children.Add(button);
            Actions.Visibility = Visibility.Visible;
            return button;
        }

        /// <summary>
        /// The answer ended (done, stopped or failed): the dots go, the answer renders at once,
        /// and an answer with text gets Copy and the time it finished.
        /// </summary>
        public void Complete(DateTime finishedAt)
        {
            StopTyping();
            RenderNow();
            if (_raw.Length == 0) return;
            TimeText.Text = finishedAt.ToString("HH:mm", CultureInfo.InvariantCulture);
            Footer.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Renders <see cref="RawText"/> now, cancelling a pending render. Never throws: it runs on
        /// a timer tick and from the window's <c>finally</c>, where an exception would take MicaStats
        /// down or leave the window busy. If the Markdown cannot be rendered, the answer is shown as
        /// plain text and the failure is reported once. With <see cref="PlainLinks"/> the links go
        /// before the document is shown; a document whose links cannot be taken out is not shown
        /// either (the plain text has none).
        /// </summary>
        internal void RenderNow()
        {
            _renderTimer.Stop();
            string raw = RawText;
            try
            {
                FlowDocument document = BuildDocument(raw);
                if (PlainLinks) ChatDocument.RemoveLinks(document);
                Answer.Show(document);
            }
            catch (Exception ex)
            {
                if (!_renderFailed)
                {
                    _renderFailed = true;
                    // The type only: the message could quote the answer.
                    Warn("Rendering an answer failed (" + ex.GetType().Name + "); it is shown as plain text");
                }
                try
                {
                    Answer.Show(ChatDocument.Plain(raw));
                }
                catch (Exception)
                {
                    // Nothing simpler is left to show it with; the answer stays as it was.
                }
            }
            Answer.Visibility = raw.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _rendered = true;
            _sinceRender.Restart();
        }

        /// <summary>The first text renders at once; later text waits until the interval has passed.</summary>
        private void ScheduleRender()
        {
            if (_renderTimer.IsEnabled) return;
            TimeSpan since = _sinceRender.Elapsed;
            if (!_rendered || since >= RenderInterval)
            {
                RenderNow();
                return;
            }
            _renderTimer.Interval = RenderInterval - since;
            _renderTimer.Start();
        }

        /// <summary>Shows the dots; they animate only while they are on screen (<see cref="TypingDots"/>).</summary>
        private void StartTyping() => Typing.Visibility = Visibility.Visible;

        /// <summary>Hides the dots, which stops their animation, so nothing keeps the renderer busy.</summary>
        private void StopTyping() => Typing.Visibility = Visibility.Collapsed;

        /// <summary>A tool and its arguments as its chip's tooltip shows them; empty arguments are left out.</summary>
        internal static string Describe(string name, string? args)
        {
            string trimmed = args?.Trim() ?? "";
            return trimmed.Length == 0 || trimmed == "{}" ? name : name + " " + trimmed;
        }

        /// <summary>What a tool chip says for a tool; an unknown tool shows its name.</summary>
        internal static string ToolLabel(string name) => name switch
        {
            "get_live_status" => "Read live status",
            "get_top_processes" => "Checked top processes",
            "get_history" => "Looked at history",
            "list_slowdown_reports" => "Listed slowdown reports",
            "get_slowdown_report" => "Read a slowdown report",
            "list_alerts" => "Checked alerts",
            "get_hardware" => "Read hardware info",
            "get_battery" => "Checked the battery",
            "get_boot_summary" => "Checked startup times",
            "search_notes" => "Searched notes",
            "get_note" => "Read a note",
            _ => name,
        };
    }

    /// <summary>
    /// The answer's RichTextBox. WPF gives the document a 5 px page padding when the box builds
    /// its view from the template and again whenever a document is handed to a box that has one;
    /// both are cleared here, so the answer lines up with the chips and pills on every render.
    /// </summary>
    internal sealed class AnswerBox : RichTextBox
    {
        /// <summary>Shows <paramref name="document"/> with no page padding.</summary>
        public void Show(FlowDocument document)
        {
            Document = document;
            document.PagePadding = new Thickness(0);
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            Document.PagePadding = new Thickness(0);
        }
    }

    /// <summary>One tool's chip: a check, a friendly label, and every call of it in the tooltip.</summary>
    internal sealed class ToolChip
    {
        private readonly List<string> _calls = new();

        public ToolChip(string tool, string label)
        {
            Tool = tool;
            Label = new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Label.SetResourceReference(TextBlock.ForegroundProperty, "Ask.Muted");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var check = new TextBlock
            {
                Text = ChatPalette.CheckGlyph,
                FontFamily = ChatPalette.IconFont,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 5, 0),
            };
            check.SetResourceReference(TextBlock.ForegroundProperty, "Ask.Accent");
            row.Children.Add(check);
            row.Children.Add(Label);
            Element = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 3, 10, 3),
                Margin = new Thickness(0, 0, 6, 6),
                Child = row,
            };
            Element.SetResourceReference(Border.BackgroundProperty, "Ask.ChipBack");
        }

        /// <summary>The tool's name, as the model called it.</summary>
        public string Tool { get; }

        /// <summary>The chip's text.</summary>
        public TextBlock Label { get; }

        /// <summary>The chip; its tooltip lists the calls, one per line.</summary>
        public Border Element { get; }

        /// <summary>Each call as <see cref="AskTurnView.Describe"/> wrote it.</summary>
        public IReadOnlyList<string> Calls => _calls;

        internal void AddCall(string call)
        {
            _calls.Add(call);
            Element.ToolTip = string.Join("\n", _calls);
        }
    }
}
