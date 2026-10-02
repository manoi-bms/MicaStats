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
            // Scroll to the top, judging each step on the part that changed (ScrollStitcher.Compare):
            // unmoved is the top, a shift is not. A change that lines up under no shift is either
            // one step replacing the whole view or something animating, and two looks cannot tell
            // which: a look after one more notch can. An area the joiner can follow moves by one
            // notch in a way that lines up; an animation does not. If that probe changes without
            // lining up either (a contents column whose highlight moved, a scroll landing late), a
            // second probe notch decides. Looks that settle while the view still glides (smooth
            // scrolling) count as a shift too. Every notch counts toward the cap, which is exact.
            _watchShifts = true;
            PixelFrame top = Settle();
            int sent = 0;
            bool reached = false;
            while (sent < _o.MaxTopNotches && !reached)
            {
                if (_cancelled()) return new ScrollCaptureResult(null, ScrollStop.Cancelled, 0);
                int batch = Math.Min(_o.TopNotchesPerStep, _o.MaxTopNotches - sent);
                if (!_target.Scroll(-batch)) return new ScrollCaptureResult(top, ScrollStop.InputLost, 1);
                sent += batch;
                PixelFrame after = SettleAfterScroll();
                if (_cancelled()) return new ScrollCaptureResult(null, ScrollStop.Cancelled, 0);
                ViewChange change = Judge(top, after);
                if (change == ViewChange.Same)
                {
                    // A slow app may not have reacted yet: look once more before calling it the top.
                    after = Confirm();
                    if (_cancelled()) return new ScrollCaptureResult(null, ScrollStop.Cancelled, 0);
                    change = Judge(top, after);
                    reached = change == ViewChange.Same;
                }
                for (int probes = 0; change == ViewChange.Replaced && probes < 2 && sent < _o.MaxTopNotches; probes++)
                {
                    if (!_target.Scroll(-1)) return new ScrollCaptureResult(after, ScrollStop.InputLost, 1);
                    sent++;
                    PixelFrame probe = SettleAfterScroll();
                    if (_cancelled()) return new ScrollCaptureResult(null, ScrollStop.Cancelled, 0);
                    ViewChange moved = Judge(after, probe);
                    if (moved == ViewChange.Same)
                    {
                        probe = Confirm();   // the same slow-app allowance as above
                        if (_cancelled()) return new ScrollCaptureResult(null, ScrollStop.Cancelled, 0);
                        moved = Judge(after, probe);
                    }
                    after = probe;
                    // Nothing changed, or a second notch that lines up no better: what changes is animation.
                    reached = moved == ViewChange.Same || (moved == ViewChange.Replaced && probes == 1);
                    change = moved;
                }
                top = after;
            }
            _watchShifts = false;
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
                    PixelFrame again = Confirm();
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

        /// <summary>How the view changed between two looks: a glide seen inside the last settle is a shift.</summary>
        private ViewChange Judge(PixelFrame before, PixelFrame after) =>
            _sawShift ? ViewChange.Shifted : ScrollStitcher.Compare(before, after, _o.Scale);

        /// <summary>Waits <see cref="ScrollCaptureOptions.EndConfirmMs"/> and looks again: a slow app may still be reacting to the wheel.</summary>
        private PixelFrame Confirm()
        {
            _wait(_o.EndConfirmMs);
            return Settle();
        }

        /// <summary>
        /// Grab until two grabs agree, or the settle timeout has passed in waiting. Stops early
        /// when cancelled. While seeking the top, notes in <see cref="_sawShift"/> whether two
        /// grabs in a row differed by a shift: the view was still gliding.
        /// </summary>
        private PixelFrame Settle()
        {
            _sawShift = false;
            PixelFrame last = _target.Grab();
            int waited = 0;
            while (waited < _o.SettleTimeoutMs && !_cancelled())
            {
                _wait(_o.SettlePollMs);
                waited += _o.SettlePollMs;
                PixelFrame now = _target.Grab();
                bool same = now.SameAs(last);
                if (!same && _watchShifts && !_sawShift)
                    _sawShift = ScrollStitcher.Compare(last, now, _o.Scale) == ViewChange.Shifted;
                last = now;
                if (same) break;
            }
            return last;
        }

        /// <summary>Whether <see cref="Settle"/> looks for shifts between its grabs (the top seek only).</summary>
        private bool _watchShifts;

        /// <summary>Whether the last <see cref="Settle"/> saw two grabs in a row differ by a shift.</summary>
        private bool _sawShift;
    }
}
