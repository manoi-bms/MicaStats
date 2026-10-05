using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Conference;

/// <summary>
/// Owns one explicitly started meeting capture and its bounded transcription pumps.
/// Device and provider implementations are supplied at the boundary so this type never
/// opens hardware or performs HTTP work by itself.
/// </summary>
public sealed class MeetingSession : IAsyncDisposable
{
    private const int QueueCapacity = 8;
    private const int MaxTranscriptCharacters = 2_000_000;
    private static readonly TimeSpan MaxSessionDuration = TimeSpan.FromHours(8);
    private static readonly TimeSpan AsrDeadline = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly IMeetingCapture _capture;
    private readonly IMeetingAsr _asr;
    private readonly TimeProvider _clock;
    private readonly List<MeetingSegment> _segments = new();
    private readonly List<MeetingGap> _gaps = new();
    private readonly Dictionary<string, int> _segmentIdCounts = new(StringComparer.Ordinal);

    private CancellationTokenSource? _runCancellation;
    private Channel<MeetingAudioChunk>? _microphoneQueue;
    private Channel<MeetingAudioChunk>? _outputQueue;
    private long _startedTimestamp;
    private AsrService _service;
    private long _generation;
    private int _transcriptCharacters;
    private bool _disposed;
    private MeetingState _state = MeetingState.Stopped;
    private string _status = "Ready.";

    public MeetingSession(IMeetingCapture capture, IMeetingAsr asr, TimeProvider? clock = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _asr = asr ?? throw new ArgumentNullException(nameof(asr));
        _clock = clock ?? TimeProvider.System;
    }

    public MeetingState State
    {
        get { lock (_gate) return _state; }
    }

    public string Status
    {
        get { lock (_gate) return _status; }
    }

    public IReadOnlyList<MeetingSegment> Segments
    {
        get { lock (_gate) return Array.AsReadOnly(_segments.ToArray()); }
    }

    public IReadOnlyList<MeetingGap> Gaps
    {
        get { lock (_gate) return Array.AsReadOnly(_gaps.ToArray()); }
    }

    public bool IsActive
    {
        get { lock (_gate) return IsActiveState(_state); }
    }

    /// <summary>Changes whenever a session starts or is invalidated by stop/failure.</summary>
    public long Generation
    {
        get { lock (_gate) return _generation; }
    }

    public event Action? Changed;

