using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Conference;

/// <summary>Uploads bounded in-memory WAV chunks to the selected BMS speech recognizer.</summary>
public sealed class BmsAsrClient : IMeetingAsr, IDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    internal const int MaxResponseBytes = 64 * 1024;
    internal const int MaxTextLength = 16 * 1024;

    private const string Asr2Model = "Qwen/Qwen3-ASR-1.7B";
    private const string Asr1Model = "typhoon-asr-realtime";

    private static readonly HashSet<string> QwenLanguages = new(StringComparer.Ordinal)
    {
        "None", "Chinese", "English", "Cantonese", "Arabic", "German", "French", "Spanish",
        "Portuguese", "Indonesian", "Italian", "Korean", "Russian", "Thai", "Vietnamese",
        "Japanese", "Turkish", "Hindi", "Malay", "Dutch", "Swedish", "Danish", "Finnish",
        "Polish", "Czech", "Filipino", "Persian", "Greek", "Romanian", "Hungarian", "Macedonian",
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _requestTimeout;
    private readonly Func<AsrService, string> _baseUrl;

    /// <summary>Uses a private no-redirect client unless an application or test client is supplied.</summary>
    public BmsAsrClient(Func<AsrService, string> baseUrl, HttpClient? httpClient = null)
        : this(baseUrl, httpClient, RequestTimeout)
    {
    }

    internal BmsAsrClient(Func<AsrService, string> baseUrl, HttpClient? httpClient, TimeSpan requestTimeout)
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

    public async Task<string> TranscribeAsync(
        MeetingAudioChunk chunk, AsrService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);

        try
        {
            Uri baseUri = MeetingServiceEndpoints.RequireBaseUrl(_baseUrl(service), "Speech recognition");
            using var request = CreateRequest(chunk.Wav, service, baseUri);
            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new MeetingException("Speech recognition is unavailable.");

            byte[] body = await ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false);
            string text = ReadText(body);
            return service == AsrService.Asr2 ? NormalizeAsr2(text) : EmptyIfWhitespace(text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new MeetingException("Speech recognition timed out.");
        }
        catch (MeetingException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
                                            or InvalidOperationException or NotSupportedException)
        {
            throw new MeetingException("Speech recognition is unavailable.");
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private static HttpRequestMessage CreateRequest(byte[] wav, AsrService service, Uri baseUri)
    {
        string model;
        switch (service)
        {
            case AsrService.Asr2:
                model = Asr2Model;
                break;
            case AsrService.Asr1:
                model = Asr1Model;
                break;
            default:
                throw new MeetingException("The selected speech recognition service is not supported.");
        }

        var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "audio.wav");
        form.Add(new StringContent(model), "model");
        if (service == AsrService.Asr1) form.Add(new StringContent("th"), "language");
        form.Add(new StringContent("json"), "response_format");
        if (service == AsrService.Asr2) form.Add(new StringContent("false"), "stream");
        return new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "audio/transcriptions")) { Content = form };
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
            throw InvalidResponse();

        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > MaxResponseBytes) throw InvalidResponse();
            output.Write(buffer, 0, read);
        }
    }

    private static string ReadText(byte[] body)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("text", out JsonElement value) ||
                value.ValueKind != JsonValueKind.String)
                throw InvalidResponse();

            string text = value.GetString()!;
            if (text.Length > MaxTextLength) throw InvalidResponse();
            return text;
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
    }

    private static string NormalizeAsr2(string text)
    {
        const string prefix = "language ";
        const string delimiter = "<asr_text>";
        if (text.StartsWith(prefix, StringComparison.Ordinal))
        {
            int delimiterAt = text.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal);
            if (delimiterAt >= 0)
            {
                string language = text.Substring(prefix.Length, delimiterAt - prefix.Length);
                if (QwenLanguages.Contains(language))
                    return EmptyIfWhitespace(text.Substring(delimiterAt + delimiter.Length).Trim());
            }
        }
        return EmptyIfWhitespace(text);
    }

    private static string EmptyIfWhitespace(string text) => string.IsNullOrWhiteSpace(text) ? "" : text;

    private static MeetingException InvalidResponse() =>
        new("The speech recognition service returned an invalid response.");
}
