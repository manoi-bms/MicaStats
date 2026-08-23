using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Kil0bitSystemMonitor.Capture;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Capture;

using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

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
                Canvas.ZoomToFit(new Size(Scroller.ViewportWidth - 40, Scroller.ViewportHeight - 40));
                UpdateStatus();
            };

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
            string zoom = $"{Canvas.Zoom * 100:0}%";
            StatusText.Text = _lastSavedPath != null
                ? $"{size}  ·  {marks}  ·  {zoom}  ·  saved to {_lastSavedPath}"
                : $"{size}  ·  {marks}  ·  {zoom}";
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
                    case Key.D0: Canvas.SetZoom(1); UpdateStatus(); e.Handled = true; return;
                    case Key.OemPlus: Canvas.SetZoom(Canvas.Zoom * 1.25); UpdateStatus(); e.Handled = true; return;
                    case Key.OemMinus: Canvas.SetZoom(Canvas.Zoom / 1.25); UpdateStatus(); e.Handled = true; return;
                }
                return;
            }

            switch (e.Key)
            {
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
                case Key.Escape: Close(); break;
                default: return;
            }
            e.Handled = true;
        }
    }
}
