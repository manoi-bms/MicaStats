using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class PadAiPrivacyTests
    {
        [Fact]
        public void Claude_goes_to_Anthropic() =>
            Assert.Equal("Text you run an AI action on, and passages found for a question, go to Anthropic (api.anthropic.com). Stored credentials are never sent.",
                PadAiPrivacy.Describe(AiProviders.Claude, null));

        [Fact]
        public void Localhost_stays_on_the_pc() =>
            Assert.Equal("Everything stays on this PC (localhost).",
                PadAiPrivacy.Describe(AiProviders.OpenAiCompatible, "http://localhost:11434/v1"));

        [Fact]
        public void A_remote_host_is_named() =>
            Assert.Equal("Text you run an AI action on, and passages found for a question, go to openrouter.ai. Stored credentials are never sent.",
                PadAiPrivacy.Describe(AiProviders.OpenAiCompatible, "https://openrouter.ai/api/v1"));

        [Fact]
        public void A_null_base_url_sends_nothing() =>
            Assert.Equal("The base URL is not a valid http or https address, so nothing can be sent.",
                PadAiPrivacy.Describe(AiProviders.OpenAiCompatible, null));

        [Fact]
        public void A_loopback_address_stays_on_the_pc() =>
            Assert.Equal("Everything stays on this PC (127.0.0.1).",
                PadAiPrivacy.Describe(AiProviders.OpenAiCompatible, "http://127.0.0.1:1234/v1"));

        [Fact]
        public void A_bad_url_sends_nothing() =>
            Assert.Equal("The base URL is not a valid http or https address, so nothing can be sent.",
                PadAiPrivacy.Describe(AiProviders.OpenAiCompatible, "not a url"));
    }
}
