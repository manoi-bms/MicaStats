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

        // ---- the note tools (spec 2) --------------------------------------------------------------

        [Fact]
        public void Notes_in_Ask_follow_the_provider()
        {
            Assert.Equal("When a question needs them, passages and notes Ask looks up go to Anthropic (api.anthropic.com). Stored credentials are never sent.",
                PadAiPrivacy.NotesInAsk(AiProviders.Claude, null));
            Assert.Equal("Passages and notes Ask looks up stay on this PC (localhost).",
                PadAiPrivacy.NotesInAsk(AiProviders.OpenAiCompatible, "http://localhost:11434/v1"));
            Assert.Equal("When a question needs them, passages and notes Ask looks up go to openrouter.ai. Stored credentials are never sent.",
                PadAiPrivacy.NotesInAsk(AiProviders.OpenAiCompatible, "https://openrouter.ai/api/v1"));
            Assert.Equal("The base URL is not a valid http or https address, so nothing can be sent.",
                PadAiPrivacy.NotesInAsk(AiProviders.OpenAiCompatible, "not a url"));
        }

        [Fact]
        public void Notes_in_Mcp_says_the_clients_decide_and_credentials_stay()
        {
            Assert.Equal("Programs you connected through MCP (Settings → AI) can search and read your notes. What they do with the text is up to them. Stored credentials are never given out.",
                PadAiPrivacy.NotesInMcp);
        }

        // ---- the destination, said where an action runs ------------------------------------------

        [Theory]
        [InlineData(AiProviders.Claude, null, "api.anthropic.com")]
        [InlineData(AiProviders.Claude, "http://localhost:11434/v1", "api.anthropic.com")]        // the other provider's address does not count
        [InlineData("SomethingElse", null, "api.anthropic.com")]                                  // an unknown provider is Claude, as the factory has it
        [InlineData(AiProviders.OpenAiCompatible, "http://localhost:11434/v1", "this PC")]
        [InlineData(AiProviders.OpenAiCompatible, "http://127.0.0.1:1234/v1", "this PC")]
        [InlineData(AiProviders.OpenAiCompatible, "http://[::1]:1234/v1", "this PC")]
        [InlineData(AiProviders.OpenAiCompatible, "https://openrouter.ai/api/v1", "openrouter.ai")]
        [InlineData(AiProviders.OpenAiCompatible, "  https://gpu.example:8443/v1?key=abc  ", "gpu.example")]   // the host only: no port, path or query
        [InlineData(AiProviders.OpenAiCompatible, null, "")]
        [InlineData(AiProviders.OpenAiCompatible, "", "")]
        [InlineData(AiProviders.OpenAiCompatible, "not a url", "")]
        [InlineData(AiProviders.OpenAiCompatible, "ftp://files.example/v1", "")]                  // not http or https: nothing can be sent
        public void The_destination_is_the_host_text_goes_to(string provider, string? baseUrl, string destination) =>
            Assert.Equal(destination, PadAiPrivacy.Destination(provider, baseUrl));

        [Fact]
        public void The_destination_agrees_with_the_privacy_line()
        {
            Assert.Contains(PadAiPrivacy.Destination(AiProviders.Claude, null), PadAiPrivacy.Describe(AiProviders.Claude, null), System.StringComparison.Ordinal);
            Assert.Contains(PadAiPrivacy.Destination(AiProviders.OpenAiCompatible, "https://openrouter.ai/api/v1"),
                            PadAiPrivacy.Describe(AiProviders.OpenAiCompatible, "https://openrouter.ai/api/v1"), System.StringComparison.Ordinal);
            Assert.StartsWith("Everything stays on this PC", PadAiPrivacy.Describe(AiProviders.OpenAiCompatible, "http://localhost:11434/v1"), System.StringComparison.Ordinal);
        }
    }
}
