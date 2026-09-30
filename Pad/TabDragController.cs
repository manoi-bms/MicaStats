using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Pad;

using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Drag to reorder tabs (spec 4.2). A press on a tab arms it; moving past the system drag
    /// threshold starts the drag, and from then on the tab moves live to where the pointer is (the
    /// others make room); release ends it and saves once. The strip scrolls near its edges.
    /// Positions are in the strip's coordinates.
    /// </summary>
    internal sealed class TabDragController
    {
        private const double EdgeScroll = 24;

        private readonly ItemsControl _strip;
        private readonly ScrollViewer _scroller;
        private readonly Func<IReadOnlyList<OpenNote>> _tabs;
        private readonly Action<OpenNote, int> _move;
        private readonly Action _dropped;
        private OpenNote? _pressed;
        private Point _pressAt;

        public TabDragController(ItemsControl strip, ScrollViewer scroller, Func<IReadOnlyList<OpenNote>> tabs,
                                 Action<OpenNote, int> move, Action dropped)
        {
            _strip = strip;
            _scroller = scroller;
            _tabs = tabs;
            _move = move;
            _dropped = dropped;
            strip.PreviewMouseMove += OnPreviewMouseMove;
            strip.PreviewMouseLeftButtonUp += (s, e) => { if (IsDragging) e.Handled = true; Release(); };
            strip.LostMouseCapture += (s, e) => Release();
        }

        internal bool IsDragging { get; private set; }

        /// <summary>A left press on a tab (the window calls this from the tab's mouse-down).</summary>
        internal void Press(OpenNote note, Point at)
        {
            _pressed = note;
            _pressAt = at;
            IsDragging = false;
        }

        /// <summary>The pointer moved with the button down. Returns true while dragging.</summary>
        internal bool MoveTo(Point at, IReadOnlyList<(double Left, double Width)> layout)
        {
            if (_pressed == null) return false;
            if (!IsDragging)
            {
                if (!TabDrop.PastThreshold(at.X - _pressAt.X, at.Y - _pressAt.Y,
                        SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance))
                    return false;
                IsDragging = true;
            }

            var tabs = _tabs();
            int dragged = IndexOf(tabs, _pressed);
            if (dragged < 0) { Release(); return false; }
            int target = TabDrop.TargetIndex(at.X, layout, dragged);
            if (target != dragged) _move(_pressed, target);
            return true;
        }

        /// <summary>The button went up (or capture was lost): ends a drag and saves the order once.</summary>
        internal void Release()
        {
            bool wasDragging = IsDragging;
            _pressed = null;
            IsDragging = false;
            if (Mouse.Captured == _strip) _strip.ReleaseMouseCapture();
            if (wasDragging) _dropped();
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_pressed == null || e.LeftButton != MouseButtonState.Pressed) return;
            bool wasDragging = IsDragging;
            if (!MoveTo(e.GetPosition(_strip), Layout())) return;
            if (!wasDragging) _strip.CaptureMouse();

            double x = e.GetPosition(_scroller).X;
            if (x < EdgeScroll) _scroller.LineLeft();
            else if (x > _scroller.ActualWidth - EdgeScroll) _scroller.LineRight();
            e.Handled = true;
        }

        /// <summary>Each tab's left edge and width in the strip, in tab order.</summary>
        private IReadOnlyList<(double Left, double Width)> Layout()
        {
            var tabs = _tabs();
            var layout = new List<(double, double)>(tabs.Count);
            foreach (var note in tabs)
            {
                if (_strip.ItemContainerGenerator.ContainerFromItem(note) is FrameworkElement container && container.IsVisible)
                {
                    var left = container.TranslatePoint(new Point(0, 0), _strip).X;
                    layout.Add((left, container.ActualWidth));
                }
                else
                {
                    layout.Add((double.MaxValue / 4, 0));
                }
            }
            return layout;
        }

        private static int IndexOf(IReadOnlyList<OpenNote> tabs, OpenNote note)
        {
            for (int i = 0; i < tabs.Count; i++) if (ReferenceEquals(tabs[i], note)) return i;
            return -1;
        }
    }
}
