using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The DPAPI secret store: round trip, nothing in clear, never a throw, never a secret in config.</summary>
    public class AiSecretStoreTests
    {
        [Fact]
        public void A_saved_secret_reads_back_in_a_new_store()
        {
            using var env = new AiTestEnv();
            string path = env.PathOf("secrets.bin");

            new SecretStore(path, env.Warn).Set(SecretNames.ClaudeKey, "sk-ant-test-123");
            var again = new SecretStore(path, env.Warn);

            Assert.True(again.Has(SecretNames.ClaudeKey));
            Assert.Equal("sk-ant-test-123", again.Get(SecretNames.ClaudeKey));
            Assert.False(again.Has(SecretNames.CompatibleKey));
            Assert.Null(again.Get(SecretNames.CompatibleKey));
            Assert.Empty(env.Warnings);
        }

        [Fact]
        public void Two_stores_on_one_file_never_drop_each_others_entries()
        {
            // The app creates a short-lived store per MCP HTTP request while Settings holds its own.
            using var env = new AiTestEnv();
            string path = env.PathOf("secrets.bin");
            var settings = new SecretStore(path, env.Warn);
            var request = new SecretStore(path, env.Warn);

            settings.Set(SecretNames.ClaudeKey, "sk-ant-1");
            request.Set(SecretNames.McpToken, "token-1");

            Assert.Equal("token-1", settings.Get(SecretNames.McpToken));
            Assert.Equal("sk-ant-1", request.Get(SecretNames.ClaudeKey));

            Parallel.For(0, 40, i =>
            {
                var store = i % 2 == 0 ? settings : new SecretStore(path, env.Warn);
                store.Set("name-" + i, "value-" + i);
            });

            var fresh = new SecretStore(path, env.Warn);
            for (int i = 0; i < 40; i++) Assert.Equal("value-" + i, fresh.Get("name-" + i));
            Assert.Equal("sk-ant-1", fresh.Get(SecretNames.ClaudeKey));
            Assert.Equal("token-1", fresh.Get(SecretNames.McpToken));
            Assert.Empty(env.Warnings);
        }

        [Fact]
        public void The_file_never_holds_the_secret_in_clear_and_needs_the_micastats_entropy()
        {
            using var env = new AiTestEnv();
            string path = env.PathOf("secrets.bin");
            new SecretStore(path, env.Warn).Set(SecretNames.McpToken, "plain-token-value");

            string text = File.ReadAllText(path);
            byte[] sealedBytes = Convert.FromBase64String(text);

            Assert.DoesNotContain("plain-token-value", text, StringComparison.Ordinal);
            Assert.DoesNotContain("plain-token-value", Encoding.UTF8.GetString(sealedBytes), StringComparison.Ordinal);
            Assert.ThrowsAny<CryptographicException>(() =>
                ProtectedData.Unprotect(sealedBytes, null, DataProtectionScope.CurrentUser));
            byte[] plain = ProtectedData.Unprotect(sealedBytes, Encoding.UTF8.GetBytes("MicaStats.Secrets.v1"),
                                                   DataProtectionScope.CurrentUser);
            Assert.Contains("plain-token-value", Encoding.UTF8.GetString(plain), StringComparison.Ordinal);
        }

        [Fact]
        public void Values_are_trimmed_and_a_blank_value_removes_the_secret()
        {
            using var env = new AiTestEnv();
            var store = new SecretStore(env.PathOf("secrets.bin"), env.Warn);

            store.Set(SecretNames.CompatibleKey, "  sk-or-123  ");
            Assert.Equal("sk-or-123", store.Get(SecretNames.CompatibleKey));

            store.Set(SecretNames.CompatibleKey, "   ");
            Assert.False(store.Has(SecretNames.CompatibleKey));
            Assert.Empty(env.Warnings);
        }

        [Fact]
        public void Remove_keeps_the_others_and_removing_the_last_deletes_the_file()
        {
            using var env = new AiTestEnv();
            string path = env.PathOf("secrets.bin");
            var store = new SecretStore(path, env.Warn);
            store.Set(SecretNames.ClaudeKey, "a");
            store.Set(SecretNames.McpToken, "b");

            store.Remove(SecretNames.ClaudeKey);

            Assert.Null(store.Get(SecretNames.ClaudeKey));
            Assert.Equal("b", store.Get(SecretNames.McpToken));

            store.Remove(SecretNames.McpToken);
            Assert.False(File.Exists(path));

            store.Remove(SecretNames.McpToken);   // nothing left: still no error
            Assert.Empty(env.Warnings);
        }

        [Fact]
        public void A_missing_file_reads_as_empty_and_creates_nothing()
        {
            using var env = new AiTestEnv();
            var store = new SecretStore(Path.Combine(env.PathOf("none"), "secrets.bin"), env.Warn);

            Assert.False(store.Has(SecretNames.ClaudeKey));
            Assert.Null(store.Get(SecretNames.ClaudeKey));
            store.Remove(SecretNames.ClaudeKey);

            Assert.False(Directory.Exists(env.PathOf("none")));
            Assert.Empty(env.Warnings);
        }

        [Fact]
        public void A_damaged_file_reads_as_empty_warns_once_and_can_be_replaced()
        {
            using var env = new AiTestEnv();
            string path = env.PathOf("secrets.bin");
            File.WriteAllText(path, "not base64 at all!!");
            var store = new SecretStore(path, env.Warn);

            Assert.Null(store.Get(SecretNames.ClaudeKey));
            Assert.False(store.Has(SecretNames.ClaudeKey));
            Assert.Single(env.Warnings);

            store.Set(SecretNames.ClaudeKey, "sk-new");

            Assert.Equal("sk-new", new SecretStore(path, env.Warn).Get(SecretNames.ClaudeKey));
            Assert.Single(env.Warnings);
        }

        [Fact]
        public void A_failed_save_warns_without_the_secret_and_keeps_nothing()
        {
            using var env = new AiTestEnv();
            string blocker = env.PathOf("blocker");
            File.WriteAllText(blocker, "a file where the folder should be");
            var store = new SecretStore(Path.Combine(blocker, "secrets.bin"), env.Warn);

            store.Set(SecretNames.ClaudeKey, "sk-ant-secret-999");

            Assert.False(store.Has(SecretNames.ClaudeKey));
            string warning = Assert.Single(env.Warnings);
            Assert.DoesNotContain("sk-ant-secret-999", warning, StringComparison.Ordinal);
        }

        [Fact]
        public void New_tokens_are_long_url_safe_and_different()
        {
            string a = SecretStore.NewToken();
            string b = SecretStore.NewToken();

            Assert.Matches("^[A-Za-z0-9_-]{43}$", a);
            Assert.Matches("^[A-Za-z0-9_-]{43}$", b);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void The_default_path_is_beside_the_config()
        {
            Assert.Equal(Path.Combine(DiagnosticsLog.DataDir, "secrets.bin"), SecretStore.DefaultPath);
        }

        [Fact]
        public void No_setting_is_shaped_like_a_secret()
        {
            // config.json is the file people attach to bug reports.
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new AppConfig()));
            foreach (var property in doc.RootElement.EnumerateObject())
                Assert.DoesNotMatch("(?i)api.?key|secret|token|password", property.Name);
        }
    }
}
