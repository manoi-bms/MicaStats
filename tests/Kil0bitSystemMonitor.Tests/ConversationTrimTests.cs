using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Kil0bitSystemMonitor.Services.Ai;
using Microsoft.Extensions.AI;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class ConversationTrimTests
    {
        [Fact]
        public void Nothing_is_dropped_when_every_exchange_fits()
        {
            List<ChatMessage> messages = Conversation("one", "answer", "two");

            IReadOnlyList<ChatMessage> fitted = ConversationTrim.Fit(messages, 100, out bool dropped);

            Assert.False(dropped);
            Assert.Equal(messages, fitted);
        }

        [Fact]
        public void The_oldest_exchange_is_dropped_whole()
        {
            List<ChatMessage> messages = Conversation(new string('a', 40), new string('b', 40), "latest");

            IReadOnlyList<ChatMessage> fitted = ConversationTrim.Fit(messages, 8, out bool dropped);

            Assert.True(dropped);
            Assert.Equal(new[] { ChatRole.System, ChatRole.User }, fitted.Select(m => m.Role));
            Assert.Equal("latest", fitted[^1].Text);
        }

        [Fact]
        public void A_tool_call_and_its_result_leave_with_their_exchange()
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "system"),
                new(ChatRole.User, "old"),
                new(ChatRole.Assistant, new List<AIContent>
                {
                    new FunctionCallContent("c1", "get_note", new Dictionary<string, object?> { ["noteId"] = "n1" }),
                }),
                new(ChatRole.Tool, new List<AIContent>
                {
                    new FunctionResultContent("c1", JsonSerializer.SerializeToElement(new { text = new string('x', 200) })),
                }),
                new(ChatRole.Assistant, "old answer"),
                new(ChatRole.User, "latest"),
            };

            IReadOnlyList<ChatMessage> fitted = ConversationTrim.Fit(messages, 8, out bool dropped);

            Assert.True(dropped);
            Assert.Empty(fitted.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
            Assert.Empty(fitted.SelectMany(m => m.Contents).OfType<FunctionResultContent>());
        }

        [Fact]
        public void The_last_user_message_and_system_are_kept_even_when_they_are_over_the_limit()
        {
            List<ChatMessage> messages = Conversation("old", "answer", new string((char)0x0E01, 100));

            IReadOnlyList<ChatMessage> fitted = ConversationTrim.Fit(messages, 1, out bool dropped);

            Assert.True(dropped);
            Assert.Equal(ChatRole.System, fitted[0].Role);
            Assert.Equal(messages[^1], fitted[^1]);
        }

        [Fact]
        public void Tool_result_text_counts_toward_the_limit()
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "s"),
                new(ChatRole.User, "old"),
                new(ChatRole.Tool, new List<AIContent>
                {
                    new FunctionResultContent("c1", new string((char)0x0E01, 100)),
                }),
                new(ChatRole.User, "new"),
            };

            IReadOnlyList<ChatMessage> fitted = ConversationTrim.Fit(messages, 20, out bool dropped);

            Assert.True(dropped);
            Assert.DoesNotContain(fitted.SelectMany(m => m.Contents), c => c is FunctionResultContent);
        }

        private static List<ChatMessage> Conversation(string first, string answer, string last) =>
        [
            new(ChatRole.System, "system"),
            new(ChatRole.User, first),
            new(ChatRole.Assistant, answer),
            new(ChatRole.User, last),
        ];
    }
}
