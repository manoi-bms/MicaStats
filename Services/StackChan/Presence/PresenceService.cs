using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor.Services.StackChan.Presence;

public interface IFaceDetector : IDisposable
{
    string AdapterName { get; }
    bool OnGpu { get; }
    /// <summary>Faces in one frame; <paramref name="usable"/> is false for a covered lens or a dark room.</summary>
    IReadOnlyList<DetectedFace> Detect(byte[] jpeg, out double milliseconds, out bool usable);
}

/// <summary>What MicaStats does on the PC when presence changes; the app supplies the real ones.</summary>
public interface IPresenceActions
{
    /// <summary>Time since the last keyboard or mouse input on this PC.</summary>
    TimeSpan UserIdleFor();
    void LockVault();
    void ShowGuard(bool on);
    void LockWindows();
    void ShowBreakToast();
}

public enum DirectionCheckResult { Saved, NoFace, NotReady, Inconclusive }

public sealed record PresenceStatus(bool CameraOn, string AdapterName, bool OnGpu, double Milliseconds, double Fps,
    PresenceState State, int Faces, string? Problem = null, DetectedFace? Face = null);

/// <summary>
/// Robot camera frames in, presence reactions out. Frames are detected on the best GPU,
/// newest first, and dropped right after; nothing is stored, logged or sent off the PC.
/// All robot commands go out from the tick, one at a time, so telemetry keeps its turn.
/// </summary>
public sealed class PresenceService : IDisposable
{
    public const int DefaultFps = 4;
    private static readonly TimeSpan CameraRefresh = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GazeInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan CardShown = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan GuardCardRefresh = TimeSpan.FromSeconds(50);
    private static readonly string[] BreakLines = ["Stand up and stretch", "Rest your eyes for a minute"];

    private readonly object _gate = new();
    private readonly AppConfig _config;
    private readonly StackChanService _robot;
    private readonly IStackChanFrameSource _frames;
    private readonly Func<bool, IFaceDetector> _detectorFactory;
    private readonly TimeProvider _clock;
    private readonly IPresenceActions _actions;
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly AwaySummary _away = new();
    private readonly Queue<byte[]> _outbox = new();
    private readonly Queue<DateTimeOffset> _frameTimes = new();
    private PresenceOptions _options;
    private PresenceTracker _tracker;
    private HeadAimer? _aimer;
    private (bool Yaw, bool Pitch) _aimerInverts;
    private IFaceDetector? _detector;
    private bool _detectorFailed;
    private StackChanFrame? _pending;
    private Task _drain = Task.CompletedTask;
    private TaskCompletionSource _idle = new();
    private readonly SemaphoreSlim _work = new(0);
    private Thread? _worker;
    private bool _draining, _cameraOn, _guardShown, _aiWasActive, _meetingWasActive, _disposed, _checkingDirection;
    private TaskCompletionSource<DetectedFace?>? _frameSeen;
    private DateTimeOffset _cameraSentAt, _welcomeUntil, _breakUntil, _guardCardAt, _gazeAt;
    private (int X, int Y)? _gazeSent;
    private HeadPose _pose = HeadAimer.Rest;
    private double _milliseconds;
    private int _detectFailures;
    private bool _onCpuFallback;
    private string? _problem;
    private DateTimeOffset _cameraStartedAt, _lastFrameAt, _cameraOffUntil;
    private static readonly TimeSpan SilentCamera = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CameraOffRetry = TimeSpan.FromSeconds(15);
    private const int FailuresBeforeFallback = 5;

