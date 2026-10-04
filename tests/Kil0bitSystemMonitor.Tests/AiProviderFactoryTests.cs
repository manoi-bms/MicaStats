using System;
using System.IO;
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

    /// <summary>
    /// Remembers environment variables of the process and puts them back when disposed: a test
    /// that sets one leaves the process as it found it, whatever happens in between.
    /// </summary>
    internal sealed class SavedEnvironment : IDisposable
    {
        private readonly (string Name, string? Value)[] _saved;

        public SavedEnvironment(params string[] names) =>
            _saved = names.Select(name => (name, Environment.GetEnvironmentVariable(name))).ToArray();

        public void Dispose()
        {
            foreach ((string name, string? value) in _saved) Environment.SetEnvironmentVariable(name, value);
        }
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

        // ----- What the environment of the process may not change ---------------------------------

        /// <summary>
        /// Making a Claude client clears the three gateway variables for the whole process. The SDK
        /// reads ANTHROPIC_CUSTOM_HEADERS once per process, in the static constructor of its request
        /// parameters, so a test that looks at a request cannot tell whether that clear is there
        /// unless it happens to send the first request of the run. This one reads the variables.
        /// </summary>
        [Theory]
        [InlineData("ANTHROPIC_BASE_URL")]
        [InlineData("ANTHROPIC_AUTH_TOKEN")]
        [InlineData("ANTHROPIC_CUSTOM_HEADERS")]
        public void Making_a_claude_client_clears_each_gateway_variable_of_the_process(string name)
        {
            using var saved = new SavedEnvironment("ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_CUSTOM_HEADERS");
            using var env = new AiTestEnv();
            SecretStore secrets = Secrets(env, claudeKey: "sk-ant-test");
            var handler = new ScriptedHttpHandler(_ => (HttpStatusCode.OK, "application/json", ScriptedHttpHandler.ClaudeText("Fine.")));
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "https://proxy.invalid");
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", "proxy-token");
            // A header that would do no harm if the SDK did take it up for the rest of the run.
            Environment.SetEnvironmentVariable("ANTHROPIC_CUSTOM_HEADERS", "x-micastats-test: 1");

            AiClientResult result = AiProviderFactory.Create(new AppConfig(), secrets, handler);
            using IChatClient? client = result.Client;

            Assert.NotNull(client);
            Assert.Null(Environment.GetEnvironmentVariable(name));
            Assert.Empty(handler.Requests);
        }

        /// <summary>
        /// A profile named in the environment is the SDK's own way to find credentials. MicaStats
        /// gives the saved key and nothing else: a profile that does not exist must not stop a
        /// question, and none may add to what is sent.
        /// </summary>
        [Fact]
        public async Task Claude_uses_the_saved_key_whatever_profile_the_environment_names()
        {
            using var saved = new SavedEnvironment("ANTHROPIC_PROFILE", "ANTHROPIC_API_KEY", "ANTHROPIC_CONFIG_DIR");
            using var env = new AiTestEnv();
            SecretStore secrets = Secrets(env, claudeKey: "sk-ant-test");
            var handler = new ScriptedHttpHandler(_ => (HttpStatusCode.OK, "application/json", ScriptedHttpHandler.ClaudeText("Fine.")));
            Environment.SetEnvironmentVariable("ANTHROPIC_PROFILE", "micastats-test-no-such-profile");
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            // The SDK looks for profiles under %APPDATA%\Anthropic unless told otherwise: an empty
            // folder of this test, so nothing of the user's is looked at.
            Environment.SetEnvironmentVariable("ANTHROPIC_CONFIG_DIR", env.PathOf("anthropic-config"));

            AiClientResult result = AiProviderFactory.Create(new AppConfig(), secrets, handler);
            Assert.Null(result.Problem);
            using IChatClient client = result.Client!;
            ChatResponse response = await client.GetResponseAsync("hi");

            Assert.Equal("Fine.", response.Text);
            ScriptedHttpHandler.Sent sent = Assert.Single(handler.Requests);
            Assert.Equal("https://api.anthropic.com/v1/messages", sent.Url);
            Assert.Equal("x-api-key=sk-ant-test", sent.Auth);
            Assert.Equal("", sent.Authorization);
            Assert.False(sent.Headers!.ContainsKey("anthropic-workspace-id"));
        }

        [Fact]
        public async Task Claude_uses_the_saved_key_and_not_one_from_the_environment()
        {
            using var saved = new SavedEnvironment("ANTHROPIC_API_KEY");
            using var env = new AiTestEnv();
            SecretStore secrets = Secrets(env, claudeKey: "sk-ant-test");
            var handler = new ScriptedHttpHandler(_ => (HttpStatusCode.OK, "application/json", ScriptedHttpHandler.ClaudeText("Fine.")));
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-from-the-environment");

            using IChatClient client = AiProviderFactory.Create(new AppConfig(), secrets, handler).Client!;
            await client.GetResponseAsync("hi");

            ScriptedHttpHandler.Sent sent = Assert.Single(handler.Requests);
            Assert.Equal("x-api-key=sk-ant-test", sent.Auth);
            Assert.Equal("", sent.Authorization);
            Assert.DoesNotContain("from-the-environment", string.Concat(sent.Headers!.Values), StringComparison.Ordinal);
        }

        /// <summary>
        /// The same with a profile that does exist, in a folder of this test: its workspace, its
        /// address and its token stay out of the request.
        /// </summary>
        [Fact]
        public async Task A_profile_that_exists_adds_nothing_to_a_claude_request()
        {
            using var saved = new SavedEnvironment("ANTHROPIC_PROFILE", "ANTHROPIC_API_KEY", "ANTHROPIC_CONFIG_DIR");
            using var env = new AiTestEnv();
            SecretStore secrets = Secrets(env, claudeKey: "sk-ant-test");
            var handler = new ScriptedHttpHandler(_ => (HttpStatusCode.OK, "application/json", ScriptedHttpHandler.ClaudeText("Fine.")));
            string folder = env.PathOf("anthropic-config");
            Directory.CreateDirectory(Path.Combine(folder, "configs"));
            Directory.CreateDirectory(Path.Combine(folder, "credentials"));
            File.WriteAllText(Path.Combine(folder, "configs", "micastats-test.json"),
                "{\"authentication\":{\"type\":\"user_oauth\"},\"workspace_id\":\"wrkspc_micastats_test\",\"base_url\":\"https://proxy.invalid\"}");
            File.WriteAllText(Path.Combine(folder, "credentials", "micastats-test.json"),
                "{\"version\":\"1\",\"type\":\"oauth_token\",\"access_token\":\"profile-token\"}");
            Environment.SetEnvironmentVariable("ANTHROPIC_PROFILE", "micastats-test");
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_CONFIG_DIR", folder);

            AiClientResult result = AiProviderFactory.Create(new AppConfig(), secrets, handler);
            Assert.Null(result.Problem);
            using IChatClient client = result.Client!;
            await client.GetResponseAsync("hi");

            ScriptedHttpHandler.Sent sent = Assert.Single(handler.Requests);
            Assert.Equal("https://api.anthropic.com/v1/messages", sent.Url);
            Assert.Equal("x-api-key=sk-ant-test", sent.Auth);
            Assert.Equal("", sent.Authorization);
            Assert.False(sent.Headers!.ContainsKey("anthropic-workspace-id"));
            Assert.DoesNotContain("profile-token", string.Concat(sent.Headers.Values), StringComparison.Ordinal);
            Assert.DoesNotContain("wrkspc_micastats_test", string.Concat(sent.Headers.Values), StringComparison.Ordinal);
        }
    }
}
