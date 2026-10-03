using Kil0bitSystemMonitor.Services.Pad.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class SecretMaskTests
    {
        private const string Text = "user {{secret:K7Q2M9XD}} pass {{secret:AAAAAAAA}} again {{secret:K7Q2M9XD}}";

        [Fact]
        public void Text_without_credentials_is_unchanged()
        {
            var m = SecretMask.Of("plain text");
            Assert.Equal("plain text", m.Text);
            Assert.Equal(0, m.Count);
            Assert.Equal("x", m.Unmask("x"));
            Assert.Null(m.Problem("x"));
        }

        [Fact]
        public void Credentials_are_numbered_by_first_appearance()
        {
            var m = SecretMask.Of(Text);
            Assert.Equal("user [[CREDENTIAL_1]] pass [[CREDENTIAL_2]] again [[CREDENTIAL_1]]", m.Text);
            Assert.Equal(2, m.Count);
            Assert.DoesNotContain("{{secret:", m.Text);
        }

        [Fact]
        public void Unmask_puts_the_pills_back()
        {
            var m = SecretMask.Of(Text);
            Assert.Equal("x {{secret:AAAAAAAA}} y {{secret:K7Q2M9XD}}", m.Unmask("x [[CREDENTIAL_2]] y [[CREDENTIAL_1]]"));
        }

        [Fact]
        public void An_invented_placeholder_stays_literal()
        {
            var m = SecretMask.Of(Text);
            Assert.Equal("[[CREDENTIAL_9]]", m.Unmask("[[CREDENTIAL_9]]"));
        }

        [Fact]
        public void Problem_checks_each_placeholder_count()
        {
            var m = SecretMask.Of(Text);
            Assert.Null(m.Problem("[[CREDENTIAL_1]] [[CREDENTIAL_2]] [[CREDENTIAL_1]]"));
            Assert.Equal(SecretMask.Lost, m.Problem("[[CREDENTIAL_1]] [[CREDENTIAL_1]]"));
            Assert.Equal(SecretMask.Lost, m.Problem("[[CREDENTIAL_1]] [[CREDENTIAL_2]]"));
            Assert.Equal(SecretMask.Lost, m.Problem("[[CREDENTIAL_1]] [[CREDENTIAL_2]] [[CREDENTIAL_1]] [[CREDENTIAL_2]]"));
        }
    }
}
