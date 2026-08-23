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
    /// </summary>
    public sealed class CaptureHotkeys : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;

        private readonly Dispatcher _dispatcher;
        private readonly Func<AppConfig?> _config;
        private readonly Dictionary<int, CaptureMode> _registered = new();
        private HwndSource? _source;
        private int _nextId = 0xA100;

        public CaptureHotkeys(Dispatcher dispatcher, Func<AppConfig?> config)
        {
            _dispatcher = dispatcher;
            _config = config;
        }

        /// <summary>Registers the configured shortcuts. Safe to call again to re-apply changes.</summary>
        public void Apply()
        {
            Unregister();

            var cfg = _config();
            if (cfg == null || !cfg.CaptureHotkeysEnabled) return;

            EnsureWindow();
            if (_source == null) return;

            Register(cfg.CaptureHotkeyRegion, CaptureMode.Region);
            Register(cfg.CaptureHotkeyWindow, CaptureMode.ActiveWindow);
            Register(cfg.CaptureHotkeyFullScreen, CaptureMode.Screen);
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

        private void Register(string? spec, CaptureMode mode)
        {
            if (!HotkeyParser.TryParse(spec, out var mods, out uint vk))
            {
                if (!string.IsNullOrWhiteSpace(spec))
                    DiagnosticsLog.Warn("capture", $"Hotkey '{spec}' for {mode} is not a valid combination");
                return;
            }

            int id = _nextId++;
            // NOREPEAT: holding the keys down must not fire a stream of captures.
            if (RegisterHotKey(_source!.Handle, id, (uint)(mods | HotkeyModifiers.NoRepeat), vk))
            {
                _registered[id] = mode;
                DiagnosticsLog.Log("capture", $"Hotkey {HotkeyParser.Describe(mods, vk)} -> {mode}");
            }
            else
            {
                DiagnosticsLog.Warn("capture",
                    $"Hotkey {spec} for {mode} is already taken by another application");
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var mode))
            {
                handled = true;
                CaptureService.Start(mode, _config(), _dispatcher);
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
