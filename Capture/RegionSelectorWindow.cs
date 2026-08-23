using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Capture;

// System.Drawing and System.Windows.Forms are in global scope (UseWindowsForms + ImplicitUsings),
// and both define these names. Bind them to the WPF types, as StatsPanelWindow.xaml.cs does.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using FlowDirection = System.Windows.FlowDirection;

namespace Kil0bitSystemMonitor.Capture
{
    /// <summary>What the user picked in the selector.</summary>
    public sealed record SelectionResult(PixelRect Region, BitmapSource FrozenDesktop, PixelRect DesktopBounds);

    /// <summary>
    /// The full-screen region picker.
    ///
    /// <para>
    /// It works on a <b>frozen frame</b>: the desktop is captured first, then displayed inside a
    /// borderless window covering every monitor, and the user selects on that still image. This
    /// is what Snipping Tool and ShareX do, for three reasons that all matter here. Selection
    /// maths happens in image pixels, so a mixed-DPI desktop cannot make the picked rectangle
    /// drift. The magnifier samples an in-memory bitmap instead of hammering the screen DC. And
    /// what the user sees while dragging is exactly what gets saved — menus and tooltips stay
    /// open in the frozen frame instead of vanishing the moment the overlay takes focus.
    /// </para>
    /// </summary>
    public sealed class RegionSelectorWindow : Window
    {
        private readonly Surface _surface;

        private RegionSelectorWindow(BitmapSource frozen, PixelRect bounds,
                                     IReadOnlyList<CaptureTarget> windows,
                                     IReadOnlyList<MonitorInfo> monitors)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.Black;
            Cursor = Cursors.Cross;
            // AllowsTransparency stays off: the window shows an opaque frozen screenshot, and a
            // layered window would cost a full-desktop alpha blend for no visual gain.

            _surface = new Surface(frozen, bounds, windows, monitors);
            Content = _surface;

            SourceInitialized += (s, e) => PositionOverDesktop(bounds);
            Loaded += (s, e) => { Activate(); Keyboard.Focus(_surface); };

            _surface.Finished += ok =>
            {
                try { DialogResult = ok; } catch { }
                Close();
            };
        }

        /// <summary>The chosen rectangle in screen pixels, valid when the dialog returned true.</summary>
        public PixelRect Region => _surface.Selection;

