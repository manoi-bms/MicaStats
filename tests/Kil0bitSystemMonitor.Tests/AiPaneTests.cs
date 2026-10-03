using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Xunit;

using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Color = System.Windows.Media.Color;
using ContentPresenter = System.Windows.Controls.ContentPresenter;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// The AI pane on the shared UI thread, built and laid out in code and never shown: which
    /// buttons each state offers, the instruction box, the Changes view, the redraw rule and the theme.
    /// </summary>
    public class AiPaneTests
    {
        /// <summary>The minus sign of a removed line, U+2212.</summary>
        private static readonly string Minus = ((char)0x2212).ToString();

        /// <summary>A finished rewrite of a selection: every button offered.</summary>
        private static readonly AiPaneView Done = new(
            Title: "Improve writing",
            SourceLine: "Selection, 11 characters",
            AskForInstruction: false,
            Result: "Better text.",
            Markdown: false,
            Running: false,
            Status: "",
            ShowReplace: true,
            CanReplace: true,
            CanInsert: true,
            CanCopy: true,
            CanRetry: true,
            CanShowChanges: true,
            Original: "Worse text.");

        /// <summary>The same request while its reply streams in.</summary>
        private static readonly AiPaneView Streaming = Done with
        {
            Result = "Bet",
            Running = true,
            CanReplace = false,
            CanInsert = false,
            CanRetry = false,
            CanShowChanges = false,
        };

        /// <summary>Ask AI before its instruction is typed.</summary>
        private static readonly AiPaneView Asking = Done with
        {
            Title = "Ask AI…",
            AskForInstruction = true,
            Result = "",
            CanReplace = false,
            CanInsert = false,
            CanCopy = false,
            CanRetry = false,
            CanShowChanges = false,
        };

        /// <summary>A finished rewrite that changed the middle line of three.</summary>
        private static readonly AiPaneView Edited = Done with { Original = "one\ntwo\nthree", Result = "one\nTWO\nthree" };

        private static string Rendered(AiPane pane)
        {
            var document = pane.ResultBox.Document;
            return new TextRange(document.ContentStart, document.ContentEnd).Text.TrimEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        private static List<AiChangeRow> Rows(AiPane pane) => pane.ChangesList.Items.Cast<AiChangeRow>().ToList();

        private static Color BrushColor(object? brush) => Assert.IsAssignableFrom<SolidColorBrush>(brush).Color;

        private static Color Wpf(PadColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

        /// <summary>Not shown: lays the pane out by hand, 320 wide in a 600 high window.</summary>
        private static void LayOut(AiPane pane)
        {
            pane.Visibility = Visibility.Visible;
            pane.Measure(new Size(320, 600));
            pane.Arrange(new Rect(0, 0, 320, 600));
            pane.UpdateLayout();
        }

        /// <summary>The background of a laid-out Changes row.</summary>
        private static Brush? RowBack(AiPane pane, int index)
        {
            var container = Assert.IsType<ContentPresenter>(pane.ChangesList.ItemContainerGenerator.ContainerFromIndex(index));
            return Assert.IsType<Border>(VisualTreeHelper.GetChild(container, 0)).Background;
        }

        private static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        /// <summary>Lets the dispatcher run its timers until <paramref name="done"/>, or for ten seconds.</summary>
        private static void PumpUntil(Func<bool> done)
        {
            var waited = Stopwatch.StartNew();
            while (!done() && waited.Elapsed < TimeSpan.FromSeconds(10)) Pump();
        }

        /// <summary>A key event needs a source; this one has no window behind it.</summary>
        private sealed class NoSource : PresentationSource
        {
            public override Visual? RootVisual { get; set; }

            public override bool IsDisposed => false;

            protected override CompositionTarget? GetCompositionTargetCore() => null;
        }

        [Fact]
        public void The_pane_is_320_wide_and_built_hidden_and_a_view_does_not_reveal_it() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            Assert.Equal(320, pane.Width);
            Assert.Equal(Visibility.Collapsed, pane.Visibility);

            pane.Show(Done);

            Assert.Equal(Visibility.Collapsed, pane.Visibility);   // the window opens and closes it
            Assert.Equal("Improve writing", pane.TitleText.Text);
            Assert.Equal("Selection, 11 characters", pane.SourceText.Text);
            Assert.Equal("Better text.", Rendered(pane));
        });

        [Fact]
        public void A_running_view_offers_Stop_and_none_of_the_four_actions() => UiThread.Run(() =>
        {
            var pane = new AiPane();

            pane.Show(Streaming);

            Assert.Equal(Visibility.Visible, pane.StopButton.Visibility);
            Assert.Equal("Stop", pane.StopButton.Content);
            Assert.All(new[] { pane.ReplaceButton, pane.InsertButton, pane.CopyButton, pane.RetryButton },
                button => Assert.Equal(Visibility.Collapsed, button.Visibility));
            Assert.Equal("Bet", Rendered(pane));
        });

        /// <summary>Where a laid-out element is in the pane.</summary>
        private static Rect BoundsIn(AiPane pane, FrameworkElement element) =>
            new(element.TranslatePoint(new System.Windows.Point(0, 0), pane), new Size(element.ActualWidth, element.ActualHeight));

        [Theory]
        [InlineData(true)]    // a rewrite: Replace selection comes first
        [InlineData(false)]   // a read action: Insert below does
        public void Stop_has_a_place_of_its_own_that_no_action_button_ever_takes(bool replace) => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.Show(Streaming with { ShowReplace = replace });
            LayOut(pane);
            Rect stop = BoundsIn(pane, pane.StopButton);
            Assert.True(stop.Width > 0 && stop.Height > 0, "Stop is drawn while the reply streams");
            Assert.True(stop.Right <= 320 && stop.Bottom <= 600, "Stop is inside the pane");

            pane.Show(Done with { ShowReplace = replace, CanReplace = replace });   // the stream ends: the four actions appear
            LayOut(pane);

            Assert.Equal(Visibility.Collapsed, pane.StopButton.Visibility);
            foreach (var button in new[] { pane.ReplaceButton, pane.InsertButton, pane.CopyButton, pane.RetryButton })
            {
                if (button.Visibility != Visibility.Visible) continue;
                Rect action = BoundsIn(pane, button);
                Assert.True(action.Width > 0 && action.Height > 0, button.Content + " is drawn");
                // A click meant for Stop, landing a moment late, must not land on a button that edits the note.
                Assert.False(stop.IntersectsWith(action), button.Content + " at " + action + " takes the place Stop had at " + stop);
            }
            Assert.False(stop.IntersectsWith(BoundsIn(pane, pane.CloseButton)), "Stop is not on Close either");
        });

        [Fact]
        public void A_finished_rewrite_offers_Replace_Insert_Copy_and_Try_again() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.Show(Streaming);

            pane.Show(Done);

            Assert.Equal(Visibility.Collapsed, pane.StopButton.Visibility);
            var buttons = new[] { pane.ReplaceButton, pane.InsertButton, pane.CopyButton, pane.RetryButton };
            Assert.All(buttons, button => Assert.Equal(Visibility.Visible, button.Visibility));
            Assert.All(buttons, button => Assert.True(button.IsEnabled));
            Assert.Equal(new object[] { "Replace selection", "Insert below", "Copy", "Try again" }, buttons.Select(button => button.Content));
        });

        [Fact]
        public void A_result_that_did_not_come_from_a_selection_has_no_Replace_button() => UiThread.Run(() =>
        {
            var pane = new AiPane();

            pane.Show(Done with { ShowReplace = false, CanReplace = false });

            Assert.Equal(Visibility.Collapsed, pane.ReplaceButton.Visibility);
            Assert.Equal(Visibility.Visible, pane.InsertButton.Visibility);
            Assert.Equal(Visibility.Visible, pane.CopyButton.Visibility);
            Assert.Equal(Visibility.Visible, pane.RetryButton.Visibility);
        });

        [Fact]
        public void Each_button_is_enabled_by_its_own_flag() => UiThread.Run(() =>
        {
            var pane = new AiPane();

            pane.Show(Done with { CanReplace = false });
            Assert.Equal(Visibility.Visible, pane.ReplaceButton.Visibility);   // still there, so the status line can say why
            Assert.False(pane.ReplaceButton.IsEnabled);
            Assert.True(pane.InsertButton.IsEnabled);

            pane.Show(Done with { CanInsert = false });
            Assert.True(pane.ReplaceButton.IsEnabled);
            Assert.False(pane.InsertButton.IsEnabled);

            pane.Show(Done with { CanCopy = false });
            Assert.True(pane.InsertButton.IsEnabled);
            Assert.False(pane.CopyButton.IsEnabled);

            pane.Show(Done with { CanRetry = false });
            Assert.True(pane.CopyButton.IsEnabled);
            Assert.False(pane.RetryButton.IsEnabled);

            pane.Show(Done);
            Assert.True(pane.RetryButton.IsEnabled);
        });

        [Fact]
        public void The_status_line_is_there_only_when_it_has_text() => UiThread.Run(() =>
        {
            var pane = new AiPane();

            pane.Show(Done);
            Assert.Equal(Visibility.Collapsed, pane.StatusText.Visibility);

            pane.Show(Done with { CanReplace = false, Status = "The text changed since the request; use Insert below or Copy" });
            Assert.Equal(Visibility.Visible, pane.StatusText.Visibility);
            Assert.Equal("The text changed since the request; use Insert below or Copy", pane.StatusText.Text);

            pane.Show(Done);
            Assert.Equal(Visibility.Collapsed, pane.StatusText.Visibility);
        });

        [Fact]
        public void The_instruction_box_is_there_only_while_an_instruction_is_awaited() => UiThread.Run(() =>
        {
            var pane = new AiPane();

            pane.Show(Done);
            Assert.Equal(Visibility.Collapsed, pane.InstructionBox.Visibility);

            pane.Show(Asking);
            Assert.Equal(Visibility.Visible, pane.InstructionBox.Visibility);
            Assert.True(pane.InstructionBox.AcceptsReturn);   // Shift+Enter adds a line
            Assert.Equal("Ask AI…", pane.TitleText.Text);

            pane.InstructionBox.Text = "make it formal";
            pane.Show(Streaming);
            Assert.Equal(Visibility.Collapsed, pane.InstructionBox.Visibility);
            Assert.Equal("", pane.InstructionBox.Text);       // the next question starts empty
        });

        [Fact]
        public void While_an_instruction_is_awaited_none_of_the_four_actions_is_offered() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            var actions = new[] { pane.ReplaceButton, pane.InsertButton, pane.CopyButton, pane.RetryButton };

            pane.Show(Asking);                                    // nothing was asked yet: there is no result to act on

            Assert.All(actions, button => Assert.Equal(Visibility.Collapsed, button.Visibility));
            Assert.Equal(Visibility.Collapsed, pane.StopButton.Visibility);
            Assert.Equal(Visibility.Visible, pane.InstructionBox.Visibility);

            pane.Show(Asking with { ShowReplace = false });       // Ask AI on a whole note
            Assert.All(actions, button => Assert.Equal(Visibility.Collapsed, button.Visibility));

            pane.Show(Done);                                      // the answer came: they are back
            Assert.All(actions, button => Assert.Equal(Visibility.Visible, button.Visibility));
        });

        [Fact]
        public void Enter_runs_the_trimmed_instruction_and_nothing_for_an_empty_one() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            var entered = new List<string>();
            pane.InstructionEntered += entered.Add;
            pane.Show(Asking);

            pane.InstructionBox.Text = "  \r\n ";
            Assert.True(pane.HandleInstructionKey(Key.Enter, ModifierKeys.None));   // swallowed: no empty line either
            Assert.Empty(entered);
            Assert.Equal("  \r\n ", pane.InstructionBox.Text);

            pane.InstructionBox.Text = "  make it formal \r\n";
            Assert.True(pane.HandleInstructionKey(Key.Enter, ModifierKeys.None));
            Assert.Equal(new[] { "make it formal" }, entered);
        });

        [Fact]
        public void Shift_Enter_and_other_keys_are_left_to_the_box() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            var entered = new List<string>();
            pane.InstructionEntered += entered.Add;
            pane.Show(Asking);
            pane.InstructionBox.Text = "line one";

            Assert.False(pane.HandleInstructionKey(Key.Enter, ModifierKeys.Shift));
            Assert.False(pane.HandleInstructionKey(Key.A, ModifierKeys.None));
            Assert.False(pane.HandleInstructionKey(Key.Escape, ModifierKeys.None));

            Assert.Empty(entered);
        });

        [Fact]
        public void The_Enter_key_pressed_in_the_box_reaches_the_pane() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            var entered = new List<string>();
            pane.InstructionEntered += entered.Add;
            pane.Show(Asking);
            pane.InstructionBox.Text = "shorter";

            var args = new KeyEventArgs(Keyboard.PrimaryDevice, new NoSource(), 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            pane.InstructionBox.RaiseEvent(args);

            Assert.True(args.Handled);
            Assert.Equal(new[] { "shorter" }, entered);
        });

        [Fact]
        public void FocusInstruction_puts_the_focus_in_the_box_with_its_text_selected() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.Show(Asking);
            pane.InstructionBox.Text = "old question";

            pane.FocusInstruction();

            Assert.Same(pane.InstructionBox, FocusManager.GetFocusedElement(pane));
            Assert.Equal("old question", pane.InstructionBox.SelectedText);
        });

        [Fact]
        public void Each_button_raises_its_own_event() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            var raised = new List<string>();
            pane.ReplaceRequested += () => raised.Add("replace");
            pane.InsertRequested += () => raised.Add("insert");
            pane.CopyRequested += () => raised.Add("copy");
            pane.RetryRequested += () => raised.Add("retry");
            pane.CloseRequested += () => raised.Add("close");
            pane.StopRequested += () => raised.Add("stop");

            pane.Show(Done);
            Click(pane.ReplaceButton);
            Click(pane.InsertButton);
            Click(pane.CopyButton);
            Click(pane.RetryButton);
            Click(pane.CloseButton);
            pane.Show(Streaming);
            Click(pane.StopButton);

            Assert.Equal(new[] { "replace", "insert", "copy", "retry", "close", "stop" }, raised);
        });

        [Fact]
        public void The_Changes_toggle_is_offered_only_when_the_view_allows_it() => UiThread.Run(() =>
        {
            var pane = new AiPane();

            pane.Show(Streaming);
            Assert.Equal(Visibility.Collapsed, pane.ChangesToggle.Visibility);

            pane.Show(Done);
            Assert.Equal(Visibility.Visible, pane.ChangesToggle.Visibility);
            Assert.Equal("Changes", pane.ChangesToggle.Content);
            Assert.False(pane.ShowingChanges);
            Assert.Equal(Visibility.Visible, pane.ResultBox.Visibility);
            Assert.Equal(Visibility.Collapsed, pane.ChangesList.Visibility);
        });

        [Fact]
        public void Changes_shows_the_line_diff_in_place_of_the_result() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.Show(Edited);

            pane.ChangesToggle.IsChecked = true;

            Assert.True(pane.ShowingChanges);
            Assert.Equal(Visibility.Collapsed, pane.ResultBox.Visibility);
            Assert.Equal(Visibility.Visible, pane.ChangesList.Visibility);
            Assert.Equal(new[]
            {
                new AiChangeRow(DiffKind.Unchanged, " ", "one"),
                new AiChangeRow(DiffKind.Removed, Minus, "two"),
                new AiChangeRow(DiffKind.Added, "+", "TWO"),
                new AiChangeRow(DiffKind.Unchanged, " ", "three"),
            }, Rows(pane));
            Assert.Equal(Visibility.Visible, pane.ChangesSummary.Visibility);
            Assert.Equal(HistoryDiff.Describe(1, 1), pane.ChangesSummary.Text);

            pane.ChangesToggle.IsChecked = false;

            Assert.False(pane.ShowingChanges);
            Assert.Equal(Visibility.Visible, pane.ResultBox.Visibility);
            Assert.Equal(Visibility.Collapsed, pane.ChangesList.Visibility);
            Assert.Equal(Visibility.Collapsed, pane.ChangesSummary.Visibility);
            Assert.Equal("one\nTWO\nthree", Rendered(pane));
        });

        [Fact]
        public void A_removed_line_is_tinted_red_and_an_added_one_green_as_the_history_compare_does() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.ApplyTheme(false);
            pane.Show(Edited);
            pane.ChangesToggle.IsChecked = true;
            LayOut(pane);

            Assert.Null(RowBack(pane, 0));
            Assert.Equal(Wpf(DiffPreview.TintOf(DiffKind.Removed, PadPalette.Light)!.Value), BrushColor(RowBack(pane, 1)));
            Assert.Equal(Wpf(DiffPreview.TintOf(DiffKind.Added, PadPalette.Light)!.Value), BrushColor(RowBack(pane, 2)));
            Assert.Null(RowBack(pane, 3));

            pane.ApplyTheme(true);   // the rows already listed repaint

            Assert.Equal(Wpf(DiffPreview.TintOf(DiffKind.Removed, PadPalette.Dark)!.Value), BrushColor(RowBack(pane, 1)));
            Assert.Equal(Wpf(DiffPreview.TintOf(DiffKind.Added, PadPalette.Dark)!.Value), BrushColor(RowBack(pane, 2)));
        });

        [Fact]
        public void The_Changes_toggle_shows_that_it_is_on_in_the_pad_accent_in_either_theme() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.ApplyTheme(false);
            pane.Show(Edited);
            LayOut(pane);
            var label = Assert.IsType<ContentPresenter>(pane.ChangesToggle.Template.FindName("Label", pane.ChangesToggle));
            Assert.Equal(Wpf(PadPalette.Light.TextSoft), BrushColor(TextElement.GetForeground(label)));

            pane.ChangesToggle.IsChecked = true;
            Assert.Equal(Wpf(PadPalette.Light.Accent), BrushColor(TextElement.GetForeground(label)));

            pane.ApplyTheme(true);
            Assert.Equal(Wpf(PadPalette.Dark.Accent), BrushColor(TextElement.GetForeground(label)));
        });

        [Fact]
        public void A_diff_that_is_too_large_says_so() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.Show(Done with { Original = new string('x', HistoryDiff.MaxChars + 1) });

            pane.ChangesToggle.IsChecked = true;

            Assert.Equal("Too large to compare", pane.ChangesSummary.Text);
            Assert.Equal(Visibility.Visible, pane.ChangesSummary.Visibility);
            Assert.Empty(Rows(pane));
        });

        [Fact]
        public void A_view_without_changes_goes_back_to_the_result() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.Show(Edited);
            pane.ChangesToggle.IsChecked = true;

            pane.Show(Streaming);   // Try again

            Assert.False(pane.ShowingChanges);
            Assert.Equal(Visibility.Collapsed, pane.ChangesToggle.Visibility);
            Assert.Equal(Visibility.Visible, pane.ResultBox.Visibility);
            Assert.Equal(Visibility.Collapsed, pane.ChangesList.Visibility);
            Assert.Equal(Visibility.Collapsed, pane.ChangesSummary.Visibility);
        });

        [Fact]
        public void The_changes_follow_the_view_while_they_are_shown() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.Show(Edited);
            pane.ChangesToggle.IsChecked = true;

            pane.Show(Edited with { Result = "one\ntwo\nthree\nfour" });

            Assert.Equal(new[]
            {
                new AiChangeRow(DiffKind.Unchanged, " ", "one"),
                new AiChangeRow(DiffKind.Unchanged, " ", "two"),
                new AiChangeRow(DiffKind.Unchanged, " ", "three"),
                new AiChangeRow(DiffKind.Added, "+", "four"),
            }, Rows(pane));
            Assert.Equal(HistoryDiff.Describe(1, 0), pane.ChangesSummary.Text);
        });

        [Fact]
        public void A_read_result_is_rendered_as_Markdown_and_a_rewrite_as_the_plain_text_it_would_insert() => UiThread.Run(() =>
        {
            var pane = new AiPane();

            pane.Show(Done with { Markdown = true, Result = "**bold** and `code`" });
            Assert.Equal("bold and code", Rendered(pane));
            Assert.Equal("**bold** and `code`", pane.ResultBox.Shown);

            pane.Show(Done with { Markdown = false, Result = "**bold** and `code`" });
            Assert.Equal("**bold** and `code`", Rendered(pane));
        });

        [Fact]
        public void A_link_in_a_result_is_text_with_its_address_in_sight_and_cannot_be_clicked() => UiThread.Run(() =>
        {
            var pane = new AiPane();

            pane.Show(Done with { Markdown = true, Result = "See [the docs](https://example.com/x) and https://example.org." });

            Assert.Equal("See the docs (https://example.com/x) and https://example.org.", Rendered(pane));
            Assert.Empty(AiAskWindowTests.Descendants<Hyperlink>(pane.ResultBox.Document));
        });

        [Fact]
        public void A_credential_marker_is_shown_as_it_is_in_the_result_and_in_the_changes() => UiThread.Run(() =>
        {
            var pane = new AiPane();

            pane.Show(Done with { Original = "pass: {{secret:K7Q2M9XD}}", Result = "Password: {{secret:K7Q2M9XD}}" });
            Assert.Equal("Password: {{secret:K7Q2M9XD}}", Rendered(pane));

            pane.ChangesToggle.IsChecked = true;
            Assert.Equal(new[]
            {
                new AiChangeRow(DiffKind.Removed, Minus, "pass: {{secret:K7Q2M9XD}}"),
                new AiChangeRow(DiffKind.Added, "+", "Password: {{secret:K7Q2M9XD}}"),
            }, Rows(pane));
        });

        [Fact]
        public void A_streaming_result_redraws_at_most_once_per_interval_and_at_once_when_it_ends() => UiThread.Run(() =>
        {
            var pane = new AiPane { RedrawInterval = TimeSpan.FromSeconds(30) };

            pane.Show(Streaming with { Result = "Bet" });
            Assert.Equal("Bet", pane.ResultBox.Shown);            // the first text draws at once

            pane.Show(Streaming with { Result = "Better te", Status = "Working" });
            Assert.Equal("Bet", pane.ResultBox.Shown);            // inside the interval: it waits
            Assert.Equal("Working", pane.StatusText.Text);        // the flags and the status do not
            Assert.Equal(Visibility.Visible, pane.StatusText.Visibility);

            pane.Show(Done);
            Assert.Equal("Better text.", pane.ResultBox.Shown);   // the end draws at once
            Assert.Equal("Better text.", Rendered(pane));
        });

        [Fact]
        public void The_redraw_that_waited_draws_the_latest_text() => UiThread.Run(() =>
        {
            var pane = new AiPane { RedrawInterval = TimeSpan.FromMilliseconds(50) };
            pane.Show(Streaming with { Result = "one" });
            pane.Show(Streaming with { Result = "one two" });
            pane.Show(Streaming with { Result = "one two three" });

            PumpUntil(() => pane.ResultBox.Shown == "one two three");

            Assert.Equal("one two three", pane.ResultBox.Shown);
            Assert.Equal("one two three", Rendered(pane));
        });

        [Fact]
        public void The_default_redraw_interval_is_100_ms() => UiThread.Run(() =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(100), new AiPane().RedrawInterval);
        });

        [Fact]
        public void A_redraw_that_waits_is_dropped_when_the_pane_is_unloaded() => UiThread.Run(() =>
        {
            var pane = new AiPane { RedrawInterval = TimeSpan.FromMilliseconds(50) };
            pane.Show(Streaming with { Result = "one" });
            pane.Show(Streaming with { Result = "one two" });     // waits for the timer

            pane.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));   // the window closed
            var waited = Stopwatch.StartNew();
            PumpUntil(() => pane.ResultBox.Shown != "one" || waited.Elapsed > TimeSpan.FromMilliseconds(400));

            Assert.Equal("one", pane.ResultBox.Shown);            // no timer ticks for a pane that is gone

            pane.Show(Done);                                      // shown again: it draws as before
            Assert.Equal("Better text.", pane.ResultBox.Shown);
        });

        [Fact]
        public void Another_request_draws_at_once_even_while_one_is_streaming() => UiThread.Run(() =>
        {
            var pane = new AiPane { RedrawInterval = TimeSpan.FromSeconds(30) };
            pane.Show(Streaming with { Result = "The old reply so far" });

            pane.Show(Streaming with { Title = "Make shorter", Result = "" });   // a new action took over
            Assert.Equal("", pane.ResultBox.Shown);
            Assert.Equal("", Rendered(pane));

            pane.Show(Done);
            pane.Show(Streaming with { Result = "" });                           // Try again
            Assert.Equal("", pane.ResultBox.Shown);
        });

        [Fact]
        public void A_refresh_with_the_same_result_keeps_the_text_as_it_is() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.Show(Done);
            var document = pane.ResultBox.Document;

            // The window refreshes the view on every keystroke in the source note: a selection in the result must survive that.
            pane.Show(Done with { CanReplace = false, Status = "The text changed since the request; use Insert below or Copy" });

            Assert.Same(document, pane.ResultBox.Document);
            Assert.False(pane.ReplaceButton.IsEnabled);
        });

        [Fact]
        public void The_theme_paints_the_pane_and_the_answer() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            Assert.Equal(Wpf(PadPalette.Dark.Chrome), BrushColor(pane.Background));   // dark until it is told

            pane.ApplyTheme(false);

            Assert.Equal(Wpf(PadPalette.Light.Chrome), BrushColor(pane.Background));
            Assert.Equal(Wpf(PadPalette.Light.Muted), BrushColor(pane.SourceText.Foreground));
            Assert.Equal(Wpf(AskPalette.Light.Ink), BrushColor(pane.ResultBox.Foreground));
            Assert.Equal(ModernWpf.ElementTheme.Light, ModernWpf.ThemeManager.GetRequestedTheme(pane));

            pane.ApplyTheme(true);

            Assert.Equal(Wpf(PadPalette.Dark.Chrome), BrushColor(pane.Background));
            Assert.Equal(Wpf(AskPalette.Dark.Ink), BrushColor(pane.ResultBox.Foreground));
            Assert.Equal(ModernWpf.ElementTheme.Dark, ModernWpf.ThemeManager.GetRequestedTheme(pane));
        });

        [Fact]
        public void The_pane_lays_out_on_its_own_and_a_long_result_scrolls_inside_it() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.Show(Done with
            {
                Markdown = true,
                Result = string.Join("\n\n", Enumerable.Range(1, 80).Select(i => "Paragraph " + i + " of a long answer.")),
                Status = "Cut short at the length limit",
            });

            LayOut(pane);

            Assert.Equal(320, pane.ActualWidth);
            Assert.Equal(600, pane.ActualHeight);
            Assert.True(pane.ResultScroller.ScrollableHeight > 0, "the result scrolls");
            Assert.True(pane.ResultBox.ActualWidth is > 200 and < 320, "the result wraps to the pane: " + pane.ResultBox.ActualWidth);
            foreach (var button in new[] { pane.ReplaceButton, pane.InsertButton, pane.CopyButton, pane.RetryButton })
            {
                var corner = button.TranslatePoint(new System.Windows.Point(button.ActualWidth, button.ActualHeight), pane);
                Assert.True(button.ActualWidth > 0 && corner.X <= 320 && corner.Y <= 600, button.Content + " is inside the pane");
            }
        });

        [Fact]
        public void The_pane_xaml_has_no_literal_colors_and_reads_only_keys_the_pad_palette_has()
        {
            string xaml = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "Pad", "AiPane.xaml"));

            Assert.Empty(new Regex("\"#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\"").Matches(xaml).Select(m => m.Value));

            var known = PadPalette.Dark.Resources().Select(r => r.Key).ToHashSet();
            var used = new Regex(@"Resource (Pad\.[A-Za-z]+)\}").Matches(xaml).Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.True(used.Count >= 5, "expected the pane to read its colors from Pad.* keys, found " + used.Count);
            Assert.All(used, key => Assert.Contains(key, known));
        }
    }
}
