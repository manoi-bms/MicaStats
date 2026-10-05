using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Conference;

public enum MeetingSource { Microphone, Output }
public enum AsrService { Asr2, Asr1 }
public enum MeetingState { Stopped, Listening, Paused, Faulted }

public sealed record MeetingDevice(string Id, string Name, bool IsDefault = false);
public sealed record MeetingVoice(string Id, string Name)
{
    public string DisplayName => $"{Name} · {Id}";
}
public sealed record MeetingAudioChunk(MeetingSource Source, TimeSpan Start, TimeSpan Duration, byte[] Wav);
public sealed record MeetingSegment(string Id, MeetingSource Source, TimeSpan Start, TimeSpan Duration, string Text)
{
    public MeetingAsrComparison? Comparison { get; init; }
}
public sealed record MeetingGap(TimeSpan Start, TimeSpan? End);
public sealed record MeetingNoteChoice(string Id, string Title);
public sealed record MeetingReference(string Id, string Title, string Text);
public sealed record MeetingPoint(string Text, IReadOnlyList<string> Sources);
public sealed record MeetingQuestion(string Question, string Answer, string MissingInformation, IReadOnlyList<string> Sources);
public sealed record MeetingAnalysis(string Summary, IReadOnlyList<MeetingPoint> Points, IReadOnlyList<MeetingQuestion> Questions)
{
    public string? ContextNotice { get; init; }
}
public sealed record MeetingContext(IReadOnlyList<MeetingSegment> Segments, IReadOnlyList<MeetingReference> References);
public sealed record MeetingStartOptions(
    string MicrophoneId,
    string OutputId,
    AsrService Service = AsrService.Asr2,
    bool CompareBothServices = false);

/// <summary>Device IO boundary. Pause completes only after both sources stop delivering audio.</summary>
public interface IMeetingCapture : IAsyncDisposable
{
    IReadOnlyList<MeetingDevice> GetDevices(MeetingSource source);
    Task StartAsync(string microphoneId, string outputId, Action<MeetingAudioChunk> onChunk,
        Action<string> onFault, CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken, Action<TimeSpan>? onFirstSourceStopped = null);
    Task ResumeAsync(CancellationToken cancellationToken);
    Task StopAsync();
}

public interface IMeetingPlayback
{
    Task PlayAsync(byte[] wav, string deviceId, CancellationToken cancellationToken);
}

public interface IMeetingAsr
{
    Task<string> TranscribeAsync(MeetingAudioChunk chunk, AsrService service, CancellationToken cancellationToken);
}

public interface IMeetingTts
{
    Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken cancellationToken);
    Task<IReadOnlyList<MeetingVoice>> GetVoicesAsync(CancellationToken cancellationToken);
}

public interface IMeetingAnalyzer
{
    Task<MeetingAnalysis> AnalyzeAsync(MeetingContext context, string? question, CancellationToken cancellationToken);
}

public interface IMeetingNotes
{
    Task<IReadOnlyList<MeetingNoteChoice>> ListAsync(CancellationToken cancellationToken);
    Task<MeetingReference?> ReadAsync(string id, CancellationToken cancellationToken);
}

/// <summary>A safe, user-facing fault; never contains raw provider response bodies.</summary>
public sealed class MeetingException : Exception
{
    public MeetingException(string message) : base(message) { }
}

/// <summary>Capture must stay stopped because the output driver has not confirmed speech termination.</summary>
public sealed class MeetingPlaybackStopException : Exception
{
    public MeetingPlaybackStopException() : base("Speech playback could not be confirmed stopped.") { }
}
