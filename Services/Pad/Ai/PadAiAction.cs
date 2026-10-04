using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Kil0bitSystemMonitor.Services.Ai;
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
        /// <summary>
        /// A rewrite must come back whole within the output cap, so it takes less text. These two
        /// are the limits while no context window is known; with one, the budget's shares of it
        /// count (<see cref="TooLong(string, AiBudget)"/>).
        /// </summary>
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
        /// the fenced block as it would be inserted, which the pane shows rendered, the diagram
        /// drawn (<see cref="RendersMarkdown"/>), and it never waits for an instruction, having its own.
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
        /// put on one line, kept inside its quotes (a double quote becomes a single one) and free of
        /// angle brackets (it can write no tag), cleaned of every part of a credential and cut at
        /// <see cref="FixMessageMaxChars"/>; and the kind is named only when it is a plain word,
        /// "diagram" otherwise.
        /// </para>
        /// </summary>
        /// <param name="fence">
        /// The run of characters the block's opening fence is made of (<see cref="BlockFence"/>).
        /// The window reads it from the note when the fix is asked for.
        /// </param>
        /// <param name="credentialIds">
        /// The ids of the credential references in the block's source, which the window reads
        /// with the source. Each is taken out of the message wherever it stands: a renderer can
        /// quote a reference with one brace, with none, or as the bare id, which no pattern for
        /// references matches.
        /// </param>
        public static PadAiAction FixDiagram(string kind, string message, string fence = "```", IEnumerable<string>? credentialIds = null) =>
            new("fix-diagram", "Fix diagram", PadAiKind.Rewrite,
                "This " + FixKind(kind) + " block does not render. The renderer's message, quoted as data: \"" + FixMessage(message, credentialIds)
                + "\". Fix the source so it renders, changing as little as possible. Reply with the corrected source only: no code fence, no explanation.")
            { BlockFence = string.IsNullOrEmpty(fence) ? "```" : fence };

        /// <summary>
        /// Set for the fix of a diagram or math block and for no other action: the run of
        /// characters its block's opening fence is made of (three backticks, four tildes,
        /// <c>$$</c>). The result of a fix goes between that fence and its closing one, so a line
        /// in it that would close the fence must never be put there: the note's own closing fence
        /// would then open a block that never ends. <see cref="AiSession"/> reads it: it unwraps a
        /// fix that came back in a code fence, refuses Replace selection for one that would close
        /// the block, and offers no Insert below, which would land inside the block.
        /// </summary>
        public string? BlockFence { get; init; }

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
        /// The renderer's message as it is quoted, in three steps whose order matters.
        ///
        /// <para>
        /// 1. Folded onto one line: every run of white space, line breaks, control characters,
        /// angle brackets and lone surrogates becomes one space, and a double quote a single one.
        /// Angle brackets go so the message can write no tag in the instruction; half a character
        /// goes because it cannot be sent.
        /// </para>
        /// <para>
        /// 2. Cleaned of every part of a credential reference, a reference cut at its start in the
        /// middle of the message too (<see cref="NotePassages.WithoutSecretPartsAnywhere"/>). After
        /// the folding, never before it: whatever the folding drops or changes could stand inside
        /// a reference, and taking it out after the cleaning would put the two halves side by
        /// side again, uncleaned. Then cleaned of every id among <paramref name="credentialIds"/>
        /// (<see cref="NotePassages.WithoutIds"/>): what a renderer quoted in a form that is no
        /// reference at all.
        /// </para>
        /// <para>
        /// 3. Cut at <see cref="FixMessageMaxChars"/> characters, never through a character. After
        /// the cleaning: a reference or an id across the limit would be cut in half, and less of
        /// it cleaned.
        /// </para>
        /// </summary>
        private static string FixMessage(string? message, IEnumerable<string>? credentialIds)
        {
            string raw = message ?? "";
            var line = new StringBuilder(raw.Length);
            bool space = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                bool pair = char.IsHighSurrogate(c) && i + 1 < raw.Length && char.IsLowSurrogate(raw[i + 1]);
                if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>' || (char.IsSurrogate(c) && !pair))
                {
                    space = line.Length > 0;   // none at the start, and one for a whole run
                    continue;
                }
                if (space) line.Append(' ');
                space = false;
                line.Append(c == '"' ? '\'' : c);
                if (pair) line.Append(raw[++i]);
            }

            string cleaned = NotePassages.WithoutIds(NotePassages.WithoutSecretPartsAnywhere(line.ToString()), credentialIds);
            if (cleaned.Length > FixMessageMaxChars)
            {
                int cut = char.IsHighSurrogate(cleaned[FixMessageMaxChars - 1]) ? FixMessageMaxChars - 1 : FixMessageMaxChars;
                cleaned = cleaned.Substring(0, cut);
            }
            return cleaned.TrimEnd();
        }

        /// <summary>A rewrite works on a selection only; the others take the whole note when nothing is selected.</summary>
        public bool NeedsSelection => Kind == PadAiKind.Rewrite;

        /// <summary>The most characters this action takes while no context window is known.</summary>
        public int MaxChars => Kind == PadAiKind.Rewrite ? RewriteMaxChars : ReadMaxChars;

        /// <summary>
        /// A rewrite is shown as its text, exactly what Replace selection would put in the note, with
        /// Changes beside it. Every other result is shown rendered (AI chat UI spec 3.2): an answer
        /// to read, and what Draw as diagram and Ask AI give back, so a diagram is seen before
        /// anything is inserted; the pane's Source toggle shows the text. What goes into the note
        /// is the text either way, never the rendering.
        /// </summary>
        public bool RendersMarkdown => Kind != PadAiKind.Rewrite;

        /// <summary>
        /// The sentence for text that is too long to send within <paramref name="budget"/>, or null
        /// when it fits (AI model limits spec 2.4). No budget is the standard one.
        ///
        /// <para>
        /// With no context window known the text counts in characters, and the limits and the
        /// sentences are the fixed ones of <see cref="TooLong(int)"/>. With a window it counts in
        /// estimated tokens, where a Thai character is a whole token and an ASCII one a quarter,
        /// against the budget's share for a rewrite or for the other actions, and the sentence
        /// names both numbers. Either way the size is the budget's own measure of the text
        /// (<see cref="AiBudget.Measure"/>), never a count made here.
        /// </para>
        /// </summary>
        public string? TooLong(string? text, AiBudget? budget)
        {
            budget ??= AiBudget.Standard;
            int size = budget.Measure(text);
            if (!budget.InTokens) return TooLong(size);   // characters: the limits and the sentences from before

            int limit = Kind == PadAiKind.Rewrite ? budget.RewriteInput : budget.ReadInput;
            if (size <= limit) return null;
            return (Kind == PadAiKind.Rewrite ? "This text is too long for a rewrite with this model: about " : "This text is too long for this model: about ")
                   + size.ToString("N0", CultureInfo.InvariantCulture) + " tokens, and it can take about "
                   + limit.ToString("N0", CultureInfo.InvariantCulture) + ". Select less text.";
        }

        /// <summary>
        /// The sentence for text of <paramref name="chars"/> characters that is too long to send
        /// while no context window is known, or null when it fits: the fixed limits
        /// (<see cref="MaxChars"/>), which <see cref="AiBudget.Standard"/> holds too.
        /// </summary>
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
