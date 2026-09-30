using System;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Ai;
using Microsoft.Extensions.AI;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    [CollectionDefinition("AnthropicEnv", DisableParallelization = true)]
    public class AnthropicEnvCollection
    {
    }

    [Collection("AnthropicEnv")]
    public class AiProviderFactoryTests
    {
        private static SecretStore Secrets(AiTestEnv env, string? claudeKey = null, string? compatibleKey = null)
        {
            var store = new SecretStore(env.PathOf("secrets.bin"), _ => { });
            if (claudeKey != null) store.Set(SecretNames.ClaudeKey, claudeKey);
            if (compatibleKey != null) store.Set(SecretNames.CompatibleKey, compatibleKey);
            return store;
        }

        private static AppConfig Compatible(string baseUrl, string model) => new()
        {
            AiProvider = AiProviders.OpenAiCompatible,
            AiCompatibleBaseUrl = baseUrl,
            AiCompatibleModel = model,
        };

        [Fact]
        public void Claude_without_a_key_asks_for_one()
        {
            using var env = new AiTestEnv();

            AiClientResult result = AiProviderFactory.Create(new AppConfig(), Secrets(env));

            Assert.Null(result.Client);
            Assert.Equal("Add an API key in Settings > AI.", result.Problem);
            Assert.True(result.IsClaude);
        }

        [Fact]
        public void A_compatible_endpoint_without_a_model_asks_for_one()
        {
            using var env = new AiTestEnv();

            AiClientResult result = AiProviderFactory.Create(Compatible("http://localhost:11434/v1", " "), Secrets(env));

            Assert.Null(result.Client);
            Assert.Equal("Choose a model in Settings > AI.", result.Problem);
            Assert.False(result.IsClaude);
        }

        [Fact]
        public void A_base_url_that_is_not_http_is_refused()
        {
            using var env = new AiTestEnv();

            AiClientResult result = AiProviderFactory.Create(Compatible("ftp://localhost/v1", "llama3.2"), Secrets(env));

            Assert.Null(result.Client);
            Assert.Contains("base URL", result.Problem, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Claude_calls_the_messages_api_with_the_key_the_model_and_the_output_cap()
        {
            using var env = new AiTestEnv();
            var handler = new ScriptedHttpHandler(_ => (HttpStatusCode.OK, "application/json", ScriptedHttpHandler.ClaudeText("CPU is fine.")));

            AiClientResult result = AiProviderFactory.Create(new AppConfig(), Secrets(env, claudeKey: "sk-ant-test"), handler);
            ChatResponse response = await result.Client!.GetResponseAsync("How is my CPU?");

            Assert.Equal("CPU is fine.", response.Text);
            ScriptedHttpHandler.Sent sent = Assert.Single(handler.Requests);
            Assert.Equal("https://api.anthropic.com/v1/messages", sent.Url);
            Assert.Equal("x-api-key=sk-ant-test", sent.Auth);
            JsonNode body = JsonNode.Parse(sent.Body)!;
            Assert.Equal("claude-haiku-4-5", body["model"]!.GetValue<string>());
            Assert.Equal(2000, body["max_tokens"]!.GetValue<int>());
        }

        [Fact]
        public async Task Claude_ignores_base_url_and_token_variables_from_the_environment()
        {
            string[] names = { "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN" };
            string?[] before = names.Select(Environment.GetEnvironmentVariable).ToArray();
            try
            {
                Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "https://proxy.invalid");
                Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", "proxy-token");
                using var env = new AiTestEnv();
                var handler = new ScriptedHttpHandler(_ => (HttpStatusCode.OK, "application/json", ScriptedHttpHandler.ClaudeText("Fine.")));

                AiClientResult result = AiProviderFactory.Create(new AppConfig(), Secrets(env, claudeKey: "sk-ant-test"), handler);
                await result.Client!.GetResponseAsync("hi");

                ScriptedHttpHandler.Sent sent = Assert.Single(handler.Requests);
                Assert.Equal("https://api.anthropic.com/v1/messages", sent.Url);
                Assert.Equal("x-api-key=sk-ant-test", sent.Auth);
                Assert.Equal("", sent.Authorization);
            }
            finally
            {
                for (int i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], before[i]);
            }
        }

        [Fact]
        public async Task Claude_ignores_custom_headers_from_the_environment()
        {
            string? before = Environment.GetEnvironmentVariable("ANTHROPIC_CUSTOM_HEADERS");
            try
            {
                Environment.SetEnvironmentVariable("ANTHROPIC_CUSTOM_HEADERS", "x-api-key: other-key\nanthropic-version: 1999-01-01");
                using var env = new AiTestEnv();
                var handler = new ScriptedHttpHandler(_ => (HttpStatusCode.OK, "application/json", ScriptedHttpHandler.ClaudeText("Fine.")));

                AiClientResult result = AiProviderFactory.Create(new AppConfig(), Secrets(env, claudeKey: "sk-ant-test"), handler);
                await result.Client!.GetResponseAsync("hi");

                ScriptedHttpHandler.Sent sent = Assert.Single(handler.Requests);
                Assert.Equal("x-api-key=sk-ant-test", sent.Auth);
                Assert.NotEqual("1999-01-01", sent.Headers!["anthropic-version"]);
                Assert.DoesNotContain("other-key", string.Concat(sent.Headers.Values), StringComparison.Ordinal);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ANTHROPIC_CUSTOM_HEADERS", before);
            }
        }

        [Fact]
        public async Task A_local_server_gets_max_tokens_and_a_placeholder_key()
        {
            using var env = new AiTestEnv();
            var handler = new ScriptedHttpHandler(_ => (HttpStatusCode.OK, "application/json", ScriptedHttpHandler.OpenAiText));

            AiClientResult result = AiProviderFactory.Create(Compatible("http://localhost:11434/v1", "llama3.2"), Secrets(env), handler);
            ChatResponse response = await result.Client!.GetResponseAsync("CPU?", new ChatOptions { MaxOutputTokens = 2000 });

            Assert.Equal("Core 4 is idle.", response.Text);
            ScriptedHttpHandler.Sent sent = Assert.Single(handler.Requests);
            Assert.Equal("http://localhost:11434/v1/chat/completions", sent.Url);
            Assert.Equal("Bearer none", sent.Auth);
            JsonNode body = JsonNode.Parse(sent.Body)!;
            Assert.Equal("llama3.2", body["model"]!.GetValue<string>());
            Assert.Equal(2000, body["max_tokens"]!.GetValue<int>());
            Assert.Null(body["max_completion_tokens"]);
        }

        [Fact]
        public async Task OpenAI_itself_keeps_max_completion_tokens()
        {
            using var env = new AiTestEnv();
            var handler = new ScriptedHttpHandler(_ => (HttpStatusCode.OK, "application/json", ScriptedHttpHandler.OpenAiText));

            AiClientResult result = AiProviderFactory.Create(Compatible("https://api.openai.com/v1", "gpt-5-mini"),
                Secrets(env, compatibleKey: "sk-test"), handler);
            await result.Client!.GetResponseAsync("CPU?", new ChatOptions { MaxOutputTokens = 2000 });

            ScriptedHttpHandler.Sent sent = Assert.Single(handler.Requests);
            Assert.Equal("Bearer sk-test", sent.Auth);
            JsonNode body = JsonNode.Parse(sent.Body)!;
            Assert.Equal(2000, body["max_completion_tokens"]!.GetValue<int>());
            Assert.Null(body["max_tokens"]);
        }

        /// <summary>
        /// Only OpenAI itself gets max_completion_tokens. A host that merely ends in "openai.com"
        /// is another server, which may ignore the newer name and leave answers unbounded.
        /// </summary>
        [Fact]
        public async Task A_host_that_only_ends_in_openai_com_gets_max_tokens()
        {
            using var env = new AiTestEnv();
            var handler = new ScriptedHttpHandler(_ => (HttpStatusCode.OK, "application/json", ScriptedHttpHandler.OpenAiText));

            AiClientResult result = AiProviderFactory.Create(Compatible("https://notopenai.com/v1", "llama3.2"),
                Secrets(env, compatibleKey: "sk-test"), handler);
            await result.Client!.GetResponseAsync("CPU?", new ChatOptions { MaxOutputTokens = 2000 });

            JsonNode body = JsonNode.Parse(Assert.Single(handler.Requests).Body)!;
            Assert.Equal(2000, body["max_tokens"]!.GetValue<int>());
            Assert.Null(body["max_completion_tokens"]);
        }

        [Theory]
        [InlineData("https://api.openai.com/v1", true)]
        [InlineData("https://openai.com/v1", true)]
        [InlineData("https://eu.api.openai.com/v1", true)]
        [InlineData("https://API.OpenAI.com/v1", true)]
        [InlineData("https://my-resource.openai.azure.com/openai/v1", true)]
        [InlineData("https://notopenai.com/v1", false)]
        [InlineData("https://api.notopenai.com/v1", false)]
        [InlineData("https://myopenai.azure.com/v1", false)]
        [InlineData("https://openai.com.example.net/v1", false)]
        [InlineData("http://localhost:11434/v1", false)]
        public void Only_openai_and_azure_openai_hosts_count_as_openai(string url, bool expected)
        {
            Assert.Equal(expected, AiProviderFactory.IsOpenAiHost(new Uri(url)));
        }

        /// <summary>
        /// The design (section 10): a busy or rate-limited service is retried twice by the SDK
        /// before the Ask window says it is busy, so three attempts in all. The reply carries a
        /// tiny retry-after so the test does not wait out the SDK backoff.
        /// </summary>
        [Theory]
        [InlineData(429)]
        [InlineData(529)]
        public async Task Claude_tries_a_busy_service_three_times_then_reports_busy(int status)
        {
            using var env = new AiTestEnv();
            var handler = new ScriptedHttpHandler(_ => ((HttpStatusCode)status, "application/json",
                "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}"));
            handler.ResponseHeaders["retry-after-ms"] = "1";
            handler.ResponseHeaders["retry-after"] = "0";
            IChatClient claude = AiProviderFactory.Create(new AppConfig(), Secrets(env, claudeKey: "sk-ant-test"), handler).Client!;

            Exception error = await Assert.ThrowsAnyAsync<Exception>(() => claude.GetResponseAsync("hi"));

            Assert.Equal(3, handler.Requests.Count);
            Assert.Equal(AiErrorText.Busy, AiErrorText.Describe(error));
        }

        [Fact]
        public async Task An_openai_compatible_service_that_stays_rate_limited_is_tried_three_times_then_reports_busy()
        {
            using var env = new AiTestEnv();
            var handler = new ScriptedHttpHandler(_ => ((HttpStatusCode)429, "application/json",
                "{\"error\":{\"message\":\"Rate limit reached\",\"type\":\"rate_limit_error\"}}"));
            handler.ResponseHeaders["retry-after-ms"] = "1";
            handler.ResponseHeaders["retry-after"] = "0";
            IChatClient local = AiProviderFactory.Create(Compatible("http://localhost:11434/v1", "llama3.2"), Secrets(env), handler).Client!;

            Exception error = await Assert.ThrowsAnyAsync<Exception>(() => local.GetResponseAsync("hi"));

            Assert.Equal(3, handler.Requests.Count);
            Assert.Equal(AiErrorText.Busy, AiErrorText.Describe(error));
        }

        [Fact]
        public async Task A_rejected_key_reads_as_such_for_both_providers()
        {
            using var env = new AiTestEnv();
            var claudeHandler = new ScriptedHttpHandler(_ => (HttpStatusCode.Unauthorized, "application/json", ScriptedHttpHandler.ClaudeUnauthorized));
            var openAiHandler = new ScriptedHttpHandler(_ => (HttpStatusCode.Unauthorized, "application/json",
                "{\"error\":{\"message\":\"Incorrect API key provided\",\"type\":\"invalid_request_error\"}}"));
            IChatClient claude = AiProviderFactory.Create(new AppConfig(), Secrets(env, claudeKey: "sk-ant-bad"), claudeHandler).Client!;
            IChatClient openAi = AiProviderFactory.Create(Compatible("https://api.openai.com/v1", "gpt-5-mini"),
                Secrets(env, compatibleKey: "sk-bad"), openAiHandler).Client!;

            Exception claudeError = await Assert.ThrowsAnyAsync<Exception>(() => claude.GetResponseAsync("hi"));
            Exception openAiError = await Assert.ThrowsAnyAsync<Exception>(() => openAi.GetResponseAsync("hi"));

            Assert.Equal("The key was rejected. Check it in Settings > AI.", AiErrorText.Describe(claudeError));
            Assert.Equal("The key was rejected. Check it in Settings > AI.", AiErrorText.Describe(openAiError));
            Assert.Single(claudeHandler.Requests);
        }
    }
}
