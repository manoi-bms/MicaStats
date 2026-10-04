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
using Kil0bitSystemMonitor.Ai;
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
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;

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

        /// <summary>A finished answer to read (Summarize on a whole note): shown rendered, with no Replace and no Changes.</summary>
        private static readonly AiPaneView Read = Done with
        {
            Title = "Summarize",
            SourceLine = "Whole note, 11 characters",
            Result = "**bold** and `code`",
            Markdown = true,
            ShowReplace = false,
            CanReplace = false,
            CanShowChanges = false,
            Info = "Finished in 4 s",
        };

        /// <summary>The same answer while it streams in.</summary>
        private static readonly AiPaneView Reading = Read with
        {
            Result = "**bo",
            Running = true,
            CanInsert = false,
            CanRetry = false,
            Activity = "Writing…",
            Info = "",
        };

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
        public void The_pane_sets_no_width_of_its_own_and_is_built_hidden_and_a_view_does_not_reveal_it() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            Assert.True(double.IsNaN(pane.Width), "the window's column gives it its width");
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
        public void A_result_that_must_not_be_inserted_below_has_no_Insert_button() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            Assert.True(Done.ShowInsert);                         // every view offers it unless it says otherwise

            pane.Show(Done with { ShowInsert = false, CanInsert = false });   // a fix of a diagram: it would land inside the block

            Assert.Equal(Visibility.Collapsed, pane.InsertButton.Visibility);
            Assert.Equal(Visibility.Visible, pane.ReplaceButton.Visibility);
            Assert.Equal(Visibility.Visible, pane.CopyButton.Visibility);
            Assert.Equal(Visibility.Visible, pane.RetryButton.Visibility);

            pane.Show(Done);                                      // the next request offers it again
            Assert.Equal(Visibility.Visible, pane.InsertButton.Visibility);
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

        // ---- the source line, what is going on and how long it took (AI chat UI spec 3.3) ----------

        [Fact]
        public void A_view_built_without_them_has_no_activity_and_no_info()
        {
            Assert.Equal("", Done.Activity);
            Assert.Equal("", Done.Info);
        }

        [Fact]
        public void The_source_line_wraps_and_is_never_cut_short() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            Assert.Equal(TextWrapping.Wrap, pane.SourceText.TextWrapping);
            Assert.Equal(TextTrimming.None, pane.SourceText.TextTrimming);

            pane.Show(Read);
            LayOut(pane);
            double oneLine = pane.SourceText.ActualHeight, toggle = pane.SourceToggle.ActualHeight;
            Assert.True(oneLine > 0 && toggle > 0, "the line and the toggle beside it are drawn");

            pane.Show(Read with { SourceLine = "Selection, 412 characters · to a-rather-long-host-name.example.com (some-long-model-name-2026-10-04)" });
            LayOut(pane);

            Assert.True(pane.SourceText.ActualHeight > oneLine * 1.5, "the line took a second line: " + pane.SourceText.ActualHeight + " against " + oneLine);
            Rect line = BoundsIn(pane, pane.SourceText);
            Assert.True(line.Left >= 0 && line.Right <= 320, "all of it is inside the pane: " + line);
            Assert.Equal(toggle, pane.SourceToggle.ActualHeight);   // the toggle beside it keeps its own height
            Assert.False(line.IntersectsWith(BoundsIn(pane, pane.SourceToggle)), "the line does not run under the toggle");
        });

        [Fact]
        public void The_activity_row_is_there_while_the_view_says_what_is_going_on() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.ApplyTheme(false);

            pane.Show(Read);
            Assert.Equal(Visibility.Collapsed, pane.ActivityRow.Visibility);

            pane.Show(Reading with { Result = "", Activity = "Waiting for claude-sonnet-5-5…" });
            Assert.Equal(Visibility.Visible, pane.ActivityRow.Visibility);
            Assert.Equal("Waiting for claude-sonnet-5-5…", pane.ActivityText.Text);
            Assert.Equal(11.5, pane.ActivityText.FontSize);
            Assert.Equal(Wpf(PadPalette.Light.Muted), BrushColor(pane.ActivityText.Foreground));
            Assert.Same(pane.ActivityRow, pane.ActivityDots.Parent);             // the dots stand in the row, before the text
            Assert.Equal(Wpf(PadPalette.Light.Muted), BrushColor(pane.ActivityDots.Fill));

            pane.Show(Reading);
            Assert.Equal(Visibility.Visible, pane.ActivityRow.Visibility);
            Assert.Equal("Writing…", pane.ActivityText.Text);
            LayOut(pane);
            Rect row = BoundsIn(pane, pane.ActivityRow);
            Assert.True(row.Height > 0, "the row is drawn");
            Assert.True(row.Top >= BoundsIn(pane, pane.SourceText).Bottom, "under the source line");
            Assert.True(row.Bottom <= BoundsIn(pane, pane.ResultScroller).Top, "above the result");
            Assert.True(BoundsIn(pane, pane.ActivityDots).Right <= BoundsIn(pane, pane.ActivityText).Left, "the dots come before the text");

            pane.ApplyTheme(true);                                // the row repaints with the pad
            Assert.Equal(Wpf(PadPalette.Dark.Muted), BrushColor(pane.ActivityText.Foreground));
            Assert.Equal(Wpf(PadPalette.Dark.Muted), BrushColor(pane.ActivityDots.Fill));

            pane.Show(Read);                                      // the request ended
            Assert.Equal(Visibility.Collapsed, pane.ActivityRow.Visibility);
            Assert.Equal("", pane.ActivityText.Text);
        });

        [Fact]
        public void The_info_line_is_there_while_the_view_has_one_and_stands_above_the_status() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.ApplyTheme(false);

            pane.Show(Reading);
            Assert.Equal(Visibility.Collapsed, pane.InfoText.Visibility);

            pane.Show(Read with { CanInsert = false, Status = "This note is read-only" });
            Assert.Equal(Visibility.Visible, pane.InfoText.Visibility);
            Assert.Equal("Finished in 4 s", pane.InfoText.Text);
            Assert.Equal(11.5, pane.InfoText.FontSize);
            Assert.Equal(Wpf(PadPalette.Light.Muted), BrushColor(pane.InfoText.Foreground));
            Assert.Equal("This note is read-only", pane.StatusText.Text);   // the status says what it said

            LayOut(pane);
            Rect info = BoundsIn(pane, pane.InfoText), status = BoundsIn(pane, pane.StatusText);
            Assert.True(info.Height > 0 && status.Height > 0, "both lines are drawn");
            Assert.True(info.Top >= BoundsIn(pane, pane.ResultScroller).Bottom, "under the result");
            Assert.True(info.Bottom <= status.Top, "the info at " + info + " is above the status at " + status);
            Assert.True(status.Bottom <= BoundsIn(pane, pane.InsertButton).Top, "and the status above the buttons");

            pane.Show(Read with { Info = "" });                   // a request that did not finish whole says nothing here
            Assert.Equal(Visibility.Collapsed, pane.InfoText.Visibility);
            Assert.Equal("", pane.InfoText.Text);
        });

        // ---- the dots, the ones the Ask window has (AI chat UI spec 3.3) ---------------------------

        /// <summary>
        /// Shows <paramref name="content"/> in a real window far off screen and lets it load: only
        /// a loaded element animates. The window is closed afterwards, with its content taken out.
        /// </summary>
        private static void InWindow(FrameworkElement content, Action test)
        {
            var window = new Window
            {
                Width = 400,
                Height = 640,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = content,
            };
            try
            {
                window.Show();
                Pump();   // Loaded is raised from the dispatcher
                test();
            }
            finally
            {
                window.Content = null;
                window.Close();
                Pump();   // and so is Unloaded
            }
        }

        /// <summary>True when every dot's opacity is animated; false when none is. A mix fails the test.</summary>
        private static bool Moving(FrameworkElement dots)
        {
            List<bool> animated = ((Panel)dots).Children.Cast<UIElement>().Select(dot => dot.HasAnimatedProperties).ToList();
            Assert.Equal(3, animated.Count);
            Assert.Single(animated.Distinct());
            return animated[0];
        }

        [Fact]
        public void The_dots_of_the_activity_row_move_only_while_the_row_is_on_screen() => UiThread.Run(() =>
        {
            var pane = new AiPane { Visibility = Visibility.Visible };
            pane.Show(Reading);
            Assert.False(Moving(pane.ActivityDots));              // never shown: nothing ticks for a pane nobody sees

            InWindow(pane, () =>
            {
                Assert.True(Moving(pane.ActivityDots));

                pane.Show(Read);                                  // the request ended: the row goes and its dots stop
                Assert.False(Moving(pane.ActivityDots));

                pane.Show(Reading with { Result = "" });          // Try again
                Assert.True(Moving(pane.ActivityDots));

                pane.Visibility = Visibility.Collapsed;           // the window closes the pane: its last view still says it runs
                Assert.Equal(Visibility.Visible, pane.ActivityRow.Visibility);
                Assert.False(Moving(pane.ActivityDots));

                pane.Visibility = Visibility.Visible;
                Assert.True(Moving(pane.ActivityDots));

                pane.Clear();
                Assert.False(Moving(pane.ActivityDots));

                pane.Show(Reading);
                Assert.True(Moving(pane.ActivityDots));
            });

            Assert.Equal(Visibility.Visible, pane.ActivityRow.Visibility);
            Assert.False(Moving(pane.ActivityDots));              // the window is gone, and with it the animation
        });

        [Fact]
        public void The_dots_are_three_small_ones_in_the_colour_their_user_names() => UiThread.Run(() =>
        {
            var dots = new TypingDots();
            Assert.Equal(Orientation.Horizontal, dots.Orientation);
            List<System.Windows.Shapes.Ellipse> three = dots.Children.Cast<System.Windows.Shapes.Ellipse>().ToList();
            Assert.Equal(3, three.Count);
            Assert.All(three, dot =>
            {
                Assert.Equal(6, dot.Width);
                Assert.Equal(6, dot.Height);
                Assert.Equal(new Thickness(0, 0, 5, 0), dot.Margin);
                Assert.Null(dot.Fill);                            // no colour of its own
            });

            var red = new SolidColorBrush(Colors.Red);
            dots.Fill = red;                                      // a brush
            Assert.All(three, dot => Assert.Same(red, dot.Fill));

            // Or a resource by its key, as the Ask window names its own; the dots follow when a theme changes it.
            var blue = new SolidColorBrush(Colors.Blue);
            var green = new SolidColorBrush(Colors.Green);
            dots.Resources["Ask.Muted"] = blue;
            dots.SetResourceReference(TypingDots.FillProperty, "Ask.Muted");
            Assert.All(three, dot => Assert.Same(blue, dot.Fill));
            dots.Resources["Ask.Muted"] = green;
            Assert.All(three, dot => Assert.Same(green, dot.Fill));
        });

        [Fact]
        public void An_Ask_turn_has_the_same_dots_which_move_while_it_waits_on_screen_and_stop_with_the_first_text() => UiThread.Run(() =>
        {
            var turn = new AskTurnView("q");
            Assert.IsType<TypingDots>(turn.Typing);
            Assert.Equal(new Thickness(2, 11, 0, 11), turn.Typing.Margin);
            Assert.Equal(System.Windows.HorizontalAlignment.Left, turn.Typing.HorizontalAlignment);
            Assert.False(Moving(turn.Typing));                    // off screen

            var muted = new SolidColorBrush(Colors.Gray);
            turn.Root.Resources["Ask.Muted"] = muted;             // the Ask window's own brush, which the turn names by its key
            Assert.All(((Panel)turn.Typing).Children.Cast<System.Windows.Shapes.Ellipse>(), dot => Assert.Same(muted, dot.Fill));

            InWindow(turn.Root, () =>
            {
                Assert.Equal(Visibility.Visible, turn.Typing.Visibility);
                Assert.True(Moving(turn.Typing));

                turn.AddTool("get_live_status", null);            // a tool is no text: still waiting
                Assert.True(Moving(turn.Typing));

                turn.AppendText("Fine.");
                Assert.Equal(Visibility.Collapsed, turn.Typing.Visibility);
                Assert.False(Moving(turn.Typing));
            });
        });

        // ---- Preview and Source (AI chat UI spec 3.2) ----------------------------------------------

        /// <summary>Everything of one kind in the result as it is shown: its pictures, its code boxes.</summary>
        private static List<T> InResult<T>(AiPane pane) where T : DependencyObject => ChatDocument.All<T>(pane.ResultBox.Document);

        [Fact]
        public void The_Source_toggle_is_offered_for_a_rendered_result_with_text_to_show_and_not_while_an_instruction_is_awaited() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            Assert.Equal(Visibility.Collapsed, pane.SourceToggle.Visibility);   // built hidden, as Changes is
            Assert.False(pane.ShowingSource);

            pane.Show(Done);                                      // a rewrite is shown as its text: Changes, and no Source
            Assert.Equal(Visibility.Collapsed, pane.SourceToggle.Visibility);
            Assert.Equal(Visibility.Visible, pane.ChangesToggle.Visibility);

            pane.Show(Read);
            Assert.Equal(Visibility.Visible, pane.SourceToggle.Visibility);
            Assert.Equal(Visibility.Collapsed, pane.ChangesToggle.Visibility);
            Assert.Equal("Source", pane.SourceToggle.Content);
            Assert.Equal("Show the text as it would be inserted", pane.SourceToggle.ToolTip);
            Assert.False(pane.ShowingSource);

            pane.Show(Reading);                                   // while the reply streams in, too
            Assert.Equal(Visibility.Visible, pane.SourceToggle.Visibility);

            pane.Show(Asking with { Markdown = true });           // Ask AI before its instruction: nothing was asked yet
            Assert.Equal(Visibility.Collapsed, pane.SourceToggle.Visibility);

            pane.Show(Asking with { Markdown = true, Result = "left over" });   // and not for whatever a view that asks for one may hold
            Assert.Equal(Visibility.Collapsed, pane.SourceToggle.Visibility);
        });

        // ---- a view that asks for Source first (Ask AI on a note that is not Markdown) ---------------

        [Fact]
        public void A_view_that_asks_for_Source_first_starts_as_text_and_the_toggle_then_decides_for_that_request() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            AiPaneView answer = Read with { Title = "Ask AI…", Result = "# count them\n**n** = 2", SourceFirst = true };

            pane.Show(answer);

            Assert.True(pane.ShowingSource);
            Assert.Equal(Visibility.Visible, pane.SourceToggle.Visibility);
            Assert.Equal("# count them\n**n** = 2", Rendered(pane));   // the text as it would go into the note: a comment, not a heading

            pane.SourceToggle.IsChecked = false;                  // the toggle still switches to the rendered view
            Assert.Equal("count them\nn = 2", Rendered(pane));

            pane.Show(answer);                                    // the window draws the view again at every keystroke in the note
            pane.Show(answer with { Status = "This note is read-only" });
            Assert.False(pane.ShowingSource);                     // the choice holds for that request
            Assert.Equal("count them\nn = 2", Rendered(pane));

            pane.SourceToggle.IsChecked = true;                   // and back
            pane.Show(answer);
            Assert.True(pane.ShowingSource);

            pane.Show(Read);                                      // another request, which does not ask for it: rendered, as ever
            Assert.False(pane.ShowingSource);
            Assert.Equal("bold and code", Rendered(pane));

            pane.Show(answer);                                    // and one that asks starts as text again
            Assert.True(pane.ShowingSource);
            Assert.Equal("# count them\n**n** = 2", Rendered(pane));
        });

        [Fact]
        public void A_view_that_does_not_ask_for_Source_first_starts_rendered() => UiThread.Run(() =>
        {
            var pane = new AiPane();

            pane.Show(Read with { Title = "Ask AI…" });

            Assert.False(pane.ShowingSource);
            Assert.Equal("bold and code", Rendered(pane));

            // A rewrite has no Source toggle to turn on, whatever its view says.
            pane.Show(Done with { SourceFirst = true });
            Assert.False(pane.ShowingSource);
            Assert.Equal(Visibility.Collapsed, pane.SourceToggle.Visibility);
        });

        [Fact]
        public void A_reply_that_asks_for_Source_first_turns_Source_on_when_its_first_text_arrives_and_once_only() => UiThread.Run(() =>
        {
            var pane = new AiPane { RedrawInterval = TimeSpan.FromMilliseconds(1), PointerHeld = () => false };
            AiPaneView waiting = Reading with { Title = "Ask AI…", Result = "", Activity = "Waiting for the model…", SourceFirst = true };

            pane.Show(Asking with { Markdown = true, SourceFirst = true });   // the pane waits for the instruction
            Assert.False(pane.ShowingSource);
            Assert.Equal(Visibility.Collapsed, pane.SourceToggle.Visibility);

            pane.Show(waiting);                                   // sent: nothing to show yet, so no toggle
            Assert.False(pane.ShowingSource);
            Assert.Equal(Visibility.Collapsed, pane.SourceToggle.Visibility);

            pane.Show(waiting with { Result = "# count", Activity = "Writing…" });
            Assert.True(pane.ShowingSource);
            Assert.Equal(Visibility.Visible, pane.SourceToggle.Visibility);
            Assert.Equal("# count", Rendered(pane));

            pane.SourceToggle.IsChecked = false;                  // turned off while it streams
            Assert.Equal("count", Rendered(pane));
            pane.Show(waiting with { Result = "# count\n\nx = 1", Activity = "Writing…" });
            PumpUntil(() => pane.ResultBox.Shown.EndsWith("x = 1", StringComparison.Ordinal));
            Assert.False(pane.ShowingSource);                     // more text does not turn it on again
            pane.Show(Read with { Title = "Ask AI…", Result = "# count\n\nx = 1", SourceFirst = true, SourceLine = waiting.SourceLine });   // nor does its end
            Assert.False(pane.ShowingSource);
            Assert.Equal("count\nx = 1", Rendered(pane));

            pane.Clear();                                         // a cleared pane starts over
            pane.Show(Read with { Title = "Ask AI…", Result = "# count", SourceFirst = true });
            Assert.True(pane.ShowingSource);
        });

        /// <summary>A rendered view with no text: there is nothing Source could show.</summary>
        private static AiPaneView NothingToShow(string state) => state switch
        {
            // Text over the limit: nothing was sent.
            "refused" => Read with { Result = "", Info = "", Status = "Select less text: at most 24,000 characters", CanInsert = false, CanCopy = false, CanRetry = false },
            // The request failed before any text came.
            "failed" => Read with { Result = "", Info = "", Status = "Add an API key in Settings > AI.", CanInsert = false, CanCopy = false },
            // It runs, and the first text has not arrived.
            "waiting" => Reading with { Result = "", Activity = "Waiting for the model…" },
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

        [Theory]
        [InlineData("refused")]
        [InlineData("failed")]
        [InlineData("waiting")]
        public void Source_is_not_offered_while_a_rendered_result_has_no_text_to_show(string state) => UiThread.Run(() =>
        {
            AiPaneView empty = NothingToShow(state);
            Assert.True(empty.Markdown);
            Assert.False(empty.AskForInstruction);
            var pane = new AiPane();

            pane.Show(empty);

            Assert.Equal(Visibility.Collapsed, pane.SourceToggle.Visibility);
            Assert.False(pane.ShowingSource);

            // With text it is offered; and when the text is gone again, which only another request does, it goes, on as it was.
            pane.Show(Read);
            Assert.Equal(Visibility.Visible, pane.SourceToggle.Visibility);
            pane.SourceToggle.IsChecked = true;

            pane.Show(empty);

            Assert.False(pane.ShowingSource);
            Assert.Equal(Visibility.Collapsed, pane.SourceToggle.Visibility);
            Assert.Equal("", Rendered(pane));
        });

        [Fact]
        public void Source_is_offered_from_the_first_text_of_a_reply_on() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.Show(NothingToShow("waiting"));
            Assert.Equal(Visibility.Collapsed, pane.SourceToggle.Visibility);

            pane.Show(Reading);                                   // the first words are in

            Assert.Equal(Visibility.Visible, pane.SourceToggle.Visibility);
            Assert.False(pane.ShowingSource);

            pane.Show(Read with { Result = "", Info = "", Status = "Stopped", CanInsert = false, CanCopy = false });   // hand-built: an end with no text
            Assert.Equal(Visibility.Collapsed, pane.SourceToggle.Visibility);
        });

        [Fact]
        public void Source_shows_the_text_as_it_would_be_inserted_and_off_shows_it_rendered_again() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.Show(Read);
            Assert.Equal("bold and code", Rendered(pane));

            pane.SourceToggle.IsChecked = true;

            Assert.True(pane.ShowingSource);
            Assert.Equal("**bold** and `code`", Rendered(pane));  // exactly what Insert below would put in the note
            Assert.Equal("**bold** and `code`", pane.ResultBox.Shown);
            Assert.Equal(Visibility.Visible, pane.ResultBox.Visibility);

            pane.SourceToggle.IsChecked = false;

            Assert.False(pane.ShowingSource);
            Assert.Equal("bold and code", Rendered(pane));
            Assert.Equal("**bold** and `code`", pane.ResultBox.Shown);
        });

        [Fact]
        public void A_diagram_is_shown_as_a_picture_and_Source_shows_its_block_as_text() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.ResultBox.Diagrams = new FakeChatDiagrams { Answer = (_, _) => ChatDiagramFakes.Drawn() };
            string block = ChatDiagramFakes.Block();

            pane.Show(Read with { Title = "Draw as diagram", Result = block });

            Assert.Single(InResult<Image>(pane));                 // the diagram itself, before anything is inserted
            Assert.NotEqual(block, Rendered(pane));

            pane.SourceToggle.IsChecked = true;

            Assert.Empty(InResult<Image>(pane));
            Assert.Empty(InResult<TextBox>(pane));                // no code block either: only text
            Assert.Equal(block, Rendered(pane));

            pane.SourceToggle.IsChecked = false;

            Assert.Single(InResult<Image>(pane));
        });

        [Fact]
        public void The_Source_toggle_looks_like_the_Changes_toggle_and_shows_that_it_is_on_in_the_pad_accent() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.ApplyTheme(false);
            pane.Show(Edited);
            LayOut(pane);
            double changesHeight = pane.ChangesToggle.ActualHeight;
            Assert.True(changesHeight > 0, "Changes is drawn for a rewrite");

            pane.Show(Read);
            LayOut(pane);

            Assert.Same(pane.ChangesToggle.Template, pane.SourceToggle.Template);   // one template for both
            Assert.Equal(pane.ChangesToggle.FontSize, pane.SourceToggle.FontSize);
            Assert.Equal(pane.ChangesToggle.Padding, pane.SourceToggle.Padding);
            Assert.Equal(pane.ChangesToggle.Margin, pane.SourceToggle.Margin);
            Assert.Same(pane.ChangesToggle.Cursor, pane.SourceToggle.Cursor);
            Assert.Equal(changesHeight, pane.SourceToggle.ActualHeight);
            Assert.Same(pane.ChangesToggle.Parent, pane.SourceToggle.Parent);       // beside it, in the same place

            var label = Assert.IsType<ContentPresenter>(pane.SourceToggle.Template.FindName("Label", pane.SourceToggle));
            Assert.Equal(Wpf(PadPalette.Light.TextSoft), BrushColor(TextElement.GetForeground(label)));

            pane.SourceToggle.IsChecked = true;
            Assert.Equal(Wpf(PadPalette.Light.Accent), BrushColor(TextElement.GetForeground(label)));

            pane.ApplyTheme(true);
            Assert.Equal(Wpf(PadPalette.Dark.Accent), BrushColor(TextElement.GetForeground(label)));
        });

        [Fact]
        public void The_Source_toggle_goes_off_when_another_request_takes_the_pane() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.Show(Read);
            pane.SourceToggle.IsChecked = true;

            pane.Show(Reading with { Result = "", Activity = "Waiting for the model…" });   // Try again: the same action on the same text, running anew
            Assert.False(pane.ShowingSource);
            Assert.Equal(Visibility.Collapsed, pane.SourceToggle.Visibility);               // and with nothing to show yet
            pane.Show(Reading);                                   // its first text: offered again, and off
            Assert.Equal(Visibility.Visible, pane.SourceToggle.Visibility);
            Assert.False(pane.ShowingSource);
            pane.Show(Read);
            Assert.Equal("bold and code", Rendered(pane));        // its answer is shown rendered

            pane.SourceToggle.IsChecked = true;
            pane.Show(Reading with { Result = "**bold** and `code` and more" });             // a request that starts to run after one that ended is another,
            Assert.False(pane.ShowingSource);                     // even when its text goes on from the old one
            Assert.Equal(Visibility.Visible, pane.SourceToggle.Visibility);
            pane.Show(Read);

            pane.SourceToggle.IsChecked = true;
            pane.Show(Read with { Title = "Explain", Result = "It is **text**." });          // another action, finished
            Assert.False(pane.ShowingSource);
            Assert.Equal("It is text.", Rendered(pane));

            pane.SourceToggle.IsChecked = true;
            pane.Show(Read with { Title = "Explain", Result = "It is **text**.", Original = "Other text." });   // the same action on other text
            Assert.False(pane.ShowingSource);
            Assert.Equal("It is text.", Rendered(pane));

            pane.SourceToggle.IsChecked = true;
            pane.Show(Read with { Title = "Explain", Result = "", Original = "Other text.", CanCopy = false });   // a new request before it starts
            Assert.False(pane.ShowingSource);
            Assert.Equal(Visibility.Collapsed, pane.SourceToggle.Visibility);

            pane.Show(Read);
            pane.SourceToggle.IsChecked = true;
            pane.Show(Done);                                      // a rewrite: no Source at all
            Assert.False(pane.ShowingSource);
            Assert.Equal(Visibility.Collapsed, pane.SourceToggle.Visibility);
            Assert.Equal("Better text.", Rendered(pane));
            pane.Show(Read);                                      // and the next rendered result starts rendered
            Assert.False(pane.ShowingSource);
            Assert.Equal("bold and code", Rendered(pane));

            pane.SourceToggle.IsChecked = true;
            pane.Show(Asking with { Markdown = true });           // Ask AI opened on its instruction box
            Assert.False(pane.ShowingSource);
            Assert.Equal(Visibility.Collapsed, pane.SourceToggle.Visibility);
        });

        [Fact]
        public void The_Source_choice_stays_while_one_request_streams_ends_and_is_refreshed() => UiThread.Run(() =>
        {
            var pane = new AiPane { RedrawInterval = TimeSpan.FromSeconds(30) };
            pane.Show(Reading with { Result = "**bo" });
            pane.SourceToggle.IsChecked = true;
            Assert.Equal("**bo", Rendered(pane));

            pane.Show(Reading with { Result = "**bold** and" });
            Assert.True(pane.ShowingSource);
            Assert.Equal("**bo", pane.ResultBox.Shown);           // inside the interval it waits, as a rendered reply does

            pane.Show(Read);                                      // the stream ends
            Assert.True(pane.ShowingSource);
            Assert.Equal("**bold** and `code`", Rendered(pane));  // still the text as it is

            var document = pane.ResultBox.Document;
            pane.Show(Read with { CanInsert = false, Status = "This note is read-only" });   // the window refreshes the view on every keystroke in the note
            Assert.True(pane.ShowingSource);
            Assert.Same(document, pane.ResultBox.Document);       // what is on screen is left alone: a selection in it survives

            pane.Show(Read with { Status = "Inserted below" });   // Insert below: still the same request
            Assert.True(pane.ShowingSource);
            Assert.Same(document, pane.ResultBox.Document);
        });

        [Fact]
        public void A_credential_placeholder_that_becomes_its_pill_mid_stream_does_not_turn_Source_off() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.Show(Reading with { Result = "Log in with [[CREDENTIAL_" });
            pane.SourceToggle.IsChecked = true;

            // The placeholder is whole now and is shown as its pill: the reply does not go on from the text shown, yet it is the same reply.
            pane.Show(Reading with { Result = "Log in with {{secret:K7Q2M9XD}} now" });

            Assert.True(pane.ShowingSource);
            Assert.Equal("Log in with {{secret:K7Q2M9XD}} now", Rendered(pane));

            pane.Show(Read with { Result = "Log in with {{secret:K7Q2M9XD}} now." });
            Assert.True(pane.ShowingSource);
            Assert.Equal("Log in with {{secret:K7Q2M9XD}} now.", Rendered(pane));
        });

        [Fact]
        public void Turning_Source_draws_at_once_though_the_text_did_not_change_and_takes_the_latest_text() => UiThread.Run(() =>
        {
            var pane = new AiPane { RedrawInterval = TimeSpan.FromSeconds(30) };
            pane.Show(Reading with { Result = "**bo" });
            pane.Show(Reading with { Result = "**bold** and" });  // waits for its redraw
            Assert.Equal("**bo", pane.ResultBox.Shown);

            pane.SourceToggle.IsChecked = true;

            Assert.Equal("**bold** and", pane.ResultBox.Shown);
            Assert.Equal("**bold** and", Rendered(pane));

            pane.SourceToggle.IsChecked = false;                  // the same text again: only its form changes

            Assert.Equal("**bold** and", pane.ResultBox.Shown);
            Assert.Equal("bold and", Rendered(pane));
        });

        [Theory]
        [InlineData(false, false)]   // a finished result, rendered
        [InlineData(true, false)]    // the same with Source on
        [InlineData(false, true)]    // while the reply streams in
        [InlineData(true, true)]
        public void Clear_leaves_nothing_of_the_result_in_either_form_and_turns_Source_off(bool source, bool streaming) => UiThread.Run(() =>
        {
            var diagrams = new FakeChatDiagrams { Answer = (_, _) => ChatDiagramFakes.Drawn() };
            var pane = new AiPane { RedrawInterval = TimeSpan.FromMilliseconds(50) };
            pane.ResultBox.Diagrams = diagrams;
            string result = "The login is hunter2.\n\n" + ChatDiagramFakes.Block();
            AiPaneView view = (streaming ? Reading : Read) with { Result = result, Status = streaming ? "" : "This note is read-only" };
            pane.Show(view);
            if (source) pane.SourceToggle.IsChecked = true;
            if (streaming) pane.Show(view with { Result = result + "\n\nAnd the PIN is 4711." });   // a redraw is waiting
            Assert.Contains("hunter2", Rendered(pane), StringComparison.Ordinal);
            Assert.Equal(source ? 0 : 1, InResult<Image>(pane).Count);

            pane.Clear();

            Assert.False(pane.ShowingSource);
            Assert.Equal(Visibility.Collapsed, pane.SourceToggle.Visibility);
            Assert.Equal("", pane.ResultBox.Shown);
            Assert.Equal("", Rendered(pane));
            Assert.Empty(InResult<Image>(pane));
            Assert.Empty(InResult<TextBox>(pane));
            Assert.Equal(1, diagrams.Cleared);                    // the pictures kept for drawing it again are forgotten
            Assert.Equal("", pane.ActivityText.Text);
            Assert.Equal(Visibility.Collapsed, pane.ActivityRow.Visibility);
            Assert.Equal("", pane.InfoText.Text);
            Assert.Equal(Visibility.Collapsed, pane.InfoText.Visibility);
            Assert.Equal("", pane.StatusText.Text);
            Assert.Equal("", pane.TitleText.Text);
            Assert.Equal("", pane.SourceText.Text);

            // Neither form comes back: not with the toggle, forced, and not with the redraw that was waiting.
            pane.SourceToggle.IsChecked = true;
            Assert.Equal("", Rendered(pane));
            pane.SourceToggle.IsChecked = false;
            Assert.Equal("", Rendered(pane));
            var waited = Stopwatch.StartNew();
            PumpUntil(() => pane.ResultBox.Shown.Length > 0 || waited.Elapsed > TimeSpan.FromMilliseconds(300));
            Assert.Equal("", pane.ResultBox.Shown);
            Assert.Equal("", Rendered(pane));

            pane.Show(Read);                                      // the next view draws as usual, rendered
            Assert.False(pane.ShowingSource);
            Assert.Equal(Visibility.Visible, pane.SourceToggle.Visibility);
            Assert.Equal("bold and code", Rendered(pane));
        });

        [Fact]
        public System.Threading.Tasks.Task Clear_makes_the_drawing_engine_forget_the_results_diagrams_drawn_being_drawn_and_waiting() => UiThread.RunAsync(async () =>
        {
            // The real adapter over the real queue, and a page the test answers by hand.
            var page = new FakePage();
            using var renderer = new DiagramRenderer(() => System.Threading.Tasks.Task.FromResult<IDiagramPage>(page));
            var diagrams = new ChatDiagrams(() => renderer, () => true) { Warn = _ => { } };
            var pane = new AiPane();
            pane.ResultBox.Diagrams = diagrams;
            pane.ResultBox.PointerHeld = () => false;
            string[] sources = { "pie\n  \"hunter2\" : 1", "pie\n  \"hunter2\" : 2", "pie\n  \"hunter2\" : 3" };
            pane.Show(Read with { Result = string.Join("\n\n", sources.Select(s => ChatDiagramFakes.Block(s))) });
            await ChatDiagramFakes.Until(() => page.Requests.Count == 1, "the first diagram to reach the page");
            page.Finish(DiagramFakes.Drawn());
            await ChatDiagramFakes.Until(() => page.Requests.Count == 2, "the second diagram to reach the page");
            Assert.Equal(1, renderer.CachedCount);                // the first is stored, the second is being drawn, the third waits
            int told = 0;
            foreach (string source in sources.Skip(1)) diagrams.Get(source, true, () => told++);   // behind the box's own waiters

            pane.Clear();                                         // text of the note was stored as a credential

            Assert.Equal(0, renderer.CachedCount);                // what was drawn went at once
            await ChatDiagramFakes.Until(() => told == 1, "the waiting draw to be answered");
            Assert.Equal(2, page.Requests.Count);
            page.Finish(DiagramFakes.Drawn());
            await ChatDiagramFakes.Until(() => told == 2, "the draw that was running to end");
            await System.Threading.Tasks.Task.Delay(50);

            Assert.DoesNotContain(page.Requests, r => r.Source == sources[2]);   // the one that waited never reached the page
            Assert.Equal(0, renderer.CachedCount);                // and the one that ran was not stored
            Assert.Equal(0, diagrams.PicturesKept);
            Assert.Equal("", Rendered(pane));
        });

        // ---- keeping up while a reply streams (AI chat UI spec 1.5) ---------------------------------

        private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

        /// <summary>
        /// A pane in the middle of a streaming reply, after a costly redraw and with the mouse
        /// button held over the result: a redraw of the stream waits, and would wait again.
        /// </summary>
        private static AiPane HeldMidStream()
        {
            var pane = new AiPane { RedrawInterval = TimeSpan.FromSeconds(30), PointerHeld = () => true };
            pane.Show(Reading with { Result = "**bo" });
            pane.LastRedrawCost = TimeSpan.FromSeconds(1);
            pane.Show(Reading with { Result = "**bold** and" });
            Assert.Equal("**bo", pane.ResultBox.Shown);
            Assert.NotNull(pane.PendingRedraw);
            return pane;
        }

        [Fact]
        public void After_a_draw_that_cost_300_ms_the_next_streamed_text_waits_1200_ms() => UiThread.Run(() =>
        {
            var pane = new AiPane { PointerHeld = () => false };
            var clock = Stopwatch.StartNew();
            pane.Show(Reading with { Result = "**bo" });          // the first text draws at once
            pane.LastRedrawCost = Ms(300);

            pane.Show(Reading with { Result = "**bold** and" });

            Assert.Equal("**bo", pane.ResultBox.Shown);           // not at once, though the plain 100 ms may have passed
            RedrawWaits.AssertWaits(pane.PendingRedraw, Ms(1200), clock);
        });

        [Fact]
        public void A_tick_while_the_pointer_is_held_draws_nothing_and_waits_the_plain_interval_and_the_next_tick_draws() => UiThread.Run(() =>
        {
            bool held = true;
            int asked = 0;
            var pane = new AiPane { RedrawInterval = Ms(20), PointerHeld = () => { asked++; return held; } };
            pane.Show(Reading with { Result = "one" });
            pane.LastRedrawCost = Ms(15);                         // the next redraw is due 60 ms after this one
            pane.Show(Reading with { Result = "one two" });

            PumpUntil(() => asked > 0);

            Assert.True(asked > 0, "the redraw timer fired");
            Assert.Equal("one", pane.ResultBox.Shown);            // a click that began on a button in the result is not lost
            Assert.Equal(Ms(20), pane.PendingRedraw);             // the timer runs again: for the plain interval, not the paced one

            held = false;
            PumpUntil(() => pane.PendingRedraw is null);
            Assert.Equal("one two", pane.ResultBox.Shown);
        });

        [Fact]
        public void Text_that_is_due_at_once_is_not_drawn_while_the_pointer_is_held() => UiThread.Run(() =>
        {
            bool held = true;
            var pane = new AiPane { RedrawInterval = TimeSpan.Zero, PointerHeld = () => held };
            pane.Show(Reading with { Result = "one" });

            pane.Show(Reading with { Result = "one two" });       // nothing to wait for but the pointer

            Assert.Equal("one", pane.ResultBox.Shown);
            Assert.Equal(TimeSpan.Zero, pane.PendingRedraw);

            held = false;
            PumpUntil(() => pane.PendingRedraw is null);
            Assert.Equal("one two", pane.ResultBox.Shown);
        });

        [Fact]
        public void The_first_text_of_a_reply_draws_at_once_while_the_pointer_is_held_and_whatever_a_redraw_cost() => UiThread.Run(() =>
        {
            var pane = new AiPane { RedrawInterval = TimeSpan.FromSeconds(30), PointerHeld = () => true };
            pane.Show(Reading with { Result = "", Activity = "Waiting for the model…" });   // the request starts: nothing to show yet
            pane.LastRedrawCost = TimeSpan.FromSeconds(1);

            pane.Show(Reading);                                   // its first words

            Assert.Equal("**bo", pane.ResultBox.Shown);
            Assert.Null(pane.PendingRedraw);
        });

        [Fact]
        public void A_finished_view_draws_at_once_while_the_pointer_is_held_and_whatever_the_last_redraw_cost() => UiThread.Run(() =>
        {
            AiPane pane = HeldMidStream();

            pane.Show(Read);                                      // the stream ends

            Assert.Equal("**bold** and `code`", pane.ResultBox.Shown);
            Assert.Equal("bold and code", Rendered(pane));
            Assert.Null(pane.PendingRedraw);
        });

        [Fact]
        public void Another_requests_view_draws_at_once_while_the_pointer_is_held_even_when_its_text_goes_on_from_the_one_shown() => UiThread.Run(() =>
        {
            AiPane pane = HeldMidStream();

            pane.Show(Reading with { Title = "Explain", Result = "**bold** and more" });   // another action took over

            Assert.Equal("**bold** and more", pane.ResultBox.Shown);
            Assert.Null(pane.PendingRedraw);
            Assert.Equal(TimeSpan.Zero, pane.LastRedrawCost);     // and a heavy last reply does not slow the first redraws of this one
        });

        [Fact]
        public void Turning_Source_draws_at_once_while_the_pointer_is_held_and_whatever_the_last_redraw_cost() => UiThread.Run(() =>
        {
            AiPane pane = HeldMidStream();

            pane.SourceToggle.IsChecked = true;

            Assert.Equal("**bold** and", pane.ResultBox.Shown);   // with the text that was waiting
            Assert.Equal("**bold** and", Rendered(pane));
            Assert.Null(pane.PendingRedraw);

            pane.LastRedrawCost = TimeSpan.FromSeconds(1);
            pane.SourceToggle.IsChecked = false;
            Assert.Equal("bold and", Rendered(pane));
        });

        [Fact]
        public void Clear_empties_the_pane_at_once_while_the_pointer_is_held_and_stops_every_wait() => UiThread.Run(() =>
        {
            AiPane pane = HeldMidStream();

            pane.Clear();

            Assert.Equal("", pane.ResultBox.Shown);
            Assert.Equal("", Rendered(pane));
            Assert.Null(pane.PendingRedraw);
            Assert.Null(pane.ResultBox.PendingRedraw);
            Assert.Equal(TimeSpan.Zero, pane.LastRedrawCost);
        });

        [Fact]
        public void A_PointerHeld_that_throws_counts_as_not_held_and_is_reported_once_by_its_type() => UiThread.Run(() =>
        {
            var warnings = new List<string>();
            var pane = new AiPane
            {
                RedrawInterval = TimeSpan.Zero,
                Warn = warnings.Add,
                PointerHeld = () => throw new InvalidOperationException("the reply says hunter2"),
            };

            pane.Show(Reading with { Result = "one" });
            pane.Show(Reading with { Result = "one two" });
            pane.Show(Reading with { Result = "one two three" });

            Assert.Equal("one two three", pane.ResultBox.Shown);  // each drawn at once: nothing held it back
            string warning = Assert.Single(warnings);
            Assert.Contains("InvalidOperationException", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", warning, StringComparison.Ordinal);
        });

        [Fact]
        public void A_draw_stores_what_it_cost_once_the_dispatcher_reaches_Loaded() => UiThread.Run(() =>
        {
            var pane = new AiPane();

            pane.Show(Read with { Result = "| a | b |\n|---|---|\n| 1 | 2 |" });

            Assert.Equal(TimeSpan.Zero, pane.LastRedrawCost);     // not when the draw returns: its layout is still to come
            RedrawWaits.ToLoaded();
            Assert.True(pane.LastRedrawCost > TimeSpan.Zero, "the cost of the draw is stored");
        });

        [Fact]
        public void A_pane_cleared_before_its_draw_was_measured_stores_no_cost() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.Show(Read);

            pane.Clear();
            RedrawWaits.ToLoaded();

            Assert.Equal(TimeSpan.Zero, pane.LastRedrawCost);
            Assert.Equal(TimeSpan.Zero, pane.ResultBox.LastRedrawCost);
        });

        [Fact]
        public void A_pane_unloaded_before_its_draw_was_measured_stores_no_cost() => UiThread.Run(() =>
        {
            var pane = new AiPane();
            pane.Show(Read);

            pane.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));   // the window closed
            RedrawWaits.ToLoaded();

            Assert.Equal(TimeSpan.Zero, pane.LastRedrawCost);
        });

        [Fact]
        public void A_picture_that_arrives_after_the_reply_ended_is_drawn_by_the_box_though_the_pane_has_no_new_text() => UiThread.Run(() =>
        {
            var diagrams = new FakeChatDiagrams();
            var pane = new AiPane { PointerHeld = () => false };
            pane.ResultBox.Diagrams = diagrams;
            pane.ResultBox.PointerHeld = () => false;
            pane.Show(Read with { Title = "Draw as diagram", Result = ChatDiagramFakes.Block() });   // finished: the pane draws no more
            Assert.Empty(InResult<Image>(pane));
            diagrams.Answer = (_, _) => ChatDiagramFakes.Drawn();

            diagrams.Gets[0].WhenDone!();                         // the picture arrives

            Assert.Empty(InResult<Image>(pane));                  // not inside whatever ended the draw
            Assert.NotNull(pane.ResultBox.PendingRedraw);         // the box's own timer draws it
            Assert.Null(pane.PendingRedraw);
            PumpUntil(() => pane.ResultBox.PendingRedraw is null);
            Assert.Single(InResult<Image>(pane));
            Assert.Equal(ChatDiagramFakes.Block(), pane.ResultBox.Shown);
        });

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference PaneWithARedrawWaiting()
        {
            var pane = new AiPane { PointerHeld = () => false };
            pane.Show(Reading with { Result = "**bo" });
            pane.LastRedrawCost = Ms(500);                        // the next redraw waits two seconds: its timer runs through the collections
            pane.Show(Reading with { Result = "**bold** and" });
            Assert.NotNull(pane.PendingRedraw);
            return new WeakReference(pane);
        }

        [Fact]
        public void A_redraw_that_waits_does_not_keep_its_pane_alive() => UiThread.Run(() =>
        {
            WeakReference pane = PaneWithARedrawWaiting();

            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(pane.IsAlive);   // a MicaPad window that closed mid-reply is not held by its pane's timer
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
