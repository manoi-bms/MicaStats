using System.Linq;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The language a fenced block names (spec 1.2).</summary>
    public class FenceLanguagesTests
    {
        [Theory]
        [InlineData("cs", "csharp")]
        [InlineData("C#", "csharp")]
        [InlineData("TSX", "javascript")]
        [InlineData("node", "javascript")]
        [InlineData("json5", "json")]
        [InlineData("xaml", "xml")]
        [InlineData("htm", "html")]
        [InlineData("pwsh", "powershell")]
        [InlineData("py", "python")]
        [InlineData("postgresql", "sql")]
        [InlineData("c++", "cpp")]
        [InlineData("vbnet", "vb")]
        [InlineData("patch", "diff")]
        [InlineData("toml", "ini")]
        [InlineData("yml", "yaml")]
        [InlineData("cmd", "batch")]
        [InlineData("log", "log")]
        public void A_word_names_its_language(string word, string id) => Assert.Equal(id, FenceLanguages.IdOf(word));

        [Theory]
        [InlineData("bash")]
        [InlineData("sh")]
        [InlineData("mermaid")]
        [InlineData("")]
        [InlineData(null)]
        public void Other_words_name_no_language(string? word) => Assert.Null(FenceLanguages.IdOf(word));

        [Fact]
        public void There_are_62_words_and_each_names_a_language_with_colors() => UiThread.Run(() =>
        {
            var words = FenceLanguages.Words.ToList();
            Assert.Equal(62, words.Count);
            foreach (string word in words)
            {
                var language = PadLanguages.ById(FenceLanguages.IdOf(word));
                Assert.True(language != null, word + " names no MicaPad language");
                Assert.True(PadHighlighting.For(language!) != null, word + " has no colors");
            }
        });

        [Theory]
        [InlineData("```cs title", "csharp")]
        [InlineData("~~~ PY", "python")]
        [InlineData("````sql", "sql")]
        [InlineData("```", null)]
        [InlineData("    ```cs", null)]
        [InlineData("$$", null)]
        public void An_opening_fence_names_its_language_by_the_first_word(string line, string? id) =>
            Assert.Equal(id, FenceLanguages.IdOfFence(line));

        [Theory]
        [InlineData("```mermaid flow", "mermaid")]
        [InlineData("  ~~~  dot", "dot")]
        [InlineData("```", null)]
        [InlineData("text", null)]
        public void The_info_word_is_the_first_word_after_the_fence(string line, string? word) =>
            Assert.Equal(word, FenceTracker.InfoWord(line));
    }
}
