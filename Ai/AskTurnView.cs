using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Ai;

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
        /// Puts text on the clipboard; Copy calls it with the answer's Markdown. Failures are
        /// logged, never thrown. Tests replace it so the real clipboard is never touched.
        /// </summary>
        internal static Action<string> SetClipboard { get; set; } = text =>
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Warn("ai", "Copying an answer failed (" + ex.GetType().Name + ")");
            }
        };

        private readonly StringBuilder _raw = new();
        private readonly Dictionary<string, ToolChip> _chipsByTool = new(StringComparer.Ordinal);
        private readonly List<ToolChip> _chips = new();
        private readonly Ellipse[] _dots = new Ellipse[3];
        private readonly DispatcherTimer _renderTimer;
        private readonly Stopwatch _sinceRender = new();
        private bool _rendered;

        /// <summary>Builds the visuals for one question.</summary>
        public AskTurnView(string question)
        {
            Question = new TextBox
            {
                Style = ChatStyles.Get("ChatReadOnlyText"),
                Text = question,
                FontSize = ChatPalette.TextSize,
            };
            var bubble = new Border
            {
                Background = ChatPalette.Bubble,
                CornerRadius = new CornerRadius(16, 16, 4, 16),
                Padding = new Thickness(14, 10, 14, 10),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = Question,
            };
            // The bubble may take 78% of the width, and only what its text needs.
            var questionRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            questionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22, GridUnitType.Star) });
            questionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78, GridUnitType.Star) });
            Grid.SetColumn(bubble, 1);
            questionRow.Children.Add(bubble);

            Tools = new WrapPanel { Margin = new Thickness(0, 3, 0, 2), Visibility = Visibility.Collapsed };

            var typing = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(2, 11, 0, 11),
            };
            for (int i = 0; i < _dots.Length; i++)
            {
                _dots[i] = new Ellipse { Width = 6, Height = 6, Fill = ChatPalette.Muted, Margin = new Thickness(0, 0, 5, 0) };
                typing.Children.Add(_dots[i]);
            }
            Typing = typing;

            Answer = new AnswerBox
            {
                Style = ChatStyles.Get("ChatAnswer"),
                Margin = new Thickness(0, 3, 0, 0),
                Visibility = Visibility.Collapsed,
            };

            NoteText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = ChatPalette.Ink,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var noteGlyph = new TextBlock
            {
                Text = ChatPalette.WarningGlyph,
                FontFamily = ChatPalette.IconFont,
                FontSize = 14,
                Foreground = ChatPalette.Amber,
                Margin = new Thickness(0, 1, 9, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            var noteRow = new Grid();
            noteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            noteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(NoteText, 1);
            noteRow.Children.Add(noteGlyph);
            noteRow.Children.Add(NoteText);
            Note = new Border
            {
                Background = ChatPalette.NoteBack,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 7, 12, 7),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = Visibility.Collapsed,
                Child = noteRow,
            };

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
                Foreground = ChatPalette.Muted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            };
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
            Typing.Loaded += (s, e) => Animate(Typing.Visibility == Visibility.Visible);
            Typing.Unloaded += (s, e) => Animate(false);
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

        /// <summary>The shortest time between two renders of a streaming answer.</summary>
        internal TimeSpan RenderInterval { get; set; } = TimeSpan.FromMilliseconds(100);

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

        /// <summary>Records one tool call: a chip per tool, whose tooltip lists every call of it.</summary>
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

        /// <summary>Renders <see cref="RawText"/> now, cancelling a pending render.</summary>
        internal void RenderNow()
        {
            _renderTimer.Stop();
            string raw = RawText;
            Answer.Show(ChatDocument.Build(ChatMarkdown.Parse(raw)));
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

        /// <summary>Shows the dots; they animate only while they are on screen.</summary>
        private void StartTyping()
        {
            Typing.Visibility = Visibility.Visible;
            if (Typing.IsLoaded) Animate(true);
        }

        /// <summary>Hides the dots and stops their animation, so nothing keeps the renderer busy.</summary>
        private void StopTyping()
        {
            if (Typing.Visibility != Visibility.Visible) return;
            Typing.Visibility = Visibility.Collapsed;
            Animate(false);
        }

        /// <summary>Three dots fading in turn at 30 frames a second, or no animation at all.</summary>
        private void Animate(bool on)
        {
            for (int i = 0; i < _dots.Length; i++)
            {
                if (!on)
                {
                    _dots[i].BeginAnimation(UIElement.OpacityProperty, null);
                    continue;
                }
                var fade = new DoubleAnimation(0.3, 1.0, TimeSpan.FromMilliseconds(450))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(150 * i),
                };
                Timeline.SetDesiredFrameRate(fade, 30);
                _dots[i].BeginAnimation(UIElement.OpacityProperty, fade);
            }
        }

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
                Foreground = ChatPalette.Muted,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = ChatPalette.CheckGlyph,
                FontFamily = ChatPalette.IconFont,
                FontSize = 10,
                Foreground = ChatPalette.Accent,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 5, 0),
            });
            row.Children.Add(Label);
            Element = new Border
            {
                Background = ChatPalette.ChipBack,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 3, 10, 3),
                Margin = new Thickness(0, 0, 6, 6),
                Child = row,
            };
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
