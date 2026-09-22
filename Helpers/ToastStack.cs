using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Animation;

namespace Kil0bitSystemMonitor.Helpers
{
    /// <summary>
    /// The bottom-right corner, and who is currently in it.
    ///
    /// <para>
    /// One registry for every kind of notice, because the corner is one place. Each toast class
    /// keeping its own list would have each class laying cards out from a list that cannot see
    /// the others, and two cards would occupy the same rectangle — which is exactly what
    /// happens on a struggling machine, where an alert and a runaway-search notice fire
    /// together.
    /// </para>
    ///
    /// <para>
    /// Trimming is per type: a burst of alerts must not evict a notice of a different kind that
    /// the user has not answered yet.
    /// </para>
    /// </summary>
    public static class ToastStack
    {
        private const double CardMargin = 18;
        private const double CardGap = 8;

        /// <summary>Every notice currently on screen, newest last.</summary>
        private static readonly List<Window> Open = new();

        /// <summary>
        /// Registers a notice and drops the oldest of its own type once there are more than
        /// <paramref name="maxOfSameType"/> of them.
        ///
        /// <para>
        /// The oldest goes rather than the newest: the most recent problem is the one the user
        /// has not seen yet.
        /// </para>
        /// </summary>
        public static void Add(Window toast, int maxOfSameType)
        {
            if (toast == null) return;

            Type kind = toast.GetType();
            while (CountOf(kind) >= maxOfSameType)
            {
                Window? oldest = OldestOf(kind);
                if (oldest == null) break;

                Open.Remove(oldest);
                try { oldest.Close(); } catch { }
            }

            Open.Add(toast);
        }

        /// <summary>Drops a notice that has closed, and closes the gap it left.</summary>
        public static void Remove(Window toast)
        {
            if (toast != null && Open.Remove(toast)) Restack();
        }

        /// <summary>
        /// Lays the open notices up from the bottom-right corner. Re-run whenever one appears or
        /// closes, so a gap never opens in the middle of the stack.
        /// </summary>
        public static void Restack()
        {
            var work = SystemParameters.WorkArea;
            double bottom = work.Bottom - CardMargin;

            for (int i = Open.Count - 1; i >= 0; i--)
            {
                Window toast = Open[i];
                if (!toast.IsLoaded) continue;

                toast.Left = work.Right - toast.ActualWidth - CardMargin;
                toast.Top = bottom - toast.ActualHeight;
                bottom -= toast.ActualHeight + CardGap;
            }
        }

        /// <summary>Fades and lifts a card into place.</summary>
        public static void PlayEntrance(Window toast)
        {
            if (toast == null) return;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            toast.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            toast.BeginAnimation(Window.TopProperty,
                new DoubleAnimation(toast.Top + 16, toast.Top, TimeSpan.FromMilliseconds(260))
                { EasingFunction = ease });
        }

        /// <summary>
        /// Closes every notice of one kind, leaving the others alone — switching off alerts must
        /// not silently take away an unanswered notice about something else.
        /// </summary>
        public static void CloseAll<T>() where T : Window
        {
            for (int i = Open.Count - 1; i >= 0; i--)
            {
                if (Open[i] is not T toast) continue;

                Open.RemoveAt(i);
                try { toast.Close(); } catch { }
            }
            Restack();
        }

        private static int CountOf(Type kind)
        {
            int n = 0;
            foreach (Window toast in Open) if (toast.GetType() == kind) n++;
            return n;
        }

        private static Window? OldestOf(Type kind)
        {
            foreach (Window toast in Open) if (toast.GetType() == kind) return toast;
            return null;
        }
    }
}
