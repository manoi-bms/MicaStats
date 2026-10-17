using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Services.Localization;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Ai;

namespace Kil0bitSystemMonitor.Pad
{
    public partial class MicaPadWindow
    {
        internal PadAiToolSet? AiToolsNow => _ai?.Tools;

        private PadAiToolSet CreateAiTools(string source, TextDocument document, int start)
        {
            var dates = new Dictionary<int, TaskDateRecord>();
            if (_taskDates.TryGetValue(document, out var controller))
            {
                int end = start + source.Length;
                foreach (DocumentLine line in document.Lines)
                {
                    if (line.Offset < start || line.EndOffset > end) continue;
                    if (controller.DatesOfLine(line.LineNumber) is { } tracked)
                        dates.Add(line.Offset - start, tracked);
                }
            }
            return new PadAiToolSet(source, dates);
        }

        private static string ToolActivityOf(string? name) => name switch
        {
            "list_tasks" => "Reading task details…",
            "scan_credentials" => "Checking credential candidates locally…",
            "propose_task_state" or "propose_credential_storage" => "Preparing changes for review…",
            _ => "Using note tools…",
        };

        private string? AiToolBlocker(AiRun run)
        {
            if (!ReferenceEquals(_ai, run) || _exiting || run.Cancel.IsCancellationRequested
                || !run.ToolsSucceeded || !run.Session.Finished || run.Session.Running)
                return "This request cannot apply changes. Try again.";
            if (run.ToolsApplied) return "These changes have already been applied.";
            if (AiIsOff()) return AiOffText;
            if (!AiSourceShown(run)) return "Show the original note to review these changes.";
            if (AiReadOnly) return AiReadOnlyText;
            if (!string.Equals(run.Document.Text, run.Snapshot, StringComparison.Ordinal))
                return "The note changed. Try again to prepare fresh changes.";
            return null;
        }

        private void DrawAiToolReview(AiRun run)
        {
            if (run.Tools is not { } tools || !run.Session.Finished || !run.ToolsSucceeded)
            {
                AiPanel.ShowToolReview(Array.Empty<AiToolReviewRow>(), false, "");
                return;
            }
            if (run.ReviewRows == null || run.ReviewLanguage != UiText.Language)
            {
                var previous = run.ReviewRows?.ToDictionary(row => row.Id, row => row.Selected);
                run.ReviewLanguage = UiText.Language;
                run.ReviewRows = tools.Proposals.Select(proposal =>
                {
                    AiToolReviewRow row;
                    if (proposal.Kind == PadAiProposalKind.TaskState)
                    {
                        var task = tools.Tasks.First(item => item.Id == proposal.TargetId);
                        row = new AiToolReviewRow(proposal.Id,
                            UiText.Format(proposal.Completed == true ? "Complete: {0}" : "Reopen: {0}", task.Text),
                            UiText.Format("Line {0} · tracked dates update automatically", SourceLine(run, task.Line)),
                            proposal.Completed == true ? "\uE930" : "\uE7A7");
                    }
                    else
                    {
                        var candidate = tools.Credentials.First(item => item.Id == proposal.TargetId);
                        row = new AiToolReviewRow(proposal.Id, UiText.Format("Store credential: {0}", candidate.Label),
                            UiText.Format("Line {0} · value hidden · replaces every exact copy in this note", SourceLine(run, candidate.Line)),
                            "\uE72E");
                    }
                    if (previous?.TryGetValue(row.Id, out bool selected) == true) row.Selected = selected;
                    return row;
                }).ToArray();
            }
            string? blocker = AiToolBlocker(run);
            string status = run.StoredToolCredentials != null && run.Document.Text != run.Snapshot
                ? run.ToolReviewNotice : blocker ?? run.ToolReviewNotice;
            AiPanel.ShowToolReview(run.ReviewRows, blocker == null, status,
                credentialsStored: run.StoredToolCredentials != null);
        }

        private static int SourceLine(AiRun run, int relativeLine) =>
            run.SourceLineNumber + relativeLine - 1;

