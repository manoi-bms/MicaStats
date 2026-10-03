using System.Collections.Generic;
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Kil0bitSystemMonitor.Services.Pad.Search;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class NotesQuestionTests
    {
        private static Passage P(int i) =>
            new("n" + i, "Note " + i, "", i, i + 1, "line", "body " + i, "sent " + i, "h" + i);

        [Fact]
        public void Sources_caps_at_eight_and_keeps_order()
        {
            var hits = Enumerable.Range(1, 12).Select(P).ToList();
            var s = NotesQuestion.Sources(hits);
            Assert.Equal(NotesQuestion.MaxSources, s.Count);
            Assert.Equal(8, s.Count);
            Assert.Equal(hits.Take(8), s);
        }

        [Fact]
        public void Sources_keeps_fewer_hits_as_they_are()
        {
            var hits = new List<Passage> { P(1), P(2) };
            Assert.Equal(hits, NotesQuestion.Sources(hits));
        }

        [Fact]
        public void Message_cleans_a_credential_in_the_question()
        {
            var sources = new List<Passage> { P(1) };
            string msg = NotesQuestion.Message("what is {{secret:K7Q2M9XD}}?", sources);
            Assert.Contains("[credential]", msg);
            Assert.DoesNotContain("{{secret:", msg);
            Assert.Equal(PadAiPrompts.ForQuestion("what is [credential]?", sources), msg);
        }

        [Fact]
        public void Status_is_singular_for_one() => Assert.Equal("Answered from 1 passage", NotesQuestion.Status(1));

        [Fact]
        public void Status_is_plural_otherwise() => Assert.Equal("Answered from 6 passages", NotesQuestion.Status(6));

        [Fact]
        public void Answering_is_singular_for_one() => Assert.Equal("Answering from 1 passage", NotesQuestion.Answering(1));

        [Fact]
        public void Answering_is_plural_otherwise() => Assert.Equal("Answering from 6 passages", NotesQuestion.Answering(6));

        [Fact]
        public void Message_puts_each_passage_between_note_tags()
        {
            string msg = NotesQuestion.Message("where?", new List<Passage> { P(1), P(2) });
            Assert.Contains("[1] Note 1 (lines 1–2)\n<note>\nbody 1\n</note>\n\n[2] Note 2 (lines 2–3)\n<note>\nbody 2\n</note>", msg);
        }

        [Fact]
        public void The_fixed_sentences()
        {
            Assert.Equal("Nothing in your notes matches, so there is nothing to answer from.", NotesQuestion.NoSources);
            Assert.Equal("Turn on Settings → MicaPad → AI to get answers", NotesQuestion.AiOff);
        }
    }
}
