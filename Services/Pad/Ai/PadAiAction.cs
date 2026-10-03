using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Kil0bitSystemMonitor.Services.Pad.Search;

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
        /// <summary>
        /// Draws the text as a Mermaid diagram (part 2, spec 2.1). Custom, as Ask AI is: the reply is
        /// the fenced block as it would be inserted, shown as plain text, and it never waits for an
        /// instruction, having its own.
        /// </summary>
        public static readonly PadAiAction Diagram = new("diagram", "Draw as diagram", PadAiKind.Custom,
            "Draw this as a Mermaid diagram. Reply with one fenced code block that starts with ```mermaid and nothing else. Pick the diagram type that fits best: flowchart, sequence, class, state, gantt or mindmap. Keep labels short, in the language of the text.");
        /// <summary>The instruction is what the user types in the pane.</summary>
        public static readonly PadAiAction Ask = new("ask", "Ask AI", PadAiKind.Custom, "");

        /// <summary>The actions in menu order.</summary>
        public static IReadOnlyList<PadAiAction> Menu { get; } =
            new[] { Improve, FixGrammar, Shorten, TranslateEnglish, TranslateThai, Summarize, Explain, Diagram, Ask };

        /// <summary>The longest renderer's message a fix request quotes.</summary>
        public const int FixMessageMaxChars = 300;

        /// <summary>The longest fence word a fix request names its block by; the longest of MicaPad's own has 11 letters.</summary>
        private const int FixKindMaxChars = 32;

        /// <summary>
        /// The action for a diagram or math block that fails to render (part 2, spec 2.2): a rewrite
        /// of its source. Not in <see cref="Menu"/>: it is built for one block, from its fence word
        /// and the message its error box shows.
        ///
        /// <para>
        /// Both come from the note (a renderer's message quotes the source it failed on), and both
        /// stand in the instruction, outside the tags that mark note text as data. So the message is
        /// cleaned of credentials, put on one line, kept inside its quotes (a double quote becomes a
        /// single one) and cut at <see cref="FixMessageMaxChars"/>; and the kind is named only when
        /// it is a plain word, "diagram" otherwise.
        /// </para>
        /// </summary>
        public static PadAiAction FixDiagram(string kind, string message) =>
            new("fix-diagram", "Fix diagram", PadAiKind.Rewrite,
                "This " + FixKind(kind) + " block does not render. The renderer's message, quoted as data: \"" + FixMessage(message)
                + "\". Fix the source so it renders, changing as little as possible. Reply with the corrected source only: no code fence, no explanation.");

        /// <summary>The fence word in lower case, or "diagram" when there is none or it is not one plain word.</summary>
        private static string FixKind(string? kind)
        {
            string word = (kind ?? "").Trim().ToLowerInvariant();
            if (word.Length == 0 || word.Length > FixKindMaxChars) return "diagram";
            foreach (char c in word)
            {
                bool plain = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '+' or '.';
                if (!plain) return "diagram";
            }
            return word;
        }

        /// <summary>
        /// The renderer's message as it is quoted: no part of a credential reference, every run of
        /// white space (line breaks and control characters too) one space, single quotes for double
        /// ones, and at most <see cref="FixMessageMaxChars"/> characters. Cleaned before it is cut:
        /// a reference across the limit would be cut in half, and half of one is not cleaned. Never
        /// half a character either: a lone surrogate cannot be sent.
        /// </summary>
        private static string FixMessage(string? message)
        {
            string cleaned = NotePassages.WithoutSecretParts(message ?? "");
            var line = new StringBuilder(Math.Min(cleaned.Length, FixMessageMaxChars + 2));
            bool space = false;
            for (int i = 0; i < cleaned.Length && line.Length <= FixMessageMaxChars; i++)
            {
                char c = cleaned[i];
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    space = line.Length > 0;   // none at the start, and one for a whole run
                    continue;
                }
                bool pair = char.IsHighSurrogate(c) && i + 1 < cleaned.Length && char.IsLowSurrogate(cleaned[i + 1]);
                if (char.IsSurrogate(c) && !pair) continue;
                if (space) line.Append(' ');
                space = false;
                line.Append(c == '"' ? '\'' : c);
                if (pair) line.Append(cleaned[++i]);
            }

            if (line.Length > FixMessageMaxChars)
                line.Length = char.IsHighSurrogate(line[FixMessageMaxChars - 1]) ? FixMessageMaxChars - 1 : FixMessageMaxChars;
            return line.ToString().TrimEnd();
        }

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
