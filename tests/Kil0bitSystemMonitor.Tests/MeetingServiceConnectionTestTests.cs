using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kil0bitSystemMonitor.Services.Conference;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class MeetingServiceConnectionTestTests
{
    [Theory]
    [InlineData(0, "Qwen/Qwen3-ASR-1.7B", false)]
    [InlineData(1, "typhoon-asr-realtime", true)]
    public async Task Asr_probe_posts_one_second_pcm_silence_with_the_selected_profile(
        int kindValue, string model, bool includesLanguage)
    {
        var kind = (MeetingServiceKind)kindValue;
        var server = new ProbeServer(_ => Json("{\"text\":\"\"}"));
        using var http = new HttpClient(server);

        string result = await MeetingServiceConnectionTest.RunAsync(
            kind, "https://speech.example/custom/v1", CancellationToken.None, http);

        Assert.Contains("transcription protocol", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("https://speech.example/custom/v1/audio/transcriptions", server.Uri?.AbsoluteUri);
        Assert.Equal(1, server.RequestCount);
        Assert.Contains(model, server.BodyText, StringComparison.Ordinal);
        Assert.Equal(includesLanguage, server.BodyText.Contains("name=language", StringComparison.Ordinal));

        int waveAt = server.BodyBytes.AsSpan().IndexOf("RIFF"u8);
        Assert.True(waveAt >= 0);
        AssertSilenceWave(server.BodyBytes, waveAt);
    }

    [Fact]
    public async Task Tts_probe_posts_fixed_text_and_discards_a_valid_wav()
    {
        var server = new ProbeServer(_ => Wav(PcmWav()));
        using var http = new HttpClient(server);

        string result = await MeetingServiceConnectionTest.RunAsync(
            MeetingServiceKind.Tts, "https://speech.example/root/v1", CancellationToken.None, http);

        Assert.Contains("valid WAV audio", result, StringComparison.Ordinal);
        Assert.Equal("https://speech.example/root/v1/audio/speech", server.Uri?.AbsoluteUri);
        using JsonDocument body = JsonDocument.Parse(server.BodyBytes);
        Assert.Equal("Connection test.", body.RootElement.GetProperty("input").GetString());
        Assert.Equal("default", body.RootElement.GetProperty("voice").GetString());
        Assert.Equal(1, server.RequestCount);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "ftp://speech.example/v1")]
    [InlineData(2, "https://user:secret@speech.example/v1")]
    public async Task Invalid_endpoints_are_rejected_before_http(int kindValue, string endpoint)
    {
        var kind = (MeetingServiceKind)kindValue;
        var server = new ProbeServer(_ => throw new InvalidOperationException("must not be called"));
        using var http = new HttpClient(server);

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(() =>
            MeetingServiceConnectionTest.RunAsync(kind, endpoint, CancellationToken.None, http));

        Assert.Contains("endpoint is not configured or is invalid", error.Message, StringComparison.Ordinal);
        if (endpoint.Length > 0) Assert.DoesNotContain(endpoint, error.Message, StringComparison.Ordinal);
        Assert.Equal(0, server.RequestCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Provider_errors_are_sanitized_and_never_retried(int kindValue)
    {
        var kind = (MeetingServiceKind)kindValue;
        const string secret = "private provider failure 74Z";
        var server = new ProbeServer(_ => Json(secret, HttpStatusCode.BadGateway));
        using var http = new HttpClient(server);

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(() =>
            MeetingServiceConnectionTest.RunAsync(
                kind, "https://speech.example/v1", CancellationToken.None, http));

        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("speech.example", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, server.RequestCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Invalid_protocol_responses_are_safe_failures(int kindValue)
    {
        var kind = (MeetingServiceKind)kindValue;
        const string secret = "invalid private response 12Q";
        var server = new ProbeServer(_ => Json(secret));
        using var http = new HttpClient(server);

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(() =>
            MeetingServiceConnectionTest.RunAsync(
                kind, "https://speech.example/v1", CancellationToken.None, http));

        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("speech.example", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new ProbeServer(async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Json("{\"text\":\"\"}");
        });
        using var http = new HttpClient(server);
        using var caller = new CancellationTokenSource();

        Task<string> pending = MeetingServiceConnectionTest.RunAsync(
            MeetingServiceKind.Asr2, "https://speech.example/v1", caller.Token, http);
        await started.Task;
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Deadline_has_a_safe_testable_timeout_path()
    {
        var server = new ProbeServer(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Json("{\"text\":\"\"}");
        });
        using var http = new HttpClient(server);

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(() =>
            MeetingServiceConnectionTest.RunAsync(
                MeetingServiceKind.Asr1,
                "https://speech.example/v1",
                CancellationToken.None,
                TimeSpan.FromMilliseconds(20),
                http));

        Assert.Equal("Connection test timed out.", error.Message);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Wav(byte[] wav)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(wav) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        return response;
    }

    private static byte[] PcmWav() => MeetingPcmAdapter.CreateWave(new short[1_600]);

    private static void AssertSilenceWave(byte[] body, int waveAt)
    {
        ReadOnlySpan<byte> wav = body.AsSpan(waveAt, 44 + 32_000);
        Assert.True(wav[..4].SequenceEqual("RIFF"u8));
        Assert.True(wav.Slice(8, 4).SequenceEqual("WAVE"u8));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.Slice(22, 2)));
        Assert.Equal(16_000, BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(24, 4)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.Slice(34, 2)));
        Assert.Equal(32_000, BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(40, 4)));
        Assert.True(wav[44..].IndexOfAnyExcept((byte)0) < 0);
    }

    private sealed class ProbeServer : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _answer;

        internal ProbeServer(Func<HttpRequestMessage, HttpResponseMessage> answer)
            : this((request, _) => Task.FromResult(answer(request))) { }

        internal ProbeServer(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) =>
            _answer = answer;

        internal int RequestCount { get; private set; }
        internal Uri? Uri { get; private set; }
        internal byte[] BodyBytes { get; private set; } = [];
        internal string BodyText => Encoding.UTF8.GetString(BodyBytes);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Uri = request.RequestUri;
            BodyBytes = request.Content == null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return await _answer(request, cancellationToken);
        }
    }
}
