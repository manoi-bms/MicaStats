using System;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>The area being scrolled: grabs its pixels and scrolls it by wheel notches.</summary>
    public interface IScrollTarget
    {
        PixelFrame Grab();

        /// <summary>Positive scrolls down (content moves up), negative scrolls up.</summary>
        void Scroll(int notches);
    }

    public enum ScrollStop { End, MaxHeight, MaxSteps, Cancelled, NoMatch, SizeChanged, Unscrollable }

    public sealed record ScrollCaptureResult(PixelFrame? Image, ScrollStop Stop, int Frames);

    public sealed record ScrollCaptureOptions(
        int MaxHeight = 20000,
        int MaxSteps = 500,
        int MaxTopNotches = 300,
        int TopNotchesPerStep = 10,
        int SettlePollMs = 50,
        int SettleTimeoutMs = 800);

    /// <summary>
    /// The scrolling capture loop (scrolling capture spec 3): scroll to the top, then step down
    /// one notch at a time and join the frames, until the end, a cap, a cancel or a mismatch.
    /// No Win32 here; the target, the wait and the cancel check are injected.
    /// </summary>
    public sealed class ScrollCaptureRun
    {
        private readonly IScrollTarget _target;
        private readonly Action<int> _wait;
        private readonly Func<bool> _cancelled;
        private readonly Action<int>? _progress;
        private readonly ScrollCaptureOptions _o;

        public ScrollCaptureRun(IScrollTarget target, Action<int> wait, Func<bool> cancelled,
            Action<int>? progress = null, ScrollCaptureOptions? options = null)
        {
            _target = target;
            _wait = wait;
            _cancelled = cancelled;
            _progress = progress;
            _o = options ?? new ScrollCaptureOptions();
        }

        public ScrollCaptureResult Run()
        {
            // Scroll to the top.
            PixelFrame top = Settle();
            int sent = 0;
            while (sent < _o.MaxTopNotches)
            {
                if (_cancelled()) return new ScrollCaptureResult(null, ScrollStop.Cancelled, 0);
                _target.Scroll(-_o.TopNotchesPerStep);
                sent += _o.TopNotchesPerStep;
                PixelFrame after = Settle();
                bool moved = !after.SameAs(top);
                top = after;
                if (!moved) break;
            }
            if (_cancelled()) return new ScrollCaptureResult(null, ScrollStop.Cancelled, 0);

            // Capture down.
            var stitcher = new ScrollStitcher(top);
            int frames = 1, steps = 0;
            while (true)
            {
                _target.Scroll(1);
                steps++;
                PixelFrame next = Settle();
                switch (stitcher.Add(next, out _))
                {
                    case StitchStep.Unchanged:
                        return steps == 1
                            ? new ScrollCaptureResult(top, ScrollStop.Unscrollable, 1)
                            : Done(stitcher, ScrollStop.End, frames);
                    case StitchStep.NoMatch:
                        return Done(stitcher, ScrollStop.NoMatch, frames);
                    case StitchStep.WidthChanged:
                        return Done(stitcher, ScrollStop.SizeChanged, frames);
                }
                frames++;
                _progress?.Invoke(Math.Min(stitcher.Height, _o.MaxHeight));
                if (stitcher.Height >= _o.MaxHeight) return Done(stitcher, ScrollStop.MaxHeight, frames);
                if (_cancelled()) return Done(stitcher, ScrollStop.Cancelled, frames);
                if (steps >= _o.MaxSteps) return Done(stitcher, ScrollStop.MaxSteps, frames);
            }
        }

        private ScrollCaptureResult Done(ScrollStitcher s, ScrollStop stop, int frames)
            => new(s.Result(_o.MaxHeight), stop, frames);

        /// <summary>Grab until two grabs agree, or the settle timeout has passed in waiting.</summary>
        private PixelFrame Settle()
        {
            PixelFrame last = _target.Grab();
            int waited = 0;
            while (waited < _o.SettleTimeoutMs)
            {
                _wait(_o.SettlePollMs);
                waited += _o.SettlePollMs;
                PixelFrame now = _target.Grab();
                bool same = now.SameAs(last);
                last = now;
                if (same) break;
            }
            return last;
        }
    }
}
