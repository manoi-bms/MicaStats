# MicaPad Search by Words and Meaning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Search notes pane (Ctrl+Shift+F) in MicaPad that finds passages in every note by keywords (offline, built in) and, when turned on, by meaning through the owner's own embedding server, reordered by their reranker.

**Architecture:** Pure units in `Services/Pad/Search/` (passages, tokens, BM25 keyword index, HTTP clients for the OpenAI embeddings and Cohere/Jina rerank formats, an encrypted vector store, a background indexer, the query pipeline) composed by `NoteSearchService`; WPF parts in `Pad/` (feeder, settings panel, pane). App creates one service with the MicaPad workspace.

**Tech Stack:** C# / .NET 8 WPF (`net8.0-windows`), System.Text.Json, System.Net.Http (SocketsHttpHandler), AvalonEdit 6.3, xUnit 2.9.2.

**Spec:** `docs/superpowers/specs/2026-10-02-micapad-semantic-search-design.md`

## Global Constraints

- Tests never touch `%APPDATA%`, never use the network, never launch MicaStats. HTTP clients take an `HttpMessageHandler`; stores take a folder.
- Never build into `bin\Release` (that folder is the owner's deployed app). Build and test with the user-local SDK: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~<Class>"`.
- UI tests run only on the shared UI thread: `UiThread.Run(() => { ... })`.
- The build must gain no warnings (4 old xUnit1031 remain): never call `Task.Wait`, `.Result` or `GetAwaiter().GetResult()` inside a `[Fact]`/`[Theory]` body or a lambda written in it; wait through a helper method as the plan's tests do. Never compare a `string[]` with a `List<string>` in `Assert.Equal` (xUnit 2.9.2 then ignores whitespace); compare arrays.
- Secrets (API keys) live only in `secrets.bin` through `SecretStore` (`App.AiSecrets`) under `pad-embedding-key` and `pad-rerank-key`; never in `config.json`, never logged, never shown after saving.
- Logs (area `search`) carry counts, HTTP status codes and exception type names only — never note text, queries, keys or answers. Log the exception type only (`ex.GetType().Name`).
- Nothing leaves the PC unless **Search by meaning** (`PadSemanticSearch`) is on; reranking additionally needs `PadRerank`.
- Credential references `{{secret:XXXXXXXX}}` become `[credential]` in everything indexed or sent.
- Numbers from the spec, verbatim: passages ~800 chars (`TargetChars = 800`), cap 1,500 (`MaxChars = 1500`), 2 MB per note (`MaxNoteChars = 2 * 1024 * 1024`); BM25 k1 = 1.2, b = 0.75; 50 prefix expansions; 16 passages per embedding request; timeouts 60 s batch / 5 s query embedding / 8 s rerank; answers over 32 MB refused; keyword top 50, vector top 50, RRF k = 60, rerank top 40, 3 per note, 20 in all; 20 cached query vectors; 20,000 vectors; vectors saved at most every 10 s; retry after 1, 2, 5, 10 then every 30 minutes; debounce 2 s per note; search 300 ms after typing.
- Commits: explicit `git add <files>`, `git commit -m`, never amend, message ends with `Co-Authored-By: <your model name> <noreply@anthropic.com>`.
- Match the surrounding code: XML doc comments on public members, the repo's comment density and naming.

## Review Focus

1. A server that hangs or is slow while the user types — the pane stays responsive: query embedding gives up after 5 s and a newer query cancels the older one (Task 6 test `A_hanging_embedding_server_times_out_to_words_only` and `A_newer_query_cancels_the_older_one`).
2. The same note edited twice quickly while a batch is being embedded — the latest text wins and no passage of the older text stays searchable (Task 5 test `The_latest_text_of_a_note_wins`).
3. The server's model is swapped behind the same address and returns another dimension — vectors of mixed dimensions are never compared; the store is emptied and indexing starts over (Task 4 test `Put_refuses_another_dimension`, Task 5 test `A_dimension_change_starts_the_vectors_over`).
4. A 2 MB note on one line (minified JSON, a log without newlines) — cut into 1,500-character passages quickly, nothing hangs, the first-line text stays short (Task 1 test `A_huge_single_line_is_cut_and_its_first_line_text_is_short`).
5. A mixed Thai and English query with spaces ("vpn ไม่ติด") — both parts find their passages (Task 2 test `A_mixed_Thai_and_English_query_finds_both`).

---

### Task 1: Passages (`NotePassages`)

**Files:**
- Create: `Services/Pad/Search/Passage.cs`
- Create: `Services/Pad/Search/NotePassages.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/SearchPassagesTests.cs`

**Interfaces:**
- Consumes: `SecretTokens.Pattern` (`Services/Pad/SecretTokens.cs`).
- Produces:
  - `public sealed record Passage(string NoteId, string Title, string Heading, int FirstLine, int LastLine, string FirstLineText, string Body, string SentText, string Hash)` — lines 1-based; `SentText` is what is indexed, embedded and reranked; `Hash` is lowercase hex SHA-256 of `SentText`.
  - `public static class NotePassages { const int TargetChars = 800; const int MaxChars = 1500; const int MaxNoteChars = 2097152; const int MaxFirstLineChars = 200; static IReadOnlyList<Passage> Cut(string noteId, string title, string text); static string WithoutSecrets(string text); static string HashOf(string sentText); }`

- [ ] **Step 1: Write the failing tests**

```csharp
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

            var fence = Assert.Single(passages, p => p.Body.Contains("echo a"));
            Assert.Contains("echo b", fence.Body);
            Assert.Equal(3, fence.FirstLine);
            Assert.Equal(7, fence.LastLine);
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
            Assert.DoesNotContain("K7Q2M9XD", passages[0].SentText);
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
            Assert.StartsWith("บันทึก › เครือข่าย\n\n", p.SentText);
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~SearchPassagesTests"`
Expected: build error, `NotePassages` does not exist.

- [ ] **Step 3: Write `Passage.cs`**

```csharp
namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>
    /// One searchable piece of a note (spec 3.1). Lines are 1-based. <see cref="SentText"/> is what
    /// the keyword index reads and what the embedding server and the reranker are sent: the title
    /// and heading path, a blank line, then <see cref="Body"/>. <see cref="Hash"/> (lowercase hex
    /// SHA-256 of <see cref="SentText"/>) keys the passage's vector.
    /// </summary>
    public sealed record Passage(
        string NoteId,
        string Title,
        string Heading,
        int FirstLine,
        int LastLine,
        string FirstLineText,
        string Body,
        string SentText,
        string Hash);
}
```

- [ ] **Step 4: Write `NotePassages.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>
    /// Cuts a note into passages (spec 3.1): at Markdown headings and blank lines, packed up to
    /// about <see cref="TargetChars"/>, never over <see cref="MaxChars"/>. A fenced code block stays
    /// whole when it fits; anything longer is cut at line boundaries, and a single longer line at
    /// <see cref="MaxChars"/>. Only the first <see cref="MaxNoteChars"/> of a note are read.
    /// </summary>
    public static class NotePassages
    {
        public const int TargetChars = 800;
        public const int MaxChars = 1500;
        public const int MaxNoteChars = 2 * 1024 * 1024;
        public const int MaxFirstLineChars = 200;

        private const string HeadingSeparator = " › ";
        private static readonly Regex Secret = new(SecretTokens.Pattern, RegexOptions.CultureInvariant);
        private static readonly Regex Heading = new(@"^ {0,3}(#{1,6})[ \t]+(.*?)[ \t]*#*[ \t]*$", RegexOptions.CultureInvariant);

        /// <summary>Credential references replaced by <c>[credential]</c>; the vault is never read.</summary>
        public static string WithoutSecrets(string text) => Secret.Replace(text, "[credential]");

        /// <summary>Lowercase hex SHA-256 of the UTF-8 text.</summary>
        public static string HashOf(string sentText) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sentText))).ToLowerInvariant();

        /// <summary>The note's passages in order. Blank text has none.</summary>
        public static IReadOnlyList<Passage> Cut(string noteId, string title, string text)
        {
            if (text.Length > MaxNoteChars) text = text.Substring(0, MaxNoteChars);
            text = WithoutSecrets(text);
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            var passages = new List<Passage>();
            int first = -1, last = -1;
            string heading = "";

            // A passage's body is the note's own lines first..last, blank lines included.
            string Span(int from, int to) => string.Join("\n", lines, from, to - from + 1);

            void Emit()
            {
                if (first >= 0)
                {
                    string body = Span(first, last);
                    if (body.Trim().Length > 0) passages.Add(Make(noteId, title, heading, first, last, lines, body));
                }
                first = -1;
            }

            foreach (var block in Blocks(lines))
            {
                if (block.StartsSection) Emit();

                if (Span(block.First, block.Last).Length > MaxChars)
                {
                    Emit();
                    foreach (var piece in Pieces(lines, block.First, block.Last))
                        passages.Add(Make(noteId, title, block.Heading, piece.First, piece.Last, lines, piece.Text));
                    continue;
                }

                if (first >= 0 && Span(first, block.Last).Length > TargetChars) Emit();
                if (first < 0)
                {
                    first = block.First;
                    heading = block.Heading;
                }
                last = block.Last;
            }
            Emit();
            return passages;
        }

        private static Passage Make(string noteId, string title, string heading, int first, int last, string[] lines, string body)
        {
            string sent = (heading.Length > 0 ? title + HeadingSeparator + heading : title) + "\n\n" + body;
            // Trim a bounded prefix only: a 2 MB line is cut into hundreds of pieces that all start on it.
            string line = lines[first];
            if (line.Length > MaxFirstLineChars * 4) line = line.Substring(0, MaxFirstLineChars * 4);
            string firstLine = line.Trim();
            if (firstLine.Length > MaxFirstLineChars) firstLine = firstLine.Substring(0, MaxFirstLineChars);
            return new Passage(noteId, title, heading, first + 1, last + 1, firstLine, body, sent, HashOf(sent));
        }

        private readonly record struct Block(int First, int Last, string Heading, bool StartsSection);

        /// <summary>Paragraphs, headings (each starting a section) and fenced blocks, 0-based lines.</summary>
        private static List<Block> Blocks(string[] lines)
        {
            var blocks = new List<Block>();
            var headings = new List<string>();
            int start = -1;

            string Path() => string.Join(HeadingSeparator, headings.Where(h => h.Length > 0));

            void Flush(int end)
            {
                if (start >= 0) blocks.Add(new Block(start, end, Path(), false));
                start = -1;
            }

            int i = 0;
            while (i < lines.Length)
            {
                string line = lines[i];
                if (FenceOf(line) is string fence)
                {
                    Flush(i - 1);
                    int end = i + 1;
                    while (end < lines.Length && !lines[end].TrimStart().StartsWith(fence, StringComparison.Ordinal)) end++;
                    if (end >= lines.Length) end = lines.Length - 1;
                    blocks.Add(new Block(i, end, Path(), false));
                    i = end + 1;
                    continue;
                }

                var match = Heading.Match(line);
                if (match.Success)
                {
                    Flush(i - 1);
                    int level = match.Groups[1].Length;
                    while (headings.Count >= level) headings.RemoveAt(headings.Count - 1);
                    while (headings.Count < level - 1) headings.Add("");
                    headings.Add(match.Groups[2].Value.Trim());
                    blocks.Add(new Block(i, i, Path(), true));
                    i++;
                    continue;
                }

                if (line.Trim().Length == 0) Flush(i - 1);
                else if (start < 0) start = i;
                i++;
            }
            Flush(lines.Length - 1);
            return blocks;
        }

        /// <summary>The fence that opens a code block on this line (``` or ~~~, three or more), or null.</summary>
        private static string? FenceOf(string line)
        {
            string t = line.TrimStart();
            if (t.Length < 3 || (t[0] != '`' && t[0] != '~')) return null;
            int n = 0;
            while (n < t.Length && t[n] == t[0]) n++;
            return n >= 3 ? t.Substring(0, n) : null;
        }

        /// <summary>Lines first..last cut at line boundaries into pieces of at most <see cref="MaxChars"/>.</summary>
        private static IEnumerable<(int First, int Last, string Text)> Pieces(string[] lines, int first, int last)
        {
            var text = new StringBuilder();
            int pieceFirst = first;
            for (int i = first; i <= last; i++)
            {
                string line = lines[i];
                if (line.Length > MaxChars)
                {
                    if (text.Length > 0) { yield return (pieceFirst, i - 1, text.ToString()); text.Clear(); }
                    for (int at = 0; at < line.Length; at += MaxChars)
                        yield return (i, i, line.Substring(at, Math.Min(MaxChars, line.Length - at)));
                    continue;
                }
                if (text.Length > 0 && text.Length + 1 + line.Length > MaxChars)
                {
                    yield return (pieceFirst, i - 1, text.ToString());
                    text.Clear();
                }
                if (text.Length == 0) pieceFirst = i;
                else text.Append('\n');
                text.Append(line);
            }
            if (text.ToString().Trim().Length > 0) yield return (pieceFirst, last, text.ToString());
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~SearchPassagesTests"`
Expected: all pass. If `A_huge_single_line…` is slow (> 2 s), the cause is building `blockText` for the 2 MB line: that is expected to be fast; profile before changing the algorithm.

- [ ] **Step 6: Commit**

```bash
git add Services/Pad/Search/Passage.cs Services/Pad/Search/NotePassages.cs tests/Kil0bitSystemMonitor.Tests/SearchPassagesTests.cs
git commit -m "feat(pad-search): cut notes into passages at headings and blank lines, fences whole, credentials removed"
```

---

### Task 2: Tokens and the keyword index

**Files:**
- Create: `Services/Pad/Search/SearchTokens.cs`
- Create: `Services/Pad/Search/KeywordIndex.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/SearchKeywordTests.cs`

**Interfaces:**
- Consumes: `Passage`, `NotePassages.Cut` (Task 1).
- Produces:
  - `public static class SearchTokens { const int MaxTokenChars = 128; static List<string> Of(string text); static string? LastWordPrefix(string query); static bool IsPieceScript(char c); }`
  - `public readonly record struct KeywordHit(Passage Passage, double Score);`
  - `public sealed class KeywordIndex { void Set(string noteId, IReadOnlyList<Passage> passages); void Remove(string noteId); IReadOnlyList<KeywordHit> Search(string query, int limit); IReadOnlyList<Passage> AllPassages(); IReadOnlyList<Passage> PassagesOf(string noteId); IReadOnlyCollection<string> NoteIds(); int PassageCount { get; } int NoteCount { get; } }` — thread-safe (one lock).

- [ ] **Step 1: Write the failing tests**

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~SearchKeywordTests"`
Expected: build error, `SearchTokens` does not exist.

- [ ] **Step 3: Write `SearchTokens.cs`**

```csharp
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>
    /// Keyword tokens (spec 3.2), lower-cased with the invariant culture. A run of letters and
    /// digits joined by <c>. - _ : /</c> is one token and, when joined, each part is a token too, so
    /// <c>ERR-1042</c> and <c>10.0.0.1</c> match whole and by part. Thai and CJK have no spaces
    /// between words, so their runs become overlapping two-character pieces, found wherever the
    /// letters appear without a dictionary.
    /// </summary>
    public static class SearchTokens
    {
        public const int MaxTokenChars = 128;

        /// <summary>Thai, kana, CJK ideographs and Hangul: indexed as two-character pieces.</summary>
        public static bool IsPieceScript(char c) =>
            (c >= '฀' && c <= '๿') || (c >= '぀' && c <= 'ヿ') ||
            (c >= '㐀' && c <= '䶿') || (c >= '一' && c <= '鿿') ||
            (c >= '가' && c <= '힯');

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) && !IsPieceScript(c);

        private static bool IsJoiner(char c) => c is '.' or '-' or '_' or ':' or '/';

        /// <summary>The tokens of <paramref name="text"/> in order, repeats kept.</summary>
        public static List<string> Of(string text)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (IsPieceScript(c))
                {
                    int start = i;
                    while (i < text.Length && IsPieceScript(text[i])) i++;
                    if (i - start == 1) tokens.Add(text.Substring(start, 1));
                    else for (int k = start; k < i - 1; k++) tokens.Add(text.Substring(k, 2));
                    continue;
                }
                if (IsWordChar(c))
                {
                    int start = i, partStart = i;
                    var parts = new List<string>();
                    while (i < text.Length)
                    {
                        if (IsWordChar(text[i])) { i++; continue; }
                        if (IsJoiner(text[i]) && i + 1 < text.Length && IsWordChar(text[i + 1]))
                        {
                            parts.Add(text.Substring(partStart, i - partStart));
                            i++;
                            partStart = i;
                            continue;
                        }
                        break;
                    }
                    parts.Add(text.Substring(partStart, i - partStart));
                    Add(tokens, text.Substring(start, i - start));
                    if (parts.Count > 1) foreach (string part in parts) Add(tokens, part);
                    continue;
                }
                i++;
            }
            return tokens;
        }

        private static void Add(List<string> tokens, string token)
        {
            if (token.Length <= MaxTokenChars) tokens.Add(token.ToLowerInvariant());
        }

        /// <summary>
        /// The word still being typed at the end of the query (two characters or more, letters and
        /// digits, no trailing space), lower-cased; null otherwise.
        /// </summary>
        public static string? LastWordPrefix(string query)
        {
            int end = query.Length;
            int start = end;
            while (start > 0 && IsWordChar(query[start - 1])) start--;
            if (end - start < 2) return null;
            return query.Substring(start, end - start).ToLowerInvariant();
        }
    }
}
```

- [ ] **Step 4: Write `KeywordIndex.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>A passage and its BM25 score for one query.</summary>
    public readonly record struct KeywordHit(Passage Passage, double Score);

    /// <summary>
    /// BM25 over passages (spec 3.2), in memory, rebuilt from the notes at start and replaced one
    /// note at a time. Any query token may match (OR). The query's last word, while still being
    /// typed, also matches as a prefix: at most <see cref="MaxPrefixExpansions"/> indexed words, the
    /// best of them counted once per passage. Thread-safe: every member takes one lock.
    /// </summary>
    public sealed class KeywordIndex
    {
        public const double K1 = 1.2;
        public const double B = 0.75;
        public const int MaxPrefixExpansions = 50;

        private sealed class Entry
        {
            public required Passage Passage { get; init; }
            public required Dictionary<string, int> Counts { get; init; }
            public required int Length { get; init; }
        }

        private readonly object _gate = new();
        private readonly Dictionary<string, List<Entry>> _byNote = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
        private long _totalLength;
        private int _count;

        /// <summary>Passages indexed.</summary>
        public int PassageCount { get { lock (_gate) return _count; } }

        /// <summary>Notes with at least one passage.</summary>
        public int NoteCount { get { lock (_gate) return _byNote.Count; } }

        /// <summary>Replaces the note's passages; an empty list removes the note.</summary>
        public void Set(string noteId, IReadOnlyList<Passage> passages)
        {
            var entries = new List<Entry>(passages.Count);
            foreach (var passage in passages)
            {
                var tokens = SearchTokens.Of(passage.SentText);
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (string token in tokens) counts[token] = counts.TryGetValue(token, out int n) ? n + 1 : 1;
                entries.Add(new Entry { Passage = passage, Counts = counts, Length = tokens.Count });
            }

            lock (_gate)
            {
                RemoveLocked(noteId);
                if (entries.Count == 0) return;
                foreach (var entry in entries)
                {
                    foreach (string term in entry.Counts.Keys)
                        _documentFrequency[term] = _documentFrequency.TryGetValue(term, out int df) ? df + 1 : 1;
                    _totalLength += entry.Length;
                    _count++;
                }
                _byNote[noteId] = entries;
            }
        }

        /// <summary>Forgets the note.</summary>
        public void Remove(string noteId)
        {
            lock (_gate) RemoveLocked(noteId);
        }

        private void RemoveLocked(string noteId)
        {
            if (!_byNote.Remove(noteId, out var entries)) return;
            foreach (var entry in entries)
            {
                foreach (string term in entry.Counts.Keys)
                {
                    int df = _documentFrequency[term] - 1;
                    if (df == 0) _documentFrequency.Remove(term);
                    else _documentFrequency[term] = df;
                }
                _totalLength -= entry.Length;
                _count--;
            }
        }

        /// <summary>Every passage, note by note.</summary>
        public IReadOnlyList<Passage> AllPassages()
        {
            lock (_gate) return _byNote.Values.SelectMany(list => list.Select(e => e.Passage)).ToList();
        }

        /// <summary>The note's passages, or none.</summary>
        public IReadOnlyList<Passage> PassagesOf(string noteId)
        {
            lock (_gate)
                return _byNote.TryGetValue(noteId, out var list) ? list.Select(e => e.Passage).ToList() : Array.Empty<Passage>();
        }

        /// <summary>The indexed notes.</summary>
        public IReadOnlyCollection<string> NoteIds()
        {
            lock (_gate) return _byNote.Keys.ToList();
        }

        /// <summary>Best first; ties by note and line so results are stable.</summary>
        public IReadOnlyList<KeywordHit> Search(string query, int limit)
        {
            var terms = SearchTokens.Of(query).Distinct(StringComparer.Ordinal).ToList();
            string? prefix = SearchTokens.LastWordPrefix(query);
            if (terms.Count == 0) return Array.Empty<KeywordHit>();

            lock (_gate)
            {
                if (_count == 0) return Array.Empty<KeywordHit>();
                double average = (double)_totalLength / _count;

                List<string>? expansions = null;
                if (prefix != null)
                {
                    expansions = _documentFrequency.Keys
                        .Where(t => t.Length > prefix.Length && t.StartsWith(prefix, StringComparison.Ordinal))
                        .OrderByDescending(t => _documentFrequency[t])
                        .ThenBy(t => t, StringComparer.Ordinal)
                        .Take(MaxPrefixExpansions)
                        .ToList();
                }

                var hits = new List<KeywordHit>();
                foreach (var list in _byNote.Values)
                {
                    foreach (var entry in list)
                    {
                        double score = 0;
                        foreach (string term in terms) score += Weight(entry, term, average);
                        if (expansions != null)
                        {
                            double best = 0;
                            foreach (string term in expansions) best = Math.Max(best, Weight(entry, term, average));
                            score += best;
                        }
                        if (score > 0) hits.Add(new KeywordHit(entry.Passage, score));
                    }
                }

                return hits
                    .OrderByDescending(h => h.Score)
                    .ThenBy(h => h.Passage.NoteId, StringComparer.Ordinal)
                    .ThenBy(h => h.Passage.FirstLine)
                    .Take(limit)
                    .ToList();
            }
        }

        private double Weight(Entry entry, string term, double average)
        {
            if (!entry.Counts.TryGetValue(term, out int tf)) return 0;
            int df = _documentFrequency[term];
            double idf = Math.Log(1 + (_count - df + 0.5) / (df + 0.5));
            return idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * entry.Length / average));
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~SearchKeywordTests"`
Expected: all pass. Note: `Assert.Equal(string[], string[])` is exact; never compare a `string[]` with a `List<string>` (xUnit 2.9.2 then ignores whitespace differences) — always `.ToArray()` the actual side as above.

- [ ] **Step 6: Commit**

```bash
git add Services/Pad/Search/SearchTokens.cs Services/Pad/Search/KeywordIndex.cs tests/Kil0bitSystemMonitor.Tests/SearchKeywordTests.cs
git commit -m "feat(pad-search): keyword tokens (compounds, Thai/CJK pieces) and a BM25 index with prefix matching"
```

---

### Task 3: Embedding and rerank clients

**Files:**
- Create: `Services/Pad/Search/SearchServer.cs`
- Create: `Services/Pad/Search/SearchHttp.cs`
- Create: `Services/Pad/Search/EmbeddingClient.cs`
- Create: `Services/Pad/Search/RerankClient.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/SearchClientsTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `public enum SearchFailure { None, Unreachable, KeyRefused, RateLimited, TimedOut, Rejected, ServerError, BadAnswer }`
  - `public sealed record SearchServer(string BaseUrl, string Model, string? Key);`
  - `public sealed record EmbeddingResult(IReadOnlyList<float[]>? Vectors, SearchFailure Failure, int? Status);`
  - `public sealed record RerankResult(IReadOnlyList<RerankScore>? Ranked, SearchFailure Failure, int? Status);` and `public readonly record struct RerankScore(int Index, double Score);` (best first)
  - `public interface IEmbedder { Task<EmbeddingResult> EmbedAsync(SearchServer server, IReadOnlyList<string> texts, TimeSpan timeout, CancellationToken cancel); }`
  - `public interface IReranker { Task<RerankResult> RerankAsync(SearchServer server, string query, IReadOnlyList<string> documents, int topN, TimeSpan timeout, CancellationToken cancel); }`
  - `public sealed class EmbeddingClient : IEmbedder, IDisposable` and `public sealed class RerankClient : IReranker, IDisposable`, both `(HttpMessageHandler? handler = null)`.
  - `public static class SearchFailureText { static string Describe(SearchFailure failure, int? status); }` — e.g. "could not be reached".
  - Caller cancellation throws `OperationCanceledException`; every other failure is a result, never an exception.

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Pad.Search;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class SearchClientsTests
    {
        /// <summary>Answers every request with a function of it; records what was sent.</summary>
        private sealed class FakeServer : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> _answer;
            public FakeServer(Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> answer) => _answer = answer;
            public FakeServer(HttpStatusCode status, string body) : this((_, _, _) => Task.FromResult(Reply(status, body))) { }
            public List<(HttpRequestMessage Request, string Body)> Seen { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
            {
                string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancel);
                Seen.Add((request, body));
                return await _answer(request, body, cancel);   // HttpClient waits for the handler, so a fake must honour the token
            }
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static readonly SearchServer Server = new("http://gpu:8000/v1", "bge-m3", "sk-test");
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task Embeddings_are_asked_in_the_OpenAI_format_and_placed_by_index()
        {
            var fake = new FakeServer(HttpStatusCode.OK,
                "{\"data\":[{\"index\":1,\"embedding\":[0.5,0.5]},{\"index\":0,\"embedding\":[1,0]}]}");
            using var client = new EmbeddingClient(fake);

            var result = await client.EmbedAsync(Server, new[] { "first", "second" }, Wait, default);

            Assert.Equal(SearchFailure.None, result.Failure);
            Assert.Equal(new[] { 1f, 0f }, result.Vectors![0]);
            Assert.Equal(new[] { 0.5f, 0.5f }, result.Vectors![1]);
            var (request, body) = Assert.Single(fake.Seen);
            Assert.Equal("http://gpu:8000/v1/embeddings", request.RequestUri!.ToString());
            Assert.Equal("Bearer sk-test", request.Headers.Authorization!.ToString());
            using var json = JsonDocument.Parse(body);
            Assert.Equal("bge-m3", json.RootElement.GetProperty("model").GetString());
            Assert.Equal(new[] { "first", "second" }, json.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToArray());
        }

        [Fact]
        public async Task No_model_and_no_key_send_neither()
        {
            var fake = new FakeServer(HttpStatusCode.OK, "{\"data\":[{\"index\":0,\"embedding\":[1]}]}");
            using var client = new EmbeddingClient(fake);

            await client.EmbedAsync(new SearchServer("http://gpu:8000/v1/", "", null), new[] { "a" }, Wait, default);

            var (request, body) = Assert.Single(fake.Seen);
            Assert.Equal("http://gpu:8000/v1/embeddings", request.RequestUri!.ToString());
            Assert.Null(request.Headers.Authorization);
            using var json = JsonDocument.Parse(body);
            Assert.False(json.RootElement.TryGetProperty("model", out _));
        }

        [Theory]
        [InlineData("{\"data\":[{\"index\":0,\"embedding\":[1,2]}]}")]                                          // one vector for two texts
        [InlineData("{\"data\":[{\"index\":0,\"embedding\":[1,2]},{\"index\":1,\"embedding\":[1]}]}")]        // mixed dimensions
        [InlineData("{\"data\":[{\"index\":0,\"embedding\":[1]},{\"index\":0,\"embedding\":[1]}]}")]          // index repeated
        [InlineData("{\"data\":[{\"index\":0,\"embedding\":[]},{\"index\":1,\"embedding\":[]}]}")]            // empty vectors
        [InlineData("{\"error\":\"nope\"}")]
        [InlineData("not json")]
        public async Task An_answer_that_does_not_fit_is_a_bad_answer(string body)
        {
            using var client = new EmbeddingClient(new FakeServer(HttpStatusCode.OK, body));
            var result = await client.EmbedAsync(Server, new[] { "a", "b" }, Wait, default);
            Assert.Equal(SearchFailure.BadAnswer, result.Failure);
            Assert.Null(result.Vectors);
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized, SearchFailure.KeyRefused)]
        [InlineData(HttpStatusCode.Forbidden, SearchFailure.KeyRefused)]
        [InlineData(HttpStatusCode.TooManyRequests, SearchFailure.RateLimited)]
        [InlineData(HttpStatusCode.NotFound, SearchFailure.Rejected)]
        [InlineData(HttpStatusCode.UnprocessableEntity, SearchFailure.Rejected)]
        [InlineData(HttpStatusCode.InternalServerError, SearchFailure.ServerError)]
        [InlineData(HttpStatusCode.BadGateway, SearchFailure.ServerError)]
        [InlineData(HttpStatusCode.Redirect, SearchFailure.Unreachable)]
        public async Task Status_codes_are_classified(HttpStatusCode status, SearchFailure expected)
        {
            using var client = new EmbeddingClient(new FakeServer(status, "{}"));
            var result = await client.EmbedAsync(Server, new[] { "a" }, Wait, default);
            Assert.Equal(expected, result.Failure);
            Assert.Equal((int)status, result.Status);
        }

        [Fact]
        public async Task A_network_error_is_unreachable()
        {
            using var client = new EmbeddingClient(new FakeServer((_, _, _) => throw new HttpRequestException("down")));
            Assert.Equal(SearchFailure.Unreachable, (await client.EmbedAsync(Server, new[] { "a" }, Wait, default)).Failure);
        }

        [Fact]
        public async Task A_server_that_never_answers_times_out()
        {
            using var client = new EmbeddingClient(new FakeServer(async (_, _, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return Reply(HttpStatusCode.OK, "{}");
            }));
            var result = await client.EmbedAsync(Server, new[] { "a" }, TimeSpan.FromMilliseconds(100), default);
            Assert.Equal(SearchFailure.TimedOut, result.Failure);
        }

        [Fact]
        public async Task The_caller_cancelling_throws()
        {
            using var cancel = new CancellationTokenSource();
            using var client = new EmbeddingClient(new FakeServer(async (_, _, token) =>
            {
                cancel.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                return Reply(HttpStatusCode.OK, "{}");
            }));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EmbedAsync(Server, new[] { "a" }, Wait, cancel.Token));
        }

        [Fact]
        public async Task An_oversized_answer_is_refused()
        {
            string huge = "{\"data\":[{\"index\":0,\"embedding\":[" + string.Join(",", Enumerable.Repeat("0.123456789", 3_500_000)) + "]}]}";
            using var client = new EmbeddingClient(new FakeServer(HttpStatusCode.OK, huge));
            Assert.Equal(SearchFailure.BadAnswer, (await client.EmbedAsync(Server, new[] { "a" }, Wait, default)).Failure);
        }

        [Fact]
        public async Task Rerank_is_asked_in_the_Cohere_format_and_answers_best_first()
        {
            var fake = new FakeServer(HttpStatusCode.OK,
                "{\"results\":[{\"index\":2,\"relevance_score\":0.9},{\"index\":0,\"relevance_score\":0.2},{\"index\":1,\"relevance_score\":0.5}]}");
            using var client = new RerankClient(fake);

            var result = await client.RerankAsync(Server, "vpn", new[] { "a", "b", "c" }, 3, Wait, default);

            Assert.Equal(SearchFailure.None, result.Failure);
            Assert.Equal(new[] { 2, 1, 0 }, result.Ranked!.Select(r => r.Index).ToArray());
            var (request, body) = Assert.Single(fake.Seen);
            Assert.Equal("http://gpu:8000/v1/rerank", request.RequestUri!.ToString());
            using var json = JsonDocument.Parse(body);
            Assert.Equal("vpn", json.RootElement.GetProperty("query").GetString());
            Assert.Equal(3, json.RootElement.GetProperty("documents").GetArrayLength());
            Assert.Equal(3, json.RootElement.GetProperty("top_n").GetInt32());
            Assert.Equal("bge-m3", json.RootElement.GetProperty("model").GetString());
        }

        [Theory]
        [InlineData("{\"results\":[{\"index\":5,\"relevance_score\":0.9}]}")]       // index out of range
        [InlineData("{\"results\":[{\"index\":0}]}")]                               // no score
        [InlineData("{\"results\":[{\"index\":0,\"relevance_score\":1},{\"index\":0,\"relevance_score\":1}]}")]
        [InlineData("{}")]
        public async Task A_rerank_answer_that_does_not_fit_is_a_bad_answer(string body)
        {
            using var client = new RerankClient(new FakeServer(HttpStatusCode.OK, body));
            Assert.Equal(SearchFailure.BadAnswer, (await client.RerankAsync(Server, "q", new[] { "a", "b" }, 2, Wait, default)).Failure);
        }

        [Fact]
        public void Failures_read_as_plain_words()
        {
            Assert.Equal("could not be reached", SearchFailureText.Describe(SearchFailure.Unreachable, null));
            Assert.Equal("refused the key", SearchFailureText.Describe(SearchFailure.KeyRefused, 401));
            Assert.Equal("is busy (too many requests)", SearchFailureText.Describe(SearchFailure.RateLimited, 429));
            Assert.Equal("timed out", SearchFailureText.Describe(SearchFailure.TimedOut, null));
            Assert.Equal("refused the request (HTTP 404)", SearchFailureText.Describe(SearchFailure.Rejected, 404));
            Assert.Equal("failed (HTTP 500)", SearchFailureText.Describe(SearchFailure.ServerError, 500));
            Assert.Equal("gave an answer MicaPad could not read", SearchFailureText.Describe(SearchFailure.BadAnswer, 200));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~SearchClientsTests"`
Expected: build error, `EmbeddingClient` does not exist.

- [ ] **Step 3: Write `SearchServer.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>Why a call to the embedding server or the reranker gave no answer (spec 3.3).</summary>
    public enum SearchFailure { None, Unreachable, KeyRefused, RateLimited, TimedOut, Rejected, ServerError, BadAnswer }

    /// <summary>A server from Settings: its base address (MicaPad adds the route), the model (may be empty) and the key (null when none is saved).</summary>
    public sealed record SearchServer(string BaseUrl, string Model, string? Key);

    /// <summary>One vector per text, in the texts' order, or the failure.</summary>
    public sealed record EmbeddingResult(IReadOnlyList<float[]>? Vectors, SearchFailure Failure, int? Status);

    /// <summary>A document's index in the request and its relevance.</summary>
    public readonly record struct RerankScore(int Index, double Score);

    /// <summary>The documents best first, or the failure.</summary>
    public sealed record RerankResult(IReadOnlyList<RerankScore>? Ranked, SearchFailure Failure, int? Status);

    /// <summary>Turns texts into vectors. Throws only <see cref="OperationCanceledException"/>, when the caller cancels.</summary>
    public interface IEmbedder
    {
        Task<EmbeddingResult> EmbedAsync(SearchServer server, IReadOnlyList<string> texts, TimeSpan timeout, CancellationToken cancel);
    }

    /// <summary>Orders documents by relevance to a query. Throws only <see cref="OperationCanceledException"/>, when the caller cancels.</summary>
    public interface IReranker
    {
        Task<RerankResult> RerankAsync(SearchServer server, string query, IReadOnlyList<string> documents, int topN, TimeSpan timeout, CancellationToken cancel);
    }

    /// <summary>How the status line and Settings name a failure, after "the embedding server" or "the reranker".</summary>
    public static class SearchFailureText
    {
        public static string Describe(SearchFailure failure, int? status) => failure switch
        {
            SearchFailure.Unreachable => "could not be reached",
            SearchFailure.KeyRefused => "refused the key",
            SearchFailure.RateLimited => "is busy (too many requests)",
            SearchFailure.TimedOut => "timed out",
            SearchFailure.Rejected => "refused the request (HTTP " + status + ")",
            SearchFailure.ServerError => "failed (HTTP " + status + ")",
            SearchFailure.BadAnswer => "gave an answer MicaPad could not read",
            _ => "answered",
        };
    }
}
```

- [ ] **Step 4: Write `SearchHttp.cs`**

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>
    /// The one POST both clients make (spec 3.3): JSON in, JSON out, the system proxy, no
    /// redirects, answers over <see cref="MaxAnswerBytes"/> refused, a per-call timeout. Network
    /// problems become a <see cref="SearchFailure"/>; only the caller's cancellation throws.
    /// </summary>
    internal static class SearchHttp
    {
        public const int MaxAnswerBytes = 32 * 1024 * 1024;

        public static HttpClient CreateClient(HttpMessageHandler? handler)
        {
            var http = handler == null
                ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
                : new HttpClient(handler, disposeHandler: false);
            http.Timeout = Timeout.InfiniteTimeSpan;   // each call has its own
            return http;
        }

        public static SearchFailure Classify(int status) => status switch
        {
            401 or 403 => SearchFailure.KeyRefused,
            429 => SearchFailure.RateLimited,
            >= 300 and < 400 => SearchFailure.Unreachable,
            >= 500 => SearchFailure.ServerError,
            _ => SearchFailure.Rejected,
        };

        /// <summary>The parsed answer (the caller disposes it), or null with the failure.</summary>
        public static async Task<(JsonDocument? Json, SearchFailure Failure, int? Status)> PostAsync(
            HttpClient http, SearchServer server, string route, object body, TimeSpan timeout, CancellationToken cancel)
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timer.CancelAfter(timeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, server.BaseUrl.TrimEnd('/') + route)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrEmpty(server.Key))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Key);

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timer.Token).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode) return (null, Classify(status), status);

                using var stream = await response.Content.ReadAsStreamAsync(timer.Token).ConfigureAwait(false);
                using var answer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, timer.Token).ConfigureAwait(false)) > 0)
                {
                    if (answer.Length + read > MaxAnswerBytes) return (null, SearchFailure.BadAnswer, status);
                    answer.Write(chunk, 0, read);
                }

                try { return (JsonDocument.Parse(answer.ToArray()), SearchFailure.None, status); }
                catch (JsonException) { return (null, SearchFailure.BadAnswer, status); }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return (null, SearchFailure.TimedOut, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                return (null, SearchFailure.Unreachable, null);
            }
        }
    }
}
```

- [ ] **Step 5: Write `EmbeddingClient.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>
    /// <c>POST {server}/embeddings</c> in the OpenAI format: <c>{"model", "input": [texts]}</c>
    /// (no <c>model</c> when Settings has none), answered with <c>data[].embedding</c> placed by
    /// <c>data[].index</c>. A count or dimension that does not match is a bad answer.
    /// </summary>
    public sealed class EmbeddingClient : IEmbedder, IDisposable
    {
        private readonly HttpClient _http;

        /// <param name="handler">Tests pass a fake server; null uses the system's network settings without redirects.</param>
        public EmbeddingClient(HttpMessageHandler? handler = null) => _http = SearchHttp.CreateClient(handler);

        public async Task<EmbeddingResult> EmbedAsync(SearchServer server, IReadOnlyList<string> texts, TimeSpan timeout, CancellationToken cancel)
        {
            var body = new Dictionary<string, object> { ["input"] = texts };
            if (server.Model.Length > 0) body["model"] = server.Model;

            var (json, failure, status) = await SearchHttp.PostAsync(_http, server, "/embeddings", body, timeout, cancel).ConfigureAwait(false);
            if (json == null) return new EmbeddingResult(null, failure, status);
            using (json) return Parse(json.RootElement, texts.Count, status);
        }

        private static EmbeddingResult Parse(JsonElement root, int count, int? status)
        {
            var bad = new EmbeddingResult(null, SearchFailure.BadAnswer, status);
            try
            {
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != count)
                    return bad;

                var vectors = new float[count][];
                int position = 0, dimension = -1;
                foreach (var item in data.EnumerateArray())
                {
                    int index = item.TryGetProperty("index", out var ix) && ix.TryGetInt32(out int i) ? i : position;
                    position++;
                    if (index < 0 || index >= count || vectors[index] != null) return bad;
                    if (!item.TryGetProperty("embedding", out var embedding) || embedding.ValueKind != JsonValueKind.Array) return bad;

                    var vector = new float[embedding.GetArrayLength()];
                    int k = 0;
                    foreach (var x in embedding.EnumerateArray())
                    {
                        if (x.ValueKind != JsonValueKind.Number) return bad;
                        vector[k++] = x.GetSingle();
                    }
                    if (vector.Length == 0 || (dimension >= 0 && vector.Length != dimension)) return bad;
                    dimension = vector.Length;
                    vectors[index] = vector;
                }
                return new EmbeddingResult(vectors, SearchFailure.None, status);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                return bad;
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
```

- [ ] **Step 6: Write `RerankClient.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>
    /// <c>POST {server}/rerank</c> in the Cohere/Jina format: <c>{"model", "query", "documents",
    /// "top_n"}</c>, answered with <c>results[]</c> of <c>index</c> and <c>relevance_score</c>,
    /// returned best first.
    /// </summary>
    public sealed class RerankClient : IReranker, IDisposable
    {
        private readonly HttpClient _http;

        /// <param name="handler">Tests pass a fake server; null uses the system's network settings without redirects.</param>
        public RerankClient(HttpMessageHandler? handler = null) => _http = SearchHttp.CreateClient(handler);

        public async Task<RerankResult> RerankAsync(SearchServer server, string query, IReadOnlyList<string> documents, int topN, TimeSpan timeout, CancellationToken cancel)
        {
            var body = new Dictionary<string, object> { ["query"] = query, ["documents"] = documents, ["top_n"] = topN };
            if (server.Model.Length > 0) body["model"] = server.Model;

            var (json, failure, status) = await SearchHttp.PostAsync(_http, server, "/rerank", body, timeout, cancel).ConfigureAwait(false);
            if (json == null) return new RerankResult(null, failure, status);
            using (json) return Parse(json.RootElement, documents.Count, status);
        }

        private static RerankResult Parse(JsonElement root, int count, int? status)
        {
            var bad = new RerankResult(null, SearchFailure.BadAnswer, status);
            try
            {
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                    return bad;

                var seen = new HashSet<int>();
                var scores = new List<RerankScore>();
                foreach (var item in results.EnumerateArray())
                {
                    if (!item.TryGetProperty("index", out var ix) || !ix.TryGetInt32(out int index)) return bad;
                    if (!item.TryGetProperty("relevance_score", out var sc) || sc.ValueKind != JsonValueKind.Number) return bad;
                    if (index < 0 || index >= count || !seen.Add(index)) return bad;
                    scores.Add(new RerankScore(index, sc.GetDouble()));
                }
                return new RerankResult(scores.OrderByDescending(s => s.Score).ThenBy(s => s.Index).ToList(), SearchFailure.None, status);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                return bad;
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~SearchClientsTests"`
Expected: all pass.

- [ ] **Step 8: Commit**

```bash
git add Services/Pad/Search/SearchServer.cs Services/Pad/Search/SearchHttp.cs Services/Pad/Search/EmbeddingClient.cs Services/Pad/Search/RerankClient.cs tests/Kil0bitSystemMonitor.Tests/SearchClientsTests.cs
git commit -m "feat(pad-search): embedding (OpenAI format) and rerank (Cohere/Jina format) clients with failure classes"
```

---

### Task 4: The encrypted vector store

**Files:**
- Create: `Services/Pad/Search/VectorStore.cs`
- Modify: `Services/Pad/NoteStore.Encryption.cs` (add `EncryptBytes` / `TryDecryptBytes` next to `WriteData`)
- Test: `tests/Kil0bitSystemMonitor.Tests/SearchVectorStoreTests.cs`

**Interfaces:**
- Consumes: `AtomicFile.Write/ReadBytes` (`Services/Pad/AtomicFile.cs`), `StoreCipher` through `NoteStore`.
- Produces:
  - `NoteStore.EncryptBytes(byte[] plain) : byte[]` and `NoteStore.TryDecryptBytes(byte[] data, out byte[] plain) : bool`.
  - `public delegate bool TryUnprotect(byte[] data, out byte[] plain);`
  - `public enum PutResult { Stored, Full, WrongDimension }`
  - `public readonly record struct VectorHit(string Hash, double Score);`
  - `public sealed class VectorStore { const int Capacity = 20000; const string FileName = "vectors.bin"; VectorStore(string folder, Func<byte[], byte[]> protect, TryUnprotect unprotect, Action<string>? warn = null, int capacity = Capacity); string FilePath; string Fingerprint; int Dimension; int Count; bool Load(string fingerprint); bool Has(string hash); PutResult Put(string hash, float[] vector); int Keep(IReadOnlySet<string> inUse); bool Save(); void Delete(); IReadOnlyList<VectorHit> Nearest(float[] query, IEnumerable<string> candidates, int limit); }`

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Search;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class SearchVectorStoreTests : IDisposable
    {
        private readonly PadTempDir _dir = new();
        private readonly List<string> _warnings = new();

        public void Dispose() => _dir.Dispose();

        /// <summary>A store whose "encryption" is a byte flip with a marker, so tests can tell protected bytes.</summary>
        private VectorStore NewStore(int capacity = VectorStore.Capacity) => new(
            Path.Combine(_dir.Root, "search"),
            plain => new byte[] { 0xEE }.Concat(plain.Select(b => (byte)~b)).ToArray(),
            (byte[] data, out byte[] plain) =>
            {
                plain = Array.Empty<byte>();
                if (data.Length == 0 || data[0] != 0xEE) return false;
                plain = data.Skip(1).Select(b => (byte)~b).ToArray();
                return true;
            },
            _warnings.Add,
            capacity);

        private static string H(int i) => i.ToString("x64");

        [Fact]
        public void Vectors_survive_a_save_and_load_and_the_file_is_protected()
        {
            var store = NewStore();
            store.Load("http://gpu/v1|bge");
            Assert.Equal(PutResult.Stored, store.Put(H(1), new[] { 3f, 4f }));
            Assert.True(store.Save());

            byte[] raw = File.ReadAllBytes(store.FilePath);
            Assert.Equal(0xEE, raw[0]);

            var again = NewStore();
            Assert.True(again.Load("http://gpu/v1|bge"));
            Assert.True(again.Has(H(1)));
            Assert.Equal(2, again.Dimension);
            var hit = Assert.Single(again.Nearest(new[] { 3f, 4f }, new[] { H(1) }, 5));
            Assert.Equal(1.0, hit.Score, 5);   // stored unit length: cosine of a vector with itself
        }

        [Fact]
        public void Another_fingerprint_starts_empty()
        {
            var store = NewStore();
            store.Load("a|m");
            store.Put(H(1), new[] { 1f });
            store.Save();

            var again = NewStore();
            Assert.False(again.Load("b|m"));
            Assert.Equal(0, again.Count);
        }

        [Fact]
        public void A_damaged_or_foreign_file_starts_empty_with_a_warning()
        {
            var store = NewStore();
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);

            File.WriteAllBytes(store.FilePath, new byte[] { 0xEE, 1, 2, 3 });
            Assert.False(store.Load("a|m"));
            Assert.Equal(0, store.Count);

            File.WriteAllBytes(store.FilePath, new byte[] { 1, 2, 3 });
            Assert.False(store.Load("a|m"));

            Assert.Equal(2, _warnings.Count);
            Assert.All(_warnings, w => Assert.DoesNotContain("\\", w));   // no paths, no contents
        }

        [Fact]
        public void Put_refuses_another_dimension()
        {
            var store = NewStore();
            store.Load("a|m");
            Assert.Equal(PutResult.Stored, store.Put(H(1), new[] { 1f, 0f }));
            Assert.Equal(PutResult.WrongDimension, store.Put(H(2), new[] { 1f, 0f, 0f }));
            Assert.Equal(1, store.Count);
        }

        [Fact]
        public void A_full_store_takes_no_more()
        {
            var store = NewStore(capacity: 2);
            store.Load("a|m");
            store.Put(H(1), new[] { 1f });
            store.Put(H(2), new[] { 1f });
            Assert.Equal(PutResult.Full, store.Put(H(3), new[] { 1f }));
            Assert.Equal(PutResult.Stored, store.Put(H(2), new[] { 0.5f }));   // replacing is not growing
        }

        [Fact]
        public void Keep_drops_unused_vectors()
        {
            var store = NewStore();
            store.Load("a|m");
            store.Put(H(1), new[] { 1f });
            store.Put(H(2), new[] { 1f });

            Assert.Equal(1, store.Keep(new HashSet<string> { H(2) }));
            Assert.False(store.Has(H(1)));
            Assert.True(store.Has(H(2)));
        }

        [Fact]
        public void Nearest_ranks_by_cosine_among_the_candidates_only()
        {
            var store = NewStore();
            store.Load("a|m");
            store.Put(H(1), new[] { 1f, 0f });
            store.Put(H(2), new[] { 0.7f, 0.7f });
            store.Put(H(3), new[] { 0f, 1f });

            var hits = store.Nearest(new[] { 1f, 0.1f }, new[] { H(1), H(2), H(9) }, 5);

            Assert.Equal(new[] { H(1), H(2) }, hits.Select(h => h.Hash).ToArray());
        }

        [Fact]
        public void Delete_removes_the_file_and_the_vectors()
        {
            var store = NewStore();
            store.Load("a|m");
            store.Put(H(1), new[] { 1f });
            store.Save();

            store.Delete();

            Assert.False(File.Exists(store.FilePath));
            Assert.Equal(0, store.Count);
            Assert.Equal(0, store.Dimension);
        }

        [Fact]
        public void Save_writes_only_after_a_change()
        {
            var store = NewStore();
            store.Load("a|m");
            Assert.False(store.Save());
            store.Put(H(1), new[] { 1f });
            Assert.True(store.Save());
            Assert.False(store.Save());
        }

        [Fact]
        public void The_notes_key_round_trips_bytes_and_refuses_plain_ones()
        {
            using var env = new PadTestEnv();
            byte[] secret = { 1, 2, 3, 4 };

            byte[] sealed_ = env.Store.EncryptBytes(secret);

            Assert.True(env.Store.TryDecryptBytes(sealed_, out byte[] plain));
            Assert.Equal(secret, plain);
            Assert.False(env.Store.TryDecryptBytes(secret, out _));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~SearchVectorStoreTests"`
Expected: build error, `VectorStore` does not exist. (Check `PadTempDir` exposes `Root` and `Dispose`; it is in `tests/Kil0bitSystemMonitor.Tests/PadTempDir.cs`. Adapt the field use to its real members if they differ.)

- [ ] **Step 3: Add the byte encryption to `NoteStore.Encryption.cs`** (beside `WriteData`, inside `partial class NoteStore`)

```csharp
        /// <summary>
        /// Encrypts bytes with the notes key, for MicaPad's own files beside the notes (the search
        /// vectors). Readable back only with <see cref="TryDecryptBytes"/> on this Windows account.
        /// </summary>
        public byte[] EncryptBytes(byte[] plain) => _cipher.Encrypt(plain);

        /// <summary>Decrypts bytes from <see cref="EncryptBytes"/>; false for anything else, plain bytes included.</summary>
        public bool TryDecryptBytes(byte[] data, out byte[] plain)
        {
            plain = Array.Empty<byte>();
            return StoreCipher.IsEncrypted(data) && _cipher.TryDecrypt(data, out plain);
        }
```

- [ ] **Step 4: Write `VectorStore.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>Reverses the protection <see cref="VectorStore"/> writes with; false when the bytes are not its own.</summary>
    public delegate bool TryUnprotect(byte[] data, out byte[] plain);

    /// <summary>What <see cref="VectorStore.Put"/> did.</summary>
    public enum PutResult { Stored, Full, WrongDimension }

    /// <summary>A vector's passage hash and its cosine with the query.</summary>
    public readonly record struct VectorHit(string Hash, double Score);

    /// <summary>
    /// Passage vectors by passage hash (spec 3.4), unit length, in memory and in
    /// <c>search\vectors.bin</c> protected with the notes key. The file names the fingerprint
    /// (server and model) and the dimension it was made with; anything else, or a file that cannot
    /// be read, starts empty. Thread-safe: every member takes one lock.
    /// </summary>
    public sealed class VectorStore
    {
        public const int Capacity = 20_000;
        public const string FileName = "vectors.bin";

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MPVX");
        private const int FormatVersion = 1;
        private const int MaxDimension = 16_384;

        private readonly object _gate = new();
        private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);
        private readonly Func<byte[], byte[]> _protect;
        private readonly TryUnprotect _unprotect;
        private readonly Action<string> _warn;
        private readonly int _capacity;
        private bool _dirty;

        /// <param name="folder">Where <see cref="FileName"/> lives; created on the first save.</param>
        public VectorStore(string folder, Func<byte[], byte[]> protect, TryUnprotect unprotect, Action<string>? warn = null, int capacity = Capacity)
        {
            FilePath = Path.Combine(folder, FileName);
            _protect = protect;
            _unprotect = unprotect;
            _warn = warn ?? (_ => { });
            _capacity = capacity;
        }

        public string FilePath { get; }

        /// <summary>The server and model these vectors belong to.</summary>
        public string Fingerprint { get { lock (_gate) return _fingerprint; } }
        private string _fingerprint = "";

        /// <summary>The vectors' length; 0 while there are none.</summary>
        public int Dimension { get { lock (_gate) return _dimension; } }
        private int _dimension;

        public int Count { get { lock (_gate) return _vectors.Count; } }

        /// <summary>
        /// Empties the store for <paramref name="fingerprint"/> and reads the file when it was made
        /// for it. True when vectors were read.
        /// </summary>
        public bool Load(string fingerprint)
        {
            lock (_gate)
            {
                _vectors.Clear();
                _fingerprint = fingerprint;
                _dimension = 0;
                _dirty = false;

                byte[]? data;
                try { data = AtomicFile.ReadBytes(FilePath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _warn("The search vectors could not be read (" + ex.GetType().Name + "); they will be made again.");
                    return false;
                }
                if (data == null) return false;

                if (!_unprotect(data, out byte[] plain))
                {
                    _warn("The search vectors could not be decrypted; they will be made again.");
                    return false;
                }

                try
                {
                    return ReadLocked(plain, fingerprint);
                }
                catch (Exception ex) when (ex is EndOfStreamException or IOException or FormatException or ArgumentException)
                {
                    _vectors.Clear();
                    _dimension = 0;
                    _warn("The search vectors file is damaged (" + ex.GetType().Name + "); they will be made again.");
                    return false;
                }
            }
        }

        private bool ReadLocked(byte[] plain, string fingerprint)
        {
            using var reader = new BinaryReader(new MemoryStream(plain), Encoding.UTF8);
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)) throw new FormatException("magic");
            if (reader.ReadInt32() != FormatVersion) throw new FormatException("version");
            string fileFingerprint = reader.ReadString();
            int dimension = reader.ReadInt32();
            int count = reader.ReadInt32();
            if (fileFingerprint != fingerprint) return false;
            if (dimension <= 0 || dimension > MaxDimension || count < 0 || count > _capacity) throw new FormatException("header");

            for (int i = 0; i < count; i++)
            {
                byte[] hash = reader.ReadBytes(32);
                if (hash.Length != 32) throw new EndOfStreamException();
                var vector = new float[dimension];
                for (int k = 0; k < dimension; k++) vector[k] = reader.ReadSingle();
                _vectors[Convert.ToHexString(hash).ToLowerInvariant()] = vector;
            }
            _dimension = dimension;
            return count > 0;
        }

        public bool Has(string hash)
        {
            lock (_gate) return _vectors.ContainsKey(hash);
        }

        /// <summary>Stores the vector at unit length. Refuses a new hash when full, and any other dimension.</summary>
        public PutResult Put(string hash, float[] vector)
        {
            lock (_gate)
            {
                if (_dimension != 0 && vector.Length != _dimension) return PutResult.WrongDimension;
                if (!_vectors.ContainsKey(hash) && _vectors.Count >= _capacity) return PutResult.Full;
                _vectors[hash] = Normalized(vector);
                _dimension = vector.Length;
                _dirty = true;
                return PutResult.Stored;
            }
        }

        /// <summary>Drops every vector no passage uses; returns how many went.</summary>
        public int Keep(IReadOnlySet<string> inUse)
        {
            lock (_gate)
            {
                var unused = _vectors.Keys.Where(h => !inUse.Contains(h)).ToList();
                foreach (string hash in unused) _vectors.Remove(hash);
                if (unused.Count > 0) _dirty = true;
                if (_vectors.Count == 0 && unused.Count > 0) _dimension = 0;
                return unused.Count;
            }
        }

        /// <summary>Writes the file when something changed since the last save; true when it wrote.</summary>
        public bool Save()
        {
            lock (_gate)
            {
                if (!_dirty) return false;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    AtomicFile.Write(FilePath, _protect(Serialize()));
                    _dirty = false;
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _warn("The search vectors could not be saved (" + ex.GetType().Name + ").");
                    return false;
                }
            }
        }

        private byte[] Serialize()
        {
            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(_fingerprint);
                writer.Write(_dimension);
                writer.Write(_vectors.Count);
                foreach (var pair in _vectors)
                {
                    writer.Write(Convert.FromHexString(pair.Key));
                    foreach (float x in pair.Value) writer.Write(x);
                }
            }
            return buffer.ToArray();
        }

        /// <summary>Forgets every vector and deletes the file (meaning search turned off, model changed, Rebuild).</summary>
        public void Delete()
        {
            lock (_gate)
            {
                _vectors.Clear();
                _dimension = 0;
                _dirty = false;
                try
                {
                    File.Delete(FilePath);
                    File.Delete(FilePath + ".ready");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _warn("The search vectors file could not be deleted (" + ex.GetType().Name + ").");
                }
            }
        }

        /// <summary>The <paramref name="limit"/> candidates closest to the query by cosine; candidates without a vector are skipped.</summary>
        public IReadOnlyList<VectorHit> Nearest(float[] query, IEnumerable<string> candidates, int limit)
        {
            float[] q = Normalized(query);
            lock (_gate)
            {
                if (_dimension == 0 || q.Length != _dimension) return Array.Empty<VectorHit>();
                var hits = new List<VectorHit>();
                foreach (string hash in candidates.Distinct(StringComparer.Ordinal))
                {
                    if (!_vectors.TryGetValue(hash, out var v)) continue;
                    double dot = 0;
                    for (int i = 0; i < v.Length; i++) dot += v[i] * q[i];
                    hits.Add(new VectorHit(hash, dot));
                }
                return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Hash, StringComparer.Ordinal).Take(limit).ToList();
            }
        }

        private static float[] Normalized(float[] vector)
        {
            double sum = 0;
            foreach (float x in vector) sum += (double)x * x;
            double length = Math.Sqrt(sum);
            var result = new float[vector.Length];
            if (length == 0) return result;
            for (int i = 0; i < vector.Length; i++) result[i] = (float)(vector[i] / length);
            return result;
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~SearchVectorStoreTests"`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add Services/Pad/Search/VectorStore.cs Services/Pad/NoteStore.Encryption.cs tests/Kil0bitSystemMonitor.Tests/SearchVectorStoreTests.cs
git commit -m "feat(pad-search): vector store keyed by passage hash, encrypted with the notes key, fingerprinted by server and model"
```

---

### Task 5: The background indexer (`SearchIndexer`)

**Files:**
- Create: `Services/Pad/Search/SearchSettings.cs`
- Create: `Services/Pad/Search/SearchIndexer.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/SearchIndexerTests.cs`

**Interfaces:**
- Consumes: `NotePassages.Cut`, `KeywordIndex`, `VectorStore`, `IEmbedder`, `SearchServer`, `SearchFailure`, `PutResult`.
- Produces:
  - `public sealed record SearchSettings(bool Meaning, SearchServer? Embedding, bool Rerank, SearchServer? Reranker) { static SearchSettings Off; string Fingerprint; bool CanEmbed; bool CanRerank; }`
  - `public sealed record IndexProgress(int Notes, int Passages, int WithVectors, int Waiting, SearchFailure LastFailure, int? LastStatus, bool Full) { static IndexProgress Empty; }`
  - `public sealed class SearchIndexer : IDisposable` with `SearchIndexer(VectorStore vectors, IEmbedder embedder, Func<SearchSettings> settings, Func<string, string?> loadStoredText, Action<string>? warn = null, Func<int, TimeSpan>? retryDelay = null, TimeSpan? saveEvery = null)`; `KeywordIndex Keywords`; `VectorStore Vectors`; `IndexProgress Progress`; `event Action? ProgressChanged` (worker thread); `void SetNote(string noteId, string title, string text, DateTime modifiedUtc)`; `void IndexStored(string noteId, string title, DateTime modifiedUtc)`; `void RemoveNote(string noteId)`; `void Reconcile(IReadOnlyCollection<string> existingIds)`; `void SettingsChanged()`; `void Rebuild()`; `Task WhenIdle()`; `static TimeSpan DefaultRetryDelay(int failures)`; `const int BatchSize = 16`; `static readonly TimeSpan BatchTimeout`.

Behaviour (spec 3.5): all work runs on one background worker in arrival order. `SetNote` cuts and indexes; passages without a vector (meaning search on) wait for embedding, newest note first, 16 per request. A failed request retries after `retryDelay(n)`. Vectors are saved at most every `saveEvery` (10 s) and when the waiting list empties. `SettingsChanged` (and the constructor) apply the settings: meaning off deletes the vectors; another fingerprint deletes them and starts over; a retry wait is cut short. `WhenIdle` completes when the worker has nothing it can run now.

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Pad.Search;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class SearchIndexerTests : IDisposable
    {
        private readonly PadTempDir _dir = new();
        public void Dispose() => _dir.Dispose();

        /// <summary>Embeds each text as [length, 1], or fails as told; records every batch.</summary>
        internal sealed class FakeEmbedder : IEmbedder
        {
            public List<IReadOnlyList<string>> Batches { get; } = new();
            public SearchFailure FailWith { get; set; } = SearchFailure.None;
            public int Dimension { get; set; } = 2;
            public Func<Task>? Gate { get; set; }

            public async Task<EmbeddingResult> EmbedAsync(SearchServer server, IReadOnlyList<string> texts, TimeSpan timeout, CancellationToken cancel)
            {
                lock (Batches) Batches.Add(texts.ToList());
                if (Gate != null) await Gate();
                if (FailWith != SearchFailure.None) return new EmbeddingResult(null, FailWith, 503);
                return new EmbeddingResult(texts.Select(t => Enumerable.Range(0, Dimension).Select(i => i == 0 ? (float)t.Length : 1f).ToArray()).ToList(), SearchFailure.None, 200);
            }

            public int TextsSent { get { lock (Batches) return Batches.Sum(b => b.Count); } }
        }

        private static readonly SearchServer Server = new("http://gpu/v1", "m", null);
        private SearchSettings _settings = new(true, Server, false, null);
        private readonly Dictionary<string, string> _stored = new();

        private SearchIndexer NewIndexer(FakeEmbedder embedder, Func<int, TimeSpan>? retry = null) => new(
            new VectorStore(Path.Combine(_dir.Root, "search"), b => b.ToArray(), (byte[] d, out byte[] p) => { p = d; return true; }),
            embedder,
            () => _settings,
            id => _stored.TryGetValue(id, out var t) ? t : null,
            retryDelay: retry ?? (_ => TimeSpan.FromMilliseconds(20)),
            saveEvery: TimeSpan.FromMilliseconds(10));

        [Fact]
        public async Task A_note_is_indexed_by_words_and_by_meaning()
        {
            var embedder = new FakeEmbedder();
            using var indexer = NewIndexer(embedder);

            indexer.SetNote("a", "t", "vpn does not connect", DateTime.UtcNow);
            await indexer.WhenIdle();

            Assert.Single(indexer.Keywords.Search("vpn", 10));
            Assert.Equal(1, indexer.Vectors.Count);
            Assert.Equal(new IndexProgress(1, 1, 1, 0, SearchFailure.None, null, false), indexer.Progress);
        }

        [Fact]
        public async Task Only_changed_passages_are_sent_again()
        {
            var embedder = new FakeEmbedder();
            using var indexer = NewIndexer(embedder);

            indexer.SetNote("a", "t", "first paragraph\n\n" + new string('x', 900), DateTime.UtcNow);
            await indexer.WhenIdle();
            Assert.Equal(2, embedder.TextsSent);

            indexer.SetNote("a", "t", "first paragraph\n\n" + new string('y', 900), DateTime.UtcNow);
            await indexer.WhenIdle();
            Assert.Equal(3, embedder.TextsSent);
        }

        [Fact]
        public async Task The_latest_text_of_a_note_wins()
        {
            var embedder = new FakeEmbedder();
            var release = new TaskCompletionSource();
            embedder.Gate = () => release.Task;
            using var indexer = NewIndexer(embedder);

            indexer.SetNote("a", "t", "old words", DateTime.UtcNow);
            while (embedder.TextsSent == 0) await Task.Delay(5);      // the old text is being embedded
            indexer.SetNote("a", "t", "new words", DateTime.UtcNow);
            release.SetResult();
            await indexer.WhenIdle();

            Assert.Empty(indexer.Keywords.Search("old", 10));
            Assert.Single(indexer.Keywords.Search("new", 10));
            var inUse = indexer.Keywords.AllPassages().Select(p => p.Hash).ToHashSet();
            Assert.Equal(1, indexer.Vectors.Count);
            Assert.True(indexer.Vectors.Has(inUse.Single()));
        }

        [Fact]
        public async Task Meaning_off_sends_nothing_and_turning_it_off_deletes_the_vectors()
        {
            var embedder = new FakeEmbedder();
            using var indexer = NewIndexer(embedder);
            indexer.SetNote("a", "t", "words", DateTime.UtcNow);
            await indexer.WhenIdle();
            Assert.True(File.Exists(indexer.Vectors.FilePath));

            _settings = SearchSettings.Off;
            indexer.SettingsChanged();
            indexer.SetNote("b", "t", "more words", DateTime.UtcNow);
            await indexer.WhenIdle();

            Assert.Equal(1, embedder.TextsSent);
            Assert.False(File.Exists(indexer.Vectors.FilePath));
            Assert.Equal(0, indexer.Vectors.Count);
            Assert.Equal(2, indexer.Keywords.NoteCount);   // words still work
        }

        [Fact]
        public async Task Another_model_starts_the_vectors_over()
        {
            var embedder = new FakeEmbedder();
            using var indexer = NewIndexer(embedder);
            indexer.SetNote("a", "t", "words", DateTime.UtcNow);
            await indexer.WhenIdle();

            _settings = _settings with { Embedding = Server with { Model = "other" } };
            indexer.SettingsChanged();
            await indexer.WhenIdle();

            Assert.Equal(2, embedder.TextsSent);
            Assert.Equal("http://gpu/v1|other", indexer.Vectors.Fingerprint);
            Assert.Equal(1, indexer.Vectors.Count);
        }

        [Fact]
        public async Task A_dimension_change_starts_the_vectors_over()
        {
            var embedder = new FakeEmbedder();
            using var indexer = NewIndexer(embedder);
            indexer.SetNote("a", "t", "words", DateTime.UtcNow);
            await indexer.WhenIdle();

            embedder.Dimension = 3;   // the server's model was swapped behind the same address
            indexer.SetNote("b", "t", "other words", DateTime.UtcNow);
            await indexer.WhenIdle();

            Assert.Equal(3, indexer.Vectors.Dimension);
            Assert.Equal(2, indexer.Vectors.Count);
        }

        [Fact]
        public async Task A_failing_server_is_retried_and_named()
        {
            var embedder = new FakeEmbedder { FailWith = SearchFailure.Unreachable };
            var delays = new List<int>();
            using var indexer = NewIndexer(embedder, n => { lock (delays) delays.Add(n); return TimeSpan.FromMilliseconds(30); });

            indexer.SetNote("a", "t", "words", DateTime.UtcNow);
            await indexer.WhenIdle();
            Assert.Equal(SearchFailure.Unreachable, indexer.Progress.LastFailure);
            Assert.Equal(1, indexer.Progress.Waiting);
            Assert.Single(indexer.Keywords.Search("words", 10));

            embedder.FailWith = SearchFailure.None;
            await Task.Delay(100);
            await indexer.WhenIdle();
            Assert.Equal(0, indexer.Progress.Waiting);
            Assert.Equal(SearchFailure.None, indexer.Progress.LastFailure);
            Assert.Equal(1, delays.First());
        }

        [Fact]
        public void Retries_wait_1_2_5_10_then_30_minutes()
        {
            Assert.Equal(new[] { 1, 2, 5, 10, 30, 30 }, Enumerable.Range(1, 6).Select(n => (int)SearchIndexer.DefaultRetryDelay(n).TotalMinutes).ToArray());
        }

        [Fact]
        public async Task Sixteen_passages_go_per_request_newest_note_first()
        {
            var embedder = new FakeEmbedder();
            var release = new TaskCompletionSource();
            embedder.Gate = () => release.Task;
            using var indexer = NewIndexer(embedder);

            indexer.SetNote("blocker", "t", "first", DateTime.UtcNow.AddDays(-10));
            while (embedder.TextsSent == 0) await Task.Delay(5);
            indexer.SetNote("old", "old", string.Join("\n\n", Enumerable.Range(0, 20).Select(i => "old " + i + " " + new string('o', 790))), DateTime.UtcNow.AddDays(-5));
            indexer.SetNote("new", "new", "fresh", DateTime.UtcNow);
            release.SetResult();
            await indexer.WhenIdle();

            Assert.All(embedder.Batches, b => Assert.True(b.Count <= SearchIndexer.BatchSize));
            Assert.StartsWith("new", embedder.Batches[1][0]);
        }

        [Fact]
        public async Task Stored_notes_are_read_on_the_worker_and_missing_ones_removed()
        {
            using var indexer = NewIndexer(new FakeEmbedder());
            _stored["s"] = "stored words";
            indexer.IndexStored("s", "t", DateTime.UtcNow);
            indexer.IndexStored("gone", "t", DateTime.UtcNow);
            indexer.SetNote("x", "t", "x words", DateTime.UtcNow);
            await indexer.WhenIdle();
            Assert.Single(indexer.Keywords.Search("stored", 10));

            indexer.Reconcile(new[] { "s" });
            await indexer.WhenIdle();
            Assert.Equal(new[] { "s" }, indexer.Keywords.NoteIds().ToArray());
        }

        [Fact]
        public async Task Rebuild_sends_everything_again()
        {
            var embedder = new FakeEmbedder();
            using var indexer = NewIndexer(embedder);
            indexer.SetNote("a", "t", "words", DateTime.UtcNow);
            await indexer.WhenIdle();

            indexer.Rebuild();
            await indexer.WhenIdle();

            Assert.Equal(2, embedder.TextsSent);
            Assert.Equal(1, indexer.Vectors.Count);
        }

        [Fact]
        public async Task Progress_changes_are_announced()
        {
            using var indexer = NewIndexer(new FakeEmbedder());
            int seen = 0;
            indexer.ProgressChanged += () => Interlocked.Increment(ref seen);
            indexer.SetNote("a", "t", "words", DateTime.UtcNow);
            await indexer.WhenIdle();
            Assert.True(seen > 0);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~SearchIndexerTests"`
Expected: build error, `SearchIndexer` does not exist.

- [ ] **Step 3: Write `SearchSettings.cs`**

```csharp
namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>
    /// Settings → MicaPad → Search as the search code needs it: meaning search and its server,
    /// reranking and its server. A server is null when Settings has no address for it.
    /// </summary>
    public sealed record SearchSettings(bool Meaning, SearchServer? Embedding, bool Rerank, SearchServer? Reranker)
    {
        public static SearchSettings Off { get; } = new(false, null, false, null);

        /// <summary>The server and model the vectors belong to; "" without a server.</summary>
        public string Fingerprint => Embedding == null ? "" : Embedding.BaseUrl + "|" + Embedding.Model;

        /// <summary>Passages and queries may be sent to the embedding server.</summary>
        public bool CanEmbed => Meaning && Embedding != null;

        /// <summary>Results may be sent to the reranker (only with meaning search, spec 2).</summary>
        public bool CanRerank => CanEmbed && Rerank && Reranker != null;
    }

    /// <summary>What the index holds and what it is waiting for (Settings status line, pane status line).</summary>
    public sealed record IndexProgress(int Notes, int Passages, int WithVectors, int Waiting, SearchFailure LastFailure, int? LastStatus, bool Full)
    {
        public static IndexProgress Empty { get; } = new(0, 0, 0, 0, SearchFailure.None, null, false);
    }
}
```

- [ ] **Step 4: Write `SearchIndexer.cs`**

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>
    /// Keeps the keyword index and the vectors in step with the notes (spec 3.5). Every change is
    /// queued and applied in order on one background worker, which also sends the passages that
    /// have no vector to the embedding server, <see cref="BatchSize"/> at a time, newest note
    /// first, retrying a failed request after <c>retryDelay(n)</c>.
    /// </summary>
    public sealed class SearchIndexer : IDisposable
    {
        public const int BatchSize = 16;
        public static readonly TimeSpan BatchTimeout = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan DefaultSaveEvery = TimeSpan.FromSeconds(10);

        /// <summary>1, 2, 5, 10, then every 30 minutes.</summary>
        public static TimeSpan DefaultRetryDelay(int failures) => TimeSpan.FromMinutes(failures switch
        {
            1 => 1,
            2 => 2,
            3 => 5,
            4 => 10,
            _ => 30,
        });

        private sealed record NoteInfo(string Title, DateTime ModifiedUtc);

        private readonly ConcurrentQueue<Action> _work = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _stop = new();
        private readonly IEmbedder _embedder;
        private readonly Func<SearchSettings> _settings;
        private readonly Func<string, string?> _loadStoredText;
        private readonly Action<string> _warn;
        private readonly Func<int, TimeSpan> _retryDelay;
        private readonly TimeSpan _saveEvery;
        private readonly Task _worker;
        private readonly object _idleGate = new();
        private readonly List<TaskCompletionSource> _idleWaiters = new();

        // Worker-only state.
        private readonly Dictionary<string, NoteInfo> _notes = new(StringComparer.Ordinal);
        private string? _loadedFingerprint;
        private int _failures;
        private long _retryAtMs;
        private long _lastSaveMs;
        private bool _full;
        private SearchFailure _lastFailure;
        private int? _lastStatus;

        private IndexProgress _progress = IndexProgress.Empty;

        public SearchIndexer(VectorStore vectors, IEmbedder embedder, Func<SearchSettings> settings, Func<string, string?> loadStoredText,
                             Action<string>? warn = null, Func<int, TimeSpan>? retryDelay = null, TimeSpan? saveEvery = null)
        {
            Vectors = vectors;
            _embedder = embedder;
            _settings = settings;
            _loadStoredText = loadStoredText;
            _warn = warn ?? (_ => { });
            _retryDelay = retryDelay ?? DefaultRetryDelay;
            _saveEvery = saveEvery ?? DefaultSaveEvery;
            Enqueue(ApplySettings);
            _worker = Task.Run(RunAsync);
        }

        public KeywordIndex Keywords { get; } = new();

        public VectorStore Vectors { get; }

        /// <summary>The latest progress; read from any thread.</summary>
        public IndexProgress Progress => Volatile.Read(ref _progress);

        /// <summary>Raised on the worker thread after progress changes.</summary>
        public event Action? ProgressChanged;

        /// <summary>The note's current text; cut and indexed on the worker.</summary>
        public void SetNote(string noteId, string title, string text, DateTime modifiedUtc) =>
            Enqueue(() => Index(noteId, title, text, modifiedUtc));

        /// <summary>A note not open in MicaPad: its stored text is read on the worker; a note with none is removed.</summary>
        public void IndexStored(string noteId, string title, DateTime modifiedUtc) => Enqueue(() =>
        {
            string? text = _loadStoredText(noteId);
            if (text == null) Forget(noteId);
            else Index(noteId, title, text, modifiedUtc);
        });

        public void RemoveNote(string noteId) => Enqueue(() => Forget(noteId));

        /// <summary>Removes every indexed note not in <paramref name="existingIds"/>.</summary>
        public void Reconcile(IReadOnlyCollection<string> existingIds)
        {
            var keep = new HashSet<string>(existingIds, StringComparer.Ordinal);
            Enqueue(() =>
            {
                foreach (string id in Keywords.NoteIds().Where(id => !keep.Contains(id)).ToList()) Forget(id);
            });
        }

        /// <summary>Settings → Search changed: applied in order with the notes.</summary>
        public void SettingsChanged() => Enqueue(ApplySettings);

        /// <summary>Drops every vector and makes them again.</summary>
        public void Rebuild() => Enqueue(() =>
        {
            Vectors.Delete();
            _loadedFingerprint = null;
            ApplySettings();
        });

        /// <summary>Completes once the worker has nothing it can run now (tests; a retry wait counts as nothing).</summary>
        public Task WhenIdle()
        {
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_idleGate) _idleWaiters.Add(waiter);
            _signal.Release();
            return waiter.Task;
        }

        private void Enqueue(Action work)
        {
            _work.Enqueue(work);
            _signal.Release();
        }

        private async Task RunAsync()
        {
            var token = _stop.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    bool didWork = false;
                    while (_work.TryDequeue(out var work))
                    {
                        work();
                        didWork = true;
                    }
                    if (didWork) Publish();

                    if (CanEmbedNow())
                    {
                        await EmbedBatchAsync(token).ConfigureAwait(false);
                        continue;
                    }

                    SaveIfDue(force: Waiting() == 0);
                    ReleaseIdleWaiters();

                    long wait = _retryAtMs > Environment.TickCount64 ? _retryAtMs - Environment.TickCount64 : 1000;
                    await _signal.WaitAsync(TimeSpan.FromMilliseconds(Math.Clamp(wait, 1, 60_000)), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _warn("Search indexing failed (" + ex.GetType().Name + ").");
                }
            }
            ReleaseIdleWaiters();
        }

        private void ReleaseIdleWaiters()
        {
            if (!_work.IsEmpty) return;
            List<TaskCompletionSource> waiters;
            lock (_idleGate)
            {
                waiters = _idleWaiters.ToList();
                _idleWaiters.Clear();
            }
            foreach (var waiter in waiters) waiter.TrySetResult();
        }

        private void Index(string noteId, string title, string text, DateTime modifiedUtc)
        {
            _notes[noteId] = new NoteInfo(title, modifiedUtc);
            Keywords.Set(noteId, NotePassages.Cut(noteId, title, text));
        }

        private void Forget(string noteId)
        {
            _notes.Remove(noteId);
            Keywords.Remove(noteId);
        }

        private void ApplySettings()
        {
            var settings = _settings();
            _failures = 0;
            _retryAtMs = 0;
            _lastFailure = SearchFailure.None;
            _lastStatus = null;
            _full = false;

            if (!settings.Meaning)
            {
                if (_loadedFingerprint != null || Vectors.Count > 0 || System.IO.File.Exists(Vectors.FilePath)) Vectors.Delete();
                _loadedFingerprint = null;
                return;
            }
            if (!settings.CanEmbed) return;   // meaning on but no server yet: keep what is stored

            if (_loadedFingerprint != settings.Fingerprint)
            {
                if (_loadedFingerprint != null) Vectors.Delete();
                Vectors.Load(settings.Fingerprint);
                _loadedFingerprint = settings.Fingerprint;
            }
        }

        /// <summary>Passages in use with no vector, newest note first, one per hash.</summary>
        private List<Passage> WaitingPassages() => Keywords.AllPassages()
            .Where(p => !Vectors.Has(p.Hash))
            .GroupBy(p => p.Hash, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(p => _notes.TryGetValue(p.NoteId, out var info) ? info.ModifiedUtc : DateTime.MinValue)
            .ThenBy(p => p.NoteId, StringComparer.Ordinal)
            .ThenBy(p => p.FirstLine)
            .ToList();

        private int Waiting() => _settings().CanEmbed && _loadedFingerprint != null ? WaitingPassages().Count : 0;

        private bool CanEmbedNow() =>
            _work.IsEmpty && !_full && _settings().CanEmbed && _loadedFingerprint == _settings().Fingerprint
            && Environment.TickCount64 >= _retryAtMs && WaitingPassages().Count > 0;

        private async Task EmbedBatchAsync(CancellationToken token)
        {
            var settings = _settings();
            var batch = WaitingPassages().Take(BatchSize).ToList();
            var result = await _embedder.EmbedAsync(settings.Embedding!, batch.Select(p => p.SentText).ToList(), BatchTimeout, token).ConfigureAwait(false);

            if (result.Vectors == null)
            {
                _failures++;
                _lastFailure = result.Failure;
                _lastStatus = result.Status;
                _retryAtMs = Environment.TickCount64 + (long)_retryDelay(_failures).TotalMilliseconds;
                _warn("The embedding server " + SearchFailureText.Describe(result.Failure, result.Status) + "; indexing waits and tries again.");
                Publish();
                return;
            }

            _failures = 0;
            _lastFailure = SearchFailure.None;
            _lastStatus = null;
            for (int i = 0; i < batch.Count; i++)
            {
                switch (Vectors.Put(batch[i].Hash, result.Vectors[i]))
                {
                    case PutResult.Full:
                        _full = true;
                        break;
                    case PutResult.WrongDimension:
                        _warn("The embedding server now gives vectors of another length; the search vectors are made again.");
                        Vectors.Delete();
                        Vectors.Load(settings.Fingerprint);
                        Vectors.Put(batch[i].Hash, result.Vectors[i]);
                        break;
                }
                if (_full) break;
            }
            SaveIfDue(force: false);
            Publish();
        }

        private void SaveIfDue(bool force)
        {
            if (_loadedFingerprint == null) return;
            long now = Environment.TickCount64;
            if (!force && now - _lastSaveMs < _saveEvery.TotalMilliseconds) return;
            Vectors.Keep(Keywords.AllPassages().Select(p => p.Hash).ToHashSet(StringComparer.Ordinal));
            if (Vectors.Save()) _lastSaveMs = now;
        }

        private void Publish()
        {
            var passages = Keywords.AllPassages();
            int withVectors = passages.Count(p => Vectors.Has(p.Hash));
            var progress = new IndexProgress(Keywords.NoteCount, passages.Count, withVectors, Waiting(), _lastFailure, _lastStatus, _full);
            if (progress == Volatile.Read(ref _progress)) return;
            Volatile.Write(ref _progress, progress);
            ProgressChanged?.Invoke();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _signal.Release();
            try { _worker.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
            _stop.Dispose();
        }
    }
}
```

Ruling for the implementer: if `The_latest_text_of_a_note_wins` leaves the old passage's vector stored (the in-flight batch finishes after the new text was indexed), that is expected until the next save: `SaveIfDue` runs `Keep(...)` before writing. The test asserts after `WhenIdle`, by which time `SaveIfDue(force: true)` has run because nothing is waiting. If it still fails, fix the worker (not the test): `Keep` must run before the idle waiters are released.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~SearchIndexerTests"`
Expected: all pass. Run it three times; a timing-dependent failure is a bug in the worker, not a reason to add sleeps to the tests.

- [ ] **Step 6: Commit**

```bash
git add Services/Pad/Search/SearchSettings.cs Services/Pad/Search/SearchIndexer.cs tests/Kil0bitSystemMonitor.Tests/SearchIndexerTests.cs
git commit -m "feat(pad-search): background indexer - keyword index per note, vectors for changed passages only, retries and settings"
```

---

### Task 6: The query pipeline (`NoteSearch`) and its pure helpers

**Files:**
- Create: `Services/Pad/Search/SearchFusion.cs`
- Create: `Services/Pad/Search/SearchSnippet.cs`
- Create: `Services/Pad/Search/SearchLocate.cs`
- Create: `Services/Pad/Search/NoteSearch.cs`
- Test: `tests/Kil0bitSystemMonitor.Tests/SearchPipelineTests.cs`

**Interfaces:**
- Consumes: `SearchIndexer` (`Keywords`, `Vectors`, `Progress`), `IEmbedder`, `IReranker`, `SearchSettings`, `SearchFailureText`, `SearchTokens`.
- Produces:
  - `public static class SearchFusion { const int K = 60; static IReadOnlyList<Passage> Fuse(IReadOnlyList<Passage> first, IReadOnlyList<Passage> second); static IReadOnlyList<Passage> Cap(IEnumerable<Passage> ordered, int perNote, int total); }`
  - `public readonly record struct SnippetRun(string Text, bool Bold);` and `public static class SearchSnippet { const int MaxChars = 200; static IReadOnlyList<SnippetRun> Make(string body, string query); }`
  - `public static class SearchLocate { static int FindLine(int lineCount, Func<int, string> lineText, int recordedLine, string firstLineText); }` (1-based)
  - `public sealed record SearchOutcome(string Query, IReadOnlyList<Passage> Hits, bool UsedMeaning, bool Reranked, SearchFailure EmbedFailure, int? EmbedStatus, SearchFailure RerankFailure, int? RerankStatus, IndexProgress Progress) { static SearchOutcome Empty(IndexProgress progress); }`
  - `public sealed class NoteSearch { consts KeywordTop=50, VectorTop=50, RerankTop=40, PerNote=3, Total=20, QueryCacheSize=20; static readonly TimeSpan QueryEmbedTimeout (5 s), RerankTimeout (8 s); NoteSearch(SearchIndexer indexer, IEmbedder embedder, IReranker reranker, Func<SearchSettings> settings); Task<SearchOutcome> SearchAsync(string query, CancellationToken cancel); }`
  - `public static class SearchStatusText { static string For(SearchOutcome outcome, SearchSettings settings); static string Index(IndexProgress progress, SearchSettings settings); }`

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Pad.Search;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class SearchPipelineTests : IDisposable
    {
        private readonly PadTempDir _dir = new();
        public void Dispose() => _dir.Dispose();

        private static Passage P(string note, int line, string body = "x") =>
            new(note, "t", "", line, line, body, body, "t\n\n" + body, NotePassages.HashOf(note + line));

        [Fact]
        public void Fusion_adds_reciprocal_ranks()
        {
            var a = P("a", 1); var b = P("b", 1); var c = P("c", 1);
            // a: 1/61 + 1/62; b: 1/62 + 1/61; c: 1/63 — a and b tie, a was ranked first sooner
            var fused = SearchFusion.Fuse(new[] { a, b, c }, new[] { b, a });
            Assert.Equal(new[] { a, b, c }, fused.ToArray());
        }

        [Fact]
        public void Fusion_with_one_list_keeps_its_order()
        {
            var a = P("a", 1); var b = P("b", 1);
            Assert.Equal(new[] { a, b }, SearchFusion.Fuse(new[] { a, b }, Array.Empty<Passage>()).ToArray());
        }

        [Fact]
        public void Cap_keeps_three_per_note_and_twenty_in_all()
        {
            var many = Enumerable.Range(1, 5).Select(i => P("a", i)).Concat(Enumerable.Range(1, 30).Select(i => P("n" + i, 1))).ToList();
            var capped = SearchFusion.Cap(many, 3, 20);
            Assert.Equal(20, capped.Count);
            Assert.Equal(3, capped.Count(p => p.NoteId == "a"));
        }

        [Fact]
        public void The_snippet_shows_the_best_line_with_matches_bold()
        {
            var runs = SearchSnippet.Make("intro line\nthe VPN does not connect\nother", "vpn connect");
            Assert.Equal("the VPN does not connect", string.Concat(runs.Select(r => r.Text)).Split('\n')[0]);
            Assert.Equal(new[] { "VPN", "connect" }, runs.Where(r => r.Bold).Select(r => r.Text).ToArray());
        }

        [Fact]
        public void A_Thai_match_is_bold_as_one_run()
        {
            var runs = SearchSnippet.Make("เน็ตไม่ติดตอนเช้า", "ไม่ติด");
            Assert.Equal("ไม่ติด", Assert.Single(runs, r => r.Bold).Text);
        }

        [Fact]
        public void A_meaning_only_hit_shows_its_first_lines()
        {
            var runs = SearchSnippet.Make("\nfirst\nsecond\nthird\nfourth", "unrelated");
            Assert.Equal("first\nsecond\nthird", string.Concat(runs.Select(r => r.Text)));
            Assert.DoesNotContain(runs, r => r.Bold);
        }

        [Fact]
        public void The_snippet_is_short()
        {
            var runs = SearchSnippet.Make(new string('a', 500) + " needle " + new string('b', 500), "needle");
            Assert.True(string.Concat(runs.Select(r => r.Text)).Length <= SearchSnippet.MaxChars + 2);   // plus the two ellipses
            Assert.Contains(runs, r => r.Bold && r.Text == "needle");
        }

        [Fact]
        public void The_passage_is_found_again_after_lines_moved()
        {
            string[] lines = { "new line", "another", "# VPN", "body" };
            Assert.Equal(3, SearchLocate.FindLine(lines.Length, i => lines[i - 1], 1, "# VPN"));
            Assert.Equal(3, SearchLocate.FindLine(lines.Length, i => lines[i - 1], 3, "# VPN"));
            Assert.Equal(4, SearchLocate.FindLine(lines.Length, i => lines[i - 1], 9, "gone"));     // clamped
            Assert.Equal(2, SearchLocate.FindLine(lines.Length, i => lines[i - 1], 2, "gone"));
        }

        // ---- the pipeline ----

        private sealed class FakeEmbedder : IEmbedder
        {
            public Func<string, float[]> Vector { get; set; } = t => t.Contains("connect") || t.Contains("vpn") ? new[] { 1f, 0f } : new[] { 0f, 1f };
            public SearchFailure FailWith { get; set; }
            public TimeSpan Delay { get; set; }
            public int Calls;

            public async Task<EmbeddingResult> EmbedAsync(SearchServer server, IReadOnlyList<string> texts, TimeSpan timeout, CancellationToken cancel)
            {
                Interlocked.Increment(ref Calls);
                if (Delay > TimeSpan.Zero)
                {
                    try { await Task.Delay(Delay, cancel).WaitAsync(timeout, cancel); }
                    catch (TimeoutException) { return new EmbeddingResult(null, SearchFailure.TimedOut, null); }
                }
                if (FailWith != SearchFailure.None) return new EmbeddingResult(null, FailWith, 503);
                return new EmbeddingResult(texts.Select(Vector).ToList(), SearchFailure.None, 200);
            }
        }

        private sealed class FakeReranker : IReranker
        {
            public SearchFailure FailWith { get; set; }
            public List<IReadOnlyList<string>> Seen { get; } = new();

            public Task<RerankResult> RerankAsync(SearchServer server, string query, IReadOnlyList<string> documents, int topN, TimeSpan timeout, CancellationToken cancel)
            {
                Seen.Add(documents.ToList());
                if (FailWith != SearchFailure.None) return Task.FromResult(new RerankResult(null, FailWith, 500));
                // reverses the order it was given
                var ranked = Enumerable.Range(0, documents.Count).Reverse().Select((index, rank) => new RerankScore(index, 1.0 / (rank + 1))).ToList();
                return Task.FromResult(new RerankResult(ranked, SearchFailure.None, 200));
            }
        }

        private SearchSettings _settings = new(true, new SearchServer("http://gpu/v1", "m", null), true, new SearchServer("http://gpu/v1", "r", null));

        private async Task<(NoteSearch Search, SearchIndexer Indexer)> Build(FakeEmbedder embedder, FakeReranker reranker)
        {
            var indexer = new SearchIndexer(
                new VectorStore(Path.Combine(_dir.Root, "search"), b => b, (byte[] d, out byte[] p) => { p = d; return true; }),
                embedder, () => _settings, _ => null, retryDelay: _ => TimeSpan.FromHours(1));
            indexer.SetNote("a", "VPN notes", "it does not connect after sleep", DateTime.UtcNow);
            indexer.SetNote("b", "Lunch", "noodles on friday", DateTime.UtcNow);
            await indexer.WhenIdle();
            return (new NoteSearch(indexer, embedder, reranker, () => _settings), indexer);
        }

        [Fact]
        public async Task Meaning_and_words_are_fused_then_reranked()
        {
            var reranker = new FakeReranker();
            var (search, indexer) = await Build(new FakeEmbedder(), reranker);
            using (indexer)
            {
                var outcome = await search.SearchAsync("noodles", default);

                Assert.True(outcome.UsedMeaning);
                Assert.True(outcome.Reranked);
                Assert.Equal(2, outcome.Hits.Count);                    // "b" by words, both by meaning
                Assert.Equal("a", outcome.Hits[0].NoteId);              // the fake reranker reverses the fused order
                Assert.Single(reranker.Seen);
                Assert.Equal("Meaning + words, reranked", SearchStatusText.For(outcome, _settings));
            }
        }

        [Fact]
        public async Task Words_only_when_meaning_is_off()
        {
            _settings = SearchSettings.Off;
            var embedder = new FakeEmbedder();
            var reranker = new FakeReranker();
            var (search, indexer) = await Build(embedder, reranker);
            using (indexer)
            {
                var outcome = await search.SearchAsync("noodles", default);

                Assert.Equal("b", Assert.Single(outcome.Hits).NoteId);
                Assert.False(outcome.UsedMeaning);
                Assert.Equal(0, embedder.Calls);
                Assert.Empty(reranker.Seen);
                Assert.Equal("Words", SearchStatusText.For(outcome, _settings));
            }
        }

        [Fact]
        public async Task A_failing_embedding_server_leaves_words_and_says_why()
        {
            var embedder = new FakeEmbedder();
            var (search, indexer) = await Build(embedder, new FakeReranker());
            using (indexer)
            {
                embedder.FailWith = SearchFailure.Unreachable;
                var outcome = await search.SearchAsync("noodles", default);

                Assert.Equal("b", outcome.Hits[0].NoteId);
                Assert.False(outcome.UsedMeaning);
                Assert.StartsWith("Words only: the embedding server could not be reached", SearchStatusText.For(outcome, _settings));
            }
        }

        [Fact]
        public async Task A_hanging_embedding_server_times_out_to_words_only()
        {
            var embedder = new FakeEmbedder();
            var (search, indexer) = await Build(embedder, new FakeReranker());
            using (indexer)
            {
                embedder.Delay = TimeSpan.FromMinutes(5);
                var started = DateTime.UtcNow;
                var outcome = await search.SearchAsync("noodles", default);

                Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(7));
                Assert.Equal(SearchFailure.TimedOut, outcome.EmbedFailure);
                Assert.Equal("b", outcome.Hits[0].NoteId);
            }
        }

        [Fact]
        public async Task A_failing_reranker_keeps_the_fused_order()
        {
            var reranker = new FakeReranker { FailWith = SearchFailure.ServerError };
            var (search, indexer) = await Build(new FakeEmbedder(), reranker);
            using (indexer)
            {
                var outcome = await search.SearchAsync("noodles", default);

                Assert.False(outcome.Reranked);
                Assert.Equal("b", outcome.Hits[0].NoteId);
                Assert.Equal("Meaning + words. Not reranked: the reranker failed (HTTP 500)", SearchStatusText.For(outcome, _settings));
            }
        }

        [Fact]
        public async Task The_query_vector_is_cached()
        {
            var embedder = new FakeEmbedder();
            var (search, indexer) = await Build(embedder, new FakeReranker());
            using (indexer)
            {
                int before = embedder.Calls;
                await search.SearchAsync("vpn", default);
                await search.SearchAsync("vpn", default);
                Assert.Equal(before + 1, embedder.Calls);
            }
        }

        [Fact]
        public async Task A_newer_query_cancels_the_older_one()
        {
            var embedder = new FakeEmbedder();
            var (search, indexer) = await Build(embedder, new FakeReranker());
            using (indexer)
            {
                embedder.Delay = TimeSpan.FromSeconds(3);
                using var older = new CancellationTokenSource();
                var first = search.SearchAsync("slow one", older.Token);
                older.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            }
        }

        [Fact]
        public async Task An_empty_query_finds_nothing_and_sends_nothing()
        {
            var embedder = new FakeEmbedder();
            var (search, indexer) = await Build(embedder, new FakeReranker());
            using (indexer)
            {
                int before = embedder.Calls;
                var outcome = await search.SearchAsync("   ", default);
                Assert.Empty(outcome.Hits);
                Assert.Equal(before, embedder.Calls);
            }
        }

        [Fact]
        public void Index_status_names_the_waiting_and_the_failure()
        {
            Assert.Equal("Indexing: 120 of 312 passages",
                SearchStatusText.Index(new IndexProgress(9, 312, 120, 192, SearchFailure.None, null, false), _settings));
            Assert.Equal("Indexing paused: the embedding server refused the key",
                SearchStatusText.Index(new IndexProgress(9, 312, 120, 192, SearchFailure.KeyRefused, 401, false), _settings));
            Assert.Equal("312 passages from 9 notes; 312 with meaning",
                SearchStatusText.Index(new IndexProgress(9, 312, 312, 0, SearchFailure.None, null, false), _settings));
            Assert.Equal("312 passages from 9 notes",
                SearchStatusText.Index(new IndexProgress(9, 312, 0, 0, SearchFailure.None, null, false), SearchSettings.Off));
            // Full = more passages than the store holds (the indexer works it out each pass); Waiting then drains to 0.
            Assert.Equal("25000 passages from 9 notes; 20000 with meaning. The index is full: the rest are found by words only.",
                SearchStatusText.Index(new IndexProgress(9, 25000, 20000, 0, SearchFailure.None, null, true), _settings));
            Assert.Equal("Indexing: 1200 of 25000 passages",
                SearchStatusText.Index(new IndexProgress(9, 25000, 1200, 18800, SearchFailure.None, null, true), _settings));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~SearchPipelineTests"`
Expected: build error, `SearchFusion` does not exist.

- [ ] **Step 3: Write `SearchFusion.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>Merging the keyword and vector lists, and the per-note and total caps (spec 3.5).</summary>
    public static class SearchFusion
    {
        public const int K = 60;

        /// <summary>
        /// Reciprocal Rank Fusion: each list gives a passage 1 / (K + rank), rank from 1; a passage
        /// is identified by note and first line. Ties go to the passage ranked first sooner.
        /// </summary>
        public static IReadOnlyList<Passage> Fuse(IReadOnlyList<Passage> first, IReadOnlyList<Passage> second)
        {
            var scored = new Dictionary<(string, int), (Passage Passage, double Score, int Best, int Order)>();
            int order = 0;
            void Add(IReadOnlyList<Passage> list)
            {
                for (int rank = 0; rank < list.Count; rank++)
                {
                    var key = (list[rank].NoteId, list[rank].FirstLine);
                    double score = 1.0 / (K + rank + 1);
                    scored[key] = scored.TryGetValue(key, out var e)
                        ? (e.Passage, e.Score + score, Math.Min(e.Best, rank), e.Order)
                        : (list[rank], score, rank, order++);
                }
            }
            Add(first);
            Add(second);
            return scored.Values
                .OrderByDescending(e => e.Score)
                .ThenBy(e => e.Best)
                .ThenBy(e => e.Order)
                .Select(e => e.Passage)
                .ToList();
        }

        /// <summary>At most <paramref name="perNote"/> passages of one note and <paramref name="total"/> in all, order kept.</summary>
        public static IReadOnlyList<Passage> Cap(IEnumerable<Passage> ordered, int perNote, int total)
        {
            var perNoteCount = new Dictionary<string, int>(StringComparer.Ordinal);
            var result = new List<Passage>();
            foreach (var passage in ordered)
            {
                int n = perNoteCount.TryGetValue(passage.NoteId, out int c) ? c : 0;
                if (n >= perNote) continue;
                perNoteCount[passage.NoteId] = n + 1;
                result.Add(passage);
                if (result.Count == total) break;
            }
            return result;
        }
    }
}
```

- [ ] **Step 4: Write `SearchSnippet.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>A piece of a result's snippet; matched words are bold.</summary>
    public readonly record struct SnippetRun(string Text, bool Bold);

    /// <summary>
    /// The snippet under a result (spec 1): the passage line with the most distinct query tokens
    /// and up to two lines after it, about <see cref="MaxChars"/> characters around the first
    /// match, every match bold. A passage found only by meaning shows its first three lines.
    /// </summary>
    public static class SearchSnippet
    {
        public const int MaxChars = 200;

        public static IReadOnlyList<SnippetRun> Make(string body, string query)
        {
            var tokens = SearchTokens.Of(query).Where(t => t.Length >= 2 || SearchTokens.IsPieceScript(t[0]))
                                               .Distinct(StringComparer.Ordinal).ToList();
            var lines = body.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

            int best = -1, bestCount = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                int count = tokens.Count(t => lines[i].Contains(t, StringComparison.OrdinalIgnoreCase));
                if (count > bestCount) { best = i; bestCount = count; }
            }

            string text;
            if (best < 0)
            {
                text = string.Join("\n", lines.Where(l => l.Trim().Length > 0).Take(3));
                return new[] { new SnippetRun(Shorten(text, 0), false) };
            }

            text = string.Join("\n", lines.Skip(best).Take(3));
            int firstMatch = tokens.Select(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
            text = Shorten(text, firstMatch);
            return Bold(text, tokens);
        }

        /// <summary>About <see cref="MaxChars"/> characters starting a little before <paramref name="focus"/>, with ellipses where cut.</summary>
        private static string Shorten(string text, int focus)
        {
            if (text.Length <= MaxChars) return text;
            int start = Math.Max(0, Math.Min(focus - 40, text.Length - MaxChars));
            string cut = text.Substring(start, Math.Min(MaxChars, text.Length - start));
            return (start > 0 ? "…" : "") + cut + (start + cut.Length < text.Length ? "…" : "");
        }

        private static IReadOnlyList<SnippetRun> Bold(string text, IReadOnlyList<string> tokens)
        {
            var marked = new bool[text.Length];
            foreach (string token in tokens)
            {
                int at = 0;
                while ((at = text.IndexOf(token, at, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    for (int i = at; i < at + token.Length; i++) marked[i] = true;
                    at += token.Length;
                }
            }

            var runs = new List<SnippetRun>();
            int start = 0;
            for (int i = 1; i <= text.Length; i++)
            {
                if (i == text.Length || marked[i] != marked[start])
                {
                    runs.Add(new SnippetRun(text.Substring(start, i - start), marked[start]));
                    start = i;
                }
            }
            return runs;
        }
    }
}
```

Note: `A_Thai_match_is_bold_as_one_run` works because the bigram tokens of "ไม่ติด" overlap and their marked ranges merge into one run. Latin tokens are matched case-insensitively anywhere (so "connect" also bolds inside "connection"); that is intended.

- [ ] **Step 5: Write `SearchLocate.cs`**

```csharp
using System;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>
    /// Finds a result's passage in the note as it is now (spec 1): the recorded line when its text
    /// still starts there, else the nearest line with that text, else the recorded line kept inside
    /// the note.
    /// </summary>
    public static class SearchLocate
    {
        /// <param name="lineText">The text of a 1-based line.</param>
        public static int FindLine(int lineCount, Func<int, string> lineText, int recordedLine, string firstLineText)
        {
            if (lineCount <= 0) return 1;
            int clamped = Math.Clamp(recordedLine, 1, lineCount);
            if (firstLineText.Length == 0) return clamped;
            if (Matches(lineText(clamped), firstLineText)) return clamped;

            for (int distance = 1; distance < lineCount; distance++)
            {
                int up = clamped - distance, down = clamped + distance;
                if (up >= 1 && Matches(lineText(up), firstLineText)) return up;
                if (down <= lineCount && Matches(lineText(down), firstLineText)) return down;
                if (up < 1 && down > lineCount) break;
            }
            return clamped;
        }

        private static bool Matches(string line, string firstLineText) =>
            line.Trim().StartsWith(firstLineText, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 6: Write `NoteSearch.cs` (with `SearchOutcome` and `SearchStatusText`)**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>One search's results and how it ran, for the pane's list and status line.</summary>
    public sealed record SearchOutcome(
        string Query,
        IReadOnlyList<Passage> Hits,
        bool UsedMeaning,
        bool Reranked,
        SearchFailure EmbedFailure,
        int? EmbedStatus,
        SearchFailure RerankFailure,
        int? RerankStatus,
        IndexProgress Progress)
    {
        public static SearchOutcome Empty(string query, IndexProgress progress) =>
            new(query, Array.Empty<Passage>(), false, false, SearchFailure.None, null, SearchFailure.None, null, progress);
    }

    /// <summary>
    /// The query pipeline (spec 3.5): keyword top 50; with meaning search, the query is embedded
    /// (5 s, the last 20 cached) and the top 50 passages by cosine join by Reciprocal Rank Fusion;
    /// with reranking, the top 40 are reordered by the reranker (8 s); then 3 per note, 20 in all.
    /// A step that fails is skipped and named in the outcome. Cancelling throws.
    /// </summary>
    public sealed class NoteSearch
    {
        public const int KeywordTop = 50;
        public const int VectorTop = 50;
        public const int RerankTop = 40;
        public const int PerNote = 3;
        public const int Total = 20;
        public const int QueryCacheSize = 20;
        public static readonly TimeSpan QueryEmbedTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan RerankTimeout = TimeSpan.FromSeconds(8);

        private readonly SearchIndexer _indexer;
        private readonly IEmbedder _embedder;
        private readonly IReranker _reranker;
        private readonly Func<SearchSettings> _settings;
        private readonly object _cacheGate = new();
        private readonly LinkedList<(string Key, float[] Vector)> _cache = new();

        public NoteSearch(SearchIndexer indexer, IEmbedder embedder, IReranker reranker, Func<SearchSettings> settings)
        {
            _indexer = indexer;
            _embedder = embedder;
            _reranker = reranker;
            _settings = settings;
        }

        public async Task<SearchOutcome> SearchAsync(string query, CancellationToken cancel)
        {
            query = query.Trim();
            var progress = _indexer.Progress;
            if (query.Length == 0) return SearchOutcome.Empty(query, progress);

            var settings = _settings();
            var keyword = _indexer.Keywords.Search(query, KeywordTop).Select(h => h.Passage).ToList();
            cancel.ThrowIfCancellationRequested();

            var vector = new List<Passage>();
            bool usedMeaning = false;
            SearchFailure embedFailure = SearchFailure.None, rerankFailure = SearchFailure.None;
            int? embedStatus = null, rerankStatus = null;

            if (settings.CanEmbed)
            {
                var (queryVector, failure, status) = await QueryVectorAsync(settings, query, cancel).ConfigureAwait(false);
                embedFailure = failure;
                embedStatus = status;
                if (queryVector != null)
                {
                    usedMeaning = true;
                    var passages = _indexer.Keywords.AllPassages();
                    var byHash = passages.ToLookup(p => p.Hash, StringComparer.Ordinal);
                    foreach (var hit in _indexer.Vectors.Nearest(queryVector, passages.Select(p => p.Hash), VectorTop))
                        vector.AddRange(byHash[hit.Hash]);
                }
            }
            cancel.ThrowIfCancellationRequested();

            IReadOnlyList<Passage> ordered = SearchFusion.Fuse(keyword, vector);
            bool reranked = false;

            if (settings.CanRerank && ordered.Count > 0)
            {
                var top = ordered.Take(RerankTop).ToList();
                var result = await _reranker.RerankAsync(settings.Reranker!, query, top.Select(p => p.SentText).ToList(), top.Count, RerankTimeout, cancel).ConfigureAwait(false);
                if (result.Ranked != null)
                {
                    reranked = true;
                    ordered = result.Ranked.Select(r => top[r.Index]).ToList();
                }
                else
                {
                    rerankFailure = result.Failure;
                    rerankStatus = result.Status;
                }
            }

            return new SearchOutcome(query, SearchFusion.Cap(ordered, PerNote, Total), usedMeaning, reranked,
                                     embedFailure, embedStatus, rerankFailure, rerankStatus, _indexer.Progress);
        }

        private async Task<(float[]? Vector, SearchFailure Failure, int? Status)> QueryVectorAsync(SearchSettings settings, string query, CancellationToken cancel)
        {
            string key = settings.Fingerprint + "\n" + query;
            lock (_cacheGate)
            {
                var node = _cache.First;
                while (node != null && node.Value.Key != key) node = node.Next;
                if (node != null)
                {
                    _cache.Remove(node);
                    _cache.AddFirst(node);
                    return (node.Value.Vector, SearchFailure.None, null);
                }
            }

            var result = await _embedder.EmbedAsync(settings.Embedding!, new[] { query }, QueryEmbedTimeout, cancel).ConfigureAwait(false);
            if (result.Vectors == null) return (null, result.Failure, result.Status);

            lock (_cacheGate)
            {
                _cache.AddFirst((key, result.Vectors[0]));
                while (_cache.Count > QueryCacheSize) _cache.RemoveLast();
            }
            return (result.Vectors[0], SearchFailure.None, null);
        }
    }

    /// <summary>The pane's status line and the Settings index line (spec 1 and 2).</summary>
    public static class SearchStatusText
    {
        public static string For(SearchOutcome outcome, SearchSettings settings)
        {
            string how;
            if (!settings.Meaning) how = "Words";
            else if (settings.Embedding == null) how = "Words only: set the embedding server in Settings";
            else if (!outcome.UsedMeaning) how = "Words only: the embedding server " + SearchFailureText.Describe(outcome.EmbedFailure, outcome.EmbedStatus);
            else if (outcome.Reranked) how = "Meaning + words, reranked";
            else if (settings.CanRerank && outcome.RerankFailure != SearchFailure.None)
                how = "Meaning + words. Not reranked: the reranker " + SearchFailureText.Describe(outcome.RerankFailure, outcome.RerankStatus);
            else how = "Meaning + words";

            return outcome.Progress.Waiting > 0 && settings.CanEmbed ? how + " · " + Index(outcome.Progress, settings) : how;
        }

        public static string Index(IndexProgress p, SearchSettings settings)
        {
            if (settings.CanEmbed && p.Waiting > 0)
            {
                return p.LastFailure != SearchFailure.None
                    ? "Indexing paused: the embedding server " + SearchFailureText.Describe(p.LastFailure, p.LastStatus)
                    : "Indexing: " + p.WithVectors + " of " + p.Passages + " passages";
            }
            string words = p.Passages + " passages from " + p.Notes + " notes";
            if (!settings.CanEmbed) return words;
            string meaning = words + "; " + p.WithVectors + " with meaning";
            return p.Full ? meaning + ". The index is full: the rest are found by words only." : meaning;
        }
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~SearchPipelineTests"`
Expected: all pass. `A_hanging_embedding_server_times_out_to_words_only` takes about 5 s (the real query timeout); that is the behaviour under test.

- [ ] **Step 8: Commit**

```bash
git add Services/Pad/Search/SearchFusion.cs Services/Pad/Search/SearchSnippet.cs Services/Pad/Search/SearchLocate.cs Services/Pad/Search/NoteSearch.cs tests/Kil0bitSystemMonitor.Tests/SearchPipelineTests.cs
git commit -m "feat(pad-search): query pipeline - words, meaning, RRF, rerank, caps, fallbacks and status text"
```

---

### Task 7: Settings, config, the service and feeding it

**Files:**
- Create: `Services/Pad/Search/NoteSearchService.cs`
- Create: `Pad/SearchFeeder.cs`
- Create: `Pad/SearchSettingsPanel.xaml`, `Pad/SearchSettingsPanel.xaml.cs`, `Pad/SearchSettingsHost.cs`
- Modify: `Models/SystemMetrics.cs` (AppConfig: six `Pad*` search properties after `PadWebImages`)
- Modify: `Services/Ai/AiSettings.cs` (`SecretNames`: `PadEmbeddingKey`, `PadRerankKey`)
- Modify: `Services/Pad/PadWorkspace.cs` (events `NoteTextChanged`, `NoteDeleted`)
- Modify: `SettingsWindow.xaml` (a Search group at the end of `PadSection`), `SettingsWindow.xaml.cs` (`LoadPadSettings` loads the panel)
- Modify: `App.xaml.cs` (`OpenPad` creates the service and the feeder; app exit disposes them)
- Test: `tests/Kil0bitSystemMonitor.Tests/SearchSettingsTests.cs`

**Interfaces:**
- Consumes: everything in `Services/Pad/Search/`, `SecretStore` (`Services/Ai/SecretStore.cs`), `KrokiClient.TryParseServer`, `AppConfig`, `PadWorkspace`, `OpenNote.TextProvider`.
- Produces:
  - AppConfig: `bool PadSemanticSearch`, `string PadEmbeddingServer`, `string PadEmbeddingModel`, `bool PadRerank`, `string PadRerankServer`, `string PadRerankModel`.
  - `SecretNames.PadEmbeddingKey = "pad-embedding-key"`, `SecretNames.PadRerankKey = "pad-rerank-key"`.
  - `public static class PadSearchSettings { static SearchSettings From(AppConfig config, Func<string, string?> secret); static bool IsSearchProperty(string? propertyName); }` (in `NoteSearchService.cs`).
  - `public sealed class NoteSearchService : IDisposable { NoteSearchService(NoteStore store, Func<SearchSettings> settings, IEmbedder? embedder = null, IReranker? reranker = null, Action<string>? warn = null); SearchIndexer Indexer; NoteSearch Search; Func<SearchSettings> Settings; }`
  - `PadWorkspace`: `public event Action<OpenNote>? NoteTextChanged;` (raised at the end of `NotifyChanged` and `Rename`), `public event Action<string>? NoteDeleted;` (raised when `DeleteClosed` returns true).
  - `internal sealed class SearchFeeder : IDisposable { SearchFeeder(PadWorkspace workspace, SearchIndexer indexer, TimeSpan? debounce = null); void ReconcileAll(); void FlushPending(); }`
  - `public sealed class SearchSettingsHost { required AppConfig Config; required Action Save; required SecretStore Secrets; Func<NoteSearchService?> Service = () => null; IEmbedder Embedder = new EmbeddingClient(); IReranker Reranker = new RerankClient(); }`
  - `public partial class SearchSettingsPanel : UserControl { void Load(SearchSettingsHost host); }` with named controls used by tests: `MeaningToggle`, `EmbeddingServerBox`, `EmbeddingModelBox`, `EmbeddingKeyBox`, `EmbeddingKeySaved`, `EmbeddingTestResult`, `RerankToggle`, `RerankServerBox`, `RerankModelBox`, `RerankKeyBox`, `RerankKeySaved`, `RerankTestResult`, `IndexStatus`, `RebuildButton`, `KeyHint`, `ServerHint`.
  - `App`: `internal static NoteSearchService? PadSearch` (null until MicaPad first opens).

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Search;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class SearchSettingsTests
    {
        [Fact]
        public void Servers_are_kept_normalized_or_empty()
        {
            var config = new AppConfig();
            config.PadEmbeddingServer = " http://gpu:8000/v1/ ";
            config.PadRerankServer = "not a server";
            Assert.Equal("http://gpu:8000/v1", config.PadEmbeddingServer);
            Assert.Equal("", config.PadRerankServer);
            config.PadEmbeddingModel = "  bge-m3 ";
            Assert.Equal("bge-m3", config.PadEmbeddingModel);
        }

        [Fact]
        public void Everything_is_off_by_default()
        {
            var config = new AppConfig();
            Assert.False(config.PadSemanticSearch);
            Assert.False(config.PadRerank);
            Assert.Equal(SearchSettings.Off, PadSearchSettings.From(config, _ => null));
        }

        [Fact]
        public void Settings_carry_the_saved_keys_and_drop_servers_without_an_address()
        {
            var config = new AppConfig
            {
                PadSemanticSearch = true, PadEmbeddingServer = "http://gpu/v1", PadEmbeddingModel = "m",
                PadRerank = true, PadRerankServer = "",
            };
            var settings = PadSearchSettings.From(config, name => name == SecretNames.PadEmbeddingKey ? "sk-e" : null);

            Assert.Equal(new SearchServer("http://gpu/v1", "m", "sk-e"), settings.Embedding);
            Assert.Null(settings.Reranker);
            Assert.True(settings.CanEmbed);
            Assert.False(settings.CanRerank);
        }

        [Fact]
        public void Keys_never_reach_the_config_file()
        {
            var config = new AppConfig { PadEmbeddingServer = "http://gpu/v1" };
            Assert.Null(typeof(AppConfig).GetProperty("PadEmbeddingKey"));
            Assert.Null(typeof(AppConfig).GetProperty("PadRerankKey"));
            Assert.DoesNotContain(typeof(AppConfig).GetProperties(), p => p.Name.StartsWith("Pad", StringComparison.Ordinal) && p.Name.EndsWith("Key", StringComparison.Ordinal));
        }

        [Fact]
        public void The_workspace_tells_about_edits_renames_and_deletes() => UiThread.Run(() =>
        {
            using var env = new PadTestEnv();
            var changed = new System.Collections.Generic.List<string>();
            var deleted = new System.Collections.Generic.List<string>();
            env.Workspace.NoteTextChanged += n => changed.Add(n.Id);
            env.Workspace.NoteDeleted += deleted.Add;

            var note = env.Workspace.NewNote();
            env.Workspace.NotifyChanged(note);
            env.Workspace.Rename(note, "Named");
            Assert.Equal(new[] { note.Id, note.Id }, changed.ToArray());

            note.TextProvider = () => "text";
            env.Workspace.NotifyChanged(note);
            env.Workspace.FlushAll(TimeSpan.FromSeconds(2));
            env.Workspace.Close(note);
            env.Workspace.FlushAll(TimeSpan.FromSeconds(2));
            Assert.True(env.Workspace.DeleteClosed(note.Id));
            Assert.Equal(new[] { note.Id }, deleted.ToArray());
        });

        [Fact]
        public void The_feeder_indexes_open_and_closed_notes_and_follows_edits() => UiThread.Run(() =>
        {
            using var env = new PadTestEnv();
            var open = env.Workspace.NewNote();
            open.TextProvider = () => "open words";
            env.Workspace.NotifyChanged(open);
            env.Workspace.FlushAll(TimeSpan.FromSeconds(2));

            using var service = new NoteSearchService(env.Store, () => SearchSettings.Off);
            using var feeder = new SearchFeeder(env.Workspace, service.Indexer, TimeSpan.FromMilliseconds(1));
            Settle(service.Indexer.WhenIdle());
            Assert.Single(service.Indexer.Keywords.Search("open", 10));

            open.TextProvider = () => "edited words";
            env.Workspace.NotifyChanged(open);
            feeder.FlushPending();
            Settle(service.Indexer.WhenIdle());
            Assert.Empty(service.Indexer.Keywords.Search("open", 10));
            Assert.Single(service.Indexer.Keywords.Search("edited", 10));
        });

        /// <summary>
        /// Waits for the indexer's background worker without blocking calls in a test body
        /// (xUnit1031 warns on Task.Wait/.Result there, and the build must gain no warnings).
        /// </summary>
        private static void Settle(Task task)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(5)) System.Threading.Thread.Sleep(5);
            Assert.True(task.IsCompleted, "the indexer did not settle");
        }

        private static SearchSettingsHost Host(AppConfig config, SecretStore secrets) => new()
        {
            Config = config,
            Save = () => { },
            Secrets = secrets,
        };

        [Fact]
        public void The_panel_saves_servers_models_and_keys_where_they_belong() => UiThread.Run(() =>
        {
            using var dir = new PadTempDir();
            var secrets = new SecretStore(Path.Combine(dir.Root, "secrets.bin"));
            var config = new AppConfig();
            var panel = new SearchSettingsPanel();
            panel.Load(Host(config, secrets));

            panel.MeaningToggle.IsOn = true;
            panel.EmbeddingServerBox.Text = "http://gpu:8000/v1/";
            panel.EmbeddingServerBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            panel.EmbeddingModelBox.Text = " bge-m3 ";
            panel.EmbeddingModelBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            panel.EmbeddingKeyBox.Password = "sk-secret";
            panel.SaveEmbeddingKey();

            Assert.True(config.PadSemanticSearch);
            Assert.Equal("http://gpu:8000/v1", config.PadEmbeddingServer);
            Assert.Equal("http://gpu:8000/v1", panel.EmbeddingServerBox.Text);
            Assert.Equal("bge-m3", config.PadEmbeddingModel);
            Assert.Equal("sk-secret", secrets.Get(SecretNames.PadEmbeddingKey));
            Assert.Equal("", panel.EmbeddingKeyBox.Password);
            Assert.Equal(Visibility.Visible, panel.EmbeddingKeySaved.Visibility);
        });

        [Fact]
        public void The_panel_refuses_a_bad_server_and_keeps_the_one_in_use() => UiThread.Run(() =>
        {
            using var dir = new PadTempDir();
            var config = new AppConfig { PadEmbeddingServer = "http://gpu/v1" };
            var panel = new SearchSettingsPanel();
            panel.Load(Host(config, new SecretStore(Path.Combine(dir.Root, "secrets.bin"))));

            panel.EmbeddingServerBox.Text = "ftp://nope";
            panel.EmbeddingServerBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));

            Assert.Equal("http://gpu/v1", config.PadEmbeddingServer);
            Assert.Equal("http://gpu/v1", panel.EmbeddingServerBox.Text);
            Assert.StartsWith("Not a server address", panel.ServerHint.Text);
        });

        [Fact]
        public void Rerank_is_only_offered_with_meaning_search() => UiThread.Run(() =>
        {
            using var dir = new PadTempDir();
            var config = new AppConfig();
            var panel = new SearchSettingsPanel();
            panel.Load(Host(config, new SecretStore(Path.Combine(dir.Root, "secrets.bin"))));

            Assert.False(panel.RerankToggle.IsEnabled);
            panel.MeaningToggle.IsOn = true;
            Assert.True(panel.RerankToggle.IsEnabled);
        });
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~SearchSettingsTests"`
Expected: build errors (`PadSemanticSearch`, `NoteSearchService`, `SearchFeeder`, `SearchSettingsPanel` missing).

- [ ] **Step 3: AppConfig properties** — in `Models/SystemMetrics.cs`, add fields beside `_padWebImages` and properties after `PadWebImages`:

```csharp
        private bool _padSemanticSearch;
        private string _padEmbeddingServer = "";
        private string _padEmbeddingModel = "";
        private bool _padRerank;
        private string _padRerankServer = "";
        private string _padRerankModel = "";

        /// <summary>
        /// Search notes by meaning (search spec 2): passages of the notes are sent to
        /// <see cref="PadEmbeddingServer"/>. Off by default: keyword search needs no server and
        /// nothing leaves the PC until it is on. Turning it off deletes the stored vectors.
        /// </summary>
        public bool PadSemanticSearch { get => _padSemanticSearch; set { Set(ref _padSemanticSearch, value); } }

        /// <summary>The embedding server's base address (MicaPad adds <c>/embeddings</c>), normalized like the Kroki server; anything else reads as empty.</summary>
        public string PadEmbeddingServer
        {
            get => _padEmbeddingServer;
            set { Set(ref _padEmbeddingServer, Kil0bitSystemMonitor.Services.Pad.KrokiClient.TryParseServer(value, out var server) ? server : ""); }
        }

        /// <summary>The embedding model name sent with each request; empty sends none.</summary>
        public string PadEmbeddingModel { get => _padEmbeddingModel; set { Set(ref _padEmbeddingModel, (value ?? "").Trim()); } }

        /// <summary>Reorder results with <see cref="PadRerankServer"/>; only used while <see cref="PadSemanticSearch"/> is on.</summary>
        public bool PadRerank { get => _padRerank; set { Set(ref _padRerank, value); } }

        /// <summary>The reranker's base address (MicaPad adds <c>/rerank</c>), normalized; anything else reads as empty.</summary>
        public string PadRerankServer
        {
            get => _padRerankServer;
            set { Set(ref _padRerankServer, Kil0bitSystemMonitor.Services.Pad.KrokiClient.TryParseServer(value, out var server) ? server : ""); }
        }

        /// <summary>The rerank model name; empty sends none.</summary>
        public string PadRerankModel { get => _padRerankModel; set { Set(ref _padRerankModel, (value ?? "").Trim()); } }
```

- [ ] **Step 4: Secret names** — in `Services/Ai/AiSettings.cs`, inside `SecretNames`:

```csharp
        /// <summary>MicaPad's embedding server key (Settings → MicaPad → Search).</summary>
        public const string PadEmbeddingKey = "pad-embedding-key";

        /// <summary>MicaPad's reranker key.</summary>
        public const string PadRerankKey = "pad-rerank-key";
```

- [ ] **Step 5: Workspace events** — in `Services/Pad/PadWorkspace.cs`:

```csharp
        /// <summary>A note's text or title changed (search indexing listens). Raised on the UI thread.</summary>
        public event Action<OpenNote>? NoteTextChanged;

        /// <summary>A closed note was deleted (search indexing listens). Raised on the UI thread.</summary>
        public event Action<string>? NoteDeleted;
```

Raise `NoteTextChanged?.Invoke(note);` as the last line of `NotifyChanged` (after `_scheduler.MarkChanged(...)`) and of `Rename` (after `EnqueueSave(note)`). Change `DeleteClosed` to:

```csharp
            _recentlyClosed.Remove(id);
            if (!_store.DeleteNote(id, _recycleBin)) return false;
            NoteDeleted?.Invoke(id);
            return true;
```

- [ ] **Step 6: Write `Services/Pad/Search/NoteSearchService.cs`**

```csharp
using System;
using System.IO;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Ai;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>Settings → MicaPad → Search as <see cref="SearchSettings"/>; keys come from the secret store.</summary>
    public static class PadSearchSettings
    {
        public static SearchSettings From(AppConfig config, Func<string, string?> secret)
        {
            SearchServer? embedding = config.PadEmbeddingServer.Length == 0 ? null
                : new SearchServer(config.PadEmbeddingServer, config.PadEmbeddingModel, secret(SecretNames.PadEmbeddingKey));
            SearchServer? reranker = config.PadRerankServer.Length == 0 ? null
                : new SearchServer(config.PadRerankServer, config.PadRerankModel, secret(SecretNames.PadRerankKey));
            return new SearchSettings(config.PadSemanticSearch, embedding, config.PadRerank, reranker);
        }

        /// <summary>True for the config properties that change how search runs.</summary>
        public static bool IsSearchProperty(string? propertyName) => propertyName is
            nameof(AppConfig.PadSemanticSearch) or nameof(AppConfig.PadEmbeddingServer) or nameof(AppConfig.PadEmbeddingModel)
            or nameof(AppConfig.PadRerank) or nameof(AppConfig.PadRerankServer) or nameof(AppConfig.PadRerankModel);
    }

    /// <summary>
    /// MicaPad's search, one per app (spec 3.5): the vectors beside the notes in <c>search\</c>,
    /// the indexer and the query pipeline, sharing the two clients.
    /// </summary>
    public sealed class NoteSearchService : IDisposable
    {
        private readonly IDisposable? _ownedEmbedder;
        private readonly IDisposable? _ownedReranker;

        public NoteSearchService(NoteStore store, Func<SearchSettings> settings, IEmbedder? embedder = null, IReranker? reranker = null, Action<string>? warn = null)
        {
            warn ??= message => DiagnosticsLog.Warn("search", message);
            if (embedder == null) { var client = new EmbeddingClient(); embedder = client; _ownedEmbedder = client; }
            if (reranker == null) { var client = new RerankClient(); reranker = client; _ownedReranker = client; }

            Settings = settings;
            var vectors = new VectorStore(Path.Combine(store.Root, "search"), store.EncryptBytes, store.TryDecryptBytes, warn);
            Indexer = new SearchIndexer(vectors, embedder, settings, store.LoadText, warn);
            Search = new NoteSearch(Indexer, embedder, reranker, settings);
        }

        public Func<SearchSettings> Settings { get; }

        public SearchIndexer Indexer { get; }

        public NoteSearch Search { get; }

        public void Dispose()
        {
            Indexer.Dispose();
            _ownedEmbedder?.Dispose();
            _ownedReranker?.Dispose();
        }
    }
}
```

- [ ] **Step 7: Write `Pad/SearchFeeder.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Search;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Hands the notes to the search indexer (spec 3.5): every note at start and on
    /// <see cref="ReconcileAll"/>, an open note's text 2 s after its last change (read on the UI
    /// thread, where <see cref="OpenNote.TextProvider"/> lives), and deletions at once.
    /// </summary>
    internal sealed class SearchFeeder : IDisposable
    {
        public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(2);

        private readonly PadWorkspace _workspace;
        private readonly SearchIndexer _indexer;
        private readonly TimeSpan _debounce;
        private readonly Dictionary<string, DispatcherTimer> _timers = new(StringComparer.Ordinal);

        public SearchFeeder(PadWorkspace workspace, SearchIndexer indexer, TimeSpan? debounce = null)
        {
            _workspace = workspace;
            _indexer = indexer;
            _debounce = debounce ?? DefaultDebounce;
            _workspace.NoteTextChanged += OnTextChanged;
            _workspace.NoteDeleted += OnDeleted;
            ReconcileAll();
        }

        /// <summary>Every note again: open ones from their editors, the rest from the store; vanished ones removed.</summary>
        public void ReconcileAll()
        {
            var metas = _workspace.Store.LoadAllMetas();
            var open = _workspace.Open.ToDictionary(n => n.Id, StringComparer.Ordinal);
            _indexer.Reconcile(metas.Select(m => m.Id).Concat(open.Keys).Distinct().ToList());
            foreach (var note in open.Values) Send(note);
            foreach (var meta in metas.Where(m => !open.ContainsKey(m.Id)))
                _indexer.IndexStored(meta.Id, meta.Title, meta.ModifiedUtc);
        }

        /// <summary>Sends every note still waiting for its debounce now (tests, and before a search).</summary>
        public void FlushPending()
        {
            foreach (string id in _timers.Keys.ToList()) Fire(id);
        }

        private void OnTextChanged(OpenNote note)
        {
            if (!_timers.TryGetValue(note.Id, out var timer))
            {
                timer = new DispatcherTimer { Interval = _debounce };
                string id = note.Id;
                timer.Tick += (_, _) => Fire(id);
                _timers[note.Id] = timer;
            }
            timer.Stop();
            timer.Start();
        }

        private void Fire(string id)
        {
            if (_timers.Remove(id, out var timer)) timer.Stop();
            var note = _workspace.Open.FirstOrDefault(n => n.Id == id);
            if (note != null) Send(note);
        }

        private void Send(OpenNote note) =>
            _indexer.SetNote(note.Id, note.Title, note.TextProvider(), note.Meta.ModifiedUtc);

        private void OnDeleted(string id)
        {
            if (_timers.Remove(id, out var timer)) timer.Stop();
            _indexer.RemoveNote(id);
        }

        public void Dispose()
        {
            _workspace.NoteTextChanged -= OnTextChanged;
            _workspace.NoteDeleted -= OnDeleted;
            foreach (var timer in _timers.Values) timer.Stop();
            _timers.Clear();
        }
    }
}
```

- [ ] **Step 8: Write `Pad/SearchSettingsHost.cs`**

```csharp
using System;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad.Search;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>What Settings → MicaPad → Search reads and writes, handed in so tests build the panel over temp stores.</summary>
    public sealed class SearchSettingsHost
    {
        public required AppConfig Config { get; init; }

        /// <summary>Writes the config to disk now.</summary>
        public required Action Save { get; init; }

        /// <summary>Where the two keys go; never the config.</summary>
        public required SecretStore Secrets { get; init; }

        /// <summary>The running search, for the index line and Rebuild; null before MicaPad first opens.</summary>
        public Func<NoteSearchService?> Service { get; init; } = () => null;

        /// <summary>For the Test buttons.</summary>
        public IEmbedder Embedder { get; init; } = new EmbeddingClient();

        public IReranker Reranker { get; init; } = new RerankClient();
    }
}
```

- [ ] **Step 9: Write `Pad/SearchSettingsPanel.xaml`** — modeled on `Ai/AiSettingsPanel.xaml` (same ModernWpf `ui:` namespace, the same row layout of title/description on the left and control on the right; open that file and copy its namespace declarations and the key Save/Saved/Remove block exactly):

```xml
<UserControl x:Class="Kil0bitSystemMonitor.Pad.SearchSettingsPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ui="http://schemas.modernwpf.com/2019">
    <!-- Settings → MicaPad → Search (search spec 2). Keys go to the secret store and are never shown again. -->
    <StackPanel>
        <Grid Margin="0,8">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="16" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <StackPanel>
                <TextBlock Text="Search by meaning" FontWeight="SemiBold" FontSize="15" />
                <TextBlock Opacity="0.6" FontSize="12.5" TextWrapping="Wrap"
                           Text="Sends passages of your notes to the embedding server below to find notes by meaning. Keyword search works without it. Turning this off deletes the stored vectors." />
            </StackPanel>
            <ui:ToggleSwitch Grid.Column="2" x:Name="MeaningToggle" Toggled="OnMeaningToggled" VerticalAlignment="Center" />
        </Grid>

        <StackPanel x:Name="EmbeddingFields" Margin="0,0,0,8">
            <TextBlock Text="Embedding server" FontSize="12.5" Opacity="0.8" />
            <TextBox x:Name="EmbeddingServerBox" Width="300" HorizontalAlignment="Left" LostFocus="OnEmbeddingServerChanged" />
            <TextBlock Text="Model" FontSize="12.5" Opacity="0.8" Margin="0,6,0,0" />
            <TextBox x:Name="EmbeddingModelBox" Width="300" HorizontalAlignment="Left" LostFocus="OnEmbeddingModelChanged" />
            <TextBlock Text="API key" FontSize="12.5" Opacity="0.8" Margin="0,6,0,0" />
            <StackPanel x:Name="EmbeddingKeyEntry" Orientation="Horizontal">
                <PasswordBox x:Name="EmbeddingKeyBox" Width="240" />
                <Button Content="Save" Margin="8,0,0,0" Click="OnSaveEmbeddingKey" />
            </StackPanel>
            <StackPanel x:Name="EmbeddingKeySaved" Orientation="Horizontal" Visibility="Collapsed">
                <TextBlock Text="Saved" VerticalAlignment="Center" Opacity="0.8" />
                <Button Content="Remove" Margin="8,0,0,0" Click="OnRemoveEmbeddingKey" />
            </StackPanel>
            <StackPanel Orientation="Horizontal" Margin="0,6,0,0">
                <Button x:Name="EmbeddingTestButton" Content="Test" Click="OnTestEmbedding" />
                <TextBlock x:Name="EmbeddingTestResult" Margin="8,0,0,0" VerticalAlignment="Center" FontSize="12.5" TextWrapping="Wrap" />
            </StackPanel>
        </StackPanel>

        <Grid Margin="0,8">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="16" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <StackPanel>
                <TextBlock Text="Rerank results" FontWeight="SemiBold" FontSize="15" />
                <TextBlock Opacity="0.6" FontSize="12.5" TextWrapping="Wrap"
                           Text="Sends your query and the best passages to the reranker below to put the most relevant first. Needs Search by meaning." />
            </StackPanel>
            <ui:ToggleSwitch Grid.Column="2" x:Name="RerankToggle" Toggled="OnRerankToggled" VerticalAlignment="Center" />
        </Grid>

        <StackPanel x:Name="RerankFields" Margin="0,0,0,8">
            <TextBlock Text="Reranker server" FontSize="12.5" Opacity="0.8" />
            <TextBox x:Name="RerankServerBox" Width="300" HorizontalAlignment="Left" LostFocus="OnRerankServerChanged" />
            <TextBlock Text="Model" FontSize="12.5" Opacity="0.8" Margin="0,6,0,0" />
            <TextBox x:Name="RerankModelBox" Width="300" HorizontalAlignment="Left" LostFocus="OnRerankModelChanged" />
            <TextBlock Text="API key" FontSize="12.5" Opacity="0.8" Margin="0,6,0,0" />
            <StackPanel x:Name="RerankKeyEntry" Orientation="Horizontal">
                <PasswordBox x:Name="RerankKeyBox" Width="240" />
                <Button Content="Save" Margin="8,0,0,0" Click="OnSaveRerankKey" />
            </StackPanel>
            <StackPanel x:Name="RerankKeySaved" Orientation="Horizontal" Visibility="Collapsed">
                <TextBlock Text="Saved" VerticalAlignment="Center" Opacity="0.8" />
                <Button Content="Remove" Margin="8,0,0,0" Click="OnRemoveRerankKey" />
            </StackPanel>
            <StackPanel Orientation="Horizontal" Margin="0,6,0,0">
                <Button x:Name="RerankTestButton" Content="Test" Click="OnTestRerank" />
                <TextBlock x:Name="RerankTestResult" Margin="8,0,0,0" VerticalAlignment="Center" FontSize="12.5" TextWrapping="Wrap" />
            </StackPanel>
        </StackPanel>

        <TextBlock x:Name="ServerHint" Opacity="0.6" FontSize="12.5" TextWrapping="Wrap" />
        <TextBlock x:Name="KeyHint" Opacity="0.6" FontSize="12.5" TextWrapping="Wrap" Visibility="Collapsed" />

        <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
            <TextBlock x:Name="IndexStatus" VerticalAlignment="Center" FontSize="12.5" Opacity="0.8" TextWrapping="Wrap" MaxWidth="360" />
            <Button x:Name="RebuildButton" Content="Rebuild index" Margin="12,0,0,0" Click="OnRebuild" />
        </StackPanel>
    </StackPanel>
</UserControl>
```

- [ ] **Step 10: Write `Pad/SearchSettingsPanel.xaml.cs`**

```csharp
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Search;

using UserControl = System.Windows.Controls.UserControl;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Settings → MicaPad → Search (search spec 2). Every change is written to the config and saved
    /// at once; App reacts to the changed <c>Pad*</c> search properties. Keys go to the secret store.
    /// </summary>
    public partial class SearchSettingsPanel : UserControl
    {
        private const string ServerHelp = "Base addresses like http://gpu:8000/v1. MicaPad adds /embeddings and /rerank. Changing the embedding server or model makes the vectors again.";

        private SearchSettingsHost? _host;
        private bool _loading;
        private int _testRun;

        public SearchSettingsPanel()
        {
            InitializeComponent();
        }

        public void Load(SearchSettingsHost host)
        {
            _host = host;
            _loading = true;
            try
            {
                var cfg = host.Config;
                MeaningToggle.IsOn = cfg.PadSemanticSearch;
                EmbeddingServerBox.Text = cfg.PadEmbeddingServer;
                EmbeddingModelBox.Text = cfg.PadEmbeddingModel;
                RerankToggle.IsOn = cfg.PadRerank;
                RerankServerBox.Text = cfg.PadRerankServer;
                RerankModelBox.Text = cfg.PadRerankModel;
                ServerHint.Text = ServerHelp;
                RefreshKeys();
                RefreshEnabled();
                RefreshIndex();
            }
            finally
            {
                _loading = false;
            }
        }

        private void Changed()
        {
            _host!.Save();
            _host.Service()?.Indexer.SettingsChanged();
            RefreshEnabled();
            RefreshIndex();
        }

        private void RefreshEnabled() => RerankToggle.IsEnabled = MeaningToggle.IsOn;

        /// <summary>The index line, from the running search; called on load and after changes.</summary>
        public void RefreshIndex()
        {
            var service = _host?.Service();
            IndexStatus.Text = service == null
                ? "The index is made when MicaPad opens."
                : SearchStatusText.Index(service.Indexer.Progress, service.Settings());
            RebuildButton.IsEnabled = service != null;
        }

        private void OnMeaningToggled(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            CommitServer(EmbeddingServerBox, () => _host.Config.PadEmbeddingServer, v => _host.Config.PadEmbeddingServer = v);
            _host.Config.PadSemanticSearch = MeaningToggle.IsOn;
            Changed();
        }

        private void OnRerankToggled(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            CommitServer(RerankServerBox, () => _host.Config.PadRerankServer, v => _host.Config.PadRerankServer = v);
            _host.Config.PadRerank = RerankToggle.IsOn;
            Changed();
        }

        private void OnEmbeddingServerChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            if (CommitServer(EmbeddingServerBox, () => _host.Config.PadEmbeddingServer, v => _host.Config.PadEmbeddingServer = v)) Changed();
        }

        private void OnRerankServerChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            if (CommitServer(RerankServerBox, () => _host.Config.PadRerankServer, v => _host.Config.PadRerankServer = v)) Changed();
        }

        /// <summary>
        /// Takes a server box into the config: an http(s) address is kept without its trailing slash,
        /// an empty box clears it, anything else is refused and the box shows the server in use again.
        /// True when the server changed.
        /// </summary>
        private bool CommitServer(TextBox box, Func<string> current, Action<string> set)
        {
            string typed = box.Text.Trim();
            if (typed == current()) return false;
            if (typed.Length == 0)
            {
                set("");
                ServerHint.Text = ServerHelp;
                return true;
            }
            if (!KrokiClient.TryParseServer(typed, out var server))
            {
                box.Text = current();
                ServerHint.Text = "Not a server address. Use http:// or https:// and a host name, like http://gpu:8000/v1.";
                return false;
            }
            box.Text = server;
            ServerHint.Text = ServerHelp;
            if (server == current()) return false;
            set(server);
            return true;
        }

        private void OnEmbeddingModelChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            string model = EmbeddingModelBox.Text.Trim();
            EmbeddingModelBox.Text = model;
            if (model == _host.Config.PadEmbeddingModel) return;
            _host.Config.PadEmbeddingModel = model;
            Changed();
        }

        private void OnRerankModelChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            string model = RerankModelBox.Text.Trim();
            RerankModelBox.Text = model;
            if (model == _host.Config.PadRerankModel) return;
            _host.Config.PadRerankModel = model;
            Changed();
        }

        // ---- keys: the same rules as Settings > AI ----

        private void OnSaveEmbeddingKey(object sender, RoutedEventArgs e) => SaveEmbeddingKey();

        private void OnSaveRerankKey(object sender, RoutedEventArgs e) => SaveKey(SecretNames.PadRerankKey, RerankKeyBox);

        /// <summary>The embedding key's Save button (tests call it directly).</summary>
        internal void SaveEmbeddingKey() => SaveKey(SecretNames.PadEmbeddingKey, EmbeddingKeyBox);

        private void OnRemoveEmbeddingKey(object sender, RoutedEventArgs e) => RemoveKey(SecretNames.PadEmbeddingKey);

        private void OnRemoveRerankKey(object sender, RoutedEventArgs e) => RemoveKey(SecretNames.PadRerankKey);

        private void SaveKey(string name, PasswordBox box)
        {
            if (_host == null) return;
            string key = box.Password.Trim();
            if (key.Length == 0)
            {
                box.Clear();
                ShowKeyHint("Paste the key into the box first, then press Save.");
                return;
            }
            try
            {
                _host.Secrets.Set(name, key);
            }
            catch (Exception ex)
            {
                ShowKeyHint("The key could not be stored (" + ex.GetType().Name + "). It is still in the box; press Save to try again.");
                return;
            }
            if (!string.Equals(_host.Secrets.Get(name), key, StringComparison.Ordinal))
            {
                ShowKeyHint("The key could not be stored. It is still in the box; press Save to try again.");
                return;
            }
            box.Clear();
            ShowKeyHint("Saved. The key is stored encrypted for your Windows account and is not shown again.");
            RefreshKeys();
            _host.Service()?.Indexer.SettingsChanged();   // a waiting retry tries the new key now
        }

        private void RemoveKey(string name)
        {
            if (_host == null) return;
            _host.Secrets.Remove(name);
            ShowKeyHint(!_host.Secrets.CanRead() || _host.Secrets.Has(name)
                ? "The key could not be removed. The file that holds it is not available; try again."
                : "Removed.");
            RefreshKeys();
        }

        private void ShowKeyHint(string text)
        {
            KeyHint.Text = text;
            KeyHint.Visibility = Visibility.Visible;
        }

        private void RefreshKeys()
        {
            if (_host == null) return;
            bool embedding = _host.Secrets.Has(SecretNames.PadEmbeddingKey);
            EmbeddingKeyEntry.Visibility = embedding ? Visibility.Collapsed : Visibility.Visible;
            EmbeddingKeySaved.Visibility = embedding ? Visibility.Visible : Visibility.Collapsed;
            bool rerank = _host.Secrets.Has(SecretNames.PadRerankKey);
            RerankKeyEntry.Visibility = rerank ? Visibility.Collapsed : Visibility.Visible;
            RerankKeySaved.Visibility = rerank ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- Test and Rebuild ----

        private async void OnTestEmbedding(object sender, RoutedEventArgs e)
        {
            if (_host == null) return;
            CommitServer(EmbeddingServerBox, () => _host.Config.PadEmbeddingServer, v => _host.Config.PadEmbeddingServer = v);
            var server = PadSearchSettings.From(_host.Config, _host.Secrets.Get).Embedding;
            if (server == null) { EmbeddingTestResult.Text = "Enter the server address first."; return; }

            int run = ++_testRun;
            EmbeddingTestResult.Text = "Testing…";
            var result = await _host.Embedder.EmbedAsync(server, new[] { "MicaPad test" }, TimeSpan.FromSeconds(15), default);
            if (run != _testRun) return;
            EmbeddingTestResult.Text = result.Vectors != null
                ? "OK: " + result.Vectors[0].Length + " dimensions"
                : "The server " + SearchFailureText.Describe(result.Failure, result.Status) + ".";
        }

        private async void OnTestRerank(object sender, RoutedEventArgs e)
        {
            if (_host == null) return;
            CommitServer(RerankServerBox, () => _host.Config.PadRerankServer, v => _host.Config.PadRerankServer = v);
            var server = PadSearchSettings.From(_host.Config, _host.Secrets.Get).Reranker;
            if (server == null) { RerankTestResult.Text = "Enter the server address first."; return; }

            int run = ++_testRun;
            RerankTestResult.Text = "Testing…";
            var result = await _host.Reranker.RerankAsync(server, "test", new[] { "test", "other" }, 2, TimeSpan.FromSeconds(15), default);
            if (run != _testRun) return;
            RerankTestResult.Text = result.Ranked != null
                ? "OK"
                : "The server " + SearchFailureText.Describe(result.Failure, result.Status) + ".";
        }

        private void OnRebuild(object sender, RoutedEventArgs e)
        {
            _host?.Service()?.Indexer.Rebuild();
            IndexStatus.Text = "Rebuilding…";
        }
    }
}
```

- [ ] **Step 11: Settings window** — in `SettingsWindow.xaml`, at the end of `PadSection` (after the history-days row, before the section's closing tag), add a heading and the panel in the same style as the neighbouring groups (copy the group header markup used above the Kroki row):

```xml
                        <TextBlock Text="Search" FontSize="18" FontWeight="SemiBold" Margin="0,24,0,4"/>
                        <pad:SearchSettingsPanel x:Name="PadSearchPanel"/>
```

Declare `xmlns:pad="clr-namespace:Kil0bitSystemMonitor.Pad"` on the window if it is not declared yet. In `SettingsWindow.xaml.cs`, at the end of the `try` in `LoadPadSettings`:

```csharp
                PadSearchPanel.Load(new Kil0bitSystemMonitor.Pad.SearchSettingsHost
                {
                    Config = _config.Config,
                    Save = _config.SaveConfig,
                    Secrets = App.AiSecrets,
                    Service = () => App.PadSearch,
                });
```

- [ ] **Step 12: App wiring** — in `App.xaml.cs`:

```csharp
        private static Kil0bitSystemMonitor.Services.Pad.Search.NoteSearchService? s_padSearch;
        private static Kil0bitSystemMonitor.Pad.SearchFeeder? s_padSearchFeeder;

        /// <summary>MicaPad's search; null until MicaPad first opens.</summary>
        internal static Kil0bitSystemMonitor.Services.Pad.Search.NoteSearchService? PadSearch => s_padSearch;
```

In `OpenPad`, after the `s_images` block and before `MicaPadWindow.Open(...)`:

```csharp
                if (s_padSearch == null)
                {
                    s_padSearch = new Kil0bitSystemMonitor.Services.Pad.Search.NoteSearchService(PadStore,
                        () => Kil0bitSystemMonitor.Services.Pad.Search.PadSearchSettings.From(config, AiSecrets.Get));
                    config.PropertyChanged += (_, e) =>
                    {
                        if (Kil0bitSystemMonitor.Services.Pad.Search.PadSearchSettings.IsSearchProperty(e.PropertyName))
                            s_padSearch.Indexer.SettingsChanged();
                    };
                    s_padSearchFeeder = new Kil0bitSystemMonitor.Pad.SearchFeeder(s_pad, s_padSearch.Indexer);
                    Kil0bitSystemMonitor.Pad.MicaPadWindow.SearchService = s_padSearch;   // the Search notes pane (Task 8)
                    Kil0bitSystemMonitor.Pad.MicaPadWindow.SearchFeeder = s_padSearchFeeder;
                }
```

(`MicaPadWindow.SearchService` / `SearchFeeder` are added in Task 8. Until then, leave those two lines out; Task 8 adds them back.) Find where App disposes the MicaPad workspace at exit (search `s_pad?.Dispose` or the app's `OnExit`) and add, before the workspace is disposed:

```csharp
            s_padSearchFeeder?.Dispose();
            s_padSearch?.Dispose();
```

- [ ] **Step 13: Run the tests to verify they pass, then the whole suite**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~SearchSettingsTests"` then the full suite without a filter.
Expected: all pass; no new warnings.

- [ ] **Step 14: Commit**

```bash
git add Models/SystemMetrics.cs Services/Ai/AiSettings.cs Services/Pad/PadWorkspace.cs Services/Pad/Search/NoteSearchService.cs Pad/SearchFeeder.cs Pad/SearchSettingsHost.cs Pad/SearchSettingsPanel.xaml Pad/SearchSettingsPanel.xaml.cs SettingsWindow.xaml SettingsWindow.xaml.cs App.xaml.cs tests/Kil0bitSystemMonitor.Tests/SearchSettingsTests.cs
git commit -m "feat(pad-search): Settings > MicaPad > Search, config and keys, the app-wide service and its feeder"
```

---

### Task 8: The Search notes pane, the window, and the guide

**Files:**
- Create: `Pad/SearchPane.xaml`, `Pad/SearchPane.xaml.cs`
- Modify: `Pad/MicaPadWindow.xaml` (toolbar button; pane beside `HistoryPanel`)
- Modify: `Pad/MicaPadWindow.xaml.cs` (statics, Ctrl+Shift+F, toggle, open a result, History closes Search)
- Modify: `App.xaml.cs` (the two `MicaPadWindow.Search*` assignments from Task 7 Step 12)
- Modify: `GUIDE.md` (MicaPad section), `README.md` (MicaPad bullets, EN and TH)
- Test: `tests/Kil0bitSystemMonitor.Tests/SearchPaneTests.cs`

**Interfaces:**
- Consumes: `NoteSearchService`, `SearchFeeder`, `SearchOutcome`, `SearchStatusText`, `SearchSnippet`, `SearchLocate`, `PadWorkspace.Reopen(id, windowId)`, `MicaPadWindow.ShowNote` (private), `Registered(workspace, windowId)`.
- Produces:
  - `public sealed record SearchRow(string NoteId, string Title, bool Closed, int FirstLine, int LastLine, string FirstLineText, IReadOnlyList<SnippetRun> Snippet) { string Where; }`
  - `public partial class SearchPane : UserControl { event Action<SearchRow>? ResultChosen; event Action? CloseRequested; Func<string, CancellationToken, Task<(IReadOnlyList<SearchRow> Rows, string Status)>>? Run; void Open(string? query); IReadOnlyList<SearchRow> Rows; Task SearchNow(); }`
  - `MicaPadWindow`: `internal static NoteSearchService? SearchService { get; set; }`, `internal static SearchFeeder? SearchFeeder { get; set; }`, `internal void ToggleSearch()`, `internal void OpenSearchResult(SearchRow row)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Search;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class SearchPaneTests
    {
        private static void WithWindow(Action<MicaPadWindow, PadTestEnv, NoteSearchService> test) => UiThread.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
            using var service = new NoteSearchService(env.Store, () => SearchSettings.Off);
            var originalService = MicaPadWindow.SearchService;
            MicaPadWindow.SearchService = service;
            var window = new MicaPadWindow(env.Workspace, new AppConfig());
            try
            {
                window.LoadSession();
                test(window, env, service);
            }
            finally
            {
                window.CloseForExit();
                MicaPadWindow.SearchService = originalService;
            }
        });

        private static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        private static T Wait<T>(Task<T> task)
        {
            while (!task.IsCompleted) Pump();
            return task.Result;
        }

        private static void Wait(Task task)
        {
            while (!task.IsCompleted) Pump();
            task.GetAwaiter().GetResult();
        }

        [Fact]
        public void Ctrl_Shift_F_opens_the_pane_and_closes_History() => WithWindow((window, env, service) =>
        {
            window.ToggleHistoryForTest();
            Assert.Equal(Visibility.Visible, window.HistoryPanel.Visibility);

            window.ToggleSearch();

            Assert.Equal(Visibility.Visible, window.SearchPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, window.HistoryPanel.Visibility);

            window.ToggleSearch();
            Assert.Equal(Visibility.Collapsed, window.SearchPanel.Visibility);
        });

        [Fact]
        public void Opening_History_closes_Search() => WithWindow((window, env, service) =>
        {
            window.ToggleSearch();
            window.ToggleHistoryForTest();
            Assert.Equal(Visibility.Collapsed, window.SearchPanel.Visibility);
        });

        [Fact]
        public void A_search_lists_matching_notes_with_their_line() => WithWindow((window, env, service) =>
        {
            var note = env.Workspace.Open.First();
            window.Editor.Document.Text = "# Intro\nfirst\n\n# Net\nthe vpn does not connect";   // headings make two passages
            service.Indexer.SetNote(note.Id, note.Title, window.Editor.Document.Text, DateTime.UtcNow);
            Wait(service.Indexer.WhenIdle());

            window.ToggleSearch();
            window.SearchPanel.QueryBox.Text = "vpn";
            Wait(window.SearchPanel.SearchNow());

            var row = Assert.Single(window.SearchPanel.Rows);
            Assert.Equal(note.Id, row.NoteId);
            Assert.Equal(4, row.FirstLine);
            Assert.False(row.Closed);
            Assert.Equal("Words", window.SearchPanel.StatusText.Text);
        });

        [Fact]
        public void Picking_a_closed_note_reopens_it_with_the_passage_selected() => WithWindow((window, env, service) =>
        {
            var note = env.Workspace.NewNote();
            window.ShowNoteForTest(note);
            window.Editor.Document.Text = "one\ntwo\n\n# Part\nneedle in here\nafter";
            env.Workspace.NotifyChanged(note);
            env.Workspace.FlushAll(TimeSpan.FromSeconds(2));
            string id = note.Id;
            window.CloseTabForTest(note);
            env.Workspace.FlushAll(TimeSpan.FromSeconds(2));
            service.Indexer.IndexStored(id, "t", DateTime.UtcNow);
            Wait(service.Indexer.WhenIdle());

            window.ToggleSearch();
            window.SearchPanel.QueryBox.Text = "needle";
            Wait(window.SearchPanel.SearchNow());
            var row = Assert.Single(window.SearchPanel.Rows);
            Assert.True(row.Closed);

            window.OpenSearchResult(row);

            Assert.Contains(env.Workspace.Open, n => n.Id == id);
            Assert.Equal("# Part\nneedle in here\nafter", window.Editor.SelectedText.Replace("\r\n", "\n"));
        });

        [Fact]
        public void The_selection_follows_the_text_when_lines_moved() => WithWindow((window, env, service) =>
        {
            var note = env.Workspace.Open.First();
            window.Editor.Document.Text = "# A\na\n# B\nneedle here";
            service.Indexer.SetNote(note.Id, note.Title, window.Editor.Document.Text, DateTime.UtcNow);
            Wait(service.Indexer.WhenIdle());
            window.ToggleSearch();
            window.SearchPanel.QueryBox.Text = "needle";
            Wait(window.SearchPanel.SearchNow());
            var row = Assert.Single(window.SearchPanel.Rows);

            window.Editor.Document.Insert(0, "new top line\n");   // the passage moved down one line
            window.OpenSearchResult(row);

            Assert.Equal("# B\nneedle here", window.Editor.SelectedText.Replace("\r\n", "\n"));
        });
    }
}
```

The tests use three small test hooks the implementer adds to `MicaPadWindow` as `internal` members (the window already exposes `LoadSession`, `CloseForExit` and named elements to tests): `ToggleHistoryForTest()` → `ToggleHistory()`, `ShowNoteForTest(OpenNote)` → `ShowNote(note)`, `CloseTabForTest(OpenNote)` → `CloseTab(note)`. If equivalent internal members already exist, use them instead and adjust the test names.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~SearchPaneTests"`
Expected: build errors (`SearchPanel`, `ToggleSearch`, `SearchService` missing).

- [ ] **Step 3: Write `Pad/SearchPane.xaml`** (modeled on `Pad/HistoryPane.xaml`: same width rules, `Pad.*` brushes so it follows the MicaPad theme)

```xml
<UserControl
    x:Class="Kil0bitSystemMonitor.Pad.SearchPane"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Width="300" Background="{DynamicResource Pad.Chrome}">

    <!-- Search notes (search spec 1): a query, how it ran, and the matching passages of every note.
         Colors are the MicaPad window's Pad.* brushes, so the pane follows its theme. -->

    <Border BorderBrush="{DynamicResource Pad.Border}" BorderThickness="1,0,0,0">
        <DockPanel>
            <DockPanel DockPanel.Dock="Top" Margin="12,8,6,8">
                <Button DockPanel.Dock="Right" Content="&#xE711;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets"
                        FontSize="10" Padding="8,6" ToolTip="Close (Ctrl+Shift+F)" Click="OnCloseClick" />
                <TextBlock Text="Search notes" FontSize="14" FontWeight="SemiBold" VerticalAlignment="Center" />
            </DockPanel>
            <TextBox x:Name="QueryBox" DockPanel.Dock="Top" Margin="12,0,12,4"
                     Background="{DynamicResource Pad.Background}" Foreground="{DynamicResource Pad.Text}"
                     BorderBrush="{DynamicResource Pad.Border}" CaretBrush="{DynamicResource Pad.Text}"
                     TextChanged="OnQueryChanged" PreviewKeyDown="OnQueryKeyDown" />
            <TextBlock x:Name="StatusText" DockPanel.Dock="Top" Margin="12,0,12,6" TextWrapping="Wrap"
                       Foreground="{DynamicResource Pad.Muted}" FontSize="11.5" />
            <ListBox x:Name="Results" Background="Transparent" BorderThickness="0"
                     ScrollViewer.HorizontalScrollBarVisibility="Disabled"
                     MouseLeftButtonUp="OnResultsClick" PreviewKeyDown="OnResultsKeyDown">
                <ListBox.ItemTemplate>
                    <DataTemplate>
                        <StackPanel Margin="4,4,4,6">
                            <DockPanel>
                                <TextBlock DockPanel.Dock="Right" Text="{Binding Where}" Foreground="{DynamicResource Pad.Muted}" FontSize="11" Margin="8,0,0,0" />
                                <TextBlock Text="{Binding Title}" FontSize="12.5" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
                            </DockPanel>
                            <TextBlock x:Name="Snippet" FontSize="12" TextWrapping="Wrap" Foreground="{DynamicResource Pad.TextSoft}" Margin="0,2,0,0"
                                       Loaded="OnSnippetLoaded" />
                        </StackPanel>
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>
        </DockPanel>
    </Border>
</UserControl>
```

- [ ] **Step 4: Write `Pad/SearchPane.xaml.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Services.Pad.Search;

using UserControl = System.Windows.Controls.UserControl;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>One result: where it is and what it says.</summary>
    public sealed record SearchRow(string NoteId, string Title, bool Closed, int FirstLine, int LastLine, string FirstLineText, IReadOnlyList<SnippetRun> Snippet)
    {
        /// <summary>"closed · line 12" or "line 12".</summary>
        public string Where => (Closed ? "closed · " : "") + "line " + FirstLine;
    }

    /// <summary>
    /// The Search notes pane (search spec 1): searches 300 ms after typing stops, Enter at once; a
    /// newer search cancels the older one. The window supplies <see cref="Run"/> and opens what is
    /// chosen.
    /// </summary>
    public partial class SearchPane : UserControl
    {
        public static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(300);

        private readonly DispatcherTimer _typing;
        private CancellationTokenSource? _running;

        public SearchPane()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;
            _typing = new DispatcherTimer { Interval = TypingPause };
            _typing.Tick += (_, _) => { _typing.Stop(); _ = SearchNow(); };
        }

        /// <summary>The user picked a result.</summary>
        public event Action<SearchRow>? ResultChosen;

        /// <summary>The user asked to close the pane.</summary>
        public event Action? CloseRequested;

        /// <summary>Esc in the query box: back to the editor.</summary>
        public event Action? ReturnRequested;

        /// <summary>Runs a query: rows and the status line. Set by the window.</summary>
        public Func<string, CancellationToken, Task<(IReadOnlyList<SearchRow> Rows, string Status)>>? Run { get; set; }

        public IReadOnlyList<SearchRow> Rows { get; private set; } = Array.Empty<SearchRow>();

        /// <summary>Shows the pane with the query box focused; <paramref name="query"/>, when given, replaces the query.</summary>
        public void Open(string? query)
        {
            Visibility = Visibility.Visible;
            if (query != null) QueryBox.Text = query;
            QueryBox.Focus();
            QueryBox.SelectAll();
            if (QueryBox.Text.Trim().Length > 0) _ = SearchNow();
        }

        /// <summary>Searches now, cancelling a search still running.</summary>
        public async Task SearchNow()
        {
            _typing.Stop();
            _running?.Cancel();
            var mine = _running = new CancellationTokenSource();
            string query = QueryBox.Text;

            if (query.Trim().Length == 0 || Run == null)
            {
                Show(Array.Empty<SearchRow>(), "");
                return;
            }

            try
            {
                var (rows, status) = await Run(query, mine.Token);
                if (!ReferenceEquals(mine, _running)) return;
                Show(rows, rows.Count == 0 ? status + " · No notes found" : status);
            }
            catch (OperationCanceledException)
            {
                // a newer search took over
            }
        }

        private void Show(IReadOnlyList<SearchRow> rows, string status)
        {
            Rows = rows;
            Results.ItemsSource = rows;
            StatusText.Text = status;
        }

        private void OnQueryChanged(object sender, TextChangedEventArgs e)
        {
            _typing.Stop();
            _typing.Start();
        }

        private void OnQueryKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { _ = SearchNow(); e.Handled = true; }
            else if (e.Key == Key.Down && Rows.Count > 0) { Results.SelectedIndex = 0; FocusSelected(); e.Handled = true; }
            else if (e.Key == Key.Escape) { ReturnRequested?.Invoke(); e.Handled = true; }
        }

        private void OnResultsKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Results.SelectedItem is SearchRow row) { ResultChosen?.Invoke(row); e.Handled = true; }
            else if (e.Key == Key.Escape) { QueryBox.Focus(); e.Handled = true; }
        }

        private void OnResultsClick(object sender, MouseButtonEventArgs e)
        {
            if (Results.SelectedItem is SearchRow row) ResultChosen?.Invoke(row);
        }

        private void FocusSelected()
        {
            Results.UpdateLayout();
            (Results.ItemContainerGenerator.ContainerFromIndex(Results.SelectedIndex) as ListBoxItem)?.Focus();
        }

        private void OnSnippetLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBlock block || block.DataContext is not SearchRow row) return;
            block.Inlines.Clear();
            foreach (var run in row.Snippet)
                block.Inlines.Add(run.Bold ? new Bold(new Run(run.Text)) : new Run(run.Text));
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    }
}
```

- [ ] **Step 5: The window XAML** — in `Pad/MicaPadWindow.xaml`:
  - Toolbar, before `ClosedNotesButton`:

```xml
            <Button x:Name="SearchButton" DockPanel.Dock="Right" Style="{StaticResource GlyphButton}"
                    Content="&#xE721;" ToolTip="Search notes (Ctrl+Shift+F)" Click="OnSearchButtonClick" Margin="0,4,0,0" />
```

  - Right after `<local:HistoryPane x:Name="HistoryPanel" Grid.Column="1" />`:

```xml
            <local:SearchPane x:Name="SearchPanel" Grid.Column="1" />
```

- [ ] **Step 6: The window code** — in `Pad/MicaPadWindow.xaml.cs`:

  - Statics (beside `DiagramRenderer`):

```csharp
        /// <summary>MicaPad's search, set by App when MicaPad first opens; null leaves the pane with words disabled.</summary>
        internal static Kil0bitSystemMonitor.Services.Pad.Search.NoteSearchService? SearchService { get; set; }

        /// <summary>Hands notes to the indexer; the pane reconciles through it each time it opens.</summary>
        internal static SearchFeeder? SearchFeeder { get; set; }
```

  - In the constructor, where `HistoryPanel.CloseRequested += CloseHistory;` is wired:

```csharp
            SearchPanel.CloseRequested += CloseSearch;
            SearchPanel.ReturnRequested += () => Editor.Focus();
            SearchPanel.ResultChosen += OpenSearchResult;
            SearchPanel.Run = RunSearchAsync;
```

  - Key handling, beside `else if (ctrlShift && key == Key.H) ToggleHistory();`:

```csharp
            else if (ctrlShift && key == Key.F) ToggleSearch();
```

  - `ShowHistory()` gains a first line `if (SearchPanel.Visibility == Visibility.Visible) SearchPanel.Visibility = Visibility.Collapsed;`.
  - New members:

```csharp
        private void OnSearchButtonClick(object sender, RoutedEventArgs e) => ToggleSearch();

        /// <summary>Ctrl+Shift+F: opens the Search notes pane (closing History), or closes it.</summary>
        internal void ToggleSearch()
        {
            if (SearchPanel.Visibility == Visibility.Visible)
            {
                CloseSearch();
                return;
            }
            if (HistoryPanel.Visibility == Visibility.Visible)
            {
                EndPreview();
                HistoryPanel.Visibility = Visibility.Collapsed;
            }
            SearchFeeder?.FlushPending();
            SearchFeeder?.ReconcileAll();
            string selected = Editor.SelectedText;
            SearchPanel.Open(selected.Length > 0 && !selected.Contains('\n') ? selected.Trim() : null);
        }

        private void CloseSearch()
        {
            SearchPanel.Visibility = Visibility.Collapsed;
            Editor.Focus();
        }

        private async Task<(IReadOnlyList<SearchRow> Rows, string Status)> RunSearchAsync(string query, CancellationToken cancel)
        {
            var service = SearchService;
            if (service == null) return (Array.Empty<SearchRow>(), "Search is not ready yet.");

            var outcome = await service.Search.SearchAsync(query, cancel);
            var open = _workspace.Open.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            var rows = outcome.Hits.Select(p => new SearchRow(
                p.NoteId, p.Title, !open.Contains(p.NoteId), p.FirstLine, p.LastLine, p.FirstLineText,
                SearchSnippet.Make(p.Body, query))).ToList();
            return (rows, SearchStatusText.For(outcome, service.Settings()));
        }

        /// <summary>
        /// Shows the result's note and selects its passage: in this window, in the window that has
        /// it open (which comes forward), or reopened here when it was closed (search spec 1).
        /// </summary>
        internal void OpenSearchResult(SearchRow row)
        {
            var note = _workspace.Open.FirstOrDefault(n => n.Id == row.NoteId) ?? _workspace.Reopen(row.NoteId, _windowId);
            if (note == null)
            {
                ShowNotice("That note is no longer there.");
                SearchFeeder?.ReconcileAll();
                return;
            }

            var owner = note.WindowId == _windowId ? this : Registered(_workspace, note.WindowId);
            ShowNote(note);
            owner?.SelectPassage(row);
        }

        private void SelectPassage(SearchRow row)
        {
            var document = Editor.Document;
            int first = SearchLocate.FindLine(document.LineCount, n => document.GetText(document.GetLineByNumber(n)), row.FirstLine, row.FirstLineText);
            int last = Math.Clamp(first + (row.LastLine - row.FirstLine), first, document.LineCount);
            var start = document.GetLineByNumber(first);
            var end = document.GetLineByNumber(last);
            Editor.Select(start.Offset, end.EndOffset - start.Offset);
            Editor.ScrollToLine(first);
            Editor.Focus();
        }
```

  Use the window's existing notice method if it is named differently from `ShowNotice` (it is used by the "Explorer could not be opened" path). Add `using Kil0bitSystemMonitor.Services.Pad.Search;`, `using System.Threading;` and `using System.Threading.Tasks;` if missing. Add the three test hooks named in Step 1 as `internal` one-liners.

- [ ] **Step 7: App** — add the two assignments in `OpenPad` from Task 7 Step 12 (`MicaPadWindow.SearchService = s_padSearch;` and `MicaPadWindow.SearchFeeder = s_padSearchFeeder;`).

- [ ] **Step 8: Run the tests to verify they pass, then the whole suite**

Run: `DOTNET_CLI_TELEMETRY_OPTOUT=1 "$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" test tests/Kil0bitSystemMonitor.Tests --filter "FullyQualifiedName~SearchPaneTests"` then the full suite.
Expected: all pass; no new warnings.

- [ ] **Step 9: The guide and README**

  - `GUIDE.md`, MicaPad section, a new "Search notes" subsection: Ctrl+Shift+F or the magnifier; words work offline in every note, open or closed (Thai included); results show the note, its line and the passage; Enter or a click opens it with the passage selected; Settings → MicaPad → Search turns on **Search by meaning** with your own embedding server (OpenAI `/v1/embeddings` format — TEI, vLLM, Infinity, LiteLLM, Ollama `/v1`) and **Rerank results** with a reranker (Cohere/Jina `/rerank` format); what is sent (passages, the query; never credentials; only when on); keys are stored encrypted; the vectors are encrypted beside the notes and deleted when meaning search is turned off; the status line names a server problem and search falls back to words.
  - `README.md`: one MicaPad bullet in English and one in Thai, in the style of the existing MicaPad bullets: "Search every note by words (offline, Thai included) and, optionally, by meaning with your own embedding and reranker servers."

- [ ] **Step 10: Commit**

```bash
git add Pad/SearchPane.xaml Pad/SearchPane.xaml.cs Pad/MicaPadWindow.xaml Pad/MicaPadWindow.xaml.cs App.xaml.cs GUIDE.md README.md tests/Kil0bitSystemMonitor.Tests/SearchPaneTests.cs
git commit -m "feat(pad-search): the Search notes pane (Ctrl+Shift+F) opens results at their passage; guide and README"
```
