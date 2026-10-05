using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Conference;

internal enum MeetingServiceKind { Asr2, Asr1, Tts }

/// <summary>Runs an explicit, bounded protocol probe against one configured meeting service.</summary>
internal static class MeetingServiceConnectionTest
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private const string TestSpeech = "Connection test.";
    private const int SilenceSamples = 16_000;

    internal static Task<string> RunAsync(
        MeetingServiceKind kind,
        string? baseUrl,
        CancellationToken cancellationToken,
        HttpClient? httpClient = null) =>
        RunAsync(kind, baseUrl, cancellationToken, Timeout, httpClient);

    internal static async Task<string> RunAsync(
        MeetingServiceKind kind,
        string? baseUrl,
        CancellationToken cancellationToken,
        TimeSpan timeout,
        HttpClient? httpClient = null)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(timeout));

        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            return kind switch
            {
                MeetingServiceKind.Asr2 => await TestAsrAsync(
                    AsrService.Asr2, baseUrl, deadline.Token, ClientTimeout(timeout), httpClient).ConfigureAwait(false),
                MeetingServiceKind.Asr1 => await TestAsrAsync(
                    AsrService.Asr1, baseUrl, deadline.Token, ClientTimeout(timeout), httpClient).ConfigureAwait(false),
                MeetingServiceKind.Tts => await TestTtsAsync(
                    baseUrl, deadline.Token, ClientTimeout(timeout), httpClient).ConfigureAwait(false),
                _ => throw new MeetingException("The selected meeting service is not supported."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new MeetingException("Connection test timed out.");
        }
    }

    private static async Task<string> TestAsrAsync(
        AsrService service,
        string? baseUrl,
        CancellationToken cancellationToken,
        TimeSpan clientTimeout,
        HttpClient? httpClient)
    {
        using var client = new BmsAsrClient(_ => baseUrl ?? "", httpClient, clientTimeout);
        byte[] wav = MeetingPcmAdapter.CreateWave(new short[SilenceSamples]);
        var chunk = new MeetingAudioChunk(
            MeetingSource.Microphone, TimeSpan.Zero, TimeSpan.FromSeconds(1), wav);

        _ = await client.TranscribeAsync(chunk, service, cancellationToken).ConfigureAwait(false);
        return service == AsrService.Asr2
            ? "ASR2 connection succeeded. The transcription protocol accepted test audio."
            : "ASR1 connection succeeded. The transcription protocol accepted test audio.";
    }

    private static async Task<string> TestTtsAsync(
        string? baseUrl,
        CancellationToken cancellationToken,
        TimeSpan clientTimeout,
        HttpClient? httpClient)
    {
        using var client = new VoxCpmTtsClient(() => baseUrl ?? "", httpClient, clientTimeout);
        _ = await client.SynthesizeAsync(TestSpeech, "default", cancellationToken).ConfigureAwait(false);
        return "Text-to-speech connection succeeded. The service returned valid WAV audio.";
    }

    private static TimeSpan ClientTimeout(TimeSpan timeout) => timeout + TimeSpan.FromSeconds(1);
}
