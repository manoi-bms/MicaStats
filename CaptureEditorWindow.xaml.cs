using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Kil0bitSystemMonitor.Capture;
using Kil0bitSystemMonitor.Controls;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Capture;

using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;

namespace Kil0bitSystemMonitor
{
    /// <summary>
    /// The annotation editor shown after a capture: mark up, redact, crop, then copy, save or
    /// pin.
    ///
    /// <para>
    /// The window owns no image state of its own — everything lives in the canvas's
    /// <see cref="AnnotationDocument"/>, and every action here is a thin call onto it. That keeps
    /// undo/redo correct by construction: there is exactly one place where state changes.
    /// </para>
    /// </summary>
    public partial class CaptureEditorWindow : Window
    {
        private readonly CaptureSettings _settings;
        private ImgPoint _textAt;
        private string? _lastSavedPath;

        public CaptureEditorWindow(BitmapSource image, CaptureSettings settings, string? sourceNote = null)
        {
            InitializeComponent();
            _settings = settings ?? CaptureSettings.Defaults;

            Canvas.Load(image);
            Canvas.DocumentChanged += UpdateStatus;
            Canvas.SelectionChanged += UpdateStatus;
            Canvas.TextRequested += BeginTextEntry;
            Canvas.RedactStyle = _settings.RedactStyle;

            SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                try
                {
                    int dark = 1;
                    Win32Helper.DwmSetWindowAttribute(hwnd, Win32Helper.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                }
                catch { }
            };

            // Fit on first layout, once the viewport actually has a size.
            ContentRendered += (s, e) =>
            {
                ZoomFit();
                UpdateStatus();
            };

            // The percent counts screen pixels, so it changes when the window moves to a display
            // with another scaling even though the canvas's own zoom does not.
            DpiChanged += (s, e) => ShowZoom(e.NewDpi.DpiScaleX);
            ShowZoom(DpiScale);

            PreviewKeyDown += OnWindowKey;
            if (!string.IsNullOrEmpty(sourceNote)) Title = "MicaStats Capture — " + sourceNote;
        }

        /// <summary>The finished image, as it would be saved.</summary>
        public BitmapSource? Result => Canvas.Export();

        private void UpdateStatus()
        {
            var doc = Canvas.Document;
            if (doc == null) return;

            UndoButton.IsEnabled = doc.CanUndo;
            RedoButton.IsEnabled = doc.CanRedo;
            ApplyCropButton.IsEnabled = !Canvas.PendingCrop.IsEmpty;

            string size = $"{doc.Crop.Width} x {doc.Crop.Height}";
            string marks = doc.Items.Count == 1 ? "1 mark" : $"{doc.Items.Count} marks";

            string hint = Canvas.Selected != null
                ? "  ·  drag to move, handles to resize, Del to remove"
                : doc.Items.Count > 0 && Canvas.Tool == CaptureTool.Select
                    ? "  ·  click a mark to select it"
                    : "";

            StatusText.Text = _lastSavedPath != null
                ? $"{size}  ·  {marks}  ·  saved to {_lastSavedPath}"
                : $"{size}  ·  {marks}{hint}";
        }

        // ----- Toolbar -----------------------------------------------------------------------

        private void OnToolChecked(object sender, RoutedEventArgs e)
        {
            if (Canvas == null) return;
            Canvas.Tool =
                ReferenceEquals(sender, ToolRect) ? CaptureTool.Rectangle :
                ReferenceEquals(sender, ToolEllipse) ? CaptureTool.Ellipse :
                ReferenceEquals(sender, ToolLine) ? CaptureTool.Line :
                ReferenceEquals(sender, ToolPen) ? CaptureTool.Pen :
                ReferenceEquals(sender, ToolHighlight) ? CaptureTool.Highlighter :
                ReferenceEquals(sender, ToolText) ? CaptureTool.Text :
                ReferenceEquals(sender, ToolStep) ? CaptureTool.Step :
                ReferenceEquals(sender, ToolRedact) ? CaptureTool.Redact :
                ReferenceEquals(sender, ToolCrop) ? CaptureTool.Crop :
                ReferenceEquals(sender, ToolSelect) ? CaptureTool.Select :
                CaptureTool.Arrow;
        }

        private void OnColourChecked(object sender, RoutedEventArgs e)
        {
            if (Canvas != null && (sender as FrameworkElement)?.Tag is string hex) Canvas.ColorHex = hex;
        }

        private void OnThicknessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (Canvas != null) Canvas.Thickness = e.NewValue;
        }

