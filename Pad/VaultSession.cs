using System;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Pad;
using Microsoft.Win32;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Locks the credential vault when it should not stay open: Windows locks or disconnects the
    /// session, the PC goes to sleep, or the unlock's five minutes run out. One per process, started
    /// by the app. SystemEvents raise on their own thread, so every lock is posted to the UI thread
    /// (<c>post</c>), where the vault's Changed handlers expect to run.
    /// </summary>
    internal sealed class VaultSession : IDisposable
    {
        private readonly CredentialVault _vault;
        private readonly Func<DateTime> _utcNow;
        private readonly Action<Action> _post;
        private DispatcherTimer? _timer;
        private bool _started;

        public VaultSession(CredentialVault vault, Func<DateTime> utcNow, Action<Action> post)
        {
            _vault = vault;
            _utcNow = utcNow;
            _post = post;
            _vault.Changed += OnVaultChanged;
        }

        /// <summary>Listens to Windows. Not in tests: they call the handlers directly.</summary>
        public void Start()
        {
            if (_started) return;
            _started = true;
            SystemEvents.SessionSwitch += OnSystemSessionSwitch;
            SystemEvents.PowerModeChanged += OnSystemPowerModeChanged;
        }

        /// <summary>How long until the unlock ends, or null while locked.</summary>
        internal TimeSpan? ExpiryIn => _vault.UnlockedUntilUtc is DateTime until ? until - _utcNow() : null;

        internal void OnSessionSwitch(SessionSwitchReason reason)
        {
            if (reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
                _post(_vault.Lock);
        }

        internal void OnPowerModeChanged(PowerModes mode)
        {
            if (mode == PowerModes.Suspend) _post(_vault.Lock);
        }

        /// <summary>The timer fired: lock if the unlock is really over (the vault checks the clock itself), else wait the rest.</summary>
        internal void OnExpiryTimer()
        {
            _timer?.Stop();
            if (_vault.IsUnlocked) Arm();
            else _vault.Lock();
        }

        public void Dispose()
        {
            _vault.Changed -= OnVaultChanged;
            _timer?.Stop();
            if (!_started) return;
            SystemEvents.SessionSwitch -= OnSystemSessionSwitch;
            SystemEvents.PowerModeChanged -= OnSystemPowerModeChanged;
        }

        private void OnSystemSessionSwitch(object? sender, SessionSwitchEventArgs e) => OnSessionSwitch(e.Reason);

        private void OnSystemPowerModeChanged(object? sender, PowerModeChangedEventArgs e) => OnPowerModeChanged(e.Mode);

        private void OnVaultChanged(object? sender, EventArgs e) => _post(Arm);

        private void Arm()
        {
            if (ExpiryIn is not TimeSpan left)
            {
                _timer?.Stop();
                return;
            }

            if (Dispatcher.FromThread(System.Threading.Thread.CurrentThread) == null) return;   // tests without a dispatcher drive OnExpiryTimer themselves
            _timer ??= new DispatcherTimer(DispatcherPriority.Background);
            _timer.Tick -= OnTick;
            _timer.Tick += OnTick;
            _timer.Interval = left > TimeSpan.Zero ? left + TimeSpan.FromMilliseconds(50) : TimeSpan.FromMilliseconds(50);
            _timer.Stop();
            _timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            try
            {
                OnExpiryTimer();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Warn("pad", "Locking the credential vault failed (" + ex.GetType().Name + ")");
            }
        }
    }
}
