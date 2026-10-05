using System.IO;
using System.Text.Json;
using Kil0bitSystemMonitor.Services.Conference;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class MeetingTranscriptPersistenceTests
{
    [Fact]
    public async Task Received_text_is_saved_while_listening_without_a_close_or_explicit_flush()
    {
        using var folder = new TestFolder();
        var io = new Boundary();
        await using var session = new MeetingSession(io, io, transcriptStore: folder.Store);
        await session.StartAsync(new("mic", "out"));
        io.Emit(4);
        await WaitUntil(() => session.Segments.Count == 1 && session.AutosaveStatus.Contains("saved locally"));

        var saved = folder.Store.LoadLatest(out var warning);
        Assert.False(warning);
        Assert.True(session.IsActive);
        Assert.Equal(Assert.Single(session.Segments), Assert.Single(saved!.Segments));
        Assert.Equal("สรุปการประชุมภาษาไทย", saved.Segments[0].Text);
        Assert.Contains("> สรุปการประชุมภาษาไทย", File.ReadAllText(Assert.Single(Directory.GetFiles(folder.Root, "*.md"))));
        Assert.Empty(Directory.GetFiles(folder.Root, "*.wav"));
    }

    [Fact]
    public async Task Recreated_session_restores_both_readings_and_gap_metadata_without_starting_io()
    {
        using var folder = new TestFolder();
        var io = new Boundary();
        MeetingSegment original;
        MeetingGap gap;
        await using (var session = new MeetingSession(io, io, transcriptStore: folder.Store))
        {
            await session.StartAsync(new("mic", "out", CompareBothServices: true));
            io.Emit(4);
            await WaitUntil(() => session.Segments.Count == 1);
            await session.PauseAsync();
            await session.ResumeAsync();
            original = Assert.Single(session.Segments);
            gap = Assert.Single(session.Gaps);
            Assert.NotNull(gap.End);
        }
        var restoredIo = new Boundary();
        await using var restored = new MeetingSession(restoredIo, restoredIo, transcriptStore: folder.Store);
        Assert.Equal(original, Assert.Single(restored.Segments));
        Assert.Equal("อีกข้อความหนึ่ง", restored.Segments[0].Comparison!.AlternativeText);
        Assert.Equal(gap, Assert.Single(restored.Gaps));
        Assert.Equal(MeetingState.Stopped, restored.State);
        Assert.Equal(0, restoredIo.Starts);
        Assert.Equal(0, restoredIo.Requests);
        Assert.Contains("Restored", restored.Status);
    }

    [Fact]
    public async Task Dispose_flushes_the_last_received_segment()
    {
        using var folder = new TestFolder();
        var io = new Boundary();
        var session = new MeetingSession(io, io, transcriptStore: folder.Store);
        try
        {
            await session.StartAsync(new("mic", "out"));
            for (var i = 0; i < 4; i++)
            {
                io.Emit(i * 4);
                await WaitUntil(() => session.Segments.Count == i + 1);
            }
        }
        finally { await session.DisposeAsync(); }
        Assert.Equal(4, folder.Store.LoadLatest(out _)!.Segments.Length);
    }

    [Fact]
    public async Task New_meeting_keeps_previous_files_and_restores_the_latest_nonempty_meeting()
    {
        using var folder = new TestFolder();
        var io = new Boundary();
        await using var session = new MeetingSession(io, io, transcriptStore: folder.Store);
        await session.StartAsync(new("mic", "out"));
        io.Emit(0);
        await WaitUntil(() => session.Segments.Count == 1);
        Assert.True(await session.FlushTranscriptAsync());
        var first = folder.Store.LoadLatest(out _)!;
        var originalBytes = File.ReadAllBytes(Path.Combine(folder.Root, first.Id + ".json"));
        await session.StopAsync();
        await session.StartAsync(new("mic", "out"));
        Assert.Empty(session.Segments);
        Assert.Equal(first.Id, folder.Store.LoadLatest(out _)!.Id);
        io.Text = "Second meeting";
        io.Emit(0);
        await WaitUntil(() => session.Segments.Count == 1);
        Assert.True(await session.FlushTranscriptAsync());
        Assert.Equal(2, Directory.GetFiles(folder.Root, "*.md").Length);
        Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(folder.Root, first.Id + ".json")));
        var latest = folder.Store.LoadLatest(out _)!;
        Assert.NotEqual(first.Id, latest.Id);
        Assert.Equal("Second meeting", Assert.Single(latest.Segments).Text);
    }

    [Fact]
    public void Interrupted_complete_save_is_recovered_and_incomplete_temp_is_ignored()
    {
        using var folder = new TestFolder();
        var first = Snapshot("Before update");
        folder.Store.Save(first);
        var path = Path.Combine(folder.Root, first.Id + ".json");
        File.WriteAllText(path + AtomicFile.TempSuffix, "incomplete");
        Assert.Equal("Before update", folder.Store.LoadLatest(out _)!.Segments[0].Text);
        var next = first with { Segments = [first.Segments[0] with { Text = "After update" }] };
        Assert.Throws<IOException>(() => AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(next),
            () => throw new IOException("Synthetic interruption")));
        Assert.True(File.Exists(path + AtomicFile.ReadySuffix));
        Assert.Equal("After update", folder.Store.LoadLatest(out var warning)!.Segments[0].Text);
        Assert.False(warning);
        Assert.False(File.Exists(path + AtomicFile.ReadySuffix));
    }

    [Fact]
    public void First_save_ready_file_restores_even_without_a_committed_json_file()
    {
        using var folder = new TestFolder();
        Directory.CreateDirectory(folder.Root);
        var snapshot = Snapshot("First save");
        var path = Path.Combine(folder.Root, snapshot.Id + ".json");
        File.WriteAllBytes(path + AtomicFile.ReadySuffix, JsonSerializer.SerializeToUtf8Bytes(snapshot));
        Assert.Equal(snapshot.Id, folder.Store.LoadLatest(out var warning)!.Id);
        Assert.False(warning);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Malformed_latest_file_is_retained_and_an_older_valid_transcript_is_recovered()
    {
        using var folder = new TestFolder();
        var older = Snapshot("Keep this meeting");
        folder.Store.Save(older);
        var badPath = Path.Combine(folder.Root, "meeting-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(badPath, "{incomplete");
        File.SetLastWriteTimeUtc(badPath, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(older.Id, folder.Store.LoadLatest(out var warning)!.Id);
        Assert.True(warning);
        Assert.Equal("{incomplete", File.ReadAllText(badPath));
    }

    [Fact]
    public void Unsupported_recovery_version_is_retained_and_skipped()
    {
        using var folder = new TestFolder();
        var older = Snapshot("Readable meeting");
        folder.Store.Save(older);
        var unsupported = Snapshot("Future format") with { Version = 999 };
        var path = Path.Combine(folder.Root, unsupported.Id + ".json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(unsupported);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(older.Id, folder.Store.LoadLatest(out var warning)!.Id);
        Assert.True(warning);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Failed_save_is_visible_blocks_clearing_and_retries_after_storage_is_repaired()
    {
        using var folder = new TestFolder();
        File.WriteAllText(folder.Root, "Synthetic blocked directory");
        var io = new Boundary();
        var session = new MeetingSession(io, io, transcriptStore: folder.Store);
        try
        {
            await session.StartAsync(new("mic", "out"));
            io.Emit(0);
            await WaitUntil(() => session.AutosaveStatus.Contains("save failed"));
            await session.StopAsync();
            await Assert.ThrowsAsync<MeetingException>(() => session.StartAsync(new("mic", "out")));
            Assert.Single(session.Segments);
            Assert.Equal(1, io.Starts);
            File.Delete(folder.Root);
            Assert.True(await session.FlushTranscriptAsync());
            Assert.Contains("saved locally", session.AutosaveStatus);
            Assert.Equal("สรุปการประชุมภาษาไทย", folder.Store.LoadLatest(out _)!.Segments[0].Text);
        }
        finally
        {
            if (File.Exists(folder.Root)) File.Delete(folder.Root);
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task Stop_during_a_new_meetings_save_wait_prevents_capture_from_starting_later()
    {
        using var folder = new TestFolder();
        File.WriteAllText(folder.Root, "Synthetic blocked directory");
        var io = new Boundary();
        var session = new MeetingSession(io, io, transcriptStore: folder.Store);
        try
        {
            await session.StartAsync(new("mic", "out"));
            io.Emit(0);
            await WaitUntil(() => session.AutosaveStatus.Contains("save failed"));
            await session.StopAsync();
            var starting = session.StartAsync(new("mic", "out"));
            var stopping = session.StopAsync();
            File.Delete(folder.Root);
            await Assert.ThrowsAsync<MeetingException>(() => starting);
            await stopping;
            Assert.Equal(1, io.Starts);
            Assert.Equal(MeetingState.Stopped, session.State);
            Assert.Single(session.Segments);
            Assert.True(await session.FlushTranscriptAsync());
        }
        finally
        {
            if (File.Exists(folder.Root)) File.Delete(folder.Root);
            await session.DisposeAsync();
        }
    }

    [Fact]
    public void Snapshot_ids_cannot_escape_the_store_directory()
    {
        using var folder = new TestFolder();
        Assert.Throws<InvalidDataException>(() => folder.Store.Save(Snapshot("text") with { Id = "../outside" }));
        Assert.False(Directory.Exists(folder.Root));
    }

    private static MeetingTranscriptSnapshot Snapshot(string text) => new(1, "meeting-" + Guid.NewGuid().ToString("N"),
        DateTimeOffset.UtcNow, [new("segment-1", MeetingSource.Output, TimeSpan.Zero, TimeSpan.FromSeconds(4), text)], []);

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class TestFolder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "meeting-save-test-" + Guid.NewGuid().ToString("N"));
        public MeetingTranscriptStore Store => new(Root);
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            if (File.Exists(Root)) File.Delete(Root);
        }
    }

    private sealed class Boundary : IMeetingCapture, IMeetingAsr
    {
        private Action<MeetingAudioChunk>? _chunk;
        public int Starts, Requests;
        public string Text = "สรุปการประชุมภาษาไทย";
        public IReadOnlyList<MeetingDevice> GetDevices(MeetingSource source) => [];
        public Task StartAsync(string microphoneId, string outputId, Action<MeetingAudioChunk> onChunk,
            Action<string> onFault, CancellationToken cancellationToken)
        {
            _chunk = onChunk;
            Starts++;
            return Task.CompletedTask;
        }
        public void Emit(int seconds) => _chunk!(new(MeetingSource.Output, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(4), [1]));
        public Task PauseAsync(CancellationToken cancellationToken, Action<TimeSpan>? onFirstSourceStopped = null)
        {
            onFirstSourceStopped?.Invoke(TimeSpan.Zero);
            return Task.CompletedTask;
        }
        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<string> TranscribeAsync(MeetingAudioChunk chunk, AsrService service, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(service == AsrService.Asr2 ? Text : "อีกข้อความหนึ่ง");
        }
    }
}