        private void OnRedactStyleChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Canvas == null) return;
            Canvas.RedactStyle = RedactStyleBox.SelectedIndex switch
            {
                1 => RedactStyle.Blur,
                2 => RedactStyle.Solid,
                _ => RedactStyle.Pixelate,
            };
        }

        // ----- Text entry --------------------------------------------------------------------

        private void BeginTextEntry(ImgPoint at, Point dip)
        {
            _textAt = at;
            var origin = Canvas.TranslatePoint(dip, TextLayer);
            System.Windows.Controls.Canvas.SetLeft(TextEntry, origin.X);
            System.Windows.Controls.Canvas.SetTop(TextEntry, origin.Y);
            TextEntry.Text = "";
            TextEntry.Visibility = Visibility.Visible;
            TextEntry.Focus();
        }

        private void OnTextEntryKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitTextEntry();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                TextEntry.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
        }

        private void OnTextEntryLostFocus(object sender, RoutedEventArgs e) => CommitTextEntry();

        private void CommitTextEntry()
        {
            if (TextEntry.Visibility != Visibility.Visible) return;
            TextEntry.Visibility = Visibility.Collapsed;
            Canvas.CommitText(_textAt, TextEntry.Text);
        }

        // ----- Actions -----------------------------------------------------------------------

        private void OnUndo(object sender, RoutedEventArgs e) => Canvas.Undo();
        private void OnRedo(object sender, RoutedEventArgs e) => Canvas.Redo();
        private void OnClear(object sender, RoutedEventArgs e) => Canvas.ClearAll();
        private void OnApplyCrop(object sender, RoutedEventArgs e) => Canvas.ApplyPendingCrop();
        private void OnResetCrop(object sender, RoutedEventArgs e) => Canvas.ResetCrop();

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            var img = Canvas.Export();
            if (img == null) return;
            StatusText.Text = ScreenCaptureEngine.CopyToClipboard(img)
                ? "Copied to clipboard"
                : "Clipboard is busy — try again";
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            var img = Canvas.Export();
            if (img == null) return;
            try
            {
                string dir = _settings.Folder;
                Directory.CreateDirectory(dir);
                string name = CaptureFileNamer.Format(_settings.NameTemplate, DateTime.Now,
                    "capture", img.PixelWidth, img.PixelHeight);
                string path = CaptureFileNamer.UniquePath(dir, name, _settings.Format, File.Exists);

                ScreenCaptureEngine.Save(img, path, _settings.Format, _settings.JpegQuality);
                _lastSavedPath = path;
                DiagnosticsLog.Log("capture", $"Saved {img.PixelWidth}x{img.PixelHeight} to {path}");
                UpdateStatus();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("capture", "Save failed", ex);
                StatusText.Text = "Could not save — see the diagnostics log";
            }
        }

        private void OnSaveAs(object sender, RoutedEventArgs e)
        {
            var img = Canvas.Export();
            if (img == null) return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
                FilterIndex = _settings.Format == CaptureFormat.Jpeg ? 2 : 1,
                InitialDirectory = Directory.Exists(_settings.Folder) ? _settings.Folder : "",
                FileName = CaptureFileNamer.Format(_settings.NameTemplate, DateTime.Now,
                    "capture", img.PixelWidth, img.PixelHeight),
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                var format = dialog.FilterIndex == 2 ? CaptureFormat.Jpeg : CaptureFormat.Png;
                ScreenCaptureEngine.Save(img, dialog.FileName, format, _settings.JpegQuality);
                _lastSavedPath = dialog.FileName;
                UpdateStatus();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("capture", "Save-as failed", ex);
                StatusText.Text = "Could not save — see the diagnostics log";
            }
        }

        private void OnPin(object sender, RoutedEventArgs e)
        {
            var img = Canvas.Export();
            if (img == null) return;
            PinnedCaptureWindow.Pin(img);
        }

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(_settings.Folder);
                if (_lastSavedPath != null && File.Exists(_lastSavedPath))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastSavedPath}\"") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo(_settings.Folder) { UseShellExecute = true });
            }
            catch { }
        }

        // ----- Zoom --------------------------------------------------------------------------

        // The arithmetic is EditorZoom's and the scrolling is CanvasZoom's; this only says which
        // gesture means what.

        /// <summary>
        /// Screen pixels per device-independent unit where the canvas is shown. A capture is made
        /// of screen pixels, so this turns the canvas's zoom into the percent on the zoom bar:
        /// 100% is one image pixel per screen pixel.
        /// </summary>
        private double DpiScale => System.Windows.Media.VisualTreeHelper.GetDpi(Canvas).DpiScaleX;

        private void ShowZoom(double dpiScale)
            => ZoomText.Text = EditorZoom.Percent(Canvas.Zoom, dpiScale).ToString(CultureInfo.InvariantCulture) + "%";

        private void ZoomTo(double zoom, Point anchorInScroller)
        {
            CanvasZoom.ZoomAt(Scroller, Canvas, zoom, anchorInScroller);
            ShowZoom(DpiScale);
        }

        /// <summary>Steps in (positive) or out (negative) around the middle of the view.</summary>
        private void ZoomStep(int steps)
            => ZoomTo(EditorZoom.Stepped(Canvas.Zoom, steps, DpiScale), CanvasZoom.ViewportCentre(Scroller));

        private void ZoomActualSize()
            => ZoomTo(EditorZoom.ActualSize(DpiScale), CanvasZoom.ViewportCentre(Scroller));

        /// <summary>Fits the crop in the view with a little room around it, never past actual size.</summary>
        private void ZoomFit()
        {
            var crop = Canvas.Document?.Crop ?? default;
            double fit = EditorZoom.Fit(Canvas.Zoom, crop.Width, crop.Height,
                Scroller.ViewportWidth - 40, Scroller.ViewportHeight - 40, DpiScale);
            ZoomTo(fit, CanvasZoom.ViewportCentre(Scroller));
        }

        /// <summary>
        /// Ctrl+wheel zooms around the pointer. Returns false when the wheel is not a zoom: with
        /// no Ctrl it scrolls as usual, and while a text annotation is being typed its box sits
        /// at a fixed place over the canvas, so the image must not move under it.
        /// </summary>
        internal bool WheelZoom(ModifierKeys modifiers, int delta, Point atInScroller)
        {
            if ((modifiers & ModifierKeys.Control) == 0) return false;
            if (TextEntry.Visibility == Visibility.Visible) return false;

            ZoomTo(EditorZoom.Wheel(Canvas.Zoom, delta, DpiScale), atInScroller);
            return true;
        }

        private void OnScrollerWheel(object sender, MouseWheelEventArgs e)
        {
            if (WheelZoom(Keyboard.Modifiers, e.Delta, e.GetPosition(Scroller))) e.Handled = true;
        }

        private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomStep(-1);
        private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomStep(+1);
        private void OnZoomActual(object sender, RoutedEventArgs e) => ZoomActualSize();
        private void OnZoomFit(object sender, RoutedEventArgs e) => ZoomFit();

        // ----- Keyboard ----------------------------------------------------------------------

        private void OnWindowKey(object sender, KeyEventArgs e)
        {
            // Never steal keys while the user is typing a text annotation.
            if (TextEntry.Visibility == Visibility.Visible) return;

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl)
            {
                switch (e.Key)
                {
                    case Key.Z: Canvas.Undo(); e.Handled = true; return;
                    case Key.Y: Canvas.Redo(); e.Handled = true; return;
                    case Key.C: OnCopy(sender, e); e.Handled = true; return;
                    case Key.S: OnSave(sender, e); e.Handled = true; return;

                    // Zoom, around the middle of the view. Shift is not looked at, so Ctrl and
                    // "+" (Shift and "=" on a US layout) zooms in like Ctrl and "=" does.
                    case Key.OemPlus:
                    case Key.Add: ZoomStep(+1); e.Handled = true; return;
                    case Key.OemMinus:
                    case Key.Subtract: ZoomStep(-1); e.Handled = true; return;
                    case Key.D0:
                    case Key.NumPad0: ZoomActualSize(); e.Handled = true; return;
                }
                return;
            }

            // Editing the selected mark takes priority over tool shortcuts.
            if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                if (Canvas.DeleteSelected()) e.Handled = true;
                return;
            }

            if (Canvas.Selected != null &&
                e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            {
                int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                double dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
                double dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
                if (Canvas.NudgeSelected(dx, dy)) e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.V: ToolSelect.IsChecked = true; break;
                case Key.A: ToolArrow.IsChecked = true; break;
                case Key.R: ToolRect.IsChecked = true; break;
                case Key.E: ToolEllipse.IsChecked = true; break;
                case Key.L: ToolLine.IsChecked = true; break;
                case Key.P: ToolPen.IsChecked = true; break;
                case Key.H: ToolHighlight.IsChecked = true; break;
                case Key.T: ToolText.IsChecked = true; break;
                case Key.N: ToolStep.IsChecked = true; break;
                case Key.B: ToolRedact.IsChecked = true; break;
                case Key.C: ToolCrop.IsChecked = true; break;
                case Key.Enter: Canvas.ApplyPendingCrop(); break;

                // Escape steps back one level: drop the selection first, close only when
                // nothing is selected. Closing out from under a selection loses the capture.
                case Key.Escape:
                    if (Canvas.Selected != null) Canvas.ClearSelection();
                    else Close();
                    break;
                default: return;
            }
            e.Handled = true;
        }
    }
}
