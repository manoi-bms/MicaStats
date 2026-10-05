using System;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Conference;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class MeetingPlaybackTests
{
    [Fact]
    public async Task Playback_waits_for_completion_before_releasing_the_audio_operation()
    {
        var operation = new ScriptedPlaybackOperation();
        var playback = new WasapiMeetingPlayback(new ScriptedPlaybackFactory(operation));

        Task playing = playback.PlayAsync(new byte[] { 1, 2, 3 }, "render-1", CancellationToken.None);

        Assert.True(operation.PlayCalled);
        Assert.False(operation.Disposed);
        Assert.False(playing.IsCompleted);

        operation.Complete();
        await playing;

        Assert.True(operation.Disposed);
    }

    [Fact]
    public async Task Cancellation_stops_then_joins_playback_before_disposal()
    {
        var operation = new ScriptedPlaybackOperation();
        var playback = new WasapiMeetingPlayback(new ScriptedPlaybackFactory(operation));
        using var cancellation = new CancellationTokenSource();
        Task playing = playback.PlayAsync(new byte[] { 1 }, "render-1", cancellation.Token);

        cancellation.Cancel();

        await WaitUntilAsync(() => operation.StopCalled);
        Assert.True(operation.StopCalled);
        Assert.False(operation.Disposed);
        Assert.False(playing.IsCompleted);

        operation.Complete();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playing);
        Assert.True(operation.Disposed);
    }

    [Fact]
    public async Task A_second_playback_is_rejected_while_the_first_is_active()
    {
        var operation = new ScriptedPlaybackOperation();
        var playback = new WasapiMeetingPlayback(new ScriptedPlaybackFactory(operation));
        Task first = playback.PlayAsync(new byte[] { 1 }, "render-1", CancellationToken.None);

        MeetingException error = await Assert.ThrowsAsync<MeetingException>(() =>
            playback.PlayAsync(new byte[] { 2 }, "render-1", CancellationToken.None));

        Assert.Equal("Speech playback is already active.", error.Message);
        operation.Complete();
        await first;
    }

    [Fact]
    public async Task Cancellation_is_bounded_when_the_native_completion_callback_never_arrives()
    {
        var operation = new ScriptedPlaybackOperation();
        var timeout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playback = new WasapiMeetingPlayback(
            new ScriptedPlaybackFactory(operation),
            TimeSpan.FromSeconds(5),
            _ => timeout.Task);
        using var cancellation = new CancellationTokenSource();
        Task playing = playback.PlayAsync(new byte[] { 1 }, "render-1", cancellation.Token);

        cancellation.Cancel();
        await WaitUntilAsync(() => operation.StopCalled);
        timeout.SetResult();

        await Assert.ThrowsAsync<MeetingPlaybackStopException>(() => playing);
        Assert.True(operation.Disposed);
    }

    [Fact]
    public async Task A_blocked_native_stop_retains_resources_and_blocks_new_playback_until_cleanup()
    {
        var stopBlock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new ScriptedPlaybackOperation(stopBlock.Task);
        var timeout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playback = new WasapiMeetingPlayback(
            new ScriptedPlaybackFactory(operation),
            TimeSpan.FromSeconds(5),
            _ => timeout.Task);
        using var cancellation = new CancellationTokenSource();
        Task playing = playback.PlayAsync(new byte[] { 1 }, "render-1", cancellation.Token);

        cancellation.Cancel();
        await WaitUntilAsync(() => operation.StopCalled);
        timeout.SetResult();

        await Assert.ThrowsAsync<MeetingPlaybackStopException>(() => playing);
        Assert.False(operation.Disposed);
        await Assert.ThrowsAsync<MeetingException>(() =>
            playback.PlayAsync(new byte[] { 2 }, "render-1", CancellationToken.None));

        stopBlock.SetResult();
        await operation.DisposedSignal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(operation.Disposed);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Yield();
        Assert.True(condition());
    }

    private sealed class ScriptedPlaybackFactory : IMeetingPlaybackOperationFactory
    {
        private readonly ScriptedPlaybackOperation _operation;

        public ScriptedPlaybackFactory(ScriptedPlaybackOperation operation)
        {
            _operation = operation;
        }

        public IMeetingPlaybackOperation Create(byte[] wav, string deviceId)
        {
            Assert.NotEmpty(wav);
            Assert.Equal("render-1", deviceId);
            return _operation;
        }
    }

    private sealed class ScriptedPlaybackOperation : IMeetingPlaybackOperation
    {
        private readonly Task? _stopBlock;
        private readonly TaskCompletionSource _disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ScriptedPlaybackOperation(Task? stopBlock = null)
        {
            _stopBlock = stopBlock;
        }

        public event Action<Exception?>? Stopped;
        public bool PlayCalled { get; private set; }
        public bool StopCalled { get; private set; }
        public bool Disposed { get; private set; }
        public Task DisposedSignal => _disposed.Task;

        public void Play() => PlayCalled = true;
        public void Stop()
        {
            StopCalled = true;
            _stopBlock?.GetAwaiter().GetResult();
        }
        public void Complete() => Stopped?.Invoke(null);
        public void Dispose()
        {
            Disposed = true;
            _disposed.TrySetResult();
        }
    }
}
