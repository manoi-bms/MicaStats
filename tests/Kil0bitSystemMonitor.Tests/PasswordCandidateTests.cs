using System.Linq;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class PasswordCandidateTests
    {
        [Fact]
        public void Finds_English_Thai_json_and_basic_auth_without_returning_values()
        {
            const string text = "password = winter-owl-77\nรหัสผ่าน: 'ช้างน้ำเงิน987'\n\"api_key\": \"sk-live-123456\",\nhttps://alice:url-pass-55@example.com/path";

            var found = PasswordCandidates.Find(text);

            Assert.Equal(4, found.Count);
            Assert.Equal(new[] { 1, 2, 3, 4 }, found.Select(item => item.Line));
            string serialized = System.Text.Json.JsonSerializer.Serialize(found);
            Assert.DoesNotContain("winter-owl-77", serialized);
            Assert.DoesNotContain("ช้างน้ำเงิน987", serialized);
            Assert.DoesNotContain("sk-live-123456", serialized);
            Assert.DoesNotContain("url-pass-55", serialized);
        }

        [Fact]
        public void Skips_examples_and_stored_references_even_in_fenced_config()
        {
            const string text = "password=password123\ntoken={{secret:ABCDEFGH}}\napi_key=${API_KEY}\n```env\npassword=example\n```\nsecret=xxxxxx";

            Assert.Empty(PasswordCandidates.Find(text));
        }

        [Fact]
        public void Repeated_exact_values_are_one_candidate()
        {
            var found = PasswordCandidates.Find("password=Same-Secret-44\ntoken=Same-Secret-44");

            Assert.Single(found);
            Assert.Equal("credential-1", found[0].Id);
        }

        [Fact]
        public void Finds_real_credentials_inside_fenced_configuration()
        {
            var found = PasswordCandidates.Find("```env\nPASSWORD=real-fenced-secret\n```");

            Assert.Single(found);
            Assert.Equal(2, found[0].Line);
        }

        [Fact]
        public void Internal_debug_text_never_contains_the_candidate_value()
        {
            var match = Assert.Single(PasswordCandidates.ScanAll("password=owl-Secret-88"));

            Assert.DoesNotContain("owl-Secret-88", match.ToString());
        }
    }
}
