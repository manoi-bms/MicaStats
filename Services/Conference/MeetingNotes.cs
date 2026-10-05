using System.Text.Json.Nodes;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Kil0bitSystemMonitor.Services.Pad.Search;

namespace Kil0bitSystemMonitor.Services.Conference;

/// <summary>Read-only, explicitly invoked access to eligible MicaPad reference notes.</summary>
public sealed class MeetingNotes : IMeetingNotes
{
    private readonly Func<PadWorkspace?> _workspace;
    private readonly Func<bool> _start;
    private readonly Dispatcher _ui;
    private readonly NoteTools _tools;
    private readonly Func<AiBudget> _budget;

    public MeetingNotes(Func<PadWorkspace?> workspace, Func<bool> start, Dispatcher ui,
        INoteReader reader, Func<AiBudget> budget)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _tools = new NoteTools(reader ?? throw new ArgumentNullException(nameof(reader)));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    }

    public async Task<IReadOnlyList<MeetingNoteChoice>> ListAsync(CancellationToken cancellationToken)
    {
        try
        {
            var started = await OnUiAsync(() =>
            {
                if (!_start())
                    return ((PadWorkspace?)null, (Dictionary<string, string>?)null);

                var workspace = _workspace()
                    ?? throw new InvalidOperationException("MicaPad notes did not start.");
                var live = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var note in workspace.Open)
                {
                    string text = note.TextProvider();
                    live[note.Id] = CleanTitle(note.LiveTitle(text));
                }
                return (workspace, live);
            }, cancellationToken).ConfigureAwait(false);

            if (started.Item1 is null)
                return Array.Empty<MeetingNoteChoice>();

            PadWorkspace workspace = started.Item1;
            Dictionary<string, string> liveTitles = started.Item2!;
            IReadOnlyList<NoteMeta> metas = await Task.Run(
                () => workspace.Store.PeekAllMetas(out _), cancellationToken).ConfigureAwait(false);

            var choices = new Dictionary<string, MeetingNoteChoice>(StringComparer.Ordinal);
            foreach (var meta in metas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(meta.Id))
                    choices[meta.Id] = new MeetingNoteChoice(meta.Id, CleanTitle(meta.Title));
            }
            foreach (var live in liveTitles)
                choices[live.Key] = new MeetingNoteChoice(live.Key, live.Value);

            return choices.Values
                .OrderBy(choice => choice.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(choice => choice.Id, StringComparer.Ordinal)
                .ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch (NotesNotReadyException)
        {
            throw new MeetingException("Reference notes are not ready; open MicaPad once.");
        }
        catch
        {
            throw new MeetingException("Reference notes are unavailable.");
        }
    }

    public async Task<MeetingReference?> ReadAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        try
        {
            AiBudget budget = _budget() ?? AiBudget.Standard;
            var args = new JsonObject
            {
                ["noteId"] = id.Trim(),
                ["firstLine"] = 1,
                ["lineCount"] = Math.Max(1, budget.NoteReadLines),
            };
            JsonNode result = await _tools.GetNoteAsync(args, cancellationToken,
                Math.Max(0, budget.NoteReadTokens), Math.Max(1, budget.NoteReadLines)).ConfigureAwait(false);
            if (result is not JsonObject note || note["error"] is not null)
                return null;

            string? noteId = (string?)note["noteId"];
            string? title = (string?)note["title"];
            string? text = (string?)note["text"];
            if (string.IsNullOrWhiteSpace(noteId) || title is null || text is null)
                return null;

            return new MeetingReference("note-" + noteId, title, text);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            throw new MeetingException("The selected reference note is unavailable.");
        }
    }

    private static string CleanTitle(string title)
    {
        string cleaned = NotePassages.WithoutSecretsAndCutEnds(title ?? string.Empty).Trim();
        return cleaned.Length == 0 ? "Untitled note" : cleaned;
    }

    private Task<T> OnUiAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        if (!_ui.CheckAccess())
            return _ui.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;
        try { return Task.FromResult(action()); }
        catch (Exception exception) { return Task.FromException<T>(exception); }
    }
}
