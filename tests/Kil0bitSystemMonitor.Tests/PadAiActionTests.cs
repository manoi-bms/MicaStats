using System.Globalization;
using System.Linq;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class PadAiActionTests
    {
        [Fact]
        public void Menu_is_the_eight_actions_in_order() =>
            Assert.Equal(new[] { "improve", "fix", "shorten", "to-english", "to-thai", "summarize", "explain", "ask" },
                PadAiAction.Menu.Select(a => a.Id).ToArray());

        [Fact]
        public void Rewrites_take_a_selection_up_to_8000_and_show_plain_text()
        {
            foreach (var a in PadAiAction.Menu.Take(5))
            {
                Assert.Equal(PadAiKind.Rewrite, a.Kind);
                Assert.True(a.NeedsSelection);
                Assert.Equal(8000, a.MaxChars);
                Assert.False(a.RendersMarkdown);
            }
        }

        [Fact]
        public void Summarize_and_Explain_read_up_to_24000_as_Markdown()
        {
            foreach (var a in new[] { PadAiAction.Summarize, PadAiAction.Explain })
            {
                Assert.Equal(PadAiKind.Read, a.Kind);
                Assert.Equal(24000, a.MaxChars);
                Assert.True(a.RendersMarkdown);
                Assert.False(a.NeedsSelection);
            }
        }

        [Fact]
        public void Ask_is_custom_with_an_empty_instruction()
        {
            Assert.Equal(PadAiKind.Custom, PadAiAction.Ask.Kind);
            Assert.Equal(24000, PadAiAction.Ask.MaxChars);
            Assert.False(PadAiAction.Ask.RendersMarkdown);
            Assert.Equal("", PadAiAction.Ask.Instruction);
        }

        [Fact]
        public void TooLong_names_the_limit()
        {
            Assert.Null(PadAiAction.Improve.TooLong(8000));
            Assert.Equal("Select less text: at most 8,000 characters for a rewrite", PadAiAction.Improve.TooLong(8001));
            var old = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("th-TH");
                Assert.Equal("Select less text: at most 24,000 characters", PadAiAction.Summarize.TooLong(24001));
            }
            finally { CultureInfo.CurrentCulture = old; }
        }

        [Fact]
        public void PadAiEnabled_is_off_by_default_and_raises_PropertyChanged()
        {
            var c = new AppConfig();
            Assert.False(c.PadAiEnabled);
            string? name = null;
            c.PropertyChanged += (_, e) => name = e.PropertyName;
            c.PadAiEnabled = true;
            Assert.Equal("PadAiEnabled", name);
        }
    }
}
