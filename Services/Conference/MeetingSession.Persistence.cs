using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Services.Conference;

public sealed partial class MeetingSession
{
    private MeetingTranscriptStore? _transcriptStore;
    private AutosaveWriter? _transcriptWriter;
    private string _transcriptId = "meeting-" + Guid.NewGuid().ToString("N");
    private DateTimeOffset _transcriptStartedAt;
    private long _transcriptRevision;
    private long _queuedRevision;
    private long _savedRevision;
    private bool _saveFailed;
    private bool _recoveryWarning;

    public string? SavedTranscriptsFolder => _transcriptStore?.Root;
    public string AutosaveStatus
    {
        get
        {
            lock (_gate)
            {
                if (_transcriptStore == null) return "Audio stays in memory";
                if (_saveFailed) return "⚠ Transcript save failed · retrying · use Save Markdown";
                if (_queuedRevision > _savedRevision) return "💾 Saving transcript locally…";
                if (_recoveryWarning) return "⚠ Some saved transcripts could not be restored · originals kept";
                return _segments.Count > 0 || _gaps.Count > 0 ? "✓ Transcript saved locally" : "💾 Transcript autosave on · audio is not saved";
            }
        }
    }

    private void InitializePersistence(MeetingTranscriptStore? store)
    {
        _transcriptStore = store;
        if (store == null) return;
        var saved = store.LoadLatest(out _recoveryWarning);
        if (saved != null)
        {
            _transcriptId = saved.Id;
            _transcriptStartedAt = saved.StartedAt;
            _segments.AddRange(saved.Segments);
            _segments.Sort(CompareSegments);
            _gaps.AddRange(saved.Gaps);
            _status = "Restored saved transcript. Listening is stopped.";
        }
        _transcriptWriter = new AutosaveWriter();
    }

    private void QueueTranscriptSave()
    {
        lock (_gate)
        {
            if (_transcriptWriter == null || _transcriptStore == null || _disposed ||
                _transcriptRevision == _queuedRevision || (_segments.Count == 0 && _gaps.Count == 0)) return;
            var snapshot = new MeetingTranscriptSnapshot(1, _transcriptId, _transcriptStartedAt,
                _segments.ToArray(), _gaps.ToArray());
            var revision = _queuedRevision = _transcriptRevision;
            var store = _transcriptStore;
            _transcriptWriter.Enqueue(snapshot.Id, () =>
            {
                try
                {
                    store.Save(snapshot);
                    lock (_gate)
                    {
                        _savedRevision = Math.Max(_savedRevision, revision);
                        _saveFailed = false;
                    }
                }
                catch
                {
                    lock (_gate) _saveFailed = true;
                    throw;
                }
                finally { NotifyChanged(); }
            });
        }
    }

    /// <summary>Normal close waits for the last received text, including any write retry.</summary>
    public Task<bool> FlushTranscriptAsync()
    {
        QueueTranscriptSave();
        return _transcriptWriter is { } writer
            ? Task.Run(() => writer.FlushAll(TimeSpan.FromSeconds(3))) : Task.FromResult(true);
    }
}
