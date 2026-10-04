using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// How often an answer that streams in may be drawn again (AI chat UI spec 1.5). A redraw
    /// builds the whole document from the whole text, so its cost grows with the answer: one
    /// with a table of 100 rows by 12 columns takes about 0.6 s, against a redraw timer of
    /// 100 ms. The pace keeps a redraw to one part in five of the time.
    /// </summary>
    internal static class RedrawPace
    {
        /// <summary>A redraw is followed by this many times what it cost before the next: it may take one part in five of the time.</summary>
        public const int Share = 4;

        /// <summary>The longest the cost of a redraw makes the next one wait.</summary>
        public static readonly TimeSpan Longest = TimeSpan.FromSeconds(2);

        /// <summary>
        /// The shortest time from one redraw to the next: at least <paramref name="least"/>, at
        /// least <see cref="Share"/> times <paramref name="lastCost"/>, at most
        /// <see cref="Longest"/>, and never less than <paramref name="least"/>. A negative cost
        /// counts as zero.
        /// </summary>
        public static TimeSpan Next(TimeSpan least, TimeSpan lastCost)
        {
            TimeSpan paced = lastCost <= TimeSpan.Zero ? TimeSpan.Zero
                : lastCost.Ticks >= Longest.Ticks / Share ? Longest   // compared before it is multiplied: a huge cost cannot overflow
                : TimeSpan.FromTicks(lastCost.Ticks * Share);
            return paced > least ? paced : least;
        }

        /// <summary>
        /// What a view's <c>PointerHeld</c> is unless a test replaces it: the left mouse button is
        /// down and the pointer is over <paramref name="answer"/> (a button inside it that is
        /// being pressed counts: it is part of the answer). The pointer's place is asked first, so
        /// an answer the pointer is not over never reads the button.
        /// </summary>
        public static bool HeldOver(UIElement answer) => answer.IsMouseOver && Mouse.LeftButton == MouseButtonState.Pressed;
    }

    /// <summary>
    /// The timer one view of an answer redraws on, and what paces it: when the view last drew,
    /// and what that cost.
    ///
    /// <para>
    /// The cost of a redraw is the time from its start until the dispatcher reaches
    /// <see cref="DispatcherPriority.Loaded"/> after the document was shown. Building the
    /// document is only part of it: the layout of the new document runs after the redraw has
    /// returned, from the dispatcher at Render priority, which is above Loaded. So a callback
    /// posted at Loaded once the document is shown runs after that layout. The timer ticks at
    /// Background, below both: a tick never comes between a redraw and its cost.
    /// </para>
    ///
    /// <para>
    /// Nothing here keeps the view alive. The view owns this object, and this object holds the
    /// view weakly: a timer that runs is held by its dispatcher, and with it whatever its tick
    /// reaches, and the callback that stores a cost waits in the dispatcher's queue. A view
    /// that was dropped while either waits is still collected, and neither does anything for it
    /// afterwards. <typeparamref name="TView"/>'s tick is given as a static lambda for that
    /// reason: it must capture nothing.
    /// </para>
    /// </summary>
    internal sealed class RedrawTimer<TView> where TView : class
    {
        private readonly WeakReference<TView> _view;
        private readonly WeakReference<RedrawTimer<TView>> _self;
        private readonly Action<TView> _tick;
        private readonly DispatcherTimer _timer;
        private readonly Stopwatch _since = new();
        private TimeSpan _lastCost;

        /// <summary>The redraw whose cost is waited for; a callback for an older one stores nothing.</summary>
        private int _measured;
        private bool _pointerFailureSaid;

        /// <param name="view">The view that redraws; held weakly.</param>
        /// <param name="tick">Called with the view when the timer fires, the timer already stopped. A static lambda.</param>
        public RedrawTimer(TView view, Action<TView> tick)
        {
            _view = new WeakReference<TView>(view);
            _self = new WeakReference<RedrawTimer<TView>>(this);
            _tick = tick;
            _timer = new DispatcherTimer(DispatcherPriority.Background);
            _timer.Tick += OnTick;
        }

        /// <summary>
        /// What the last redraw cost, to the end of its layout; zero until one was measured and
        /// after <see cref="Forget"/>. Setting it (the tests do) also drops a cost still waited
        /// for, so the value set stays until the next redraw is measured.
        /// </summary>
        public TimeSpan LastCost
        {
            get => _lastCost;
            set
            {
                _measured++;
                _lastCost = value;
            }
        }

        /// <summary>True while a redraw waits for the timer.</summary>
        public bool Waiting => _timer.IsEnabled;

        /// <summary>How long the timer was set to wait, while a redraw waits for it; otherwise null.</summary>
        public TimeSpan? Pending => _timer.IsEnabled ? _timer.Interval : null;

        /// <summary>True once the view drew, since it was made or since <see cref="Forget"/>.</summary>
        public bool HasDrawn => _since.IsRunning;

        /// <summary>
        /// Whether a redraw of a streaming answer may be drawn now. It may not while the pace of
        /// the last redraw has not passed (<see cref="RedrawPace.Next"/>, from
        /// <paramref name="least"/> and <see cref="LastCost"/>), nor while the pointer is held:
        /// every redraw replaces the buttons in the answer, and a click that began on one would
        /// end on its replacement and be lost. When it may not, the timer is started: for what is
        /// left of the pace, or, under a held pointer, for <paramref name="least"/>. Asked when
        /// the timer fires and when new text finds no redraw waiting, so the cost it reads is
        /// the newest. A <paramref name="pointerHeld"/> that throws counts as not held, and is
        /// told to <paramref name="warn"/> once, by its type.
        /// </summary>
        public bool Ready(TimeSpan least, Func<bool> pointerHeld, Action<string> warn)
        {
            TimeSpan left = Left(least);
            if (left > TimeSpan.Zero)
            {
                Start(left);
                return false;
            }
            if (Held(pointerHeld, warn))
            {
                Start(least);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Asks for a redraw that the timer makes: when the pace of the last redraw has passed,
        /// and not at once even then. For a picture that arrived, and for Try again: whoever
        /// asks is in the middle of something (the end of a draw, a click), and may ask several
        /// times in a row. A redraw that waits already is the one that shows it.
        /// </summary>
        public void Ask(TimeSpan least)
        {
            if (_timer.IsEnabled) return;
            TimeSpan left = Left(least);
            Start(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        }

        /// <summary>What is left of the pace of the last redraw; nothing (or less) when it has passed, or when no redraw is there to pace against.</summary>
        private TimeSpan Left(TimeSpan least) =>
            _since.IsRunning ? RedrawPace.Next(least, _lastCost) - _since.Elapsed : TimeSpan.Zero;

        /// <summary>Nothing waits for the timer any more: the view draws now, which shows whatever a redraw was waiting to show.</summary>
        public void Stop() => _timer.Stop();

        /// <summary>
        /// The view goes away (released, unloaded): nothing waits for the timer, and the cost of
        /// a redraw not yet stored is never stored.
        /// </summary>
        public void Cancel()
        {
            _timer.Stop();
            _measured++;
        }

        /// <summary>
        /// The view shows another answer from now on, or none: as <see cref="Cancel"/>, and what
        /// the last answer's redraws cost says nothing about the next one's.
        /// </summary>
        public void Forget()
        {
            Cancel();
            _lastCost = TimeSpan.Zero;
            _since.Reset();
        }

        /// <summary>
        /// A redraw that began at <paramref name="started"/> (<see cref="Stopwatch.GetTimestamp"/>)
        /// has shown its document. The wait for the next one counts from here, and its cost is
        /// stored once the layout it caused is done, unless another redraw, <see cref="Cancel"/>
        /// or <see cref="Forget"/> came first, or the view is gone by then.
        /// </summary>
        public void Measure(long started)
        {
            _since.Restart();
            Post(_timer.Dispatcher, _self, ++_measured, started);
        }

        /// <summary>Static, so the callback that waits in the dispatcher's queue holds this object only weakly, and the view not at all.</summary>
        private static void Post(Dispatcher dispatcher, WeakReference<RedrawTimer<TView>> weak, int measured, long started)
        {
            dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (weak.TryGetTarget(out RedrawTimer<TView>? pace) && pace._measured == measured && pace._view.TryGetTarget(out _))
                    pace._lastCost = Stopwatch.GetElapsedTime(started);
            }));
        }

        private void Start(TimeSpan wait)
        {
            _timer.Interval = wait;
            _timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            _timer.Stop();   // one tick for one wait; whoever still has to wait starts it again
            if (_view.TryGetTarget(out TView? view)) _tick(view);
        }

        private bool Held(Func<bool> pointerHeld, Action<string> warn)
        {
            try
            {
                return pointerHeld();
            }
            catch (Exception ex)
            {
                if (!_pointerFailureSaid)
                {
                    _pointerFailureSaid = true;
                    try
                    {
                        warn("Asking whether the pointer is held over an answer failed (" + ex.GetType().Name + "); it counts as not held");
                    }
                    catch (Exception)
                    {
                        // Logging is best effort; it must not throw into a timer tick either.
                    }
                }
                return false;
            }
        }
    }
}
