using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Conference;

/// <summary>Coordinates private, cancellable analysis over finalized meeting text.</summary>
public sealed class MeetingIntelligence : IAsyncDisposable
{
    private static readonly TimeSpan AutomaticCadence = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _analyzerGate = new(1, 1);
    private readonly MeetingSession _session;
    private readonly IMeetingAnalyzer _analyzer;
    private readonly TimeProvider _clock;
    private readonly List<MeetingReference> _references = new();

    private CancellationTokenSource? _automaticCancellation;
    private CancellationTokenSource? _manualCancellation;
    private MeetingAnalysis? _analysis;
    private DateTimeOffset? _lastAutomaticStart;
    private long _sessionGeneration;
    private long _operationGeneration;
    private int _observedSegmentCount;
    private bool _automaticActive;
    private bool _manualActive;
    private bool _pendingAutomatic;
    private bool _referencesReloading;
    private bool _isBusy;
    private bool _disposed;
    private string _status = "Waiting for meeting transcript.";

    public MeetingIntelligence(MeetingSession session, IMeetingAnalyzer analyzer, TimeProvider? clock = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _clock = clock ?? TimeProvider.System;
        _sessionGeneration = session.Generation;
        _observedSegmentCount = session.Segments.Count;
        _session.Changed += OnSessionChanged;
        if (session.IsActive && _observedSegmentCount > 0)
        {
            lock (_gate)
            {
                _pendingAutomatic = true;
                ScheduleAutomaticLocked();
            }
        }
    }

    public MeetingAnalysis? Analysis
    {
        get { lock (_gate) return _analysis; }
    }

    public string Status
    {
        get { lock (_gate) return _status; }
    }

    public bool IsBusy
    {
        get { lock (_gate) return _isBusy; }
    }

    public IReadOnlyList<MeetingReference> References
    {
        get { lock (_gate) return Array.AsReadOnly(_references.ToArray()); }
    }

    public event Action? Changed;

    public async Task AskAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new MeetingException("Enter a question first.");

