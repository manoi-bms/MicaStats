using System;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Capture;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>
    /// Runs a capture end to end: optional delay, grab the pixels, then hand off to the editor
    /// or complete silently (copy / auto-save) according to settings.
    ///
    /// <para>
    /// One capture at a time. Two selector overlays fighting over the screen would be
    /// unrecoverable for the user, and a hotkey held down would otherwise start a new capture
    /// on every auto-repeat.
    /// </para>
    /// </summary>
    public static class CaptureService
    {
        private static bool _busy;

        /// <summary>Raised after a capture completes, with the saved path (null when not saved).</summary>
        public static event Action<string?>? Completed;

        public static bool IsBusy => _busy;

        /// <summary>Starts a capture on the UI thread. Safe to call from a hotkey handler.</summary>
        public static void Start(CaptureMode mode, AppConfig? config, Dispatcher dispatcher)
        {
            if (dispatcher == null) return;
            dispatcher.BeginInvoke(new Action(() => Run(mode, config)));
        }

        /// <summary>Runs a capture. Must be called on the UI thread.</summary>
        public static void Run(CaptureMode mode, AppConfig? config)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var settings = CaptureSettings.From(config);

                if (settings.DelaySeconds > 0) WaitPumping(settings.DelaySeconds);

                var (image, note) = Grab(mode, settings);
                if (image == null)
                {
                    // Cancelling the selector is a normal outcome, not a failure.
                    return;
                }

                DiagnosticsLog.Log("capture", $"{mode} capture {image.PixelWidth}x{image.PixelHeight}");

                if (settings.OpenEditor)
                {
                    var editor = new CaptureEditorWindow(image, settings, note);
                    editor.Show();
                    editor.Activate();
                }
                else
                {
                    Finish(image, settings);
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("capture", $"{mode} capture failed", ex);
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>
        /// Waits without freezing the UI. A plain Sleep would stop the message pump, so the
        /// menu the user is trying to photograph would never finish painting.
        /// </summary>
        private static void WaitPumping(int seconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromSeconds(seconds), DispatcherPriority.Background,
                (s, e) =>
                {
                    if (s is DispatcherTimer t) t.Stop();
                    frame.Continue = false;
                }, Dispatcher.CurrentDispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        private static (BitmapSource? Image, string? Note) Grab(CaptureMode mode, CaptureSettings settings)
        {
            switch (mode)
            {
                case CaptureMode.Region:
                {
                    var picked = RegionSelectorWindow.Pick(settings.IncludeCursor);
                    if (picked == null) return (null, null);

                    // Crop the frozen frame rather than re-reading the screen: the result is then
                    // exactly the pixels the user saw while selecting.
                    var r = picked.Region;
                    var b = picked.DesktopBounds;
                    var crop = new System.Windows.Int32Rect(r.X - b.X, r.Y - b.Y, r.Width, r.Height);
                    var cropped = new CroppedBitmap(picked.FrozenDesktop, crop);
                    cropped.Freeze();
                    return (cropped, $"{r.Width}x{r.Height}");
                }

                case CaptureMode.ActiveWindow:
                {
                    var hwnd = ScreenCaptureEngine.ForegroundWindow();
                    var rect = ScreenCaptureEngine.GetWindowBounds(hwnd);
                    if (rect.IsEmpty) return (null, null);
                    // Keep it on screen: a maximised window reports bounds slightly outside.
                    rect = rect.Intersect(ScreenCaptureEngine.VirtualBounds());
                    return (ScreenCaptureEngine.CaptureRectSource(rect, settings.IncludeCursor), "window");
                }

                case CaptureMode.Screen:
                {
                    var monitors = ScreenCaptureEngine.GetMonitors();
                    var pos = GetCursorScreenPos();
                    var monitor = CaptureGeometry.MonitorAt(monitors, pos.X, pos.Y);
                    var rect = monitor?.Bounds ?? ScreenCaptureEngine.VirtualBounds();
                    return (ScreenCaptureEngine.CaptureRectSource(rect, settings.IncludeCursor), "screen");
                }

                default:
                    return (ScreenCaptureEngine.CaptureRectSource(
                        ScreenCaptureEngine.VirtualBounds(), settings.IncludeCursor), "all screens");
            }
        }

        /// <summary>Copy and/or save without opening the editor.</summary>
        private static void Finish(BitmapSource image, CaptureSettings settings)
        {
            if (settings.CopyToClipboard) ScreenCaptureEngine.CopyToClipboard(image);

            string? saved = null;
            if (settings.AutoSave)
            {
                try
                {
                    Directory.CreateDirectory(settings.Folder);
                    string name = CaptureFileNamer.Format(settings.NameTemplate, DateTime.Now,
                        "capture", image.PixelWidth, image.PixelHeight);
                    saved = CaptureFileNamer.UniquePath(settings.Folder, name, settings.Format, File.Exists);
                    ScreenCaptureEngine.Save(image, saved, settings.Format, settings.JpegQuality);
                    DiagnosticsLog.Log("capture", "Saved to " + saved);
                }
                catch (Exception ex)
                {
                    saved = null;
                    DiagnosticsLog.Error("capture", "Auto-save failed", ex);
                }
            }

            Completed?.Invoke(saved);
        }

        private static (int X, int Y) GetCursorScreenPos()
        {
            try
            {
                if (Helpers.Win32Helper.GetCursorPos(out var p)) return (p.X, p.Y);
            }
            catch { }
            return (0, 0);
        }
    }
}
