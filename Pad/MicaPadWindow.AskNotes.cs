using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Kil0bitSystemMonitor.Services.Pad.Search;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Ask your notes in the MicaPad window (MicaPad AI spec 4): what the Search pane's Ask runs.
    /// The search as usual, then one request with the question and the passages found, its log
    /// line, Copy under an answer, and stopping an answer when the window hides or closes.
    ///
    /// <para>
    /// The consent check, the runner, the log and the clipboard are those of the AI actions
    /// (<c>MicaPadWindow.Ai.cs</c>). Nothing is sent unless AI is on, and that is asked at the
    /// start, again once the search is back, and once more right before the request.
    /// </para>
    /// </summary>
    public partial class MicaPadWindow
    {
        // ---- ask your notes (MicaPad AI spec 4) ------------------------------------------------

        /// <summary>
        /// The Search pane's Ask: the search as usual, then the question and the first passages
        /// found (at most eight) for the model to answer from. The rows are built in hit order,
        /// so row i is source i + 1, the <c>[n]</c> the answer cites.
        ///
        /// <para>
        /// While AI is off this is the normal search and a sentence saying how to turn AI on:
        /// no runner is built, nothing is counted or sent. With nothing found there is no request
        /// either. The request itself starts only when the pane reads the answer
        /// (<see cref="AnswerFromNotesAsync"/>). Cancelling throws, as the search does.
        /// </para>
        ///
        /// <para>
        /// The passages are those of the notes as they are now. An edit reaches the index two
        /// seconds after it, so the feeder is flushed and the index waited for before the search:
        /// text deleted a moment ago is not found, and so is not sent.
        /// </para>
        /// </summary>
        private async Task<AskStart> AskNotesAsync(string query, CancellationToken token)
        {
            // The consent gate: asked before anything else.
            bool off = AiIsOff();

            // Every edit still waiting for its two seconds goes to the index now, as when the pane opens.
            SearchFeeder?.FlushPending();

            // Off, or search not ready yet: the normal search, and the sentence that says why
            // there is no answer (for search not ready, the search's own status says it).
            if (off || SearchService is not { } service)
            {
                (IReadOnlyList<SearchRow> found, string how) = await RunSearchAsync(query, token);
                return new AskStart(found, how, null, off ? NotesQuestion.AiOff : how);
            }

            // The index has taken those edits in before it is searched. This waits for the notes'
            // words only, never for the embedding server.
            await service.Indexer.WhenApplied().WaitAsync(token);

            // Off the UI thread, as RunSearchAsync does; the rows are built back here.
            SearchOutcome outcome = await Task.Run(() => service.Search.SearchAsync(query, token), token);
            string status = SearchStatusText.For(outcome, service.Settings());
            IReadOnlyList<Passage> sources = NotesQuestion.Sources(outcome.Hits);

            // The search as usual, with a sentence in place of the answer: no row is a numbered source.
            AskStart Instead(string sentence, string why)
            {
                LogAsk(sources.Count, 0, why);
                return new AskStart(NoteRows(outcome.Hits, query, numbered: 0), status, null, sentence);
            }

            if (sources.Count == 0) return Instead(NotesQuestion.NoSources, "no sources");

            // Asked again once the search is back, before a runner is built: the search takes a
            // while (an embedding server, a reranker), and the setting may have been turned off.
            if (AiIsOff()) return Instead(NotesQuestion.AiOff, "AI off");

            PadAiRunner? runner;
            try
            {
                runner = AiRunnerFactory();
            }
            catch (Exception ex)
            {
                WarnAi("Building the AI runner for a question", ex);
                return Instead(AiErrorText.Describe(ex), "failed");
            }
            if (runner == null) return Instead(AiNotReadyText, "not available");

            // The status claims an answer only once one came: "Answering" while it streams,
            // "Answered" after a clean end, the search status alone after anything else. Both
            // name where the passages go.
            string message = NotesQuestion.Message(query, sources);
            string destination = AiDestinationNow();
            return new AskStart(NoteRows(outcome.Hits, query, numbered: sources.Count), status,
                                AnswerFromNotesAsync(runner, message, sources.Count, token), null,
                                Answering: status + " · " + NotesQuestion.Answering(sources.Count, destination),
                                Answered: status + " · " + NotesQuestion.Status(sources.Count, destination));
        }

        /// <summary>A question's rows, in hit order; the first <paramref name="numbered"/> carry their source numbers, 1 up.</summary>
        private List<SearchRow> NoteRows(IReadOnlyList<Passage> hits, string query, int numbered)
        {
            var open = _workspace.Open.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            return hits.Select((p, i) => new SearchRow(
                p.NoteId, p.Title, !open.Contains(p.NoteId), p.FirstLine, p.LastLine, p.FirstLineText,
                SearchSnippet.Make(p.Body, query), i < numbered ? i + 1 : null)).ToList();
        }

        /// <summary>
        /// The answer the pane reads: one request, which starts with the first read and is logged
        /// when it ends. <paramref name="message"/> is free of credentials already (the passages
        /// by the index, the question by <see cref="NotesQuestion.Message"/>).
        /// </summary>
        private async IAsyncEnumerable<PadAiUpdate> AnswerFromNotesAsync(PadAiRunner runner, string message, int sources,
            [EnumeratorCancellation] CancellationToken token)
        {
            // Asked once more, right before the request: this runs when the pane starts to read
            // the answer, which is after AskNotesAsync returned. Off: nothing is counted or sent,
            // and the pane shows why.
            if (AiIsOff())
            {
                LogAsk(sources, 0, "AI off");
                yield return new PadAiUpdate(PadAiUpdateKind.Error, NotesQuestion.AiOff);
                yield return new PadAiUpdate(PadAiUpdateKind.Done);
                yield break;
            }

            string outcome = "stopped";   // unless it runs to its end: the pane reads no further once it is stopped
            try
            {
                bool failed = false, cutShort = false;
                // The runner never throws and ends with Done, after a cancel too.
                await foreach (PadAiUpdate update in runner.RunAsync(message, token))
                {
                    if (update.Kind == PadAiUpdateKind.Error) failed = true;
                    else if (update.Kind == PadAiUpdateKind.CutShort) cutShort = true;
                    else if (update.Kind == PadAiUpdateKind.Done)
                        outcome = failed ? "failed" : token.IsCancellationRequested ? "stopped" : cutShort ? "cut short" : "ok";
                    yield return update;
                }
            }
            finally
            {
                LogAsk(sources, message.Length, outcome);
            }
        }

        /// <summary>
        /// One line per question: how many passages, how many characters its request held and how
        /// it ended. Never the question or the answer. "In the request", not "sent", as
        /// <see cref="LogAi"/> says: the runner may refuse a request handed to it (no key, the
        /// daily limit) without sending anything.
        /// </summary>
        private void LogAsk(int sources, int request, string outcome)
        {
            try
            {
                AiLog("AI ask-notes: " + sources.ToString(CultureInfo.InvariantCulture) + " sources, "
                      + request.ToString(CultureInfo.InvariantCulture) + " chars in the request, " + outcome);
            }
            catch (Exception)
            {
                // Logging is best effort; it must not end the request path with an exception.
            }
        }

        /// <summary>Copy under an answer: the answer as the model wrote it goes to the clipboard, and the status bar says so.</summary>
        private void CopyNotesAnswer(string answer)
        {
            if (answer.Length == 0) return;
            try
            {
                AiCopy(answer);
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                Warn("Copying an answer failed (" + ex.GetType().Name + ")");
                ShowStatus("Clipboard busy, try again");
                return;
            }
            ShowStatus("Copied");
        }

        /// <summary>
        /// Stops an answer from notes as the window hides or closes. Guarded for the same reason
        /// as <see cref="CancelAi"/>: a cancel that throws must not come out of closing.
        /// </summary>
        private void StopNotesAnswer() => GuardAi("Stopping an answer from notes", SearchPanel.StopAnswer);
    }
}