        private void PositionOverDesktop(PixelRect bounds)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || bounds.IsEmpty) return;
            // Physical pixels: Left/Top would be scaled by WPF using one monitor's factor, which
            // is the wrong factor for a window spanning monitors of different DPI.
            SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                SWP_NOZORDER | SWP_NOACTIVATE);
        }

        /// <summary>
        /// Freezes the desktop, runs the picker, and returns the selection. Returns null when
        /// the user cancels. Must be called on the UI thread.
        /// </summary>
        public static SelectionResult? Pick(bool includeCursor, IntPtr excludeWindow = default)
        {
            var bounds = ScreenCaptureEngine.VirtualBounds();
            if (bounds.IsEmpty) return null;

            var frozen = ScreenCaptureEngine.CaptureRectSource(bounds, includeCursor);
            if (frozen == null)
            {
                DiagnosticsLog.Error("capture", "Could not freeze the desktop for region selection");
                return null;
            }

            var windows = ScreenCaptureEngine.EnumerateWindows(bounds, excludeWindow);
            var monitors = ScreenCaptureEngine.GetMonitors();

            var win = new RegionSelectorWindow(frozen, bounds, windows, monitors);
            bool? ok = win.ShowDialog();
            if (ok != true || win.Region.IsEmpty) return null;

            return new SelectionResult(win.Region, frozen, bounds);
        }

        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        /// <summary>
        /// The drawing surface. Everything — dimming, selection, handles, hover highlight,
        /// loupe, hints — is painted in one <see cref="OnRender"/> pass, which keeps the frozen
        /// screenshot and its decorations perfectly in sync while dragging.
        /// </summary>
        private sealed class Surface : FrameworkElement
        {
            private readonly BitmapSource _image;
            private readonly PixelRect _bounds;
            private readonly IReadOnlyList<CaptureTarget> _windows;
            private readonly IReadOnlyList<MonitorInfo> _monitors;
            private readonly List<PixelRect> _guides = new();

            private bool _dragging;
            private int _anchorX, _anchorY;
            private int _cursorX, _cursorY;
            private PixelRect _selection;
            private PixelRect _hover;
            private bool _magnifier = true;
            private bool _snap = true;

            private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x88, 0x08, 0x08, 0x0C)));
            private static readonly Brush AccentBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x3F, 0xD2, 0xE4)));
            private static readonly Brush PlateBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xE8, 0x14, 0x14, 0x1C)));
            private static readonly Brush HoverBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x3F, 0xD2, 0xE4)));
            private static readonly Pen AccentPen = Frozen(new Pen(AccentBrush, 1.6));
            private static readonly Pen HoverPen = Frozen(new Pen(AccentBrush, 1.2) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) });
            private static readonly Pen LoupePen = Frozen(new Pen(AccentBrush, 2));
            private static readonly Pen CrossPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0x45, 0x3A))), 1));
            private static readonly Pen GridPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF))), 0.6));

            /// <summary>Raised with true to accept the selection, false to cancel.</summary>
            public event Action<bool>? Finished;

            public PixelRect Selection => _selection;

            public Surface(BitmapSource image, PixelRect bounds,
                           IReadOnlyList<CaptureTarget> windows, IReadOnlyList<MonitorInfo> monitors)
            {
                _image = image;
                _bounds = bounds;
                _windows = windows;
                _monitors = monitors;
                Focusable = true;

                foreach (var m in monitors) _guides.Add(m.Bounds);
                foreach (var w in windows) _guides.Add(w.Bounds);
            }

            /// <summary>
            /// Image pixels per device-independent unit, derived from the rendered size rather
            /// than from a DPI API. Self-correcting: whatever WPF decides this window's scale is,
            /// the mapping between the bitmap and the screen stays exact.
            /// </summary>
            private double Scale => ActualWidth > 0 ? _image.PixelWidth / ActualWidth : 1.0;

            private (int X, int Y) ToScreen(Point dip) =>
                ((int)Math.Round(dip.X * Scale) + _bounds.X, (int)Math.Round(dip.Y * Scale) + _bounds.Y);

            private Rect ToDip(PixelRect r)
            {
                double s = Scale;
                if (s <= 0) s = 1;
                return new Rect((r.X - _bounds.X) / s, (r.Y - _bounds.Y) / s, r.Width / s, r.Height / s);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                var (sx, sy) = ToScreen(e.GetPosition(this));
                _cursorX = sx; _cursorY = sy;

                if (_dragging)
                {
                    var raw = PixelRect.FromPoints(_anchorX, _anchorY, sx, sy);
                    _selection = _snap ? CaptureGeometry.SnapToGuides(raw, _guides, SnapThreshold) : raw;
                    _selection = CaptureGeometry.Clamp(_selection, _bounds);
                }
                else
                {
                    // Not dragging: offer whatever is under the cursor as a one-click target.
                    var target = CaptureGeometry.WindowAt(_windows, sx, sy);
                    _hover = target?.Bounds ?? CaptureGeometry.MonitorAt(_monitors, sx, sy)?.Bounds ?? default;
                    if (!_hover.IsEmpty) _hover = _hover.Intersect(_bounds);
                }
                InvalidateVisual();
                base.OnMouseMove(e);
            }

            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
            {
                var (sx, sy) = ToScreen(e.GetPosition(this));
                _dragging = true;
                _anchorX = sx; _anchorY = sy;
                _selection = default;
                CaptureMouse();
                InvalidateVisual();
                base.OnMouseLeftButtonDown(e);
            }

            protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
            {
                if (!_dragging) { base.OnMouseLeftButtonUp(e); return; }
                _dragging = false;
                ReleaseMouseCapture();

                // A click without a meaningful drag means "take the thing under the cursor".
                if (_selection.Width < 4 || _selection.Height < 4)
                    _selection = _hover;

                if (!_selection.IsEmpty) Finished?.Invoke(true);
                else InvalidateVisual();

                base.OnMouseLeftButtonUp(e);
            }

            /// <summary>Right-click abandons the capture, matching every other snipping tool.</summary>
            protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
            {
                Finished?.Invoke(false);
                base.OnMouseRightButtonDown(e);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                switch (e.Key)
                {
                    case Key.Escape:
                        Finished?.Invoke(false);
                        e.Handled = true;
                        return;

                    case Key.Enter:
                        if (_selection.IsEmpty) _selection = _hover;
                        if (!_selection.IsEmpty) Finished?.Invoke(true);
                        e.Handled = true;
                        return;

                    case Key.M:
                        _magnifier = !_magnifier;
                        break;

                    case Key.S:
                        _snap = !_snap;
                        break;

                    case Key.A:
                        _selection = _bounds;   // whole desktop
                        break;

                    case Key.Left:
                    case Key.Right:
                    case Key.Up:
                    case Key.Down:
                    {
                        int dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
                        int dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
                        if (_selection.IsEmpty)
                        {
                            _cursorX += dx; _cursorY += dy;
                        }
                        else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                        {
                            // Ctrl resizes from the bottom-right; plain arrows move the whole box.
                            _selection = CaptureGeometry.ResizeBy(_selection, ResizeHandle.BottomRight, dx, dy);
                        }
                        else
                        {
                            _selection = _selection.Offset(dx, dy);
                        }
                        _selection = CaptureGeometry.Clamp(_selection, _bounds);
                        break;
                    }
                }
                InvalidateVisual();
                base.OnKeyDown(e);
            }

            protected override void OnRender(DrawingContext dc)
            {
                var full = new Rect(0, 0, ActualWidth, ActualHeight);
                dc.DrawImage(_image, full);

                var active = !_selection.IsEmpty ? _selection : (!_dragging ? _hover : default);

                // Dim everything except the active area, drawn as four bands so the selection
                // itself keeps the untouched pixels of the frozen frame.
                if (active.IsEmpty)
                {
                    dc.DrawRectangle(DimBrush, null, full);
                }
                else
                {
                    var a = ToDip(active);
                    dc.DrawRectangle(DimBrush, null, new Rect(0, 0, ActualWidth, Math.Max(0, a.Top)));
                    dc.DrawRectangle(DimBrush, null, new Rect(0, a.Bottom, ActualWidth, Math.Max(0, ActualHeight - a.Bottom)));
                    dc.DrawRectangle(DimBrush, null, new Rect(0, a.Top, Math.Max(0, a.Left), a.Height));
                    dc.DrawRectangle(DimBrush, null, new Rect(a.Right, a.Top, Math.Max(0, ActualWidth - a.Right), a.Height));

                    if (!_selection.IsEmpty)
                    {
                        dc.DrawRectangle(null, AccentPen, a);
                        DrawHandles(dc, a);
                    }
                    else
                    {
                        dc.DrawRectangle(HoverBrush, HoverPen, a);
                    }
                }

                DrawReadout(dc, active);
                if (_magnifier) DrawMagnifier(dc);
                DrawHint(dc);
            }

            private void DrawHandles(DrawingContext dc, Rect a)
            {
                const double r = 3.5;
                var points = new[]
                {
                    new Point(a.Left, a.Top), new Point(a.Left + a.Width / 2, a.Top), new Point(a.Right, a.Top),
                    new Point(a.Right, a.Top + a.Height / 2), new Point(a.Right, a.Bottom),
                    new Point(a.Left + a.Width / 2, a.Bottom), new Point(a.Left, a.Bottom),
                    new Point(a.Left, a.Top + a.Height / 2),
                };
                foreach (var p in points) dc.DrawEllipse(Brushes.White, AccentPen, p, r, r);
            }

            /// <summary>Live size and origin, parked beside the cursor and kept on screen.</summary>
            private void DrawReadout(DrawingContext dc, PixelRect active)
            {
                string text = active.IsEmpty
                    ? $"{_cursorX}, {_cursorY}"
                    : $"{active.Width} x {active.Height}   ({active.X}, {active.Y})";

                var ft = Text(text, 12, Colors.White);
                double w = ft.Width + 14, h = ft.Height + 8;
                var (px, py) = CaptureGeometry.PlaceNearCursor(_cursorX, _cursorY,
                    (int)(w * Scale), (int)(h * Scale), (int)(18 * Scale), _bounds);
                var at = ToDip(new PixelRect(px, py, (int)(w * Scale), (int)(h * Scale)));

                dc.DrawRoundedRectangle(PlateBrush, AccentPen, new Rect(at.X, at.Y, w, h), 5, 5);
                dc.DrawText(ft, new Point(at.X + 7, at.Y + 4));
            }

            /// <summary>
            /// The loupe: a zoomed crop of the frozen frame with a pixel grid, centre crosshair
            /// and the exact colour under the cursor. Because the source is the frozen bitmap,
            /// this costs nothing per frame beyond the blit.
            /// </summary>
            private void DrawMagnifier(DrawingContext dc)
            {
                const int SourcePx = 21;   // odd, so one pixel is dead centre
                const double SizeDip = 132;

                var src = CaptureGeometry.MagnifierSource(_cursorX, _cursorY, SourcePx, _bounds);
                if (src.IsEmpty) return;

                var (px, py) = CaptureGeometry.PlaceNearCursor(_cursorX, _cursorY,
                    (int)(SizeDip * Scale), (int)((SizeDip + 26) * Scale), (int)(26 * Scale), _bounds);
                var box = ToDip(new PixelRect(px, py, (int)(SizeDip * Scale), (int)(SizeDip * Scale)));
                var area = new Rect(box.X, box.Y, SizeDip, SizeDip);

                CroppedBitmap crop;
                try
                {
                    crop = new CroppedBitmap(_image,
                        new Int32Rect(src.X - _bounds.X, src.Y - _bounds.Y, src.Width, src.Height));
                    crop.Freeze();
                }
                catch { return; }

                dc.PushClip(new RectangleGeometry(area, 6, 6));
                // Nearest-neighbour is deliberate: the loupe is for judging individual pixels.
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
                dc.DrawImage(crop, area);

                double cell = SizeDip / SourcePx;
                for (int i = 1; i < SourcePx; i++)
                {
                    dc.DrawLine(GridPen, new Point(area.X + i * cell, area.Y), new Point(area.X + i * cell, area.Bottom));
                    dc.DrawLine(GridPen, new Point(area.X, area.Y + i * cell), new Point(area.Right, area.Y + i * cell));
                }

                int centre = SourcePx / 2;
                var hot = new Rect(area.X + centre * cell, area.Y + centre * cell, cell, cell);
                dc.DrawRectangle(null, CrossPen, hot);
                dc.Pop();

                dc.DrawRoundedRectangle(null, LoupePen, area, 6, 6);

                string hex = SampleHex(src.X - _bounds.X + centre, src.Y - _bounds.Y + centre);
                var label = Text($"{hex}   {_cursorX},{_cursorY}", 11, Colors.White);
                var plate = new Rect(area.X, area.Bottom + 4, Math.Max(area.Width, label.Width + 12), label.Height + 6);
                dc.DrawRoundedRectangle(PlateBrush, null, plate, 4, 4);
                dc.DrawText(label, new Point(plate.X + 6, plate.Y + 3));
            }

            /// <summary>Reads one pixel out of the frozen frame, as #RRGGBB.</summary>
            private string SampleHex(int bx, int by)
            {
                try
                {
                    if (bx < 0 || by < 0 || bx >= _image.PixelWidth || by >= _image.PixelHeight) return "#------";
                    var one = new CroppedBitmap(_image, new Int32Rect(bx, by, 1, 1));
                    var buf = new byte[4];
                    one.CopyPixels(buf, 4, 0);
                    return $"#{buf[2]:X2}{buf[1]:X2}{buf[0]:X2}";
                }
                catch { return "#------"; }
            }

            private void DrawHint(DrawingContext dc)
            {
                var ft = Text("Drag to select   ·   Click a window or screen   ·   M magnifier   ·   S snap   ·   A all   ·   Enter accept   ·   Esc cancel",
                    12, Color.FromRgb(0xDD, 0xE3, 0xEA));

                var monitor = CaptureGeometry.MonitorAt(_monitors, _cursorX, _cursorY);
                var host = monitor?.Bounds ?? _bounds;
                var hostDip = ToDip(host);

                double w = ft.Width + 20, h = ft.Height + 12;
                var plate = new Rect(hostDip.X + (hostDip.Width - w) / 2, hostDip.Y + 28, w, h);
                dc.DrawRoundedRectangle(PlateBrush, AccentPen, plate, 7, 7);
                dc.DrawText(ft, new Point(plate.X + 10, plate.Y + 6));
            }

            private static FormattedText Text(string text, double size, Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"),
                        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    size, brush, 96);
            }

            private const int SnapThreshold = 8;

            private static T Frozen<T>(T f) where T : Freezable
            {
                f.Freeze();
                return f;
            }
        }
    }
}
