namespace Kil0bitSystemMonitor.Services.Conference;

/// <summary>
/// Coordinates one user-requested speech operation with the meeting capture lifecycle.
/// Generated audio and input text remain local to the operation and are released when it finishes.
/// </summary>
public sealed class MeetingSpeech : IAsyncDisposable
{
    private static readonly TimeSpan PlaybackSettlingTime = TimeSpan.FromMilliseconds(300);

    private readonly object _gate = new();
    private readonly MeetingSession _session;
    private readonly IMeetingTts _tts;
    private readonly IMeetingPlayback _playback;
    private readonly TimeProvider _clock;
    private SpeechOperation? _operation;
    private bool _disposed;
    private string _status = "Ready to speak.";

    public MeetingSpeech(MeetingSession session, IMeetingTts tts, IMeetingPlayback playback,
        TimeProvider? clock = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _tts = tts ?? throw new ArgumentNullException(nameof(tts));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _clock = clock ?? TimeProvider.System;
        _session.Changed += SessionChanged;
    }

    public bool IsBusy
    {
        get { lock (_gate) return _operation is not null; }
    }

    public string Status
    {
        get { lock (_gate) return _status; }
    }

    public event Action? Changed;

    public async Task SpeakAsync(string text, string voice, string playbackDeviceId,
        IReadOnlyList<string>? sources = null, CancellationToken cancellationToken = default)
    {
        text = text?.Trim() ?? string.Empty;
        voice = voice?.Trim() ?? string.Empty;
        playbackDeviceId = playbackDeviceId?.Trim() ?? string.Empty;
        if (text.Length == 0)
            throw new MeetingException("Enter text to speak.");
        if (text.Length > 4096)
            throw new MeetingException("Speech text cannot exceed 4,096 characters.");
        if (voice.Length == 0)
            throw new MeetingException("Choose a speech voice.");
        if (playbackDeviceId.Length == 0)
            throw new MeetingException("Choose a playback device.");

        SpeechOperation operation;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_operation is not null)
                throw new MeetingException("Speech generation or playback is already active.");

            operation = new SpeechOperation(
                _session.Generation,
                _session.IsActive,
                _session.State == MeetingState.Paused,
                sources,
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            _operation = operation;
            _status = "Generating speech...";
        }
        RaiseChanged();

