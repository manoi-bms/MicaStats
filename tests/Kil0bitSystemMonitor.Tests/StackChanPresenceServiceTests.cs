using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.StackChan;
using Kil0bitSystemMonitor.Services.StackChan.Presence;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

[Collection("StackChanActivityGlobal")]
public sealed class StackChanPresenceServiceTests : IDisposable
{
    private static readonly StackChanDevice UsbRobot = new(0x112233445566, "StackChan USB",
        StackChanFirmware.UiFlow2, StackChanConnectionKind.Usb, "COM9");
    private static readonly DetectedFace Owner = new(0.5f, 0.45f, 0.3f, 0.4f, 0.95f);
    private static readonly DetectedFace OwnerRight = new(0.9f, 0.45f, 0.3f, 0.4f, 0.95f);
    private static readonly DetectedFace Behind = new(0.8f, 0.3f, 0.1f, 0.14f, 0.9f);

    private readonly ManualClock _clock = new();
    private readonly FakeTransport _transport = new();
    private readonly FakeFrames _frames = new();
    private readonly FakeDetector _detector = new();
    private readonly FakeDetector _cpuDetector = new() { Name = "CPU", Gpu = false };
    private readonly FakeActions _actions = new();
    private readonly AppConfig _config = new()
    {
        StackChanEnabled = true,
        StackChanUseUsb = true,
        StackChanPresenceEnabled = true,
        StackChanReactToAlerts = true
    };
    private readonly StackChanService _robot;
    private readonly PresenceService _service;

    public StackChanPresenceServiceTests()
    {
        _robot = new StackChanService(_config, _transport, _clock, startBackground: false);
        _service = new PresenceService(_config, _robot, _frames, forceCpu => forceCpu ? _cpuDetector : _detector, _clock, _actions, startBackground: false);
    }

    public void Dispose()
    {
        _service.Dispose();
        _robot.Dispose();
    }

    private async Task ConnectAsync() => await _robot.ConnectAsync(UsbRobot);

    private async Task SeeAsync(double seconds, params DetectedFace[] faces)
    {
        var end = _clock.GetUtcNow().AddSeconds(seconds);
        while (_clock.GetUtcNow() < end)
        {
            _detector.Faces = faces;
            _frames.Raise();
            await _service.WhenIdleAsync();
            await _service.TickAsync();
            _clock.Advance(TimeSpan.FromMilliseconds(250));
        }
    }

    private IEnumerable<JsonElement> Commands(string property) => _transport.Writes
        .Where(write => write.Channel == StackChanChannel.Dashboard)
        .Select(write => JsonDocument.Parse(write.Json).RootElement)
        .Where(root => root.TryGetProperty(property, out _))
        .Select(root => root.GetProperty(property));

    [Fact]
    public async Task enabling_sends_camera_on_and_refreshes_every_5s()
    {
        await ConnectAsync();
        await _service.TickAsync();
        Assert.True(Assert.Single(Commands("camera")).GetProperty("on").GetBoolean());
        Assert.Equal(PresenceService.DefaultFps, Commands("camera").Single().GetProperty("fps").GetInt32());

        _clock.Advance(TimeSpan.FromSeconds(4));
        await _service.TickAsync();
        Assert.Single(Commands("camera"));

        _clock.Advance(TimeSpan.FromSeconds(1.1));
        await _service.TickAsync();
        Assert.Equal(2, Commands("camera").Count());
        Assert.True(_service.Status.CameraOn);
    }

    [Fact]
    public async Task disabling_sends_camera_off()
    {
        await ConnectAsync();
        await _service.TickAsync();

        _config.StackChanPresenceEnabled = false;
        await _service.TickAsync();

        Assert.False(Commands("camera").Last().GetProperty("on").GetBoolean());
        Assert.False(_service.Status.CameraOn);
    }

    [Fact]
    public async Task latest_frame_wins_when_detector_busy()
    {
        await ConnectAsync();
        await _service.TickAsync();
        _detector.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _frames.Raise(1);
        await _detector.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _frames.Raise(2);
        _frames.Raise(3);

        _detector.Block.SetResult();
        await _service.WhenIdleAsync();

        Assert.Equal(new byte[] { 1, 3 }, _detector.Seen.Select(jpeg => jpeg[0]));
    }

