using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kil0bitSystemMonitor.Services.Conference;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class MeetingTtsTests
{
    [Fact]
    public async Task Default_voice_posts_the_verified_contract_and_returns_valid_pcm_wav()
    {
        byte[] wav = PcmWav(sampleRate: 48_000, channels: 1, bits: 16, dataBytes: 9_600);
        var server = new TtsServer((request, _) => Task.FromResult(Wav(wav)));
        using var http = new HttpClient(server);
        using var tts = new VoxCpmTtsClient(TtsUrl, http);

        byte[] result = await tts.SynthesizeAsync("สวัสดี", "default", CancellationToken.None);

        Assert.Equal(wav, result);
        Assert.Equal(HttpMethod.Post, server.Method);
        Assert.Equal("https://speech.example/custom/v1/audio/speech", server.Uri?.AbsoluteUri);
        using JsonDocument body = JsonDocument.Parse(server.Body);
        JsonElement root = body.RootElement;
        Assert.Equal("สวัสดี", root.GetProperty("input").GetString());
        Assert.Equal("voxcpm-thai", root.GetProperty("model").GetString());
        Assert.Equal("default", root.GetProperty("voice").GetString());
        Assert.Equal("wav", root.GetProperty("response_format").GetString());
        Assert.Equal(1d, root.GetProperty("speed").GetDouble());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("application/json; charset=utf-8", server.ContentType);
    }

    [Fact]
    public async Task Refresh_uses_the_verified_catalog_and_enables_a_nondefault_voice()
    {
        byte[] wav = PcmWav(48_000, 1, 16, 9_600);
        var server = new TtsServer((request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? Json("""{"voices":[{"id":"default","name":"Default (no cloning)","gender":null},{"id":"female_sofia","name":"Sofia","gender":"female"}]}""")
            : Wav(wav)));
        using var tts = new VoxCpmTtsClient(TtsUrl, new HttpClient(server));

        IReadOnlyList<MeetingVoice> voices = await tts.GetVoicesAsync(CancellationToken.None);
        Assert.Equal("https://speech.example/custom/v1/voices", server.Uri?.AbsoluteUri);
        byte[] spoken = await tts.SynthesizeAsync("hello", "female_sofia", CancellationToken.None);

        Assert.Equal([new MeetingVoice("default", "Default (no cloning)"), new MeetingVoice("female_sofia", "Sofia")], voices);
        Assert.Equal(wav, spoken);
        Assert.Equal(2, server.RequestCount);
        Assert.Equal("female_sofia", JsonDocument.Parse(server.Body).RootElement.GetProperty("voice").GetString());
    }

    [Fact]
    public async Task A_nondefault_voice_requires_refresh_before_any_speech_request()
    {
        var server = new TtsServer((_, _) => Task.FromResult(Wav(PcmWav(48_000, 1, 16, 960))));
        using var tts = new VoxCpmTtsClient(TtsUrl, new HttpClient(server));

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(
            () => tts.SynthesizeAsync("hello", "female_sofia", CancellationToken.None));

        Assert.Equal("Refresh voices before using a non-default speech voice.", error.Message);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task A_successful_refresh_removing_a_voice_invalidates_it_before_post()
    {
        int refresh = 0;
        var server = new TtsServer((request, _) =>
        {
            if (request.Method != HttpMethod.Get) return Task.FromResult(Wav(PcmWav(48_000, 1, 16, 960)));
            refresh++;
            return Task.FromResult(Json(refresh == 1
                ? "{\"voices\":[{\"id\":\"default\",\"name\":\"Default\"},{\"id\":\"female\",\"name\":\"Female\"}]}"
                : "{\"voices\":[{\"id\":\"default\",\"name\":\"Default\"}]}"));
        });
        using var tts = new VoxCpmTtsClient(TtsUrl, new HttpClient(server));
        await tts.GetVoicesAsync(CancellationToken.None);
        await tts.GetVoicesAsync(CancellationToken.None);

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(
            () => tts.SynthesizeAsync("hello", "female", CancellationToken.None));

        Assert.Equal("Choose a voice from the refreshed speech voice list.", error.Message);
        Assert.Equal(2, server.RequestCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_speech_is_rejected_before_network_use(string text)
    {
        var server = new TtsServer((_, _) => Task.FromResult(Wav(PcmWav(48_000, 1, 16, 960))));
        using var tts = new VoxCpmTtsClient(TtsUrl, new HttpClient(server));

        await Assert.ThrowsAsync<MeetingException>(() => tts.SynthesizeAsync(text, "default", CancellationToken.None));

        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task Speech_over_4096_characters_is_rejected_without_truncation_or_network_use()
    {
        var server = new TtsServer((_, _) => Task.FromResult(Wav(PcmWav(48_000, 1, 16, 960))));
        using var tts = new VoxCpmTtsClient(TtsUrl, new HttpClient(server));

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(() =>
            tts.SynthesizeAsync(new string('x', VoxCpmTtsClient.MaxInputLength + 1), "default", CancellationToken.None));

        Assert.Equal("Speech text is limited to 4,096 characters.", error.Message);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task Non_wav_content_is_rejected_without_exposing_its_body()
    {
        const string secret = "provider detail secret Y8K";
        var server = new TtsServer((_, _) => Task.FromResult(Json(secret)));
        using var tts = new VoxCpmTtsClient(TtsUrl, new HttpClient(server));

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(
            () => tts.SynthesizeAsync("hello", "default", CancellationToken.None));

        Assert.Equal("The speech service returned invalid audio. Try another voice or try again.", error.Message);
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_over_10MiB_is_rejected_even_without_content_length()
    {
        var server = new TtsServer((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(new byte[VoxCpmTtsClient.MaxAudioBytes + 1])),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            return Task.FromResult(response);
        });
        using var tts = new VoxCpmTtsClient(TtsUrl, new HttpClient(server));

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(
            () => tts.SynthesizeAsync("hello", "default", CancellationToken.None));

        Assert.Equal("The speech service returned invalid audio. Try another voice or try again.", error.Message);
    }

    [Fact]
    public async Task Pcm16_at_the_supported_duration_boundary_is_accepted_and_longer_audio_is_rejected()
    {
        byte[] ninetySeconds = PcmWav(8_000, 1, 16, 8_000 * 2 * 90);
        var acceptedServer = new TtsServer((_, _) => Task.FromResult(Wav(ninetySeconds)));
        using (var accepted = new VoxCpmTtsClient(TtsUrl, new HttpClient(acceptedServer)))
            Assert.Equal(ninetySeconds, await accepted.SynthesizeAsync("hello", "default", CancellationToken.None));

        byte[] tooLong = PcmWav(8_000, 1, 16, 8_000 * 2 * 90 + 2);
        var rejectedServer = new TtsServer((_, _) => Task.FromResult(Wav(tooLong)));
        using var rejected = new VoxCpmTtsClient(TtsUrl, new HttpClient(rejectedServer));

        await Assert.ThrowsAsync<MeetingException>(
            () => rejected.SynthesizeAsync("hello", "default", CancellationToken.None));
    }

    [Fact]
    public async Task Malformed_or_unsupported_wav_structures_are_rejected()
    {
        var invalid = new List<byte[]>();
        byte[] wrongRiff = PcmWav(48_000, 1, 16, 960); wrongRiff[0] = (byte)'X'; invalid.Add(wrongRiff);
        byte[] wrongLength = PcmWav(48_000, 1, 16, 960); BinaryPrimitives.WriteUInt32LittleEndian(wrongLength.AsSpan(4), 1); invalid.Add(wrongLength);
        byte[] compressed = PcmWav(48_000, 1, 16, 960); BinaryPrimitives.WriteUInt16LittleEndian(compressed.AsSpan(20), 3); invalid.Add(compressed);
        invalid.Add(PcmWav(48_000, 1, 24, 960));
        invalid.Add(PcmWav(12_345, 1, 16, 960));
        invalid.Add(PcmWav(48_000, 3, 16, 960));
        byte[] wrongAlign = PcmWav(48_000, 1, 16, 960); BinaryPrimitives.WriteUInt16LittleEndian(wrongAlign.AsSpan(32), 4); invalid.Add(wrongAlign);
        byte[] wrongRate = PcmWav(48_000, 1, 16, 960); BinaryPrimitives.WriteUInt32LittleEndian(wrongRate.AsSpan(28), 1); invalid.Add(wrongRate);
        invalid.Add(PcmWav(48_000, 1, 16, 0));
        invalid.Add(PcmWav(48_000, 2, 16, 962));

        foreach (byte[] wav in invalid)
        {
            var server = new TtsServer((_, _) => Task.FromResult(Wav(wav)));
            using var tts = new VoxCpmTtsClient(TtsUrl, new HttpClient(server));
            await Assert.ThrowsAsync<MeetingException>(
                () => tts.SynthesizeAsync("hello", "default", CancellationToken.None));
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"voices\":{}}")]
    [InlineData("{\"voices\":[{\"id\":\"default\"}]}")]
    [InlineData("{\"voices\":[{\"id\":\"default\",\"name\":7}]}")]
    [InlineData("{\"voices\":[{\"id\":\"female\",\"name\":\"Female\"}]}")]
    public async Task Malformed_voice_catalogs_are_safe_failures(string body)
    {
        var server = new TtsServer((_, _) => Task.FromResult(Json(body)));
        using var tts = new VoxCpmTtsClient(TtsUrl, new HttpClient(server));

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(
            () => tts.GetVoicesAsync(CancellationToken.None));

        Assert.Equal("The speech service returned an invalid voice list. Try refreshing again.", error.Message);
        Assert.DoesNotContain(body, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Voice_catalog_body_and_item_counts_are_bounded()
    {
        var oversizedBodyServer = new TtsServer((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(new byte[VoxCpmTtsClient.MaxVoiceBodyBytes + 1])),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        });
        using (var tts = new VoxCpmTtsClient(TtsUrl, new HttpClient(oversizedBodyServer)))
            await Assert.ThrowsAsync<MeetingException>(() => tts.GetVoicesAsync(CancellationToken.None));

        string items = string.Join(',', Enumerable.Range(0, VoxCpmTtsClient.MaxVoices + 1)
            .Select(index => "{\"id\":\"voice" + index + "\",\"name\":\"Voice\"}"));
        var tooManyServer = new TtsServer((_, _) => Task.FromResult(Json("{\"voices\":[" + items + "]}")));
        using var tooMany = new VoxCpmTtsClient(TtsUrl, new HttpClient(tooManyServer));

        await Assert.ThrowsAsync<MeetingException>(() => tooMany.GetVoicesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Provider_failures_are_sanitized_and_never_retried()
    {
        const string secret = "upstream secret V4P";
        var server = new TtsServer((_, _) => Task.FromResult(Json(secret, HttpStatusCode.BadGateway)));
        using var tts = new VoxCpmTtsClient(TtsUrl, new HttpClient(server));

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(
            () => tts.SynthesizeAsync("hello", "default", CancellationToken.None));

        Assert.Equal("The speech service is unavailable. Try again.", error.Message);
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new TtsServer(async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Wav(PcmWav(48_000, 1, 16, 960));
        });
        using var tts = new VoxCpmTtsClient(TtsUrl, new HttpClient(server));
        using var caller = new CancellationTokenSource();

        Task<byte[]> pending = tts.SynthesizeAsync("hello", "default", caller.Token);
        await started.Task;
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task The_request_deadline_has_a_safe_testable_timeout_path()
    {
        var server = new TtsServer(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Wav(PcmWav(48_000, 1, 16, 960));
        });
        using var tts = new VoxCpmTtsClient(TtsUrl, new HttpClient(server), TimeSpan.FromMilliseconds(20));

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(
            () => tts.SynthesizeAsync("hello", "default", CancellationToken.None));

        Assert.Equal("Speech generation timed out. Try again.", error.Message);
        Assert.Equal(1, server.RequestCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("file:///speech")]
    [InlineData("https://user:secret@speech.example/v1")]
    [InlineData("https://speech.example/v1#private")]
    public async Task Missing_or_invalid_configured_endpoints_are_blocked_before_http(string endpoint)
    {
        var server = new TtsServer((_, _) => Task.FromResult(Wav(PcmWav(48_000, 1, 16, 960))));
        using var tts = new VoxCpmTtsClient(() => endpoint, new HttpClient(server));

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(
            () => tts.SynthesizeAsync("hello", "default", CancellationToken.None));

        Assert.Equal("Speech endpoint is not configured or is invalid.", error.Message);
        if (endpoint.Length > 0) Assert.DoesNotContain(endpoint, error.Message, StringComparison.Ordinal);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task Changing_the_endpoint_requires_a_new_voice_catalog()
    {
        string endpoint = "https://speech-one.example/v1";
        byte[] wav = PcmWav(48_000, 1, 16, 960);
        var server = new TtsServer((request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? Json("{\"voices\":[{\"id\":\"default\",\"name\":\"Default\"},{\"id\":\"custom\",\"name\":\"Custom\"}]}")
            : Wav(wav)));
        using var tts = new VoxCpmTtsClient(() => endpoint, new HttpClient(server));
        await tts.GetVoicesAsync(CancellationToken.None);
        endpoint = "https://speech-two.example/v1";

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(
            () => tts.SynthesizeAsync("hello", "custom", CancellationToken.None));

        Assert.Equal("Refresh voices before using a non-default speech voice.", error.Message);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task A_stale_catalog_response_cannot_replace_a_newer_catalog()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int request = 0;
        var server = new TtsServer(async (_, cancellationToken) =>
        {
            int current = Interlocked.Increment(ref request);
            if (current == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return Json("{\"voices\":[{\"id\":\"default\",\"name\":\"Default\"},{\"id\":\"old\",\"name\":\"Old\"}]}");
            }
            return Json("{\"voices\":[{\"id\":\"default\",\"name\":\"Default\"},{\"id\":\"new\",\"name\":\"New\"}]}");
        });
        using var tts = new VoxCpmTtsClient(TtsUrl, new HttpClient(server));

        Task<IReadOnlyList<MeetingVoice>> stale = tts.GetVoicesAsync(CancellationToken.None);
        await firstStarted.Task;
        await tts.GetVoicesAsync(CancellationToken.None);
        releaseFirst.SetResult();
        await stale;

        MeetingException oldError = await Assert.ThrowsAsync<MeetingException>(
            () => tts.SynthesizeAsync("hello", "old", CancellationToken.None));
        Assert.Equal("Choose a voice from the refreshed speech voice list.", oldError.Message);
    }

    [Fact]
    public void The_default_network_handler_never_follows_redirects()
    {
        using HttpMessageHandler handler = VoxCpmTtsClient.CreateHandler();

        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
    }

    [Fact]
    public async Task Disposing_the_adapter_does_not_dispose_an_injected_http_client()
    {
        var server = new TtsServer((_, _) => Task.FromResult(Json("{\"voices\":[]}")));
        using var http = new HttpClient(server);
        var tts = new VoxCpmTtsClient(TtsUrl, http);
        tts.Dispose();

        using HttpResponseMessage response = await http.GetAsync("https://caller.example/still-owned-by-caller");

        Assert.True(response.IsSuccessStatusCode);
    }

    private static string TtsUrl() => "https://speech.example/custom/v1";

    private static HttpResponseMessage Wav(byte[] wav, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(wav) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        return response;
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static byte[] PcmWav(int sampleRate, short channels, short bits, int dataBytes)
    {
        byte[] wav = new byte[44 + dataBytes];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(wav.Length - 8));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), (uint)sampleRate);
        short blockAlign = (short)(channels * (bits / 8));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), (ushort)bits);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), (uint)dataBytes);
        return wav;
    }

    private sealed class TtsServer(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string Body { get; private set; } = "";
        public string? ContentType { get; private set; }
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Method = request.Method;
            Uri = request.RequestUri;
            ContentType = request.Content?.Headers.ContentType?.ToString();
            Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return await answer(request, cancellationToken);
        }
    }
}
