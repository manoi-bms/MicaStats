using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    public class TaskDateEditorTests
    {
        [Fact]
        public void Show_uses_invariant_Gregorian_minutes_even_under_Thai_culture_and_does_not_save() => UiThread.Run(() =>
        {
            CultureInfo oldCulture = CultureInfo.CurrentCulture;
            CultureInfo oldUi = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("th-TH");
                CultureInfo.CurrentUICulture = new CultureInfo("th-TH");
                int saves = 0;
                var editor = new TaskDateEditor();

                editor.Show(Dates(new DateTimeOffset(2026, 10, 6, 8, 9, 27, TimeSpan.FromHours(7)),
                                  new DateTimeOffset(2026, 10, 7, 18, 4, 51, TimeSpan.FromHours(7))),
                            () => default, (start, finish) => { saves++; return null; });

                Assert.Equal("2026-10-06 08:09", editor.StartInput.Text);
                Assert.Equal("2026-10-07 18:04", editor.FinishInput.Text);
                Assert.Equal(Visibility.Visible, editor.Visibility);
                Assert.Equal(0, saves);
            }
            finally
            {
                CultureInfo.CurrentCulture = oldCulture;
                CultureInfo.CurrentUICulture = oldUi;
            }
        });

        [Fact]
        public void Saving_unchanged_text_preserves_seconds_and_each_original_offset() => UiThread.Run(() =>
        {
            var originalStart = new DateTimeOffset(2026, 3, 4, 5, 6, 37, TimeSpan.FromHours(7));
            var originalFinish = new DateTimeOffset(2026, 3, 5, 6, 7, 49, TimeSpan.FromHours(9));
            DateTimeOffset? savedStart = null;
            DateTimeOffset? savedFinish = null;
            int dismissed = 0;
            var editor = new TaskDateEditor();
            editor.Dismissed += (_, _) => dismissed++;
            editor.Show(Dates(originalStart, originalFinish), () => default, (start, finish) =>
            {
                savedStart = start;
                savedFinish = finish;
                return null;
            });

            editor.OnSave();

            Assert.Equal(originalStart, savedStart);
            Assert.Equal(originalFinish, savedFinish);
            Assert.Equal(1, dismissed);
            Assert.Equal(Visibility.Collapsed, editor.ErrorText.Visibility);
        });

        [Fact]
        public void Edited_values_use_their_original_offsets_and_an_empty_finish_reopens_the_task() => UiThread.Run(() =>
        {
            var originalStart = new DateTimeOffset(2026, 3, 4, 5, 6, 37, TimeSpan.FromHours(7));
            var originalFinish = new DateTimeOffset(2026, 3, 5, 6, 7, 49, TimeSpan.FromHours(9));
            var saves = new List<(DateTimeOffset Start, DateTimeOffset? Finish)>();
            var editor = new TaskDateEditor();
            editor.Show(Dates(originalStart, originalFinish), () => default, (start, finish) =>
            {
                saves.Add((start, finish));
                return null;
            });

            editor.StartInput.Text = "2026-04-01 10:11";
            editor.FinishInput.Text = "2026-04-02 12:13";
            editor.OnSave();
            Assert.Equal(new DateTimeOffset(2026, 4, 1, 10, 11, 0, TimeSpan.FromHours(7)), saves[0].Start);
            Assert.Equal(new DateTimeOffset(2026, 4, 2, 12, 13, 0, TimeSpan.FromHours(9)), saves[0].Finish);

            editor.FinishInput.Text = "";
            editor.OnSave();
            Assert.Null(saves[1].Finish);

            editor.Show(Dates(originalStart), () => default, (start, finish) =>
            {
                saves.Add((start, finish));
                return null;
            });
            editor.FinishInput.Text = "2026-04-03 14:15";
            editor.OnSave();
            Assert.Equal(new DateTimeOffset(2026, 4, 3, 14, 15, 0, TimeSpan.FromHours(7)), saves[2].Finish);
        });

        [Fact]
        public void Invalid_dates_order_and_callback_errors_stay_open_with_an_inline_message() => UiThread.Run(() =>
        {
            int saves = 0;
            int dismissed = 0;
            var editor = new TaskDateEditor();
            editor.Dismissed += (_, _) => dismissed++;
            editor.Show(Dates(new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero)), () => default,
                        (start, finish) => { saves++; return "The note changed. Open the menu again."; });

            editor.StartInput.Text = "not a date";
            editor.OnSave();
            Assert.Contains("yyyy-MM-dd HH:mm", editor.ErrorText.Text, StringComparison.Ordinal);
            Assert.Equal(0, saves);

            editor.StartInput.Text = "2026-10-06 08:00";
            editor.FinishInput.Text = "2026-10-05 08:00";
            editor.OnSave();
            Assert.Contains("earlier", editor.ErrorText.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, saves);

            editor.FinishInput.Text = "";
            editor.OnSave();
            Assert.Equal(1, saves);
            Assert.Equal("The note changed. Open the menu again.", editor.ErrorText.Text);
            Assert.Equal(Visibility.Visible, editor.Visibility);
            Assert.Equal(0, dismissed);
        });

        [Fact]
        public void A_boundary_date_that_cannot_exist_at_the_original_offset_is_an_inline_error_not_an_exception() => UiThread.Run(() =>
        {
            int saves = 0;
            var editor = new TaskDateEditor();
            editor.Show(Dates(new DateTimeOffset(1, 1, 2, 0, 0, 0, TimeSpan.FromHours(14))), () => default,
                        (start, finish) => { saves++; return null; });
            editor.StartInput.Text = "0001-01-01 00:00";

            editor.OnSave();

            Assert.Equal(0, saves);
            Assert.Equal(Visibility.Visible, editor.ErrorText.Visibility);
            Assert.Contains("yyyy-MM-dd HH:mm", editor.ErrorText.Text, StringComparison.Ordinal);
        });

        [Fact]
        public void Now_buttons_preserve_the_exact_injected_values_and_clear_makes_finish_optional() => UiThread.Run(() =>
        {
            var startNow = new DateTimeOffset(2026, 10, 6, 8, 9, 31, 456, TimeSpan.FromHours(7));
            var finishNow = new DateTimeOffset(2026, 10, 6, 9, 10, 42, 789, TimeSpan.FromHours(7));
            var times = new Queue<DateTimeOffset>(new[] { startNow, finishNow });
            DateTimeOffset? savedStart = null;
            DateTimeOffset? savedFinish = null;
            var editor = new TaskDateEditor();
            editor.Show(Dates(startNow.AddDays(-1)), () => times.Dequeue(), (start, finish) =>
            {
                savedStart = start;
                savedFinish = finish;
                return null;
            });

            editor.StartNowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            editor.FinishNowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            editor.OnSave();
            Assert.Equal(startNow, savedStart);
            Assert.Equal(finishNow, savedFinish);

            editor.ClearFinishButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            editor.OnSave();
            Assert.Null(savedFinish);
        });

        [Fact]
        public void Cancel_and_Escape_never_save_while_plain_Enter_saves_and_modified_Enter_is_left_alone() => UiThread.Run(() =>
        {
            int saves = 0;
            int dismissed = 0;
            var editor = new TaskDateEditor();
            editor.Dismissed += (_, _) => dismissed++;
            editor.Show(Dates(new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero)), () => default,
                        (start, finish) => { saves++; return null; });

            Assert.False(editor.HandleKey(Key.Enter, ModifierKeys.Shift));
            Assert.Equal(0, saves);
            editor.OnCancel();
            Assert.Equal(0, saves);
            Assert.Equal(1, dismissed);

            Assert.True(editor.HandleKey(Key.Escape, ModifierKeys.None));
            Assert.Equal(0, saves);
            Assert.Equal(2, dismissed);
            Assert.True(editor.HandleKey(Key.Enter, ModifierKeys.None));
            Assert.Equal(1, saves);
            Assert.Equal(3, dismissed);
        });

        [Fact]
        public void Routed_Enter_on_buttons_is_left_for_Cancel_Now_and_Clear_instead_of_saving() => UiThread.Run(() =>
        {
            int saves = 0;
            int dismissed = 0;
            var now = new DateTimeOffset(2026, 10, 6, 9, 10, 42, TimeSpan.FromHours(7));
            var editor = new TaskDateEditor();
            editor.Dismissed += (_, _) => dismissed++;
            editor.Show(Dates(new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.FromHours(7)), now), () => now,
                        (start, finish) => { saves++; return null; });

            KeyEventArgs cancelEnter = PreviewEnter(editor.CancelButton);
            Assert.False(cancelEnter.Handled);
            Assert.Equal(0, saves);
            editor.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, dismissed);

            KeyEventArgs nowEnter = PreviewEnter(editor.StartNowButton);
            Assert.False(nowEnter.Handled);
            Assert.Equal(0, saves);
            editor.StartNowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("2026-10-06 09:10", editor.StartInput.Text);

            KeyEventArgs clearEnter = PreviewEnter(editor.ClearFinishButton);
            Assert.False(clearEnter.Handled);
            Assert.Equal(0, saves);
            editor.ClearFinishButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("", editor.FinishInput.Text);
        });

        [Fact]
        public void Hide_clears_inputs_errors_callbacks_and_the_draft() => UiThread.Run(() =>
        {
            int saves = 0;
            var editor = new TaskDateEditor();
            editor.Show(Dates(new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero)), () => default,
                        (start, finish) => { saves++; return null; });
            editor.StartInput.Text = "invalid";
            editor.OnSave();
            Assert.Equal(Visibility.Visible, editor.ErrorText.Visibility);

            editor.Hide();
            editor.OnSave();

            Assert.Equal(Visibility.Collapsed, editor.Visibility);
            Assert.Equal("", editor.StartInput.Text);
            Assert.Equal("", editor.FinishInput.Text);
            Assert.Equal("", editor.ErrorText.Text);
            Assert.Equal(Visibility.Collapsed, editor.ErrorText.Visibility);
            Assert.Equal(0, saves);
        });

        [Fact]
        public void Narrow_short_popup_layout_shrinks_fields_and_scrolls_without_horizontal_overflow() => UiThread.Run(() =>
        {
            var editor = new TaskDateEditor { Width = 240, Height = 160 };
            ModernWpf.ThemeManager.SetRequestedTheme(editor, ModernWpf.ElementTheme.Dark);
            PadThemeApplier.ApplyResources(editor.Resources, PadPalette.Dark);
            editor.Show(Dates(new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero)), () => default,
                        (start, finish) => null);

            editor.Measure(new Size(240, 160));
            editor.Arrange(new Rect(0, 0, 240, 160));
            editor.UpdateLayout();

            Assert.Equal(240, editor.ActualWidth);
            Assert.Equal(160, editor.ActualHeight);
            Assert.True(editor.StartInput.ActualWidth > 20);
            Assert.True(editor.FinishInput.ActualWidth > 20);
            Assert.Equal(0, editor.EditorScroll.ScrollableWidth);
            Assert.True(editor.EditorScroll.ScrollableHeight > 0);
        });

        private static TaskDateRecord Dates(DateTimeOffset start, DateTimeOffset? finish = null) =>
            new("task-id", 0, "- [ ] Task", start, finish);

        private static KeyEventArgs PreviewEnter(Button button)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, new NoSource(), 0, Key.Enter)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            button.RaiseEvent(args);
            return args;
        }

        private sealed class NoSource : System.Windows.PresentationSource
        {
            public override System.Windows.Media.Visual? RootVisual { get; set; }

            public override bool IsDisposed => false;

            protected override System.Windows.Media.CompositionTarget? GetCompositionTargetCore() => null;
        }
    }
}
