using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Function names in their own color (ruling R4): where a call is, and that only uncolored text in a flagged language gets it.</summary>
    public class FunctionCallsTests
    {
        private static string Names(string line, int maxLength = MarkdownLineTokenizer.MaxInlineLength) =>
            string.Join(",", FunctionCalls.Find(line, maxLength).Select(f => line.Substring(f.Start, f.Length)));

        /// <summary>The names <see cref="FunctionCalls.Find"/> gives with a language's call syntax.</summary>
        private static string NamesIn(string languageId, string line) =>
            string.Join(",", FunctionCalls.Find(line, MarkdownLineTokenizer.MaxInlineLength, CallSyntax.For(languageId)).Select(f => line.Substring(f.Start, f.Length)));

        [Theory]
        [InlineData("Console.WriteLine(\"x\");", "WriteLine")]
        [InlineData("foo (1)", "foo")]
        [InlineData("foo\t(1)", "foo")]
        [InlineData("x = a[1](2)", "")]
        [InlineData("1foo(", "")]
        [InlineData("a.b(c(d), e)", "b,c")]
        [InlineData("my_func2 (x)", "my_func2")]
        [InlineData("foo = bar", "")]
        [InlineData("", "")]
        // A lone $ is no name: PowerShell's $(...) is a subexpression (so jQuery's $(...) goes too).
        [InlineData("$(Get-Date)", "")]
        [InlineData("$(document).ready(f)", "ready")]
        [InlineData("$fn(1); _(\"text\")", "$fn,_")]
        public void Find_returns_each_name_followed_by_a_parenthesis(string line, string expected)
        {
            Assert.Equal(expected, Names(line));
        }

        /// <summary>PowerShell (tight calls): a method call has no space before its parenthesis, so a name with one is an argument.</summary>
        [Theory]
        [InlineData("Join-Path -Path (Get-Location)", "")]
        [InlineData("\"{0}\" -f ($x)", "")]
        [InlineData("$a -split (',')", "")]
        [InlineData("New-Object System.Drawing.PointF ($m)", "")]
        [InlineData("$s.Trim(); [Math]::Round($x, 2)", "Trim,Round")]
        // function and filter name the function they declare, spaced or not.
        [InlineData("function Foo ($x) { }", "Foo")]
        [InlineData("filter Foo\t($x) { $_ }", "Foo")]
        [InlineData("function Foo($x) { }", "Foo")]
        public void With_tight_calls_a_name_spaced_from_its_parenthesis_is_no_call(string line, string expected)
        {
            Assert.True(CallSyntax.For("powershell").Tight);
            Assert.Equal(expected, NamesIn("powershell", line));
            Assert.Equal("foo", NamesIn("csharp", "foo (1)"));   // other languages keep the optional spaces
        }

        [Theory]
        [InlineData("Join-Path -Path (Get-Location)", "Path (")]
        [InlineData("\"{0}\" -f ($x)", "f (")]
        [InlineData("$a -split (',')", "split (")]
        [InlineData("New-Object System.Drawing.PointF ($m)", "PointF (")]
        public void A_powershell_argument_before_a_parenthesis_is_no_call(string code, string argument) => UiThread.Run(() =>
        {
            int at = code.IndexOf(argument, StringComparison.Ordinal);
            var file = HighlighterOf(Show("powershell", code)).HighlightLine(1);
            var fence = new FenceHighlighter(new MarkdownDocumentCache()).HighlightLine(new TextDocument("```powershell\n" + code + "\n```"), 2)!;

            Assert.NotEqual(SyntaxCategory.Function, CategoryAt(file, at));
            Assert.NotEqual(SyntaxCategory.Function, CategoryAt(fence, at));
        });

        /// <summary>A PowerShell <c>function</c> or <c>filter</c> names its function even with a space before the parenthesis.</summary>
        [Theory]
        [InlineData("function Foo ($x) { }")]
        [InlineData("filter Foo ($x) { $_ }")]
        [InlineData("function Foo($x) { }")]
        public void A_powershell_function_declaration_colors_its_name(string code) => UiThread.Run(() =>
        {
            int at = code.IndexOf("Foo", StringComparison.Ordinal);
            var file = HighlighterOf(Show("powershell", code)).HighlightLine(1);
            var fence = new FenceHighlighter(new MarkdownDocumentCache()).HighlightLine(new TextDocument("```powershell\n" + code + "\n```"), 2)!;

            Assert.Equal(SyntaxCategory.Function, CategoryAt(file, at));
            Assert.Equal(SyntaxCategory.Function, CategoryAt(fence, at));
        });

        [Fact]
        public void A_powershell_method_call_is_a_call() => UiThread.Run(() =>
        {
            const string code = "$s.Trim(); [Math]::Round($x, 2)";
            var file = HighlighterOf(Show("powershell", code)).HighlightLine(1);
            var fence = new FenceHighlighter(new MarkdownDocumentCache()).HighlightLine(new TextDocument("```powershell\n" + code + "\n```"), 2)!;

            Assert.Equal("Trim,Round", string.Join(",", FunctionNames(file)));
            Assert.Equal("Trim,Round", string.Join(",", FunctionNames(fence)));
        });

        [Theory]
        [InlineData("if (a) foreach (b) while (c) return (d)", "")]
        [InlineData("static_assert(x); decltype(y) z; sizeof (int)", "")]
        [InlineData("async (x) => await (x); yield (x)", "")]
        [InlineData("catch (E) when (x) { Run(); }", "Run")]
        [InlineData("IF (a) Match(b) New(c)", "IF,Match,New")]                 // case counts: Regex.Match, errors.New
        // After ".", "::" or "->" a keyword is a member: str.match, promise.catch, Vec::new, $o->delete.
        [InlineData("s.match(re); p.catch(e); Vec::new(); $o->delete(k)", "match,catch,new,delete")]
        public void Find_skips_keywords_unless_they_are_members(string line, string expected)
        {
            Assert.Equal(expected, Names(line));
        }

        /// <summary>
        /// A name a declaration word of the language names is a type, not a call; <c>not (</c> is C#'s
        /// pattern keyword. A word that declares nothing in the language (<c>object</c> anywhere,
        /// <c>struct</c> in Python) leaves the name after it a call.
        /// </summary>
        [Theory]
        [InlineData("python", "class Foo(Base):", "")]
        [InlineData("kotlin", "data class Point(val x: Int); enum class E(val v: Int); interface I(", "")]
        [InlineData("rust", "fn f(g: impl Fn(i32) -> i32)", "f")]
        [InlineData("rust", "struct S(i32); enum E(1); trait T(); type U(); union V(", "")]
        [InlineData("csharp", "if (x is not (1 or 2)) Run();", "Run")]
        [InlineData("csharp", "record R(int X); struct S (1); enum E(1); interface I(); class C(", "")]
        [InlineData("java", "record R(int x); class C(", "")]
        [InlineData("typescript", "class A(); interface B(); type C(); enum D(); namespace E(", "")]
        [InlineData("javascript", "class A(", "")]
        [InlineData("go", "type T(", "")]
        [InlineData("php", "class A(); interface B(); trait C(); enum D(", "")]
        [InlineData("cpp", "class A(); struct B(); enum C(); union D(", "")]
        [InlineData("ruby", "class A(); module B(", "")]
        [InlineData("csharp", "var p = new Foo(1); myclass Bar(2)", "Foo,Bar")]
        [InlineData("csharp", "x.not(1); y = type(z)", "not,type")]
        [InlineData("csharp", "public object Convert(object value, Type t)", "Convert")]
        [InlineData("csharp", "object Clone(); public static object Parse(string s)", "Clone,Parse")]
        [InlineData("kotlin", "object O(1); fun f() = struct(2)", "O,f,struct")]
        [InlineData("python", "struct Foo(1); impl Bar(2); type Baz(3)", "Foo,Bar,Baz")]
        [InlineData("pascal", "class Foo(1)", "Foo")]
        public void Find_skips_what_a_declaration_names(string languageId, string line, string expected)
        {
            Assert.Equal(expected, NamesIn(languageId, line));
        }

        [Theory]
        [InlineData("python", "class Foo(Base):", "Foo", SyntaxCategory.Text)]          // the definition's own MethodCall
        [InlineData("kotlin", "data class Point(val x: Int)", "Point", SyntaxCategory.Text)]
        [InlineData("rust", "fn f(g: impl Fn(i32) -> i32) {}", "Fn", SyntaxCategory.Text)]
        [InlineData("csharp", "if (x is not (1 or 2)) Run();", "not", SyntaxCategory.Keyword)]
        [InlineData("csharp", "record Point(int X, int Y);", "Point", SyntaxCategory.Text)]
        [InlineData("csharp", "var p = new Foo(1);", "Foo", SyntaxCategory.Function)]       // as before
        public void A_declared_name_is_not_a_function(string languageId, string code, string name, SyntaxCategory expected) => UiThread.Run(() =>
        {
            var line = HighlighterOf(Show(languageId, code)).HighlightLine(1);

            line.ValidateInvariants();
            Assert.Equal(expected, CategoryAt(line, code.IndexOf(name, StringComparison.Ordinal)));
        });

        /// <summary>A word that declares no type in the language (C#'s <c>object</c>) leaves the name after it a method, the definition's own call color included.</summary>
        [Theory]
        [InlineData("public object Convert(object value, Type t) => value;", "Convert")]
        [InlineData("object Clone() => null;", "Clone")]
        [InlineData("public static object Parse(string s) => s;", "Parse")]
        public void A_method_returning_object_keeps_its_function_color(string code, string name) => UiThread.Run(() =>
        {
            int at = code.IndexOf(name, StringComparison.Ordinal);
            var file = HighlighterOf(Show("csharp", code)).HighlightLine(1);
            var fence = new FenceHighlighter(new MarkdownDocumentCache()).HighlightLine(new TextDocument("```csharp\n" + code + "\n```"), 2)!;

            file.ValidateInvariants();
            Assert.Equal(SyntaxCategory.Function, CategoryAt(file, at));
            Assert.Equal(SyntaxCategory.Function, CategoryAt(fence, at));
        });

        [Theory]
        [InlineData("na\u00EFve(x)", "")]
        [InlineData("caf\u00E9(1)", "")]
        [InlineData("\u0E0A\u0E37\u0E48foo(1)", "")]                             // Thai letters and marks, then ASCII
        [InlineData("\u0E01\u0E32\u0E23 foo(1)", "foo")]
        public void A_name_is_the_whole_word_and_only_ascii_names_are_colored(string line, string expected)
        {
            Assert.Equal(expected, Names(line));
        }

        [Fact]
        public void Find_skips_a_line_over_the_inline_limit()
        {
            string over = "foo(" + new string(' ', 3997);
            string exact = "foo(" + new string(' ', 3996);

            Assert.Equal(4001, over.Length);
            Assert.Empty(FunctionCalls.Find(over, MarkdownLineTokenizer.MaxInlineLength));
            Assert.Equal("foo", Names(exact));
        }

        [Fact]
        public void Only_languages_that_call_with_parentheses_get_the_pass()
        {
            var flagged = PadLanguages.All.Concat(PadLanguages.FenceOnly).Where(l => l.FunctionCalls).Select(l => l.Id).OrderBy(id => id);

            Assert.Equal(new[] { "cpp", "csharp", "go", "java", "javascript", "kotlin", "pascal", "php", "powershell", "python", "ruby", "rust", "typescript" }, flagged);
        }

        [Fact]
        public void The_pass_colors_only_what_no_section_covers_and_keeps_the_sections_in_order()
        {
            const string code = "x(foo(1), \"bar(\", baz (2)) // qux(";
            var document = new TextDocument("first line\n" + code);
            var line = new HighlightedLine(document, document.GetLineByNumber(2));
            int at = line.DocumentLine.Offset;
            // The string "bar(" with a section nested in it, and the comment to the line end.
            line.Sections.Add(new HighlightedSection { Offset = at + 10, Length = 6, Color = new HighlightingColor { Name = "String" } });
            line.Sections.Add(new HighlightedSection { Offset = at + 11, Length = 3, Color = new HighlightingColor { Name = "Char" } });
            line.Sections.Add(new HighlightedSection { Offset = at + 27, Length = 7, Color = new HighlightingColor { Name = "Comment" } });
            var failures = new List<Exception>();

            FunctionCallHighlighter.AddTo(line, failures.Add);

            line.ValidateInvariants();
            Assert.Empty(failures);
            Assert.Equal(new (int, int, string?)[]
            {
                (0, 1, "Function"), (2, 3, "Function"), (10, 6, "String"), (11, 3, "Char"), (18, 3, "Function"), (27, 7, "Comment"),
            }, line.Sections.Select(s => (s.Offset - at, s.Length, (string?)s.Color.Name)));
            Assert.Same(line.Sections[0].Color, line.Sections[1].Color);   // one shared color
        }

        [Fact]
        public void A_keyword_the_definition_colored_as_a_call_is_repainted_as_a_keyword_in_place()
        {
            const string code = "if ($x) $o->delete($k);";
            var document = new TextDocument(code);
            var line = new HighlightedLine(document, document.GetLineByNumber(1));
            var call = new HighlightingColor { Name = "FunctionCall" };
            line.Sections.Add(new HighlightedSection { Offset = 0, Length = 2, Color = call });
            line.Sections.Add(new HighlightedSection { Offset = 12, Length = 6, Color = call });   // a member: stays a call
            var failures = new List<Exception>();

            FunctionCallHighlighter.AddTo(line, failures.Add);

            line.ValidateInvariants();
            Assert.Empty(failures);
            Assert.Equal(new[] { (0, 2, SyntaxCategory.Keyword), (12, 6, SyntaxCategory.Function) },
                         line.Sections.Select(s => (s.Offset, s.Length, SyntaxColors.Categorize(s.Color.Name))));
            Assert.Same(call, line.Sections[1].Color);
        }

        [Fact]
        public void A_failing_pass_costs_only_that_line_its_function_colors_and_is_reported_once()
        {
            var document = new TextDocument("foo(1)\nbar(2)\nbaz(3)");
            var failures = new List<Exception>();
            var highlighter = new FunctionCallHighlighter(new BrokenFirstLine(document), failures.Add);

            var first = highlighter.HighlightLine(1);
            var again = highlighter.HighlightLine(1);
            var second = highlighter.HighlightLine(2);

            Assert.IsType<ArgumentOutOfRangeException>(Assert.Single(failures));
            Assert.Equal("Comment", Assert.Single(first.Sections).Color.Name);   // left as the definition gave it
            Assert.Equal("Comment", Assert.Single(again.Sections).Color.Name);
            var call = Assert.Single(second.Sections);
            Assert.Equal((document.GetLineByNumber(2).Offset, 3, "Function"), (call.Offset, call.Length, call.Color.Name));
        }

        [Theory]
        [InlineData("csharp", "if (x) Foo(\"bar(\") // baz(", "Foo", "if")]
        [InlineData("javascript", "if (x) foo(\"bar(\") // baz(", "foo", "if")]
        [InlineData("go", "if (x) foo(\"bar(\") // baz(", "foo", "if")]
        // Definitions that color a keyword before "(" as a call: PHP's FunctionCall, C++'s MethodName, C#'s MethodCall.
        [InlineData("php", "<?php if ($x) { foreach ($a as $b) { while (true) { return f($x); } } }", "f", "if,foreach,while,return")]
        [InlineData("php", "<?php $o->delete($k); if ($k) { }", "delete", "if")]
        [InlineData("cpp", "static_assert(x); decltype(y) z; foo(1);", "foo", "static_assert,decltype")]
        [InlineData("csharp", "try { } catch (E) when (x) { Run(); }", "Run", "catch,when")]
        // Keywords ES5's definition leaves plain.
        [InlineData("javascript", "async (x) => await (x); yield (x);", "", "")]
        public void Function_color_never_enters_strings_comments_or_keywords(string languageId, string code, string functions, string keywords) => UiThread.Run(() =>
        {
            var line = HighlighterOf(Show(languageId, code)).HighlightLine(1);

            line.ValidateInvariants();
            Assert.Equal(functions, string.Join(",", FunctionNames(line)));
            foreach (string keyword in keywords.Split(',', StringSplitOptions.RemoveEmptyEntries))
                Assert.True(SyntaxCategory.Keyword == CategoryAt(line, code.IndexOf(keyword, StringComparison.Ordinal)), keyword + " is not a keyword");
            if (!code.Contains("\"bar(\"", StringComparison.Ordinal)) return;
            Assert.Equal(SyntaxCategory.String, CategoryAt(line, code.IndexOf("bar(", StringComparison.Ordinal)));
            Assert.Equal(SyntaxCategory.Comment, CategoryAt(line, code.IndexOf("baz(", StringComparison.Ordinal)));
        });

        [Theory]
        [InlineData("powershell", "Write-Host $(Get-Date)", "Write-Host,Get-Date")]   // cmdlets are functions, the $ is not
        [InlineData("javascript", "$(document).ready(f);", "ready")]
        public void A_lone_dollar_is_not_a_function_name(string languageId, string code, string functions) => UiThread.Run(() =>
        {
            Assert.Equal(functions, string.Join(",", FunctionNames(HighlighterOf(Show(languageId, code)).HighlightLine(1))));
        });

        [Theory]
        [InlineData("csharp", "void Run() { }", "Run")]
        [InlineData("javascript", "foo(1);", "foo")]
        [InlineData("go", "func main() {", "main")]
        public void A_file_in_a_flagged_language_paints_function_names(string languageId, string code, string name) => UiThread.Run(() =>
        {
            var editor = Show(languageId, code);

            Assert.Equal(PadPalette.Dark.SyntaxFunction, FenceColorsTests.ForegroundAt(editor.TextArea.TextView, 1, code.IndexOf(name, StringComparison.Ordinal)));
        });

        [Fact]
        public void A_file_in_a_language_that_is_not_flagged_gets_no_function_pass() => UiThread.Run(() =>
        {
            const string code = "SELECT COUNT(*), dbo.fn_total(1)";
            var highlighter = HighlighterOf(Show("sql", code));

            Assert.IsNotType<FunctionCallHighlighter>(highlighter);
            Assert.Empty(FunctionNames(highlighter.HighlightLine(1)));
            Assert.Equal(new[] { "COUNT", "fn_total" }, FunctionNames(HighlighterOf(Show("javascript", code)).HighlightLine(1)));
        });

        [Fact]
        public void Lines_over_the_inline_limit_get_no_function_pass() => UiThread.Run(() =>
        {
            string over = "foo(1);" + new string(' ', 3994);
            string exact = "foo(1);" + new string(' ', 3993);
            var highlighter = HighlighterOf(Show("javascript", over + "\n" + exact));

            Assert.Equal(4001, over.Length);
            Assert.Empty(FunctionNames(highlighter.HighlightLine(1)));
            Assert.Equal(new[] { "foo" }, FunctionNames(highlighter.HighlightLine(2)));
        });

        /// <summary>An editor showing <paramref name="code"/> in a language, laid out as a shown window would.</summary>
        private static TextEditor Show(string languageId, string code)
        {
            var editor = new TextEditor { Document = new TextDocument(code) };
            var language = new EditorLanguage(editor, () => PadPalette.Dark, folds: false) { Warn = _ => { } };
            language.Apply(PadLanguages.ById(languageId)!);
            var view = editor.TextArea.TextView;
            view.Measure(new Size(1200, 800));
            view.Arrange(new Rect(0, 0, 1200, 800));
            view.EnsureVisualLines();
            return editor;
        }

        /// <summary>The highlighter whole-file colors come from: what the syntax colorizer registered.</summary>
        private static IHighlighter HighlighterOf(TextEditor editor) =>
            (IHighlighter)editor.TextArea.TextView.GetService(typeof(IHighlighter))!;

        private static List<string> FunctionNames(HighlightedLine line) =>
            line.Sections.Where(s => SyntaxColors.Categorize(s.Color?.Name) == SyntaxCategory.Function)
                .Select(s => line.Document.GetText(s.Offset, s.Length)).ToList();

        /// <summary>The category of the innermost section at <paramref name="column"/>; sections are in order, so the last covering one.</summary>
        private static SyntaxCategory CategoryAt(HighlightedLine line, int column)
        {
            int offset = line.DocumentLine.Offset + column;
            var category = SyntaxCategory.Text;
            foreach (var section in line.Sections)
                if (section.Offset <= offset && offset < section.Offset + section.Length) category = SyntaxColors.Categorize(section.Color?.Name);
            return category;
        }

        /// <summary>
        /// Highlights nothing but its first line's first word, as a comment; that line points past the
        /// document's end, so the pass throws reading its text.
        /// </summary>
        private sealed class BrokenFirstLine : IHighlighter
        {
            private readonly TextDocument _document;
            private readonly TextDocument _longer = new(new string(' ', 100) + "\nfoo(1)");

            public BrokenFirstLine(TextDocument document) => _document = document;

            public IDocument Document => _document;

            public HighlightingColor DefaultTextColor => new();

            public event HighlightingStateChangedEventHandler HighlightingStateChanged { add { } remove { } }

            public HighlightedLine HighlightLine(int lineNumber)
            {
                if (lineNumber != 1) return new HighlightedLine(_document, _document.GetLineByNumber(lineNumber));
                var line = new HighlightedLine(_document, _longer.GetLineByNumber(2));
                line.Sections.Add(new HighlightedSection { Offset = 0, Length = 3, Color = new HighlightingColor { Name = "Comment" } });
                return line;
            }

            public IEnumerable<HighlightingColor> GetColorStack(int lineNumber) => Array.Empty<HighlightingColor>();

            public void UpdateHighlightingState(int lineNumber) { }

            public void BeginHighlighting() { }

            public void EndHighlighting() { }

            public HighlightingColor GetNamedColor(string name) => null!;

            public void Dispose() { }
        }
    }
}
