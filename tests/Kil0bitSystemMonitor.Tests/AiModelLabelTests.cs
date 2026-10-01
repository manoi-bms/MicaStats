using Kil0bitSystemMonitor.Services.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The model line under the Ask window's title.</summary>
    public class AiModelLabelTests
    {
        [Fact]
        public void Claude_shows_its_model()
        {
            Assert.Equal("Claude \u00B7 claude-haiku-4-5",
                AiModelLabel.For(AiProviders.Claude, "claude-haiku-4-5", "llama3.2", "http://localhost:11434/v1"));
        }

        [Fact]
        public void A_compatible_server_shows_the_model_and_only_the_host()
        {
            Assert.Equal("llama3.2 \u00B7 localhost",
                AiModelLabel.For(AiProviders.OpenAiCompatible, "claude-haiku-4-5", "llama3.2", "http://localhost:11434/v1"));
            Assert.Equal("gpt-5 \u00B7 gateway.example.com",
                AiModelLabel.For(AiProviders.OpenAiCompatible, "", "gpt-5", "https://user:sk-secret@gateway.example.com/openai/v1?key=sk-secret"));
        }

        [Fact]
        public void A_missing_model_or_address_leaves_what_is_known()
        {
            Assert.Equal("localhost", AiModelLabel.For(AiProviders.OpenAiCompatible, "", " ", "http://localhost:1234/v1"));
            Assert.Equal("qwen3", AiModelLabel.For(AiProviders.OpenAiCompatible, "", "qwen3", "not a url"));
            Assert.Null(AiModelLabel.For(AiProviders.OpenAiCompatible, "", "", ""));
            Assert.Equal("Claude", AiModelLabel.For(AiProviders.Claude, " ", "", ""));
        }
    }
}