    [Fact]
    public async Task onlooker_locks_vault_and_blurs_then_clears_blur_only()
    {
        await ConnectAsync();
        await SeeAsync(2, Owner);
        await SeeAsync(2, Owner, Behind);

        Assert.Equal(1, _actions.VaultLocks);
        Assert.Equal([true], _actions.Guard);
        Assert.Equal(PresenceReaction.Onlooker, _robot.Presence);
        Assert.Equal("guard", Commands("presence").Last(p => p.TryGetProperty("scene", out _)).GetProperty("scene").GetString());

        await SeeAsync(2.5, Owner);

        Assert.Equal([true, false], _actions.Guard);
        Assert.Equal(1, _actions.VaultLocks);
        Assert.NotEqual(PresenceReaction.Onlooker, _robot.Presence);
    }

    [Fact]
    public async Task guard_toggles_respect_settings()
    {
        _config.StackChanGuardVault = false;
        _config.StackChanGuardBlur = false;
        await ConnectAsync();
        await SeeAsync(2, Owner);
        await SeeAsync(2, Owner, Behind);

        Assert.Equal(0, _actions.VaultLocks);
        Assert.Empty(_actions.Guard);
        Assert.Equal(PresenceReaction.Onlooker, _robot.Presence);
    }

    [Fact]
    public async Task lock_windows_only_when_enabled_and_once()
    {
        _config.StackChanLockWindowsWhenAway = true;
        _config.StackChanLockWindowsMinutes = 1;
        await ConnectAsync();
        await SeeAsync(2, Owner);
        await SeeAsync(75);
        Assert.Equal(1, _actions.WindowsLocks);
        await SeeAsync(60);
        Assert.Equal(1, _actions.WindowsLocks);
    }

    [Fact]
    public async Task lock_windows_never_when_disabled()
    {
        await ConnectAsync();
        await SeeAsync(2, Owner);
        await SeeAsync(75);

        Assert.Equal(0, _actions.WindowsLocks);
        Assert.Equal(PresenceReaction.Doze, _robot.Presence);
    }

    [Fact]
    public async Task break_toast_suppressed_in_meeting()
    {
        _config.StackChanBreakMinutes = 15;
        await ConnectAsync();
        using (CompanionActivity.Enter(StackChanActivity.Meeting))
            await SeeAsync(16 * 60, Owner);
        Assert.Equal(0, _actions.BreakToasts);

        await SeeAsync(2, Owner);
        Assert.Equal(1, _actions.BreakToasts);
        Assert.Equal(PresenceReaction.Break, _robot.Presence);
    }

    [Fact]
    public async Task head_turns_toward_the_face_at_once()
    {
        _config.StackChanAllowAutoMotion = true;
        await ConnectAsync();
        await SeeAsync(2, OwnerRight);

        Assert.Contains(MotionPoses(), pose => pose.Yaw < 0);
    }

    [Fact]
    public async Task head_searches_for_a_face_before_any_direction_check()
    {
        _config.StackChanAllowAutoMotion = true;
        await ConnectAsync();
        await SeeAsync(15);

        Assert.Contains(new HeadPose(-40, 25), MotionPoses());
    }

    [Fact]
    public async Task head_learns_and_saves_its_direction_while_centring_the_face()
    {
        _config.StackChanAllowAutoMotion = true;
        await ConnectAsync();
        // This robot's camera is mirrored left-right; the person sits at yaw 10, pitch 30.
        _detector.FaceFor = () =>
        {
            var pose = _transport.LastPose();
            double ex = -(pose.Yaw - 10) / 25, ey = (pose.Pitch - 30) / 18.75;
            return Math.Abs(ex) > 1 || Math.Abs(ey) > 1 ? null
                : new DetectedFace((float)(0.5 + ex / 2), (float)(0.5 + ey / 2), 0.3f, 0.4f, 0.95f);
        };
        await SeeAsync(40);

        Assert.True(_config.StackChanHeadDirectionChecked);
        Assert.True(_config.StackChanHeadInvertYaw);
        Assert.False(_config.StackChanHeadInvertPitch);
        var face = _service.Status.Face;
        Assert.NotNull(face);
        Assert.InRange(face!.CenterX, 0.44f, 0.56f);
        Assert.InRange(face.CenterY, 0.44f, 0.56f);
    }

