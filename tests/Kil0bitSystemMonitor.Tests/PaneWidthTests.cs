using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class PaneWidthTests
    {
        [Fact]
        public void The_constants_are_the_specified_ones()
        {
            Assert.Equal(360, PaneWidth.Default);
            Assert.Equal(260, PaneWidth.Min);
            Assert.Equal(900, PaneWidth.Max);
            Assert.Equal(320, PaneWidth.EditorMin);
        }

        [Fact]
        public void A_wanted_width_inside_the_limits_is_kept()
        {
            Assert.Equal(400, PaneWidth.Fit(400, 1200));
            Assert.Equal(PaneWidth.Default, PaneWidth.Fit(PaneWidth.Default, 1200));
        }

        [Fact]
        public void Under_the_minimum_it_is_the_minimum_and_over_the_maximum_the_maximum()
        {
            Assert.Equal(260, PaneWidth.Fit(100, 2000));
            Assert.Equal(900, PaneWidth.Fit(5000, 2000));
            Assert.Equal(900, PaneWidth.Fit(double.PositiveInfinity, 2000));
            Assert.Equal(360, PaneWidth.Fit(double.NegativeInfinity, 2000));   // not positive: the default first
        }

        [Fact]
        public void The_editor_keeps_its_minimum()
        {
            Assert.Equal(480, PaneWidth.Fit(900, 800));   // a saved 900 in an area of 800
            Assert.Equal(300, PaneWidth.Fit(300, 620));
            Assert.Equal(300, PaneWidth.Fit(900, 620));
        }

        [Fact]
        public void An_area_too_narrow_for_both_gives_the_minimum()
        {
            Assert.Equal(260, PaneWidth.Fit(500, 579));
            Assert.Equal(260, PaneWidth.Fit(500, 580));
            Assert.Equal(260, PaneWidth.Fit(500, 400));
            Assert.Equal(260, PaneWidth.Fit(500, 100));
        }

        [Fact]
        public void An_unmeasured_area_does_not_limit_the_width_and_never_gives_nonsense()
        {
            foreach (double area in new[] { 0, -5, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                Assert.Equal(400, PaneWidth.Fit(400, area));
                double r = PaneWidth.Fit(5000, area);
                Assert.True(r is >= 260 and <= 900, area + " gave " + r);
            }
        }

        [Fact]
        public void A_wanted_width_that_is_not_a_number_or_not_positive_is_the_default_first()
        {
            Assert.Equal(360, PaneWidth.Fit(double.NaN, 1200));
            Assert.Equal(360, PaneWidth.Fit(0, 1200));
            Assert.Equal(360, PaneWidth.Fit(-50, 1200));
            Assert.Equal(360, PaneWidth.Fit(double.NaN, 0));
            Assert.Equal(260, PaneWidth.Fit(double.NaN, 500));   // and then the area still limits it
        }

        [Fact]
        public void No_input_gives_a_result_outside_the_limits()
        {
            double[] values = { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, 0, 1, 259, 260, 360, 900, 901, 1e9 };
            foreach (double w in values)
                foreach (double a in values)
                {
                    double r = PaneWidth.Fit(w, a);
                    Assert.True(!double.IsNaN(r) && r >= 260 && r <= 900, w + "," + a + " gave " + r);
                }
        }

        // ---- the config value ----

        [Fact]
        public void The_config_value_defaults_to_360_and_is_clamped_and_NaN_is_the_default()
        {
            var config = new AppConfig();
            Assert.Equal(360, config.PadPaneWidth);
            config.PadPaneWidth = 100;
            Assert.Equal(260, config.PadPaneWidth);
            config.PadPaneWidth = 5000;
            Assert.Equal(900, config.PadPaneWidth);
            config.PadPaneWidth = double.PositiveInfinity;
            Assert.Equal(900, config.PadPaneWidth);
            config.PadPaneWidth = double.NegativeInfinity;
            Assert.Equal(260, config.PadPaneWidth);
            config.PadPaneWidth = 500;
            config.PadPaneWidth = double.NaN;
            Assert.Equal(360, config.PadPaneWidth);
        }

        [Fact]
        public void The_config_value_round_trips_and_an_older_config_gets_the_default()
        {
            var back = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig { PadPaneWidth = 512 }))!;
            Assert.Equal(512, back.PadPaneWidth);
            Assert.Equal(360, JsonSerializer.Deserialize<AppConfig>("{}")!.PadPaneWidth);
            Assert.Equal(900, JsonSerializer.Deserialize<AppConfig>("{\"PadPaneWidth\": 99999}")!.PadPaneWidth);
        }

        [Fact]
        public void Setting_the_same_value_raises_no_notice_and_a_new_one_raises_one()
        {
            var config = new AppConfig();
            int notices = 0;
            config.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(AppConfig.PadPaneWidth)) notices++; };
            config.PadPaneWidth = 360;
            Assert.Equal(0, notices);
            config.PadPaneWidth = 400;
            Assert.Equal(1, notices);
        }

        // ---- the window ----

        private static Task OnWindow(Action<MicaPadWindow, AppConfig> test) =>
            UiThread.RunAsync(() =>
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
                var config = new AppConfig();
                var window = new MicaPadWindow(env.Workspace, config);
                try
                {
                    window.LoadSession();
                    test(window, config);
                }
                finally
                {
                    window.CloseForExit();
                }
                return Task.CompletedTask;
            }, TimeSpan.FromSeconds(60));

        /// <summary>An unshown window has no layout of its own: the editor area is laid out directly, this wide.</summary>
        private static void LayOut(MicaPadWindow window, double width = 1000)
        {
            window.EditorArea.Measure(new System.Windows.Size(width, 500));
            window.EditorArea.Arrange(new System.Windows.Rect(0, 0, width, 500));
            window.EditorArea.UpdateLayout();
        }

        private static ColumnDefinition PaneColumn(MicaPadWindow w) => w.EditorArea.ColumnDefinitions[2];

        private static void ShowAi(MicaPadWindow w)
        {
            w.SearchPanel.Visibility = Visibility.Collapsed;
            w.AiPanel.Visibility = Visibility.Visible;
        }

        [Fact]
        public Task With_no_pane_shown_the_splitter_is_collapsed_and_the_pane_column_takes_no_room() => OnWindow((w, c) =>
        {
            w.SearchPanel.Visibility = Visibility.Collapsed;
            w.AiPanel.Visibility = Visibility.Collapsed;
            LayOut(w);
            Assert.Equal(3, w.EditorArea.ColumnDefinitions.Count);
            Assert.Equal(Visibility.Collapsed, w.PaneSplitter.Visibility);
            Assert.Equal(0, PaneColumn(w).ActualWidth);
            Assert.True(w.Editor.ActualWidth > 0);
        });

        [Fact]
        public Task With_the_AI_pane_shown_the_splitter_is_visible_and_the_column_has_the_fitted_width() => OnWindow((w, c) =>
        {
            LayOut(w);
            ShowAi(w);
            LayOut(w);
            Assert.Equal(Visibility.Visible, w.PaneSplitter.Visibility);
            Assert.True(PaneColumn(w).Width.IsAbsolute);
            Assert.Equal(PaneWidth.Fit(360, w.EditorArea.ActualWidth - 5), PaneColumn(w).Width.Value);
            Assert.Equal(PaneColumn(w).Width.Value, w.AiPanel.ActualWidth);
        });

        [Fact]
        public Task Showing_Search_notes_after_the_AI_pane_keeps_the_width() => OnWindow((w, c) =>
        {
            c.PadPaneWidth = 480;
            LayOut(w);
            ShowAi(w);
            LayOut(w);
            Assert.Equal(480, PaneColumn(w).Width.Value);

            w.AiPanel.Visibility = Visibility.Collapsed;
            w.SearchPanel.Visibility = Visibility.Visible;
            LayOut(w);
            Assert.Equal(480, PaneColumn(w).Width.Value);
            Assert.Equal(480, w.SearchPanel.ActualWidth);
            Assert.Equal(Visibility.Visible, w.PaneSplitter.Visibility);
        });

        [Fact]
        public Task Setting_the_config_value_resizes_the_shown_pane_and_one_set_while_no_pane_is_shown_applies_later() => OnWindow((w, c) =>
        {
            ShowAi(w);
            LayOut(w);
            c.PadPaneWidth = 500;
            Assert.Equal(500, PaneColumn(w).Width.Value);

            w.AiPanel.Visibility = Visibility.Collapsed;
            c.PadPaneWidth = 420;
            w.AiPanel.Visibility = Visibility.Visible;
            LayOut(w);
            Assert.Equal(420, PaneColumn(w).Width.Value);
        });

        [Fact]
        public Task The_pane_follows_the_window_when_it_is_resized_and_the_saved_value_is_not_changed_by_it() => OnWindow((w, c) =>
        {
            c.PadPaneWidth = 900;
            ShowAi(w);
            LayOut(w, 800);
            double narrow = PaneColumn(w).Width.Value;
            Assert.True(narrow <= w.EditorArea.ActualWidth - PaneWidth.EditorMin, "the editor keeps its minimum: " + narrow);

            LayOut(w, 1600);
            Assert.Equal(900, PaneColumn(w).Width.Value);
            Assert.Equal(900, c.PadPaneWidth);
        });

        [Fact]
        public Task A_window_narrower_than_both_minimums_gives_the_pane_its_minimum_and_does_not_save_it() => OnWindow((w, c) =>
        {
            ShowAi(w);
            LayOut(w, 450);
            Assert.Equal(PaneWidth.Min, PaneColumn(w).Width.Value);
            Assert.Equal(360, c.PadPaneWidth);
        });

        [Fact]
        public Task The_columns_and_the_splitter_have_the_specified_shape() => OnWindow((w, c) =>
        {
            var columns = w.EditorArea.ColumnDefinitions;
            Assert.True(columns[0].Width.IsStar);
            Assert.True(columns[1].Width.IsAuto);
            Assert.Equal(1, Grid.GetColumn(w.PaneSplitter));
            Assert.Equal(5, w.PaneSplitter.Width);
            Assert.True(w.PaneSplitter.Focusable);
            Assert.Equal("Drag to resize; double-click to reset", w.PaneSplitter.ToolTip);
            Assert.True(double.IsNaN(w.AiPanel.Width));
            Assert.True(double.IsNaN(w.SearchPanel.Width));
            Assert.Equal(2, Grid.GetColumn(w.AiPanel));
            Assert.Equal(2, Grid.GetColumn(w.SearchPanel));
        });

        [Fact]
        public Task A_drag_that_ends_writes_the_fitted_actual_width_and_puts_the_columns_back() => OnWindow((w, c) =>
        {
            ShowAi(w);
            LayOut(w);

            // What the splitter may have left: star widths on both.
            PaneColumn(w).Width = new GridLength(1, GridUnitType.Star);
            w.EditorArea.ColumnDefinitions[0].Width = new GridLength(2, GridUnitType.Star);
            w.PaneDragEnded(550);

            Assert.Equal(550, c.PadPaneWidth);
            Assert.True(PaneColumn(w).Width.IsAbsolute);
            Assert.Equal(550, PaneColumn(w).Width.Value);
            Assert.True(w.EditorArea.ColumnDefinitions[0].Width.IsStar);
            Assert.Equal(1, w.EditorArea.ColumnDefinitions[0].Width.Value);
        });

        [Fact]
        public Task A_drag_past_the_limits_is_fitted_before_it_is_saved() => OnWindow((w, c) =>
        {
            ShowAi(w);
            LayOut(w);
            w.PaneDragEnded(100);
            Assert.Equal(260, c.PadPaneWidth);
            w.PaneDragEnded(double.NaN);
            Assert.Equal(360, c.PadPaneWidth);
            w.PaneDragEnded(5000);
            Assert.Equal(PaneWidth.Fit(900, w.EditorArea.ActualWidth - 5), c.PadPaneWidth);
        });

        [Fact]
        public Task Moving_the_column_without_ending_a_drag_does_not_save() => OnWindow((w, c) =>
        {
            int notices = 0;
            c.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(AppConfig.PadPaneWidth)) notices++; };
            ShowAi(w);
            LayOut(w);
            PaneColumn(w).Width = new GridLength(470);
            LayOut(w);
            Assert.Equal(0, notices);
            Assert.Equal(360, c.PadPaneWidth);
        });

        [Fact]
        public Task The_double_click_resets_to_the_default() => OnWindow((w, c) =>
        {
            c.PadPaneWidth = 700;
            ShowAi(w);
            LayOut(w, 1200);
            Assert.Equal(700, PaneColumn(w).Width.Value);
            w.ResetPaneWidth();
            Assert.Equal(360, c.PadPaneWidth);
            Assert.Equal(360, PaneColumn(w).Width.Value);
        });

        [Fact]
        public Task A_change_in_one_window_is_applied_in_another_and_the_other_does_not_write_it_back() => OnWindow((w, c) =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            using var env2 = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            var other = new MicaPadWindow(env2.Workspace, c);
            try
            {
                other.LoadSession();
                ShowAi(w);
                other.AiPanel.Visibility = Visibility.Collapsed;
                other.SearchPanel.Visibility = Visibility.Visible;
                LayOut(w);
                LayOut(other);

                var writes = new List<double>();
                c.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(AppConfig.PadPaneWidth)) writes.Add(c.PadPaneWidth); };
                w.PaneDragEnded(520);

                Assert.Equal(new[] { 520.0 }, writes);   // one notice: the other window did not write back
                Assert.Equal(520, PaneColumn(w).Width.Value);
                Assert.Equal(520, PaneColumn(other).Width.Value);
            }
            finally
            {
                other.CloseForExit();
            }
        });

        [Fact]
        public Task History_alone_keeps_its_own_width_without_the_splitter() => OnWindow((w, c) =>
        {
            w.SearchPanel.Visibility = Visibility.Collapsed;
            w.AiPanel.Visibility = Visibility.Collapsed;
            w.HistoryPanel.Visibility = Visibility.Visible;
            LayOut(w);
            Assert.Equal(Visibility.Collapsed, w.PaneSplitter.Visibility);
            Assert.True(PaneColumn(w).Width.IsAuto);
            Assert.Equal(280, w.HistoryPanel.ActualWidth);
        });

        private static void Invoke(object target, string method, params object[] args) =>
            target.GetType().GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
                .Invoke(target, args);

        [Fact]
        public Task What_the_GridSplitter_writes_after_a_drag_is_read_from_the_pane_and_put_back_as_pixels() => OnWindow((w, c) =>
        {
            ShowAi(w);
            LayOut(w, 1200);
            double before = w.AiPanel.ActualWidth;
            var columns = w.EditorArea.ColumnDefinitions;
            var splitter = w.PaneSplitter;

            // The splitter's own handlers, called as the mouse would (no real input).
            Invoke(splitter, "OnDragStarted", new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0));
            Invoke(splitter, "OnDragDelta", new System.Windows.Controls.Primitives.DragDeltaEventArgs(-100, 0));
            LayOut(w, 1200);
            string written = columns[0].Width + " | " + columns[1].Width + " | " + columns[2].Width;
            Assert.Equal(before + 100, w.AiPanel.ActualWidth, 0.5);
            Assert.Equal(360, c.PadPaneWidth);   // nothing saved while the drag goes on

            Invoke(splitter, "OnDragCompleted", new System.Windows.Controls.Primitives.DragCompletedEventArgs(-100, 0, false));
            // The splitter overrides do not raise the routed event the window listens to; raise it as Thumb would.
            splitter.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(-100, 0, false)
            {
                RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            });
            LayOut(w, 1200);

            Assert.True(Math.Abs(c.PadPaneWidth - (before + 100)) < 0.5, "saved " + c.PadPaneWidth + " after the splitter wrote: " + written);
            Assert.True(columns[2].Width.IsAbsolute, "the pane column is pixels again; the splitter had written: " + written);
            Assert.True(columns[0].Width.IsStar && columns[0].Width.Value == 1);
            Assert.True(columns[1].Width.IsAuto);
        });

        // ---- a closed window ----

        [Fact]
        public Task After_the_window_is_closed_a_pane_visibility_no_longer_moves_the_splitter() => OnWindow((w, c) =>
        {
            w.CloseForExit();
            w.AiPanel.Visibility = Visibility.Visible;
            Assert.Equal(Visibility.Collapsed, w.PaneSplitter.Visibility);
        });

        // ---- the handlers, called the way the routed events would ----

        private sealed class NoSource : PresentationSource
        {
            public override System.Windows.Media.Visual? RootVisual { get; set; }

            public override bool IsDisposed => false;

            protected override System.Windows.Media.CompositionTarget? GetCompositionTargetCore() => null;
        }

        private static void KeyUp(MicaPadWindow w, System.Windows.Input.Key key) =>
            w.PaneSplitter.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, new NoSource(), 0, key)
            {
                RoutedEvent = System.Windows.Input.Keyboard.KeyUpEvent,
            });

        [Fact]
        public Task KeyUp_of_Left_or_Right_on_the_splitter_writes_the_fitted_width_and_another_key_writes_nothing() => OnWindow((w, c) =>
        {
            var writes = new List<double>();
            c.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(AppConfig.PadPaneWidth)) writes.Add(c.PadPaneWidth); };
            ShowAi(w);
            LayOut(w, 1200);

            KeyUp(w, System.Windows.Input.Key.A);
            KeyUp(w, System.Windows.Input.Key.Up);
            Assert.Empty(writes);

            PaneColumn(w).Width = new GridLength(500);   // what the splitter's own arrow-key move leaves
            LayOut(w, 1200);
            KeyUp(w, System.Windows.Input.Key.Left);
            Assert.Equal(new[] { 500.0 }, writes);

            PaneColumn(w).Width = new GridLength(540);
            LayOut(w, 1200);
            KeyUp(w, System.Windows.Input.Key.Right);
            Assert.Equal(new[] { 500.0, 540.0 }, writes);
            Assert.Equal(540, PaneColumn(w).Width.Value);
        });

        [Fact]
        public Task A_double_click_on_the_splitter_resets_the_width_and_marks_the_event_handled() => OnWindow((w, c) =>
        {
            c.PadPaneWidth = 700;
            ShowAi(w);
            LayOut(w, 1200);
            var args = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.Controls.Control.MouseDoubleClickEvent,
            };
            w.PaneSplitter.RaiseEvent(args);
            Assert.True(args.Handled);
            Assert.Equal(360, c.PadPaneWidth);
            Assert.Equal(360, PaneColumn(w).Width.Value);
        });
    }
}
