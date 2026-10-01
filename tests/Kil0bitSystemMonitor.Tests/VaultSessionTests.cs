using System;
using System.IO;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Microsoft.Win32;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>When the vault locks by itself: Windows locked, the PC asleep, the 5 minutes over.</summary>
    public class VaultSessionTests : IDisposable
    {
        private const string Pin = "246810";
        private readonly PadTempDir _dir = new();
        private readonly FakeClock _clock = new();
        private readonly CredentialVault _vault;
        private readonly VaultSession _session;

        public VaultSessionTests()
        {
            _vault = new CredentialVault(Path.Combine(_dir.Root, CredentialVault.FileName), () => _clock.UtcNow, 1000, _ => { });
            _vault.Load();
            _vault.Create(Pin);
            _vault.Unlock(Pin);
            _session = new VaultSession(_vault, () => _clock.UtcNow, action => action());
        }

        public void Dispose()
        {
            _session.Dispose();
            _dir.Dispose();
        }

        [Theory]
        [InlineData(SessionSwitchReason.SessionLock, false)]
        [InlineData(SessionSwitchReason.ConsoleDisconnect, false)]
        [InlineData(SessionSwitchReason.RemoteDisconnect, false)]
        [InlineData(SessionSwitchReason.SessionUnlock, true)]
        [InlineData(SessionSwitchReason.SessionLogon, true)]
        public void Locking_windows_locks_the_vault(SessionSwitchReason reason, bool stillUnlocked)
        {
            _session.OnSessionSwitch(reason);

            Assert.Equal(stillUnlocked, _vault.IsUnlocked);
        }

        [Theory]
        [InlineData(PowerModes.Suspend, false)]
        [InlineData(PowerModes.Resume, true)]
        [InlineData(PowerModes.StatusChange, true)]
        public void Sleep_locks_the_vault(PowerModes mode, bool stillUnlocked)
        {
            _session.OnPowerModeChanged(mode);

            Assert.Equal(stillUnlocked, _vault.IsUnlocked);
        }

        [Fact]
        public void The_timer_is_set_for_the_end_of_the_unlock_and_locks_then()
        {
            _clock.Advance(60);
            Assert.Equal(TimeSpan.FromMinutes(4), _session.ExpiryIn);

            _clock.Advance(240);
            int changed = 0;
            _vault.Changed += (s, e) => changed++;
            _session.OnExpiryTimer();

            Assert.False(_vault.IsUnlocked);
            Assert.Null(_session.ExpiryIn);
            Assert.Equal(1, changed);
        }
    }
}
