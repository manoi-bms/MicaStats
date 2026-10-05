namespace Kil0bitSystemMonitor.Services.Conference;

/// <summary>Owns explicit note selection and rejects reads made obsolete by removal or credentials.</summary>
public sealed class MeetingReferenceSelection : IAsyncDisposable
{
    public const int MaximumSelected = 8;

    private readonly object _gate = new();
    private readonly object _publishGate = new();
    private readonly IMeetingNotes _notes;
    private readonly MeetingIntelligence _intelligence;
    private readonly List<MeetingNoteChoice> _available = new();
    private readonly List<string> _selectedIds = new();
    private readonly Dictionary<string, MeetingReference> _references = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _reloadCancellation;
    private long _epoch;
    private long _contextVersion;
    private bool _disposed;
    private string _status = "Select Refresh notes to list available MicaPad references.";

    public MeetingReferenceSelection(IMeetingNotes notes, MeetingIntelligence intelligence)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _intelligence = intelligence ?? throw new ArgumentNullException(nameof(intelligence));
    }

    public IReadOnlyList<MeetingNoteChoice> Available
    {
        get { lock (_gate) return Array.AsReadOnly(_available.ToArray()); }
    }

    public IReadOnlyList<MeetingNoteChoice> Selected
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_selectedIds.Select(ChoiceForLocked).ToArray());
            }
        }
    }

    public string Status
    {
        get { lock (_gate) return _status; }
    }

    public event Action? Changed;

    /// <summary>Raised synchronously before retained reference text or derived analysis changes.</summary>
    public event Action? ContextInvalidated;

    public async Task RefreshAvailableAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource request;
        long epoch;
        lock (_gate)
        {
            ThrowIfDisposed();
            _refreshCancellation?.Cancel();
            request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _refreshCancellation = request;
            epoch = _epoch;
            _status = "Refreshing available reference notes...";
        }
        RaiseChanged();

        try
        {
            IReadOnlyList<MeetingNoteChoice> choices = await _notes.ListAsync(request.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!CanAcceptLocked(epoch, request, _refreshCancellation))
                    return;
                _available.Clear();
                _available.AddRange(choices
                    .Where(choice => !string.IsNullOrWhiteSpace(choice.Id))
                    .GroupBy(choice => choice.Id, StringComparer.Ordinal)
                    .Select(group => group.First()));
                _status = _available.Count == 0
                    ? "No MicaPad reference notes are available."
                    : $"{_available.Count} reference notes available.";
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (MeetingException exception)
        {
            lock (_gate)
                if (CanAcceptLocked(epoch, request, _refreshCancellation))
                    _status = exception.Message;
        }
        catch
        {
            lock (_gate)
                if (CanAcceptLocked(epoch, request, _refreshCancellation))
                    _status = "Reference notes are unavailable.";
        }
        finally
        {
            bool notify = false;
            lock (_gate)
            {
                if (ReferenceEquals(_refreshCancellation, request))
                {
                    _refreshCancellation = null;
                    notify = true;
                }
            }
            request.Dispose();
            if (notify) RaiseChanged();
        }
    }

    public async Task SelectAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new MeetingException("Choose a reference note first.");

        CancellationTokenSource request;
        long epoch;
        string selectedId = id.Trim();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_selectedIds.Contains(selectedId, StringComparer.Ordinal))
                return;
            if (_selectedIds.Count >= MaximumSelected)
                throw new MeetingException($"Select at most {MaximumSelected} reference notes.");
            if (!_available.Any(choice => string.Equals(choice.Id, selectedId, StringComparison.Ordinal)))
                throw new MeetingException("That reference note is no longer available.");

            request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pending[selectedId] = request;
            _selectedIds.Add(selectedId);
            epoch = _epoch;
            _status = "Reading the selected reference note...";
        }
        RaiseChanged();

        try
        {
            MeetingReference? reference = await _notes.ReadAsync(selectedId, request.Token).ConfigureAwait(false);
            IReadOnlyList<MeetingReference>? snapshot = null;
            long contextVersion = 0;
            lock (_gate)
            {
                if (!CanAcceptSelectionLocked(selectedId, epoch, request))
                    return;
                _pending.Remove(selectedId);
                if (reference is null)
                {
                    _selectedIds.Remove(selectedId);
                    _status = "The selected reference note is unavailable.";
                }
                else
                {
                    _references[selectedId] = reference;
                    if (_reloadCancellation is null)
                    {
                        snapshot = ReferenceSnapshotLocked();
                        contextVersion = ++_contextVersion;
                    }
                    _status = $"{_references.Count} reference notes selected.";
                }
            }
            if (snapshot is not null)
                PublishContext(contextVersion, snapshot);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (CanAcceptSelectionLocked(selectedId, epoch, request))
                {
                    _pending.Remove(selectedId);
                    _selectedIds.Remove(selectedId);
                }
            }
        }
        catch (MeetingException exception)
        {
            lock (_gate)
            {
                if (CanAcceptSelectionLocked(selectedId, epoch, request))
                {
                    _pending.Remove(selectedId);
                    _selectedIds.Remove(selectedId);
                    _status = exception.Message;
                }
            }
        }
        catch
        {
            lock (_gate)
            {
                if (CanAcceptSelectionLocked(selectedId, epoch, request))
                {
                    _pending.Remove(selectedId);
                    _selectedIds.Remove(selectedId);
                    _status = "The selected reference note is unavailable.";
                }
            }
        }
        finally
        {
            request.Dispose();
            RaiseChanged();
        }
    }

    public void Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        IReadOnlyList<MeetingReference>? snapshot = null;
        long contextVersion = 0;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_selectedIds.Remove(id))
                return;
            if (_pending.Remove(id, out var pending))
                pending.Cancel();
            if (_reloadCancellation is not null)
            {
                ++_epoch;
                CancelReloadLocked();
                foreach (var stale in _pending.ToArray())
                {
                    stale.Value.Cancel();
                    _selectedIds.Remove(stale.Key);
                }
                _pending.Clear();
            }
            _references.Remove(id);
            snapshot = ReferenceSnapshotLocked();
            contextVersion = ++_contextVersion;
            _status = _selectedIds.Count > _references.Count
                ? "Reference reload stopped; remaining selections need to be reloaded."
                : _references.Count == 0
                    ? "No reference notes selected."
                    : $"{_references.Count} reference notes selected.";
        }
        PublishContext(contextVersion, snapshot);
        RaiseChanged();
    }

    /// <summary>Clears note-derived context before returning, then safely reloads still-selected notes.</summary>
    public Task InvalidateCredentialsAsync(CancellationToken cancellationToken = default)
    {
        string[] selected;
        CancellationTokenSource request;
        long epoch;
        long contextVersion;
        lock (_gate)
        {
            ThrowIfDisposed();
            ++_epoch;
            foreach (var pending in _pending.Values)
                pending.Cancel();
            _pending.Clear();
            CancelReloadLocked();
            _references.Clear();
            selected = _selectedIds.ToArray();
            request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _reloadCancellation = request;
            epoch = _epoch;
            contextVersion = ++_contextVersion;
            _status = selected.Length == 0
                ? "Credential context cleared."
                : "Credential context cleared; reloading selected notes...";
        }

        BeginReferenceReload(contextVersion);
        RaiseChanged();
        return ReloadAfterCredentialInvalidationAsync(selected, epoch, request);
    }

    /// <summary>Clears meeting-scoped selections and any note-derived context before a later session starts.</summary>
    public void ResetForNewSession()
    {
        long contextVersion;
        lock (_gate)
        {
            ThrowIfDisposed();
            ++_epoch;
            foreach (var pending in _pending.Values)
                pending.Cancel();
            _pending.Clear();
            CancelReloadLocked();
            _selectedIds.Clear();
            _references.Clear();
            contextVersion = ++_contextVersion;
            _status = "Reference-note selection cleared for the new meeting.";
        }
        PublishContext(contextVersion, Array.Empty<MeetingReference>());
        RaiseChanged();
    }

    public ValueTask DisposeAsync()
    {
        bool clear;
        long contextVersion;
        lock (_gate)
        {
            if (_disposed)
                return ValueTask.CompletedTask;
            _disposed = true;
            ++_epoch;
            _refreshCancellation?.Cancel();
            _refreshCancellation = null;
            clear = _references.Count > 0 || _reloadCancellation is not null;
            CancelReloadLocked();
            foreach (var pending in _pending.Values)
                pending.Cancel();
            _pending.Clear();
            _references.Clear();
            _selectedIds.Clear();
            contextVersion = ++_contextVersion;
        }
        if (clear)
            PublishContext(contextVersion, Array.Empty<MeetingReference>());
        return ValueTask.CompletedTask;
    }

    private async Task ReloadAfterCredentialInvalidationAsync(string[] selected, long epoch,
        CancellationTokenSource request)
    {
        try
        {
            var loaded = new Dictionary<string, MeetingReference>(StringComparer.Ordinal);
            foreach (string id in selected)
            {
                request.Token.ThrowIfCancellationRequested();
                MeetingReference? reference = await _notes.ReadAsync(id, request.Token).ConfigureAwait(false);
                if (reference is not null)
                    loaded[id] = reference;
            }

            IReadOnlyList<MeetingReference>? snapshot = null;
            long contextVersion = 0;
            lock (_gate)
            {
                if (!CanAcceptLocked(epoch, request, _reloadCancellation))
                    return;
                foreach (string id in _selectedIds)
                    if (loaded.TryGetValue(id, out var reference))
                        _references[id] = reference;
                snapshot = ReferenceSnapshotLocked();
                contextVersion = ++_contextVersion;
                _status = _references.Count == _selectedIds.Count
                    ? $"{_references.Count} reference notes reloaded after credential filtering."
                    : "Some selected reference notes are unavailable after credential filtering.";
            }
            PublishContext(contextVersion, snapshot);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            CompleteFailedReload(epoch, request, "Reference-note reload was canceled.");
        }
        catch
        {
            CompleteFailedReload(epoch, request, "Selected reference notes could not be reloaded.");
        }
        finally
        {
            bool notify = false;
            lock (_gate)
            {
                if (ReferenceEquals(_reloadCancellation, request))
                {
                    _reloadCancellation = null;
                    notify = true;
                }
            }
            request.Dispose();
            if (notify) RaiseChanged();
        }
    }

    private MeetingNoteChoice ChoiceForLocked(string id) =>
        _available.FirstOrDefault(choice => string.Equals(choice.Id, id, StringComparison.Ordinal))
        ?? new MeetingNoteChoice(id, _references.TryGetValue(id, out var reference) ? reference.Title : id);

    private IReadOnlyList<MeetingReference> ReferenceSnapshotLocked() =>
        Array.AsReadOnly(_selectedIds
            .Where(_references.ContainsKey)
            .Select(id => _references[id])
            .ToArray());

    private bool CanAcceptSelectionLocked(string id, long epoch, CancellationTokenSource request) =>
        !_disposed && epoch == _epoch && _selectedIds.Contains(id, StringComparer.Ordinal) &&
        _pending.TryGetValue(id, out var current) && ReferenceEquals(current, request) &&
        !request.IsCancellationRequested;

    private bool CanAcceptLocked(long epoch, CancellationTokenSource request,
        CancellationTokenSource? current) =>
        !_disposed && epoch == _epoch && ReferenceEquals(request, current) && !request.IsCancellationRequested;

    private void CancelReloadLocked()
    {
        _reloadCancellation?.Cancel();
        _reloadCancellation = null;
    }

    private void CompleteFailedReload(long epoch, CancellationTokenSource request, string status)
    {
        IReadOnlyList<MeetingReference>? snapshot = null;
        long contextVersion = 0;
        lock (_gate)
        {
            if (_disposed || epoch != _epoch || !ReferenceEquals(request, _reloadCancellation))
                return;
            _references.Clear();
            snapshot = ReferenceSnapshotLocked();
            contextVersion = ++_contextVersion;
            _status = status;
        }
        PublishContext(contextVersion, snapshot);
    }

    private void BeginReferenceReload(long version)
    {
        lock (_publishGate)
        {
            lock (_gate)
                if (version != _contextVersion)
                    return;
            RaiseContextInvalidated();
            lock (_gate)
                if (version != _contextVersion)
                    return;
            _intelligence.BeginReferenceReload();
        }
    }

    private void PublishContext(long version, IReadOnlyList<MeetingReference> references)
    {
        lock (_publishGate)
        {
            lock (_gate)
                if (version != _contextVersion)
                    return;
            RaiseContextInvalidated();
            lock (_gate)
                if (version != _contextVersion)
                    return;
            _intelligence.SetReferences(references);
        }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch { }
    }

    private void RaiseContextInvalidated()
    {
        try { ContextInvalidated?.Invoke(); }
        catch { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MeetingReferenceSelection));
    }
}
