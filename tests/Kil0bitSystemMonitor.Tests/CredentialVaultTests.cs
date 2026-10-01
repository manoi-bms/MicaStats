using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The credential vault: store without the PIN, reveal with it, wrong PINs wait.</summary>
    public class CredentialVaultTests : IDisposable
    {
        private const int TestRounds = 1000;
        private const string Pin = "246810";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MicaStats.MicaPad.Vault.v1");
        private readonly PadTempDir _dir = new();
        private readonly FakeClock _clock = new();
        private readonly List<string> _warnings = new();

        public void Dispose() => _dir.Dispose();

        private string VaultPath => Path.Combine(_dir.Root, CredentialVault.FileName);

        private CredentialVault New() => new(VaultPath, () => _clock.UtcNow, TestRounds, _warnings.Add);

        private CredentialVault Loaded()
        {
            var vault = New();
            vault.Load();
            return vault;
        }

        private CredentialVault Created()
        {
            var vault = Loaded();
            vault.Create(Pin);
            return vault;
        }

        private static byte[] Unseal(byte[] sealedBytes) => ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser);

        private static byte[] Seal(byte[] json) => ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);

        [Fact]
        public void Without_a_file_there_is_no_vault()
        {
            var vault = New();

            Assert.Equal(VaultLoadStatus.Missing, vault.Load());
            Assert.False(vault.Exists);
            Assert.Empty(vault.Credentials);
            Assert.Equal(UnlockOutcome.NoVault, vault.Unlock(Pin).Outcome);
            Assert.False(File.Exists(VaultPath));
        }

        [Fact]
        public void A_stored_value_comes_back_only_with_the_pin()
        {
            var vault = Created();
            string secret = "p@ss \u0E23\u0E2B\u0E31\u0E2A\r\nline two";

            string id = vault.Add(secret, "Bank", "note-1");

            Assert.True(SecretTokens.IsId(id));
            Assert.Equal(new CredentialInfo(id, "Bank", _clock.UtcNow, "note-1"), Assert.Single(vault.Credentials));
            Assert.False(vault.IsUnlocked);
            Assert.Throws<InvalidOperationException>(() => vault.Reveal(id));

            Assert.Equal(UnlockOutcome.Unlocked, vault.Unlock(Pin).Outcome);
            Assert.Equal(secret, vault.Reveal(id));
        }

        [Fact]
        public void A_fresh_vault_object_reads_the_file_back()
        {
            string id = Created().Add("s3cret!", "Label", null);

            var again = New();
            Assert.Equal(VaultLoadStatus.Ready, again.Load());
            Assert.Equal("Label", again.Find(id)!.Label);
            again.Unlock(Pin);
            Assert.Equal("s3cret!", again.Reveal(id));
        }

        [Fact]
        public void Wrong_pins_count_down_then_wait_doubling_up_to_fifteen_minutes()
        {
            var vault = Created();
            for (int left = 4; left >= 1; left--)
                Assert.Equal(new UnlockResult(UnlockOutcome.WrongPin, left), vault.Unlock("000000"));

            var fifth = vault.Unlock("000000");
            Assert.Equal(UnlockOutcome.WrongPin, fifth.Outcome);
            Assert.Equal(_clock.UtcNow.AddSeconds(30), fifth.WaitUntilUtc);

            // During a wait even the right PIN is not checked, and nothing counts.
            Assert.Equal(new UnlockResult(UnlockOutcome.Waiting, 0, _clock.UtcNow.AddSeconds(30)), vault.Unlock(Pin));

            double wait = 30;
            foreach (double expected in new double[] { 60, 120, 240, 480, 900, 900 })
            {
                _clock.Advance(wait);
                var result = vault.Unlock("000000");
                Assert.Equal(UnlockOutcome.WrongPin, result.Outcome);
                Assert.Equal(_clock.UtcNow.AddSeconds(expected), result.WaitUntilUtc);
                wait = expected;
            }

            _clock.Advance(wait);
            Assert.Equal(UnlockOutcome.Unlocked, vault.Unlock(Pin).Outcome);
            vault.Lock();
            Assert.Equal(new UnlockResult(UnlockOutcome.WrongPin, 4), vault.Unlock("000000"));   // the right PIN restarted the count
        }

        [Theory]
        [InlineData(1, null)]
        [InlineData(4, null)]
        [InlineData(5, 30)]
        [InlineData(6, 60)]
        [InlineData(9, 480)]
        [InlineData(10, 900)]
        [InlineData(50, 900)]
        public void The_wait_schedule(int failed, int? seconds) =>
            Assert.Equal(seconds == null ? null : TimeSpan.FromSeconds(seconds.Value), CredentialVault.WaitAfter(failed));

        [Fact]
        public void A_wait_survives_a_restart()
        {
            var vault = Created();
            for (int i = 0; i < 5; i++) vault.Unlock("000000");

            Assert.Equal(UnlockOutcome.Waiting, Loaded().Unlock(Pin).Outcome);
        }

        [Fact]
        public void An_unlock_lasts_five_minutes()
        {
            var vault = Created();
            string id = vault.Add("x1234", null, null);
            vault.Unlock(Pin);

            Assert.Equal(_clock.UtcNow.AddMinutes(5), vault.UnlockedUntilUtc);
            _clock.Advance(299);
            Assert.Equal("x1234", vault.Reveal(id));
            _clock.Advance(1);
            Assert.False(vault.IsUnlocked);
            Assert.Null(vault.UnlockedUntilUtc);
            Assert.Throws<InvalidOperationException>(() => vault.Reveal(id));
        }

        [Fact]
        public void Lock_now_locks_at_once_and_says_so_once()
        {
            var vault = Created();
            vault.Unlock(Pin);
            int changed = 0;
            vault.Changed += (s, e) => changed++;

            vault.Lock();
            vault.Lock();

            Assert.False(vault.IsUnlocked);
            Assert.Equal(1, changed);
        }

        [Fact]
        public void Changing_the_pin_keeps_every_value()
        {
            var vault = Created();
            string a = vault.Add("first secret", "A", null);
            string b = vault.Add("second secret", "B", null);

            Assert.Throws<ArgumentException>(() => vault.ChangePin(Pin, "12"));
            Assert.Equal(UnlockOutcome.WrongPin, vault.ChangePin("111111", "13579135").Outcome);
            Assert.Equal(UnlockOutcome.Unlocked, vault.ChangePin(Pin, "13579135").Outcome);

            var again = Loaded();
            Assert.Equal(UnlockOutcome.WrongPin, again.Unlock(Pin).Outcome);
            Assert.Equal(UnlockOutcome.Unlocked, again.Unlock("13579135").Outcome);
            Assert.Equal("first secret", again.Reveal(a));
            Assert.Equal("second secret", again.Reveal(b));
        }

        [Fact]
        public void Reset_deletes_every_credential()
        {
            var vault = Created();
            vault.Add("gone soon", null, null);

            vault.Reset();

            Assert.False(vault.Exists);
            Assert.Empty(vault.Credentials);
            Assert.False(File.Exists(VaultPath));
            Assert.False(Loaded().Exists);
        }

        [Fact]
        public void Delete_needs_the_vault_unlocked()
        {
            var vault = Created();
            string id = vault.Add("delete me", null, null);

            Assert.Throws<InvalidOperationException>(() => vault.Delete(id));
            vault.Unlock(Pin);
            vault.Delete(id);

            Assert.Null(vault.Find(id));
            Assert.Null(Loaded().Find(id));
        }

        [Fact]
        public void Labels_are_trimmed_kept_on_one_line_and_capped()
        {
            var vault = Created();
            string id = vault.Add("value", "  two\r\nlines\t", null);
            Assert.Equal("two lines", vault.Find(id)!.Label);

            vault.Rename(id, new string('x', 80));
            Assert.Equal(new string('x', 60), vault.Find(id)!.Label);

            vault.Rename(id, null);
            Assert.Equal("", vault.Find(id)!.Label);
            Assert.Throws<KeyNotFoundException>(() => vault.Rename("ZZZZZZZZ", "x"));
        }

        [Fact]
        public void Nothing_readable_is_on_disk()
        {
            Created().Add("hunter2-value", "Bank login", null);

            byte[] onDisk = File.ReadAllBytes(VaultPath);
            Assert.True(onDisk.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Bank login")) < 0);   // the DPAPI layer hides even labels
            string json = Encoding.UTF8.GetString(Unseal(onDisk));
            Assert.Contains("\"label\":\"Bank login\"", json);
            Assert.DoesNotContain("hunter2", json);
        }

        [Fact]
        public void A_value_cannot_be_moved_to_another_id()
        {
            var vault = Created();
            string a = vault.Add("value of a", null, null);
            string b = vault.Add("value of b", null, null);

            var doc = JsonNode.Parse(Unseal(File.ReadAllBytes(VaultPath)))!;
            var entries = doc["credentials"]!.AsArray();
            entries[0]!["id"] = b;
            entries[1]!["id"] = a;
            File.WriteAllBytes(VaultPath, Seal(Encoding.UTF8.GetBytes(doc.ToJsonString())));

            var again = Loaded();
            again.Unlock(Pin);
            Assert.ThrowsAny<CryptographicException>(() => again.Reveal(a));
        }

        [Fact]
        public void A_private_key_from_another_vault_does_not_unlock()
        {
            using var otherDir = new PadTempDir();
            var other = new CredentialVault(Path.Combine(otherDir.Root, CredentialVault.FileName), () => _clock.UtcNow, TestRounds);
            other.Load();
            other.Create(Pin);
            Created();

            var mine = JsonNode.Parse(Unseal(File.ReadAllBytes(VaultPath)))!;
            var theirs = JsonNode.Parse(Unseal(File.ReadAllBytes(other.FilePath)))!;
            mine["privateKey"] = theirs["privateKey"]!.GetValue<string>();
            File.WriteAllBytes(VaultPath, Seal(Encoding.UTF8.GetBytes(mine.ToJsonString())));

            Assert.Equal(UnlockOutcome.WrongPin, Loaded().Unlock(Pin).Outcome);
        }

        [Fact]
        public void A_vault_this_account_cannot_open_is_moved_aside()
        {
            File.WriteAllBytes(VaultPath, new byte[] { 1, 2, 3, 4 });
            var vault = New();

            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("th-TH");   // Buddhist calendar by default
                Assert.Equal(VaultLoadStatus.MovedAside, vault.Load());
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
            }

            Assert.Matches(new Regex(@"^vault-locked-20[0-9]{6}-[0-9]{6}(-[0-9]+)?\.bin$"), Path.GetFileName(vault.MovedAsideTo));

            Assert.False(vault.Exists);
            Assert.False(File.Exists(VaultPath));
            Assert.StartsWith("vault-locked-", Path.GetFileName(vault.MovedAsideTo));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(vault.MovedAsideTo!));
            Assert.Contains(_warnings, w => w.Contains(vault.MovedAsideTo!, StringComparison.Ordinal));
        }

        [Fact]
        public void A_vault_file_held_open_elsewhere_throws_and_is_not_moved()
        {
            Created();
            using (new FileStream(VaultPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => New().Load());
            }

            Assert.True(File.Exists(VaultPath));
            Assert.True(Loaded().Exists);
        }

        [Fact]
        public void A_failed_save_changes_nothing_in_memory_or_on_disk()
        {
            var vault = Created();
            using (new FileStream(VaultPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => vault.Add("not stored", null, null));
            }

            Assert.Empty(vault.Credentials);
            Assert.False(File.Exists(VaultPath + AtomicFile.ReadySuffix));
            Assert.Empty(Loaded().Credentials);
        }

        [Theory]
        [InlineData("123456", true)]
        [InlineData("123456789012", true)]
        [InlineData("12345", false)]
        [InlineData("1234567890123", false)]
        [InlineData("12345a", false)]
        [InlineData(" 123456", false)]
        [InlineData("\u0E51\u0E52\u0E53\u0E54\u0E55\u0E56", false)]   // Thai digits
        [InlineData("\u0661\u0662\u0663\u0664\u0665\u0666", false)]   // Arabic-Indic digits
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Pins_are_six_to_twelve_ascii_digits(string? pin, bool valid) =>
            Assert.Equal(valid, CredentialVault.IsValidPin(pin));

        [Fact]
        public void Create_refuses_a_bad_pin_and_a_second_vault()
        {
            var vault = Loaded();
            Assert.Throws<ArgumentException>(() => vault.Create("123"));

            vault.Create(Pin);

            Assert.Throws<InvalidOperationException>(() => vault.Create(Pin));
            Assert.Throws<InvalidOperationException>(() => New().Create("999999"));   // not loaded, but the file exists
        }

        [Fact]
        public void Add_refuses_empty_and_oversized_values()
        {
            var vault = Created();

            Assert.Throws<ArgumentException>(() => vault.Add("", null, null));
            Assert.Throws<ArgumentException>(() => vault.Add(new string('x', CredentialVault.MaxSecretLength + 1), null, null));
            Assert.True(SecretTokens.IsId(vault.Add(new string('x', CredentialVault.MaxSecretLength), null, null)));
        }

        [Fact]
        public void Adding_never_needs_the_pin_and_ids_are_unique()
        {
            var vault = Created();

            var ids = Enumerable.Range(0, 50).Select(i => vault.Add("v" + i, null, null)).ToList();

            Assert.Equal(50, ids.Distinct().Count());
            Assert.False(vault.IsUnlocked);
        }

        [Fact]
        public void Changes_are_announced()
        {
            var vault = Created();
            int changed = 0;
            vault.Changed += (s, e) => changed++;

            string id = vault.Add("x", null, null);
            vault.Rename(id, "y");
            vault.Unlock(Pin);
            vault.Delete(id);

            Assert.Equal(4, changed);
        }

        [Fact]
        public void Sealed_garbage_text_is_moved_aside_and_kept()
        {
            byte[] sealedBytes = Seal(Encoding.UTF8.GetBytes("this is not json"));
            File.WriteAllBytes(VaultPath, sealedBytes);
            var vault = New();

            Assert.Equal(VaultLoadStatus.MovedAside, vault.Load());

            Assert.False(vault.Exists);
            Assert.False(File.Exists(VaultPath));
            Assert.Equal(sealedBytes, File.ReadAllBytes(vault.MovedAsideTo!));
        }

        [Fact]
        public void A_vault_of_an_unknown_version_is_moved_aside_and_kept()
        {
            Created();
            var doc = JsonNode.Parse(Unseal(File.ReadAllBytes(VaultPath)))!;
            doc["version"] = 2;
            byte[] sealedBytes = Seal(Encoding.UTF8.GetBytes(doc.ToJsonString()));
            File.WriteAllBytes(VaultPath, sealedBytes);
            var vault = New();

            Assert.Equal(VaultLoadStatus.MovedAside, vault.Load());

            Assert.False(vault.Exists);
            Assert.False(File.Exists(VaultPath));
            Assert.Equal(sealedBytes, File.ReadAllBytes(vault.MovedAsideTo!));
        }

        [Fact]
        public void A_failed_save_keeps_an_older_ready_copy_it_did_not_write()
        {
            Created();
            string ready = VaultPath + AtomicFile.ReadySuffix;
            File.Copy(VaultPath, ready);
            File.Delete(VaultPath);
            var vault = New();
            vault.Load();   // commits the .ready into vault.bin
            File.Move(VaultPath, ready);
            Assert.False(File.Exists(VaultPath));

            using (new FileStream(VaultPath + AtomicFile.TempSuffix, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => vault.Add("not stored", null, null));
            }

            Assert.True(File.Exists(ready));
        }

        [Fact]
        public void A_pin_is_not_checked_when_the_count_cannot_be_saved()
        {
            var vault = Created();
            using (new FileStream(VaultPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => vault.Unlock(Pin));
            }

            Assert.False(vault.IsUnlocked);
            Assert.Equal(UnlockOutcome.Unlocked, vault.Unlock(Pin).Outcome);
        }

        [Fact]
        public void A_wait_saved_far_ahead_by_a_wrong_clock_is_capped_at_fifteen_minutes()
        {
            Created();
            var doc = JsonNode.Parse(Unseal(File.ReadAllBytes(VaultPath)))!;
            doc["lockedUntilUtc"] = _clock.UtcNow.AddHours(2).ToString("O", CultureInfo.InvariantCulture);
            doc["failedAttempts"] = 5;
            File.WriteAllBytes(VaultPath, Seal(Encoding.UTF8.GetBytes(doc.ToJsonString())));
            var vault = Loaded();

            Assert.Equal(new UnlockResult(UnlockOutcome.Waiting, 0, _clock.UtcNow.AddMinutes(15)), vault.Unlock(Pin));

            _clock.Advance(900);
            Assert.Equal(UnlockOutcome.Unlocked, vault.Unlock(Pin).Outcome);
        }

        // ---- final review: the PIN card's state when it opens (item 6) ------------------------

        [Fact]
        public void The_pin_state_shows_tries_left_and_a_running_wait_and_saves_nothing()
        {
            var vault = Created();
            Assert.Equal(new PinState(5, null), vault.PinState);

            vault.Unlock("000000");
            vault.Unlock("000000");
            Assert.Equal(new PinState(3, null), vault.PinState);

            for (int i = 0; i < 3; i++) vault.Unlock("000000");   // the 5th starts a 30 s wait
            byte[] onDisk = File.ReadAllBytes(VaultPath);
            Assert.Equal(new PinState(0, _clock.UtcNow.AddSeconds(30)), vault.PinState);
            Assert.Equal(new PinState(0, _clock.UtcNow.AddSeconds(30)), Loaded().PinState);   // a wait from an earlier run

            _clock.Advance(30);
            Assert.Equal(new PinState(0, null), vault.PinState);   // over: the next wrong PIN starts a longer one
            Assert.Equal(onDisk, File.ReadAllBytes(VaultPath));
        }

        [Fact]
        public void The_pin_state_caps_a_wait_saved_far_ahead_as_a_check_does()
        {
            Created();
            var doc = JsonNode.Parse(Unseal(File.ReadAllBytes(VaultPath)))!;
            doc["lockedUntilUtc"] = _clock.UtcNow.AddHours(2).ToString("O", CultureInfo.InvariantCulture);
            doc["failedAttempts"] = 5;
            File.WriteAllBytes(VaultPath, Seal(Encoding.UTF8.GetBytes(doc.ToJsonString())));
            byte[] onDisk = File.ReadAllBytes(VaultPath);
            var vault = Loaded();

            Assert.Equal(new PinState(0, _clock.UtcNow.AddMinutes(15)), vault.PinState);
            Assert.Equal(onDisk, File.ReadAllBytes(VaultPath));   // read-only: the cap is saved by the next check
            Assert.Equal(new UnlockResult(UnlockOutcome.Waiting, 0, _clock.UtcNow.AddMinutes(15)), vault.Unlock(Pin));
        }

        [Fact]
        public void Without_a_vault_the_pin_state_has_every_try()
        {
            Assert.Equal(new PinState(CredentialVault.TriesBeforeFirstWait, null), Loaded().PinState);
        }

        [Fact]
        public void The_production_rounds_are_600000_and_work()
        {
            Assert.Equal(600_000, CredentialVault.ProductionRounds);
            var vault = new CredentialVault(VaultPath, () => _clock.UtcNow);
            Assert.Equal(600_000, vault.Rounds);

            vault.Load();
            vault.Create(Pin);
            string id = vault.Add("slow but sure", null, null);

            Assert.Equal(UnlockOutcome.Unlocked, vault.Unlock(Pin).Outcome);
            Assert.Equal("slow but sure", vault.Reveal(id));
        }

        [Fact]
        public void EnsureLoaded_loads_once_and_reports_a_file_it_cannot_read_yet()
        {
            Created().Add("x1234", null, null);
            var vault = New();
            Assert.False(vault.IsLoaded);

            using (new FileStream(VaultPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.False(vault.EnsureLoaded());
                Assert.False(vault.IsLoaded);
            }

            Assert.True(vault.EnsureLoaded());
            Assert.True(vault.IsLoaded);
            Assert.Single(vault.Credentials);
            File.Delete(VaultPath);
            Assert.True(vault.EnsureLoaded());   // once loaded, it does not read again
            Assert.Single(vault.Credentials);
        }

        [Fact]
        public void Lock_announces_an_unlock_that_ran_out_unnoticed()
        {
            var vault = Created();
            vault.Unlock(Pin);
            int changed = 0;
            vault.Changed += (s, e) => changed++;

            _clock.Advance(300);
            Assert.False(vault.IsUnlocked);   // dropped quietly by the getter
            Assert.Equal(0, changed);

            vault.Lock();
            vault.Lock();
            Assert.Equal(1, changed);
        }

        [Fact]
        public void Create_refuses_when_only_the_ready_copy_is_left()
        {
            Created().Add("x1234", null, null);
            File.Copy(VaultPath, VaultPath + AtomicFile.ReadySuffix);
            File.Delete(VaultPath);

            var vault = New();

            Assert.Throws<InvalidOperationException>(() => vault.Create(Pin));
        }
    }
}
