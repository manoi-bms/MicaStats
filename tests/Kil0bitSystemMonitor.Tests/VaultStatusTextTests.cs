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

        // Final review, item 9: Change PIN needs a vault, like Reset vault; Lock now an unlocked one.
        [Theory]
        [InlineData(false, false, false, false, false, false)]
        [InlineData(false, true, true, false, false, false)]
        [InlineData(true, false, false, false, false, false)]
        [InlineData(true, true, false, true, false, true)]
        [InlineData(true, true, true, true, true, true)]
        public void The_card_buttons_follow_the_vault(bool loaded, bool exists, bool unlocked, bool changePin, bool lockNow, bool reset)
            => Assert.Equal(new VaultButtons(changePin, lockNow, reset), VaultStatusText.Buttons(loaded, exists, unlocked));

        [Fact]
        public void Settings_enables_the_buttons_by_the_rule()
        {
            string code = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "SettingsWindow.xaml.cs"));
            Assert.Contains("PadVaultChangePin.IsEnabled = buttons.ChangePin;", code);
            Assert.Contains("PadVaultLock.IsEnabled = buttons.LockNow;", code);
            Assert.Contains("PadVaultReset.IsEnabled = buttons.Reset;", code);
        }

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
            string section = guide.Substring(at, (next < 0 ? guide.Length : next) - at);
            foreach (string phrase in new[] { "Store as credential", "Copy secret", "Unmask", "Reset vault", "a longer PIN is stronger", "Recycle Bin", "Save As" })
                Assert.Contains(phrase, section);
        }

        // Final review, item 2: the pill menu opens with a right-click, and the spec's "does not protect" list in plain words.
        [Fact]
        public void Guide_says_how_the_pill_menu_opens_and_what_is_not_protected()
        {
            string guide = File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "GUIDE.md"));
            int at = guide.IndexOf("### Credentials and encryption", StringComparison.Ordinal);
            int next = guide.IndexOf("\n### ", at + 1, StringComparison.Ordinal);
            string section = guide.Substring(at, (next < 0 ? guide.Length : next) - at);

            Assert.DoesNotContain("Click a pill for its menu", section);
            Assert.Contains("Right-click a pill (or press the menu key) for its menu", section);
            foreach (string phrase in new[] { "guess", "offline", "6-digit PIN", "12-digit", "administrators", "SYSTEM", "SSD", "in memory", "reset Windows password" })
                Assert.Contains(phrase, section);
            Assert.DoesNotContain("Within 6 to 12 digits", section);
        }
    }
}
