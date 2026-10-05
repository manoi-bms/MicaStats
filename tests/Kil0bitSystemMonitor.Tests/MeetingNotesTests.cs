using System.Windows.Threading;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Conference;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class MeetingNotesTests
{
    private const string NoteId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Read_uses_note_tool_credential_filtering_budget_and_deterministic_source_id()
    {
        var reader = new ReaderFake
        {
            Note = new NoteText(NoteId, "VPN {{secret:K7Q2M9XD}}", "first\n{{secret:K7Q2M9XD}}\nthird")
        };
        var budget = AiBudget.Standard with { NoteReadLines = 2, NoteReadTokens = 100 };
        var notes = new MeetingNotes(() => null, () => false, Dispatcher.CurrentDispatcher,
            reader, () => budget);

        MeetingReference? reference = await notes.ReadAsync(NoteId, CancellationToken.None);

        Assert.NotNull(reference);
        Assert.Equal("note-" + NoteId, reference!.Id);
        Assert.Equal("VPN [credential]", reference.Title);
        Assert.Equal("first\n[credential]", reference.Text);
        Assert.DoesNotContain("K7Q2", reference.Title + reference.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_uses_read_only_metadata_and_the_sanitized_live_title_of_open_notes()
    {
        using var directory = new PadTempDir();
        var store = new NoteStore(directory.Root, _ => { });
        using var workspace = new PadWorkspace(store);
        OpenNote open = workspace.NewNote();
        open.TextProvider = () => "# Live {{secret:K7Q2M9XD}} title\nbody";
        var notes = new MeetingNotes(() => workspace, () => true, Dispatcher.CurrentDispatcher,
            new ReaderFake(), () => AiBudget.Standard);

        IReadOnlyList<MeetingNoteChoice> choices = await notes.ListAsync(CancellationToken.None);

        MeetingNoteChoice choice = Assert.Single(choices, item => item.Id == open.Id);
        Assert.Contains("Live [credential]", choice.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("K7Q2", choice.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_lists_metadata_without_reading_note_content_until_explicit_selection()
    {
        var notes = new NotesFake(
            new[] { new MeetingNoteChoice("n1", "Plan") },
            (_, _) => Task.FromResult<MeetingReference?>(new MeetingReference("note-n1", "Plan", "eligible")));
        await using var fixture = new IntelligenceFixture();
        await using var selection = new MeetingReferenceSelection(notes, fixture.Intelligence);

        await selection.RefreshAvailableAsync();

        Assert.Single(selection.Available);
        Assert.Empty(selection.Selected);
        Assert.Equal(0, notes.ReadCount);

        await selection.SelectAsync("n1");

        Assert.Single(selection.Selected);
        Assert.Single(fixture.Intelligence.References);
        Assert.Equal("eligible", fixture.Intelligence.References[0].Text);
        Assert.Equal(1, notes.ReadCount);
    }

    [Fact]
    public async Task Removing_a_reference_rejects_a_late_read_and_invalidates_context_first()
    {
        var read = new TaskCompletionSource<MeetingReference?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notes = new NotesFake(new[] { new MeetingNoteChoice("n1", "Plan") }, (_, _) => read.Task);
        await using var fixture = new IntelligenceFixture();
        await using var selection = new MeetingReferenceSelection(notes, fixture.Intelligence);
        await selection.RefreshAvailableAsync();
        var invalidations = 0;
        selection.ContextInvalidated += () => invalidations++;

        Task selecting = selection.SelectAsync("n1");
        await EventuallyAsync(() => selection.Selected.Count == 1);
        selection.Remove("n1");
        read.SetResult(new MeetingReference("note-n1", "Plan", "late secret context"));
        await selecting;

        Assert.Empty(selection.Selected);
        Assert.Empty(fixture.Intelligence.References);
        Assert.Equal(1, invalidations);
    }

    [Fact]
    public async Task Removal_cannot_be_overwritten_by_an_older_context_publish()
    {
        var read = new TaskCompletionSource<MeetingReference?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notes = new NotesFake(new[] { new MeetingNoteChoice("n1", "Plan") }, (_, _) => read.Task);
        await using var fixture = new IntelligenceFixture();
        await using var selection = new MeetingReferenceSelection(notes, fixture.Intelligence);
        await selection.RefreshAvailableAsync();
        using var publishEntered = new ManualResetEventSlim();
        using var releasePublish = new ManualResetEventSlim();
        selection.ContextInvalidated += () =>
        {
            publishEntered.Set();
            releasePublish.Wait(TimeSpan.FromSeconds(3));
        };

        Task selecting = selection.SelectAsync("n1");
        read.SetResult(new MeetingReference("note-n1", "Plan", "old context"));
        await Task.Run(() => Assert.True(publishEntered.Wait(TimeSpan.FromSeconds(3))));
        Task removing = Task.Run(() => selection.Remove("n1"));
        await Task.Delay(30);
        releasePublish.Set();
        await Task.WhenAll(selecting, removing);

        Assert.Empty(selection.Selected);
        Assert.Empty(fixture.Intelligence.References);
    }

    [Fact]
    public async Task Credential_invalidation_clears_context_synchronously_and_reloads_selected_notes()
    {
        var reloaded = new TaskCompletionSource<MeetingReference?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var notes = new NotesFake(new[] { new MeetingNoteChoice("n1", "Plan") }, (_, _) =>
        {
            reads++;
            return reads == 1
                ? Task.FromResult<MeetingReference?>(new MeetingReference("note-n1", "Plan", "before"))
                : reloaded.Task;
        });
        await using var fixture = new IntelligenceFixture();
        await using var selection = new MeetingReferenceSelection(notes, fixture.Intelligence);
        await selection.RefreshAvailableAsync();
        await selection.SelectAsync("n1");
        var invalidations = 0;
        selection.ContextInvalidated += () => invalidations++;

        Task reload = selection.InvalidateCredentialsAsync();

        Assert.Empty(fixture.Intelligence.References);
        Assert.Equal(1, invalidations);
        reloaded.SetResult(new MeetingReference("note-n1", "Plan", "after filtering"));
        await reload;

        Assert.Single(selection.Selected);
        Assert.Equal("after filtering", fixture.Intelligence.References.Single().Text);
        Assert.Equal(2, invalidations);
    }

    [Fact]
    public async Task Credential_reload_holds_analysis_until_filtered_notes_are_ready()
    {
        var reloaded = new TaskCompletionSource<MeetingReference?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var notes = new NotesFake(new[] { new MeetingNoteChoice("n1", "Plan") }, (_, _) =>
        {
            reads++;
            return reads == 1
                ? Task.FromResult<MeetingReference?>(new MeetingReference("note-n1", "Plan", "before"))
                : reloaded.Task;
        });
        await using var fixture = new IntelligenceFixture();
        await using var selection = new MeetingReferenceSelection(notes, fixture.Intelligence);
        await selection.RefreshAvailableAsync();
        await selection.SelectAsync("n1");
        await fixture.StartAsync();
        fixture.Capture.Emit(Chunk(0));
        await EventuallyAsync(() => fixture.Analyzer.Calls == 1);

        Task reload = selection.InvalidateCredentialsAsync();
        fixture.Capture.Emit(Chunk(4));
        MeetingException held = await Assert.ThrowsAsync<MeetingException>(
            () => fixture.Intelligence.AskAsync("What changed?"));
        await Task.Delay(50);

        Assert.Contains("reload", held.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.Analyzer.Calls);
        reloaded.SetResult(new MeetingReference("note-n1", "Plan", "after filtering"));
        await reload;
        Assert.Equal("after filtering", fixture.Intelligence.References.Single().Text);
    }

    [Fact]
    public async Task New_session_reset_clears_selection_and_rejects_late_note_reads()
    {
        var late = new TaskCompletionSource<MeetingReference?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notes = new NotesFake(new[]
        {
            new MeetingNoteChoice("n1", "Old"),
            new MeetingNoteChoice("n2", "Pending"),
        }, (id, _) => id == "n1"
            ? Task.FromResult<MeetingReference?>(new MeetingReference("note-n1", "Old", "old meeting"))
            : late.Task);
        await using var fixture = new IntelligenceFixture();
        await using var selection = new MeetingReferenceSelection(notes, fixture.Intelligence);
        await selection.RefreshAvailableAsync();
        await selection.SelectAsync("n1");
        Task pending = selection.SelectAsync("n2");
        await EventuallyAsync(() => selection.Selected.Count == 2);

        selection.ResetForNewSession();
        late.SetResult(new MeetingReference("note-n2", "Pending", "late old meeting"));
        await pending;

        Assert.Empty(selection.Selected);
        Assert.Empty(fixture.Intelligence.References);
        Assert.Equal(2, selection.Available.Count);
    }

    [Fact]
    public async Task Removing_during_credential_reload_releases_analysis_and_rejects_the_old_reload()
    {
        var reloaded = new TaskCompletionSource<MeetingReference?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var notes = new NotesFake(new[] { new MeetingNoteChoice("n1", "Plan") }, (_, _) =>
        {
            reads++;
            return reads == 1
                ? Task.FromResult<MeetingReference?>(new MeetingReference("note-n1", "Plan", "before"))
                : reloaded.Task;
        });
        await using var fixture = new IntelligenceFixture();
        await using var selection = new MeetingReferenceSelection(notes, fixture.Intelligence);
        await selection.RefreshAvailableAsync();
        await selection.SelectAsync("n1");
        await fixture.StartAsync();
        fixture.Capture.Emit(Chunk(0));
        await EventuallyAsync(() => fixture.Analyzer.Calls == 1);

        Task reload = selection.InvalidateCredentialsAsync();
        selection.Remove("n1");
        await fixture.Intelligence.AskAsync("Can analysis continue?");
        reloaded.SetResult(new MeetingReference("note-n1", "Plan", "obsolete"));
        await reload;

        Assert.Equal(2, fixture.Analyzer.Calls);
        Assert.Empty(selection.Selected);
        Assert.Empty(fixture.Intelligence.References);
    }

    [Fact]
    public async Task Selection_is_bounded_to_eight_explicit_notes()
    {
        var available = Enumerable.Range(1, 9).Select(i => new MeetingNoteChoice("n" + i, "Note " + i)).ToArray();
        var notes = new NotesFake(available, (id, _) =>
            Task.FromResult<MeetingReference?>(new MeetingReference("note-" + id, id, "text")));
        await using var fixture = new IntelligenceFixture();
        await using var selection = new MeetingReferenceSelection(notes, fixture.Intelligence);
        await selection.RefreshAvailableAsync();
        foreach (var choice in available.Take(8))
            await selection.SelectAsync(choice.Id);

        MeetingException exception = await Assert.ThrowsAsync<MeetingException>(() => selection.SelectAsync("n9"));

        Assert.Contains("8", exception.Message, StringComparison.Ordinal);
        Assert.Equal(8, selection.Selected.Count);
        Assert.Equal(8, fixture.Intelligence.References.Count);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static MeetingAudioChunk Chunk(int startSeconds) =>
        new(MeetingSource.Microphone, TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(4), new byte[] { 1 });

    private sealed class ReaderFake : INoteReader
    {
        public NoteText? Note { get; init; }
        public Task<NoteSearchResult> SearchAsync(string query, CancellationToken ct) =>
            Task.FromResult(new NoteSearchResult(Array.Empty<NoteHit>(), false));
        public Task<NoteText?> ReadAsync(string noteId, CancellationToken ct) => Task.FromResult(Note);
    }

    private sealed class NotesFake(
        IReadOnlyList<MeetingNoteChoice> available,
        Func<string, CancellationToken, Task<MeetingReference?>> read) : IMeetingNotes
    {
        public int ReadCount { get; private set; }
        public Task<IReadOnlyList<MeetingNoteChoice>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult(available);
        public Task<MeetingReference?> ReadAsync(string id, CancellationToken cancellationToken)
        {
            ReadCount++;
            return read(id, cancellationToken);
        }
    }

    private sealed class IntelligenceFixture : IAsyncDisposable
    {
        private readonly MeetingSession _session;
        public CaptureFake Capture { get; }
        public AnalyzerFake Analyzer { get; }
        public MeetingIntelligence Intelligence { get; }

        public IntelligenceFixture()
        {
            Capture = new CaptureFake();
            Analyzer = new AnalyzerFake();
            _session = new MeetingSession(Capture, new AsrFake());
            Intelligence = new MeetingIntelligence(_session, Analyzer);
        }

        public Task StartAsync() => _session.StartAsync(new MeetingStartOptions("mic", "out"));

        public async ValueTask DisposeAsync()
        {
            await Intelligence.DisposeAsync();
            await _session.DisposeAsync();
        }
    }

    private sealed class CaptureFake : IMeetingCapture
    {
        private Action<MeetingAudioChunk>? _onChunk;
        public IReadOnlyList<MeetingDevice> GetDevices(MeetingSource source) => Array.Empty<MeetingDevice>();
        public Task StartAsync(string microphoneId, string outputId, Action<MeetingAudioChunk> onChunk,
            Action<string> onFault, CancellationToken cancellationToken)
        {
            _onChunk = onChunk;
            return Task.CompletedTask;
        }
        public Task PauseAsync(CancellationToken cancellationToken, Action<TimeSpan>? onFirstSourceStopped = null) =>
            Task.CompletedTask;
        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Emit(MeetingAudioChunk chunk) => _onChunk?.Invoke(chunk);
    }

    private sealed class AsrFake : IMeetingAsr
    {
        public Task<string> TranscribeAsync(MeetingAudioChunk chunk, AsrService service,
            CancellationToken cancellationToken) => Task.FromResult("meeting text");
    }

    private sealed class AnalyzerFake : IMeetingAnalyzer
    {
        public int Calls { get; private set; }
        public Task<MeetingAnalysis> AnalyzeAsync(MeetingContext context, string? question,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new MeetingAnalysis(
                string.Empty, Array.Empty<MeetingPoint>(), Array.Empty<MeetingQuestion>()));
        }
    }
}
