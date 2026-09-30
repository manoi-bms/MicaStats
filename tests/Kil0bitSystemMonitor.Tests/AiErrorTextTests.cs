using System;
using System.ClientModel;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Anthropic.Exceptions;
using Kil0bitSystemMonitor.Services.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class AiErrorTextTests
    {
        [Fact]
        public void A_rejected_key_points_to_settings()
        {
            var claude = new AnthropicUnauthorizedException(new HttpRequestException("401"))
            {
                StatusCode = HttpStatusCode.Unauthorized,
                ResponseBody = "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}",
            };
            var http = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);

            Assert.Equal("The key was rejected. Check it in Settings > AI.", AiErrorText.Describe(claude));
            Assert.Equal("The key was rejected. Check it in Settings > AI.", AiErrorText.Describe(http));
        }

        [Fact]
        public void Rate_limits_and_overload_ask_the_user_to_wait()
        {
            var limited = new AnthropicRateLimitException(new HttpRequestException("429"))
            {
                StatusCode = (HttpStatusCode)429,
                ResponseBody = "",
            };
            var overloaded = new AnthropicApiException("overloaded", new HttpRequestException("529"))
            {
                StatusCode = (HttpStatusCode)529,
                ResponseBody = "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}",
            };

            Assert.Equal(AiErrorText.Busy, AiErrorText.Describe(limited));
            Assert.Equal(AiErrorText.Busy, AiErrorText.Describe(overloaded));
        }

        [Fact]
        public void An_unknown_model_shows_the_server_message()
        {
            var notFound = new AnthropicNotFoundException(new HttpRequestException("404"))
            {
                StatusCode = HttpStatusCode.NotFound,
                ResponseBody = "{\"type\":\"error\",\"error\":{\"type\":\"not_found_error\",\"message\":\"model: claude-nope\"}}",
            };

            Assert.Equal("The AI service said: model: claude-nope", AiErrorText.Describe(notFound));
        }

        [Fact]
        public void Network_failures_and_timeouts_have_their_own_sentences()
        {
            Assert.Equal(AiErrorText.Unreachable, AiErrorText.Describe(new HttpRequestException("No such host is known.")));
            Assert.Equal(AiErrorText.Unreachable, AiErrorText.Describe(new AnthropicIOException("I/O exception", new HttpRequestException("reset"))));
            Assert.Equal(AiErrorText.TimedOut, AiErrorText.Describe(new TaskCanceledException("timeout", new TimeoutException())));
        }

        [Fact]
        public void A_retry_aggregate_is_classified_by_its_last_failure()
        {
            var network = new AggregateException("Retry failed after 3 tries.",
                new HttpRequestException("first"), new HttpRequestException("No such host is known."));
            var nested = new AggregateException(network);
            var refused = new AggregateException(new ClientResultException("registry.ollama.ai/library/gemma:2b does not support tools"));

            Assert.Equal(AiErrorText.Unreachable, AiErrorText.Describe(network));
            Assert.Equal(AiErrorText.Unreachable, AiErrorText.Describe(nested));
            Assert.False(AiErrorText.IsToolsUnsupported(network));
            Assert.True(AiErrorText.IsToolsUnsupported(refused));
        }

        [Fact]
        public void Anything_else_keeps_its_first_line()
        {
            Assert.Equal("The AI request failed: boom", AiErrorText.Describe(new InvalidOperationException("boom\nstack")));
        }

        [Theory]
        [InlineData("registry.ollama.ai/library/gemma:2b does not support tools", true)]
        [InlineData("\"auto\" tool choice requires --enable-auto-tool-choice and --tool-call-parser to be set", true)]
        [InlineData("tools param requires --jinja flag", true)]
        [InlineData("model 'llama9' not found", false)]
        public void Tool_refusals_are_recognised_by_their_message(string message, bool expected)
        {
            Assert.Equal(expected, AiErrorText.IsToolsUnsupported(new ClientResultException(message)));
        }

        [Fact]
        public void Auth_network_and_cancellation_failures_are_never_tool_refusals()
        {
            var unauthorized = new HttpRequestException("tools are not supported", null, HttpStatusCode.Unauthorized);

            Assert.False(AiErrorText.IsToolsUnsupported(unauthorized));
            Assert.False(AiErrorText.IsToolsUnsupported(new HttpRequestException("tools host is not supported")));
            Assert.False(AiErrorText.IsToolsUnsupported(new OperationCanceledException("tools not supported")));
        }
    }
}
