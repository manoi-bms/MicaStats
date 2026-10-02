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
        public void PowerShell_backtick_escapes_and_a_trailing_backslash_does_not_hide_a_brace()
        {
            string ps = "if (Test-Path \"C:\\Temp\\\") {\n  Write-Host \"x\"\n}";
            var folds = BraceFolding.Compute(ps, BraceSyntax.For("powershell"));
            Assert.Single(folds);
            Assert.Equal(ps.IndexOf('{'), folds[0].Start);
            Assert.Equal(ps.Length, folds[0].End);
        }

        [Fact]
        public void PowerShell_here_strings_hide_their_braces()
        {
            string ps = "$json = @\"\n{\n  \"a\": [\n    1\n  ]\n}\n\"@\nfunction F {\n  1\n}";
            var folds = BraceFolding.Compute(ps, BraceSyntax.For("powershell"));
            Assert.Equal(new[] { "{\n  1\n}" }, Folded(ps, folds));
            string crlf = ps.Replace("\n", "\r\n");
            Assert.Single(BraceFolding.Compute(crlf, BraceSyntax.For("powershell")));
        }

        [Fact]
        public void TypeScript_folds_like_JavaScript()
        {
            string ts = "const m = `don't`;\nif (x) {\n  y();\n}";
            Assert.Equal(new[] { "{\n  y();\n}" }, Folded(ts, BraceFolding.Compute(ts, BraceSyntax.For("typescript"))));
            Assert.Same(BraceSyntax.JavaScript, BraceSyntax.For("typescript"));
        }

        [Fact]
        public void Rust_lifetimes_do_not_swallow_braces()
        {
            string rs = "impl<'a> Parser<'a> {\n    pub fn new(input: &'a str) -> Self {\n        Self { input }\n    }\n}";
            var folds = Folded(rs, BraceFolding.Compute(rs, BraceSyntax.For("rust")));
            Assert.Equal(2, folds.Length);
            Assert.Equal(rs.Substring(rs.IndexOf('{')), folds[0]);
            Assert.StartsWith("{\n        Self", folds[1]);
        }

        /// <summary>A complete char literal hides its brace; a lifetime or a label is code, so the braces after it still count.</summary>
        [Theory]
        [InlineData("fn f() {\n    let brace = '}';\n}")]
        [InlineData("fn f() {\n    let brace = '{';\n}")]
        [InlineData("fn f() {\n    let b = b'{';\n}")]
        [InlineData("fn f() {\n    let q = '\\''; let c = '}';\n}")]
        [InlineData("fn f() {\n    let s = '\\\\'; let c = '{';\n}")]
        [InlineData("fn f() {\n    let e = '\\u{1F600}'; let c = '}';\n}")]
        [InlineData("fn f() {\n    let d = '\"'; let c = '{';\n}")]
        [InlineData("fn f<'a>(x: &'a str) -> &'a str {\n    'outer: loop { break 'outer; }\n    x\n}")]
        public void Rust_char_literals_hide_their_braces_and_lifetimes_stay_code(string rs)
        {
            var folds = Folded(rs, BraceFolding.Compute(rs, BraceSyntax.For("rust")));
            Assert.Equal(new[] { rs.Substring(rs.IndexOf('{')) }, folds);
        }

        [Fact]
        public void Go_raw_strings_may_span_lines_and_hide_braces()
        {
            string go = "var s = `{\nx\n}`\nfunc f() {\n\tx()\n}";
            Assert.Equal(new[] { "{\n\tx()\n}" }, Folded(go, BraceFolding.Compute(go, BraceSyntax.For("go"))));
            string rune = "r := '{'\nfunc f() {\n\tx()\n}";
            Assert.Single(BraceFolding.Compute(rune, BraceSyntax.For("go")));
        }

        [Fact]
        public void Kotlin_triple_quoted_strings_hide_their_braces()
        {
            string kt = "val s = \"\"\"\n{\n  a\n}\n\"\"\"\nfun f() {\n  x()\n}";
            Assert.Equal(new[] { "{\n  x()\n}" }, Folded(kt, BraceFolding.Compute(kt, BraceSyntax.For("kotlin"))));
        }

        [Fact]
        public void Backtick_quotes_are_only_JavaScript_strings()
        {
            string cs = "var s = \"`\"; if (x) {\n}";
            Assert.Single(BraceFolding.Compute(cs, BraceSyntax.For("csharp")));
            string stray = "a `\n{\n  1\n}";
            Assert.Single(BraceFolding.Compute(stray, BraceSyntax.For("csharp")));
            Assert.Empty(BraceFolding.Compute(stray, BraceSyntax.For("javascript")));
        }

        [Theory]
        [InlineData("var p = @\"C:\\dir\\\"; {\n}")]
        [InlineData("var p = $@\"{a}\\\"; {\n}")]
        [InlineData("var p = @$\"{a}\\\"; {\n}")]
        [InlineData("var q = @\"say \"\"{\"\" \\\"; {\n}")]
        public void CSharp_verbatim_strings_take_backslash_literally_and_double_their_quotes(string cs)
        {
            var folds = BraceFolding.Compute(cs, BraceSyntax.For("csharp"));
            Assert.Equal(new[] { "{\n}" }, Folded(cs, folds));
        }

        [Fact]
        public void A_CSharp_verbatim_string_may_span_lines()
        {
            string cs = "var s = @\"\n{\n}\n\";";
            Assert.Empty(BraceFolding.Compute(cs, BraceSyntax.For("csharp")));
            // Other C-like languages have no verbatim strings: there the braces are code.
            Assert.Single(BraceFolding.Compute(cs, BraceSyntax.For("java")));
        }

        [Theory]
        [InlineData("if ($a -eq 'x`') {\n  1\n}")]
        [InlineData("$s = 'it''s {'; if ($x) {\n  1\n}")]
        [InlineData("$p = 'C:\\dir\\'; if ($x) {\n  1\n}")]
        public void PowerShell_single_quoted_strings_take_the_backtick_literally(string ps)
        {
            var folds = BraceFolding.Compute(ps, BraceSyntax.For("powershell"));
            Assert.Equal(new[] { "{\n  1\n}" }, Folded(ps, folds));
        }

        [Fact]
        public void PHP_hash_comments_are_ignored()
        {
            string php = "if (x) {\n# }\n  1;\n}";
            var folds = BraceFolding.Compute(php, BraceSyntax.For("php"));
            Assert.Equal(new[] { php.Substring(php.IndexOf('{')) }, Folded(php, folds));
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

        private static void Find(Kil0bitSystemMonitor.Pad.MicaPadWindow window, string what, bool previous = false)
        {
            window.FindBar.Open(replace: false);
            window.FindBar.FindBox.Text = what;
            window.FindBar.Recompute();
            if (previous) window.FindBar.FindPrevious();
            else window.FindBar.FindNext();
            Assert.Equal(what, window.Editor.SelectedText);
        }

        [Fact]
        public void Find_opens_a_heading_fold_its_match_ends_at() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "# A\ntext here\n# B";
            var folding = window.LanguageView.Folding!;
            folding.Update();
            var section = folding.Manager!.AllFoldings.Single();
            Assert.Equal(window.Editor.Document.Text.IndexOf("\n#", System.StringComparison.Ordinal), section.EndOffset);
            section.IsFolded = true;

            Find(window, "here");

            Assert.False(section.IsFolded);
        });

        [Fact]
        public void Find_previous_opens_a_brace_fold_its_match_ends_at() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            PadLanguageWindowTests.OpenFile(window, env, "a.json", "{\n  \"a\": 1\n}");
            var folding = window.LanguageView.Folding!;
            folding.Update();
            var section = folding.Manager!.AllFoldings.Single();
            section.IsFolded = true;

            Find(window, "}", previous: true);

            Assert.False(section.IsFolded);
        });

        [Fact]
        public void Replace_then_find_opens_the_fold_the_next_match_ends_at() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "# A\none\n# B\none";
            var folding = window.LanguageView.Folding!;
            folding.Update();
            var second = folding.Manager!.AllFoldings.Last();
            second.IsFolded = true;
            window.Editor.CaretOffset = 0;
            Find(window, "one");

            window.FindBar.Open(replace: true);
            window.FindBar.ReplaceBox.Text = "two";
            window.FindBar.ReplaceButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.Equal("# A\ntwo\n# B\none", window.Editor.Document.Text);
            Assert.Equal("one", window.Editor.SelectedText);
            Assert.False(second.IsFolded);
        });

        [Fact]
        public void Selecting_up_to_a_fold_end_without_find_leaves_it_folded() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "# A\ntext here\n# B";
            var folding = window.LanguageView.Folding!;
            folding.Update();
            var section = folding.Manager!.AllFoldings.Single();
            section.IsFolded = true;

            window.Editor.Select(section.EndOffset - 4, 4);

            Assert.True(section.IsFolded);
        });

        [Fact]
        public void Saving_keeps_what_is_folded() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            PadLanguageWindowTests.OpenFile(window, env, "a.json", "{\n  \"a\": 1,\n  \"b\": 2\n}");
            var folding = window.LanguageView.Folding!;
            folding.Update();
            folding.Manager!.AllFoldings.Single().IsFolded = true;
            window.Editor.Document.Insert(window.Editor.Document.TextLength, "\n");

            Assert.True(window.HandleShortcut(System.Windows.Input.Key.S, System.Windows.Input.ModifierKeys.Control));

            Assert.False(env.Workspace.Active!.HasUnsavedEdits);
            Assert.Equal("JSON", window.LanguageButton.Content);
            Assert.True(folding.Manager!.AllFoldings.Single().IsFolded);
        });

        [Fact]
        public void The_markdown_switch_leaves_a_json_tab_folded() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            PadLanguageWindowTests.OpenFile(window, env, "a.json", "{\n  \"a\": 1,\n  \"b\": 2\n}");
            var folding = window.LanguageView.Folding!;
            folding.Update();
            folding.Manager!.AllFoldings.Single().IsFolded = true;

            config.PadMarkdown = false;
            Assert.True(folding.Manager!.AllFoldings.Single().IsFolded);
            config.PadMarkdown = true;
            Assert.True(folding.Manager!.AllFoldings.Single().IsFolded);
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
