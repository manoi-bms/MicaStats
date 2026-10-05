using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The user docs describe model discovery and the limits derived from a model's context window.</summary>
    public class AiModelLimitsDocsTests
    {
        private static string Read(string name) =>
            File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), name)).Replace("\r\n", "\n", StringComparison.Ordinal);

        private static string Flat(string text) => Regex.Replace(text, @"\s+", " ");

        [Fact]
        public void The_guide_documents_model_discovery_dynamic_limits_and_ask_statuses()
        {
            string guide = Flat(Read("GUIDE.md"));

            foreach (string needle in new[]
            {
                "model list", "Refresh", "Context window", "Auto", "standard limits",
                "8,192", "262,144", "1,048,576", "4,096",
                "The answer was cut short at the length limit.",
                "Earlier turns are no longer sent to the model: the conversation is longer than it can take.",
                "only in **Settings → AI** or when AI is used",
                "MCP clients keep the fixed limit of 400 lines and 16,000 characters",
            })
                Assert.Contains(needle, guide, StringComparison.Ordinal);

            Assert.Contains("When only your number is known", guide, StringComparison.Ordinal);
            Assert.Contains("answer limits stay at 4,096", guide, StringComparison.Ordinal);
        }

        [Fact]
        public void The_readme_documents_model_limits_in_english_and_thai()
        {
            string readme = Read("README.md");
            int thaiAt = readme.IndexOf("## เกี่ยวกับ MicaStats", StringComparison.Ordinal);
            Assert.True(thaiAt > 0);
            string english = Flat(readme.Substring(0, thaiAt));
            string thai = Flat(readme.Substring(thaiAt));

            foreach (string part in new[] { english, thai })
            {
                foreach (string needle in new[]
                {
                    "Refresh", "Auto", "8,192", "262,144", "1,048,576", "4,096",
                    "The answer was cut short at the length limit.",
                    "Earlier turns are no longer sent to the model: the conversation is longer than it can take.",
                    "16,000", "400",
                })
                    Assert.Contains(needle, part, StringComparison.Ordinal);
            }

            Assert.Contains("model list", english, StringComparison.Ordinal);
            Assert.Contains("standard limits", english, StringComparison.Ordinal);
            Assert.Contains("only in **Settings → AI** or when AI is used", english, StringComparison.Ordinal);
            Assert.Contains("รายการโมเดล", thai, StringComparison.Ordinal);
            Assert.Contains("ขีดจำกัดมาตรฐาน", thai, StringComparison.Ordinal);
            Assert.Contains("เฉพาะเมื่อเปิด **Settings → AI** หรือเมื่อใช้ AI", thai, StringComparison.Ordinal);

            string wiki = Assert.Single(readme.Split('\n'), line =>
                line.StartsWith("* **Markdown the way Wiki.js shows it**", StringComparison.Ordinal));
            Assert.Contains("Copy", wiki, StringComparison.Ordinal);
        }
    }
}
