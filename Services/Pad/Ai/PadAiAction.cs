using System.Collections.Generic;
using System.Globalization;

namespace Kil0bitSystemMonitor.Services.Pad.Ai
{
    /// <summary>What an AI action gives back: text to put in the note, an answer to read, or whatever the user asked for.</summary>
    public enum PadAiKind { Rewrite, Read, Custom }

    /// <summary>
    /// One AI action of MicaPad's AI menu (MicaPad AI spec 3.1): its name in the menu, the
    /// instruction sent to the model, and how much text it takes.
    /// </summary>
    public sealed record PadAiAction(string Id, string Name, PadAiKind Kind, string Instruction)
    {
        /// <summary>A rewrite must come back whole within the output cap, so it takes less text.</summary>
        public const int RewriteMaxChars = 8000;
        public const int ReadMaxChars = 24000;

        public static readonly PadAiAction Improve = new("improve", "Improve writing", PadAiKind.Rewrite,
            "Improve the writing: clearer and more natural, same meaning, not longer.");
        public static readonly PadAiAction FixGrammar = new("fix", "Fix spelling and grammar", PadAiKind.Rewrite,
            "Fix spelling, grammar and punctuation only. Change nothing else.");
        public static readonly PadAiAction Shorten = new("shorten", "Make shorter", PadAiKind.Rewrite,
            "Make it shorter while keeping every fact.");
        public static readonly PadAiAction TranslateEnglish = new("to-english", "Translate to English", PadAiKind.Rewrite,
            "Translate into English.");
        public static readonly PadAiAction TranslateThai = new("to-thai", "Translate to Thai", PadAiKind.Rewrite,
            "Translate into Thai.");
        public static readonly PadAiAction Summarize = new("summarize", "Summarize", PadAiKind.Read,
            "Summarize it in a few bullet points.");
        public static readonly PadAiAction Explain = new("explain", "Explain", PadAiKind.Read,
            "Explain what this is and what it does, briefly. If it is code, explain the code.");
        /// <summary>The instruction is what the user types in the pane.</summary>
        public static readonly PadAiAction Ask = new("ask", "Ask AI", PadAiKind.Custom, "");

        /// <summary>The actions in menu order.</summary>
        public static IReadOnlyList<PadAiAction> Menu { get; } =
            new[] { Improve, FixGrammar, Shorten, TranslateEnglish, TranslateThai, Summarize, Explain, Ask };

        /// <summary>A rewrite works on a selection only; the others take the whole note when nothing is selected.</summary>
        public bool NeedsSelection => Kind == PadAiKind.Rewrite;

        public int MaxChars => Kind == PadAiKind.Rewrite ? RewriteMaxChars : ReadMaxChars;

        /// <summary>An answer to read is shown as Markdown; text that may go into the note is shown as it is.</summary>
        public bool RendersMarkdown => Kind == PadAiKind.Read;

        /// <summary>The sentence for text that is too long to send, or null when it fits.</summary>
        public string? TooLong(int chars)
        {
            if (chars <= MaxChars) return null;
            string limit = MaxChars.ToString("N0", CultureInfo.InvariantCulture);
            return Kind == PadAiKind.Rewrite
                ? "Select less text: at most " + limit + " characters for a rewrite"
                : "Select less text: at most " + limit + " characters";
        }
    }
}
