using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kil0bitSystemMonitor.Services.Ai;

// UseWindowsForms puts System.Windows.Forms and System.Drawing in scope; these names exist in both.
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// One question and its answer in the Ask window, built in code: the question, the streamed
    /// answer, a note (limited mode, stopped, an error), the Details line naming each tool used
    /// with its arguments, and the suggested-action buttons.
    /// </summary>
    internal sealed class AskTurnView
    {
        private static readonly Brush Card = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        private static readonly Brush Ink = Frozen(Color.FromArgb(0xE9, 0xED, 0xED, 0xF2));
        private static readonly Brush Muted = Frozen(Color.FromArgb(0x88, 0xED, 0xED, 0xF2));
        private static readonly Brush Amber = Frozen(Color.FromRgb(0xE8, 0xA5, 0x3C));
        private static readonly Brush Cyan = Frozen(Color.FromRgb(0x3F, 0xD2, 0xE4));

        private readonly List<string> _tools = new();

        /// <summary>Builds the visuals for one question.</summary>
        public AskTurnView(string question)
        {
            Question = new TextBlock
            {
                Text = question,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
                Foreground = Cyan,
                Margin = new Thickness(0, 0, 0, 6),
            };
            Answer = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ink };
            Note = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Amber,
                FontSize = 11.5,
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            Details = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Muted,
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            Actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };

            var stack = new StackPanel();
            stack.Children.Add(Question);
            stack.Children.Add(Answer);
            stack.Children.Add(Note);
            stack.Children.Add(Details);
            stack.Children.Add(Actions);

            Root = new Border
            {
                Background = Card,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 10),
                Child = stack,
            };
        }

        /// <summary>The element added to the transcript.</summary>
        public Border Root { get; }

        /// <summary>The question as asked.</summary>
        public TextBlock Question { get; }

        /// <summary>The answer, growing as it streams.</summary>
        public TextBlock Answer { get; }

        /// <summary>Limited mode, Stopped, or an error sentence; collapsed until used.</summary>
        public TextBlock Note { get; }

        /// <summary>"Details: tool args; tool args", collapsed until a tool is used.</summary>
        public TextBlock Details { get; }

        /// <summary>Holds the suggestion buttons.</summary>
        public WrapPanel Actions { get; }

        /// <summary>The suggestion buttons, in the order they arrived.</summary>
        public List<Button> ActionButtons { get; } = new();

        /// <summary>
        /// Adds streamed text to the answer. Whitespace before the first visible character is
        /// dropped: some OpenAI-compatible servers (vLLM with a reasoning parser) start every
        /// answer with blank lines.
        /// </summary>
        public void AppendText(string text)
        {
            if (Answer.Text.Length == 0) text = text.TrimStart();
            if (text.Length > 0) Answer.Text += text;
        }

        /// <summary>Records one tool call on the Details line.</summary>
        public void AddTool(string name, string? args)
        {
            _tools.Add(Describe(name, args));
            Details.Text = "Details: " + string.Join("; ", _tools);
            Details.Visibility = Visibility.Visible;
        }

        /// <summary>Shows a note under the answer, replacing any earlier one.</summary>
        public void ShowNote(string text)
        {
            Note.Text = text;
            Note.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Adds a suggestion button; <paramref name="onClick"/> runs only when it is clicked. An
        /// end-process button shows its process and PID, never the model's label.
        /// </summary>
        public Button AddAction(SuggestedAction action, Action onClick)
        {
            var button = new Button
            {
                Content = action.ButtonText,
                ToolTip = action.Reason,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(10, 4, 10, 4),
            };
            button.Click += (s, e) => onClick();
            ActionButtons.Add(button);
            Actions.Children.Add(button);
            Actions.Visibility = Visibility.Visible;
            return button;
        }

        /// <summary>A tool and its arguments as the Details line shows them; empty arguments are left out.</summary>
        internal static string Describe(string name, string? args)
        {
            string trimmed = args?.Trim() ?? "";
            return trimmed.Length == 0 || trimmed == "{}" ? name : name + " " + trimmed;
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
