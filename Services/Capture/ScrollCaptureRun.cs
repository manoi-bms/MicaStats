using System;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>The area being scrolled: grabs its pixels and scrolls it by wheel notches.</summary>
    public interface IScrollTarget
    {
        PixelFrame Grab();

        /// <summary>
        /// Positive scrolls down (content moves up), negative scrolls up. False when the wheel
        /// cannot be sent to the picked window (it closed, another window came over the area, or
        /// Windows would deliver the wheel elsewhere): the capture then stops.
        /// </summary>
        bool Scroll(int notches);
    }

    public enum ScrollStop { End, MaxHeight, MaxSteps, Cancelled, NoMatch, SizeChanged, Unscrollable, InputLost }

    public sealed record ScrollCaptureResult(PixelFrame? Image, ScrollStop Stop, int Frames);

    /// <param name="Scale">The scale of the monitor the area is on, for the joiner's scrollbar strip.</param>
    public sealed record ScrollCaptureOptions(
        int MaxHeight = 20000,
        int MaxSteps = 500,
        int MaxTopNotches = 300,
        int TopNotchesPerStep = 10,
        int SettlePollMs = 50,
        int SettleTimeoutMs = 800,
        int ScrollSettleFloorMs = 150,
        int EndConfirmMs = 400,
        double Scale = 1.0);

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
            // Scroll to the top. It is reached when a step leaves the view unmoved: compared
            // the way the joiner compares, so a GIF or a spinner in the area does not keep it going.
            PixelFrame top = Settle();
            int sent = 0;
            while (sent < _o.MaxTopNotches)
            {
                if (_cancelled()) return new ScrollCaptureResult(null, ScrollStop.Cancelled, 0);
                if (!_target.Scroll(-_o.TopNotchesPerStep)) return new ScrollCaptureResult(top, ScrollStop.InputLost, 1);
                sent += _o.TopNotchesPerStep;
                PixelFrame after = SettleAfterScroll();
                if (_cancelled()) return new ScrollCaptureResult(null, ScrollStop.Cancelled, 0);
                if (ScrollStitcher.Unmoved(top, after, _o.Scale))
                {
                    // A slow app may not have reacted yet: wait once more before calling it the top.
                    _wait(_o.EndConfirmMs);
                    after = Settle();
                    if (_cancelled()) return new ScrollCaptureResult(null, ScrollStop.Cancelled, 0);
                    if (ScrollStitcher.Unmoved(top, after, _o.Scale))
                    {
                        top = after;
                        break;
                    }
                }
                top = after;
            }
            if (_cancelled()) return new ScrollCaptureResult(null, ScrollStop.Cancelled, 0);

            // The top is reached: from here Esc keeps what was captured, and the card says so.
            _progress?.Invoke(Math.Min(top.Height, _o.MaxHeight));

            // Capture down.
            var stitcher = new ScrollStitcher(top, _o.Scale);
            int frames = 1, steps = 0;
            while (true)
            {
                if (!_target.Scroll(1)) return Done(stitcher, ScrollStop.InputLost, frames);
                steps++;
                PixelFrame next = SettleAfterScroll();
                if (_cancelled()) return Done(stitcher, ScrollStop.Cancelled, frames);
                StitchStep step = stitcher.Add(next, out _);
                if (step == StitchStep.Unchanged)
                {
                    // Confirm the end: a slow app may still be reacting to the wheel.
                    _wait(_o.EndConfirmMs);
                    PixelFrame again = Settle();
                    if (_cancelled()) return Done(stitcher, ScrollStop.Cancelled, frames);
                    if (!again.SameAs(next))
                        step = stitcher.Add(again, out _);
                }
                switch (step)
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

        private PixelFrame SettleAfterScroll()
        {
            _wait(_o.ScrollSettleFloorMs);
            return Settle();
        }

        /// <summary>Grab until two grabs agree, or the settle timeout has passed in waiting. Stops early when cancelled.</summary>
        private PixelFrame Settle()
        {
            PixelFrame last = _target.Grab();
            int waited = 0;
            while (waited < _o.SettleTimeoutMs && !_cancelled())
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
