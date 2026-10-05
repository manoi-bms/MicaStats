using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Kil0bitSystemMonitor.Services.Conference;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class MeetingAsrTests
{
    [Fact]
    public async Task Asr2_posts_the_verified_wav_contract_and_returns_recognized_text()
    {
        var server = new AsrServer(_ => Json("{\"text\":\"language English<asr_text>Hello meeting.\"}"));
        using var http = new HttpClient(server);
        using var asr = new BmsAsrClient(AsrUrl, http);
        var chunk = new MeetingAudioChunk(MeetingSource.Microphone, TimeSpan.Zero,
            TimeSpan.FromSeconds(4), [0x52, 0x49, 0x46, 0x46]);

        string text = await asr.TranscribeAsync(chunk, AsrService.Asr2, CancellationToken.None);

        Assert.Equal("Hello meeting.", text);
        Assert.Equal("https://asr-two.example/v1/audio/transcriptions", server.Uri?.AbsoluteUri);
        Assert.Contains("name=model", server.Body, StringComparison.Ordinal);
        Assert.Contains("Qwen/Qwen3-ASR-1.7B", server.Body, StringComparison.Ordinal);
        Assert.Contains("name=response_format", server.Body, StringComparison.Ordinal);
        Assert.Contains("name=stream", server.Body, StringComparison.Ordinal);
        Assert.Contains("false", server.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("name=language", server.Body, StringComparison.Ordinal);
        Assert.Contains("name=file; filename=audio.wav", server.Body, StringComparison.Ordinal);
        Assert.Contains("Content-Type: audio/wav", server.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Asr1_posts_its_documented_defaults_and_leaves_text_plain()
    {
        var server = new AsrServer(_ => Json("{\"text\":\"language Thai<asr_text>spoken literally\"}"));
        using var http = new HttpClient(server);
        using var asr = new BmsAsrClient(AsrUrl, http);

        string text = await asr.TranscribeAsync(Chunk(), AsrService.Asr1, CancellationToken.None);

        Assert.Equal("language Thai<asr_text>spoken literally", text);
        Assert.Equal("https://asr-one.example/custom/v1/audio/transcriptions", server.Uri?.AbsoluteUri);
        Assert.Contains("typhoon-asr-realtime", server.Body, StringComparison.Ordinal);
        Assert.Contains("name=language", server.Body, StringComparison.Ordinal);
        Assert.Contains("th", server.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("name=stream", server.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("language Thai<asr_text>  สวัสดีครับ  ", "สวัสดีครับ")]
    [InlineData("language None<asr_text>", "")]
    [InlineData("language Klingon<asr_text>Qapla'", "language Klingon<asr_text>Qapla'")]
    [InlineData("ordinary <asr_text> words", "ordinary <asr_text> words")]
    [InlineData("preface language English<asr_text>Hello", "preface language English<asr_text>Hello")]
    public async Task Asr2_only_normalizes_an_anchored_known_language_envelope(string providerText, string expected)
    {
        var server = new AsrServer(_ => Json(System.Text.Json.JsonSerializer.Serialize(new { text = providerText })));
        using var asr = new BmsAsrClient(AsrUrl, new HttpClient(server));

        Assert.Equal(expected, await asr.TranscribeAsync(Chunk(), AsrService.Asr2, CancellationToken.None));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"text\":null}")]
    [InlineData("{\"text\":42}")]
    public async Task Invalid_json_shapes_are_safe_failures(string body)
    {
        var server = new AsrServer(_ => Json(body));
        using var asr = new BmsAsrClient(AsrUrl, new HttpClient(server));

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(
            () => asr.TranscribeAsync(Chunk(), AsrService.Asr2, CancellationToken.None));

        Assert.Equal("The speech recognition service returned an invalid response.", error.Message);
        Assert.DoesNotContain(body, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recognized_text_over_the_limit_is_rejected_without_exposing_it()
    {
        string secretText = new('s', BmsAsrClient.MaxTextLength + 1);
        var server = new AsrServer(_ => Json(System.Text.Json.JsonSerializer.Serialize(new { text = secretText })));
        using var asr = new BmsAsrClient(AsrUrl, new HttpClient(server));

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(
            () => asr.TranscribeAsync(Chunk(), AsrService.Asr2, CancellationToken.None));

        Assert.Equal("The speech recognition service returned an invalid response.", error.Message);
        Assert.DoesNotContain(secretText, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Response_bodies_over_the_limit_are_rejected()
    {
        var server = new AsrServer(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(new byte[BmsAsrClient.MaxResponseBytes + 1])),
        });
        using var asr = new BmsAsrClient(AsrUrl, new HttpClient(server));

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(
            () => asr.TranscribeAsync(Chunk(), AsrService.Asr2, CancellationToken.None));

        Assert.Equal("The speech recognition service returned an invalid response.", error.Message);
    }

    [Fact]
    public async Task Provider_errors_are_sanitized_and_are_not_retried()
    {
        const string providerSecret = "internal provider secret 8H3X";
        var server = new AsrServer(_ => Json(providerSecret, HttpStatusCode.BadGateway));
        using var asr = new BmsAsrClient(AsrUrl, new HttpClient(server));

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(
            () => asr.TranscribeAsync(Chunk(), AsrService.Asr2, CancellationToken.None));

        Assert.Equal("Speech recognition is unavailable.", error.Message);
        Assert.DoesNotContain(providerSecret, error.Message, StringComparison.Ordinal);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task Network_exception_details_are_not_exposed()
    {
        const string secret = "socket failed with secret 91J";
        var server = new AsrServer(_ => throw new HttpRequestException(secret));
        using var asr = new BmsAsrClient(AsrUrl, new HttpClient(server));

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(
            () => asr.TranscribeAsync(Chunk(), AsrService.Asr2, CancellationToken.None));

        Assert.Equal("Speech recognition is unavailable.", error.Message);
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        var server = new AsrServer(async (_, cancel) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancel);
            return Json("{}");
        });
        using var asr = new BmsAsrClient(AsrUrl, new HttpClient(server));
        using var caller = new CancellationTokenSource();

        Task<string> pending = asr.TranscribeAsync(Chunk(), AsrService.Asr2, caller.Token);
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task The_request_deadline_becomes_a_safe_timeout()
    {
        var server = new AsrServer(async (_, cancel) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancel);
            return Json("{}");
        });
        using var asr = new BmsAsrClient(AsrUrl, new HttpClient(server), TimeSpan.FromMilliseconds(20));

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(
            () => asr.TranscribeAsync(Chunk(), AsrService.Asr2, CancellationToken.None));

        Assert.Equal("Speech recognition timed out.", error.Message);
        Assert.Equal(1, server.RequestCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://asr.example/v1")]
    [InlineData("https://user:secret@asr.example/v1")]
    [InlineData("https://asr.example/v1?token=secret")]
    public async Task Missing_or_invalid_configured_endpoints_are_blocked_before_http(string endpoint)
    {
        var server = new AsrServer(_ => Json("{\"text\":\"unexpected\"}"));
        using var asr = new BmsAsrClient(_ => endpoint, new HttpClient(server));

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(
            () => asr.TranscribeAsync(Chunk(), AsrService.Asr2, CancellationToken.None));

        Assert.Equal("Speech recognition endpoint is not configured or is invalid.", error.Message);
        if (endpoint.Length > 0) Assert.DoesNotContain(endpoint, error.Message, StringComparison.Ordinal);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task The_configured_endpoint_is_captured_once_for_each_request()
    {
        int reads = 0;
        var server = new AsrServer(_ => Json("{\"text\":\"ok\"}"));
        using var asr = new BmsAsrClient(_ =>
        {
            reads++;
            return "https://asr.example/root/v1";
        }, new HttpClient(server));

        await asr.TranscribeAsync(Chunk(), AsrService.Asr2, CancellationToken.None);

        Assert.Equal(1, reads);
        Assert.Equal("https://asr.example/root/v1/audio/transcriptions", server.Uri?.AbsoluteUri);
    }

    [Fact]
    public void The_default_network_handler_does_not_follow_redirects()
    {
        using HttpMessageHandler handler = BmsAsrClient.CreateHandler();

        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
    }

    [Fact]
    public async Task Disposing_the_adapter_does_not_dispose_an_injected_http_client()
    {
        var server = new AsrServer(_ => Json("{\"text\":\"ok\"}"));
        using var http = new HttpClient(server);
        var asr = new BmsAsrClient(AsrUrl, http);
        asr.Dispose();

        using HttpResponseMessage response = await http.GetAsync("https://caller.example/still-owned-by-caller");

        Assert.True(response.IsSuccessStatusCode);
    }

    private static MeetingAudioChunk Chunk() => new(
        MeetingSource.Microphone, TimeSpan.Zero, TimeSpan.FromSeconds(4), [0x52, 0x49, 0x46, 0x46]);

    private static string AsrUrl(AsrService service) => service switch
    {
        AsrService.Asr1 => "https://asr-one.example/custom/v1",
        AsrService.Asr2 => "https://asr-two.example/v1",
        _ => "",
    };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class AsrServer : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _answer;

        public AsrServer(Func<HttpRequestMessage, HttpResponseMessage> answer)
            : this((request, _) => Task.FromResult(answer(request)))
        {
        }

        public AsrServer(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) => _answer = answer;

        public Uri? Uri { get; private set; }
        public string Body { get; private set; } = "";
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Uri = request.RequestUri;
            Body = request.Content == null ? "" : Encoding.Latin1.GetString(await request.Content.ReadAsByteArrayAsync(cancellationToken));
            return await _answer(request, cancellationToken);
        }
    }
}