        var pausedBySpeech = false;
        var enteredPlaybackPhase = false;
        try
        {
            if (!SessionSnapshotIsCurrent(operation))
                SafeCancel(operation);

            byte[] wav = await _tts.SynthesizeAsync(text, voice, operation.Cancellation.Token)
                .ConfigureAwait(false);
            ThrowIfObsolete(operation);

            if (operation.MeetingWasActive)
            {
                if (!MeetingIsSameAndActive(operation))
                    return;

                if (!operation.MeetingWasAlreadyPaused)
                {
                    SetStatus(operation, "Pausing listening for speech playback...");
                    await _session.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                    if (_session.Generation != operation.MeetingGeneration ||
                        _session.State != MeetingState.Paused)
                    {
                        SetStatus(operation, "Could not pause listening; speech was not played.");
                        return;
                    }
                    pausedBySpeech = true;
                }
                else if (_session.Generation != operation.MeetingGeneration ||
                         _session.State != MeetingState.Paused)
                {
                    return;
                }
            }
            else if (!SessionSnapshotIsCurrent(operation))
            {
                return;
            }

            ThrowIfObsolete(operation);
            enteredPlaybackPhase = true;
            SetStatus(operation, "Playing speech...");
            await _playback.PlayAsync(wav, playbackDeviceId, operation.Cancellation.Token)
                .ConfigureAwait(false);
            ThrowIfObsolete(operation);
            SetStatus(operation, "Speech playback completed.");
        }
        catch (MeetingPlaybackStopException)
        {
            // The render device may still be producing audio. Keep capture off so it cannot
            // feed an unconfirmed playback tail back into recognition or the conference mic.
            await _session.StopAsync().ConfigureAwait(false);
            pausedBySpeech = false;
            SetStatus(operation, "Speech playback could not be confirmed stopped. Listening stopped.");
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            SetStatus(operation, "Speech stopped.");
        }
        catch
        {
            SetStatus(operation, enteredPlaybackPhase
                ? "Speech playback failed."
                : "Speech generation failed.");
        }
        finally
        {
            if (pausedBySpeech)
            {
                try
                {
                    await Task.Delay(PlaybackSettlingTime, _clock, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (_session.Generation == operation.MeetingGeneration &&
                        _session.IsActive && _session.State == MeetingState.Paused)
                    {
                        await _session.ResumeAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch
                {
                    SetStatus(operation, "Could not resume listening after speech playback.");
                }
            }

            lock (_gate)
            {
                if (ReferenceEquals(_operation, operation))
                    _operation = null;
            }
            operation.Cancellation.Dispose();
            operation.Completion.TrySetResult();
            RaiseChanged();
        }
    }

    public async Task StopAsync()
    {
        SpeechOperation? operation;
        lock (_gate)
        {
            operation = _operation;
            if (operation is not null)
                _status = "Stopping speech...";
        }
        if (operation is null)
            return;

        RaiseChanged();
        SafeCancel(operation);
        await operation.Completion.Task.ConfigureAwait(false);
    }

    public void InvalidateSources(IReadOnlyList<string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
            return;

        SpeechOperation? operation;
        lock (_gate)
        {
            operation = _operation;
            if (operation is null || !sources.Any(operation.Sources.Contains))
                return;
            _status = "Speech stopped because its source changed.";
        }
        RaiseChanged();
        SafeCancel(operation);
    }

    public void InvalidateAll()
    {
        SpeechOperation? operation;
        lock (_gate)
        {
            operation = _operation;
            if (operation is null)
                return;
            _status = "Speech stopped because its context changed.";
        }
        RaiseChanged();
        SafeCancel(operation);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _session.Changed -= SessionChanged;
        await StopAsync().ConfigureAwait(false);
    }

    private void SessionChanged()
    {
        SpeechOperation? operation;
        lock (_gate)
        {
            operation = _operation;
            if (operation is null || SessionSnapshotIsCurrent(operation))
                return;
            _status = "Speech stopped because the meeting changed.";
        }
        RaiseChanged();
        SafeCancel(operation);
    }

    private bool SessionSnapshotIsCurrent(SpeechOperation operation)
    {
        if (_session.Generation != operation.MeetingGeneration)
            return false;
        return operation.MeetingWasActive ? _session.IsActive : !_session.IsActive;
    }

    private bool MeetingIsSameAndActive(SpeechOperation operation) =>
        _session.Generation == operation.MeetingGeneration && _session.IsActive;

    private void ThrowIfObsolete(SpeechOperation operation)
    {
        operation.Cancellation.Token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!ReferenceEquals(_operation, operation))
                throw new OperationCanceledException(operation.Cancellation.Token);
        }
        if (!SessionSnapshotIsCurrent(operation))
        {
            SafeCancel(operation);
            throw new OperationCanceledException(operation.Cancellation.Token);
        }
    }

    private void SetStatus(SpeechOperation operation, string status)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_operation, operation))
                return;
            _status = status;
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MeetingSpeech));
    }

    private static void SafeCancel(SpeechOperation operation)
    {
        try { operation.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private sealed class SpeechOperation
    {
        public SpeechOperation(long meetingGeneration, bool meetingWasActive, bool meetingWasAlreadyPaused,
            IReadOnlyList<string>? sources, CancellationTokenSource cancellation)
        {
            MeetingGeneration = meetingGeneration;
            MeetingWasActive = meetingWasActive;
            MeetingWasAlreadyPaused = meetingWasAlreadyPaused;
            Sources = new HashSet<string>(sources ?? [], StringComparer.Ordinal);
            Cancellation = cancellation;
        }

        public long MeetingGeneration { get; }
        public bool MeetingWasActive { get; }
        public bool MeetingWasAlreadyPaused { get; }
        public HashSet<string> Sources { get; }
        public CancellationTokenSource Cancellation { get; }
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
