using System.Collections.Generic;
using Microsoft.Extensions.AI;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// One Ask window conversation: the messages sent back to the model on each question (never
    /// the system prompt, which the assistant adds per request) and the suggested actions of the
    /// latest answer.
    /// </summary>
    public sealed class AiConversation
    {
        /// <summary>User questions, assistant answers and tool calls with their results, oldest first.</summary>
        public List<ChatMessage> Messages { get; } = new();

        /// <summary>
        /// The suggestions of the latest answer. Cleared when a new question starts and by
        /// <see cref="Clear"/>, so a button whose suggestion is gone must stop working.
        /// </summary>
        public List<SuggestedAction> Suggestions { get; } = new();

        /// <summary>Where the notes went when they were read; null while none was. One field, so both properties change at once.</summary>
        private volatile string? _notesDestination;

        /// <summary>
        /// True once a note tool handed note text to the model in this conversation. From then on
        /// note text may steer its answers, this one and every later one (the tool's result is
        /// sent again with each question), so the Ask window shows their links as text. Set by the
        /// note functions themselves, on whatever thread they run: what the tool loop reports
        /// about a call depends on the call ids a provider gives, and those can repeat.
        /// </summary>
        public bool NotesRead => _notesDestination != null;

        /// <summary>
        /// Where the notes went when they were read (<c>PadAiPrivacy.Destination</c>:
        /// "api.anthropic.com", "this PC", a host), or null while <see cref="NotesRead"/> is
        /// false. A question that goes somewhere else must not carry them along.
        /// </summary>
        public string? NotesDestination => _notesDestination;

        /// <summary>A note tool returned notes to a model at <paramref name="destination"/>; see <see cref="NotesRead"/>.</summary>
        internal void MarkNotesRead(string destination) => _notesDestination = destination ?? "";

        /// <summary>
        /// What was read was taken out of <see cref="Messages"/> because the next question goes to
        /// another destination: for that one the conversation has read no notes, until a note
        /// tool marks it again.
        /// </summary>
        internal void ForgetNotesRead() => _notesDestination = null;

        /// <summary>Starts over: forgets every message and suggestion.</summary>
        public void Clear()
        {
            Messages.Clear();
            Suggestions.Clear();
            _notesDestination = null;
        }
    }

    /// <summary>What an <see cref="AssistantUpdate"/> carries.</summary>
    public enum AssistantUpdateKind
    {
        /// <summary>A piece of the answer text, to append to what is shown.</summary>
        Text,

        /// <summary>A tool ran: <see cref="AssistantUpdate.ToolName"/> with <see cref="AssistantUpdate.ToolArgs"/> (JSON), for the turn's tool chips.</summary>
        ToolUsed,

        /// <summary>A suggested-action button (<see cref="AssistantUpdate.Suggestion"/>).</summary>
        Suggestion,

        /// <summary>The endpoint cannot use tools; this answer comes from a snapshot. <see cref="AssistantUpdate.Text"/> explains.</summary>
        LimitedMode,

        /// <summary>The question failed; <see cref="AssistantUpdate.Text"/> is a sentence for the user. The question is not kept.</summary>
        Error,

        /// <summary>Always the last update of every question, whatever happened before it.</summary>
        Done,
    }

    /// <summary>One step of an answer as the Ask window renders it.</summary>
    public sealed record AssistantUpdate(AssistantUpdateKind Kind, string? Text = null, string? ToolName = null,
                                         string? ToolArgs = null, SuggestedAction? Suggestion = null);
}
