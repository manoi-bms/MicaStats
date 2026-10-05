using Kil0bitSystemMonitor.Services.Conference;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class MeetingIntelligenceTests
{
    [Fact]
    public async Task Automatic_analysis_coalesces_new_segments_and_observes_the_ten_second_cadence()
    {
        var capture = new CaptureFake();
        await using var session = new MeetingSession(capture, new ImmediateAsr());
        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        var first = new TaskCompletionSource<MeetingAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        var analyzer = new AnalyzerFake((_, _) => first.Task);
        await using var intelligence = new MeetingIntelligence(session, analyzer);

        capture.Emit(Chunk(0));
        capture.Emit(Chunk(4));
        capture.Emit(Chunk(8));
        await EventuallyAsync(() => session.Segments.Count == 3 && analyzer.Calls.Count == 1);
        first.SetResult(Result("first pass"));
        await EventuallyAsync(() => intelligence.Analysis?.Summary == "first pass");
        await Task.Delay(50);

        Assert.Single(analyzer.Calls);
        Assert.True(intelligence.Analysis!.Summary == "first pass");
    }

    [Fact]
    public async Task Manual_question_supersedes_an_automatic_request_even_when_it_ignores_cancellation()
    {
        var capture = new CaptureFake();
        await using var session = new MeetingSession(capture, new ImmediateAsr());
        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        var automatic = new TaskCompletionSource<MeetingAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manual = new TaskCompletionSource<MeetingAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        var analyzer = new AnalyzerFake((_, question) => question is null ? automatic.Task : manual.Task);
        await using var intelligence = new MeetingIntelligence(session, analyzer);
        capture.Emit(Chunk(0));
        await EventuallyAsync(() => analyzer.Calls.Count == 1);

        var ask = intelligence.AskAsync("What was decided?");
        await Task.Delay(30);
        Assert.Single(analyzer.Calls);
        automatic.SetResult(Result("obsolete automatic answer"));
        await EventuallyAsync(() => analyzer.Calls.Count == 2);
        manual.SetResult(Result("manual answer"));
        await ask;
        await Task.Delay(30);

        Assert.Equal("manual answer", intelligence.Analysis?.Summary);
        Assert.Equal("What was decided?", analyzer.Calls[1].Question);
    }

    [Fact]
    public async Task Stop_rejects_a_late_analysis_result()
    {
        var capture = new CaptureFake();
        await using var session = new MeetingSession(capture, new ImmediateAsr());
        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        var completion = new TaskCompletionSource<MeetingAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        var analyzer = new AnalyzerFake((_, _) => completion.Task);
        await using var intelligence = new MeetingIntelligence(session, analyzer);
        capture.Emit(Chunk(0));
        await EventuallyAsync(() => analyzer.Calls.Count == 1);

        await session.StopAsync();
        completion.SetResult(Result("late"));
        await Task.Delay(30);

        Assert.Null(intelligence.Analysis);
        Assert.Contains("stopped", intelligence.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refresh_configuration_clears_results_and_rejects_obsolete_completion()
    {
        var capture = new CaptureFake();
        await using var session = new MeetingSession(capture, new ImmediateAsr());
        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        var completion = new TaskCompletionSource<MeetingAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        var analyzer = new AnalyzerFake((_, _) => completion.Task);
        await using var intelligence = new MeetingIntelligence(session, analyzer);
        capture.Emit(Chunk(0));
        await EventuallyAsync(() => analyzer.Calls.Count == 1);

        intelligence.RefreshConfiguration();
        completion.SetResult(Result("old provider"));
        await Task.Delay(30);

        Assert.Null(intelligence.Analysis);
    }

    [Fact]
    public async Task Export_quotes_analysis_text_and_preserves_valid_source_ids()
    {
        var capture = new CaptureFake();
        await using var session = new MeetingSession(capture, new ImmediateAsr());
        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        var result = new MeetingAnalysis("summary\n# forged", new[]
        {
            new MeetingPoint("point", new[] { "segment-0000000000000000000-0" })
        }, Array.Empty<MeetingQuestion>());
        var analyzer = new AnalyzerFake((_, _) => Task.FromResult(result));
        await using var intelligence = new MeetingIntelligence(session, analyzer);
        capture.Emit(Chunk(0));
        await EventuallyAsync(() => intelligence.Analysis is not null);

        var markdown = intelligence.ExportMarkdown();

        Assert.Contains("> summary\n> # forged", markdown);
        Assert.Contains("> segment-0000000000000000000-0", markdown);
    }

    [Fact]
    public async Task Safe_analyzer_status_explains_a_daily_limit()
    {
        var capture = new CaptureFake();
        await using var session = new MeetingSession(capture, new ImmediateAsr());
        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        var analyzer = new AnalyzerFake((_, _) =>
            Task.FromException<MeetingAnalysis>(new MeetingException("Daily AI limit reached; adjust Settings.")));
        await using var intelligence = new MeetingIntelligence(session, analyzer);

        await intelligence.AskAsync("What happened?");

        Assert.Equal("Daily AI limit reached; adjust Settings.", intelligence.Status);
        Assert.Null(intelligence.Analysis);
    }

    [Fact]
    public async Task Export_maps_cited_note_ids_to_safe_titles_and_excludes_cleared_credentials()
    {
        var capture = new CaptureFake();
        await using var session = new MeetingSession(capture, new ImmediateAsr());
        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        var afterClear = new TaskCompletionSource<MeetingAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cited = new MeetingAnalysis("summary", new[]
        {
            new MeetingPoint("supported point", new[] { "note-roadmap" })
        }, Array.Empty<MeetingQuestion>()) { ContextNotice = "Older transcript text was omitted." };
        var analyzer = new AnalyzerFake((_, _) =>
            Interlocked.Increment(ref calls) == 1 ? Task.FromResult(cited) : afterClear.Task);
        await using var intelligence = new MeetingIntelligence(session, analyzer);
        intelligence.SetReferences(new[]
        {
            new MeetingReference("note-roadmap", "# Roadmap\n- Q4", "RAW NOTE SECRET"),
            new MeetingReference("note-uncited", "Uncited title", "uncited body")
        });
        capture.Emit(Chunk(0));
        await EventuallyAsync(() => intelligence.Analysis is not null);

        var markdown = intelligence.ExportMarkdown();
        Assert.Contains("> Older transcript text was omitted.", markdown);
        Assert.Contains("> ID: note-roadmap\n> Title: # Roadmap\n> - Q4", markdown);
        Assert.DoesNotContain("RAW NOTE SECRET", markdown);
        Assert.DoesNotContain("Uncited title", markdown);

        intelligence.SetReferences(Array.Empty<MeetingReference>());
        var cleared = intelligence.ExportMarkdown();
        Assert.DoesNotContain("note-roadmap", cleared);
        Assert.DoesNotContain("# Roadmap", cleared);
    }

    [Fact]
    public async Task Reference_reload_blocks_all_analysis_until_the_final_snapshot_is_published()
    {
        var capture = new CaptureFake();
        await using var session = new MeetingSession(capture, new ImmediateAsr());
        await session.StartAsync(new MeetingStartOptions("mic", "out"));
        var analyzer = new AnalyzerFake((_, _) => Task.FromResult(Result("ready")));
        await using var intelligence = new MeetingIntelligence(session, analyzer);

        intelligence.BeginReferenceReload();
        intelligence.BeginReferenceReload();
        capture.Emit(Chunk(0));
        await EventuallyAsync(() => session.Segments.Count == 1);
        await Task.Delay(30);
        var error = await Assert.ThrowsAsync<MeetingException>(() => intelligence.AskAsync("What changed?"));

        Assert.Contains("reloaded", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(analyzer.Calls);
        Assert.Null(intelligence.Analysis);
        Assert.Empty(intelligence.References);

        intelligence.SetReferences(new[] { new MeetingReference("note-final", "Final", "eligible") });
        await EventuallyAsync(() => analyzer.Calls.Count == 1 && intelligence.Analysis is not null);
        await Task.Delay(30);

        Assert.Single(analyzer.Calls);
        Assert.Equal("note-final", Assert.Single(analyzer.Calls[0].Context.References).Id);
    }

    private static MeetingAudioChunk Chunk(int startSeconds) =>
        new(MeetingSource.Microphone, TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(4), new byte[] { 1 });

    private static MeetingAnalysis Result(string summary) =>
        new(summary, Array.Empty<MeetingPoint>(), Array.Empty<MeetingQuestion>());

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class ImmediateAsr : IMeetingAsr
    {
        public Task<string> TranscribeAsync(MeetingAudioChunk chunk, AsrService service, CancellationToken cancellationToken) =>
            Task.FromResult($"text at {chunk.Start.TotalSeconds}");
    }

    private sealed class AnalyzerFake(
        Func<MeetingContext, string?, Task<MeetingAnalysis>> analyze) : IMeetingAnalyzer
    {
        public List<(MeetingContext Context, string? Question)> Calls { get; } = new();

        public Task<MeetingAnalysis> AnalyzeAsync(MeetingContext context, string? question,
            CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add((context, question));
            return analyze(context, question);
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
        public Task PauseAsync(CancellationToken cancellationToken, Action<TimeSpan>? onFirstSourceStopped = null)
        {
            onFirstSourceStopped?.Invoke(TimeSpan.Zero);
            return Task.CompletedTask;
        }
        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Emit(MeetingAudioChunk chunk) => _onChunk?.Invoke(chunk);
    }
}
