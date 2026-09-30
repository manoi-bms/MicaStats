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
        public void Own_definitions_color_what_they_should(string languageId, string line, string part, string colorName) => UiThread.Run(() =>
        {
            var definition = PadHighlighting.For(PadLanguages.ById(languageId)!)!;
            var highlighter = new DocumentHighlighter(new TextDocument(line), definition);
            var sections = highlighter.HighlightLine(1).Sections;
            int start = line.IndexOf(part, System.StringComparison.Ordinal);

            Assert.Contains(sections, s => s.Offset == start && s.Length == part.Length && s.Color.Name == colorName);
        });
    }
}
