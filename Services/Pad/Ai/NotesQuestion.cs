using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad.Search;

namespace Kil0bitSystemMonitor.Services.Pad.Ai
{
    /// <summary>The question for notes, built from the passages Search notes found (MicaPad AI spec 4).</summary>
    public static class NotesQuestion
    {
        public const int MaxSources = 8;
        public const string NoSources = "Nothing in your notes matches, so there is nothing to answer from.";
        public const string AiOff = "Turn on Settings → MicaPad → AI to get answers";

        /// <summary>The first <see cref="MaxSources"/> hits, in order.</summary>
        public static IReadOnlyList<Passage> Sources(IReadOnlyList<Passage> hits) => hits.Take(MaxSources).ToList();

        /// <summary>The user message: the question with any credential reference taken out, then the numbered sources.</summary>
        public static string Message(string question, IReadOnlyList<Passage> sources) =>
            PadAiPrompts.ForQuestion(NotePassages.WithoutSecrets(question ?? ""), sources);

        public static string Status(int sources) =>
            "Answered from " + sources.ToString(CultureInfo.InvariantCulture) + (sources == 1 ? " passage" : " passages");
    }
}
