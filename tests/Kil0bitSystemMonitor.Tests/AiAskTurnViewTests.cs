using Kil0bitSystemMonitor.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>How streamed answer text is shown in one Ask turn.</summary>
    public class AiAskTurnViewTests
    {
        [Fact]
        public void Line_breaks_before_the_answer_starts_are_not_shown() => UiThread.Run(() =>
        {
            // vLLM-served reasoning models (e.g. with a reasoning parser) start the content with "\n\n".
            var turn = new AskTurnView("q");
            turn.AppendText("\n");
            turn.AppendText(" \n ");
            turn.AppendText("\n\npong");
            Assert.Equal("pong", turn.Answer.Text);
        });

        [Fact]
        public void Line_breaks_inside_the_answer_are_kept() => UiThread.Run(() =>
        {
            var turn = new AskTurnView("q");
            turn.AppendText("first");
            turn.AppendText("\n\n");
            turn.AppendText("second");
            Assert.Equal("first\n\nsecond", turn.Answer.Text);
        });
    }
}