        internal void ApplyAiToolChanges(IReadOnlyList<string> proposalIds)
        {
            if (_ai is not { Tools: { } tools } run) return;
            if (AiToolBlocker(run) is { } blocker)
            {
                RefreshAi(blocker);
                return;
            }
            var ids = proposalIds.ToHashSet(StringComparer.Ordinal);
            var selected = tools.Proposals.Where(item => ids.Contains(item.Id)).ToArray();
            if (selected.Length == 0)
            {
                RefreshAi("Select at least one change to apply.");
                return;
            }
            if (ids.Count != selected.Length)
            {
                RefreshAi("These changes are no longer available. Try again.");
                return;
            }
            if (run.StoredToolProposalIds != null && !ids.SetEquals(run.StoredToolProposalIds))
            {
                RefreshAi("Restore the original selection to retry these saved credentials.");
                return;
            }
            bool credentials = selected.Any(item => item.Kind == PadAiProposalKind.StoreCredential);
            if (!credentials)
            {
                CommitAiToolChanges(run, selected, null);
                return;
            }
            if (VaultCard.IsOpen) return;
            if (LoadedVault() is not { } vault)
            {
                RefreshAi("The credential vault could not be loaded. Try again.");
                return;
            }
            if (vault.Exists)
            {
                CommitAiToolChanges(run, selected, vault);
                return;
            }
            VaultCard.ShowCreatePin(pin => VaultStep("Creating the credential PIN", () =>
            {
                if (AiToolBlocker(run) is { } changed)
                {
                    VaultCard.Hide();
                    RefreshAi(changed);
                    return;
                }
                // Another window may have created the vault while this card waited. Keep its PIN.
                if (!vault.Exists) vault.Create(pin);
                VaultCard.Hide();
                CommitAiToolChanges(run, selected, vault);
            }));
        }

        private void CommitAiToolChanges(AiRun run, IReadOnlyList<PadAiProposal> selected, CredentialVault? vault)
        {
            if (AiToolBlocker(run) is { } blocker)
            {
                RefreshAi(blocker);
                return;
            }
            var tools = run.Tools!;
            var edits = new List<(int Offset, int Length, string Text)>();
            foreach (var proposal in selected.Where(item => item.Kind == PadAiProposalKind.TaskState))
            {
                var task = tools.Tasks.First(item => item.Id == proposal.TargetId);
                var line = run.Document.GetLineByNumber(SourceLine(run, task.Line));
                string text = run.Document.GetText(line);
                if (!_taskDates.TryGetValue(run.Document, out var controller) || !controller.IsTaskLine(line.LineNumber))
                {
                    RefreshAi("This task is no longer available. Try again.");
                    return;
                }
                if (MarkdownTasks.IsFinished(text) != proposal.Completed && MarkdownTasks.Toggle(text) is { } change)
                    edits.Add((line.Offset + change.Offset, change.Length, change.Text));
            }

            var secrets = selected.Where(item => item.Kind == PadAiProposalKind.StoreCredential)
                .Select(item => (Candidate: tools.Credentials.First(c => c.Id == item.TargetId),
                                 Value: tools.GetCredentialValue(item.TargetId)!))
                .OrderByDescending(item => item.Value.Length).ToArray();
            var replacements = new List<(int Offset, int Length, int Credential)>();
            for (int i = 0; i < secrets.Length; i++)
                foreach (int offset in SecretScrubber.Find(run.Snapshot, secrets[i].Value))
                {
                    int length = secrets[i].Value.Length;
                    if (replacements.Any(other => offset < other.Offset + other.Length && other.Offset < offset + length)
                        || edits.Any(other => offset < other.Offset + other.Length && other.Offset < offset + length))
                    {
                        RefreshAi("These changes overlap. Select one credential or apply task and credential changes separately.");
                        return;
                    }
                    replacements.Add((offset, length, i));
                }

            if (Enumerable.Range(0, secrets.Length).Any(index => !replacements.Any(edit => edit.Credential == index)))
            {
                RefreshAi("This credential is no longer available. Try again.");
                return;
            }

            IReadOnlyList<string> stored = Array.Empty<string>();
            void ApplyNote(IReadOnlyList<string> credentialIds)
            {
                var planned = edits.Concat(replacements.Select(edit =>
                    (edit.Offset, edit.Length, Text: SecretTokens.Format(credentialIds[edit.Credential]))))
                    .OrderBy(item => item.Offset).ToArray();
                var text = new StringBuilder(run.Snapshot.Length);
                int from = 0;
                foreach (var edit in planned)
                {
                    if (edit.Offset < from || edit.Offset + edit.Length > run.Snapshot.Length)
                        throw new InvalidOperationException("The reviewed note edits overlap or exceed the source.");
                    text.Append(run.Snapshot, from, edit.Offset - from).Append(edit.Text);
                    from = edit.Offset + edit.Length;
                }
                text.Append(run.Snapshot, from, run.Snapshot.Length - from);
                ApplyAiDocumentEdits(run, planned, text.ToString());
            }

            try
            {
                if (secrets.Length == 0) ApplyNote(stored);
                else if (run.StoredToolCredentials is { } saved)
                {
                    if (saved.Count != secrets.Length || saved.Any(id => vault!.Find(id)?.NoteId != run.Note.Id))
                    {
                        RefreshAi("The saved credentials changed. Start a new request.");
                        return;
                    }
                    stored = saved;
                    ApplyNote(stored);
                }
                else stored = vault!.AddManyAndApply(
                    secrets.Select(item => (item.Value, (string?)item.Candidate.Label, (string?)run.Note.Id)).ToArray(), ApplyNote);
            }
            catch (CredentialBatchApplyException ex)
            {
                // The vault rollback itself failed. Keep the exact saved IDs for a retry, so
                // a second click completes these references without creating another batch.
                run.StoredToolCredentials = ex.StoredIds;
                run.StoredToolProposalIds = selected.Select(item => item.Id).ToArray();
                run.ToolReviewNotice = run.Document.Text == run.Snapshot
                    ? "Credentials are saved, but the note is unchanged. Retry the same selected changes."
                    : "Credentials are saved, but some note changes could not finish. Review the note and stored credentials before saving.";
                WarnAi("Recovering reviewed credential changes", ex);
                RefreshAi(run.ToolReviewNotice);
                return;
            }
            catch (Exception ex)
            {
                WarnAi("Applying reviewed note changes", ex);
                run.ToolReviewNotice = run.Document.Text != run.Snapshot
                    ? "The note could not be restored. Review it before starting a new request."
                    : run.StoredToolCredentials != null
                    ? "Credentials are saved, but the note is unchanged. Retry the same selected changes."
                    : secrets.Length > 0
                        ? "Could not apply the credential changes. The note and vault were left unchanged. Try again."
                        : "Could not apply the task changes. The note was left unchanged. Try again.";
                RefreshAi(run.ToolReviewNotice);
                return;
            }

            run.ToolsApplied = true;
            run.ToolReviewNotice = "";
            run.StoredToolCredentials = null;
            run.StoredToolProposalIds = null;
            if (stored.Count > 0)
            {
                run.Document.UndoStack.ClearAll();
                for (int i = 0; i < stored.Count; i++)
                    QueueScrub(new PendingScrub(run.Note.Id, stored[i], secrets[i].Value, SecretTokens.Format(stored[i])));
                DropAiTextAfterStore(run.Note);
                DropMarkdownPreviewAfterStore(run.Note);
                _workspace.FlushPending();
                RetryPendingScrubs();
                string status = UiText.Format("Stored {0} credentials. Older versions are being cleaned.", stored.Count);
                if (run.Note.Meta.IsFileBacked) status += UiText.Tr(" — save the file (Ctrl+S) to remove it there");
                ShowStatus(status);
            }
            else RefreshAi("Reviewed task changes applied.");
        }

