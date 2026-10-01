using System;
using System.IO;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>GUIDE.md's Markdown section lists what MicaPad shows, with the Wiki.js syntax (spec 7).</summary>
    public class MarkdownGuideTests
    {
        private static string Section(string heading)
        {
            string guide = "\n" + File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), "GUIDE.md"));
            Assert.DoesNotContain("### ### ", guide, StringComparison.Ordinal);
            int at = guide.IndexOf("\n" + heading + "\n", StringComparison.Ordinal);
            Assert.True(at >= 0, heading + " is missing");
            int next = guide.IndexOf("\n### ", at + 1, StringComparison.Ordinal);
            return guide.Substring(at, (next < 0 ? guide.Length : next) - at);
        }

        [Fact]
        public void The_markdown_section_lists_each_feature_with_its_wikijs_syntax()
        {
            string section = Section("### Markdown");

            foreach (string phrase in new[]
                     {
                         "Reading font", "`# Title`", "`===`", "`**bold**`", "H~2~O", "x^2^", "`cs`", "`yaml`",
                         "`- [x]`", "`>>`", "{.is-info}", "{.is-success}", "{.is-warning}", "{.is-danger}",
                         ":-:", "Format table", "Ctrl+Z", "Front matter", "[^1]", "[text][id]", "*[HTML]", ":smile:",
                         "<kbd>Ctrl</kbd>", "{#id}", "$x^2$", "$$", "$5 and $10",
                         "=200x", "=x120", "=200x120", "data:image/", "Load images from the web", "20 MB", "10 MB", "Draw diagrams",
                         "\\\\server\\share",
                     })
                Assert.Contains(phrase, section);
        }
    }
}