    private List<HeadPose> MotionPoses()
    {
        lock (_transport.Writes)
            return _transport.Writes.Where(write => write.Channel == StackChanChannel.Motion).Select(write =>
            {
                var root = JsonDocument.Parse(write.Json).RootElement;
                return new HeadPose(root.GetProperty("yawServo").GetProperty("angle").GetInt32() / 10.0,
                    root.GetProperty("pitchServo").GetProperty("angle").GetInt32() / 10.0);
            }).ToList();
    }

    [Fact]
    public async Task head_does_not_move_in_quiet_mode()
    {
        _config.StackChanAllowAutoMotion = true;
        _config.StackChanHeadDirectionChecked = true;
        _config.StackChanQuietMode = true;
        await ConnectAsync();
        await SeeAsync(3, OwnerRight);

        Assert.DoesNotContain(_transport.Writes, write => write.Channel == StackChanChannel.Motion);
    }

    [Fact]
    public async Task gaze_follows_the_main_face()
    {
        await ConnectAsync();
        await SeeAsync(1, OwnerRight);

        var gaze = Commands("presence").Last(p => p.TryGetProperty("gaze", out _)).GetProperty("gaze");
        Assert.Equal(80, gaze.GetProperty("x").GetInt32());
    }

    [Fact]
    public async Task disconnect_clears_guard_and_camera()
    {
        await ConnectAsync();
        await SeeAsync(2, Owner);
        await SeeAsync(2, Owner, Behind);
        Assert.Equal([true], _actions.Guard);

        _transport.RaiseDisconnected();
        await _service.TickAsync();

        Assert.Equal([true, false], _actions.Guard);
        Assert.False(_service.Status.CameraOn);
        Assert.Equal(PresenceState.Unknown, _service.Status.State);
        Assert.Equal(PresenceReaction.None, _robot.Presence);
    }

