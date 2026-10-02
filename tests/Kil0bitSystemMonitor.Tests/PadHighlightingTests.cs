using System.Linq;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Every language's definition loads, MicaPad's own definitions color what they should, and every named color reads well in both themes.</summary>
    public class PadHighlightingTests
    {
        [Fact]
        public void Every_language_with_colors_has_a_definition() => UiThread.Run(() =>
        {
            foreach (var language in PadLanguages.All.Where(l => l.Definition != null))
                Assert.True(PadHighlighting.For(language) != null, language.Name + " did not load");
            Assert.Null(PadHighlighting.For(PadLanguages.Plain));
            Assert.Null(PadHighlighting.For(PadLanguages.Markdown));
        });

        [Fact]
        public void Definitions_are_loaded_once() => UiThread.Run(() =>
        {
            var log = PadLanguages.ById("log")!;
            Assert.Same(PadHighlighting.For(log), PadHighlighting.For(log));
        });

        [Fact]
        public void Every_named_color_reads_well_in_both_themes() => UiThread.Run(() =>
        {
            foreach (var language in PadLanguages.All.Where(l => l.Definition != null))
            {
                var definition = PadHighlighting.For(language)!;
                foreach (var color in definition.NamedHighlightingColors)
                    foreach (var palette in new[] { PadPalette.Dark, PadPalette.Light })
                    {
                        var paint = SyntaxColors.Resolve(color.Name, null, palette)!.Value;
                        Assert.True(PadColor.Contrast(paint, palette.Background) >= 4.5,
                            language.Name + "." + color.Name + " in " + palette.Name);
                    }
            }
        });

        [Fact]
        public void Yaml_apostrophe_in_text_is_not_a_string() => UiThread.Run(() =>
        {
            var definition = PadHighlighting.For(PadLanguages.ById("yaml")!)!;
            var highlighter = new DocumentHighlighter(new TextDocument("note: don't panic"), definition);
            Assert.DoesNotContain(highlighter.HighlightLine(1).Sections, s => s.Color.Name == "String");
        });

        [Fact]
        public void Yaml_apostrophe_does_not_leak_into_the_next_line() => UiThread.Run(() =>
        {
            var definition = PadHighlighting.For(PadLanguages.ById("yaml")!)!;
            var document = new TextDocument("note: don't panic\nnext: 1");
            var highlighter = new DocumentHighlighter(document, definition);
            int lineStart = document.GetLineByNumber(2).Offset;
            Assert.Contains(highlighter.HighlightLine(2).Sections, s => s.Offset == lineStart && s.Length == 4 && s.Color.Name == "KeyName");
        });

        [Fact]
        public void Yaml_quoted_value_is_a_string() => UiThread.Run(() =>
        {
            var definition = PadHighlighting.For(PadLanguages.ById("yaml")!)!;
            var highlighter = new DocumentHighlighter(new TextDocument("title: 'hello'"), definition);
            Assert.Contains(highlighter.HighlightLine(1).Sections, s => s.Offset == 7 && s.Length == 7 && s.Color.Name == "String");
        });

        [Fact]
        public void Shell_hash_inside_a_string_is_not_a_comment() => UiThread.Run(() =>
        {
            var highlighter = new DocumentHighlighter(new TextDocument("echo \"a # b\""), PadHighlighting.For(PadLanguages.ById("shell")!)!);
            Assert.DoesNotContain(highlighter.HighlightLine(1).Sections, s => s.Color.Name == "Comment");
        });

        [Fact]
        public void Rust_lifetime_is_not_a_char() => UiThread.Run(() =>
        {
            const string line = "fn f<'a>(x: &'a str)";
            var highlighter = new DocumentHighlighter(new TextDocument(line), PadHighlighting.For(PadLanguages.ById("rust")!)!);
            Assert.DoesNotContain(highlighter.HighlightLine(1).Sections, s => s.Color.Name == "Char");
        });

        [Theory]
        [InlineData("pascal", "x := 1; { a\nb } y := 2;", 2, "b }")]
        [InlineData("ruby", "=begin\nnote\n=end\nx = 1", 2, "note")]
        [InlineData("rust", "/* a\nb */ let x;", 2, "b */")]
        public void Block_comments_span_lines(string languageId, string text, int lineNumber, string part) => UiThread.Run(() =>
        {
            var document = new TextDocument(text);
            var sections = new DocumentHighlighter(document, PadHighlighting.For(PadLanguages.ById(languageId)!)!).HighlightLine(lineNumber).Sections;
            var line = document.GetLineByNumber(lineNumber);
            int start = line.Offset + document.GetText(line).IndexOf(part, System.StringComparison.Ordinal);
            Assert.Contains(sections, s => s.Offset == start && s.Length == part.Length && s.Color.Name == "Comment");
        });

        [Theory]
        [InlineData("ini", "[main]", "[main]", "Section")]
        [InlineData("ini", "key = value", "key", "KeyName")]
        [InlineData("ini", "key = value", "value", "String")]
        [InlineData("ini", "; a comment", "; a comment", "Comment")]
        [InlineData("yaml", "name: MicaPad # note", "name", "KeyName")]
        [InlineData("yaml", "name: MicaPad # note", "# note", "Comment")]
        [InlineData("yaml", "enabled: true", "true", "Bool")]
        [InlineData("yaml", "count: 42", "42", "Number")]
        [InlineData("batch", "@echo off", "echo", "Keywords")]
        [InlineData("batch", "rem cleanup", "rem cleanup", "Comment")]
        [InlineData("batch", ":: cleanup", ":: cleanup", "Comment")]
        [InlineData("batch", ":done", ":done", "LabelDirective")]
        [InlineData("batch", "echo %PATH%", "%PATH%", "Variable")]
        [InlineData("log", "2026-09-30 16:05:34.932 [ERROR] boom", "2026-09-30 16:05:34.932", "LogTimestamp")]
        [InlineData("log", "2026-09-30 16:05:34.932 [ERROR] boom", "ERROR", "LogError")]
        [InlineData("log", "[warn] disk low", "warn", "LogWarning")]
        [InlineData("log", "INFO started", "INFO", "LogInfo")]
        [InlineData("log", "DEBUG x=1", "DEBUG", "LogDebug")]
        [InlineData("typescript", "interface A { b: string }", "interface", "Keywords")]
        [InlineData("typescript", "interface A { b: string }", "string", "Type")]
        [InlineData("typescript", "let s = `x${y}`", "`x${y}`", "String")]
        [InlineData("typescript", "@Component()", "@Component", "Directive")]
        [InlineData("shell", "# note", "# note", "Comment")]
        [InlineData("shell", "echo $HOME", "$HOME", "Variable")]
        [InlineData("shell", "echo ${#name}", "${#name}", "Variable")]
        [InlineData("shell", "echo $#", "$#", "Variable")]
        [InlineData("shell", "echo \"a # b\"", "\"a # b\"", "String")]
        [InlineData("shell", "if [ -f x ]; then", "if", "Keywords")]
        [InlineData("shell", "if [ -f x ]; then", "then", "Keywords")]
        [InlineData("shell", "#!/bin/bash", "#!/bin/bash", "Directive")]
        [InlineData("pascal", "{$IFDEF DEBUG}", "{$IFDEF DEBUG}", "Directive")]
        [InlineData("pascal", "{ note }", "{ note }", "Comment")]
        [InlineData("pascal", "(* note *)", "(* note *)", "Comment")]
        [InlineData("pascal", "// note", "// note", "Comment")]
        [InlineData("pascal", "s := 'it''s';", "'it''s'", "String")]
        [InlineData("pascal", "BEGIN", "BEGIN", "Keywords")]
        [InlineData("pascal", "x: Integer;", "Integer", "Type")]
        [InlineData("pascal", "x := $FF;", "$FF", "Number")]
        [InlineData("pascal", "s := #13;", "#13", "Char")]
        [InlineData("go", "func main() {", "func", "Keywords")]
        [InlineData("go", "s := `raw`", "`raw`", "String")]
        [InlineData("go", "var x error", "error", "Type")]
        [InlineData("go", "x := nil", "nil", "Null")]
        [InlineData("dockerfile", "FROM node:20 AS build", "FROM", "Keywords")]
        [InlineData("dockerfile", "FROM node:20 AS build", "AS", "Keywords")]
        [InlineData("dockerfile", "RUN echo $HOME", "$HOME", "Variable")]
        [InlineData("dockerfile", "# note", "# note", "Comment")]
        [InlineData("dockerfile", "# syntax=docker/dockerfile:1", "# syntax=docker/dockerfile:1", "Directive")]
        [InlineData("rust", "fn main() {", "fn", "Keywords")]
        [InlineData("rust", "#[derive(Debug)]", "#[derive(Debug)]", "Directive")]
        [InlineData("rust", "let c = 'a';", "'a'", "Char")]
        [InlineData("rust", "let v: Vec<u8>", "u8", "Type")]
        [InlineData("rust", "println!(\"x\")", "println!", "Directive")]
        [InlineData("ruby", "def greet(name)", "def", "Keywords")]
        [InlineData("ruby", "# note", "# note", "Comment")]
        [InlineData("ruby", "@name = :sym", "@name", "Variable")]
        [InlineData("ruby", "@name = :sym", ":sym", "Variable")]
        [InlineData("ruby", "x = nil", "nil", "Null")]
        [InlineData("ruby", "=begin", "=begin", "Comment")]
        [InlineData("kotlin", "fun main() {", "fun", "Keywords")]
        [InlineData("kotlin", "val s = \"a ${b}\"", "\"a ${b}\"", "String")]
        [InlineData("kotlin", "@Test", "@Test", "Directive")]
        [InlineData("kotlin", "val n: Int = 1", "Int", "Type")]
        [InlineData("markdown-fence", "# Title", "# Title", "HeadingKeywords")]
        [InlineData("markdown-fence", "`code`", "`code`", "CodeString")]
        [InlineData("markdown-fence", "[a](b)", "[a]", "LinkTag")]
        [InlineData("markdown-fence", "> quote", "> quote", "QuoteComment")]
        public void Own_definitions_color_what_they_should(string languageId, string line, string part, string colorName) => UiThread.Run(() =>
        {
            var definition = PadHighlighting.For(PadLanguages.ForFence(languageId)!)!;
            var highlighter = new DocumentHighlighter(new TextDocument(line), definition);
            var sections = highlighter.HighlightLine(1).Sections;
            int start = line.IndexOf(part, System.StringComparison.Ordinal);

            Assert.Contains(sections, s => s.Offset == start && s.Length == part.Length && s.Color.Name == colorName);
        });
    }
}
