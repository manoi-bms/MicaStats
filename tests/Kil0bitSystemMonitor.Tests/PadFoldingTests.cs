using System.Linq;
using ICSharpCode.AvalonEdit.Folding;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Where text folds — braces, headings, fences — and the fold margin in the window.</summary>
    public class PadFoldingTests
    {
        private static string[] Folded(string text, System.Collections.Generic.IReadOnlyList<FoldRange> folds) =>
            folds.Select(f => text.Substring(f.Start, f.End - f.Start)).ToArray();

        [Fact]
        public void Multi_line_braces_fold_and_single_line_ones_do_not()
        {
            string json = "{\n  \"a\": [1, 2],\n  \"b\": [\n    3\n  ]\n}";
            var folds = BraceFolding.Compute(json, BraceSyntax.For("json"));
            Assert.Equal(new[] { json, "[\n    3\n  ]" }, Folded(json, folds));
        }

        [Fact]
        public void Braces_in_strings_and_comments_are_ignored()
        {
            string cs = "void F() {\n  var s = \"{\";\n  // {\n  /* {\n  */ char c = '{';\n}";
            var folds = BraceFolding.Compute(cs, BraceSyntax.For("csharp"));
            Assert.Single(folds);
            Assert.Equal(cs.IndexOf('{'), folds[0].Start);
            Assert.Equal(cs.Length, folds[0].End);
        }

        [Fact]
        public void PowerShell_block_comments_and_hash_comments_are_ignored()
        {
            string ps = "function F {\n  # {\n  <# {\n  #>\n}";
            var folds = BraceFolding.Compute(ps, BraceSyntax.For("powershell"));
            Assert.Single(folds);
        }

        [Fact]
        public void Mismatched_brackets_never_throw_and_folds_come_sorted()
        {
            string text = "{\n [\n }\n ]\n{\n{\n}\n}";
            var folds = BraceFolding.Compute(text, BraceSyntax.For("javascript"));
            Assert.Equal(folds.OrderBy(f => f.Start).ToList(), folds.ToList());
            Assert.Empty(BraceFolding.Compute("", BraceSyntax.For("json")));
            Assert.Empty(BraceFolding.Compute("\"unclosed {\n", BraceSyntax.For("json")));
        }

        [Fact]
        public void Headings_fold_to_the_next_heading_of_the_same_or_higher_level()
        {
            string md = "# A\ntext\n## B\nmore\n\n# C\nend";
            var folds = HeadingFolding.Compute(md);
            Assert.Equal(new[] { new FoldRange(3, 18), new FoldRange(13, 18), new FoldRange(23, 27) }, folds);
        }

        [Fact]
        public void A_heading_with_nothing_under_it_does_not_fold()
        {
            Assert.Empty(HeadingFolding.Compute("# A\n# B"));
            Assert.Empty(HeadingFolding.Compute("# A\n\n\n# B"));
        }

        [Fact]
        public void Fenced_blocks_fold_and_hide_their_hashes()
        {
            string md = "x\n```\n# not a heading\n```";
            var folds = HeadingFolding.Compute(md);
            Assert.Equal(new[] { new FoldRange(5, md.Length) }, folds);
        }

        [Fact]
        public void A_json_file_gets_a_fold_margin_and_plain_text_does_not() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            PadLanguageWindowTests.OpenFile(window, env, "a.json", "{\n  \"a\": [\n    1\n  ]\n}");
            var folding = window.LanguageView.Folding!;
            folding.Update();

            Assert.True(folding.IsActive);
            Assert.Equal(2, folding.Manager!.AllFoldings.Count());
            Assert.Single(window.Editor.TextArea.LeftMargins.OfType<FoldingMargin>());

            window.ChooseLanguage(env.Workspace.Active!, "plain");
            Assert.False(folding.IsActive);
            Assert.Empty(window.Editor.TextArea.LeftMargins.OfType<FoldingMargin>());
        });

        [Fact]
        public void Moving_the_caret_into_a_fold_opens_it() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            PadLanguageWindowTests.OpenFile(window, env, "a.json", "{\n  \"a\": 1,\n  \"b\": 2\n}");
            var folding = window.LanguageView.Folding!;
            folding.Update();
            var fold = folding.Manager!.AllFoldings.Single();
            fold.IsFolded = true;

            window.Editor.CaretOffset = window.Editor.Document.Text.IndexOf("\"b\"", System.StringComparison.Ordinal);

            Assert.False(fold.IsFolded);
        });

        [Fact]
        public void Switching_tabs_folds_the_new_document() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "# A\ntext\n# B\nmore";
            var folding = window.LanguageView.Folding!;
            folding.Update();
            Assert.Equal(2, folding.Manager!.AllFoldings.Count());

            PadLanguageWindowTests.OpenFile(window, env, "a.json", "{\n  \"a\": 1\n}");
            folding.Update();
            Assert.Single(folding.Manager!.AllFoldings);
        });
    }
}
