using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor.Services.Capture
{
    /// <summary>What a registered shortcut does.</summary>
    internal enum HotkeyTarget
    {
        CaptureRegion,
        CaptureWindow,
        CaptureScreen,
        Pad,
        Ai,
    }

    /// <summary>One shortcut <see cref="CaptureHotkeys.Apply"/> registers, decided before any Win32 call.</summary>
    /// <param name="Spec">The combination as configured, in <see cref="HotkeyParser"/> syntax.</param>
    /// <param name="Area">The diagnostics log area its registration is reported under.</param>
    /// <param name="Label">The name the log uses for it.</param>
    /// <param name="Target">What pressing it does.</param>
    internal readonly record struct HotkeyPlan(string? Spec, string Area, string Label, HotkeyTarget Target);

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
    /// The same hidden window also carries MicaPad's shortcut and Ask MicaStats'. Each
    /// registration maps to an action. MicaPad's key registers whether or not the capture
    /// shortcuts are switched on; Ask MicaStats' only while the assistant is, so switching the
    /// assistant off frees the combination for other programs.
    /// </para>
    /// </summary>
    public sealed class CaptureHotkeys : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;

        private readonly Dispatcher _dispatcher;
        private readonly Func<AppConfig?> _config;
        private readonly Dictionary<int, Action> _registered = new();
        private readonly Action _openPad;
        private readonly Action _openAi;
        private HwndSource? _source;
        private int _nextId = 0xA100;

        /// <summary>Creates the hotkey host; nothing registers until <see cref="Apply"/>.</summary>
        /// <param name="dispatcher">The UI dispatcher that capture runs on.</param>
        /// <param name="config">Supplies the current config, read on every Apply and every trigger.</param>
        /// <param name="openPad">Opens MicaPad; queued onto the dispatcher, never run inside the window procedure.</param>
        /// <param name="openAi">Opens Ask MicaStats; queued the same way.</param>
        public CaptureHotkeys(Dispatcher dispatcher, Func<AppConfig?> config, Action openPad, Action openAi)
        {
            _dispatcher = dispatcher;
            _config = config;
            _openPad = openPad;
            _openAi = openAi;
        }

        /// <summary>Registers the configured shortcuts. Safe to call again to re-apply changes.</summary>
        public void Apply()
        {
            Unregister();

            var cfg = _config();
            if (cfg == null) return;

            var plan = Plan(cfg);
            if (plan.Count == 0) return;

            EnsureWindow();
            if (_source == null) return;

            foreach (var entry in plan)
                Register(entry.Spec, entry.Area, entry.Label, ActionFor(entry.Target));
        }

        /// <summary>
        /// The shortcuts <paramref name="cfg"/> asks for, in registration order: the three capture
        /// keys while capture shortcuts are on, MicaPad's when set, and Ask MicaStats' when set
        /// and the assistant is on.
        /// </summary>
        internal static IReadOnlyList<HotkeyPlan> Plan(AppConfig cfg)
        {
            var plan = new List<HotkeyPlan>();
            if (cfg.CaptureHotkeysEnabled)
            {
                plan.Add(new HotkeyPlan(cfg.CaptureHotkeyRegion, "capture", nameof(CaptureMode.Region), HotkeyTarget.CaptureRegion));
                plan.Add(new HotkeyPlan(cfg.CaptureHotkeyWindow, "capture", nameof(CaptureMode.ActiveWindow), HotkeyTarget.CaptureWindow));
                plan.Add(new HotkeyPlan(cfg.CaptureHotkeyFullScreen, "capture", nameof(CaptureMode.Screen), HotkeyTarget.CaptureScreen));
            }

            if (!string.IsNullOrWhiteSpace(cfg.PadHotkey))
                plan.Add(new HotkeyPlan(cfg.PadHotkey, "pad", "MicaPad", HotkeyTarget.Pad));

            if (cfg.AiAssistantEnabled && !string.IsNullOrWhiteSpace(cfg.AiHotkey))
                plan.Add(new HotkeyPlan(cfg.AiHotkey, "ai", "Ask MicaStats", HotkeyTarget.Ai));

            return plan;
        }

        private Action ActionFor(HotkeyTarget target) => target switch
        {
            HotkeyTarget.CaptureRegion => () => CaptureService.Start(CaptureMode.Region, _config(), _dispatcher),
            HotkeyTarget.CaptureWindow => () => CaptureService.Start(CaptureMode.ActiveWindow, _config(), _dispatcher),
            HotkeyTarget.CaptureScreen => () => CaptureService.Start(CaptureMode.Screen, _config(), _dispatcher),
            // Queued, not called: the handler runs inside WndProc, and opening a window there would re-enter it.
            HotkeyTarget.Pad => () => _dispatcher.BeginInvoke(_openPad),
            HotkeyTarget.Ai => () => _dispatcher.BeginInvoke(_openAi),
            _ => () => { },
        };

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

        /// <summary>Unregisters every shortcut and destroys the hidden window.</summary>
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
