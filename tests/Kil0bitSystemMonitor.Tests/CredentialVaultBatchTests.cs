using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public sealed class CredentialVaultBatchTests
    {
        private const string Pin = "246810";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MicaStats.MicaPad.Vault.v1");

        private static CredentialVault New(AiTestEnv env) =>
            new(env.PathOf(CredentialVault.FileName), () => env.Clock.UtcNow, rounds: 1000, warn: env.Warn);

        private static byte[] Unseal(byte[] sealedBytes) =>
            ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser);

        private static CredentialVault Created(AiTestEnv env)
        {
            var vault = New(env);
            Assert.Equal(VaultLoadStatus.Missing, vault.Load());
            vault.Create(Pin);
            return vault;
        }

        [Fact]
        public void AddMany_stores_encrypted_values_in_order_while_locked()
        {
            using var env = new AiTestEnv();
            var vault = Created(env);
            int changed = 0;
            vault.Changed += (_, _) => changed++;
            var values = new (string Secret, string? Label, string? NoteId)[]
            {
                ("same-secret-value", "  Bank\r\nprimary  ", "note-1"),
                ("another-secret-value", "Mail", null),
                ("same-secret-value", null, "note-2"),
            };

            IReadOnlyList<string> ids = vault.AddMany(values);

            Assert.False(vault.IsUnlocked);
            Assert.Equal(1, changed);
            Assert.Equal(values.Length, ids.Count);
            Assert.Equal(values.Length, ids.Distinct(StringComparer.Ordinal).Count());
            Assert.All(ids, id => Assert.True(SecretTokens.IsId(id)));
            Assert.Equal(new[] { "Bank primary", "Mail", "" }, vault.Credentials.Select(c => c.Label));
            Assert.Equal(new string?[] { "note-1", null, "note-2" }, vault.Credentials.Select(c => c.NoteId));

            byte[] sealedBytes = File.ReadAllBytes(vault.FilePath);
            Assert.True(sealedBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("same-secret-value")) < 0);
            string json = Encoding.UTF8.GetString(Unseal(sealedBytes));
            Assert.DoesNotContain("same-secret-value", json, StringComparison.Ordinal);
            Assert.DoesNotContain("another-secret-value", json, StringComparison.Ordinal);

            Assert.Equal(UnlockOutcome.Unlocked, vault.Unlock(Pin).Outcome);
            Assert.Equal(values.Select(v => v.Secret).ToArray(), ids.Select(vault.Reveal).ToArray());
        }

        [Fact]
        public void Empty_batch_returns_empty_without_loading_or_creating_the_vault()
        {
            using var env = new AiTestEnv();
            var vault = New(env);
            int changed = 0;
            vault.Changed += (_, _) => changed++;

            IReadOnlyList<string> ids = vault.AddMany(
                Array.Empty<(string Secret, string? Label, string? NoteId)>());

            Assert.Empty(ids);
            Assert.False(vault.IsLoaded);
            Assert.False(vault.Exists);
            Assert.False(File.Exists(vault.FilePath));
            Assert.Equal(0, changed);
        }

        [Fact]
        public void Throwing_changed_subscriber_does_not_hide_ids_or_skip_later_subscribers()
        {
            using var env = new AiTestEnv();
            var vault = Created(env);
            int laterSubscriberCalls = 0;
            vault.Changed += (_, _) => throw new InvalidOperationException("listener details must not be logged");
            vault.Changed += (_, _) => laterSubscriberCalls++;

            IReadOnlyList<string> ids = vault.AddMany(
                new[] { (Secret: "private-value", Label: (string?)"Label", NoteId: (string?)null) });

            Assert.True(SecretTokens.IsId(Assert.Single(ids)));
            Assert.Equal(1, laterSubscriberCalls);
            string warning = Assert.Single(env.Warnings);
            Assert.Contains(nameof(InvalidOperationException), warning, StringComparison.Ordinal);
            Assert.DoesNotContain("listener details", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("private-value", warning, StringComparison.Ordinal);
        }

        [Fact]
        public void Invalid_later_value_rejects_the_whole_batch_before_saving()
        {
            using var env = new AiTestEnv();
            var vault = Created(env);
            byte[] before = File.ReadAllBytes(vault.FilePath);
            int changed = 0;
            vault.Changed += (_, _) => changed++;
            foreach (string invalid in new[] { "", new string('x', CredentialVault.MaxSecretLength + 1) })
            {
                var values = new (string Secret, string? Label, string? NoteId)[]
                {
                    ("would-have-been-valid", "First", null),
                    (invalid, "Invalid", null),
                };

                var error = Assert.Throws<ArgumentException>(() => vault.AddMany(values));

                Assert.Equal("values", error.ParamName);
                Assert.DoesNotContain("would-have-been-valid", error.Message, StringComparison.Ordinal);
            }
            Assert.Empty(vault.Credentials);
            Assert.Equal(before, File.ReadAllBytes(vault.FilePath));
            Assert.Equal(0, changed);
        }

        [Fact]
        public void More_than_32_values_is_rejected_without_vault_side_effects()
        {
            using var env = new AiTestEnv();
            var vault = New(env);
            var values = Enumerable.Range(0, 33)
                .Select(i => (Secret: "secret-" + i, Label: (string?)null, NoteId: (string?)null))
                .ToArray();

            var error = Assert.Throws<ArgumentException>(() => vault.AddMany(values));

            Assert.Equal("values", error.ParamName);
            Assert.False(vault.IsLoaded);
            Assert.False(File.Exists(vault.FilePath));
        }

        [Fact]
        public void Failed_batch_save_changes_neither_state_disk_nor_notifications()
        {
            using var env = new AiTestEnv();
            var vault = Created(env);
            byte[] before = File.ReadAllBytes(vault.FilePath);
            int changed = 0;
            vault.Changed += (_, _) => changed++;
            var values = new (string Secret, string? Label, string? NoteId)[]
            {
                ("first-private-value", "First", null),
                ("second-private-value", "Second", "note-2"),
            };

            using (new FileStream(vault.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.ThrowsAny<IOException>(() => vault.AddMany(values));

            Assert.Empty(vault.Credentials);
            Assert.Equal(0, changed);
            Assert.Equal(before, File.ReadAllBytes(vault.FilePath));
            Assert.False(File.Exists(vault.FilePath + AtomicFile.ReadySuffix));

            var reloaded = New(env);
            Assert.Equal(VaultLoadStatus.Ready, reloaded.Load());
            Assert.Empty(reloaded.Credentials);
        }

        [Fact]
        public void Apply_failure_restores_the_previous_vault_and_rethrows_the_original_exception()
        {
            using var env = new AiTestEnv();
            var vault = Created(env);
            string existingId = vault.Add("existing-private-value", "Existing", "note-old");
            byte[] before = Unseal(File.ReadAllBytes(vault.FilePath));
            int changed = 0;
            vault.Changed += (_, _) => changed++;
            var applyFailure = new InvalidOperationException("Host apply failed.");
            IReadOnlyList<string>? proposedIds = null;

            var caught = Assert.Throws<InvalidOperationException>(() => vault.AddManyAndApply(
                new[] { (Secret: "new-private-value", Label: (string?)"New", NoteId: (string?)"note-new") },
                ids =>
                {
                    proposedIds = ids.ToArray();
                    throw applyFailure;
                }));

            Assert.Same(applyFailure, caught);
            Assert.NotNull(proposedIds);
            Assert.Single(proposedIds);
            Assert.Equal(before, Unseal(File.ReadAllBytes(vault.FilePath)));
            Assert.Equal(existingId, Assert.Single(vault.Credentials).Id);
            Assert.Equal(0, changed);

            Assert.Equal(UnlockOutcome.Unlocked, vault.Unlock(Pin).Outcome);
            Assert.Equal("existing-private-value", vault.Reveal(existingId));
            Assert.Null(vault.Find(proposedIds![0]));
        }

        [Fact]
        public void Apply_runs_after_the_batch_is_committed_and_changed_fires_after_apply()
        {
            using var env = new AiTestEnv();
            var vault = Created(env);
            int changed = 0;
            vault.Changed += (_, _) => changed++;
            IReadOnlyList<string>? observedIds = null;

            IReadOnlyList<string> ids = vault.AddManyAndApply(
                new[]
                {
                    (Secret: "first-private-value", Label: (string?)"First", NoteId: (string?)null),
                    (Secret: "second-private-value", Label: (string?)"Second", NoteId: (string?)"note-2"),
                },
                committedIds =>
                {
                    observedIds = committedIds.ToArray();
                    Assert.Equal(0, changed);
                    Assert.Equal(observedIds.ToArray(), vault.Credentials.Select(c => c.Id).ToArray());

                    var diskView = New(env);
                    Assert.Equal(VaultLoadStatus.Ready, diskView.Load());
                    Assert.Equal(observedIds.ToArray(), diskView.Credentials.Select(c => c.Id).ToArray());
                });

            Assert.NotNull(observedIds);
            Assert.Equal(ids.ToArray(), observedIds!.ToArray());
            Assert.Equal(1, changed);
        }

        [Fact]
        public void Failed_rollback_reports_safe_stored_ids_and_announces_recoverable_entries()
        {
            using var env = new AiTestEnv();
            var vault = Created(env);
            int changed = 0;
            vault.Changed += (_, _) => throw new ApplicationException("listener failure");
            vault.Changed += (_, _) => changed++;
            var applyFailure = new InvalidOperationException("Host apply failed.");
            FileStream? blocker = null;
            CredentialBatchApplyException failure;

            try
            {
                failure = Assert.Throws<CredentialBatchApplyException>(() => vault.AddManyAndApply(
                    new[] { (Secret: "recoverable-private-value", Label: (string?)"Recovery", NoteId: (string?)null) },
                    _ =>
                    {
                        blocker = new FileStream(vault.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
                        throw applyFailure;
                    }));
            }
            finally
            {
                blocker?.Dispose();
            }

            string storedId = Assert.Single(failure.StoredIds);
            Assert.True(SecretTokens.IsId(storedId));
            Assert.DoesNotContain("recoverable-private-value", failure.Message, StringComparison.Ordinal);
            Assert.Same(applyFailure, failure.ApplyException);
            Assert.IsAssignableFrom<IOException>(failure.RollbackException!);
            Assert.Equal(storedId, Assert.Single(vault.Credentials).Id);
            Assert.Equal(1, changed);
            Assert.False(File.Exists(vault.FilePath + AtomicFile.ReadySuffix));

            var reloaded = New(env);
            Assert.Equal(VaultLoadStatus.Ready, reloaded.Load());
            Assert.Equal(storedId, Assert.Single(reloaded.Credentials).Id);
            Assert.Equal(UnlockOutcome.Unlocked, reloaded.Unlock(Pin).Outcome);
            Assert.Equal("recoverable-private-value", reloaded.Reveal(storedId));
        }

        [Fact]
        public void Keep_signal_retains_loadable_entries_and_reports_recovery_ids()
        {
            using var env = new AiTestEnv();
            var vault = Created(env);
            int changed = 0;
            vault.Changed += (_, _) => changed++;

            CredentialBatchApplyException failure = Assert.Throws<CredentialBatchApplyException>(() =>
                vault.AddManyAndApply(
                    new[] { (Secret: "kept-private-value", Label: (string?)"Kept", NoteId: (string?)"note-keep") },
                    _ => throw new CredentialBatchKeepException()));

            string storedId = Assert.Single(failure.StoredIds);
            Assert.IsType<CredentialBatchKeepException>(failure.ApplyException);
            Assert.Null(failure.RollbackException);
            Assert.DoesNotContain("kept-private-value", failure.Message, StringComparison.Ordinal);
            Assert.Equal(storedId, Assert.Single(vault.Credentials).Id);
            Assert.Equal(1, changed);

            var reloaded = New(env);
            Assert.Equal(VaultLoadStatus.Ready, reloaded.Load());
            Assert.Equal(storedId, Assert.Single(reloaded.Credentials).Id);
            Assert.Equal(UnlockOutcome.Unlocked, reloaded.Unlock(Pin).Outcome);
            Assert.Equal("kept-private-value", reloaded.Reveal(storedId));
        }
    }
}