    [Fact]
    public async Task welcome_scene_lists_away_summary_ascii()
    {
        await ConnectAsync();
        await SeeAsync(2, Owner);
        await SeeAsync(15);
        _service.NoteAlert();
        _service.NoteAlert();
        _service.NoteMetrics(97, 40, 10);
        await SeeAsync(130);
        await SeeAsync(2, Owner);

        var scene = Commands("presence").Last(p => p.TryGetProperty("scene", out _));
        Assert.Equal("welcome", scene.GetProperty("scene").GetString());
        var lines = scene.GetProperty("lines").EnumerateArray().Select(line => line.GetString()!).ToArray();
        Assert.Contains(lines, line => line.Contains("2 alerts", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("CPU 97%", StringComparison.Ordinal));
        Assert.All(lines, line => Assert.True(line.Length <= 32 && line.All(c => c is >= ' ' and <= '~')));
        Assert.Equal(PresenceReaction.Welcome, _robot.Presence);
    }

    [Fact]
    public async Task onlooker_outranks_welcome()
    {
        await ConnectAsync();
        await SeeAsync(2, Owner);
        await SeeAsync(135);
        await SeeAsync(1, Owner);
        Assert.Equal(PresenceReaction.Welcome, _robot.Presence);

        await SeeAsync(2, Owner, Behind);

        Assert.Equal(PresenceReaction.Onlooker, _robot.Presence);
    }

    private async Task<DirectionCheckResult> RunDirectionCheckAsync()
    {
        _service.DirectionSettle = TimeSpan.FromMilliseconds(5);
        var check = _service.CheckDirectionAsync();
        while (!check.IsCompleted)
        {
            _frames.Raise();
            await _service.WhenIdleAsync();
            _clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(5);
        }
        return await check;
    }

    [Fact]
    public async Task direction_check_saves_inverts_from_observed_face_shift()
    {
        await ConnectAsync();
        await SeeAsync(1, Owner);
        // This robot's picture: turning right (+yaw) moves the face right; tilting up (+pitch) moves it up.
        _detector.FaceFor = () =>
        {
            var pose = _transport.LastPose();
            return new DetectedFace(0.5f + (float)pose.Yaw * 0.01f, 0.5f - ((float)pose.Pitch - 25) * 0.01f, 0.3f, 0.4f, 0.95f);
        };

        Assert.Equal(DirectionCheckResult.Saved, await RunDirectionCheckAsync());

        Assert.False(_config.StackChanHeadInvertYaw);
        Assert.True(_config.StackChanHeadInvertPitch);
        Assert.True(_config.StackChanHeadDirectionChecked);
        Assert.Equal(new HeadPose(0, 25), _transport.LastPose());
    }

    [Fact]
    public async Task direction_check_starts_from_where_the_face_was_found()
    {
        await ConnectAsync();
        await SeeAsync(1, Owner);
        _service.CurrentPose = new HeadPose(30, 40);
        // The face is only in view near where the search found it.
        _detector.FaceFor = () =>
        {
            var pose = _transport.Writes.Any(w => w.Channel == StackChanChannel.Motion) ? _transport.LastPose() : new HeadPose(30, 40);
            return Math.Abs(pose.Yaw - 30) <= 10 && Math.Abs(pose.Pitch - 40) <= 10
                ? new DetectedFace(0.5f + (float)(pose.Yaw - 30) * 0.01f, 0.5f - (float)(pose.Pitch - 40) * 0.01f, 0.3f, 0.4f, 0.95f)
                : throw new InvalidOperationException("no face here");
        };

        Assert.Equal(DirectionCheckResult.Saved, await RunDirectionCheckAsync());
        Assert.Equal(new HeadPose(30, 40), _transport.LastPose());
        Assert.False(_config.StackChanHeadInvertYaw);
        Assert.True(_config.StackChanHeadInvertPitch);
    }

    [Fact]
    public async Task direction_check_waits_for_a_picture_that_shows_the_face()
    {
        await ConnectAsync();
        await SeeAsync(1, Owner);
        int frame = 0;
        // The detector misses the face in every other picture, as at the edge of the frame.
        _detector.FaceFor = () =>
        {
            var pose = _transport.LastPose();
            return frame++ % 2 == 0 ? null
                : new DetectedFace(0.5f + (float)pose.Yaw * 0.01f, 0.5f - ((float)pose.Pitch - 25) * 0.01f, 0.3f, 0.4f, 0.95f);
        };

        Assert.Equal(DirectionCheckResult.Saved, await RunDirectionCheckAsync());
    }

    [Fact]
    public async Task status_reports_where_the_face_is()
    {
        await ConnectAsync();
        await SeeAsync(1, OwnerRight);

        Assert.Equal(OwnerRight, _service.Status.Face);
    }

    [Fact]
    public async Task direction_check_needs_a_face()
    {
        await ConnectAsync();
        await SeeAsync(1);

        Assert.Equal(DirectionCheckResult.NoFace, await RunDirectionCheckAsync());
        Assert.False(_config.StackChanHeadDirectionChecked);
    }

    [Fact]
    public async Task direction_check_is_not_ready_in_quiet_mode()
    {
        _config.StackChanQuietMode = true;
        await ConnectAsync();
        await SeeAsync(1, Owner);

        Assert.Equal(DirectionCheckResult.NotReady, await _service.CheckDirectionAsync());
    }

    [Fact]
    public async Task detection_always_runs_on_one_dedicated_thread()
    {
        await ConnectAsync();
        await _service.TickAsync();
        for (int burst = 0; burst < 20; burst++)
        {
            _frames.Raise();
            await _service.WhenIdleAsync();
            await Task.Delay(2);
        }

        Assert.Equal(20, _detector.Threads.Count);
        Assert.Single(_detector.Threads.Distinct());
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, _detector.Threads);
    }

    [Fact]
    public async Task changing_a_timer_while_guarded_lifts_the_cover()
    {
        await ConnectAsync();
        await SeeAsync(2, Owner);
        await SeeAsync(2, Owner, Behind);
        Assert.Equal([true], _actions.Guard);

        _config.StackChanBreakMinutes = 90;
        await _service.TickAsync();

        Assert.Equal([true, false], _actions.Guard);
    }

    [Fact]
    public async Task head_holds_still_while_present_and_briefly_out_of_view()
    {
        _config.StackChanAllowAutoMotion = true;
        _config.StackChanHeadDirectionChecked = true;
        await ConnectAsync();
        await SeeAsync(2, Owner);
        int moves = _transport.Writes.Count(write => write.Channel == StackChanChannel.Motion);

        await SeeAsync(8);

        Assert.Equal(PresenceState.Present, _service.Status.State);
        Assert.Equal(moves, _transport.Writes.Count(write => write.Channel == StackChanChannel.Motion));
    }