        CancellationTokenSource requestCancellation;
        long operation;
        long sessionGeneration;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_referencesReloading)
                throw new MeetingException("Reference notes are being reloaded; try again when they are ready.");
            if (!_session.IsActive)
                throw new MeetingException("Start a meeting before asking a question.");

            CancelAutomaticLocked();
            _manualCancellation?.Cancel();
            _manualCancellation?.Dispose();
            requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _manualCancellation = requestCancellation;
            _manualActive = true;
            _isBusy = true;
            _status = "Preparing a private answer...";
            operation = ++_operationGeneration;
            sessionGeneration = _session.Generation;
        }
        RaiseChanged();

        var enteredAnalyzer = false;
        try
        {
            await _analyzerGate.WaitAsync(requestCancellation.Token).ConfigureAwait(false);
            enteredAnalyzer = true;
            MeetingContext context;
            lock (_gate)
            {
                if (!CanAcceptLocked(operation, sessionGeneration, requestCancellation))
                    return;
                context = SnapshotContextLocked();
            }
            var result = await _analyzer.AnalyzeAsync(context, question.Trim(), requestCancellation.Token)
                .ConfigureAwait(false);
            lock (_gate)
            {
                if (!CanAcceptLocked(operation, sessionGeneration, requestCancellation))
                    return;
                _analysis = result;
                _status = "Private answer ready.";
            }
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
        }
        catch (MeetingException exception)
        {
            lock (_gate)
            {
                if (CanAcceptLocked(operation, sessionGeneration, requestCancellation))
                    _status = exception.Message;
            }
        }
        catch
        {
            lock (_gate)
            {
                if (CanAcceptLocked(operation, sessionGeneration, requestCancellation))
                    _status = "Meeting analysis is unavailable right now.";
            }
        }
        finally
        {
            if (enteredAnalyzer)
                _analyzerGate.Release();
            var notify = false;
            lock (_gate)
            {
                if (ReferenceEquals(_manualCancellation, requestCancellation))
                {
                    _manualCancellation = null;
                    _manualActive = false;
                    _isBusy = false;
                    notify = true;
                    ScheduleAutomaticLocked();
                }
            }
            requestCancellation.Dispose();
            if (notify) RaiseChanged();
        }
    }

    public void RefreshConfiguration()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            InvalidateDerivedLocked("AI configuration refreshed.");
        }
        RaiseChanged();
    }

    public void SetReferences(IReadOnlyList<MeetingReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        lock (_gate)
        {
            ThrowIfDisposed();
            _references.Clear();
            _references.AddRange(references);
            _referencesReloading = false;
            InvalidateDerivedLocked("Reference context changed.");
        }
        RaiseChanged();
    }

    /// <summary>
    /// Starts an atomic reference refresh. No analysis is dispatched until SetReferences
    /// publishes the complete replacement snapshot.
    /// </summary>
    public void BeginReferenceReload()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _referencesReloading = true;
            _references.Clear();
            InvalidateDerivedLocked("Reference notes are being reloaded.");
        }
        RaiseChanged();
    }

    public string ExportMarkdown()
    {
        MeetingAnalysis? analysis;
        MeetingReference[] references;
        lock (_gate)
        {
            analysis = _analysis;
            references = _references.ToArray();
        }

        var markdown = new StringBuilder(_session.ExportMarkdown().TrimEnd());
        if (analysis is null)
            return markdown.Append('\n').ToString();

        markdown.Append("\n\n# Meeting analysis\n\n## Summary\n\n");
        AppendQuoted(markdown, analysis.Summary);
        if (!string.IsNullOrWhiteSpace(analysis.ContextNotice))
        {
            markdown.Append("## Context notice\n\n");
            AppendQuoted(markdown, analysis.ContextNotice);
        }
        if (analysis.Points.Count > 0)
        {
            markdown.Append("## Key points\n\n");
            foreach (var point in analysis.Points)
            {
                AppendQuoted(markdown, point.Text);
                AppendSources(markdown, point.Sources);
            }
        }
        if (analysis.Questions.Count > 0)
        {
            markdown.Append("## Questions and suggested answers\n\n");
            foreach (var question in analysis.Questions)
            {
                markdown.Append("### Question\n\n");
                AppendQuoted(markdown, question.Question);
                markdown.Append("### Suggested answer\n\n");
                AppendQuoted(markdown, question.Answer);
                if (!string.IsNullOrWhiteSpace(question.MissingInformation))
                {
                    markdown.Append("### Missing information\n\n");
                    AppendQuoted(markdown, question.MissingInformation);
                }
                AppendSources(markdown, question.Sources);
            }
        }
        AppendCitedReferences(markdown, analysis, references);
        return markdown.ToString();
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return ValueTask.CompletedTask;
            _disposed = true;
            _session.Changed -= OnSessionChanged;
            ++_operationGeneration;
            CancelAutomaticLocked();
            _manualCancellation?.Cancel();
            _manualCancellation = null;
            _manualActive = false;
            _isBusy = false;
        }
        return ValueTask.CompletedTask;
    }

    private void OnSessionChanged()
    {
        var notify = false;
        lock (_gate)
        {
            if (_disposed)
                return;

            var generation = _session.Generation;
            var active = _session.IsActive;
            var count = _session.Segments.Count;
            if (generation != _sessionGeneration)
            {
                _sessionGeneration = generation;
                ++_operationGeneration;
                CancelAutomaticLocked();
                _manualCancellation?.Cancel();
                _manualActive = false;
                _isBusy = false;
                if (active)
                {
                    _analysis = null;
                    _lastAutomaticStart = null;
                    _status = "Waiting for meeting transcript.";
                }
                else
                {
                    _status = "Meeting stopped; the latest analysis remains available for export.";
                }
                _observedSegmentCount = count;
                _pendingAutomatic = active && count > 0;
                notify = true;
            }

            if (!active)
            {
                if (_automaticActive || _manualActive || _isBusy)
                {
                    ++_operationGeneration;
                    CancelAutomaticLocked();
                    _manualCancellation?.Cancel();
                    _manualActive = false;
                    _isBusy = false;
                    notify = true;
                }
            }
            else if (count != _observedSegmentCount)
            {
                _observedSegmentCount = count;
                _pendingAutomatic = count > 0;
                ScheduleAutomaticLocked();
            }
            else if (_pendingAutomatic)
            {
                ScheduleAutomaticLocked();
            }
        }
        if (notify) RaiseChanged();
    }

    private void ScheduleAutomaticLocked()
    {
        if (_disposed || _referencesReloading || !_pendingAutomatic || _manualActive ||
            _automaticActive || !_session.IsActive)
            return;

        _pendingAutomatic = false;
        _automaticActive = true;
        _automaticCancellation = new CancellationTokenSource();
        var cancellation = _automaticCancellation;
        var operation = ++_operationGeneration;
        var sessionGeneration = _session.Generation;
        _ = RunAutomaticAsync(operation, sessionGeneration, cancellation);
    }

    private async Task RunAutomaticAsync(long operation, long sessionGeneration,
        CancellationTokenSource cancellation)
    {
        await Task.Yield();
        var enteredAnalyzer = false;
        try
        {
            TimeSpan delay;
            lock (_gate)
            {
                var next = (_lastAutomaticStart ?? DateTimeOffset.MinValue) + AutomaticCadence;
                delay = next > _clock.GetUtcNow() ? next - _clock.GetUtcNow() : TimeSpan.Zero;
            }
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, _clock, cancellation.Token).ConfigureAwait(false);

            await _analyzerGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            enteredAnalyzer = true;

            MeetingContext context;
            lock (_gate)
            {
                if (!CanAcceptAutomaticLocked(operation, sessionGeneration, cancellation))
                    return;
                _lastAutomaticStart = _clock.GetUtcNow();
                _pendingAutomatic = false;
                _isBusy = true;
                _status = "Updating private meeting analysis...";
                context = SnapshotContextLocked();
            }
            RaiseChanged();

            var result = await _analyzer.AnalyzeAsync(context, null, cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!CanAcceptAutomaticLocked(operation, sessionGeneration, cancellation))
                    return;
                _analysis = result;
                _status = "Meeting analysis is up to date.";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (MeetingException exception)
        {
            lock (_gate)
            {
                if (CanAcceptAutomaticLocked(operation, sessionGeneration, cancellation))
                    _status = exception.Message;
            }
        }
        catch
        {
            lock (_gate)
            {
                if (CanAcceptAutomaticLocked(operation, sessionGeneration, cancellation))
                    _status = "Meeting analysis is unavailable; transcription continues.";
            }
        }
        finally
        {
            if (enteredAnalyzer)
                _analyzerGate.Release();
            var notify = false;
            lock (_gate)
            {
                if (ReferenceEquals(_automaticCancellation, cancellation))
                {
                    _automaticCancellation = null;
                    _automaticActive = false;
                    _isBusy = false;
                    notify = true;
                    ScheduleAutomaticLocked();
                }
            }
            cancellation.Dispose();
            if (notify) RaiseChanged();
        }
    }

    private MeetingContext SnapshotContextLocked() =>
        new(_session.Segments, Array.AsReadOnly(_references.ToArray()));

    private bool CanAcceptLocked(long operation, long sessionGeneration, CancellationTokenSource cancellation) =>
        !_disposed && operation == _operationGeneration && _session.Generation == sessionGeneration &&
        _session.IsActive && ReferenceEquals(_manualCancellation, cancellation) && !cancellation.IsCancellationRequested;

    private bool CanAcceptAutomaticLocked(long operation, long sessionGeneration,
        CancellationTokenSource cancellation) =>
        !_disposed && operation == _operationGeneration && _session.Generation == sessionGeneration &&
        _session.IsActive && !_manualActive && ReferenceEquals(_automaticCancellation, cancellation) &&
        !cancellation.IsCancellationRequested;

    private void InvalidateDerivedLocked(string status)
    {
        ++_operationGeneration;
        CancelAutomaticLocked();
        _manualCancellation?.Cancel();
        _manualActive = false;
        _isBusy = false;
        _analysis = null;
        _status = _referencesReloading ? "Reference notes are being reloaded." : status;
        _pendingAutomatic = _session.IsActive && _session.Segments.Count > 0;
        ScheduleAutomaticLocked();
    }

    private void CancelAutomaticLocked()
    {
        _automaticCancellation?.Cancel();
    }

    private static void AppendQuoted(StringBuilder markdown, string text)
    {
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (var line in normalized.Split('\n'))
            markdown.Append("> ").Append(line).Append('\n');
        markdown.Append('\n');
    }

    private static void AppendSources(StringBuilder markdown, IReadOnlyList<string> sources)
    {
        if (sources.Count == 0)
            return;
        markdown.Append("Sources:\n\n");
        foreach (var source in sources)
            AppendQuoted(markdown, source);
    }

    private static void AppendCitedReferences(StringBuilder markdown, MeetingAnalysis analysis,
        IReadOnlyList<MeetingReference> references)
    {
        var cited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in analysis.Points)
            cited.UnionWith(point.Sources);
        foreach (var question in analysis.Questions)
            cited.UnionWith(question.Sources);

        var selected = references.Where(reference => cited.Contains(reference.Id)).ToArray();
        if (selected.Length == 0)
            return;

        markdown.Append("## Referenced notes\n\n");
        foreach (var reference in selected)
        {
            markdown.Append("### Note reference\n\n");
            AppendQuoted(markdown, $"ID: {reference.Id}\nTitle: {reference.Title}");
        }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MeetingIntelligence));
    }
}