        private void ApplyAiDocumentEdits(AiRun run, IReadOnlyList<(int Offset, int Length, string Text)> edits, string target)
        {
            var document = run.Document;
            var dates = run.Note.Meta.TaskDates.ToList();
            document.UndoStack.StartUndoGroup();
            try
            {
                try
                {
                    // Preserve task anchors and their dates by editing only the exact ranges.
                    // The update and vault transaction restore the baseline if any edit fails.
                    using (document.RunUpdate())
                        foreach (var edit in edits.OrderByDescending(item => item.Offset))
                            document.Replace(edit.Offset, edit.Length, edit.Text);
                    if (!string.Equals(document.Text, target, StringComparison.Ordinal))
                        throw new InvalidOperationException("The reviewed note edit did not complete.");
                }
                catch (Exception applyFailure)
                {
                    try { using (document.RunUpdate()) ReplaceAiText(document, run.Snapshot); }
                    catch (Exception rollbackFailure) { WarnAi("Restoring a reviewed note edit", rollbackFailure); }
                    finally
                    {
                        run.Note.Meta.TaskDates = dates;
                        GuardAi("Restoring reviewed task dates", run.Note.Meta.NotifyTaskDatesRestored);
                    }
                    if (!string.Equals(document.Text, run.Snapshot, StringComparison.Ordinal))
                        throw new CredentialBatchKeepException(); // any saved references must remain valid
                    WarnAi("Rolling back reviewed note changes", applyFailure);
                    throw;
                }
            }
            finally { document.UndoStack.EndUndoGroup(); }
        }

        private static void ReplaceAiText(TextDocument document, string target)
        {
            string current = document.Text;
            int start = 0;
            while (start < current.Length && start < target.Length && current[start] == target[start]) start++;
            int oldEnd = current.Length, newEnd = target.Length;
            while (oldEnd > start && newEnd > start && current[oldEnd - 1] == target[newEnd - 1]) { oldEnd--; newEnd--; }
            if (oldEnd > start || newEnd > start)
                document.Replace(start, oldEnd - start, target.Substring(start, newEnd - start));
        }
    }
}