    [Fact]
    public async Task dark_frames_never_lock_windows()
    {
        _config.StackChanLockWindowsWhenAway = true;
        _config.StackChanLockWindowsMinutes = 1;
        await ConnectAsync();
        await SeeAsync(2, Owner);
        _detector.Usable = false;
        await SeeAsync(90);

        Assert.Equal(0, _actions.WindowsLocks);
        Assert.Equal(PresenceState.Unknown, _service.Status.State);
        Assert.NotEqual(PresenceReaction.Doze, _robot.Presence);
    }

    [Fact]
    public async Task lock_waits_for_keyboard_and_mouse_idle()
    {
        _config.StackChanLockWindowsWhenAway = true;
        _config.StackChanLockWindowsMinutes = 1;
        _actions.Idle = TimeSpan.FromSeconds(5);
        await ConnectAsync();
        await SeeAsync(2, Owner);
        await SeeAsync(75);

        Assert.Equal(0, _actions.WindowsLocks);
    }

    [Fact]
    public async Task gpu_failure_falls_back_to_cpu_and_says_so()
    {
        _detector.Throw = true;
        _cpuDetector.Faces = [Owner];
        await ConnectAsync();
        await SeeAsync(3);

        Assert.False(_service.Status.OnGpu);
        Assert.Equal("CPU", _service.Status.AdapterName);
        Assert.Contains("CPU", _service.Status.Problem, StringComparison.Ordinal);
        Assert.Equal(PresenceState.Present, _service.Status.State);
        Assert.True(_detector.Disposed);
    }

    [Fact]
    public async Task silent_camera_reports_a_problem()
    {
        await ConnectAsync();
        await _service.TickAsync();
        Assert.Null(_service.Status.Problem);

        _clock.Advance(TimeSpan.FromSeconds(11));
        await _service.TickAsync();

        Assert.Equal("StackChan is not sending pictures.", _service.Status.Problem);
    }

    [Fact]
    public async Task camera_off_is_retried_when_the_writer_is_busy()
    {
        await ConnectAsync();
        await _service.TickAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.Gate = gate.Task;
        Task manual = _robot.SetFaceAsync(StackChanFace.Happy);

        _config.StackChanPresenceEnabled = false;
        await _service.TickAsync();
        gate.SetResult();
        await manual;
        await _service.TickAsync();

        Assert.False(Commands("camera").Last().GetProperty("on").GetBoolean());
    }

    [Fact]
    public async Task dispose_turns_the_camera_off_before_returning()
    {
        await ConnectAsync();
        await _service.TickAsync();
        // The robot answers a little slowly; quitting must not close the port before it does.
        _transport.Gate = Task.Delay(150);

        _service.Dispose();

        Assert.False(Commands("camera").Last().GetProperty("on").GetBoolean());
    }

    [Fact]
    public async Task dispose_waits_for_detection_before_freeing_the_detector()
    {
        await ConnectAsync();
        await _service.TickAsync();
        _detector.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _frames.Raise();
        await _detector.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposing = Task.Run(_service.Dispose);
        await Task.Delay(100);
        Assert.False(_detector.Disposed);
        _detector.Block.SetResult();
        await disposing;

        Assert.True(_detector.Disposed);
        Assert.False(_detector.DisposedWhileDetecting);
    }

    [Fact]
    public async Task direction_check_returns_to_rest_when_a_sample_fails()
    {
        await ConnectAsync();
        await SeeAsync(1, Owner);
        _service.DirectionSampleTimeout = TimeSpan.FromMilliseconds(100);
        int samples = 0;
        _detector.FaceFor = () => ++samples > 1 && _transport.LastPose().Yaw > 5
            ? throw new InvalidOperationException("lost") : Owner;

        Assert.Equal(DirectionCheckResult.NoFace, await RunDirectionCheckAsync());
        Assert.Equal(new HeadPose(0, 25), _transport.LastPose());
        Assert.False(_config.StackChanHeadDirectionChecked);
    }

