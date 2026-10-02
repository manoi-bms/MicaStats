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

            // One step, then two probe notches that move nothing lining up: the block animates.
            Assert.Equal(new ScrollCaptureOptions().TopNotchesPerStep + 2, page.UpNotches);
        }

        [Fact]
        public void A_pane_replaced_by_one_step_up_under_a_large_fixed_header_is_not_the_top()
        {
            // A 160-row header and 30 px panels stay put, three quarters of the area; between the
            // panels a 140-row pane shows the page. One step up (900 rows) replaces the pane
            // completely; one notch more moves it in a way that lines up, so the run keeps going up.
            var content = ScrollStitcherTests.Page(2000);
            int w = content.Width;
            const int headerRows = 160, paneRows = View - headerRows;
            var header = ScrollStitcherTests.Noise(w, headerRows, seed: 61);
            var leftPanel = ScrollStitcherTests.Noise(30, paneRows, seed: 62);
            var rightPanel = ScrollStitcherTests.Noise(30, paneRows, seed: 63);
            PixelFrame Layout(PixelFrame frame, int top)
            {
                var px = new int[w * View];
                Array.Copy(header.Pixels, px, header.Pixels.Length);
                var pane = new int[(w - 60) * paneRows];
                for (int y = 0; y < paneRows; y++) frame.Row(y).Slice(0, w - 60).CopyTo(pane.AsSpan(y * (w - 60)));
                var lower = ScrollStitcherTests.Beside(leftPanel, new PixelFrame(w - 60, paneRows, pane), rightPanel);
                Array.Copy(lower.Pixels, 0, px, headerRows * w, lower.Pixels.Length);
                return new PixelFrame(w, View, px);
            }
            var page = new FakePage(content, 1700) { Decorate = Layout };
            var env = new Env { Cancelled = () => page.DownScrolls > 0 };   // the top is all this checks

            var r = Run(page, env);

            // 1700 to 800 and a probe notch to 710 that lines up; 710 to 0 and a probe that moves nothing.
            Assert.Equal(2 * (new ScrollCaptureOptions().TopNotchesPerStep + 1), page.UpNotches);
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
            public int Offset, Downs, Ups, JumpAtDown = -1, JumpRows;
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
                if (notches < 0) Ups -= notches;
                Offset = Math.Clamp(Offset + notches * Notch, 0, MaxOffset);
                return true;
            }
        }

        [Fact]
        public void A_box_shorter_than_one_notch_plus_8_rows_is_taken_for_the_top_and_stops_with_no_match()
        {
            // An 80-row box scrolled 90 rows a notch: one notch replaces it entirely, so both probe
            // notches after the first step up see nothing line up and call it the top. The joiner
            // needs 8 rows of overlap, so the first step down stops with the note.
            var box = new InnerBox(80, 2000);

            var r = new ScrollCaptureRun(box, _ => { }, () => false).Run();

            Assert.Equal(new ScrollCaptureOptions().TopNotchesPerStep + 2, box.Ups);
            Assert.Equal(ScrollStop.NoMatch, r.Stop);
            Assert.Equal("Stopped: the view changed in a way MicaStats could not follow", CaptureService.ScrollNote(r.Stop));
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

        // ----- Round 3: what moves by itself, on a virtual clock --------------------------------------

        /// <summary>Something on the page that changes every <see cref="PeriodMs"/>: random pixels, or a caret that blinks.</summary>
        private sealed record Anim(int X, int Y, int W, int H, int PeriodMs, int Seed = 1, bool InContent = false, bool Blink = false);

        /// <summary>
        /// A page on a virtual clock (the reviewers' probe page): grabs take 10 ms, waits add to the
        /// clock, a scroll eases out over <see cref="ScrollMs"/> to where the notches lead, and
        /// animations change every period. A header, when given, stays put above the page.
        /// </summary>
        private sealed class ClockPage : IScrollTarget
        {
            public long Now;
            public readonly List<Anim> Anims = new();
            public int UpNotches, Downs, StartOffset = -1;
            private readonly PixelFrame _content;
            private readonly PixelFrame? _header;
            private readonly int _h, _bodyTop, _bodyH, _notch, _scrollMs;
            private double _from, _to;
            private long _t0;

            public ClockPage(PixelFrame content, int h, int notch, int offset, int scrollMs = 0, PixelFrame? header = null)
            {
                _content = content;
                _h = h;
                _notch = notch;
                _scrollMs = scrollMs;
                _header = header;
                _bodyTop = header?.Height ?? 0;
                _bodyH = h - _bodyTop;
                _from = _to = offset;
            }

            public int Offset
            {
                get
                {
                    if (_scrollMs <= 0 || Now - _t0 >= _scrollMs) return (int)_to;
                    double p = (Now - _t0) / (double)_scrollMs;
                    p = 1 - (1 - p) * (1 - p) * (1 - p);
                    return (int)Math.Round(_from + (_to - _from) * p);
                }
            }

            /// <summary>Wheel input takes effect this long after it is sent.</summary>
            public int LatencyMs;
            private readonly List<(long At, int Notches)> _late = new();

            public bool Scroll(int notches)
            {
                if (notches < 0) UpNotches -= notches;
                else { if (Downs == 0) StartOffset = Offset; Downs++; }
                if (LatencyMs > 0) _late.Add((Now + LatencyMs, notches));
                else Apply(notches);
                return true;
            }

            private void Apply(int notches)
            {
                int at = Offset;
                _from = at;
                _to = Math.Clamp(_to + notches * _notch, 0, _content.Height - _bodyH);
                _t0 = Now;
            }

            public PixelFrame Grab()
            {
                Now += 10;
                while (_late.Count > 0 && _late[0].At <= Now)
                {
                    Apply(_late[0].Notches);
                    _late.RemoveAt(0);
                }
                int w = _content.Width, off = Offset;
                var px = new int[w * _h];
                if (_header != null) Array.Copy(_header.Pixels, px, _header.Pixels.Length);
                Array.Copy(_content.Pixels, off * w, px, _bodyTop * w, _bodyH * w);
                foreach (var a in Anims)
                {
                    long k = Now / a.PeriodMs;
                    var rnd = new Random(a.Seed * 7919 + (int)(a.Blink ? k % 2 : k));
                    int y0 = a.InContent ? a.Y - off + _bodyTop : a.Y;
                    for (int y = Math.Max(y0, a.InContent ? _bodyTop : 0); y < Math.Min(_h, y0 + a.H); y++)
                        for (int x = a.X; x < a.X + a.W; x++)
                            px[y * w + x] = a.Blink
                                ? (k % 2 == 0 ? unchecked((int)0xFF000000) : px[y * w + x])
                                : (rnd.Next(2) == 0 ? unchecked((int)0xFF000000) | rnd.Next(0xFFFFFF) : unchecked((int)0xFFFFFFFF));
                }
                return new PixelFrame(w, _h, px);
            }
        }

        /// <summary>Runs until the first step down (all a top test needs), or to the end.</summary>
        private static ScrollCaptureResult RunOn(ClockPage p, bool toTheEnd = false)
            => new ScrollCaptureRun(p, ms => p.Now += ms, () => !toTheEnd && p.Downs > 0).Run();

        [Theory]
        [InlineData(false, 1500)]
        [InlineData(false, 3000)]
        [InlineData(false, 4200)]
        [InlineData(true, 1500)]
        [InlineData(true, 3000)]
        [InlineData(true, 4200)]
        public void Animations_in_opposite_corners_do_not_make_a_real_scroll_look_like_the_top(bool bubble, int start)
        {
            // Claude: a 900-row view, a 60-row header with a 40 x 40 logo at 60 fps, and a 22-row
            // ticker along the bottom or a 48 x 50 chat bubble flush with the bottom right.
            var p = new ClockPage(ScrollStitcherTests.Article(300, 6000, 33), 900, 100, start, 0, ScrollStitcherTests.NoiseBlock(300, 60, 11));
            p.Anims.Add(new Anim(6, 8, 40, 40, 16, Seed: 5));
            p.Anims.Add(bubble ? new Anim(226, 850, 48, 50, 16, Seed: 6) : new Anim(0, 878, 276, 22, 16, Seed: 7));

            RunOn(p);

            Assert.Equal(0, p.StartOffset);
        }

        // ----- Round 4 ------------------------------------------------------------------------------

        /// <summary>A view drawn by a callback from (offset, now), on a virtual clock: the reviewers' probe page.</summary>
        private sealed class LivePage : IScrollTarget
        {
            public long Now;
            public int UpNotches, Downs, StartOffset = -1;
            private readonly Func<int, long, PixelFrame> _render;
            private readonly int _max, _notch;
            private int _offset;

            public LivePage(Func<int, long, PixelFrame> render, int max, int notch, int offset)
            {
                _render = render;
                _max = max;
                _notch = notch;
                _offset = offset;
            }

            public bool Scroll(int notches)
            {
                if (notches < 0) UpNotches -= notches;
                else { if (Downs == 0) StartOffset = _offset; Downs++; }
                _offset = Math.Clamp(_offset + notches * _notch, 0, _max);
                return true;
            }

            public PixelFrame Grab()
            {
                Now += 10;
                return _render(_offset, Now);
            }
        }

        private static ScrollCaptureResult RunOn(LivePage p, bool toTheEnd = false)
            => new ScrollCaptureRun(p, ms => p.Now += ms, () => !toTheEnd && p.Downs > 0).Run();

        private static int AnimPixel(long now, int periodMs, int seed, int x, int y)
        {
            var r = new Random(seed * 7919 + (int)(now / periodMs) * 131 + x * 31 + y * 17);
            return r.Next(2) == 0 ? unchecked((int)0xFF000000) | r.Next(0xFFFFFF) : unchecked((int)0xFFFFFFFF);
        }

        /// <summary>
        /// Claude's docs page: 400 x 900, a 50-row header, a 140 px contents column filling the
        /// height (an entry every <paramref name="pitch"/> rows) whose active entry follows the
        /// section in view, the article to its right.
        /// </summary>
        private static PixelFrame DocsView(PixelFrame content, PixelFrame header, int pitch, int sectionLen, bool boxed, int offset)
        {
            const int W = 400, H = 900, NavW = 140, CX = 150;
            int white = unchecked((int)0xFFFFFFFF), navBg = unchecked((int)0xFFF6F6F6);
            var px = new int[W * H];
            Array.Copy(header.Pixels, px, header.Pixels.Length);
            int hh = header.Height, count = (H - hh - 6) / pitch;
            for (int y = hh; y < H; y++)
            {
                for (int x = 0; x < NavW; x++) px[y * W + x] = navBg;
                for (int x = NavW; x < CX; x++) px[y * W + x] = white;
                content.Row(offset + y - hh).CopyTo(px.AsSpan(y * W + CX, content.Width));
            }
            int active = ((offset + 100) / sectionLen) % count;
            for (int e = 0; e < count; e++)
            {
                int y0 = hh + 6 + e * pitch;
                if (y0 + pitch > H) break;
                var rnd = new Random(1000 + e);
                int len = 50 + rnd.Next(70);
                bool on = e == active;
                if (on && boxed)
                    for (int y = y0 + 2; y < y0 + pitch - 2; y++) for (int x = 4; x < NavW - 4; x++) px[y * W + x] = unchecked((int)0xFFDDE8FF);
                for (int y = y0 + 7; y < y0 + 19; y++)
                    for (int x = 12; x < 12 + len; x++)
                        if (rnd.Next(3) == 0)
                        {
                            int c = unchecked((int)0xFF000000) | rnd.Next(0x808080);
                            px[y * W + x] = on && !boxed ? unchecked((int)0xFF2050C0) : c;
                            if (on && !boxed) px[y * W + x + 1] = unchecked((int)0xFF2050C0);
                        }
            }
            return new PixelFrame(W, H, px);
        }

        [Theory]
        [InlineData(false, 400, 2500)]
        [InlineData(false, 400, 5500)]
        [InlineData(false, 400, 7000)]
        [InlineData(false, 400, 8000)]
        [InlineData(false, 700, 4000)]
        [InlineData(false, 700, 5500)]
        [InlineData(true, 700, 8000)]
        [InlineData(true, 1200, 5500)]
        [InlineData(true, 1200, 8000)]
        public void A_contents_column_whose_highlight_follows_the_section_does_not_fake_the_top(bool boxed, int sectionLen, int start)
        {
            // Claude: the probe notch crosses a section boundary, so the highlight moves and the
            // column can no longer be left out; a second probe notch moves the article in a way
            // that lines up.
            var content = ScrollStitcherTests.Article(250, 9000, 41, left: 10, right: 34);
            var header = ScrollStitcherTests.NoiseBlock(400, 50, 3);
            var p = new LivePage((off, now) => DocsView(content, header, 26, sectionLen, boxed, off), 9000 - 850, 100, start);

            RunOn(p);

            Assert.Equal(0, p.StartOffset);
        }

        [Fact]
        public void A_docs_page_with_a_following_contents_column_is_captured_whole()
        {
            var content = ScrollStitcherTests.Article(250, 9000, 41, left: 10, right: 34);
            var header = ScrollStitcherTests.NoiseBlock(400, 50, 3);
            var p = new LivePage((off, now) => DocsView(content, header, 26, 400, false, off), 9000 - 850, 100, 5500);

            var r = RunOn(p, toTheEnd: true);

            Assert.Equal(0, p.StartOffset);
            Assert.Equal(ScrollStop.End, r.Stop);
            Assert.Equal(50 + 9000, r.Image!.Height);
        }

        [Fact]
        public void Wheel_input_that_lands_late_while_something_animates_does_not_fake_the_top()
        {
            // Codex: wheel input takes effect 300 ms after it is sent, and a block changes every 200 ms.
            var p = new ClockPage(ScrollStitcherTests.Article(300, 3000, 17), 300, 90, 1700) { LatencyMs = 300 };
            p.Anims.Add(new Anim(150, 120, 40, 40, 200, Seed: 4));

            RunOn(p);

            Assert.Equal(0, p.StartOffset);
        }

        [Theory]
        [InlineData("blockquote", 16, 0)]
        [InlineData("blockquote", 100, 0)]
        [InlineData("blockquote", 16, 2500)]
        [InlineData("table", 16, 0)]
        [InlineData("table", 100, 2500)]
        public void A_gif_crossed_by_a_vertical_line_reaches_the_top_quickly(string lines, int periodMs, int start)
        {
            // Claude: a 120 x 90 GIF near the top of a 300 x 900 page, and a vertical line through its
            // columns. Rows that did not change line up with themselves one row off; they must not
            // count as a shift, or the seek runs to 300 notches.
            const int w = 300;
            var px = (int[])ScrollStitcherTests.Article(w, 6000, 33).Pixels.Clone();
            int gray = unchecked((int)0xFFC8C8C8);
            if (lines == "blockquote")
                for (int y = 300; y < 900; y++) for (int x = 40; x < 44; x++) px[y * w + x] = gray;
            else
                for (int y = 300; y < 1200; y++) foreach (int x in new[] { 20, 100, 180, 270 }) px[y * w + x] = gray;
            var content = new PixelFrame(w, 6000, px);
            var p = new LivePage((off, now) =>
            {
                var f = content.Pixels.AsSpan(off * w, 900 * w).ToArray();
                for (int y = Math.Max(0, 100 - off); y < Math.Min(900, 190 - off); y++)
                    for (int x = 30; x < 150; x++) f[y * w + x] = AnimPixel(now, periodMs, 3, x, y);
                return new PixelFrame(w, 900, f);
            }, 6000 - 900, 100, start);

            RunOn(p);

            Assert.Equal(0, p.StartOffset);
            Assert.True(p.UpNotches <= (start / 1000 + 2) * (new ScrollCaptureOptions().TopNotchesPerStep + 2), p.UpNotches + " up-notches");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2500)]
        public void A_header_of_repeated_scanlines_with_a_gif_below_reaches_the_top_quickly(int start)
        {
            // Codex: a 64-row header whose scanlines repeat every 2 rows lines up with itself 2 rows
            // off; it did not change, so it is no evidence of a scroll.
            const int w = 300, h = 900, hh = 64;
            var scan = ScrollStitcherTests.NoiseBlock(w, 2, 12);
            var header = new int[w * hh];
            for (int y = 0; y < hh; y++) scan.Row(y % 2).CopyTo(header.AsSpan(y * w, w));
            var content = ScrollStitcherTests.Article(w, 6000, 33);
            var p = new LivePage((off, now) =>
            {
                var f = new int[w * h];
                Array.Copy(header, f, header.Length);
                Array.Copy(content.Pixels, off * w, f, hh * w, (h - hh) * w);
                for (int y = Math.Max(hh, hh + 100 - off); y < Math.Min(h, hh + 190 - off); y++)
                    for (int x = 30; x < 150; x++) f[y * w + x] = AnimPixel(now, 16, 3, x, y);
                return new PixelFrame(w, h, f);
            }, 6000 - (h - hh), 100, start);

            RunOn(p);

            Assert.Equal(0, p.StartOffset);
            Assert.True(p.UpNotches <= (start / 1000 + 2) * (new ScrollCaptureOptions().TopNotchesPerStep + 2), p.UpNotches + " up-notches");
        }

        [Fact]
        public void The_notch_cap_is_exact_even_with_a_probe_after_every_step()
        {
            // Every 10-notch step replaces the 300-row view (900 rows) and its probe notch lines up,
            // so each step costs 11 notches; the last step is cut to what the cap leaves.
            var content = ScrollStitcherTests.Page(28000);
            var page = new FakePage(content, 27600);
            var env = new Env { Cancelled = () => page.DownScrolls > 0 };

            Run(page, env);

            Assert.Equal(new ScrollCaptureOptions().MaxTopNotches, page.UpNotches);
        }

        [Theory]
        [InlineData(16, 1700)]
        [InlineData(16, 2500)]
        [InlineData(100, 3000)]
        public void A_logo_animating_in_a_small_header_does_not_hide_the_probe_notch(int periodMs, int start)
        {
            // A 300-row view: a 40-row header with a 24 x 24 logo that animates, the page below.
            // The probe notch moves the page 100 rows while the logo changes; the page's rows still
            // line up in a long run, so the run keeps going up.
            var header = ScrollStitcherTests.NoiseBlock(200, 40, 1);
            var p = new ClockPage(ScrollStitcherTests.Article(200, 4000, 21), 300, 100, start, 0, header);
            p.Anims.Add(new Anim(6, 8, 24, 24, periodMs, Seed: 5));

            RunOn(p);

            Assert.Equal(0, p.StartOffset);
        }

        [Fact]
        public void Two_corner_widgets_do_not_turn_a_step_from_1700_to_800_into_the_top()
        {
            // Codex: two widgets changing all the time in opposite corners (top-left, and bottom-right
            // against the scrollbar strip); one step up goes 1700 to 800.
            var p = new ClockPage(ScrollStitcherTests.Article(300, 3000, 17), 600, 90, 1700);
            p.Anims.Add(new Anim(0, 0, 30, 30, 16, Seed: 2));
            p.Anims.Add(new Anim(300 - 24 - 32, 600 - 36, 32, 36, 16, Seed: 3));

            RunOn(p);

            Assert.Equal(0, p.StartOffset);
        }

        [Theory]
        [InlineData(17, 0)]
        [InlineData(17, 130)]
        [InlineData(17, 270)]
        [InlineData(17, 400)]
        [InlineData(28, 0)]
        [InlineData(28, 130)]
        [InlineData(28, 270)]
        [InlineData(28, 400)]
        public void A_blinking_caret_at_the_top_takes_at_most_two_steps(int caretH, int phase)
        {
            // An editor at its top, the caret blinking every 530 ms, at any point of its blink.
            var p = new ClockPage(ScrollStitcherTests.Article(200, 4000, 21), 300, 100, 0) { Now = phase };
            p.Anims.Add(new Anim(60, 4, 2, caretH, 530, InContent: true, Blink: true));

            RunOn(p);

            Assert.True(p.UpNotches <= 2 * new ScrollCaptureOptions().TopNotchesPerStep + 2, p.UpNotches + " up-notches");   // two steps and two probes
            Assert.Equal(0, p.StartOffset);
        }

        [Theory]
        [InlineData(100)]   // 10 fps
        [InlineData(500)]   // 2 fps
        public void A_slow_gif_at_the_top_takes_at_most_two_steps(int periodMs)
        {
            var p = new ClockPage(ScrollStitcherTests.Article(300, 6000, 33), 900, 100, 0);
            p.Anims.Add(new Anim(30, 100, 120, 90, periodMs, Seed: 3, InContent: true));

            RunOn(p);

            Assert.True(p.UpNotches <= 2 * new ScrollCaptureOptions().TopNotchesPerStep + 2, p.UpNotches + " up-notches");   // two steps and two probes
        }

        [Fact]
        public void A_text_box_that_does_not_scroll_with_a_tall_caret_says_nothing_scrolled()
        {
            var p = new ClockPage(ScrollStitcherTests.Article(200, 300, 4), 300, 100, 0);
            p.Anims.Add(new Anim(90, 100, 2, 20, 530, Blink: true));

            var r = RunOn(p, toTheEnd: true);

            Assert.Equal(ScrollStop.Unscrollable, r.Stop);
            Assert.True(p.UpNotches <= 2 * new ScrollCaptureOptions().TopNotchesPerStep + 2, p.UpNotches + " up-notches");   // two steps and two probes
            Assert.True(p.Downs <= 2, p.Downs + " steps down");
        }

        [Fact]
        public void A_slow_smooth_scroll_with_an_animation_still_reaches_the_top()
        {
            // Claude: every scroll eases out over 2.5 s while a 30 x 30 animation runs at 60 fps.
            var p = new ClockPage(ScrollStitcherTests.Article(200, 4000, 21), 300, 100, 2500, scrollMs: 2500);
            p.Anims.Add(new Anim(140, 10, 30, 30, 16, Seed: 9));

            RunOn(p);

            Assert.Equal(0, p.StartOffset);
        }

        [Fact]
        public void A_caret_at_the_end_of_a_repeating_page_ends_without_adding_rows()
        {
            // Codex: at the end the view stops moving, its content repeats every 90 rows, and only a
            // 12-row caret near row 260 changed; that must end the capture, not join at shift 90.
            var distinct = ScrollStitcherTests.Page(690);
            int w = distinct.Width;
            var px = new int[w * 900];
            Array.Copy(distinct.Pixels, px, 600 * w);
            for (int y = 600; y < 900; y++) distinct.Row(600 + (y - 600) % 90).CopyTo(px.AsSpan(y * w, w));
            var content = new PixelFrame(w, 900, px);
            var page = new FakePage(content, 0);
            // The caret shows after an even number of steps: it appears in the look after the last one.
            page.Decorate = (frame, top) => page.DownScrolls % 2 == 0
                ? ScrollStitcherTests.WithBlock(frame, 30, 258, 2, 12, unchecked((int)0xFF000000))
                : frame;

            var r = Run(page, new Env());

            Assert.Equal(ScrollStop.End, r.Stop);
            Assert.Equal(900, r.Image!.Height);
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
