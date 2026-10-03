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

        private volatile bool _notesRead;

        /// <summary>
        /// True once a note tool handed note text to the model in this conversation. From then on
        /// note text may steer its answers, this one and every later one (the tool's result is
        /// sent again with each question), so the Ask window shows their links as text. Set by the
        /// note functions themselves, on whatever thread they run: what the tool loop reports
        /// about a call depends on the call ids a provider gives, and those can repeat.
        /// </summary>
        public bool NotesRead => _notesRead;

        /// <summary>A note tool returned notes; see <see cref="NotesRead"/>.</summary>
        internal void MarkNotesRead() => _notesRead = true;

        /// <summary>Starts over: forgets every message and suggestion.</summary>
        public void Clear()
        {
            Messages.Clear();
            Suggestions.Clear();
            _notesRead = false;
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
