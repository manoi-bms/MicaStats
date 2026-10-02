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
            foreach (var language in PadLanguages.All.Concat(PadLanguages.FenceOnly).Where(l => l.Definition != null))
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
            foreach (var language in PadLanguages.All.Concat(PadLanguages.FenceOnly).Where(l => l.Definition != null))
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

        /// <summary>AvalonEdit's own definitions: the groups that hold keywords are keywords, and PowerShell cmdlets are functions, as in Prism.</summary>
        [Theory]
        [InlineData("cpp", "if (x) { try { f(); } catch (...) { } }", "if", SyntaxCategory.Keyword)]
        [InlineData("cpp", "if (x) { try { f(); } catch (...) { } }", "try", SyntaxCategory.Keyword)]
        [InlineData("java", "public void f() { return null; }", "void", SyntaxCategory.Keyword)]
        [InlineData("java", "public void f() { return null; }", "null", SyntaxCategory.Keyword)]
        [InlineData("java", "package a.b;", "package", SyntaxCategory.Keyword)]
        [InlineData("javascript", "return null;", "null", SyntaxCategory.Keyword)]
        [InlineData("powershell", "Get-Location | Out-Null", "Get-Location", SyntaxCategory.Function)]
        public void Built_in_definitions_color_their_words_by_what_they_are(string languageId, string line, string word, SyntaxCategory expected) => UiThread.Run(() =>
        {
            var highlighter = new DocumentHighlighter(new TextDocument(line), PadHighlighting.For(PadLanguages.ById(languageId)!)!);
            int start = line.IndexOf(word, System.StringComparison.Ordinal);
            var section = Assert.Single(highlighter.HighlightLine(1).Sections, s => s.Offset == start && s.Length == word.Length);
            Assert.Equal(expected, SyntaxColors.Categorize(section.Color.Name));
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

        /// <summary>A quoted INI value hides a ; or # inside it: it is one string to its closing quote, and only a comment after it is a comment.</summary>
        [Theory]
        [InlineData("message = \"Hello ; world\"", "\"Hello ; world\"", null)]
        [InlineData("x = 'a # b'", "'a # b'", null)]
        [InlineData("path: \"C:\\a ; b\" ; note", "\"C:\\a ; b\"", "; note")]
        public void Ini_quoted_values_hide_their_comment_marks(string line, string value, string? comment) => UiThread.Run(() =>
        {
            var sections = new DocumentHighlighter(new TextDocument(line), PadHighlighting.For(PadLanguages.ById("ini")!)!).HighlightLine(1).Sections;
            int start = line.IndexOf(value, System.StringComparison.Ordinal);
            Assert.Contains(sections, s => s.Offset == start && s.Length == value.Length && s.Color.Name == "String");
            if (comment == null)
            {
                Assert.DoesNotContain(sections, s => s.Color.Name == "Comment");
                return;
            }
            int at = line.LastIndexOf(comment, System.StringComparison.Ordinal);
            Assert.Contains(sections, s => s.Offset == at && s.Length == comment.Length && s.Color.Name == "Comment");
        });

        /// <summary>A yes/no/on/true word is Bool only as a whole value, never in the middle of text or a script block.</summary>
        [Theory]
        [InlineData("message: no data on disk")]
        [InlineData("  echo true && exit 0")]
        [InlineData("run: yes please")]
        [InlineData("- off we go")]
        [InlineData("message: well, no, thanks")]
        [InlineData("tags: [no way, yes sir]")]
        [InlineData("title: see [true] here")]
        public void Yaml_bool_words_inside_text_are_not_bool(string line) => UiThread.Run(() =>
        {
            var highlighter = new DocumentHighlighter(new TextDocument(line), PadHighlighting.For(PadLanguages.ById("yaml")!)!);
            Assert.DoesNotContain(highlighter.HighlightLine(1).Sections, s => s.Color.Name == "Bool");
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
        [InlineData("ini", "key = value ; note", "value", "String")]
        [InlineData("ini", "key = value ; note", "; note", "Comment")]
        [InlineData("ini", "key = value # note", "# note", "Comment")]
        [InlineData("ini", "color = #FF0000", "#FF0000", "String")]
        [InlineData("ini", "url = http://x/a;b#top", "http://x/a;b#top", "String")]
        [InlineData("ini", "[it's]", "[it's]", "Section")]
        [InlineData("ini", "it's = x", "it's", "KeyName")]
        [InlineData("yaml", "- item: x", "item", "KeyName")]
        [InlineData("yaml", "  - name: x", "name", "KeyName")]
        [InlineData("yaml", "name: MicaPad # note", "name", "KeyName")]
        [InlineData("yaml", "name: MicaPad # note", "# note", "Comment")]
        [InlineData("yaml", "enabled: true", "true", "Bool")]
        [InlineData("yaml", "enabled: true # note", "true", "Bool")]
        [InlineData("yaml", "- yes", "yes", "Bool")]
        [InlineData("yaml", "value: ~", "~", "Bool")]
        [InlineData("yaml", "flags: [true, false, null]", "true", "Bool")]
        [InlineData("yaml", "flags: [true, false, null]", "false", "Bool")]
        [InlineData("yaml", "flags: [true, false, null]", "null", "Bool")]
        [InlineData("yaml", "flags: {active: true, disabled: false}", "true", "Bool")]
        [InlineData("yaml", "flags: {active: true, disabled: false}", "false", "Bool")]
        [InlineData("yaml", "- [yes, no]", "no", "Bool")]
        [InlineData("yaml", "a: {b: [on, {c: off}]}", "off", "Bool")]
        [InlineData("yaml", "true", "true", "Bool")]
        [InlineData("yaml", "ports: [80, 443]", "443", "Number")]
        [InlineData("yaml", "count: 42", "42", "Number")]
        [InlineData("batch", "@echo off", "echo", "Keywords")]
        [InlineData("batch", "rem cleanup", "rem cleanup", "Comment")]
        [InlineData("batch", ":: cleanup", ":: cleanup", "Comment")]
        [InlineData("batch", ":done", ":done", "LabelDirective")]
        [InlineData("batch", "echo %PATH%", "%PATH%", "Variable")]
        [InlineData("batch", "cd /d %~dp0", "%~dp0", "Variable")]
        [InlineData("batch", "set ROOT=%~dp0%NAME%", "%~dp0%NAME%", "Variable")]   // two variables; AvalonEdit joins touching sections of one color
        [InlineData("batch", "for %%f in (*.txt) do echo %%~nxf", "%%f", "Variable")]
        [InlineData("batch", "for %%f in (*.txt) do echo %%~nxf", "%%~nxf", "Variable")]
        [InlineData("batch", "echo %1 %*", "%*", "Variable")]
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
        [InlineData("dockerfile", "RUN add-apt-repository ppa", "RUN", "Keywords")]
        [InlineData("pascal", "on E: Exception do", "on", "Keywords")]
        [InlineData("pascal", "property X: Integer read FX write FX default 0;", "read", "Keywords")]
        [InlineData("pascal", "property X: Integer read FX write FX default 0;", "write", "Keywords")]
        [InlineData("pascal", "property X: Integer read FX write FX default 0;", "default", "Keywords")]
        public void Own_definitions_color_what_they_should(string languageId, string line, string part, string colorName) => UiThread.Run(() =>
        {
            var definition = PadHighlighting.For(PadLanguages.ForFence(languageId)!)!;
            var highlighter = new DocumentHighlighter(new TextDocument(line), definition);
            var sections = highlighter.HighlightLine(1).Sections;
            int start = line.IndexOf(part, System.StringComparison.Ordinal);

            Assert.Contains(sections, s => s.Offset == start && s.Length == part.Length && s.Color.Name == colorName);
        });
        [Fact]
        public void Dockerfile_instruction_needs_a_space_or_the_end_after_it() => UiThread.Run(() =>
        {
            var document = new TextDocument("RUN add-apt-repository ppa\nFROM-x");
            var highlighter = new DocumentHighlighter(document, PadHighlighting.For(PadLanguages.ById("dockerfile")!)!);
            Assert.DoesNotContain(highlighter.HighlightLine(1).Sections, s => s.Color.Name == "Keywords" && s.Offset == 4);
            Assert.Empty(highlighter.HighlightLine(2).Sections);
            Assert.Contains(new DocumentHighlighter(new TextDocument("RUN"), PadHighlighting.For(PadLanguages.ById("dockerfile")!)!).HighlightLine(1).Sections, s => s.Color.Name == "Keywords" && s.Length == 3);
        });

        /// <summary>Quote characters inside a line comment must not open a string (or, for Pascal, the brace a comment) on the next line.</summary>
        [Theory]
        [InlineData("shell", "# Don't \"run\" `as` root")]
        [InlineData("ruby", "# Don't \"call\" `this` twice")]
        [InlineData("rust", "// the \"quote ' char `x")]
        [InlineData("go", "// use `x here ' \"")]
        [InlineData("typescript", "// call `foo first, don't \"touch")]
        [InlineData("kotlin", "// don't \"touch `this")]
        [InlineData("pascal", "// TODO: handle { and ' and \"")]
        [InlineData("dockerfile", "# Don't \"cache\" `this` layer")]
        [InlineData("dockerfile", "# syntax=docker/dockerfile:1 don't \"x")]
        public void A_quote_inside_a_line_comment_does_not_color_the_next_line(string languageId, string comment) => UiThread.Run(() =>
        {
            var document = new TextDocument(comment + "\nx");
            var highlighter = new DocumentHighlighter(document, PadHighlighting.For(PadLanguages.ById(languageId)!)!);
            var first = highlighter.HighlightLine(1).Sections;
            Assert.Contains(first, s => s.Offset == 0 && s.Length == comment.Length && (s.Color.Name == "Comment" || s.Color.Name == "Directive"));
            Assert.Empty(highlighter.HighlightLine(2).Sections);
        });

        [Theory]
        [InlineData("s := 'it''s { not a comment';\nx")]
        [InlineData("s := s + '{';\nx")]
        [InlineData("s := '''';\nx")]
        public void Pascal_strings_hide_their_braces_and_double_their_quotes(string text) => UiThread.Run(() =>
        {
            var highlighter = new DocumentHighlighter(new TextDocument(text), PadHighlighting.For(PadLanguages.ById("pascal")!)!);
            var first = highlighter.HighlightLine(1).Sections;
            Assert.DoesNotContain(first, s => s.Color.Name == "Comment");
            Assert.Single(first, s => s.Color.Name == "String");
            Assert.Empty(highlighter.HighlightLine(2).Sections);
        });

        [Theory]
        [InlineData("rust", "let d = '\"';\nx", "'\"'")]
        [InlineData("rust", "let d = '`';\nx", "'`'")]
        [InlineData("rust", "let d = '\\'';\nx", "'\\''")]
        [InlineData("go", "r := '`'\nx", "'`'")]
        [InlineData("go", "r := '\"'\nx", "'\"'")]
        [InlineData("go", "r := '\\''\nx", "'\\''")]
        [InlineData("kotlin", "val c = '\"'\nx", "'\"'")]
        [InlineData("kotlin", "val c = '\\u0041'\nx", "'\\u0041'")]
        public void A_char_literal_holding_a_quote_does_not_open_a_string(string languageId, string text, string literal) => UiThread.Run(() =>
        {
            var highlighter = new DocumentHighlighter(new TextDocument(text), PadHighlighting.For(PadLanguages.ById(languageId)!)!);
            int start = text.IndexOf(literal, System.StringComparison.Ordinal);
            Assert.Contains(highlighter.HighlightLine(1).Sections, s => s.Offset == start && s.Length == literal.Length && s.Color.Name == "Char");
            Assert.Empty(highlighter.HighlightLine(2).Sections);
        });

        [Fact]
        public void Heredoc_bodies_are_strings_and_the_code_after_them_is_colored_normally() => UiThread.Run(() =>
        {
            var shell = new DocumentHighlighter(new TextDocument("cat <<EOF\nDon't run this as root.\nEOF\necho $HOME"), PadHighlighting.For(PadLanguages.ById("shell")!)!);
            Assert.Contains(shell.HighlightLine(2).Sections, s => s.Offset == 10 && s.Length == 23 && s.Color.Name == "String");   // offsets are in the document
            shell.HighlightLine(3);
            Assert.Contains(shell.HighlightLine(4).Sections, s => s.Offset == 43 && s.Length == 5 && s.Color.Name == "Variable");

            var ruby = new DocumentHighlighter(new TextDocument("puts <<~MSG\n  Couldn't greet\nMSG\nx = nil"), PadHighlighting.For(PadLanguages.ById("ruby")!)!);
            Assert.Contains(ruby.HighlightLine(2).Sections, s => s.Color.Name == "String");
            ruby.HighlightLine(3);
            Assert.Contains(ruby.HighlightLine(4).Sections, s => s.Offset == 37 && s.Length == 3 && s.Color.Name == "Null");

            var herestring = new DocumentHighlighter(new TextDocument("cat <<< \"x\"\ny=$((1<<n))\nx"), PadHighlighting.For(PadLanguages.ById("shell")!)!);
            herestring.HighlightLine(1);
            herestring.HighlightLine(2);
            Assert.Empty(herestring.HighlightLine(3).Sections);
        });

        /// <summary>Not a heredoc: a mixed-case tag could never be closed, and an arithmetic shift is no heredoc. The line after must stay plain.</summary>
        [Theory]
        [InlineData("shell", "cat <<End-of-message")]
        [InlineData("shell", "cat <<EndOfText")]
        [InlineData("shell", "cat <<Eof")]
        [InlineData("shell", "echo $(( 1 << SHIFT ))")]
        [InlineData("shell", "(( mask = 1 << IDX ))")]
        [InlineData("ruby", "puts <<~Sql")]
        [InlineData("ruby", "puts <<End-of-text")]
        public void A_mixed_case_tag_or_a_shift_does_not_open_a_heredoc(string languageId, string line) => UiThread.Run(() =>
        {
            var highlighter = new DocumentHighlighter(new TextDocument(line + "\nx"), PadHighlighting.For(PadLanguages.ById(languageId)!)!);
            Assert.DoesNotContain(highlighter.HighlightLine(1).Sections, s => s.Color.Name == "String");
            Assert.Empty(highlighter.HighlightLine(2).Sections);
        });

        [Theory]
        [InlineData("shell", "cat <<'EOF'")]
        [InlineData("shell", "cat <<\"EOF\"")]
        [InlineData("shell", "cat <<-EOF")]
        [InlineData("shell", "cat << EOF")]
        [InlineData("shell", "x=$(cat <<EOF")]
        [InlineData("ruby", "puts <<-EOS")]
        public void An_upper_case_tag_opens_a_heredoc_that_its_line_closes(string languageId, string line) => UiThread.Run(() =>
        {
            string tag = line.Substring(line.IndexOf("<<", System.StringComparison.Ordinal)).Trim('<', '-', '~', ' ', '\'', '"');
            var highlighter = new DocumentHighlighter(new TextDocument(line + "\nDon't\n" + tag + "\nx"), PadHighlighting.For(PadLanguages.ById(languageId)!)!);
            highlighter.HighlightLine(1);
            Assert.Contains(highlighter.HighlightLine(2).Sections, s => s.Color.Name == "String");
            highlighter.HighlightLine(3);
            Assert.Empty(highlighter.HighlightLine(4).Sections);
        });

        /// <summary>
        /// A common tag's heredoc ends only on its own tag's line: a bare WARNING, END or COMMIT inside
        /// is text, and the later Don't opens no string. A rare tag still ends on its own line.
        /// </summary>
        [Theory]
        [InlineData("shell", "cat <<EOF\nWARNING\nEND\nDon't\nEOF\necho $HOME", "$HOME", "Variable")]
        [InlineData("shell", "cat <<-'SQL'\n\tCOMMIT;\n\tCOMMIT\n\tDon't\n\tSQL\necho $HOME", "$HOME", "Variable")]
        [InlineData("ruby", "sql = <<~SQL\n  SELECT 1;\n  COMMIT\n  Don't\nSQL\nx = nil", "nil", "Null")]
        [InlineData("shell", "cat <<ZZTOP\nDon't\nZZTOP\necho $HOME", "$HOME", "Variable")]
        [InlineData("shell", "cat <<\\EOF\nDon't\nEOF\necho $HOME", "$HOME", "Variable")]           // a backslash-quoted tag
        [InlineData("shell", "cat <<-\\ZZTOP\n\tDon't\n\tZZTOP\necho $HOME", "$HOME", "Variable")]
        public void A_heredoc_ends_only_on_its_own_tag_line(string languageId, string text, string part, string colorName) => UiThread.Run(() =>
        {
            var document = new TextDocument(text);
            var highlighter = new DocumentHighlighter(document, PadHighlighting.For(PadLanguages.ById(languageId)!)!);
            int last = document.LineCount;
            highlighter.HighlightLine(1);
            for (int n = 2; n < last; n++)   // the body and the closing tag line: all string
            {
                var line = document.GetLineByNumber(n);
                Assert.True(highlighter.HighlightLine(n).Sections.Any(s => s.Offset == line.Offset && s.Length == line.Length && s.Color.Name == "String"),
                            "line " + n + " is not all string");
            }

            var after = document.GetLineByNumber(last);
            var sections = highlighter.HighlightLine(last).Sections;
            int start = after.Offset + document.GetText(after).IndexOf(part, System.StringComparison.Ordinal);
            Assert.Contains(sections, s => s.Offset == start && s.Length == part.Length && s.Color.Name == colorName);
            Assert.DoesNotContain(sections, s => s.Color.Name == "String");
        });

        /// <summary>A <c>]</c> inside an attribute's string does not end the attribute, so its closing quote opens no string.</summary>
        [Theory]
        [InlineData("#[doc = \"[x]\"]\nfn main() {}", 2)]
        [InlineData("#[doc = \"[x]\"] fn main() {}", 1)]
        [InlineData("#![doc = \"a]b\"]\nfn main() {}", 2)]
        public void A_rust_attribute_holds_brackets_inside_its_strings(string text, int fnLine) => UiThread.Run(() =>
        {
            var document = new TextDocument(text);
            var highlighter = new DocumentHighlighter(document, PadHighlighting.For(PadLanguages.ById("rust")!)!);
            string attribute = text.Substring(0, text.IndexOf("\"]", System.StringComparison.Ordinal) + 2);
            Assert.Contains(highlighter.HighlightLine(1).Sections, s => s.Offset == 0 && s.Length == attribute.Length && s.Color.Name == "Directive");

            var line = document.GetLineByNumber(fnLine);
            int fn = line.Offset + document.GetText(line).IndexOf("fn", System.StringComparison.Ordinal);
            var sections = highlighter.HighlightLine(fnLine).Sections;
            Assert.Contains(sections, s => s.Offset == fn && s.Length == 2 && s.Color.Name == "Keywords");
            Assert.DoesNotContain(sections, s => s.Color.Name == "String" && s.Offset >= fn);
        });

        [Theory]
        [InlineData("let d = b'\"';", "b'\"'")]
        [InlineData("let d = '\\\\';", "'\\\\'")]
        [InlineData("let d = '\\u{1F600}';", "'\\u{1F600}'")]
        public void Rust_byte_backslash_and_unicode_chars_are_one_char(string line, string literal) => UiThread.Run(() =>
        {
            var highlighter = new DocumentHighlighter(new TextDocument(line + "\nx"), PadHighlighting.For(PadLanguages.ById("rust")!)!);
            int start = line.IndexOf(literal, System.StringComparison.Ordinal);
            Assert.Contains(highlighter.HighlightLine(1).Sections, s => s.Offset == start && s.Length == literal.Length && s.Color.Name == "Char");
            Assert.Empty(highlighter.HighlightLine(2).Sections);
        });

        /// <summary>A sample token for each own definition and the color name it must get.</summary>
        private static readonly System.Collections.Generic.Dictionary<string, (string Line, string Part, string Color)> Samples = new()
        {
            ["ini"] = ("; note", "; note", "Comment"),
            ["yaml"] = ("# note", "# note", "Comment"),
            ["batch"] = ("rem note", "rem note", "Comment"),
            ["log"] = ("ERROR boom", "ERROR", "LogError"),
            ["typescript"] = ("// note", "// note", "Comment"),
            ["shell"] = ("# note", "# note", "Comment"),
            ["pascal"] = ("// note", "// note", "Comment"),
            ["go"] = ("// note", "// note", "Comment"),
            ["dockerfile"] = ("# note", "# note", "Comment"),
            ["rust"] = ("// note", "// note", "Comment"),
            ["ruby"] = ("# note", "# note", "Comment"),
            ["kotlin"] = ("// note", "// note", "Comment"),
            ["markdown-fence"] = ("> quote", "> quote", "QuoteComment"),
        };

        [Fact]
        public void A_whole_file_in_an_own_definition_is_painted_with_its_palette_color_in_both_themes() => UiThread.Run(() =>
        {
            var own = PadLanguages.All.Concat(PadLanguages.FenceOnly).Where(l => l.OwnDefinition).ToList();
            Assert.Equal(own.Select(l => l.Id).OrderBy(x => x), Samples.Keys.OrderBy(x => x));
            foreach (var language in own)
                foreach (var palette in new[] { PadPalette.Dark, PadPalette.Light })
                {
                    var (line, part, colorName) = Samples[language.Id];
                    var editor = new ICSharpCode.AvalonEdit.TextEditor { Document = new TextDocument(line) };
                    var view = editor.TextArea.TextView;
                    view.LineTransformers.Add(new ThemedHighlightingColorizer(PadHighlighting.For(language)!, () => palette));
                    view.Measure(new System.Windows.Size(1200, 800));
                    view.Arrange(new System.Windows.Rect(0, 0, 1200, 800));
                    view.EnsureVisualLines();

                    var expected = SyntaxColors.Resolve(colorName, null, palette)!.Value;
                    Assert.True(expected != palette.Text, language.Id + " sample must be a colored category");
                    Assert.Equal(expected, FenceColorsTests.ForegroundAt(view, 1, line.IndexOf(part, System.StringComparison.Ordinal)));
                }
        });
    }
}