    public async Task StartAsync(MeetingStartOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.MicrophoneId) || string.IsNullOrWhiteSpace(options.OutputId))
            throw new MeetingException("Choose a microphone and output device before starting.");

        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            long generation;
            CancellationToken runToken;
            lock (_gate)
            {
                if (IsActiveState(_state))
                    throw new MeetingException("A meeting session is already active.");

                generation = ++_generation;
                _runCancellation?.Cancel();
                _runCancellation?.Dispose();
                _runCancellation = new CancellationTokenSource();
                runToken = _runCancellation.Token;
                _microphoneQueue = CreateQueue();
                _outputQueue = CreateQueue();
                _segments.Clear();
                _gaps.Clear();
                _segmentIdCounts.Clear();
                _transcriptCharacters = 0;
                _startedTimestamp = _clock.GetTimestamp();
                _service = options.Service;
                _state = MeetingState.Listening;
                _status = $"Listening with {DisplayService(options.Service)}.";
            }

            _ = RunWorkerAsync(_microphoneQueue!, generation, runToken);
            _ = RunWorkerAsync(_outputQueue!, generation, runToken);
            _ = EnforceDurationLimitAsync(generation, runToken);
            RaiseChanged();

            try
            {
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(runToken, cancellationToken);
                await _capture.StartAsync(options.MicrophoneId, options.OutputId,
                    chunk => AcceptChunk(generation, chunk),
                    ignoredDetail => _ = FaultAsync(generation, "Audio device became unavailable."),
                    startup.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await StopGenerationAsync(generation, MeetingState.Stopped, "Stopped.").ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (runToken.IsCancellationRequested)
            {
                await StopGenerationAsync(generation, MeetingState.Stopped, "Stopped.").ConfigureAwait(false);
                return;
            }
            catch
            {
                await StopGenerationAsync(generation, MeetingState.Faulted,
                    "Could not start the selected audio devices.").ConfigureAwait(false);
                throw new MeetingException("Could not start the selected audio devices.");
            }
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long generation;
            CancellationToken runToken;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_state != MeetingState.Listening)
                    return;
                generation = _generation;
                runToken = _runCancellation?.Token ?? CancellationToken.None;
                _status = "Pausing listening...";
            }
            RaiseChanged();

            try
            {
                using var pause = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runToken);
                await _capture.PauseAsync(pause.Token,
                    firstSourceStopped => OpenGap(generation, firstSourceStopped)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (runToken.IsCancellationRequested &&
                                                      !cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                await StopGenerationAsync(generation, MeetingState.Faulted,
                    "Could not pause audio capture.").ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            lock (_gate)
            {
                if (_generation != generation || _state != MeetingState.Listening)
                    return;
                _state = MeetingState.Paused;
                _status = "Listening paused.";
            }
            RaiseChanged();
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long generation;
            CancellationToken runToken;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_state != MeetingState.Paused)
                    return;
                generation = _generation;
                runToken = _runCancellation?.Token ?? CancellationToken.None;
                _status = "Resuming listening...";
            }
            RaiseChanged();

            try
            {
                using var resume = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runToken);
                await _capture.ResumeAsync(resume.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (runToken.IsCancellationRequested &&
                                                      !cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                await StopGenerationAsync(generation, MeetingState.Faulted,
                    "Could not resume audio capture.").ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            lock (_gate)
            {
                if (_generation != generation || _state != MeetingState.Paused)
                    return;
                CloseCurrentGapLocked();
                _state = MeetingState.Listening;
                _status = $"Listening with {DisplayService(_service)}.";
            }
            RaiseChanged();
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task StopAsync()
    {
        lock (_gate)
        {
            if (IsActiveState(_state))
            {
                ++_generation;
                _runCancellation?.Cancel();
            }
        }
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            long generation;
            lock (_gate)
            {
                if (_disposed || _state == MeetingState.Stopped)
                    return;
                generation = _generation;
            }
            await StopGenerationAsync(generation, MeetingState.Stopped, "Stopped.").ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    public string ExportMarkdown()
    {
        MeetingSegment[] segments;
        MeetingGap[] gaps;
        lock (_gate)
        {
            segments = _segments.ToArray();
            gaps = _gaps.ToArray();
        }

        var rows = new List<(TimeSpan Start, int Kind, MeetingSegment? Segment, MeetingGap? Gap)>();
        rows.AddRange(segments.Select(segment => (segment.Start, 0, (MeetingSegment?)segment, (MeetingGap?)null)));
        rows.AddRange(gaps.Select(gap => (gap.Start, 1, (MeetingSegment?)null, (MeetingGap?)gap)));
        rows.Sort((left, right) =>
        {
            var byTime = left.Start.CompareTo(right.Start);
            return byTime != 0 ? byTime : left.Kind.CompareTo(right.Kind);
        });

        var markdown = new StringBuilder("# Meeting transcript\n\n");
        if (rows.Count == 0)
        {
            markdown.Append("_No transcript received._\n");
            return markdown.ToString();
        }

        foreach (var row in rows)
        {
            if (row.Segment is { } segment)
            {
                markdown.Append("## ").Append(FormatElapsed(segment.Start)).Append(" — ")
                    .Append(DisplaySource(segment.Source)).Append("\n\n")
                    .Append("Segment ID: `").Append(segment.Id).Append("`\n\n");
                AppendQuoted(markdown, segment.Text);
            }
            else if (row.Gap is { } gap)
            {
                markdown.Append("## ").Append(FormatElapsed(gap.Start)).Append(" — Listening gap\n\n> ");
                if (gap.End is { } end)
                    markdown.Append("Listening paused until ").Append(FormatElapsed(end)).Append('.');
                else
                    markdown.Append("Listening remained paused when the session ended.");
                markdown.Append("\n\n");
            }
        }

        return markdown.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _runCancellation?.Dispose();
            _runCancellation = null;
        }
        await _capture.DisposeAsync().ConfigureAwait(false);
    }

    private static Channel<MeetingAudioChunk> CreateQueue() =>
        Channel.CreateBounded<MeetingAudioChunk>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private void AcceptChunk(long generation, MeetingAudioChunk chunk)
    {
        Channel<MeetingAudioChunk>? queue;
        var durationExceeded = false;
        lock (_gate)
        {
            if (_generation != generation || _state != MeetingState.Listening)
                return;
            if (ElapsedLocked() >= MaxSessionDuration)
            {
                durationExceeded = true;
                queue = null;
            }
            else
            {
                queue = chunk.Source == MeetingSource.Microphone ? _microphoneQueue : _outputQueue;
            }
        }

        if (durationExceeded)
        {
            _ = FaultAsync(generation, "The eight-hour meeting limit was reached.");
            return;
        }

        var owned = chunk with { Wav = (byte[])chunk.Wav.Clone() };
        if (queue is null || !queue.Writer.TryWrite(owned))
            _ = FaultAsync(generation, "Transcription backlog is full; listening stopped.");
    }

    private async Task RunWorkerAsync(Channel<MeetingAudioChunk> queue, long generation, CancellationToken runToken)
    {
        try
        {
            await foreach (var chunk in queue.Reader.ReadAllAsync(runToken).ConfigureAwait(false))
            {
                string text;
                try
                {
                    using var deadline = new CancellationTokenSource(AsrDeadline, _clock);
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(runToken, deadline.Token);
                    text = await _asr.TranscribeAsync(chunk, _service, request.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (runToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    await FaultAsync(generation, "Speech recognition timed out.").ConfigureAwait(false);
                    return;
                }
                catch
                {
                    await FaultAsync(generation, "Speech recognition failed.").ConfigureAwait(false);
                    return;
                }

                text = text?.Trim() ?? string.Empty;
                if (text.Length == 0)
                    continue;

                var limitReached = false;
                lock (_gate)
                {
                    if (_generation != generation || !IsActiveState(_state))
                        return;
                    if (_transcriptCharacters > MaxTranscriptCharacters - text.Length)
                    {
                        limitReached = true;
                    }
                    else
                    {
                        _transcriptCharacters += text.Length;
                        var idBase = $"segment-{chunk.Start.Ticks:D19}-{(int)chunk.Source}";
                        _segmentIdCounts.TryGetValue(idBase, out var duplicateCount);
                        _segmentIdCounts[idBase] = duplicateCount + 1;
                        var id = duplicateCount == 0 ? idBase : $"{idBase}-{duplicateCount:D4}";
                        _segments.Add(new MeetingSegment(id, chunk.Source, chunk.Start, chunk.Duration, text));
                        _segments.Sort(CompareSegments);
                    }
                }

                if (limitReached)
                {
                    await FaultAsync(generation, "The transcript size limit was reached.").ConfigureAwait(false);
                    return;
                }
                RaiseChanged();
            }
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {
        }
        catch
        {
            await FaultAsync(generation, "Transcription stopped unexpectedly.").ConfigureAwait(false);
        }
    }

    private async Task EnforceDurationLimitAsync(long generation, CancellationToken runToken)
    {
        try
        {
            await Task.Delay(MaxSessionDuration, _clock, runToken).ConfigureAwait(false);
            await FaultAsync(generation, "The eight-hour meeting limit was reached.").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {
        }
    }

    private async Task FaultAsync(long generation, string safeStatus)
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopGenerationAsync(generation, MeetingState.Faulted, safeStatus).ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task StopGenerationAsync(long generation, MeetingState finalState, string safeStatus)
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_generation != generation || !IsActiveState(_state))
                return;
            ++_generation;
            cancellation = _runCancellation;
            _runCancellation = null;
            _microphoneQueue?.Writer.TryComplete();
            _outputQueue?.Writer.TryComplete();
            _microphoneQueue = null;
            _outputQueue = null;
            _state = finalState;
            _status = safeStatus;
        }

        cancellation?.Cancel();
        RaiseChanged();
        try
        {
            await _capture.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // The selected safe status already tells the user that capture has stopped/faulted.
        }
        finally
        {
            cancellation?.Dispose();
        }
    }

    private TimeSpan ElapsedLocked()
    {
        var elapsed = _clock.GetElapsedTime(_startedTimestamp, _clock.GetTimestamp());
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    private void CloseCurrentGapLocked()
    {
        if (_gaps.Count == 0 || _gaps[^1].End is not null)
            return;
        _gaps[^1] = _gaps[^1] with { End = ElapsedLocked() };
    }

    private void OpenGap(long generation, TimeSpan firstSourceStopped)
    {
        var changed = false;
        lock (_gate)
        {
            if (_generation != generation || _state != MeetingState.Listening ||
                (_gaps.Count > 0 && _gaps[^1].End is null))
                return;
            _gaps.Add(new MeetingGap(firstSourceStopped < TimeSpan.Zero ? TimeSpan.Zero : firstSourceStopped, null));
            changed = true;
        }
        if (changed) RaiseChanged();
    }

    private static int CompareSegments(MeetingSegment left, MeetingSegment right)
    {
        var byStart = left.Start.CompareTo(right.Start);
        if (byStart != 0) return byStart;
        var bySource = left.Source.CompareTo(right.Source);
        return bySource != 0 ? bySource : string.CompareOrdinal(left.Id, right.Id);
    }

    private static bool IsActiveState(MeetingState state) =>
        state is MeetingState.Listening or MeetingState.Paused;

    private static string DisplayService(AsrService service) => service == AsrService.Asr2 ? "ASR2" : "ASR1";
    private static string DisplaySource(MeetingSource source) => source == MeetingSource.Microphone ? "Microphone" : "Output";

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1 ? elapsed.ToString(@"hh\:mm\:ss") : elapsed.ToString(@"mm\:ss");

    private static void AppendQuoted(StringBuilder markdown, string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (var line in normalized.Split('\n'))
            markdown.Append("> ").Append(line).Append('\n');
        markdown.Append('\n');
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MeetingSession));
    }
}
