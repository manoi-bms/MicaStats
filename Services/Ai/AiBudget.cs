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
        /// <summary>Largest answer Ask MicaStats asks for, in tokens.</summary>
        public int AskOutputTokens { get; init; }
        /// <summary>Largest answer MicaPad asks for, in tokens.</summary>
        public int PadOutputTokens { get; init; }
        /// <summary>Most text a rewrite takes: characters when <see cref="ContextTokens"/> is 0, estimated tokens otherwise.</summary>
        public int RewriteInput { get; init; }
        /// <summary>Most text Summarize, Explain, Ask AI and Draw as diagram take: the same unit as <see cref="RewriteInput"/>.</summary>
        public int ReadInput { get; init; }
        /// <summary>Longest MicaPad reply kept, in characters.</summary>
        public int PadReplyChars { get; init; }
        /// <summary>An Ask conversation as sent again, in tokens; 48,000 when the window is unknown.</summary>
        public int HistoryTokens { get; init; }
        /// <summary>What get_note gives Ask per call, in tokens; 0 when unknown (the caller then uses its character cap).</summary>
        public int NoteReadTokens { get; init; }
        /// <summary>What get_note gives Ask per call, in lines.</summary>
        public int NoteReadLines { get; init; }
        /// <summary>A tool result kept in the conversation, in tokens; 0 when unknown.</summary>
        public int KeptResultTokens { get; init; }
        /// <summary>Passages Ask your notes may use.</summary>
        public int NotesSources { get; init; }

        /// <summary>True when the limits are in estimated tokens (a window is known), false when in characters.</summary>
        public bool InTokens => ContextTokens > 0;

        private const int MinWindow = 1024;
        private const int MaxWindow = 2_000_000;

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

            int ask = Math.Clamp(w / 8, 2000, 16000);
            int pad = Math.Clamp(w / 4, 2048, 32000);
            // A largest output the provider reported caps both; it never raises them, and may take them below their floor.
            if (reportedOutput > 0)
            {
                ask = Math.Min(ask, reportedOutput);
                pad = Math.Min(pad, reportedOutput);
            }

            int noteRead = Math.Clamp(w / 16, 4000, 64000);
            return new AiBudget
            {
                ContextTokens = w,
                AskOutputTokens = ask,
                PadOutputTokens = pad,
                RewriteInput = (int)((long)pad * 4 / 5),
                ReadInput = Math.Max(1000, w / 2 - pad),
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
