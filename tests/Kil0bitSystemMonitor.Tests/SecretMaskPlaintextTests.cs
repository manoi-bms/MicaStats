using Kil0bitSystemMonitor.Services.Pad.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class SecretMaskPlaintextTests
    {
        [Fact]
        public void Plaintext_and_stored_credentials_mask_and_roundtrip_exactly()
        {
            const string original = "password=owl-Secret-88; {{secret:K7Q2M9XD}}; owl-Secret-88";
            var mask = SecretMask.Of(original, new[] { "owl-Secret-88" });

            Assert.Equal(1, mask.Count);
            Assert.Equal(1, mask.PlaintextCount);
            Assert.DoesNotContain("owl-Secret-88", mask.Text);
            Assert.DoesNotContain("{{secret:", mask.Text);
            Assert.Equal(original, mask.Unmask(mask.Text));
            Assert.Null(mask.Problem(mask.Text));
        }

        [Fact]
        public void Read_only_unmask_restores_pills_but_keeps_plaintext_hidden()
        {
            var mask = SecretMask.Of("password=owl-Secret-88 {{secret:K7Q2M9XD}}", new[] { "owl-Secret-88" });

            string result = mask.Unmask(mask.Text, restorePlaintext: false);

            Assert.DoesNotContain("owl-Secret-88", result);
            Assert.Contains("[[LOCAL_SECRET_1]]", result);
            Assert.Contains("{{secret:K7Q2M9XD}}", result);
        }

        [Fact]
        public void Missing_or_repeated_plaintext_placeholders_block_rewrite()
        {
            var mask = SecretMask.Of("owl-Secret-88 owl-Secret-88", new[] { "owl-Secret-88" });

            Assert.Equal(SecretMask.Lost, mask.Problem("[[LOCAL_SECRET_1]]"));
            Assert.Equal(SecretMask.Lost, mask.Problem("[[LOCAL_SECRET_1]] [[LOCAL_SECRET_1]] [[LOCAL_SECRET_1]]"));
        }

        [Fact]
        public void Typed_placeholders_force_a_collision_free_prefix()
        {
            const string original = "[[LOCAL_SECRET_1]] password=owl-Secret-88";
            var mask = SecretMask.Of(original, new[] { "owl-Secret-88" });

            Assert.Contains("[[LOCAL_SECRET_X_1]]", mask.Text);
            Assert.Equal(original, mask.Unmask(mask.Text));
        }
    }
}
