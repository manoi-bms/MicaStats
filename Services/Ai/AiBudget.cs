using System;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>
    /// Every limit the AI code uses, as one value. With no context window known it is exactly
    /// today's fixed numbers (<see cref="Standard"/>); with one, the limits follow the window
    /// (spec 2026-10-05, section 2.2), and never pass a ceiling that no provider can raise.
    /// </summary>
    public sealed record AiBudget
    {
        /// <summary>The window in use, in tokens; 0 when unknown.</summary>
        public int ContextTokens { get; init; }
        /// <summary>Largest answer Ask MicaStats asks for. Always tokens (the number sent as the request's limit).</summary>
        public int AskOutputTokens { get; init; }
        /// <summary>Largest answer MicaPad asks for. Always tokens.</summary>
        public int PadOutputTokens { get; init; }
        /// <summary>
        /// Most text a rewrite takes. Characters when <see cref="InTokens"/> is false, estimated tokens
        /// when true; compare it with <see cref="Measure"/>, never by counting by hand.
        /// </summary>
        public int RewriteInput { get; init; }
        /// <summary>
        /// Most text Summarize, Explain, Ask AI and Draw as diagram take. The same unit as
        /// <see cref="RewriteInput"/>: characters when <see cref="InTokens"/> is false, estimated tokens when true.
        /// </summary>
        public int ReadInput { get; init; }
        /// <summary>Longest MicaPad reply kept. Always characters, whether or not a window is known.</summary>
        public int PadReplyChars { get; init; }
        /// <summary>An Ask conversation as sent again. Always estimated tokens; 48,000 when no window is known.</summary>
        public int HistoryTokens { get; init; }
        /// <summary>
        /// What get_note gives Ask per call, in estimated tokens. 0 means no window is known: the caller
        /// keeps its own character limit. It never means "allow nothing".
        /// </summary>
        public int NoteReadTokens { get; init; }
        /// <summary>What get_note gives Ask per call. Always lines.</summary>
        public int NoteReadLines { get; init; }
        /// <summary>
        /// A tool result kept in the conversation, in estimated tokens. 0 means no window is known: the
        /// caller keeps its own character limit. It never means "allow nothing".
        /// </summary>
        public int KeptResultTokens { get; init; }
        /// <summary>Passages Ask your notes may use. Always a count.</summary>
        public int NotesSources { get; init; }

        /// <summary>
        /// True when a window is known. This says the unit of <see cref="RewriteInput"/> and
        /// <see cref="ReadInput"/> (estimated tokens when true, characters when false), and of nothing else:
        /// every other number has one unit of its own, written on it.
        /// </summary>
        public bool InTokens => ContextTokens > 0;

        /// <summary>
        /// The size of a text in the unit of <see cref="RewriteInput"/> and <see cref="ReadInput"/>: its
        /// estimated tokens when <see cref="InTokens"/>, otherwise its length in characters. Null is 0.
        /// </summary>
        public int Measure(string? text) => InTokens ? TokenEstimate.Of(text) : text?.Length ?? 0;

        private const int MinWindow = 1024;
        private const int MaxWindow = 2_000_000;

        /// <summary>The most either answer asks for while the provider reported neither a window nor a largest output.</summary>
        public const int UnreportedOutputCeiling = 4096;

        /// <summary>Today's numbers; no window.</summary>
        public static AiBudget Standard { get; } = new AiBudget
        {
            ContextTokens = 0,
            AskOutputTokens = 2000,
            PadOutputTokens = 4096,
            RewriteInput = 8000,
            ReadInput = 24000,
            PadReplyChars = 64000,
            HistoryTokens = 48000,
            NoteReadTokens = 0,
            NoteReadLines = 400,
            KeptResultTokens = 0,
            NotesSources = 8,
        };

        /// <summary>
        /// The limits for a model. <paramref name="reportedContext"/> and <paramref name="reportedOutput"/> are
        /// as the provider gave them (0 when it gave none); <paramref name="userContext"/> is the number set in
        /// Settings (0 for Auto). With no usable window this is <see cref="Standard"/> itself.
        /// </summary>
        public static AiBudget For(int reportedContext, int reportedOutput, int userContext)
        {
            int w = WindowInUse(reportedContext, userContext);
            if (w == 0) return Standard;

            // The floors are for ordinary windows; on a small one the pieces are held to shares of it
            // (output and input together fit the window; Ask's output and one note read fit half of it).
            int ask = Math.Min(Math.Clamp(w / 8, 2000, 16000), w / 4);
            int pad = Math.Min(Math.Clamp(w / 4, 2048, 32000), w / 2);
            // A largest output the provider reported caps both; it never raises them, and may take them below their floor.
            if (reportedOutput >= 256)   // under 256 counts as not reported: 1 to 4 would make the rewrite input zero
            {
                ask = Math.Min(ask, reportedOutput);
                pad = Math.Min(pad, reportedOutput);
            }
            else if (Normalize(reportedContext) == 0)
            {
                // The provider gave nothing and the window is the user's own number: a model's largest
                // answer is often far below its window, and a provider refuses a request that asks for more.
                ask = Math.Min(ask, UnreportedOutputCeiling);
                pad = Math.Min(pad, UnreportedOutputCeiling);
            }

            int noteRead = Math.Min(Math.Clamp(w / 16, 4000, 64000), w / 4);
            return new AiBudget
            {
                ContextTokens = w,
                AskOutputTokens = ask,
                PadOutputTokens = pad,
                RewriteInput = (int)((long)pad * 4 / 5),
                ReadInput = Math.Max(Math.Min(1000, w / 4), w / 2 - pad),
                PadReplyChars = Math.Max(64000, 4 * pad),
                HistoryTokens = w / 2,
                NoteReadTokens = noteRead,
                NoteReadLines = w >= 1_000_000 ? 4000 : w >= 128_000 ? 2000 : w >= 32_000 ? 1000 : 400,
                KeptResultTokens = noteRead + noteRead / 4,
                NotesSources = w >= 128_000 ? 20 : w >= 32_000 ? 12 : 8,
            };
        }

        /// <summary>
        /// The window in use: the user's number when the provider gave none; the smaller of the two when
        /// both; the provider's when the user set none; 0 when neither. A number under 1,024 counts as none,
        /// one over 2,000,000 as 2,000,000.
        /// </summary>
        public static int WindowInUse(int reportedContext, int userContext)
        {
            int reported = Normalize(reportedContext);
            int user = Normalize(userContext);
            if (reported == 0) return user;
            if (user == 0) return reported;
            return Math.Min(reported, user);
        }

        private static int Normalize(int window) => window < MinWindow ? 0 : Math.Min(window, MaxWindow);
    }
}
