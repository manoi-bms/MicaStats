using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Pad.Search;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class SearchClientsTests
    {
        /// <summary>Answers every request with a function of it; records what was sent.</summary>
        private sealed class FakeServer : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> _answer;
            public FakeServer(Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> answer) => _answer = answer;
            public FakeServer(HttpStatusCode status, string body) : this((_, _, _) => Task.FromResult(Reply(status, body))) { }
            public List<(HttpRequestMessage Request, string Body)> Seen { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
            {
                string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancel);
                Seen.Add((request, body));
                return await _answer(request, body, cancel);   // HttpClient waits for the handler, so a fake must honour the token
            }
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static readonly SearchServer Server = new("http://gpu:8000/v1", "bge-m3", "sk-test");
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task Embeddings_are_asked_in_the_OpenAI_format_and_placed_by_index()
        {
            var fake = new FakeServer(HttpStatusCode.OK,
                "{\"data\":[{\"index\":1,\"embedding\":[0.5,0.5]},{\"index\":0,\"embedding\":[1,0]}]}");
            using var client = new EmbeddingClient(fake);

            var result = await client.EmbedAsync(Server, new[] { "first", "second" }, Wait, default);

            Assert.Equal(SearchFailure.None, result.Failure);
            Assert.Equal(new[] { 1f, 0f }, result.Vectors![0]);
            Assert.Equal(new[] { 0.5f, 0.5f }, result.Vectors![1]);
            var (request, body) = Assert.Single(fake.Seen);
            Assert.Equal("http://gpu:8000/v1/embeddings", request.RequestUri!.ToString());
            Assert.Equal("Bearer sk-test", request.Headers.Authorization!.ToString());
            using var json = JsonDocument.Parse(body);
            Assert.Equal("bge-m3", json.RootElement.GetProperty("model").GetString());
            Assert.Equal(new[] { "first", "second" }, json.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToArray());
        }

        [Fact]
        public async Task No_model_and_no_key_send_neither()
        {
            var fake = new FakeServer(HttpStatusCode.OK, "{\"data\":[{\"index\":0,\"embedding\":[1]}]}");
            using var client = new EmbeddingClient(fake);

            await client.EmbedAsync(new SearchServer("http://gpu:8000/v1/", "", null), new[] { "a" }, Wait, default);

            var (request, body) = Assert.Single(fake.Seen);
            Assert.Equal("http://gpu:8000/v1/embeddings", request.RequestUri!.ToString());
            Assert.Null(request.Headers.Authorization);
            using var json = JsonDocument.Parse(body);
            Assert.False(json.RootElement.TryGetProperty("model", out _));
        }

        [Theory]
        [InlineData("{\"data\":[{\"index\":0,\"embedding\":[1,2]}]}")]                                          // one vector for two texts
        [InlineData("{\"data\":[{\"index\":0,\"embedding\":[1,2]},{\"index\":1,\"embedding\":[1]}]}")]        // mixed dimensions
        [InlineData("{\"data\":[{\"index\":0,\"embedding\":[1]},{\"index\":0,\"embedding\":[1]}]}")]          // index repeated
        [InlineData("{\"data\":[{\"index\":0,\"embedding\":[]},{\"index\":1,\"embedding\":[]}]}")]            // empty vectors
        [InlineData("{\"error\":\"nope\"}")]
        [InlineData("not json")]
        public async Task An_answer_that_does_not_fit_is_a_bad_answer(string body)
        {
            using var client = new EmbeddingClient(new FakeServer(HttpStatusCode.OK, body));
            var result = await client.EmbedAsync(Server, new[] { "a", "b" }, Wait, default);
            Assert.Equal(SearchFailure.BadAnswer, result.Failure);
            Assert.Null(result.Vectors);
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized, SearchFailure.KeyRefused)]
        [InlineData(HttpStatusCode.Forbidden, SearchFailure.KeyRefused)]
        [InlineData(HttpStatusCode.TooManyRequests, SearchFailure.RateLimited)]
        [InlineData(HttpStatusCode.NotFound, SearchFailure.Rejected)]
        [InlineData(HttpStatusCode.UnprocessableEntity, SearchFailure.Rejected)]
        [InlineData(HttpStatusCode.InternalServerError, SearchFailure.ServerError)]
        [InlineData(HttpStatusCode.BadGateway, SearchFailure.ServerError)]
        [InlineData(HttpStatusCode.Redirect, SearchFailure.Unreachable)]
        public async Task Status_codes_are_classified(HttpStatusCode status, SearchFailure expected)
        {
            using var client = new EmbeddingClient(new FakeServer(status, "{}"));
            var result = await client.EmbedAsync(Server, new[] { "a" }, Wait, default);
            Assert.Equal(expected, result.Failure);
            Assert.Equal((int)status, result.Status);
        }

        [Fact]
        public async Task A_network_error_is_unreachable()
        {
            using var client = new EmbeddingClient(new FakeServer((_, _, _) => throw new HttpRequestException("down")));
            Assert.Equal(SearchFailure.Unreachable, (await client.EmbedAsync(Server, new[] { "a" }, Wait, default)).Failure);
        }

        [Fact]
        public async Task A_server_that_never_answers_times_out()
        {
            using var client = new EmbeddingClient(new FakeServer(async (_, _, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return Reply(HttpStatusCode.OK, "{}");
            }));
            var result = await client.EmbedAsync(Server, new[] { "a" }, TimeSpan.FromMilliseconds(100), default);
            Assert.Equal(SearchFailure.TimedOut, result.Failure);
        }

        [Fact]
        public async Task The_caller_cancelling_throws()
        {
            using var cancel = new CancellationTokenSource();
            using var client = new EmbeddingClient(new FakeServer(async (_, _, token) =>
            {
                cancel.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                return Reply(HttpStatusCode.OK, "{}");
            }));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EmbedAsync(Server, new[] { "a" }, Wait, cancel.Token));
        }

        [Fact]
        public async Task An_oversized_answer_is_refused()
        {
            string huge = "{\"data\":[{\"index\":0,\"embedding\":[" + string.Join(",", Enumerable.Repeat("0.123456789", 3_500_000)) + "]}]}";
            using var client = new EmbeddingClient(new FakeServer(HttpStatusCode.OK, huge));
            Assert.Equal(SearchFailure.BadAnswer, (await client.EmbedAsync(Server, new[] { "a" }, Wait, default)).Failure);
        }

        [Fact]
        public async Task Rerank_is_asked_in_the_Cohere_format_and_answers_best_first()
        {
            var fake = new FakeServer(HttpStatusCode.OK,
                "{\"results\":[{\"index\":2,\"relevance_score\":0.9},{\"index\":0,\"relevance_score\":0.2},{\"index\":1,\"relevance_score\":0.5}]}");
            using var client = new RerankClient(fake);

            var result = await client.RerankAsync(Server, "vpn", new[] { "a", "b", "c" }, 3, Wait, default);

            Assert.Equal(SearchFailure.None, result.Failure);
            Assert.Equal(new[] { 2, 1, 0 }, result.Ranked!.Select(r => r.Index).ToArray());
            var (request, body) = Assert.Single(fake.Seen);
            Assert.Equal("http://gpu:8000/v1/rerank", request.RequestUri!.ToString());
            using var json = JsonDocument.Parse(body);
            Assert.Equal("vpn", json.RootElement.GetProperty("query").GetString());
            Assert.Equal(3, json.RootElement.GetProperty("documents").GetArrayLength());
            Assert.Equal(3, json.RootElement.GetProperty("top_n").GetInt32());
            Assert.Equal("bge-m3", json.RootElement.GetProperty("model").GetString());
        }

        [Theory]
        [InlineData("{\"results\":[{\"index\":5,\"relevance_score\":0.9}]}")]       // index out of range
        [InlineData("{\"results\":[{\"index\":0}]}")]                               // no score
        [InlineData("{\"results\":[{\"index\":0,\"relevance_score\":1},{\"index\":0,\"relevance_score\":1}]}")]
        [InlineData("{}")]
        public async Task A_rerank_answer_that_does_not_fit_is_a_bad_answer(string body)
        {
            using var client = new RerankClient(new FakeServer(HttpStatusCode.OK, body));
            Assert.Equal(SearchFailure.BadAnswer, (await client.RerankAsync(Server, "q", new[] { "a", "b" }, 2, Wait, default)).Failure);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not a url")]
        public async Task A_malformed_address_is_unreachable_not_an_exception(string baseUrl)
        {
            var bad = new SearchServer(baseUrl, "m", null);
            using var embedder = new EmbeddingClient(new FakeServer(HttpStatusCode.OK, "{}"));
            using var reranker = new RerankClient(new FakeServer(HttpStatusCode.OK, "{}"));
            Assert.Equal(SearchFailure.Unreachable, (await embedder.EmbedAsync(bad, new[] { "a" }, Wait, default)).Failure);
            Assert.Equal(SearchFailure.Unreachable, (await reranker.RerankAsync(bad, "q", new[] { "a" }, 1, Wait, default)).Failure);
        }

        [Fact]
        public async Task A_key_with_a_newline_is_a_failure_not_an_exception()
        {
            var bad = new SearchServer("http://gpu:8000/v1", "m", "sk\nbad");
            using var embedder = new EmbeddingClient(new FakeServer(HttpStatusCode.OK, "{}"));
            using var reranker = new RerankClient(new FakeServer(HttpStatusCode.OK, "{}"));
            Assert.NotEqual(SearchFailure.None, (await embedder.EmbedAsync(bad, new[] { "a" }, Wait, default)).Failure);
            Assert.NotEqual(SearchFailure.None, (await reranker.RerankAsync(bad, "q", new[] { "a" }, 1, Wait, default)).Failure);
        }

        [Fact]
        public async Task A_lone_surrogate_is_replaced_and_a_valid_pair_is_kept()
        {
            string lone = "a" + (char)0xD83D + "b";
            string emoji = "x" + char.ConvertFromUtf32(0x1F600) + "y";
            var fake = new FakeServer(HttpStatusCode.OK, "{\"data\":[{\"index\":0,\"embedding\":[1]},{\"index\":1,\"embedding\":[1]}]}");
            using var client = new EmbeddingClient(fake);

            var result = await client.EmbedAsync(Server, new[] { lone, emoji }, Wait, default);

            Assert.Equal(SearchFailure.None, result.Failure);
            using var json = JsonDocument.Parse(Assert.Single(fake.Seen).Body);
            var sent = json.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToArray();
            Assert.Equal("a" + (char)0xFFFD + "b", sent[0]);
            Assert.Equal(emoji, sent[1]);

            var rerankFake = new FakeServer(HttpStatusCode.OK, "{\"results\":[{\"index\":0,\"relevance_score\":1}]}");
            using var reranker = new RerankClient(rerankFake);
            Assert.Equal(SearchFailure.None, (await reranker.RerankAsync(Server, lone, new[] { lone }, 1, Wait, default)).Failure);
        }

        [Fact]
        public void Failures_read_as_plain_words()
        {
            Assert.Equal("could not be reached", SearchFailureText.Describe(SearchFailure.Unreachable, null));
            Assert.Equal("refused the key", SearchFailureText.Describe(SearchFailure.KeyRefused, 401));
            Assert.Equal("is busy (too many requests)", SearchFailureText.Describe(SearchFailure.RateLimited, 429));
            Assert.Equal("timed out", SearchFailureText.Describe(SearchFailure.TimedOut, null));
            Assert.Equal("refused the request (HTTP 404)", SearchFailureText.Describe(SearchFailure.Rejected, 404));
            Assert.Equal("failed (HTTP 500)", SearchFailureText.Describe(SearchFailure.ServerError, 500));
            Assert.Equal("gave an answer MicaPad could not read", SearchFailureText.Describe(SearchFailure.BadAnswer, 200));
        }
    }
}