    public PresenceService(AppConfig config, StackChanService robot, IStackChanFrameSource frames,
        Func<bool, IFaceDetector> detectorFactory, TimeProvider clock, IPresenceActions actions, bool startBackground = true)
    {
        _config = config;
        _robot = robot;
        _frames = frames;
        _detectorFactory = detectorFactory;
        _clock = clock;
        _actions = actions;
        _options = BuildOptions();
        _tracker = new PresenceTracker(_options);
        _aiWasActive = CompanionActivity.IsActive(StackChanActivity.Ai);
        _meetingWasActive = CompanionActivity.IsActive(StackChanActivity.Meeting);
        _frames.FrameReceived += OnFrame;
        CompanionActivity.Changed += OnActivityChanged;
        if (startBackground) _ = Task.Run(RunAsync);
    }

    /// <summary>Raised on the detection thread with the in-memory frame and its faces; only for the Settings preview.</summary>
    public event Action<StackChanFrame, IReadOnlyList<DetectedFace>>? Preview;
    public event Action? Changed;

    internal bool HasPreviewSubscribers => Preview != null;

    public PresenceStatus Status
    {
        get
        {
            lock (_gate)
            {
                double fps = _frameTimes.Count < 2 ? 0
                    : (_frameTimes.Count - 1) / Math.Max(0.001, (_clock.GetUtcNow() - _frameTimes.Peek()).TotalSeconds);
                var now = _clock.GetUtcNow();
                string? problem = _problem;
                if (problem == null && _cameraOn && now - (_lastFrameAt > _cameraStartedAt ? _lastFrameAt : _cameraStartedAt) >= SilentCamera)
                    problem = "StackChan is not sending pictures.";
                return new PresenceStatus(_cameraOn, _detector?.AdapterName ?? "", _detector?.OnGpu ?? false,
                    _milliseconds, Math.Round(fps, 1), _tracker.State, _tracker.FaceCount, problem, _tracker.MainFace);
            }
        }
    }

    /// <summary>Where the head was last sent; the direction check starts from here.</summary>
    internal HeadPose CurrentPose
    {
        get { lock (_gate) return _pose; }
        set { lock (_gate) _pose = value; }
    }

