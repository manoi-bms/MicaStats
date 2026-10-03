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
        public void A_credential_reference_across_the_size_limit_leaves_no_fragment()
        {
            // The reference starts 13 characters before the 2 MB limit and ends after it: a note cut
            // at the limit first would end with "{{secret:K7Q2", half a reference that nothing cleans.
            const string reference = "{{secret:K7Q2M9XD}}";
            const string line = "some words on a line\n";
            string filler = string.Concat(Enumerable.Repeat(line, NotePassages.MaxNoteChars / line.Length + 1))
                                  .Substring(0, NotePassages.MaxNoteChars - 13);

            var passages = NotePassages.Cut("n1", "t", filler + reference + "\nafter the limit");

            Assert.NotEmpty(passages);
            foreach (var p in passages)
            {
                foreach (string text in new[] { p.Body, p.SentText, p.FirstLineText, p.Heading, p.Title })
                {
                    Assert.DoesNotContain("{{secret", text, StringComparison.Ordinal);
                    Assert.DoesNotContain("secret:", text, StringComparison.Ordinal);
                    Assert.DoesNotContain("K7Q2", text, StringComparison.Ordinal);
                }
            }
            Assert.EndsWith("[credential]", passages[^1].Body.TrimEnd(), StringComparison.Ordinal);
            Assert.DoesNotContain(passages, p => p.Body.Contains("after the limit", StringComparison.Ordinal));   // the limit still holds
        }

        [Theory]
        [InlineData("db password is {{secret:K7Q2M9XD}}")]         // the 30 characters end inside the id
        [InlineData("my main database pwd {{secret:K7Q2M9XD}}")]   // ... right after the colon
        [InlineData("my password {{secret:K7Q2M9XD}}")]            // ... before the second closing brace
        [InlineData("the password {{secret:K7Q2M9XD}}")]           // ... before both closing braces
        public void A_credential_reference_cut_short_by_an_automatic_title_leaves_no_fragment(string firstLine)
        {
            string text = firstLine + "\nmore";
            string title = Kil0bitSystemMonitor.Services.Pad.NoteTitle.FromText(text, 1);
            Assert.Contains("{{secret:", title, StringComparison.Ordinal);       // the title is the line's first 30 characters:
            Assert.DoesNotContain("K7Q2M9XD}}", title, StringComparison.Ordinal); // half a reference, which nothing cleaned

            var p = Assert.Single(NotePassages.Cut("n1", title, text));

            Assert.EndsWith(" [credential]", p.Title, StringComparison.Ordinal);
            foreach (string sent in new[] { p.Title, p.SentText, p.Body, p.FirstLineText })
            {
                Assert.DoesNotContain("{{secret", sent, StringComparison.Ordinal);
                Assert.DoesNotContain("K7Q2", sent, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void A_title_that_only_looks_like_the_start_of_a_reference_is_kept()
        {
            Assert.Equal("braces {{", NotePassages.TitleWithoutSecrets("braces {{"));
            Assert.Equal("a {{secret", NotePassages.TitleWithoutSecrets("a {{secret"));   // no id character yet: nothing to hide
            Assert.Equal("a {{secret:K7Q2 and more", NotePassages.TitleWithoutSecrets("a {{secret:K7Q2 and more"));   // typed by hand, not cut: as any text
        }

        [Theory]
        [InlineData("explain {{secret:K7Q2", "explain [credential]")]                        // cut at its end
        [InlineData("vpn M9XD}} and {{secret:K7Q2", "vpn [credential] and [credential]")]    // cut at its start, and another at its end
        [InlineData("M9XD}} now", "[credential] now")]                                        // at the very start of the text
        [InlineData("a {{secret: b", "a [credential] b")]                                     // no id character yet
        [InlineData("a {{secret:K7Q2M9XD b", "a [credential] b")]                             // the whole id, no closing braces
        [InlineData("a {{secret:K7Q2M9XD} b", "a [credential] b")]                            // one closing brace short
        [InlineData("a {{secret:K7Q2}} b", "a [credential] b")]                               // too few characters to be a reference
        [InlineData("t:K7Q2M9XD}} b", "t:[credential] b")]                                    // cut inside its opening
        [InlineData("a {{secret:K7Q2M9XD}} b", "a [credential] b")]                           // a whole one, as WithoutSecrets
        public void A_reference_cut_at_either_end_becomes_credential(string text, string cleaned)
        {
            string result = NotePassages.WithoutSecretParts(text);

            Assert.Equal(cleaned, result);
            foreach (string part in new[] { "K7Q2", "M9XD", "{{secret" })
                Assert.DoesNotContain(part, result, StringComparison.Ordinal);   // no part of an id
        }

        [Theory]
        [InlineData("")]
        [InlineData("plain words, nothing to hide")]
        [InlineData("if (a) { b(); }}")]            // merely contains }}
        [InlineData("{{name}} and }} alone")]
        [InlineData("a {{secret")]                  // not the start of a reference yet
        [InlineData("braces {{ and {")]
        [InlineData("ABCDEFGHJK}}")]                // more characters than an id has
        public void Text_with_no_part_of_a_reference_is_unchanged(string text) =>
            Assert.Equal(text, NotePassages.WithoutSecretParts(text));

        [Fact]
        public void A_title_is_still_cleaned_only_at_its_end()
        {
            // The automatic title is a prefix of the first line: it can be cut at its end and nowhere else.
            Assert.Equal("db [credential]", NotePassages.TitleWithoutSecrets("db {{secret:K7Q2"));
            Assert.Equal("db [credential]", NotePassages.TitleWithoutSecrets("db {{secret:K7Q2M9XD}"));
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
