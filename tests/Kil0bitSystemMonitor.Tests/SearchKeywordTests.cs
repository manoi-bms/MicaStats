using System.Linq;
using Kil0bitSystemMonitor.Services.Pad.Search;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class SearchKeywordTests
    {
        [Fact]
        public void A_compound_is_one_token_and_its_parts()
        {
            Assert.Equal(new[] { "err-1042", "err", "1042" }, SearchTokens.Of("ERR-1042").ToArray());
            Assert.Equal(new[] { "10.0.0.1", "10", "0", "0", "1" }, SearchTokens.Of("10.0.0.1").ToArray());
            Assert.Equal(new[] { "hn", "6501234" }, SearchTokens.Of("HN 6501234").ToArray());
        }

        [Fact]
        public void A_trailing_joiner_is_not_part_of_the_word()
        {
            Assert.Equal(new[] { "end" }, SearchTokens.Of("end.").ToArray());
            Assert.Equal(new[] { "a", "b" }, SearchTokens.Of("a - b").ToArray());
        }

        [Fact]
        public void Thai_runs_become_two_character_pieces()
        {
            Assert.Equal(new[] { "ไม", "ม่", "่ต", "ติ", "ิด" }, SearchTokens.Of("ไม่ติด").ToArray());
            Assert.Equal(new[] { "ก" }, SearchTokens.Of("ก").ToArray());
            Assert.Equal(new[] { "vpn", "ไม", "ม่" }, SearchTokens.Of("VPN ไม่").ToArray());
        }

        [Fact]
        public void Overlong_tokens_are_dropped()
        {
            Assert.Empty(SearchTokens.Of(new string('a', SearchTokens.MaxTokenChars + 1)));
            Assert.Single(SearchTokens.Of(new string('a', SearchTokens.MaxTokenChars)));
        }

        [Fact]
        public void The_last_word_is_a_prefix_only_while_it_is_being_typed()
        {
            Assert.Equal("conn", SearchTokens.LastWordPrefix("vpn conn"));
            Assert.Null(SearchTokens.LastWordPrefix("vpn conn "));
            Assert.Null(SearchTokens.LastWordPrefix("vpn c"));      // one letter expands to too much
            Assert.Null(SearchTokens.LastWordPrefix("ไม่ติด"));
        }

        private static KeywordIndex IndexOf(params (string Id, string Title, string Text)[] notes)
        {
            var index = new KeywordIndex();
            foreach (var n in notes) index.Set(n.Id, NotePassages.Cut(n.Id, n.Title, n.Text));
            return index;
        }

        [Fact]
        public void Codes_and_addresses_match_exactly_and_by_part()
        {
            var index = IndexOf(("a", "errors", "printer shows ERR-1042 again"), ("b", "net", "gateway is 10.0.0.1"), ("c", "x", "nothing here"));

            Assert.Equal("a", index.Search("err-1042", 10).First().Passage.NoteId);
            Assert.Equal("a", index.Search("1042", 10).First().Passage.NoteId);
            Assert.Equal("b", index.Search("10.0.0.1", 10).First().Passage.NoteId);
            Assert.DoesNotContain(index.Search("err-1042", 10), h => h.Passage.NoteId == "c");
        }

        [Fact]
        public void A_Thai_query_finds_Thai_text()
        {
            var index = IndexOf(("a", "บันทึก", "วีพีเอ็นไม่ติด แก้โดยรีสตาร์ท"), ("b", "อื่น", "ประชุมพรุ่งนี้"));
            Assert.Equal("a", index.Search("ไม่ติด", 10).First().Passage.NoteId);
        }

        [Fact]
        public void A_mixed_Thai_and_English_query_finds_both()
        {
            var index = IndexOf(("a", "t", "VPN config notes"), ("b", "t", "เน็ตไม่ติดตอนเช้า"), ("c", "t", "lunch menu"));
            var ids = index.Search("vpn ไม่ติด", 10).Select(h => h.Passage.NoteId).ToList();
            Assert.Contains("a", ids);
            Assert.Contains("b", ids);
            Assert.DoesNotContain("c", ids);
        }

        [Fact]
        public void A_denser_passage_ranks_first()
        {
            var index = IndexOf(("a", "t", "backup " + string.Join(" ", Enumerable.Repeat("word", 60))), ("b", "t", "backup backup backup restore"));
            Assert.Equal("b", index.Search("backup", 10).First().Passage.NoteId);
        }

        [Fact]
        public void The_word_being_typed_matches_as_a_prefix()
        {
            var index = IndexOf(("a", "t", "connection refused"), ("b", "t", "nothing"));
            Assert.Equal("a", Assert.Single(index.Search("conn", 10)).Passage.NoteId);
            Assert.Empty(index.Search("conn ", 10));   // a finished word must match whole
        }

        [Fact]
        public void The_title_is_searchable()
        {
            var index = IndexOf(("a", "Wireguard setup", "keys and peers"));
            Assert.Single(index.Search("wireguard", 10));
        }

        [Fact]
        public void Updating_and_removing_a_note_replace_its_passages()
        {
            var index = IndexOf(("a", "t", "alpha"));
            index.Set("a", NotePassages.Cut("a", "t", "beta"));
            Assert.Empty(index.Search("alpha", 10));
            Assert.Single(index.Search("beta", 10));

            index.Remove("a");
            Assert.Empty(index.Search("beta", 10));
            Assert.Equal(0, index.PassageCount);
            Assert.Equal(0, index.NoteCount);
        }

        [Fact]
        public void An_empty_query_or_index_finds_nothing()
        {
            Assert.Empty(new KeywordIndex().Search("x", 10));
            Assert.Empty(IndexOf(("a", "t", "text")).Search("  ", 10));
        }

        [Fact]
        public void The_limit_is_kept()
        {
            var index = new KeywordIndex();
            for (int i = 0; i < 30; i++) index.Set("n" + i, NotePassages.Cut("n" + i, "t", "common word " + i));
            Assert.Equal(5, index.Search("common", 5).Count);
        }
    }
}
