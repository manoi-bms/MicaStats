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
                return ScrollStitcherTests.View(_content, top, View);
            }

            public void Scroll(int notches)
            {
                if (notches > 0) DownScrolls++;
                _from = _to;
                _to = Math.Clamp(_to + notches * Notch, 0, _content.Height - View);
                _fresh = true;
            }
        }

        private sealed class Env
        {
            public int Waits;
            public Func<bool> Cancelled = () => false;
            public void Wait(int ms) => Waits += ms;
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
    }
}
