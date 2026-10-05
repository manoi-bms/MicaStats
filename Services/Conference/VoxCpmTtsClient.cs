using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Conference;

/// <summary>Generates one bounded in-memory PCM WAV with the public VoxCPM service.</summary>
public sealed class VoxCpmTtsClient : IMeetingTts, IDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    internal const int MaxInputLength = 4096;
    internal const int MaxAudioBytes = 10 * 1024 * 1024;
    internal const int MaxVoiceBodyBytes = 64 * 1024;
    internal const int MaxVoices = 200;
    internal const int MaxVoiceIdLength = 128;
    internal const int MaxVoiceNameLength = 256;

    private static readonly HashSet<int> PcmSampleRates =
        [8000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 88200, 96000, 176400, 192000];

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _requestTimeout;
    private readonly Func<string> _baseUrl;
    private readonly object _voicesGate = new();
    private HashSet<string>? _validatedVoices;
    private string? _validatedVoicesEndpoint;
    private long _voiceRefreshSequence;

    public VoxCpmTtsClient(Func<string> baseUrl, HttpClient? httpClient = null)
        : this(baseUrl, httpClient, RequestTimeout)
    {
    }

    internal VoxCpmTtsClient(Func<string> baseUrl, HttpClient? httpClient, TimeSpan requestTimeout)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _baseUrl = baseUrl;
        _ownsHttp = httpClient == null;
        _http = httpClient ?? new HttpClient(CreateHandler());
        if (_ownsHttp) _http.Timeout = Timeout.InfiniteTimeSpan;
        _requestTimeout = requestTimeout;
    }

    internal static HttpMessageHandler CreateHandler() => new SocketsHttpHandler { AllowAutoRedirect = false };

    public async Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new MeetingException("Enter text to speak.");
        if (text.Length > MaxInputLength) throw new MeetingException("Speech text is limited to 4,096 characters.");
        string selectedVoice = string.IsNullOrWhiteSpace(voice) ? "default" : voice.Trim();
        Uri baseUri = MeetingServiceEndpoints.RequireBaseUrl(_baseUrl(), "Speech");
        ValidateVoice(selectedVoice, baseUri.AbsoluteUri);
        cancellationToken.ThrowIfCancellationRequested();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        try
        {
            string json = JsonSerializer.Serialize(new
            {
                input = text,
                model = "voxcpm-thai",
                voice = selectedVoice,
                response_format = "wav",
                speed = 1.0,
                stream = false,
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "audio/speech"))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw Unavailable();
            if (!IsMediaType(response.Content, "audio/wav")) throw InvalidAudio();
            byte[] wav = await ReadBoundedAsync(response.Content, MaxAudioBytes, InvalidAudio, deadline.Token)
                .ConfigureAwait(false);
            ValidatePcmWav(wav);
            return wav;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new MeetingException("Speech generation timed out. Try again.");
        }
        catch (MeetingException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
                                            or InvalidOperationException or NotSupportedException)
        {
            throw Unavailable();
        }
    }

    public async Task<IReadOnlyList<MeetingVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Uri baseUri = MeetingServiceEndpoints.RequireBaseUrl(_baseUrl(), "Speech");
        long refreshSequence = Interlocked.Increment(ref _voiceRefreshSequence);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "voices"));
            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw Unavailable();
            if (!IsJson(response.Content)) throw InvalidCatalog();
            byte[] body = await ReadBoundedAsync(response.Content, MaxVoiceBodyBytes, InvalidCatalog, deadline.Token)
                .ConfigureAwait(false);
            IReadOnlyList<MeetingVoice> voices = ParseVoices(body);
            lock (_voicesGate)
            {
                if (refreshSequence == _voiceRefreshSequence)
                {
                    _validatedVoices = new HashSet<string>(voices.Select(item => item.Id), StringComparer.Ordinal);
                    _validatedVoicesEndpoint = baseUri.AbsoluteUri;
                }
            }
            return voices;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new MeetingException("Refreshing speech voices timed out. Try again.");
        }
        catch (MeetingException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
                                            or InvalidOperationException or NotSupportedException or JsonException)
        {
            throw Unavailable();
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private void ValidateVoice(string voice, string endpoint)
    {
        if (voice == "default") return;
        HashSet<string>? voices;
        string? catalogEndpoint;
        lock (_voicesGate)
        {
            voices = _validatedVoices;
            catalogEndpoint = _validatedVoicesEndpoint;
        }
        if (voices == null || !string.Equals(catalogEndpoint, endpoint, StringComparison.Ordinal))
            throw new MeetingException("Refresh voices before using a non-default speech voice.");
        if (!voices.Contains(voice))
            throw new MeetingException("Choose a voice from the refreshed speech voice list.");
    }

    private static IReadOnlyList<MeetingVoice> ParseVoices(byte[] body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("voices", out JsonElement list)) throw InvalidCatalog();
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaxVoices) throw InvalidCatalog();

            var result = new List<MeetingVoice>(list.GetArrayLength());
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw InvalidCatalog();
                string id = RequiredCatalogString(item, "id", MaxVoiceIdLength);
                string name = RequiredCatalogString(item, "name", MaxVoiceNameLength);

                if (!ValidCatalogText(id, MaxVoiceIdLength) || !ValidCatalogText(name, MaxVoiceNameLength) || !ids.Add(id))
                    throw InvalidCatalog();
                result.Add(new MeetingVoice(id, name));
            }

            if (!ids.Contains("default")) throw InvalidCatalog();
            return result;
        }
        catch (JsonException)
        {
            throw InvalidCatalog();
        }
        catch (InvalidOperationException)
        {
            throw InvalidCatalog();
        }
    }

    private static string RequiredCatalogString(JsonElement item, string name, int maxLength)
    {
        if (!item.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw InvalidCatalog();
        string text = value.GetString()!;
        if (!ValidCatalogText(text, maxLength)) throw InvalidCatalog();
        return text;
    }

    private static bool ValidCatalogText(string text, int maxLength)
    {
        if (text.Length == 0 || text.Length > maxLength || text != text.Trim()) return false;
        foreach (char c in text)
            if (char.IsControl(c)) return false;
        return true;
    }

    private static bool IsMediaType(HttpContent content, string expected) =>
        string.Equals(content.Headers.ContentType?.MediaType, expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsJson(HttpContent content)
    {
        string? type = content.Headers.ContentType?.MediaType;
        return string.Equals(type, "application/json", StringComparison.OrdinalIgnoreCase) ||
               (type?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maxBytes,
        Func<MeetingException> invalid, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes) throw invalid();
        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maxBytes) throw invalid();
            output.Write(buffer, 0, read);
        }
    }

    private static void ValidatePcmWav(byte[] wav)
    {
        if (wav.Length < 44 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8) ||
            (ulong)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(4, 4)) + 8UL != (ulong)wav.Length)
            throw InvalidAudio();

        bool foundFormat = false, foundData = false;
        int channels = 0, sampleRate = 0, byteRate = 0, blockAlign = 0, bits = 0, dataBytes = 0;
        int offset = 12;
        while (offset < wav.Length)
        {
            if (wav.Length - offset < 8) throw InvalidAudio();
            ReadOnlySpan<byte> id = wav.AsSpan(offset, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(offset + 4, 4));
            ulong payloadAt = (ulong)offset + 8;
            ulong end = payloadAt + size;
            ulong paddedEnd = end + (size & 1U);
            if (end > (ulong)wav.Length || paddedEnd > (ulong)wav.Length) throw InvalidAudio();

            if (id.SequenceEqual("fmt "u8))
            {
                if (foundFormat || size < 16) throw InvalidAudio();
                ReadOnlySpan<byte> format = wav.AsSpan((int)payloadAt, 16);
                if (BinaryPrimitives.ReadUInt16LittleEndian(format) != 1) throw InvalidAudio();
                channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
                uint unsignedSampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format[4..]);
                uint unsignedByteRate = BinaryPrimitives.ReadUInt32LittleEndian(format[8..]);
                if (unsignedSampleRate > int.MaxValue || unsignedByteRate > int.MaxValue) throw InvalidAudio();
                sampleRate = (int)unsignedSampleRate;
                byteRate = (int)unsignedByteRate;
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
                if (channels is < 1 or > 2 || !PcmSampleRates.Contains(sampleRate) ||
                    bits != 16 || blockAlign != channels * (bits / 8) ||
                    byteRate != sampleRate * blockAlign)
                    throw InvalidAudio();
                foundFormat = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (foundData || size == 0 || size > MaxAudioBytes) throw InvalidAudio();
                dataBytes = (int)size;
                foundData = true;
            }
            offset = (int)paddedEnd;
        }

        if (!foundFormat || !foundData || dataBytes % blockAlign != 0 ||
            (long)dataBytes > (long)byteRate * 90)
            throw InvalidAudio();
    }

    private static MeetingException InvalidAudio() =>
        new("The speech service returned invalid audio. Try another voice or try again.");
    private static MeetingException InvalidCatalog() =>
        new("The speech service returned an invalid voice list. Try refreshing again.");
    private static MeetingException Unavailable() =>
        new("The speech service is unavailable. Try again.");
}
