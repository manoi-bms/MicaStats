using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>
    /// System-wide capture shortcuts, via <c>RegisterHotKey</c> on a hidden message window.
    ///
    /// <para>
    /// Registration can legitimately fail — another application may already own the
    /// combination, and Windows itself reserves several. That is reported to the diagnostics log
    /// and the remaining hotkeys still register, rather than the whole feature going quiet with
    /// no explanation.
    /// </para>
    ///
    /// <para>
    /// The same hidden window also carries MicaPad's shortcut. Each registration maps to an
    /// action, and MicaPad's key registers whether or not the capture shortcuts are switched on.
    /// </para>
    /// </summary>
    public sealed class CaptureHotkeys : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;

        private readonly Dispatcher _dispatcher;
        private readonly Func<AppConfig?> _config;
        private readonly Dictionary<int, Action> _registered = new();
        private readonly Action _openPad;
        private HwndSource? _source;
        private int _nextId = 0xA100;

        /// <summary>Creates the hotkey host; nothing registers until <see cref="Apply"/>.</summary>
        /// <param name="dispatcher">The UI dispatcher that capture runs on.</param>
        /// <param name="config">Supplies the current config, read on every Apply and every trigger.</param>
        /// <param name="openPad">Opens MicaPad; queued onto the dispatcher, never run inside the window procedure.</param>
        public CaptureHotkeys(Dispatcher dispatcher, Func<AppConfig?> config, Action openPad)
        {
            _dispatcher = dispatcher;
            _config = config;
            _openPad = openPad;
        }

        /// <summary>Registers the configured shortcuts. Safe to call again to re-apply changes.</summary>
        public void Apply()
        {
            Unregister();

            var cfg = _config();
            if (cfg == null) return;

            bool wantCapture = cfg.CaptureHotkeysEnabled;
            bool wantPad = !string.IsNullOrWhiteSpace(cfg.PadHotkey);
            if (!wantCapture && !wantPad) return;

            EnsureWindow();
            if (_source == null) return;

            if (wantCapture)
            {
                Register(cfg.CaptureHotkeyRegion, "capture", nameof(CaptureMode.Region),
                    () => CaptureService.Start(CaptureMode.Region, _config(), _dispatcher));
                Register(cfg.CaptureHotkeyWindow, "capture", nameof(CaptureMode.ActiveWindow),
                    () => CaptureService.Start(CaptureMode.ActiveWindow, _config(), _dispatcher));
                Register(cfg.CaptureHotkeyFullScreen, "capture", nameof(CaptureMode.Screen),
                    () => CaptureService.Start(CaptureMode.Screen, _config(), _dispatcher));
            }

            // Queued, not called: the handler runs inside WndProc, and opening a window there would re-enter it.
            if (wantPad) Register(cfg.PadHotkey, "pad", "MicaPad", () => _dispatcher.BeginInvoke(_openPad));
        }

        private void EnsureWindow()
        {
            if (_source != null) return;
            try
            {
                // A message-only window: never visible, exists solely to receive WM_HOTKEY.
                var parameters = new HwndSourceParameters("MicaStatsHotkeys")
                {
                    Width = 0,
                    Height = 0,
                    ParentWindow = (IntPtr)(-3),   // HWND_MESSAGE
                };
                _source = new HwndSource(parameters);
                _source.AddHook(WndProc);
            }
            catch (Exception ex)
            {
                _source = null;
                DiagnosticsLog.Error("capture", "Could not create the hotkey window", ex);
            }
        }

        private void Register(string? spec, string area, string label, Action action)
        {
            if (!HotkeyParser.TryParse(spec, out var mods, out uint vk))
            {
                if (!string.IsNullOrWhiteSpace(spec))
                    DiagnosticsLog.Warn(area, $"Hotkey '{spec}' for {label} is not a valid combination");
                return;
            }

            int id = _nextId++;
            // NOREPEAT: holding the keys down must not fire a stream of actions.
            if (RegisterHotKey(_source!.Handle, id, (uint)(mods | HotkeyModifiers.NoRepeat), vk))
            {
                _registered[id] = action;
                DiagnosticsLog.Log(area, $"Hotkey {HotkeyParser.Describe(mods, vk)} -> {label}");
            }
            else
            {
                DiagnosticsLog.Warn(area, $"Hotkey {spec} for {label} is already taken by another application");
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var action))
            {
                handled = true;
                action();
            }
            return IntPtr.Zero;
        }

        private void Unregister()
        {
            if (_source == null) return;
            foreach (int id in _registered.Keys)
            {
                try { UnregisterHotKey(_source.Handle, id); } catch { }
            }
            _registered.Clear();
        }

        public void Dispose()
        {
            Unregister();
            try
            {
                _source?.RemoveHook(WndProc);
                _source?.Dispose();
            }
            catch { }
            _source = null;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
