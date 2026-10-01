using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>A Kroki server as a test scripts it; it records what was sent.</summary>
    internal sealed class FakeKrokiHandler : HttpMessageHandler
    {
        private readonly List<(HttpMethod Method, Uri Uri, string Body, string? ContentType)> _requests = new();

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _) => Task.FromResult(Answer(HttpStatusCode.OK, DiagramFakes.Svg));

        public IReadOnlyList<(HttpMethod Method, Uri Uri, string Body, string? ContentType)> Requests
        {
            get { lock (_requests) return _requests.ToArray(); }
        }

        public static HttpResponseMessage Answer(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "image/svg+xml") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancel);
            lock (_requests) _requests.Add((request.Method, request.RequestUri!, body, request.Content?.Headers.ContentType?.ToString()));
            return await Respond(request, cancel);
        }
    }

    /// <summary>Kroki (spec section 3): the request, its errors, the server setting, and the renderer's Kroki route.</summary>
    public class KrokiClientTests
    {
        private static KrokiResult Draw(KrokiClient client, string server, string type, string source)
        {
            var task = client.DrawAsync(server, type, source);
            Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "Kroki did not answer");
            return task.Result;
        }

        private static DiagramResult Wait(Task<DiagramResult> task)
        {
            Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "the draw did not finish");
            return task.Result;
        }

        [Fact]
        public void Only_the_block_text_is_posted_to_the_type_url()
        {
            var handler = new FakeKrokiHandler();
            using var client = new KrokiClient(handler);

            var result = Draw(client, "https://kroki.io", "plantuml", "@startuml\na -> b\n@enduml");

            Assert.Equal(DiagramFakes.Svg, result.Svg);
            Assert.Null(result.Error);
            Assert.True(result.Lasting);
            var sent = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, sent.Method);
            Assert.Equal("https://kroki.io/plantuml/svg", sent.Uri.AbsoluteUri);
            Assert.Equal("@startuml\na -> b\n@enduml", sent.Body);
            Assert.Equal("text/plain; charset=utf-8", sent.ContentType);
        }

        [Fact]
        public void A_server_with_a_path_keeps_it()
        {
            var handler = new FakeKrokiHandler();
            using var client = new KrokiClient(handler);
            Assert.True(KrokiClient.TryParseServer("http://localhost:8000/kroki/", out var server));

            Draw(client, server!, "d2", "a -> b");

            Assert.Equal("http://localhost:8000/kroki/d2/svg", Assert.Single(handler.Requests).Uri.AbsoluteUri);
        }

        [Fact]
        public void A_400_answer_shows_its_first_line_and_lasts()
        {
            var handler = new FakeKrokiHandler
            {
                Respond = (_, _) => Task.FromResult(FakeKrokiHandler.Answer(HttpStatusCode.BadRequest,
                    "\n  Syntax Error? (Assumed diagram type: sequence) (line: 2)  \nmore detail\n")),
            };
            using var client = new KrokiClient(handler);

            var result = Draw(client, "https://kroki.io", "plantuml", "@startuml\nnope\n@enduml");

            Assert.Null(result.Svg);
            Assert.Equal("Syntax Error? (Assumed diagram type: sequence) (line: 2)", result.Error);
            Assert.True(result.Lasting);
        }

        [Fact]
        public void Other_failures_say_the_server_could_not_be_reached_and_pass()
        {
            using var down = new KrokiClient(new FakeKrokiHandler
            {
                Respond = (_, _) => Task.FromResult(FakeKrokiHandler.Answer(HttpStatusCode.ServiceUnavailable, "busy")),
            });
            using var refused = new KrokiClient(new FakeKrokiHandler
            {
                Respond = (_, _) => throw new HttpRequestException("No connection could be made"),
            });
            using var slow = new KrokiClient(new FakeKrokiHandler
            {
                Respond = async (_, cancel) =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancel);
                    return FakeKrokiHandler.Answer(HttpStatusCode.OK, DiagramFakes.Svg);
                },
            }, timeout: TimeSpan.FromMilliseconds(100));

            foreach (var client in new[] { down, refused, slow })
            {
                var result = Draw(client, "https://kroki.io", "d2", "a -> b");

                Assert.Equal("The Kroki server could not be reached (kroki.io).", result.Error);
                Assert.False(result.Lasting);
            }
        }

        [Theory]
        [InlineData("https://kroki.io", "https://kroki.io")]
        [InlineData("https://kroki.io/", "https://kroki.io")]
        [InlineData("  http://localhost:8000  ", "http://localhost:8000")]
        [InlineData("https://diagrams.example.com/kroki", "https://diagrams.example.com/kroki")]
        public void A_server_is_an_http_or_https_address(string text, string normalized)
        {
            Assert.True(KrokiClient.TryParseServer(text, out var server));
            Assert.Equal(normalized, server);
        }

        [Theory]
        [InlineData("ftp://kroki.io")]
        [InlineData("kroki.io")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("https://user:secret@kroki.io")]
        [InlineData("https://kroki.io/?q=1")]
        [InlineData("javascript:alert(1)")]
        [InlineData("file:///C:/notes")]
        public void Anything_else_is_refused(string? text) => Assert.False(KrokiClient.TryParseServer(text, out _));

        [Theory]
        [InlineData("https://kroki.io", "kroki.io")]
        [InlineData("http://localhost:8000/kroki", "localhost:8000")]
        public void The_host_names_the_server_in_messages(string server, string host) => Assert.Equal(host, KrokiClient.HostOf(server));

        [Fact]
        public void The_renderer_fetches_a_kroki_picture_then_draws_it_on_paper()
        {
            var page = new FakePage { Answer = _ => DiagramFakes.Drawn(80, 40) };
            var handler = new FakeKrokiHandler
            {
                Respond = (_, _) => Task.FromResult(FakeKrokiHandler.Answer(HttpStatusCode.OK, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"40\"/>")),
            };
            using var renderer = new DiagramRenderer(() => Task.FromResult<IDiagramPage>(page), kroki: new KrokiClient(handler));
            var request = DiagramFakes.Request("puml", "@startuml\na -> b\n@enduml", PadThemes.Dark, "https://kroki.io");

            var result = Wait(renderer.RenderAsync(request, new object()));

            Assert.True(result.IsPicture);
            Assert.True(result.Paper);
            Assert.Equal("https://kroki.io/plantuml/svg", Assert.Single(handler.Requests).Uri.AbsoluteUri);
            var drawn = Assert.Single(page.Requests);
            Assert.Equal("svg", drawn.Kind);
            Assert.Equal("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"40\"/>", drawn.Source);
            Assert.False(drawn.Dark);
            Assert.True(renderer.TryGetCached(request.Key, out _));
        }

        [Fact]
        public void An_unreachable_server_is_not_cached_and_the_page_is_not_asked()
        {
            var page = new FakePage { Answer = _ => DiagramFakes.Drawn() };
            var handler = new FakeKrokiHandler
            {
                Respond = (_, _) => Task.FromResult(FakeKrokiHandler.Answer(HttpStatusCode.BadGateway, "")),
            };
            using var renderer = new DiagramRenderer(() => Task.FromResult<IDiagramPage>(page), kroki: new KrokiClient(handler));
            var request = DiagramFakes.Request("d2", "a -> b", server: "https://kroki.io");

            var result = Wait(renderer.RenderAsync(request, new object()));

            Assert.Equal("The Kroki server could not be reached (kroki.io).", result.Error);
            Assert.False(renderer.TryGetCached(request.Key, out _));
            Assert.Empty(page.Requests);
        }

        [Fact]
        public void A_kroki_syntax_error_is_cached()
        {
            var handler = new FakeKrokiHandler
            {
                Respond = (_, _) => Task.FromResult(FakeKrokiHandler.Answer(HttpStatusCode.BadRequest, "Error: unexpected token")),
            };
            using var renderer = new DiagramRenderer(() => Task.FromResult<IDiagramPage>(new FakePage()), kroki: new KrokiClient(handler));
            var request = DiagramFakes.Request("d2", "a ->", server: "https://kroki.io");

            Assert.Equal("Error: unexpected token", Wait(renderer.RenderAsync(request, new object())).Error);
            Assert.True(renderer.TryGetCached(request.Key, out _));
        }

        [Fact]
        public void An_answer_that_is_no_picture_is_not_cached()
        {
            var page = new FakePage { Answer = r => new PageDrawing(null, null, 0, 0, DiagramText.CouldNotRead) };
            var handler = new FakeKrokiHandler
            {
                Respond = (_, _) => Task.FromResult(FakeKrokiHandler.Answer(HttpStatusCode.OK, "<html>Sign in to the network</html>")),
            };
            using var renderer = new DiagramRenderer(() => Task.FromResult<IDiagramPage>(page), kroki: new KrokiClient(handler));
            var request = DiagramFakes.Request("d2", "a -> b", server: "https://kroki.io");

            var result = Wait(renderer.RenderAsync(request, new object()));

            Assert.Equal(DiagramText.CouldNotRead, result.Error);
            Assert.False(result.Lasting);
            Assert.False(renderer.TryGetCached(request.Key, out _));
        }
    }
}
