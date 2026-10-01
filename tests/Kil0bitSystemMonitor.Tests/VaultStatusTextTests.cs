using System;
using System.IO;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Settings' credentials status line, its card, and the guide's section on it.</summary>
    public class VaultStatusTextTests
    {
        [Theory]
        [InlineData(false, false, 0, false, "The vault could not be read right now.")]
        [InlineData(false, true, 3, true, "The vault could not be read right now.")]
        [InlineData(true, false, 0, false, "No PIN set yet. Select text in MicaPad and choose Store as credential.")]
        [InlineData(true, true, 0, false, "No credentials stored. Locked.")]
        [InlineData(true, true, 1, false, "1 credential stored. Locked.")]
        [InlineData(true, true, 2, false, "2 credentials stored. Locked.")]
        [InlineData(true, true, 12, false, "12 credentials stored. Locked.")]
        [InlineData(true, true, 0, true, "No credentials stored. Unlocked.")]
        [InlineData(true, true, 1, true, "1 credential stored. Unlocked.")]
        [InlineData(true, true, 5, true, "5 credentials stored. Unlocked.")]
        public void Describe_follows_the_table(bool loaded, bool exists, int count, bool unlocked, string expected)
            => Assert.Equal(expected, VaultStatusText.Describe(loaded, exists, count, unlocked));

        [Fact]
        public void Settings_has_the_credentials_card_with_its_three_buttons()
        {
            string xaml = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "SettingsWindow.xaml"));
            Assert.Contains("Text=\"Credentials\"", xaml);
            Assert.Contains("x:Name=\"PadVaultStatus\"", xaml);
            Assert.Contains("Glyph=\"&#xE72E;\"", xaml);
            Assert.Contains("x:Name=\"PadVaultChangePin\"", xaml);
            Assert.Contains("x:Name=\"PadVaultLock\"", xaml);
            Assert.Contains("x:Name=\"PadVaultReset\"", xaml);
            Assert.Contains("Content=\"Change PIN&#x2026;\"", xaml);
            Assert.Contains("Content=\"Lock now\"", xaml);
            Assert.Contains("Content=\"Reset vault&#x2026;\"", xaml);
            Assert.True(xaml.IndexOf("x:Name=\"PadThemeBox\"", StringComparison.Ordinal) < xaml.IndexOf("x:Name=\"PadVaultStatus\"", StringComparison.Ordinal));
        }

        [Fact]
        public void Guide_explains_credentials_and_encryption()
        {
            string guide = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "GUIDE.md"));
            int at = guide.IndexOf("### Credentials and encryption", StringComparison.Ordinal);
            Assert.True(at > guide.IndexOf("### Real files", StringComparison.Ordinal));
            int next = guide.IndexOf("\n## ", at, StringComparison.Ordinal);
            string section = guide.Substring(at, next - at);
            foreach (string phrase in new[] { "Store as credential", "Copy secret", "Unmask", "Reset vault", "a longer PIN is stronger", "Recycle Bin", "Save As" })
                Assert.Contains(phrase, section);
        }
    }
}