    [Fact]
    public async Task status_reports_detector_adapter_and_timing()
    {
        await ConnectAsync();
        await SeeAsync(1, Owner);

        Assert.Equal("Fake GPU", _service.Status.AdapterName);
        Assert.True(_service.Status.OnGpu);
        Assert.Equal(3.5, _service.Status.Milliseconds, 3);
        Assert.Equal(1, _service.Status.Faces);
        Assert.Equal(PresenceState.Present, _service.Status.State);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-17T03:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class FakeFrames : IStackChanFrameSource
    {
        private int _sequence;
        public event Action<StackChanFrame>? FrameReceived;
        public void Raise(byte tag = 0) =>
            FrameReceived?.Invoke(new StackChanFrame(_sequence++, [tag, 0xFF, 0xD9], DateTimeOffset.UtcNow));
    }

    private sealed class FakeDetector : IFaceDetector
    {
        private int _detecting;
        public IReadOnlyList<DetectedFace> Faces { get; set; } = [];
        public Func<DetectedFace?>? FaceFor { get; set; }
        public TaskCompletionSource? Block { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<byte[]> Seen { get; } = [];
        public List<int> Threads { get; } = [];
        public bool Usable { get; set; } = true;
        public bool Throw { get; set; }
        public bool Disposed { get; private set; }
        public bool DisposedWhileDetecting { get; private set; }
        public string Name { get; init; } = "Fake GPU";
        public bool Gpu { get; init; } = true;
        public string AdapterName => Name;
        public bool OnGpu => Gpu;

        public IReadOnlyList<DetectedFace> Detect(byte[] jpeg, out double milliseconds, out bool usable)
        {
            Interlocked.Increment(ref _detecting);
            try
            {
                lock (Seen)
                {
                    Seen.Add(jpeg);
                    Threads.Add(Environment.CurrentManagedThreadId);
                }
                Started.TrySetResult();
                Block?.Task.Wait(TimeSpan.FromSeconds(5));
                if (Throw) throw new InvalidOperationException("Synthetic device removed.");
                milliseconds = 3.5;
                usable = Usable;
                return FaceFor is { } faceFor ? (faceFor() is { } face ? [face] : []) : Faces;
            }
            finally { Interlocked.Decrement(ref _detecting); }
        }

        public void Dispose()
        {
            DisposedWhileDetecting |= Volatile.Read(ref _detecting) > 0;
            Disposed = true;
        }
    }

    private sealed class FakeActions : IPresenceActions
    {
        public TimeSpan Idle { get; set; } = TimeSpan.FromHours(1);
        public TimeSpan UserIdleFor() => Idle;
        public int VaultLocks { get; private set; }
        public int WindowsLocks { get; private set; }
        public int BreakToasts { get; private set; }
        public List<bool> Guard { get; } = [];
        public void LockVault() => VaultLocks++;
        public void ShowGuard(bool on) => Guard.Add(on);
        public void LockWindows() => WindowsLocks++;
        public void ShowBreakToast() => BreakToasts++;
    }

    private sealed record Write(StackChanChannel Channel, byte[] Json);

    private sealed class FakeTransport : IStackChanTransport, IStackChanUsbCompanionStarter
    {
        public event Action? Disconnected;
        public List<Write> Writes { get; } = [];
        public Task Gate { get; set; } = Task.CompletedTask;
        public Task<IReadOnlyList<StackChanDevice>> DiscoverAsync(TimeSpan duration, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StackChanDevice>>([UsbRobot]);
        public Task<StackChanCapabilities> ConnectAsync(StackChanDevice device, CancellationToken cancellationToken) =>
            Task.FromResult(StackChanCapabilities.Avatar | StackChanCapabilities.Rgb | StackChanCapabilities.Motion |
                StackChanCapabilities.Dashboard);
        public Task<StackChanCapabilities> StartCompanionAsync(StackChanDevice device, byte[] script, CancellationToken cancellationToken) =>
            ConnectAsync(device, cancellationToken);
        public async Task WriteAsync(StackChanChannel channel, byte[] json, CancellationToken cancellationToken)
        {
            await Gate.WaitAsync(cancellationToken);
            lock (Writes) Writes.Add(new Write(channel, (byte[])json.Clone()));
        }
        public Task DisconnectAsync() => Task.CompletedTask;
        public void RaiseDisconnected() => Disconnected?.Invoke();

        public HeadPose LastPose()
        {
            lock (Writes)
            {
                var motion = Writes.LastOrDefault(write => write.Channel == StackChanChannel.Motion);
                if (motion == null) return new HeadPose(0, 25);
                var root = JsonDocument.Parse(motion.Json).RootElement;
                return new HeadPose(root.GetProperty("yawServo").GetProperty("angle").GetInt32() / 10.0,
                    root.GetProperty("pitchServo").GetProperty("angle").GetInt32() / 10.0);
            }
        }
        public void Dispose() { }
    }
}
