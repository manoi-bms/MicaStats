using Kil0bitSystemMonitor.Services.Pad.Ai;
using Kil0bitSystemMonitor.Services.Pad.Search;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class PadAiPromptsTests
    {
        [Fact]
        public void ForAction_wraps_the_text_in_note_tags() =>
            Assert.Equal("Task: Translate into English.\n\n<note>\nสวัสดี\n</note>",
                PadAiPrompts.ForAction("Translate into English.", "สวัสดี"));

        [Fact]
        public void ForQuestion_lists_numbered_sources()
        {
            var a = new Passage("n1", "Servers", "Production", 3, 9, "", "body one\n", "", "");
            var b = new Passage("n2", "Notes", "", 1, 2, "", "body two", "", "");
            Assert.Equal(
                "Question: where is the server?\n\nSources:\n[1] Servers — Production (lines 3–9)\nbody one\n\n[2] Notes (lines 1–2)\nbody two",
                PadAiPrompts.ForQuestion("where is the server?", new[] { a, b }));
        }

        [Fact]
        public void System_prompt_holds_the_key_rules()
        {
            Assert.Contains("<note>", PadAiPrompts.System);
            Assert.Contains("[[CREDENTIAL_1]]", PadAiPrompts.System);
            Assert.Contains("cite them as [1], [2]", PadAiPrompts.System);
        }
    }
}
