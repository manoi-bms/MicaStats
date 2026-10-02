using System;
using System.Collections.Generic;
using Kil0bitSystemMonitor.Services.Capture;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class ScrollCaptureRunTests
    {
        private const int View = 300, Notch = 90;

        private sealed class FakePage : IScrollTarget
        {
            private readonly PixelFrame _content;
            private int _from, _to;
            private bool _fresh;
            public bool Animate;
            public int Grabs, DownScrolls;
            public PixelFrame? Override;
            public int OverrideAfterDownScrolls = int.MaxValue;
            public int Top => _to;
            public int LagMs, ScrollCalls, UpNotches;
            /// <summary>Scroll calls after this many fail, as when the wheel cannot reach the window.</summary>
            public int RefuseAfter = int.MaxValue;
            /// <summary>Paints over each grabbed frame, given the row the view starts at.</summary>
            public Func<PixelFrame, int, PixelFrame>? Decorate;
            private readonly List<(int Notches, int Left)> _pending = new();

            /// <summary>Time passes: a lagging app applies its pending scrolls once their delay is over.</summary>
            public void Tick(int ms)
            {
                for (int i = 0; i < _pending.Count; i++) _pending[i] = (_pending[i].Notches, _pending[i].Left - ms);
                while (_pending.Count > 0 && _pending[0].Left <= 0)
                {
                    Apply(_pending[0].Notches);
                    _pending.RemoveAt(0);
                }
            }

            private void Apply(int notches)
            {
                _from = _to;
                _to = Math.Clamp(_to + notches * Notch, 0, _content.Height - View);
                _fresh = true;
            }

            public FakePage(PixelFrame content, int top)
            {
                _content = content;
                _from = _to = top;
            }

            public PixelFrame Grab()
            {
                Grabs++;
                if (DownScrolls >= OverrideAfterDownScrolls && Override != null) return Override;
                int top = _to;
                if (Animate && _fresh) top = (_from + _to) / 2;
                _fresh = false;
                var frame = ScrollStitcherTests.View(_content, top, View);
                return Decorate == null ? frame : Decorate(frame, top);
            }

            public bool Scroll(int notches)
            {
                if (ScrollCalls >= RefuseAfter) return false;
                ScrollCalls++;
                if (notches > 0) DownScrolls++;
                else UpNotches -= notches;
                if (LagMs > 0) _pending.Add((notches, LagMs));
                else Apply(notches);
                return true;
            }
        }

        private sealed class Env
        {
            public int Waits;
            public Func<bool> Cancelled = () => false;
            public Action<int>? OnWait;
            public void Wait(int ms) { Waits += ms; OnWait?.Invoke(ms); }
        }

        private static bool Same(PixelFrame? a, PixelFrame b) => a != null && a.Width == b.Width && a.Height == b.Height && a.SameAs(b);

        private static ScrollCaptureResult Run(FakePage page, Env env, Action<int>? progress = null, ScrollCaptureOptions? options = null)
            => new ScrollCaptureRun(page, env.Wait, () => env.Cancelled(), progress, options).Run();

        [Fact]
        public void Starts_at_the_top_and_captures_the_whole_page()
        {
            var content = ScrollStitcherTests.Page(2000);
            var r = Run(new FakePage(content, 900), new Env());
            Assert.Equal(ScrollStop.End, r.Stop);
            Assert.True(Same(r.Image, content));
        }

        [Fact]
        public void An_animated_scroll_is_settled_before_joining()
        {
            var content = ScrollStitcherTests.Page(2000);
            var r = Run(new FakePage(content, 900) { Animate = true }, new Env());
            Assert.Equal(ScrollStop.End, r.Stop);
            Assert.True(Same(r.Image, content));
        }

        [Fact]
        public void A_half_way_frame_is_never_joined()
        {
            // With a step cap there is no later step to catch up, so a half-way frame would show in the result.
            var content = ScrollStitcherTests.Page(2000);
            var env = new Env();
            var r = Run(new FakePage(content, 900) { Animate = true }, env, null, new ScrollCaptureOptions(MaxSteps: 3));
            Assert.Equal(ScrollStop.MaxSteps, r.Stop);
            Assert.NotNull(r.Image);
            Assert.Equal(View + 3 * Notch, r.Image!.Height);
            Assert.True(Same(r.Image, ScrollStitcherTests.View(content, 0, View + 3 * Notch)));
            Assert.True(env.Waits > 0);
        }

        [Fact]
        public void A_slow_app_is_waited_for_and_the_whole_page_is_captured()
        {
            var content = ScrollStitcherTests.Page(2000);
            var page = new FakePage(content, 900) { LagMs = 120 };
            var env = new Env { OnWait = page.Tick };
            var r = Run(page, env);
            Assert.Equal(ScrollStop.End, r.Stop);
            Assert.True(Same(r.Image, content));
        }

        [Fact]
        public void An_app_slower_than_the_floor_and_a_settle_is_caught_by_the_confirm()
        {
            var content = ScrollStitcherTests.Page(2000);
            var page = new FakePage(content, 900) { LagMs = 300 };
            var env = new Env { OnWait = page.Tick };
            var r = Run(page, env);
            Assert.Equal(ScrollStop.End, r.Stop);
            Assert.True(Same(r.Image, content));
        }

        [Fact]
        public void A_page_that_never_moves_ends_after_the_confirm_wait()
        {
            var content = ScrollStitcherTests.Page(2000);
            var page = new FakePage(content, 0);
            var env = new Env();
            var r = Run(page, env, null, new ScrollCaptureOptions(EndConfirmMs: 400));
            Assert.Equal(ScrollStop.End, r.Stop);
            Assert.True(env.Waits >= 400);
            var flat = new FakePage(ScrollStitcherTests.Page(View), 0);
            var env2 = new Env();
            Assert.Equal(ScrollStop.Unscrollable, Run(flat, env2).Stop);
            Assert.True(env2.Waits >= 400);
        }

        [Fact]
        public void A_cancel_during_a_settle_is_honoured_before_the_next_scroll()
        {
            var content = ScrollStitcherTests.Page(3000);
            var page = new FakePage(content, 0);
            var env = new Env();
            int callsAtCancel = -1;
            env.Cancelled = () =>
            {
                if (env.Waits < 1000) return false;
                if (callsAtCancel < 0) callsAtCancel = page.ScrollCalls;
                return true;
            };
            var r = Run(page, env);
            Assert.Equal(ScrollStop.Cancelled, r.Stop);
            Assert.True(callsAtCancel > 0);
            Assert.Equal(callsAtCancel, page.ScrollCalls);
        }

        [Fact]
        public void Stops_at_the_height_cap()
        {
            var content = ScrollStitcherTests.Page(3000);
            var r = Run(new FakePage(content, 0), new Env(), null, new ScrollCaptureOptions(MaxHeight: 1000));
            Assert.Equal(ScrollStop.MaxHeight, r.Stop);
            Assert.NotNull(r.Image);
            Assert.Equal(1000, r.Image!.Height);
            Assert.True(Same(r.Image, ScrollStitcherTests.View(content, 0, 1000)));
        }

        [Fact]
        public void Stops_at_the_step_cap()
        {
            var content = ScrollStitcherTests.Page(3000);
            var r = Run(new FakePage(content, 0), new Env(), null, new ScrollCaptureOptions(MaxSteps: 3));
            Assert.Equal(ScrollStop.MaxSteps, r.Stop);
            Assert.NotNull(r.Image);
            Assert.Equal(View + 3 * Notch, r.Image!.Height);
        }

        [Fact]
        public void Cancel_while_scrolling_keeps_what_was_joined()
        {
            var content = ScrollStitcherTests.Page(3000);
            var page = new FakePage(content, 0);
            var env = new Env();
            env.Cancelled = () => page.Grabs >= 12;
            var r = Run(page, env);
            Assert.Equal(ScrollStop.Cancelled, r.Stop);
            Assert.NotNull(r.Image);
            Assert.True(r.Image!.Height > View);
        }

        [Fact]
        public void Cancel_during_scroll_to_top_cancels()
        {
            var content = ScrollStitcherTests.Page(2000);
            var env = new Env { Cancelled = () => true };
            var r = Run(new FakePage(content, 900), env);
            Assert.Equal(ScrollStop.Cancelled, r.Stop);
            Assert.Null(r.Image);
        }

        [Fact]
        public void An_area_that_does_not_scroll_returns_its_one_frame()
        {
            var content = ScrollStitcherTests.Page(View);
            var r = Run(new FakePage(content, 0), new Env());
            Assert.Equal(ScrollStop.Unscrollable, r.Stop);
            Assert.NotNull(r.Image);
            Assert.Equal(View, r.Image!.Height);
        }

        [Fact]
        public void A_view_that_changes_unrelatedly_stops_with_no_match()
        {
            var content = ScrollStitcherTests.Page(3000);
            var page = new FakePage(content, 0)
            {
                Override = ScrollStitcherTests.View(ScrollStitcherTests.Page(3000, seed: 99), 500, View),
                OverrideAfterDownScrolls = 2,
            };
            var r = Run(page, new Env());
            Assert.Equal(ScrollStop.NoMatch, r.Stop);
            Assert.NotNull(r.Image);
            Assert.True(r.Image!.Height > View);
        }

        [Fact]
        public void Progress_reports_the_joined_height()
        {
            var content = ScrollStitcherTests.Page(1000);
            var seen = new List<int>();
            var r = Run(new FakePage(content, 0), new Env(), seen.Add);
            Assert.Equal(ScrollStop.End, r.Stop);
            Assert.NotEmpty(seen);
            for (int i = 1; i < seen.Count; i++) Assert.True(seen[i] > seen[i - 1]);
            Assert.Equal(1000, seen[^1]);
        }

        // ----- Final review fixes -------------------------------------------------------------

        /// <summary>A block of rows that changes color on every grab, at a fixed place on screen: a GIF or a spinner.</summary>
        private static Func<PixelFrame, int, PixelFrame> Animation(FakePage page) => (frame, top) =>
        {
            var px = (int[])frame.Pixels.Clone();
            int color = unchecked((int)0xFF000000) | (0x305070 + page.Grabs * 0x030201);
            for (int y = 100; y < 110; y++)
                for (int x = 10; x < 40; x++) px[y * frame.Width + x] = color;
            return new PixelFrame(frame.Width, frame.Height, px);
        };

        [Fact]
        public void An_animation_on_a_page_at_the_top_takes_one_step_to_the_top()
        {
            var content = ScrollStitcherTests.Page(2000);
            var page = new FakePage(content, 0);
            page.Decorate = Animation(page);

            var r = Run(page, new Env());

            Assert.Equal(new ScrollCaptureOptions().TopNotchesPerStep, page.UpNotches);   // not all 300
            Assert.Equal(ScrollStop.End, r.Stop);
            Assert.Equal(2000, r.Image!.Height);
        }

        [Fact]
        public void A_large_animation_on_a_page_at_the_top_still_takes_one_step_to_the_top()
        {
            // A 60 x 50 GIF: too big to pass as a caret, so it is recognised by changing on its own.
            var content = ScrollStitcherTests.Page(2000);
            var page = new FakePage(content, 0);
            page.Decorate = (frame, top) =>
            {
                var px = (int[])frame.Pixels.Clone();
                int color = unchecked((int)0xFF000000) | (0x305070 + page.Grabs * 0x030201);
                for (int y = 120; y < 180; y++)
                    for (int x = 10; x < 60; x++) px[y * frame.Width + x] = color;
                return new PixelFrame(frame.Width, frame.Height, px);
            };

            Run(page, new Env());

            Assert.Equal(new ScrollCaptureOptions().TopNotchesPerStep, page.UpNotches);
        }

        [Fact]
        public void A_pane_replaced_by_one_step_up_under_a_large_fixed_header_is_not_the_top()
        {
            // A 225-row header and 30 px panels stay put; between the panels a 75-row pane shows
            // the page. One step up (900 rows) replaces the pane completely while three quarters
            // of the rows stay the same: the run must keep going up.
            var content = ScrollStitcherTests.Page(2000);
            int w = content.Width;
            var header = ScrollStitcherTests.Noise(w, 225, seed: 61);
            var leftPanel = ScrollStitcherTests.Noise(30, 75, seed: 62);
            var rightPanel = ScrollStitcherTests.Noise(30, 75, seed: 63);
            PixelFrame Layout(PixelFrame frame, int top)
            {
                var px = new int[w * View];
                Array.Copy(header.Pixels, px, header.Pixels.Length);
                var pane = new int[(w - 60) * 75];
                for (int y = 0; y < 75; y++) frame.Row(y).Slice(0, w - 60).CopyTo(pane.AsSpan(y * (w - 60)));
                var lower = ScrollStitcherTests.Beside(leftPanel, new PixelFrame(w - 60, 75, pane), rightPanel);
                Array.Copy(lower.Pixels, 0, px, 225 * w, lower.Pixels.Length);
                return new PixelFrame(w, View, px);
            }
            var page = new FakePage(content, 1700) { Decorate = Layout };

            var r = Run(page, new Env());

            // 1700 to 800 to 0, then a step that leaves it unmoved.
            Assert.Equal(3 * new ScrollCaptureOptions().TopNotchesPerStep, page.UpNotches);
            Assert.True(Same(r.Image, Layout(ScrollStitcherTests.View(content, 0, View), 0)));
        }

        /// <summary>
        /// A static 600-row page of text with a scroll box in rows 220 to 220 + its height,
        /// columns 20-175, white beside it. The wheel scrolls only the box, 90 rows a notch.
        /// </summary>
        private sealed class InnerBox : IScrollTarget
        {
            public const int W = 200, H = 600, BoxTop = 220;
            private readonly PixelFrame _static, _content;
            private readonly int _boxH;
            public int Offset, Downs, JumpAtDown = -1, JumpRows;
            public InnerBox(int boxH, int offset)
            {
                _boxH = boxH;
                Offset = offset;
                _static = Text(W, H, 3);
                _content = Text(156, 4000, 11);
            }
            public PixelFrame Content => _content;
            private int MaxOffset => _content.Height - _boxH;
            private static PixelFrame Text(int w, int h, int seed)
            {
                var rnd = new Random(seed);
                var px = new int[w * h];
                for (int y = 0; y < h; y++)
                {
                    int ink = unchecked((int)0xFF000000) | rnd.Next(0xFFFFFF);
                    int a = rnd.Next(w - 40), b = a + 1 + rnd.Next(30);
                    for (int x = 0; x < w; x++) px[y * w + x] = x >= a && x <= b ? ink : unchecked((int)0xFFFFFFFF);
                }
                return new PixelFrame(w, h, px);
            }
            public PixelFrame Grab()
            {
                var px = (int[])_static.Pixels.Clone();
                for (int y = BoxTop; y < BoxTop + _boxH; y++)
                {
                    for (int x = 0; x < W; x++) px[y * W + x] = unchecked((int)0xFFFFFFFF);
                    _content.Row(Offset + y - BoxTop).CopyTo(px.AsSpan(y * W + 20, 156));
                }
                return new PixelFrame(W, H, px);
            }
            public bool Scroll(int notches)
            {
                if (notches > 0 && ++Downs == JumpAtDown)
                {
                    Offset = Math.Min(MaxOffset, Offset + JumpRows);
                    return true;
                }
                Offset = Math.Clamp(Offset + notches * Notch, 0, MaxOffset);
                return true;
            }
        }

        [Theory]
        [InlineData(120)]
        [InlineData(160)]
        [InlineData(180)]
        public void A_scroll_box_inside_a_still_page_is_scrolled_all_the_way_to_its_top(int boxH)
        {
            // From 2,000 rows down, one step up (900 rows) replaces the whole box while the page
            // around it stays the same: that is not the top.
            var box = new InnerBox(boxH, 2000);

            var r = new ScrollCaptureRun(box, _ => { }, () => false).Run();

            Assert.Equal(ScrollStop.End, r.Stop);
            // The page above, the whole box content from its first row, the page below.
            Assert.Equal(InnerBox.H + box.Content.Height - boxH, r.Image!.Height);
            Assert.True(r.Image.Row(InnerBox.BoxTop).Slice(20, 156).SequenceEqual(box.Content.Row(0)));
        }

        [Fact]
        public void A_scroll_box_whose_content_jumps_stops_with_no_match()
        {
            var box = new InnerBox(160, 0) { JumpAtDown = 4, JumpRows = 700 };

            var r = new ScrollCaptureRun(box, _ => { }, () => false).Run();

            Assert.Equal(ScrollStop.NoMatch, r.Stop);
        }

        [Fact]
        public void The_card_says_Esc_stops_as_soon_as_the_top_is_reached()
        {
            var content = ScrollStitcherTests.Page(1000);
            var page = new FakePage(content, 600);
            var seen = new List<(int Height, int DownScrolls)>();

            Run(page, new Env(), h => seen.Add((h, page.DownScrolls)));

            Assert.Equal((View, 0), seen[0]);
        }

        [Fact]
        public void A_scroll_that_cannot_be_sent_stops_and_keeps_what_was_joined()
        {
            var content = ScrollStitcherTests.Page(3000);
            var page = new FakePage(content, 0) { RefuseAfter = 4 };   // one step up, three down

            var r = Run(page, new Env());

            Assert.Equal(ScrollStop.InputLost, r.Stop);
            Assert.True(Same(r.Image, ScrollStitcherTests.View(content, 0, View + 3 * Notch)));
            Assert.Equal(4, page.ScrollCalls);
        }

        [Fact]
        public void A_scroll_that_cannot_be_sent_at_the_start_keeps_the_one_frame()
        {
            var content = ScrollStitcherTests.Page(3000);
            var page = new FakePage(content, 600) { RefuseAfter = 0 };

            var r = Run(page, new Env());

            Assert.Equal(ScrollStop.InputLost, r.Stop);
            Assert.True(Same(r.Image, ScrollStitcherTests.View(content, 600, View)));
            Assert.Equal(1, r.Frames);
        }

        [Fact]
        public void The_monitor_scale_widens_the_scrollbar_strip_for_the_joiner()
        {
            // A 34 px scrollbar whose 120-row thumb follows the view, as at 175%.
            var content = ScrollStitcherTests.Page(2000);
            PixelFrame WithBar(PixelFrame frame, int top)
            {
                var px = (int[])frame.Pixels.Clone();
                int w = frame.Width, at = top * (View - 120) / (content.Height - View);
                for (int y = 0; y < View; y++)
                    for (int x = w - 34; x < w; x++) px[y * w + x] = y >= at && y < at + 120 ? unchecked((int)0xFF888888) : unchecked((int)0xFFEEEEEE);
                return new PixelFrame(w, View, px);
            }

            var atOne = Run(new FakePage(content, 0) { Decorate = WithBar }, new Env());
            var scaled = Run(new FakePage(content, 0) { Decorate = WithBar }, new Env(), null, new ScrollCaptureOptions(Scale: 1.75));

            Assert.Equal(ScrollStop.NoMatch, atOne.Stop);
            Assert.Equal(ScrollStop.End, scaled.Stop);
            Assert.Equal(2000, scaled.Image!.Height);
        }
    }
}