    internal TimeSpan DirectionSettle { get; set; } = TimeSpan.FromSeconds(1.5);
    internal TimeSpan DirectionSampleTimeout { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Learns which way the head must turn by watching the owner's face move in the picture
    /// after two small, slow nudges. Needs no assumption about camera mirroring or servo wiring.
    /// </summary>
    public async Task<DirectionCheckResult> CheckDirectionAsync(CancellationToken token = default)
    {
        if (!IsActive() || _config.StackChanQuietMode ||
            !_robot.Snapshot.Capabilities.HasFlag(StackChanCapabilities.Motion))
            return DirectionCheckResult.NotReady;
        lock (_gate)
        {
            if (_checkingDirection) return DirectionCheckResult.NotReady;
            _checkingDirection = true;
        }
        // Start where the head already sees the face; small nudges keep the face in the picture.
        HeadPose origin;
        lock (_gate) origin = _pose;
        const double Nudge = 8;
        double yawStep = origin.Yaw + Nudge <= 45 ? Nudge : -Nudge;
        double pitchStep = origin.Pitch + Nudge <= 65 ? Nudge : -Nudge;
        bool nudged = false;
        try
        {
            var start = await MoveAndSampleAsync(origin, token).ConfigureAwait(false);
            if (start == null) return DirectionCheckResult.NoFace;
            nudged = true;
            var turned = await MoveAndSampleAsync(origin with { Yaw = origin.Yaw + yawStep }, token).ConfigureAwait(false);
            var tilted = await MoveAndSampleAsync(origin with { Pitch = origin.Pitch + pitchStep }, token).ConfigureAwait(false);
            if (turned == null || tilted == null) return DirectionCheckResult.NoFace;

            // Face movement per degree of positive turn and tilt.
            float shiftX = (turned.CenterX - start.CenterX) * Math.Sign(yawStep);
            float shiftY = (tilted.CenterY - start.CenterY) * Math.Sign(pitchStep);
            if (Math.Abs(shiftX) < 0.02f || Math.Abs(shiftY) < 0.02f) return DirectionCheckResult.Inconclusive;
            // The aimer turns by -offset: right for a face left of centre. That centres the face only
            // when a positive turn moves the face right (and a positive tilt moves it down).
            _config.StackChanHeadInvertYaw = shiftX < 0;
            _config.StackChanHeadInvertPitch = shiftY < 0;
            _config.StackChanHeadDirectionChecked = true;
            return DirectionCheckResult.Saved;
        }
        finally
        {
            // Whatever happened, the head goes back to rest; never left turned 10 degrees.
            if (nudged)
            {
                try
                {
                    await _robot.NudgeHeadAsync(origin.Yaw, origin.Pitch, CancellationToken.None).ConfigureAwait(false);
                    lock (_gate) _pose = origin;
                }
                catch (Exception) { /* Disconnected: the robot keeps its last pose. */ }
            }
            lock (_gate) _checkingDirection = false;
        }
    }

    private async Task<DetectedFace?> MoveAndSampleAsync(HeadPose pose, CancellationToken token)
    {
        await _robot.NudgeHeadAsync(pose.Yaw, pose.Pitch, token).ConfigureAwait(false);
        await Task.Delay(DirectionSettle, token).ConfigureAwait(false); // Servo travel, then a fresh frame.
        var seen = new TaskCompletionSource<DetectedFace?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _frameSeen = seen;
        try { return await seen.Task.WaitAsync(DirectionSampleTimeout, token).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }
        finally { lock (_gate) { if (ReferenceEquals(_frameSeen, seen)) _frameSeen = null; } }
    }

    public void NoteAlert()
    {
        lock (_gate) _away.NoteAlert();
    }

    public void NoteMetrics(float cpu, float ram, float gpu)
    {
        lock (_gate) _away.NoteMetrics(_clock.GetUtcNow(), cpu, ram, gpu);
    }

    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            Task drain;
            lock (_gate)
            {
                if (!_draining) return;
                drain = _drain;
            }
            await drain.ConfigureAwait(false);
        }
    }

    internal async Task TickAsync(CancellationToken token = default)
    {
        await _tickGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow();
            if (!IsActive())
            {
                await DeactivateAsync(now, token).ConfigureAwait(false);
                return;
            }
            _cameraOffUntil = default;

            if (!_cameraOn || now - _cameraSentAt >= CameraRefresh)
            {
                if (await TrySendAsync(StackChanProtocol.Camera(true, DefaultFps), token).ConfigureAwait(false))
                {
                    lock (_gate)
                    {
                        if (!_cameraOn) _cameraStartedAt = now;
                        _cameraOn = true;
                        _cameraSentAt = now;
                    }
                }
            }

            DetectedFace? face;
            bool onlooker;
            PresenceState state;
            lock (_gate)
            {
                RefreshOptions(now);
                _tracker.SetBreakSuppressed(CompanionActivity.IsActive(StackChanActivity.Meeting));
                Handle(_tracker.Tick(now), now);
                if (_tracker.OnlookerActive && now - _guardCardAt >= GuardCardRefresh)
                {
                    _guardCardAt = now;
                    _outbox.Enqueue(StackChanProtocol.Scene("guard", [], 60));
                }
                face = _tracker.MainFace;
                onlooker = _tracker.OnlookerActive;
                state = _tracker.State;
            }

            await FlushOutboxAsync(token).ConfigureAwait(false);
            await UpdateGazeAsync(face, now, token).ConfigureAwait(false);
            await AimHeadAsync(face, state, now, token).ConfigureAwait(false);
            UpdateReaction(now);
        }
        finally
        {
            _tickGate.Release();
        }
        RaiseChanged();
    }

    private bool IsActive()
    {
        var snapshot = _robot.Snapshot;
        return !_disposed && _config.StackChanEnabled && _config.StackChanPresenceEnabled &&
               snapshot.State == StackChanConnectionState.Connected &&
               snapshot.Connection == StackChanConnectionKind.Usb &&
               snapshot.Capabilities.HasFlag(StackChanCapabilities.Dashboard);
    }

    private async Task DeactivateAsync(DateTimeOffset now, CancellationToken token)
    {
        bool cameraWasOn, guardWasShown;
        lock (_gate)
        {
            cameraWasOn = _cameraOn;
            // Keep asking the robot to turn its camera off for a while if the first try is skipped.
            if (cameraWasOn) _cameraOffUntil = now + CameraOffRetry;
            guardWasShown = _guardShown;
            _cameraOn = false;
            _guardShown = false;
            _pending = null;
            _outbox.Clear();
            _frameTimes.Clear();
            _welcomeUntil = _breakUntil = default;
            _gazeSent = null;
            _away.End();
            if (_tracker.State != PresenceState.Unknown || _tracker.OnlookerActive) _tracker = new PresenceTracker(_options);
        }
        if (guardWasShown) _actions.ShowGuard(false);
        _robot.SetPresence(PresenceReaction.None);
        _robot.SetHeadTracking(false);
        var snapshot = _robot.Snapshot;
        // Turn the camera off when the robot can still hear it (this also releases its gaze and card);
        // otherwise its own 10 s keepalive does.
        if (now < _cameraOffUntil && snapshot.State == StackChanConnectionState.Connected &&
            await TrySendAsync(StackChanProtocol.Camera(false), token).ConfigureAwait(false))
            _cameraOffUntil = default;
    }

    private void OnFrame(StackChanFrame frame)
    {
        lock (_gate)
        {
            if (!_cameraOn || _disposed) return;
            _lastFrameAt = _clock.GetUtcNow();
            _pending = frame;
            if (_draining) return;
            _draining = true;
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _drain = _idle.Task;
            if (_worker == null)
            {
                // One long-lived thread owns detection: WPF image decoding ties a dispatcher to the
                // thread it runs on, so pool threads would each keep one alive.
                _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "StackChan presence detection" };
                _worker.Start();
            }
            _work.Release();
        }
    }

    private void WorkerLoop()
    {
        try
        {
            while (true)
            {
                _work.Wait(_shutdown.Token);
                while (true)
                {
                    StackChanFrame? frame;
                    lock (_gate)
                    {
                        frame = _pending;
                        _pending = null;
                        if (frame == null)
                        {
                            _draining = false;
                            _idle.TrySetResult();
                            break;
                        }
                    }
                    Process(frame);
                }
            }
        }
        catch (OperationCanceledException) { /* Disposed. */ }
        finally
        {
            lock (_gate)
            {
                _pending = null;
                _draining = false;
                _idle.TrySetResult();
            }
            System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
        }
    }

    private void Process(StackChanFrame frame)
    {
        var detector = Detector();
        if (detector == null) return;
        IReadOnlyList<DetectedFace> faces;
        double milliseconds;
        bool usable;
        try
        {
            faces = detector.Detect(frame.Jpeg, out milliseconds, out usable);
            _detectFailures = 0;
        }
        catch (Exception)
        {
            // One bad frame is skipped. Repeated failures mean the device is gone (driver update,
            // remote session): move to the CPU once, and say so.
            if (++_detectFailures >= FailuresBeforeFallback) FallBackToCpu(detector);
            return;
        }

        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (!_cameraOn) return;
            _milliseconds = milliseconds;
            _frameTimes.Enqueue(now);
            while (_frameTimes.Count > 0 && now - _frameTimes.Peek() > TimeSpan.FromSeconds(3)) _frameTimes.Dequeue();
            Handle(_tracker.Observe(now, faces, usable), now);
            // The direction check waits for a picture that actually shows a face: at the edge of the
            // frame the detector misses some pictures, and one miss must not fail the check.
            if (_frameSeen is { } seen && usable && faces.Count > 0)
            {
                _frameSeen = null;
                DetectedFace largest = faces[0];
                foreach (var candidate in faces)
                    if (candidate.Width * candidate.Height > largest.Width * largest.Height) largest = candidate;
                seen.TrySetResult(largest);
            }
        }
        UpdateReaction(now);
        if (Preview is { } preview)
        {
            try { preview(frame, faces); } catch { /* A closed Settings page cannot stop detection. */ }
        }
    }

    private IFaceDetector? Detector()
    {
        lock (_gate)
        {
            if (_detector != null || _detectorFailed) return _detector;
        }
        IFaceDetector? created = null;
        try { created = _detectorFactory(_onCpuFallback); }
        catch (Exception) { /* No model or runtime: presence stays off rather than crashing. */ }
        lock (_gate)
        {
            if (created == null)
            {
                _detectorFailed = true;
                _problem = "Face detection could not start. Reinstall MicaStats.";
            }
            else if (_detector == null && !_disposed) _detector = created;
            else created.Dispose();
            return _detector;
        }
    }

    private void FallBackToCpu(IFaceDetector failed)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_detector, failed)) return;
            _detector = null;
            _detectFailures = 0;
            if (_onCpuFallback)
            {
                _detectorFailed = true;
                _problem = "Face detection stopped working. Restart MicaStats.";
            }
            else
            {
                _onCpuFallback = true;
                _problem = "The GPU stopped answering, so face detection moved to the CPU.";
            }
        }
        failed.Dispose(); // Only the drain thread uses it, and this is the drain thread.
    }

    /// <summary>Applies tracker events; caller holds the gate. PC actions run here, robot commands queue for the tick.</summary>
    private void Handle(IReadOnlyList<PresenceEvent> events, DateTimeOffset now)
    {
        foreach (var presenceEvent in events)
        {
            switch (presenceEvent.Kind)
            {
                case PresenceEventKind.Left:
                    _away.Begin(now);
                    break;
                case PresenceEventKind.Arrived:
                    // A welcome reads the summary; any other arrival just closes it.
                    if (!events.Any(e => e.Kind == PresenceEventKind.Welcome)) _away.End();
                    break;
                case PresenceEventKind.Welcome:
                    _welcomeUntil = now + CardShown;
                    _outbox.Enqueue(StackChanProtocol.Scene("welcome", _away.Lines(now), (int)CardShown.TotalSeconds));
                    break;
                case PresenceEventKind.OnlookerStarted:
                    if (_config.StackChanGuardVault) Safely(_actions.LockVault);
                    if (_config.StackChanGuardBlur)
                    {
                        _guardShown = true;
                        Safely(() => _actions.ShowGuard(true));
                    }
                    _guardCardAt = now;
                    _outbox.Enqueue(StackChanProtocol.Scene("guard", [], 60));
                    break;
                case PresenceEventKind.OnlookerEnded:
                    if (_guardShown)
                    {
                        _guardShown = false;
                        Safely(() => _actions.ShowGuard(false));
                    }
                    _outbox.Enqueue(StackChanProtocol.Scene("none", [], 1));
                    break;
                case PresenceEventKind.BreakDue:
                    _breakUntil = now + CardShown;
                    _outbox.Enqueue(StackChanProtocol.Scene("break", BreakLines, (int)CardShown.TotalSeconds));
                    Safely(_actions.ShowBreakToast);
                    break;
                case PresenceEventKind.LockDue:
                    // The camera can be wrong; the keyboard and mouse are not. Never lock someone who is
                    // typing, and never in the middle of a meeting.
                    if (_config.StackChanLockWindowsWhenAway && _options.LockAfter is { } lockAfter &&
                        !CompanionActivity.IsActive(StackChanActivity.Meeting) && IdleAtLeast(lockAfter))
                        Safely(_actions.LockWindows);
                    break;
            }
        }
    }

    private void UpdateReaction(DateTimeOffset now)
    {
        PresenceReaction reaction;
        lock (_gate)
        {
            if (!_cameraOn) return;
            reaction = _tracker.OnlookerActive ? PresenceReaction.Onlooker
                : now < _welcomeUntil ? PresenceReaction.Welcome
                : now < _breakUntil ? PresenceReaction.Break
                : _tracker.State == PresenceState.Away ? PresenceReaction.Doze
                : PresenceReaction.None;
        }
        _robot.SetPresence(reaction);
    }

    private async Task FlushOutboxAsync(CancellationToken token)
    {
        while (true)
        {
            byte[] json;
            lock (_gate)
            {
                if (_outbox.Count == 0) return;
                json = _outbox.Peek();
            }
            if (!await TrySendAsync(json, token).ConfigureAwait(false)) return; // Retry next tick.
            lock (_gate) { if (_outbox.Count > 0 && ReferenceEquals(_outbox.Peek(), json)) _outbox.Dequeue(); }
        }
    }

    private async Task UpdateGazeAsync(DetectedFace? face, DateTimeOffset now, CancellationToken token)
    {
        (int X, int Y)? target = face == null ? null
            : (Math.Clamp((int)Math.Round((face.CenterX - 0.5) * 200), -100, 100),
               Math.Clamp((int)Math.Round((face.CenterY - 0.5) * 200), -100, 100));
        lock (_gate)
        {
            // The direction check needs the writer for its nudges; gaze waits.
            if (_checkingDirection || now - _gazeAt < GazeInterval || target == _gazeSent) return;
        }
        if (await TrySendAsync(StackChanProtocol.Gaze(target?.X, target?.Y), token).ConfigureAwait(false))
        {
            lock (_gate)
            {
                _gazeSent = target;
                _gazeAt = now;
            }
        }
    }

    private async Task AimHeadAsync(DetectedFace? face, PresenceState state, DateTimeOffset now, CancellationToken token)
    {
        bool allowed = _config.StackChanAllowAutoMotion && !_config.StackChanQuietMode &&
                       _robot.Snapshot.Capabilities.HasFlag(StackChanCapabilities.Motion);
        _robot.SetHeadTracking(allowed);
        lock (_gate) { if (_checkingDirection) return; }
        if (!allowed || state == PresenceState.Unknown) return;
        // A person is holding the head: pause, so no search or return is spent on refused moves.
        if (_robot.ManualHoldActive) return;
        // Start from where the head really is: a person or a gesture may have moved it.
        if (_robot.HeadPosition is { } head) lock (_gate) _pose = new HeadPose(head.Yaw, head.Pitch);
        // The aimer follows at once and calibrates itself from how the face moves after each step.
        HeadPose? pose;
        bool learned;
        (bool Yaw, bool Pitch) inverts;
        lock (_gate)
        {
            var saved = (_config.StackChanHeadInvertYaw, _config.StackChanHeadInvertPitch);
            // Start from what was saved; rebuild only when someone else (the manual check) changed it.
            if (_aimer == null || saved != _aimerInverts)
            {
                _aimer = new HeadAimer(saved.Item1, saved.Item2);
                _aimerInverts = saved;
            }
            // Present but out of view: the aimer looks where the person went, or holds still for a glance.
            pose = _aimer.Next(now, face, _pose, present: state == PresenceState.Present);
            learned = _aimer.Learned;
            inverts = (_aimer.InvertYaw, _aimer.InvertPitch);
            if (learned) _aimerInverts = inverts;
        }
        if (learned && (!_config.StackChanHeadDirectionChecked ||
                        _config.StackChanHeadInvertYaw != inverts.Yaw || _config.StackChanHeadInvertPitch != inverts.Pitch))
        {
            // Save what the head learned, so the next start begins the right way round.
            _config.StackChanHeadInvertYaw = inverts.Yaw;
            _config.StackChanHeadInvertPitch = inverts.Pitch;
            _config.StackChanHeadDirectionChecked = true;
        }
        if (pose == null) return;
        try
        {
            bool sent = await _robot.AimHeadAsync(pose.Yaw, pose.Pitch, token).ConfigureAwait(false);
            // A move dropped while the link was busy must not be learned as a servo limit.
            lock (_gate) { if (sent) _pose = pose; else _aimer?.Dropped(); }
        }
        catch (InvalidOperationException)
        {
            // Quiet or motion switched off between checks.
            lock (_gate) _aimer?.Dropped();
        }
    }

    private async Task<bool> TrySendAsync(byte[] json, CancellationToken token)
    {
        try { return await _robot.SendPresenceAsync(json, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private PresenceOptions BuildOptions() => PresenceOptions.Default with
    {
        BreakAfter = TimeSpan.FromMinutes(_config.StackChanBreakMinutes),
        LockAfter = _config.StackChanLockWindowsWhenAway
            ? TimeSpan.FromMinutes(_config.StackChanLockWindowsMinutes) : null
    };

    /// <summary>Caller holds the gate. A changed timer setting starts a fresh tracker; a raised guard is lowered first.</summary>
    private void RefreshOptions(DateTimeOffset now)
    {
        var options = BuildOptions();
        if (options == _options) return;
        if (_tracker.OnlookerActive) Handle([new PresenceEvent(PresenceEventKind.OnlookerEnded)], now);
        _options = options;
        _tracker = new PresenceTracker(options);
    }

    private void OnActivityChanged()
    {
        bool ai = CompanionActivity.IsActive(StackChanActivity.Ai);
        bool meeting = CompanionActivity.IsActive(StackChanActivity.Meeting);
        lock (_gate)
        {
            if (_aiWasActive && !ai) _away.NoteActivityEnded(StackChanActivity.Ai);
            if (_meetingWasActive && !meeting) _away.NoteActivityEnded(StackChanActivity.Meeting);
            _aiWasActive = ai;
            _meetingWasActive = meeting;
        }
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250), _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try { await TickAsync(_shutdown.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested) { }
                catch (Exception) when (!_shutdown.IsCancellationRequested) { /* The next tick retries. */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void RaiseChanged()
    {
        if (Changed is not { } changed) return;
        foreach (Action observer in changed.GetInvocationList())
            try { observer(); } catch { /* A closed Settings page cannot stop presence. */ }
    }

    private bool IdleAtLeast(TimeSpan span)
    {
        try { return _actions.UserIdleFor() >= span; }
        catch (Exception) { return false; }
    }

    private static void Safely(Action action)
    {
        try { action(); } catch { /* A PC action failing must not stop detection. */ }
    }

    public void Dispose()
    {
        bool guardWasShown, cameraWasOn;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            guardWasShown = _guardShown;
            cameraWasOn = _cameraOn;
            _guardShown = false;
            _cameraOn = false;
        }
        _frames.FrameReceived -= OnFrame;
        CompanionActivity.Changed -= OnActivityChanged;
        _shutdown.Cancel();
        if (guardWasShown) Safely(() => _actions.ShowGuard(false));
        if (cameraWasOn)
        {
            // Stop the stream before the app closes the port: a port closed mid-picture can leave the
            // robot stuck. Bounded, so quitting never hangs on a robot that does not answer.
            try { TrySendAsync(StackChanProtocol.Camera(false), CancellationToken.None).Wait(TimeSpan.FromSeconds(1)); }
            catch (AggregateException) { }
        }
        Task drain;
        lock (_gate)
        {
            _pending = null;
            drain = _drain;
        }
        // Freeing a native session mid-inference crashes the process; let the last detection finish.
        // If it hangs, leak the detector rather than risk that.
        bool finished;
        try { finished = drain.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { finished = true; }
        if (!finished) return;
        IFaceDetector? detector;
        lock (_gate)
        {
            detector = _detector;
            _detector = null;
        }
        detector?.Dispose();
    }
}
