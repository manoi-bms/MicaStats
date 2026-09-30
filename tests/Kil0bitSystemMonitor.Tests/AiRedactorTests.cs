using System;
using System.IO;
using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>What the redactor hides, one rule per case, and what it must leave alone.</summary>
    public class AiRedactorTests
    {
        private static readonly Redactor R = new(@"C:\Users\Manoi", "Manoi", "DESKTOP-ABC123");

        [Theory]
        // The user's own profile folder, any case, either separator.
        [InlineData(@"C:\Users\Manoi\AppData\Local\app.exe", @"%USERPROFILE%\AppData\Local\app.exe")]
        [InlineData(@"c:/users/manoi/Documents/a.txt", @"%USERPROFILE%/Documents/a.txt")]
        [InlineData(@"C:\Users\Manoi", @"%USERPROFILE%")]
        [InlineData(@"Opened C:\Users\MANOI\x.txt today", @"Opened %USERPROFILE%\x.txt today")]
        // Anybody else's profile folder.
        [InlineData(@"C:\Users\Bob\Desktop\x.txt", @"C:\Users\<user>\Desktop\x.txt")]
        [InlineData(@"D:\Users\Jane Doe\file.txt", @"D:\Users\<user>\file.txt")]
        [InlineData(@"C:\Users\Manoi2\x", @"C:\Users\<user>\x")]
        [InlineData(@"C:\Users\Bob", @"C:\Users\<user>")]
        // The computer and user names, as whole words only.
        [InlineData("Computer DESKTOP-ABC123 is slow", "Computer [computer] is slow")]
        [InlineData("desktop-abc123", "[computer]")]
        [InlineData("DESKTOP-ABC1234", "DESKTOP-ABC1234")]
        [InlineData("Signed in as Manoi.", "Signed in as [user].")]
        [InlineData("Manoiko", "Manoiko")]
        // Addresses, without eating clock times, dates or code.
        [InlineData("Address 192.168.1.20 is up", "Address [ip] is up")]
        [InlineData("Gateway 10.0.0.1.", "Gateway [ip].")]
        [InlineData("999.1.1.1 and 1.2.3", "999.1.1.1 and 1.2.3")]
        [InlineData("fe80::1c2d:3e4f:5a6b:7c8d", "[ip]")]
        [InlineData("2001:0db8:85a3:0000:0000:8a2e:0370:7334", "[ip]")]
        [InlineData("loopback ::1 only", "loopback [ip] only")]
        [InlineData("At 10:00:00 via global::System", "At 10:00:00 via global::System")]
        [InlineData("MAC AA-BB-CC-DD-EE-FF", "MAC [mac]")]
        [InlineData("mac aa:bb:cc:dd:ee:0f.", "mac [mac].")]
        [InlineData("Report 2026-09-30 14:02", "Report 2026-09-30 14:02")]
        // Nothing to hide.
        [InlineData("chrome.exe uses 12% CPU", "chrome.exe uses 12% CPU")]
        public void Redacts(string input, string expected) => Assert.Equal(expected, R.Redact(input));

        [Fact]
        public void Names_shorter_than_three_characters_are_left_alone()
        {
            var r = new Redactor(@"C:\Users\Al", "Al", "PC");

            Assert.Equal(@"Al met PC at C:\Temp", r.Redact(@"Al met PC at C:\Temp"));
            Assert.Equal(@"%USERPROFILE%\x.txt", r.Redact(@"C:\Users\Al\x.txt"));   // the folder rule still applies
        }

        [Fact]
        public void The_current_user_profile_becomes_the_placeholder()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            string text = Redactor.ForCurrentUser().Redact(Path.Combine(profile, "AppData", "x.exe"));

            Assert.Equal(@"%USERPROFILE%\AppData\x.exe", text);
        }

        [Fact]
        public void Json_string_values_are_redacted_in_place_and_names_and_numbers_are_not()
        {
            JsonNode node = JsonNode.Parse("""{"Manoi":"C:\\Users\\Manoi\\a.exe","n":5,"list":["192.168.0.1",3,{"deep":"DESKTOP-ABC123"}],"flag":true,"none":null}""")!;

            JsonNode? back = R.RedactJson(node);

            Assert.Same(node, back);
            Assert.Equal("""{"Manoi":"%USERPROFILE%\\a.exe","n":5,"list":["[ip]",3,{"deep":"[computer]"}],"flag":true,"none":null}""",
                         node.ToJsonString());
        }

        [Fact]
        public void A_bare_json_string_comes_back_redacted_and_null_stays_null()
        {
            JsonNode? back = R.RedactJson(JsonValue.Create("mac AA-BB-CC-DD-EE-FF"));

            Assert.Equal("mac [mac]", back!.GetValue<string>());
            Assert.Null(R.RedactJson(null));
        }

        [Fact]
        public void Redacting_twice_changes_nothing_more()
        {
            string once = R.Redact(@"C:\Users\Manoi\a and C:\Users\Bob\b on DESKTOP-ABC123 at 10.1.2.3");

            Assert.Equal(@"%USERPROFILE%\a and C:\Users\<user>\b on [computer] at [ip]", once);
            Assert.Equal(once, R.Redact(once));
        }
    }
}
