using System;
using System.Collections.Generic;
using System.Text.Json;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Microsoft.Extensions.AI;

namespace Kil0bitSystemMonitor.Services.Ai
{
    /// <summary>Chooses the newest whole exchanges that fit an Ask request's history budget.</summary>
    public static class ConversationTrim
    {
        /// <summary>
        /// Returns the system message followed by the newest whole exchanges that fit
        /// <paramref name="maxTokens"/>. The newest user exchange is always present, even when it
        /// is over the limit. The input messages and their contents are never changed.
        /// </summary>
        public static IReadOnlyList<ChatMessage> Fit(IReadOnlyList<ChatMessage> messages, int maxTokens, out bool dropped)
        {
            ArgumentNullException.ThrowIfNull(messages);
            int system = FirstSystem(messages);
            var starts = new List<int>();
            for (int i = 0; i < messages.Count; i++)
                if (messages[i].Role == ChatRole.User) starts.Add(i);

            if (starts.Count == 0)
            {
                dropped = messages.Count > (system >= 0 ? 1 : 0);
                return system >= 0 ? new[] { messages[system] } : Array.Empty<ChatMessage>();
            }

            int firstKept = starts.Count - 1;
            int used = system >= 0 ? Tokens(messages[system]) : 0;
            used = SaturatingAdd(used, ExchangeTokens(messages, starts[^1], messages.Count));
            int cap = Math.Max(0, maxTokens);

            for (int exchange = starts.Count - 2; exchange >= 0; exchange--)
            {
                int cost = ExchangeTokens(messages, starts[exchange], starts[exchange + 1]);
                if (used > cap || cost > cap - used) break;
                used += cost;
                firstKept = exchange;
            }

            var result = new List<ChatMessage>(messages.Count - starts[firstKept] + (system >= 0 ? 1 : 0));
            if (system >= 0) result.Add(messages[system]);
            for (int i = starts[firstKept]; i < messages.Count; i++)
                if (messages[i].Role != ChatRole.System) result.Add(messages[i]);
            dropped = result.Count < messages.Count;
            return result;
        }

        private static int FirstSystem(IReadOnlyList<ChatMessage> messages)
        {
            for (int i = 0; i < messages.Count; i++)
                if (messages[i].Role == ChatRole.System) return i;
            return -1;
        }

        private static int ExchangeTokens(IReadOnlyList<ChatMessage> messages, int start, int end)
        {
            int total = 0;
            for (int i = start; i < end; i++) total = SaturatingAdd(total, Tokens(messages[i]));
            return total;
        }

        private static int Tokens(ChatMessage message)
        {
            int total = 0;
            foreach (AIContent content in message.Contents)
            {
                string? text = content switch
                {
                    TextContent t => t.Text,
                    TextReasoningContent r => r.Text,
                    FunctionCallContent call => ToolHistory.ArgsJson(call.Arguments),
                    FunctionResultContent result => ResultText(result.Result),
                    _ => null,
                };
                total = SaturatingAdd(total, TokenEstimate.Of(text));
            }
            return total;
        }

        private static string ResultText(object? result) => result switch
        {
            null => "null",
            JsonElement json => json.GetRawText(),
            string text => text,
            _ => JsonSerializer.Serialize(result, ToolJson.TextOptions),
        };

        private static int SaturatingAdd(int left, int right) =>
            left > int.MaxValue - right ? int.MaxValue : left + right;

    }
}
