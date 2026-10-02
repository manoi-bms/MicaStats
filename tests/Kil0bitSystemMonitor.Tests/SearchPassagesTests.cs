using System;
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad.Search;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class SearchPassagesTests
    {
        [Fact]
        public void Paragraphs_are_packed_up_to_the_target_and_keep_their_lines()
        {
            string para = new string('a', 300);
            string text = para + "\n\n" + para + "\n\n" + para;   // lines 1, 3, 5

            var passages = NotePassages.Cut("n1", "Notes", text);

            Assert.Equal(2, passages.Count);                      // 300 + 2 + 300 fits 800; the third does not
            Assert.Equal((1, 3), (passages[0].FirstLine, passages[0].LastLine));
            Assert.Equal((5, 5), (passages[1].FirstLine, passages[1].LastLine));
        }

        [Fact]
        public void A_heading_starts_a_passage_and_names_it()
        {
            string text = "intro\n# Network\n## VPN\nit does not connect\n";

            var passages = NotePassages.Cut("n1", "ssh.md", text);

            Assert.Equal(3, passages.Count);
            Assert.Equal("", passages[0].Heading);
            Assert.Equal("Network", passages[1].Heading);
            Assert.Equal("Network › VPN", passages[2].Heading);
            Assert.Equal((3, 4), (passages[2].FirstLine, passages[2].LastLine));
            Assert.Equal("ssh.md › Network › VPN\n\n## VPN\nit does not connect", passages[2].SentText);
            Assert.Equal("ssh.md\n\nintro", passages[0].SentText);
        }

        [Fact]
        public void A_fenced_block_is_kept_whole_even_with_blank_lines()
        {
            // Long neighbours, so the fence is a passage of its own rather than packed with them.
            string text = new string('b', 790) + "\n\n```bash\necho a\n\necho b\n```\n\n" + new string('a', 790);

            var passages = NotePassages.Cut("n1", "t", text);

            var fence = Assert.Single(passages, p => p.Body.Contains("echo a", StringComparison.Ordinal));
            Assert.Contains("echo b", fence.Body, StringComparison.Ordinal);
            Assert.Equal(3, fence.FirstLine);
            Assert.Equal(7, fence.LastLine);
        }

        [Fact]
        public void A_closing_hash_run_is_dropped_only_after_a_space()
        {
            Assert.Equal("C#", NotePassages.Cut("n1", "t", "# C#\nbody")[0].Heading);
            Assert.Equal("Title", NotePassages.Cut("n1", "t", "# Title ##\nbody")[0].Heading);
            Assert.Equal("Tabs", NotePassages.Cut("n1", "t", "##\tTabs\t#\t\nbody")[0].Heading);
            Assert.Equal("", NotePassages.Cut("n1", "t", "#tag line\nbody")[0].Heading);   // no space: not a heading
            Assert.Equal("", NotePassages.Cut("n1", "t", "    # indented\nbody")[0].Heading);   // four spaces: not a heading
        }

        [Fact]
        public void A_heading_with_a_long_run_of_spaces_is_read_in_linear_time()
        {
            string line = "# a" + new string(' ', 4000) + "x";
            var clock = System.Diagnostics.Stopwatch.StartNew();

            var passages = NotePassages.Cut("n1", "t", line + "\nbody");

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), "took " + clock.Elapsed.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms");
            Assert.StartsWith("a", passages[0].Heading, StringComparison.Ordinal);
            Assert.True(passages[0].Heading.Length <= 200);
        }

        [Fact]
        public void A_huge_heading_is_capped_in_every_piece()
        {
            string capped = new string('h', 200);

            var passages = NotePassages.Cut("n1", "t", "# " + new string('h', 100_000));

            Assert.True(passages.Count > 1);
            Assert.All(passages, p => Assert.Equal(capped, p.Heading));
            Assert.All(passages, p => Assert.StartsWith("t › " + capped + "\n\n", p.SentText, StringComparison.Ordinal));
        }

        [Fact]
        public void A_heading_inside_a_fence_is_not_a_heading()
        {
            var passages = NotePassages.Cut("n1", "t", "```\n# not a heading\n```");
            Assert.All(passages, p => Assert.Equal("", p.Heading));
        }

        [Fact]
        public void A_long_fence_is_cut_at_line_boundaries_under_the_cap()
        {
            string body = string.Join("\n", Enumerable.Range(0, 100).Select(i => "line " + i + " " + new string('x', 40)));
            var passages = NotePassages.Cut("n1", "t", "```\n" + body + "\n```");

            Assert.True(passages.Count > 1);
            Assert.All(passages, p => Assert.True(p.Body.Length <= NotePassages.MaxChars));
            for (int i = 1; i < passages.Count; i++) Assert.Equal(passages[i - 1].LastLine + 1, passages[i].FirstLine);
        }

        [Fact]
        public void A_huge_single_line_is_cut_and_its_first_line_text_is_short()
        {
            string line = new string('{', 2 * 1024 * 1024 + 10);

            var passages = NotePassages.Cut("n1", "t", line);

            Assert.Equal((int)Math.Ceiling(NotePassages.MaxNoteChars / (double)NotePassages.MaxChars), passages.Count);
            Assert.All(passages, p => Assert.True(p.Body.Length <= NotePassages.MaxChars));
            Assert.All(passages, p => Assert.True(p.FirstLineText.Length <= NotePassages.MaxFirstLineChars));
            Assert.All(passages, p => Assert.Equal(1, p.FirstLine));
        }

        [Fact]
        public void Credential_references_are_never_indexed()
        {
            var passages = NotePassages.Cut("n1", "t", "db password {{secret:K7Q2M9XD}} here");
            Assert.Equal("db password [credential] here", Assert.Single(passages).Body);
            Assert.DoesNotContain("K7Q2M9XD", passages[0].SentText, StringComparison.Ordinal);
        }

        [Fact]
        public void A_credential_reference_in_the_title_is_never_indexed()
        {
            var p = Assert.Single(NotePassages.Cut("n1", "db {{secret:K7Q2M9XD}}", "# Access\nuser admin"));
            Assert.StartsWith("db [credential] › Access\n\n", p.SentText, StringComparison.Ordinal);
            Assert.DoesNotContain("K7Q2M9XD", p.SentText, StringComparison.Ordinal);
            Assert.DoesNotContain("K7Q2M9XD", p.Title, StringComparison.Ordinal);
        }

        [Fact]
        public void Blank_text_has_no_passages()
        {
            Assert.Empty(NotePassages.Cut("n1", "t", " \n\n \t\n"));
        }

        [Fact]
        public void Crlf_and_cr_line_endings_count_lines_the_same()
        {
            var lf = NotePassages.Cut("n1", "t", "a\n\nb");
            var crlf = NotePassages.Cut("n1", "t", "a\r\n\r\nb");
            Assert.Equal(lf.Select(p => (p.FirstLine, p.LastLine, p.Body)), crlf.Select(p => (p.FirstLine, p.LastLine, p.Body)));
        }

        [Fact]
        public void Thai_text_is_cut_like_any_other()
        {
            var passages = NotePassages.Cut("n1", "บันทึก", "# เครือข่าย\nวีพีเอ็นไม่ติด แก้โดยรีสตาร์ท");
            var p = Assert.Single(passages);
            Assert.Equal("เครือข่าย", p.Heading);
            Assert.StartsWith("บันทึก › เครือข่าย\n\n", p.SentText, StringComparison.Ordinal);
            Assert.Equal("# เครือข่าย", p.FirstLineText);
        }

        [Fact]
        public void The_hash_follows_the_sent_text()
        {
            var a = NotePassages.Cut("n1", "t", "same text").Single();
            var b = NotePassages.Cut("n2", "t", "same text").Single();
            var c = NotePassages.Cut("n1", "other title", "same text").Single();

            Assert.Equal(a.Hash, b.Hash);
            Assert.NotEqual(a.Hash, c.Hash);
            Assert.Equal(64, a.Hash.Length);
            Assert.Equal(a.Hash.ToLowerInvariant(), a.Hash);
        }
    }
}
