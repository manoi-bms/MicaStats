using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    [Collection("AnthropicEnv")]
    public class AiAssistantTests : IDisposable
    {
        private readonly AiTestEnv _env = new();
        private readonly FakeMicaData _data = new();
        private readonly ScriptedChatClient _model = new();
        private readonly AiConversation _conversation = new();
        private readonly UsageMeter _usage;
        private int _limit = 100;

        public AiAssistantTests()
        {
            _usage = new UsageMeter(_env.PathOf("ai-usage.json"), () => new DateTime(2026, 9, 30, 12, 0, 0));
        }

        public void Dispose() => _env.Dispose();

        private MicaTools Tools(NoteAccess? notes = null) => new(_data, new Redactor(@"C:\Users\alice", "alice", "DESK-7")) { Notes = notes };

        private AiAssistant Assistant(bool isClaude = false, IChatClient? client = null, TimeSpan? silence = null, NoteAccess? notes = null,
                                      string destination = "") =>
            new(client ?? _model, isClaude, Tools(notes), _usage,
                new AiAssistantOptions
                {
                    DailyLimit = () => _limit,
                    InactivityTimeout = silence ?? TimeSpan.FromSeconds(60),
                    Destination = destination,
                });

        /// <summary>The note tools over a fake reader, allowed for Ask and not for MCP.</summary>
        private static NoteAccess NotesForAsk(FakeNoteReader? reader = null) =>
            new(new Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools(reader ?? new FakeNoteReader()), () => true, () => false);

        private async Task<List<AssistantUpdate>> AskAsync(AiAssistant assistant, string question, CancellationToken ct = default)
        {
            var updates = new List<AssistantUpdate>();
            await foreach (AssistantUpdate update in assistant.AskAsync(_conversation, question, ct)) updates.Add(update);
            // Every question ends with exactly one Done, whatever happened before it.
            Assert.Equal(AssistantUpdateKind.Done, updates[^1].Kind);
            Assert.Single(updates, u => u.Kind == AssistantUpdateKind.Done);
            return updates;
        }

        private static string TextOf(IEnumerable<AssistantUpdate> updates) =>
            string.Concat(updates.Where(u => u.Kind == AssistantUpdateKind.Text).Select(u => u.Text));

        private static IEnumerable<AIContent> Contents(ScriptedChatClient.Request request) =>
            request.Messages.SelectMany(m => m.Contents);

        // ----- the tool loop -----------------------------------------------------------------

        [Fact]
        public async Task A_question_runs_a_tool_and_streams_the_answer()
        {
            _model.Call(ToolNames.GetLiveStatus).Reply("CPU is fine.");

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "How is my CPU?");

            AssistantUpdate used = Assert.Single(updates, u => u.Kind == AssistantUpdateKind.ToolUsed);
            Assert.Equal(ToolNames.GetLiveStatus, used.ToolName);
            Assert.Equal("{}", used.ToolArgs);
            Assert.Equal("CPU is fine.", TextOf(updates));
            Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant },
                _conversation.Messages.Select(m => m.Role));
            Assert.Equal("How is my CPU?", _conversation.Messages[0].Text);
            Assert.Equal(1, _usage.UsedToday);
        }

        [Fact]
        public async Task Tool_results_reach_the_model_as_json_not_as_a_quoted_string()
        {
            _model.Call(ToolNames.GetLiveStatus).Reply("Fine.");

            await AskAsync(Assistant(), "CPU?");

            FunctionResultContent result = Contents(_model.Requests[1]).OfType<FunctionResultContent>().Single();
            JsonElement json = Assert.IsType<JsonElement>(result.Result);
            Assert.Equal(JsonValueKind.Object, json.ValueKind);
            Assert.Equal(12.3, json.GetProperty("cpu").GetProperty("usagePercent").GetDouble());
        }

        [Fact]
        public async Task Every_request_carries_the_system_prompt_the_tools_and_the_output_cap()
        {
            _model.Reply("Hello.");

            await AskAsync(Assistant(), "Hi");

            ScriptedChatClient.Request request = Assert.Single(_model.Requests);
            Assert.Equal(ChatRole.System, request.Messages[0].Role);
            Assert.Equal(AiPrompts.System, request.Messages[0].Text);
            Assert.Equal(ToolNames.ReadOnly.Append(ToolNames.SuggestAction), request.ToolNames);
            Assert.Equal(2000, request.Options!.MaxOutputTokens);
            Assert.DoesNotContain(_conversation.Messages, m => m.Role == ChatRole.System);
        }

        [Fact]
        public async Task With_notes_allowed_every_request_carries_the_same_prompt_and_the_two_note_tools_before_suggest_action()
        {
            _model.Reply("Hello.");

            await AskAsync(Assistant(notes: NotesForAsk()), "Hi");

            ScriptedChatClient.Request request = Assert.Single(_model.Requests);
            Assert.Equal(AiPrompts.System, request.Messages[0].Text);   // one constant either way: Claude caches it
            Assert.Equal(ToolNames.ReadOnly.Concat(ToolNames.Notes).Append(ToolNames.SuggestAction), request.ToolNames);
            Assert.Equal(2000, request.Options!.MaxOutputTokens);
        }

        [Fact]
        public async Task The_previous_exchange_is_sent_with_the_next_question()
        {
            _model.Reply("First.").Reply("Second.");
            AiAssistant assistant = Assistant();

            await AskAsync(assistant, "One?");
            await AskAsync(assistant, "Two?");

            Assert.Equal(new[] { "", "One?", "First.", "Two?" },
                _model.Requests[1].Messages.Select(m => m.Role == ChatRole.System ? "" : m.Text));
            Assert.Equal(2, _usage.UsedToday);
        }

        [Fact]
        public async Task After_eight_tool_rounds_the_model_must_answer_without_tools()
        {
            _model.Otherwise = request => request.ToolNames.Count > 0
                ? _model.CallMessage(ToolNames.GetLiveStatus)
                : new ChatMessage(ChatRole.Assistant, "Final answer.");

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "Watch it closely");

            Assert.Equal(9, _model.Requests.Count);
            Assert.All(_model.Requests.Take(8), r => Assert.Equal(10, r.ToolNames.Count));
            ScriptedChatClient.Request last = _model.Requests[8];
            Assert.Empty(last.ToolNames);
            Assert.DoesNotContain(Contents(last), c => c is FunctionCallContent or FunctionResultContent);
            Assert.Equal(AiPrompts.ToolLimitReached, last.Messages[^1].Text);
            Assert.Equal(8, _data.LatestCalls);
            Assert.Equal(8, updates.Count(u => u.Kind == AssistantUpdateKind.ToolUsed));
            Assert.Equal("Final answer.", TextOf(updates));
            Assert.Equal(1, _usage.UsedToday);
        }

        [Fact]
        public async Task With_notes_allowed_the_eight_rounds_hold_for_the_note_tools_too_and_count_as_one_question()
        {
            var reader = new FakeNoteReader();
            _model.Otherwise = request => request.ToolNames.Count > 0
                ? _model.CallMessage(ToolNames.SearchNotes, new Dictionary<string, object?> { ["query"] = "vpn" })
                : new ChatMessage(ChatRole.Assistant, "Final answer.");

            List<AssistantUpdate> updates = await AskAsync(Assistant(notes: NotesForAsk(reader)), "Read everything");

            Assert.Equal(9, _model.Requests.Count);
            Assert.All(_model.Requests.Take(8), r => Assert.Equal(12, r.ToolNames.Count));
            Assert.Empty(_model.Requests[8].ToolNames);
            Assert.Equal(8, reader.Searches);
            Assert.Equal(8, updates.Count(u => u.Kind == AssistantUpdateKind.ToolUsed));
            Assert.Equal("Final answer.", TextOf(updates));
            Assert.Equal(1, _usage.UsedToday);   // a note tool call does not count against the daily limit
        }

        // ----- note text and the Ask switch (fix round 1) -------------------------------------

        /// <summary>One request as the model reads it: every text, tool call and tool result, in order.</summary>
        private static string Sent(ScriptedChatClient.Request request) => string.Join("\n", Contents(request).Select(c => c switch
        {
            TextContent text => text.Text,
            FunctionCallContent call => call.Name + " " + ToolHistory.ArgsJson(call.Arguments),
            FunctionResultContent result => ResultText(result),
            _ => "",
        }));

        private static FakeNoteReader HandshakeNote() => new()
        {
            Hits = new() { new Kil0bitSystemMonitor.Services.Pad.Ai.NoteHit("a1", "Servers", "Production", 3, 9, false, "the handshake is purple-walrus") },
            Note = new Kil0bitSystemMonitor.Services.Pad.Ai.NoteText("a1", "Servers", "the handshake is purple-walrus\nthe port is 8443"),
        };

        private static string OffResult => "{\"error\":\"" + Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools.Off + "\"}";

        [Fact]
        public async Task Once_the_ask_switch_is_off_note_text_already_read_is_not_sent_again()
        {
            bool allowed = true;
            var notes = new NoteAccess(new Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools(HandshakeNote()), () => allowed, () => true);
            _model.Call(ToolNames.GetLiveStatus)
                  .Call(ToolNames.SearchNotes, new Dictionary<string, object?> { ["query"] = "handshake" })
                  .Call(ToolNames.GetNote, new Dictionary<string, object?> { ["noteId"] = "a1" })
                  .Reply("It is in your Servers note.")
                  .Reply("Still there.")
                  .Reply("The CPU is fine.");

            // The app builds an assistant for each question.
            await AskAsync(Assistant(notes: notes), "What is the handshake?");
            await AskAsync(Assistant(notes: notes), "Sure?");
            string whileOn = Sent(_model.Requests[4]);   // read now: the messages it holds are changed in place below
            allowed = false;
            await AskAsync(Assistant(notes: notes), "And the CPU?");

            // While the switch was on, the conversation carried what was read.
            Assert.Contains("purple-walrus", whileOn, StringComparison.Ordinal);
            // Off: the next request holds the refusal where the note text was, and the PC result as it was.
            ScriptedChatClient.Request afterOff = _model.Requests[5];
            Assert.DoesNotContain("purple-walrus", Sent(afterOff), StringComparison.Ordinal);
            Assert.DoesNotContain("8443", Sent(afterOff), StringComparison.Ordinal);
            string[] results = Contents(afterOff).OfType<FunctionResultContent>().Select(ResultText).ToArray();
            Assert.Equal(3, results.Length);
            Assert.Contains("usagePercent", results[0], StringComparison.Ordinal);
            Assert.Equal(new[] { OffResult, OffResult }, results.Skip(1));
            Assert.Equal(ToolNames.ReadOnly.Append(ToolNames.SuggestAction), afterOff.ToolNames);
            // In place: the conversation no longer holds the text, so nothing later can send it either.
            Assert.DoesNotContain("purple-walrus",
                string.Concat(_conversation.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(ResultText)),
                StringComparison.Ordinal);
        }

        [Fact]
        public async Task Once_the_ask_switch_is_off_limited_mode_does_not_send_note_text_either()
        {
            bool allowed = true;
            var notes = new NoteAccess(new Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools(HandshakeNote()), () => allowed, () => true);
            _model.Call(ToolNames.GetNote, new Dictionary<string, object?> { ["noteId"] = "a1" })
                  .Reply("It is in your Servers note.")
                  .Fail(new ClientResultException("registry.ollama.ai/library/gemma:2b does not support tools"))
                  .Reply("From the snapshot: fine.");

            await AskAsync(Assistant(notes: notes), "What is the handshake?");
            allowed = false;
            List<AssistantUpdate> second = await AskAsync(Assistant(notes: notes), "And the CPU?");

            Assert.Equal(AssistantUpdateKind.LimitedMode, second[0].Kind);
            ScriptedChatClient.Request refused = _model.Requests[2], limited = _model.Requests[3];
            Assert.Empty(limited.ToolNames);
            foreach (ScriptedChatClient.Request request in new[] { refused, limited })
            {
                Assert.DoesNotContain("purple-walrus", Sent(request), StringComparison.Ordinal);
                Assert.Contains(Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools.Off, Sent(request), StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// An assistant that already knows its endpoint cannot use tools sends one request per
        /// question, and it never passes the tool loop: the conversation itself must be clean.
        /// </summary>
        [Fact]
        public async Task An_assistant_already_in_limited_mode_does_not_send_note_text_once_the_switch_is_off()
        {
            bool allowed = true;
            var notes = new NoteAccess(new Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools(HandshakeNote()), () => allowed, () => true);
            _model.Call(ToolNames.GetNote, new Dictionary<string, object?> { ["noteId"] = "a1" })
                  .Reply("It is in your Servers note.")
                  .Fail(new ClientResultException("registry.ollama.ai/library/gemma:2b does not support tools"))
                  .Reply("From the snapshot: fine.")
                  .Reply("Still fine.");
            await AskAsync(Assistant(notes: notes), "What is the handshake?");
            AiAssistant limited = Assistant(notes: notes);
            await AskAsync(limited, "And the CPU?");   // finds out, and answers in limited mode
            string whileOn = Sent(_model.Requests[3]);

            allowed = false;
            await AskAsync(limited, "And now?");

            Assert.Contains("purple-walrus", whileOn, StringComparison.Ordinal);
            Assert.Equal(5, _model.Requests.Count);    // one request: no tool attempt this time
            Assert.Empty(_model.Requests[4].ToolNames);
            Assert.DoesNotContain("purple-walrus", Sent(_model.Requests[4]), StringComparison.Ordinal);
            Assert.Contains(Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools.Off, Sent(_model.Requests[4]), StringComparison.Ordinal);
        }

        /// <summary>The switch is asked right before every request, also between two tool rounds of one question.</summary>
        [Fact]
        public async Task A_switch_turned_off_in_the_middle_of_a_question_takes_the_note_text_out_of_its_next_request()
        {
            bool allowed = true;
            var notes = new NoteAccess(new Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools(HandshakeNote()), () => allowed, () => true);
            var sent = new List<string>();
            _model.Otherwise = request =>
            {
                sent.Add(Sent(request));
                switch (sent.Count)
                {
                    case 1:
                        return _model.CallMessage(ToolNames.GetNote, new Dictionary<string, object?> { ["noteId"] = "a1" });
                    case 2:
                        allowed = false;   // turned off in Settings while the model works
                        return _model.CallMessage(ToolNames.GetLiveStatus);
                    default:
                        return new ChatMessage(ChatRole.Assistant, "The CPU is fine.");
                }
            };

            await AskAsync(Assistant(notes: notes), "What is the handshake, and how is the CPU?");

            Assert.Equal(3, sent.Count);
            Assert.Contains("purple-walrus", sent[1], StringComparison.Ordinal);        // read while it was allowed
            Assert.DoesNotContain("purple-walrus", sent[2], StringComparison.Ordinal);
            Assert.Contains(OffResult, sent[2], StringComparison.Ordinal);
            Assert.Contains("usagePercent", sent[2], StringComparison.Ordinal);         // the PC result is untouched
        }

        [Fact]
        public async Task A_switch_that_cannot_be_read_takes_the_note_text_back_too()
        {
            bool broken = false;
            var notes = new NoteAccess(new Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools(HandshakeNote()),
                () => broken ? throw new InvalidOperationException("the config is gone") : true, () => true);
            _model.Call(ToolNames.GetNote, new Dictionary<string, object?> { ["noteId"] = "a1" }).Reply("In Servers.").Reply("Fine.");

            await AskAsync(Assistant(notes: notes), "What is the handshake?");
            broken = true;
            await AskAsync(Assistant(notes: notes), "And the CPU?");

            Assert.DoesNotContain("purple-walrus", Sent(_model.Requests[2]), StringComparison.Ordinal);
            Assert.Equal(OffResult, ResultText(Contents(_model.Requests[2]).OfType<FunctionResultContent>().Single()));
        }

        /// <summary>
        /// A provider may give two calls of one message the same id. What the tool loop reports
        /// then names only one of them, so the conversation is marked by the note function itself.
        /// </summary>
        [Fact]
        public async Task A_note_tool_that_returned_notes_marks_the_conversation_whatever_call_ids_the_provider_reports()
        {
            var reader = new FakeNoteReader();
            _model.Otherwise = request => request.Messages.Any(m => m.Role == ChatRole.Tool)
                ? new ChatMessage(ChatRole.Assistant, "The gateway is in Servers.")
                : new ChatMessage(ChatRole.Assistant, new List<AIContent>
                {
                    new FunctionCallContent("same", ToolNames.SearchNotes, new Dictionary<string, object?> { ["query"] = "vpn" }),
                    new FunctionCallContent("same", ToolNames.GetLiveStatus, new Dictionary<string, object?>()),
                });
            Assert.False(_conversation.NotesRead);

            List<AssistantUpdate> updates = await AskAsync(Assistant(notes: NotesForAsk(reader)), "What is my VPN gateway?");

            Assert.Equal(1, reader.Searches);
            Assert.True(_conversation.NotesRead);
            // Why the tool names cannot be what decides: the note tool is not among them.
            Assert.DoesNotContain(updates, u => u.Kind == AssistantUpdateKind.ToolUsed && u.ToolName == ToolNames.SearchNotes);
            _conversation.Clear();
            Assert.False(_conversation.NotesRead);
        }

        [Fact]
        public async Task A_conversation_is_not_marked_when_no_note_text_came_back()
        {
            var reader = new FakeNoteReader();
            bool allowed = true;
            var notes = new NoteAccess(new Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools(reader), () => allowed, () => true);
            _model.Call(ToolNames.GetLiveStatus).Reply("Fine.")
                  .Call(ToolNames.SearchNotes, new Dictionary<string, object?> { ["query"] = "vpn" }).Reply("I could not look.");

            await AskAsync(Assistant(notes: notes), "How is the CPU?");
            AiAssistant built = Assistant(notes: notes);   // offered the note tools while the switch was on
            allowed = false;
            await AskAsync(built, "What is my VPN gateway?");

            Assert.Equal(0, reader.Searches);
            Assert.False(_conversation.NotesRead);
        }

        // ----- what the model made of the notes (final review, A1) ---------------------------

        private const string Removed = "(Removed: this answer used your notes, and notes access has changed.)";

        private static Dictionary<string, object?> NoteA1 => new() { ["noteId"] = "a1" };

        /// <summary>What a provider checks before it takes a request: every tool result has its call before it, and every call its result.</summary>
        private static void AssertCallsPair(ScriptedChatClient.Request request)
        {
            var waiting = new List<string>();
            foreach (AIContent content in Contents(request))
            {
                if (content is FunctionCallContent call) waiting.Add(call.CallId);
                else if (content is FunctionResultContent result) Assert.True(waiting.Remove(result.CallId), "a result without its call: " + result.CallId);
            }
            Assert.Empty(waiting);
        }

        /// <summary>The text of each message of a request after the system prompt; a tool call and a tool result read as "".</summary>
        private static string[] Texts(ScriptedChatClient.Request request) => request.Messages.Skip(1).Select(m => m.Text).ToArray();

        [Fact]
        public async Task Once_the_ask_switch_is_off_an_answer_that_quoted_a_note_is_not_sent_again()
        {
            bool allowed = true;
            var notes = new NoteAccess(new Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools(HandshakeNote()), () => allowed, () => true);
            _model.Reply("Hello.")
                  .Call(ToolNames.GetNote, NoteA1)
                  .Reply("The handshake is purple-walrus.")
                  .Reply("Yes, purple-walrus, on port 8443.")
                  .Reply("The CPU is fine.")
                  .Reply("Still fine.");

            await AskAsync(Assistant(notes: notes), "Hi");
            await AskAsync(Assistant(notes: notes), "What is the handshake?");
            await AskAsync(Assistant(notes: notes), "Sure?");
            allowed = false;
            await AskAsync(Assistant(notes: notes), "And the CPU?");
            await AskAsync(Assistant(notes: notes), "And now?");

            ScriptedChatClient.Request afterOff = _model.Requests[4];
            Assert.DoesNotContain("purple-walrus", Sent(afterOff), StringComparison.Ordinal);
            Assert.DoesNotContain("8443", Sent(afterOff), StringComparison.Ordinal);
            // The user's questions stay, and so does the answer given before any note was read.
            // The two answers that used the note are replaced.
            Assert.Equal(new[] { "Hi", "Hello.", "What is the handshake?", "", "", Removed, "Sure?", Removed, "And the CPU?" }, Texts(afterOff));
            Assert.Equal(OffResult, ResultText(Contents(afterOff).OfType<FunctionResultContent>().Single()));
            AssertCallsPair(afterOff);

            // An answer given after the take-back used no note, so it stays for the questions after it.
            ScriptedChatClient.Request later = _model.Requests[5];
            Assert.Equal(new[] { "Hi", "Hello.", "What is the handshake?", "", "", Removed, "Sure?", Removed, "And the CPU?", "The CPU is fine.", "And now?" },
                Texts(later));
            // In place: the conversation no longer holds the text, so nothing later can send it either.
            Assert.DoesNotContain("purple-walrus", string.Join("\n", _conversation.Messages.Select(m => m.Text)), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Once_the_ask_switch_is_off_limited_mode_does_not_send_an_answer_that_quoted_a_note_either()
        {
            bool allowed = true;
            var notes = new NoteAccess(new Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools(HandshakeNote()), () => allowed, () => true);
            _model.Call(ToolNames.GetNote, NoteA1)
                  .Reply("The handshake is purple-walrus.")
                  .Fail(new ClientResultException("registry.ollama.ai/library/gemma:2b does not support tools"))
                  .Reply("From the snapshot: fine.");

            await AskAsync(Assistant(notes: notes), "What is the handshake?");
            allowed = false;
            List<AssistantUpdate> second = await AskAsync(Assistant(notes: notes), "And the CPU?");

            Assert.Equal(AssistantUpdateKind.LimitedMode, second[0].Kind);
            ScriptedChatClient.Request refused = _model.Requests[2], limited = _model.Requests[3];
            Assert.Empty(limited.ToolNames);
            foreach (ScriptedChatClient.Request request in new[] { refused, limited })
            {
                Assert.DoesNotContain("purple-walrus", Sent(request), StringComparison.Ordinal);
                Assert.Contains(Removed, Sent(request), StringComparison.Ordinal);
                Assert.Contains("What is the handshake?", Sent(request), StringComparison.Ordinal);   // the user's own question stays
            }
        }

        /// <summary>An assistant that already knows its endpoint cannot use tools sends one request, which never passes the tool loop.</summary>
        [Fact]
        public async Task An_assistant_already_in_limited_mode_does_not_send_an_answer_that_quoted_a_note_once_the_switch_is_off()
        {
            bool allowed = true;
            var notes = new NoteAccess(new Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools(HandshakeNote()), () => allowed, () => true);
            _model.Call(ToolNames.GetNote, NoteA1)
                  .Reply("The handshake is purple-walrus.")
                  .Fail(new ClientResultException("registry.ollama.ai/library/gemma:2b does not support tools"))
                  .Reply("From the snapshot: fine.")
                  .Reply("Still fine.");
            await AskAsync(Assistant(notes: notes), "What is the handshake?");
            AiAssistant limited = Assistant(notes: notes);
            await AskAsync(limited, "And the CPU?");   // finds out, and answers in limited mode
            string whileOn = Sent(_model.Requests[3]);

            allowed = false;
            await AskAsync(limited, "And now?");

            Assert.Contains("purple-walrus", whileOn, StringComparison.Ordinal);
            Assert.Equal(5, _model.Requests.Count);
            Assert.DoesNotContain("purple-walrus", Sent(_model.Requests[4]), StringComparison.Ordinal);
            Assert.Contains(Removed, Sent(_model.Requests[4]), StringComparison.Ordinal);
        }

        /// <summary>
        /// Notes read through one provider do not follow the conversation to another (a local
        /// server, then Claude), although the switch is on all the while.
        /// </summary>
        [Fact]
        public async Task When_the_provider_changes_what_was_read_from_notes_and_the_answers_that_used_it_do_not_follow()
        {
            NoteAccess notes = NotesForAsk(HandshakeNote());
            _model.Call(ToolNames.GetNote, NoteA1)
                  .Reply("The handshake is purple-walrus.")
                  .Reply("I do not have it any more.")
                  .Call(ToolNames.GetNote, NoteA1)
                  .Reply("It is purple-walrus.")
                  .Reply("Yes: purple-walrus.");

            await AskAsync(Assistant(notes: notes, destination: "this PC"), "What is the handshake?");
            Assert.True(_conversation.NotesRead);
            Assert.Equal("this PC", _conversation.NotesDestination);

            await AskAsync(Assistant(notes: notes, destination: "api.anthropic.com"), "Tell me again?");

            ScriptedChatClient.Request moved = _model.Requests[2];
            Assert.DoesNotContain("purple-walrus", Sent(moved), StringComparison.Ordinal);
            Assert.DoesNotContain("8443", Sent(moved), StringComparison.Ordinal);
            Assert.Equal(new[] { "What is the handshake?", "", "", Removed, "Tell me again?" }, Texts(moved));
            Assert.Equal(OffResult, ResultText(Contents(moved).OfType<FunctionResultContent>().Single()));
            AssertCallsPair(moved);
            // The switch is on: the tools are still offered, to read again for this provider.
            Assert.Equal(ToolNames.ReadOnly.Concat(ToolNames.Notes).Append(ToolNames.SuggestAction), moved.ToolNames);
            // Nothing of the notes is left in the conversation, so it counts as not having read any.
            Assert.False(_conversation.NotesRead);
            Assert.Null(_conversation.NotesDestination);

            await AskAsync(Assistant(notes: notes, destination: "api.anthropic.com"), "Look it up again");
            Assert.True(_conversation.NotesRead);
            Assert.Equal("api.anthropic.com", _conversation.NotesDestination);

            await AskAsync(Assistant(notes: notes, destination: "api.anthropic.com"), "Sure?");

            // Read for this provider: it stays in the conversation, as before.
            ScriptedChatClient.Request same = _model.Requests[5];
            Assert.Contains("the handshake is purple-walrus", Sent(same), StringComparison.Ordinal);
            Assert.Contains("It is purple-walrus.", Texts(same));
            Assert.DoesNotContain("The handshake is purple-walrus.", Texts(same));   // what the first provider answered stays out
        }

        [Fact]
        public async Task A_conversation_that_never_read_notes_is_untouched_by_a_switch_turned_off_and_a_new_provider()
        {
            bool allowed = true;
            var notes = new NoteAccess(new Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools(HandshakeNote()), () => allowed, () => true);
            _model.Call(ToolNames.GetLiveStatus).Reply("The CPU is at 12%.").Reply("Still 12%.").Reply("Yes.");

            await AskAsync(Assistant(notes: notes, destination: "this PC"), "How is the CPU?");
            await AskAsync(Assistant(notes: notes, destination: "this PC"), "And now?");
            string before = Sent(_model.Requests[2]);
            allowed = false;
            await AskAsync(Assistant(notes: notes, destination: "api.anthropic.com"), "Sure?");

            Assert.False(_conversation.NotesRead);
            ScriptedChatClient.Request after = _model.Requests[3];
            Assert.StartsWith(before, Sent(after), StringComparison.Ordinal);   // everything sent before is sent again as it was
            Assert.Equal(new[] { "How is the CPU?", "", "", "The CPU is at 12%.", "And now?", "Still 12%.", "Sure?" }, Texts(after));
            Assert.Contains("usagePercent", Sent(after), StringComparison.Ordinal);
            AssertCallsPair(after);
        }

        /// <summary>A note tool that gave no notes (no such note, a refusal) is no reason to take an answer out.</summary>
        [Fact]
        public async Task An_answer_after_a_note_tool_that_returned_no_notes_stays()
        {
            bool allowed = true;
            var notes = new NoteAccess(new Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools(new FakeNoteReader { AnswersNull = true }), () => allowed, () => true);
            _model.Call(ToolNames.GetNote, new Dictionary<string, object?> { ["noteId"] = "zz" })
                  .Reply("There is no such note.")
                  .Reply("Fine.");

            await AskAsync(Assistant(notes: notes, destination: "this PC"), "What is in note zz?");
            allowed = false;
            await AskAsync(Assistant(notes: notes, destination: "api.anthropic.com"), "And the CPU?");

            Assert.Equal(new[] { "What is in note zz?", "", "", "There is no such note.", "And the CPU?" }, Texts(_model.Requests[2]));
            AssertCallsPair(_model.Requests[2]);
        }

        /// <summary>
        /// With the Ask switch off the note tools are not offered, but the system prompt names
        /// them, so a model may call one all the same. The tool loop answers that call with a
        /// sentence of its own, not with anything from the notes: the answer after it used none.
        /// </summary>
        [Fact]
        public async Task An_answer_after_a_note_tool_that_was_never_offered_stays()
        {
            var reader = new FakeNoteReader();
            var notes = new NoteAccess(new Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools(reader), () => false, () => true);
            _model.Call(ToolNames.SearchNotes, new Dictionary<string, object?> { ["query"] = "vpn" })
                  .Reply("I cannot search your notes here.")
                  .Reply("The CPU is fine.");

            await AskAsync(Assistant(notes: notes), "What is my VPN gateway?");
            object? answered = _conversation.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result;
            await AskAsync(Assistant(notes: notes), "And the CPU?");

            Assert.Equal(ToolNames.ReadOnly.Append(ToolNames.SuggestAction), _model.Requests[0].ToolNames);   // not offered
            Assert.Equal("Error: Requested function \"search_notes\" not found.", answered);                  // the tool loop's own words, as kept
            Assert.Equal(0, reader.Searches);
            Assert.False(_conversation.NotesRead);
            Assert.False(_conversation.NotesEverRead);
            Assert.Equal(new[] { "What is my VPN gateway?", "", "", "I cannot search your notes here.", "And the CPU?" }, Texts(_model.Requests[2]));
            Assert.Equal("I cannot search your notes here.", _conversation.Messages[3].Text);                // and it stays in the conversation
            AssertCallsPair(_model.Requests[2]);
        }

        /// <summary>A conversation of one question that called <c>search_notes</c> and got <paramref name="result"/>, then an answer.</summary>
        private static List<ChatMessage> AfterNoteTool(object? result, Exception? threw = null) => new()
        {
            new ChatMessage(ChatRole.User, "What is my VPN gateway?"),
            new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("c1", ToolNames.SearchNotes, new Dictionary<string, object?> { ["query"] = "vpn" }),
            }),
            new ChatMessage(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("c1", result) { Exception = threw } }),
            new ChatMessage(ChatRole.Assistant, "The answer."),
        };

        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        /// <summary>What can hold no note text: the tool loop's own sentence, a tool's error, a function that threw.</summary>
        [Fact]
        public void A_note_tool_result_that_can_hold_no_note_text_is_no_note_read()
        {
            var noNotes = new (object? Result, Exception? Threw)[]
            {
                ("Error: Requested function \"search_notes\" not found.", null),
                ("Error: Function failed.", new InvalidOperationException("boom")),
                ("Error: Function failed. Exception: boom", new InvalidOperationException("boom")),
                (Json("{\"error\":\"Notes are not ready: open MicaPad once\"}"), null),
                (Json(OffResult), null),
            };

            foreach ((object? result, Exception? threw) in noNotes)
            {
                List<ChatMessage> messages = AfterNoteTool(result, threw);

                ToolHistory.TakeBackNotes(messages);

                Assert.Equal("The answer.", messages[3].Text);
                // The call's own result reads as the refusal either way.
                Assert.Equal(OffResult, ResultText(messages[2].Contents.OfType<FunctionResultContent>().Single()));
            }
        }

        /// <summary>
        /// Strict for a real read: a result as the tool gave it, and every shape that is not known
        /// to be an error. A result shortened to text is a plain string too, and it holds notes.
        /// </summary>
        [Fact]
        public void Every_other_note_tool_result_counts_as_a_note_read()
        {
            string found = "{\"query\":\"vpn\",\"results\":[{\"noteId\":\"a1\",\"text\":\"the vpn gateway is 10.0.0.7\"}],\"about\":\"x\"}";
            var notes = new object?[]
            {
                Json(found),
                found,                                                            // the same as text
                ToolHistory.Cap("{\"noteId\":\"a1\",\"text\":\"" + new string('n', 30_000) + "\"}"),   // shortened to text: cut inside its JSON
                ToolHistory.Cap("{\"noteId\":\"a1\",\"text\":\"Error: " + new string('n', 30_000) + "\"}"),   // and of a note that begins with "Error:"
                "Error is what the note is about: the vpn gateway is 10.0.0.7",   // not the tool loop's "Error:" sentence
                " Error: with a space before it",
                "error: in small letters",
                Json("[\"the vpn gateway is 10.0.0.7\"]"),
                Json("\"the vpn gateway is 10.0.0.7\""),
                null,
            };

            foreach (object? result in notes)
            {
                List<ChatMessage> messages = AfterNoteTool(result);

                ToolHistory.TakeBackNotes(messages);

                Assert.Equal(Removed, messages[3].Text);
                Assert.Equal(OffResult, ResultText(messages[2].Contents.OfType<FunctionResultContent>().Single()));
            }
        }

        /// <summary>
        /// A note may itself begin with "Error:", in its title, a heading and its text. What a
        /// note tool gives back is never that text alone: it is a JSON object and is kept as one,
        /// so the note's first words cannot pass for the tool loop's sentence.
        /// </summary>
        [Fact]
        public async Task A_note_that_begins_with_Error_is_still_taken_back_with_the_answer_that_used_it()
        {
            bool allowed = true;
            const string title = "Error: Requested function \"search_notes\" not found.";
            var reader = new FakeNoteReader
            {
                Note = new Kil0bitSystemMonitor.Services.Pad.Ai.NoteText("a1", title, "Error: the handshake is purple-walrus"),
                Hits = new() { new Kil0bitSystemMonitor.Services.Pad.Ai.NoteHit("a1", title, "Error:", 1, 1, true, "Error: the handshake is purple-walrus") },
            };
            var notes = new NoteAccess(new Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools(reader), () => allowed, () => true);
            _model.Call(ToolNames.SearchNotes, new Dictionary<string, object?> { ["query"] = "Error: handshake" })
                  .Call(ToolNames.GetNote, NoteA1)
                  .Reply("The handshake is purple-walrus.")
                  .Reply("Fine.");

            await AskAsync(Assistant(notes: notes), "What is the handshake?");
            FunctionResultContent[] kept = _conversation.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToArray();
            string[] keptAs = kept.Select(ResultText).ToArray();
            allowed = false;
            await AskAsync(Assistant(notes: notes), "And the CPU?");

            // The kept form of each result: a JSON object, whose text begins with its brace. The
            // note's own "Error:" stands inside a field of it.
            Assert.Equal(2, kept.Length);
            Assert.All(kept, result => Assert.Equal(JsonValueKind.Object, Assert.IsType<JsonElement>(result.Result).ValueKind));
            Assert.All(keptAs, form => Assert.StartsWith("{", form, StringComparison.Ordinal));
            Assert.All(keptAs, form => Assert.Contains("Error: the handshake is purple-walrus", form, StringComparison.Ordinal));

            ScriptedChatClient.Request afterOff = _model.Requests[3];
            Assert.DoesNotContain("purple-walrus", Sent(afterOff), StringComparison.Ordinal);
            Assert.Equal(new[] { "What is the handshake?", "", "", "", "", Removed, "And the CPU?" }, Texts(afterOff));
            Assert.Equal(new[] { OffResult, OffResult }, Contents(afterOff).OfType<FunctionResultContent>().Select(ResultText));
            AssertCallsPair(afterOff);
        }

        /// <summary>
        /// Between two rounds of one question: an assistant message can carry words and a tool call
        /// at once. Its words go, its call stays with its result.
        /// </summary>
        [Fact]
        public async Task A_switch_turned_off_in_the_middle_of_a_question_takes_out_what_the_model_said_about_the_note()
        {
            bool allowed = true;
            var notes = new NoteAccess(new Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools(HandshakeNote()), () => allowed, () => true);
            var sent = new List<ScriptedChatClient.Request>();
            var texts = new List<string>();
            _model.Otherwise = request =>
            {
                sent.Add(request);
                texts.Add(Sent(request));
                switch (sent.Count)
                {
                    case 1:
                        return _model.CallMessage(ToolNames.GetNote, NoteA1);
                    case 2:
                        allowed = false;   // turned off in Settings while the model works
                        return new ChatMessage(ChatRole.Assistant, new List<AIContent>
                        {
                            new TextContent("The note says purple-walrus. Now the CPU."),
                            _model.CallMessage(ToolNames.GetLiveStatus).Contents[0],
                        });
                    default:
                        return new ChatMessage(ChatRole.Assistant, "The CPU is fine.");
                }
            };

            await AskAsync(Assistant(notes: notes), "What is the handshake, and how is the CPU?");

            Assert.Equal(3, sent.Count);
            Assert.Contains("purple-walrus", texts[1], StringComparison.Ordinal);        // read while it was allowed
            Assert.DoesNotContain("purple-walrus", texts[2], StringComparison.Ordinal);
            Assert.Contains(Removed, texts[2], StringComparison.Ordinal);
            Assert.Contains(OffResult, texts[2], StringComparison.Ordinal);
            Assert.Contains("usagePercent", texts[2], StringComparison.Ordinal);         // the PC result is untouched
            AssertCallsPair(sent[2]);
        }

        /// <summary>
        /// A call the model makes after it read a note can quote the note in its arguments, and a
        /// tool can repeat them: an error names the bad argument, and suggest_action its label.
        /// </summary>
        [Fact]
        public async Task What_the_model_wrote_into_later_tool_calls_is_taken_back_with_its_answers()
        {
            bool allowed = true;
            var notes = new NoteAccess(new Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools(HandshakeNote()), () => allowed, () => true);
            _model.Call(ToolNames.GetNote, NoteA1)
                  .Call(ToolNames.GetHistory, new Dictionary<string, object?> { ["metric"] = "purple-walrus", ["from"] = "-1h" })
                  .Call(ToolNames.SuggestAction, new Dictionary<string, object?>
                  {
                      ["kind"] = "open_diagnostics",
                      ["reason"] = "The note says purple-walrus.",
                      ["label"] = "purple-walrus",
                  })
                  .Call(ToolNames.GetLiveStatus)
                  .Reply("The handshake is purple-walrus.")
                  .Reply("Fine.");

            await AskAsync(Assistant(notes: notes), "What is the handshake?");
            string whileOn = string.Join("\n", _conversation.Messages.SelectMany(m => m.Contents).Select(c => c switch
            {
                FunctionCallContent call => ToolHistory.ArgsJson(call.Arguments),
                FunctionResultContent result => ResultText(result),
                _ => "",
            }));
            allowed = false;
            await AskAsync(Assistant(notes: notes), "And the CPU?");

            // Why it matters: the conversation kept the model's quote three times outside its answer.
            Assert.Contains("\"metric\":\"purple-walrus\"", whileOn, StringComparison.Ordinal);
            Assert.Contains("Unknown metric 'purple-walrus'", whileOn.Replace("\\u0027", "'", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.Contains("labelled 'purple-walrus'", whileOn.Replace("\\u0027", "'", StringComparison.Ordinal), StringComparison.Ordinal);

            ScriptedChatClient.Request afterOff = _model.Requests[5];
            Assert.DoesNotContain("purple-walrus", Sent(afterOff), StringComparison.Ordinal);
            AssertCallsPair(afterOff);
            FunctionCallContent[] calls = Contents(afterOff).OfType<FunctionCallContent>().ToArray();
            Assert.Equal(new[] { ToolNames.GetNote, ToolNames.GetHistory, ToolNames.SuggestAction, ToolNames.GetLiveStatus }, calls.Select(c => c.Name));
            Assert.Equal("{\"noteId\":\"a1\"}", ToolHistory.ArgsJson(calls[0].Arguments));   // asked before any note was read: as it was
            Assert.All(calls.Skip(1), call => Assert.Equal("{}", ToolHistory.ArgsJson(call.Arguments)));
            string[] results = Contents(afterOff).OfType<FunctionResultContent>().Select(ResultText).ToArray();
            Assert.Equal(OffResult, results[0]);
            Assert.Contains("usagePercent", results[3], StringComparison.Ordinal);            // what the PC measured stays
        }

        // ----- a suggestion after notes were read (final review, A2) ---------------------------

        private static Dictionary<string, object?> EndChrome => new()
        {
            ["kind"] = "end_process",
            ["pid"] = 4242,
            ["createTime"] = 134037504000000000L,
            ["processName"] = "chrome.exe",
            ["reason"] = "Your note says to end it.",
        };

        /// <summary>Pasted text in a note can steer the model: the one destructive button is not offered on its word.</summary>
        [Fact]
        public async Task Once_notes_were_read_a_suggestion_to_end_a_process_is_dropped_and_the_other_kinds_are_not()
        {
            _model.Call(ToolNames.GetNote, NoteA1)
                  .Call(ToolNames.SuggestAction, EndChrome)
                  .Call(ToolNames.SuggestAction, new Dictionary<string, object?> { ["kind"] = "open_diagnostics", ["reason"] = "See the reports." })
                  .Reply("Done.")
                  .Call(ToolNames.SuggestAction, EndChrome)
                  .Reply("Done again.");
            NoteAccess notes = NotesForAsk(HandshakeNote());

            List<AssistantUpdate> first = await AskAsync(Assistant(notes: notes), "What does my note say to do?");

            SuggestedAction kept = Assert.Single(first, u => u.Kind == AssistantUpdateKind.Suggestion).Suggestion!;
            Assert.Equal(SuggestedActionKind.OpenDiagnostics, kept.Kind);
            Assert.Equal(kept, Assert.Single(_conversation.Suggestions));
            // The model is told there is no button, so it does not say there is one.
            JsonElement dropped = (JsonElement)Contents(_model.Requests[2]).OfType<FunctionResultContent>().Last().Result!;
            Assert.False(dropped.TryGetProperty("recorded", out _));
            Assert.Contains("notes", dropped.GetProperty("error").GetString(), StringComparison.Ordinal);

            // A later question of the same conversation: the note is still in it.
            List<AssistantUpdate> second = await AskAsync(Assistant(notes: notes), "End it then");

            Assert.DoesNotContain(second, u => u.Kind == AssistantUpdateKind.Suggestion);
            Assert.Empty(_conversation.Suggestions);
        }

        // ----- two facts about notes: held now, and read once (re-check, residual 5) -----------

        /// <summary>
        /// A provider change takes the notes out of what is sent, and for the new destination the
        /// conversation holds none. But it did read notes, and that lasts: the one destructive
        /// button stays away for the rest of the conversation, as the links in its answers stay text.
        /// </summary>
        [Fact]
        public async Task After_a_provider_change_took_the_notes_back_a_suggestion_to_end_a_process_is_still_dropped()
        {
            NoteAccess notes = NotesForAsk(HandshakeNote());
            _model.Call(ToolNames.GetNote, NoteA1)
                  .Reply("Your note says to end chrome.exe.")
                  .Call(ToolNames.SuggestAction, EndChrome)
                  .Call(ToolNames.SuggestAction, new Dictionary<string, object?> { ["kind"] = "open_diagnostics", ["reason"] = "See the reports." })
                  .Reply("Done.");

            await AskAsync(Assistant(notes: notes, destination: "this PC"), "What does my note say to do?");
            Assert.True(_conversation.NotesRead);
            Assert.True(_conversation.NotesEverRead);

            List<AssistantUpdate> second = await AskAsync(Assistant(notes: notes, destination: "api.anthropic.com"), "End it then");

            // Taken back: nothing of the notes goes to the new destination, and none is held for it.
            Assert.DoesNotContain("purple-walrus", Sent(_model.Requests[2]), StringComparison.Ordinal);
            Assert.Contains(Removed, Texts(_model.Requests[2]));
            Assert.False(_conversation.NotesRead);
            Assert.Null(_conversation.NotesDestination);
            // What lasts: this conversation read notes.
            Assert.True(_conversation.NotesEverRead);
            SuggestedAction kept = Assert.Single(second, u => u.Kind == AssistantUpdateKind.Suggestion).Suggestion!;
            Assert.Equal(SuggestedActionKind.OpenDiagnostics, kept.Kind);        // the other kinds stay
            Assert.Equal(kept, Assert.Single(_conversation.Suggestions));
            JsonElement dropped = (JsonElement)Contents(_model.Requests[3]).OfType<FunctionResultContent>().Last().Result!;
            Assert.Equal(AiAssistant.NoEndProcessAfterNotes, dropped.GetProperty("error").GetString());   // and the model is told there is no button
        }

        [Fact]
        public void That_a_conversation_read_notes_is_set_by_a_read_and_cleared_only_by_starting_over()
        {
            var conversation = new AiConversation();
            Assert.False(conversation.NotesRead);
            Assert.False(conversation.NotesEverRead);                            // a new conversation has read none

            conversation.MarkNotesRead("this PC");
            Assert.True(conversation.NotesRead);
            Assert.True(conversation.NotesEverRead);

            conversation.ForgetNotesRead();                                      // a take-back for another destination
            Assert.False(conversation.NotesRead);
            Assert.Null(conversation.NotesDestination);
            Assert.True(conversation.NotesEverRead);

            conversation.MarkNotesRead("api.anthropic.com");                     // read again, for the new one
            Assert.Equal("api.anthropic.com", conversation.NotesDestination);
            Assert.True(conversation.NotesEverRead);

            conversation.Clear();
            Assert.False(conversation.NotesRead);
            Assert.False(conversation.NotesEverRead);                            // and so has a cleared one
            Assert.Null(conversation.NotesDestination);
        }

        // ----- the question itself (final review, A3) ------------------------------------------

        [Fact]
        public async Task A_credential_marker_in_the_question_reaches_the_model_as_credential()
        {
            _model.Reply("It is a stored credential.").Reply("Still.");
            AiAssistant assistant = Assistant();

            await AskAsync(assistant, "What is {{secret:K7Q2M9XD}} for, and {{secret:K7Q2");
            await AskAsync(assistant, "M9XD}} and this?");

            Assert.Equal("What is [credential] for, and [credential]", _model.Requests[0].Messages[^1].Text);
            Assert.Equal("[credential] and this?", _model.Requests[1].Messages[^1].Text);
            foreach (string sent in _model.Requests.Select(Sent).Concat(_conversation.Messages.Select(m => m.Text)))
            {
                Assert.DoesNotContain("secret", sent, StringComparison.Ordinal);
                Assert.DoesNotContain("K7Q2", sent, StringComparison.Ordinal);
                Assert.DoesNotContain("M9XD", sent, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task A_credential_marker_in_the_question_is_cleaned_in_limited_mode_too()
        {
            _model.Fail(new ClientResultException("registry.ollama.ai/library/gemma:2b does not support tools"))
                  .Reply("From the snapshot.");

            await AskAsync(Assistant(), "Is {{secret:K7Q2M9XD}} safe?");

            string limited = _model.Requests[1].Messages[^1].Text;
            Assert.StartsWith("Is [credential] safe?", limited, StringComparison.Ordinal);
            Assert.DoesNotContain("K7Q2M9XD", string.Join("\n", _model.Requests.Select(Sent)), StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_model_that_never_stops_asking_for_tools_still_ends_with_an_answer()
        {
            _model.Otherwise = _ => _model.CallMessage(ToolNames.GetLiveStatus);

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "Loop");

            Assert.Equal(AiAssistant.NoAnswer, TextOf(updates));
            var answered = new HashSet<string>(_conversation.Messages.SelectMany(m => m.Contents)
                .OfType<FunctionResultContent>().Select(r => r.CallId));
            Assert.All(_conversation.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>(),
                call => Assert.Contains(call.CallId, answered));
            Assert.Equal(AiAssistant.NoAnswer, _conversation.Messages[^1].Text);
        }

        // ----- keeping answered questions small ----------------------------------------------

        private static string ResultText(FunctionResultContent result) => result.Result switch
        {
            null => "",
            JsonElement element => element.GetRawText(),
            string s => s,
            object other => other.ToString() ?? "",
        };

        [Fact]
        public void A_kept_question_holds_at_most_the_cap_of_each_tool_result()
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.Assistant, new List<AIContent>
                {
                    new FunctionCallContent("c1", ToolNames.GetSlowdownReport),
                    new FunctionCallContent("c2", ToolNames.GetBattery),
                }),
                new(ChatRole.Tool, new List<AIContent>
                {
                    new FunctionResultContent("c1", JsonSerializer.SerializeToElement(new string('x', 100_000))),
                    new FunctionResultContent("c2", JsonSerializer.SerializeToElement(new { percent = 80 })),
                }),
                new(ChatRole.Assistant, "The report shows a disk storm."),
            };

            List<ChatMessage> kept = ToolHistory.KeepAnswered(messages);

            FunctionResultContent[] results = kept.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToArray();
            string big = ResultText(results[0]);
            Assert.True(big.Length <= ToolHistory.MaxResultChars, "kept " + big.Length + " characters");
            Assert.StartsWith("\"xxxx", big, StringComparison.Ordinal);
            Assert.Contains("MicaStats shortened this result", big, StringComparison.Ordinal);
            Assert.Contains("100002 characters", big, StringComparison.Ordinal);
            Assert.Equal("c1", results[0].CallId);
            // A result under the cap is kept exactly as the model saw it.
            JsonElement small = Assert.IsType<JsonElement>(results[1].Result);
            Assert.Equal(80, small.GetProperty("percent").GetInt32());
        }

        /// <summary>
        /// What one get_note call returns at most fits in what a conversation keeps of a result,
        /// so a full read stays whole. Shortened, it would be cut in the middle of its JSON and
        /// lose the line that says it is data.
        /// </summary>
        [Fact]
        public async Task A_full_get_note_result_kept_in_the_conversation_is_still_valid_json_with_its_about_line()
        {
            // 400 lines of 99 characters: more than one call gives, so the result is as large as get_note makes it.
            string text = string.Join("\n", Enumerable.Repeat(new string('a', 99), 400));
            var reader = new FakeNoteReader { Note = new Kil0bitSystemMonitor.Services.Pad.Ai.NoteText("a1", "Long", text) };
            NoteAccess notes = NotesForAsk(reader);
            _model.Call(ToolNames.GetNote, new Dictionary<string, object?> { ["noteId"] = "a1", ["lineCount"] = 400 })
                  .Reply("It is long.")
                  .Reply("Yes.");

            await AskAsync(Assistant(notes: notes), "What is in the long note?");
            await AskAsync(Assistant(notes: notes), "Sure?");

            FunctionResultContent kept = _conversation.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
            JsonElement json = Assert.IsType<JsonElement>(kept.Result);          // as the tool gave it, not shortened to text
            Assert.Equal(Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools.About, json.GetProperty("about").GetString());
            Assert.True(json.GetProperty("truncated").GetBoolean());
            Assert.Equal(160, json.GetProperty("lastLine").GetInt32());          // 160 lines of 100 are the 16,000 one call gives
            // And what the next question sends is that same whole result.
            string resent = ResultText(Contents(_model.Requests[2]).OfType<FunctionResultContent>().Single());
            using JsonDocument parsed = JsonDocument.Parse(resent);
            Assert.Equal(Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools.About, parsed.RootElement.GetProperty("about").GetString());
            Assert.True(Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools.MaxChars < ToolHistory.MaxResultChars);
        }

        // ----- a note result that is over the cap once written as JSON (re-check, residual 4) ------

        /// <summary>40 emoji: 80 characters in a note, 480 once written as JSON (each is two escapes of six).</summary>
        private static string Emoji(int count) => string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1F600), count));

        /// <summary>
        /// How many characters of its first <c>text</c> value a result still holds once the plain
        /// cut (<see cref="ToolHistory.Cap"/>) made it a string: what was kept of the note before.
        /// An escape cut in two holds no character.
        /// </summary>
        private static int TextCharsHeld(string cut)
        {
            var start = System.Text.RegularExpressions.Regex.Match(cut, "\"text\":\\s*\"");
            Assert.True(start.Success, "the cut result holds no text value");
            int end = cut.LastIndexOf("\n[MicaStats shortened", StringComparison.Ordinal);
            Assert.True(end > 0, "not a result the plain cut shortened");
            int chars = 0;
            for (int i = start.Index + start.Length; i < end && cut[i] != '"';)
            {
                int step = cut[i] != '\\' ? 1 : i + 1 < end && cut[i + 1] == 'u' ? 6 : 2;
                if (i + step > end) break;
                i += step;
                chars++;
            }
            return chars;
        }

        /// <summary>The kept result of the one tool call of the conversation, which must still be a JSON object within the cap, with its <c>about</c> line.</summary>
        private JsonElement KeptNoteResult()
        {
            FunctionResultContent kept = _conversation.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
            JsonElement json = Assert.IsType<JsonElement>(kept.Result);          // not a string cut in the middle of its JSON
            Assert.Equal(JsonValueKind.Object, json.ValueKind);
            Assert.True(json.GetRawText().Length <= ToolHistory.MaxResultChars, "kept " + json.GetRawText().Length + " characters");
            Assert.Equal(Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools.About, json.GetProperty("about").GetString());
            return json;
        }

        /// <summary>
        /// 16,000 characters of a note are far more once written as JSON: an emoji takes twelve
        /// characters, a quote two. The kept result is then the tool's own shape with fewer whole
        /// lines, never a string cut in the middle.
        /// </summary>
        [Theory]
        [InlineData("emoji")]
        [InlineData("quotes")]
        public async Task A_get_note_result_over_the_cap_once_written_as_JSON_is_kept_as_valid_json_with_fewer_whole_lines(string kind)
        {
            string line = kind == "emoji" ? Emoji(40) : string.Concat(Enumerable.Repeat("\"a\": \"b\", ", 6));
            string text = "login {{secret:K7Q2M9XD}}\n" + string.Join("\n", Enumerable.Range(2, 399).Select(i =>
                i.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + line));
            var reader = new FakeNoteReader { Note = new Kil0bitSystemMonitor.Services.Pad.Ai.NoteText("a1", "Long", text) };
            NoteAccess notes = NotesForAsk(reader);
            _model.Call(ToolNames.GetNote, new Dictionary<string, object?> { ["noteId"] = "a1", ["lineCount"] = 400 })
                  .Reply("It is long.")
                  .Reply("Yes.");

            await AskAsync(Assistant(notes: notes), "What is in the long note?");
            await AskAsync(Assistant(notes: notes), "Sure?");

            // Within its own question the model saw the whole result: within get_note's cap as text, over the kept cap as JSON.
            JsonElement seen = Assert.IsType<JsonElement>(Contents(_model.Requests[1]).OfType<FunctionResultContent>().Single().Result);
            string seenText = seen.GetProperty("text").GetString()!;
            Assert.True(seenText.Length <= Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools.MaxChars);
            Assert.True(seen.GetRawText().Length > ToolHistory.MaxResultChars, "the tool returned " + seen.GetRawText().Length + " characters");

            JsonElement kept = KeptNoteResult();
            Assert.Equal("a1", kept.GetProperty("noteId").GetString());
            Assert.Equal(1, kept.GetProperty("firstLine").GetInt32());
            Assert.Equal(400, kept.GetProperty("lines").GetInt32());
            Assert.True(kept.GetProperty("truncated").GetBoolean());
            Assert.False(kept.TryGetProperty("cutInLine", out _));

            // Whole lines from the start of what the tool gave, and the last line it names is the last it holds.
            string keptText = kept.GetProperty("text").GetString()!;
            Assert.StartsWith(keptText, seenText, StringComparison.Ordinal);
            Assert.Equal('\n', seenText[keptText.Length]);
            Assert.Equal(keptText.Split('\n').Length, kept.GetProperty("lastLine").GetInt32());

            // Never more of the note than the plain cut kept before, and still most of it.
            int before = TextCharsHeld(ToolHistory.Cap(seen.GetRawText()));
            Assert.InRange(keptText.Length, before - 3 * line.Length, before);

            // Cleaned once, by the tool: the credential is still [credential], and the note was not read again.
            Assert.StartsWith("login [credential]\n", keptText, StringComparison.Ordinal);
            Assert.DoesNotContain("K7Q2M9XD", kept.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(1, reader.Reads);

            // And the next question sends that same whole result.
            Assert.Equal(kept.GetRawText(), ResultText(Contents(_model.Requests[2]).OfType<FunctionResultContent>().Single()));
        }

        [Fact]
        public async Task A_search_notes_result_over_the_cap_is_kept_as_valid_json_with_fewer_whole_passages()
        {
            var reader = new FakeNoteReader
            {
                Hits = Enumerable.Range(1, 8).Select(i => new Kil0bitSystemMonitor.Services.Pad.Ai.NoteHit(
                    "n" + i, "Title " + i, "Heading", 1, 9, false, "passage " + i + " " + Emoji(250))).ToList(),
            };
            NoteAccess notes = NotesForAsk(reader);
            _model.Call(ToolNames.SearchNotes, new Dictionary<string, object?> { ["query"] = "smile" })
                  .Reply("Eight passages.")
                  .Reply("Yes.");

            await AskAsync(Assistant(notes: notes), "Where do I smile?");
            await AskAsync(Assistant(notes: notes), "Sure?");

            JsonElement seen = Assert.IsType<JsonElement>(Contents(_model.Requests[1]).OfType<FunctionResultContent>().Single().Result);
            JsonElement[] seenHits = seen.GetProperty("results").EnumerateArray().ToArray();
            Assert.Equal(8, seenHits.Length);
            Assert.True(seen.GetRawText().Length > ToolHistory.MaxResultChars, "the tool returned " + seen.GetRawText().Length + " characters");

            JsonElement kept = KeptNoteResult();
            Assert.Equal("smile", kept.GetProperty("query").GetString());
            Assert.Equal("words", kept.GetProperty("searchedBy").GetString());
            JsonElement[] keptHits = kept.GetProperty("results").EnumerateArray().ToArray();
            Assert.InRange(keptHits.Length, 4, 7);

            // The first passages, each whole and as the tool gave it; and each was whole in what the plain cut kept before.
            string before = ToolHistory.Cap(seen.GetRawText());
            for (int i = 0; i < keptHits.Length; i++)
            {
                Assert.Equal(seenHits[i].GetProperty("noteId").GetString(), keptHits[i].GetProperty("noteId").GetString());
                Assert.Equal(seenHits[i].GetProperty("text").GetString(), keptHits[i].GetProperty("text").GetString());
                Assert.Contains(seenHits[i].GetRawText(), before, StringComparison.Ordinal);
            }
            Assert.Equal(1, reader.Searches);
            Assert.Equal(kept.GetRawText(), ResultText(Contents(_model.Requests[2]).OfType<FunctionResultContent>().Single()));
        }

        /// <summary>A kept result of one tool call, as <see cref="ToolHistory.KeepAnswered"/> keeps it.</summary>
        private static FunctionResultContent KeptOf(string tool, object? result)
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("c1", tool) }),
                new(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("c1", result) }),
                new(ChatRole.Assistant, "Done."),
            };
            return ToolHistory.KeepAnswered(messages).SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        }

        private static JsonElement NoteJson(JsonObject result) => JsonSerializer.SerializeToElement(result);

        [Fact]
        public void One_passage_longer_than_the_cap_is_kept_with_its_text_cut_and_no_more_of_it_than_before()
        {
            string text = "one passage " + Emoji(4000);
            JsonElement given = NoteJson(new JsonObject
            {
                ["query"] = "smile",
                ["searchedBy"] = "words",
                ["results"] = new JsonArray(new JsonObject
                {
                    ["noteId"] = "n1", ["title"] = "T", ["heading"] = "", ["firstLine"] = 1, ["lastLine"] = 1, ["open"] = true, ["text"] = text,
                }),
                ["about"] = Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools.About,
            });
            Assert.True(given.GetRawText().Length > ToolHistory.MaxResultChars);

            JsonElement kept = Assert.IsType<JsonElement>(KeptOf(ToolNames.SearchNotes, given).Result);

            Assert.True(kept.GetRawText().Length <= ToolHistory.MaxResultChars, "kept " + kept.GetRawText().Length + " characters");
            Assert.Equal(Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools.About, kept.GetProperty("about").GetString());
            string keptText = Assert.Single(kept.GetProperty("results").EnumerateArray().ToArray()).GetProperty("text").GetString()!;
            Assert.StartsWith(keptText, text, StringComparison.Ordinal);
            Assert.False(char.IsHighSurrogate(keptText[^1]));                     // never half an emoji
            int before = TextCharsHeld(ToolHistory.Cap(given.GetRawText()));
            Assert.InRange(keptText.Length, before - 40, before);
        }

        [Fact]
        public void One_line_longer_than_the_cap_is_kept_cut_inside_the_line_and_says_so()
        {
            string text = "one line " + Emoji(4000);
            JsonElement given = NoteJson(new JsonObject
            {
                ["noteId"] = "a1", ["title"] = "Long", ["lines"] = 3, ["firstLine"] = 2, ["lastLine"] = 2, ["truncated"] = false,
                ["text"] = text,
                ["about"] = Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools.About,
            });

            JsonElement kept = Assert.IsType<JsonElement>(KeptOf(ToolNames.GetNote, given).Result);

            Assert.True(kept.GetRawText().Length <= ToolHistory.MaxResultChars, "kept " + kept.GetRawText().Length + " characters");
            Assert.True(kept.GetProperty("truncated").GetBoolean());
            Assert.True(kept.GetProperty("cutInLine").GetBoolean());
            Assert.Equal(2, kept.GetProperty("lastLine").GetInt32());            // the line it holds part of
            Assert.Equal("about", kept.EnumerateObject().Last().Name);           // still closed by the line that says it is data
            string keptText = kept.GetProperty("text").GetString()!;
            Assert.StartsWith(keptText, text, StringComparison.Ordinal);
            Assert.False(char.IsHighSurrogate(keptText[^1]));
            int before = TextCharsHeld(ToolHistory.Cap(given.GetRawText()));
            Assert.InRange(keptText.Length, before - 40, before);
        }

        /// <summary>A note can be renamed to anything, so the title alone can be over the cap: then the title is what is cut.</summary>
        [Fact]
        public void A_note_whose_title_alone_is_over_the_cap_is_kept_as_valid_json_with_the_title_cut()
        {
            string title = new string('T', 21_000);
            JsonElement given = NoteJson(new JsonObject
            {
                ["noteId"] = "a1", ["title"] = title, ["lines"] = 1, ["firstLine"] = 1, ["lastLine"] = 1, ["truncated"] = false,
                ["text"] = "x",
                ["about"] = Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools.About,
            });

            JsonElement kept = Assert.IsType<JsonElement>(KeptOf(ToolNames.GetNote, given).Result);

            Assert.True(kept.GetRawText().Length <= ToolHistory.MaxResultChars, "kept " + kept.GetRawText().Length + " characters");
            Assert.Equal("about", kept.EnumerateObject().Last().Name);           // still closed by the line that says it is data
            Assert.True(kept.GetProperty("truncated").GetBoolean());
            string keptTitle = kept.GetProperty("title").GetString()!;
            Assert.StartsWith(keptTitle, title, StringComparison.Ordinal);
            Assert.InRange(keptTitle.Length, 1, ToolHistory.MaxResultChars);
            Assert.Equal("", kept.GetProperty("text").GetString());              // the text went first
            // No more of the note than the plain cut kept: that one held the start of the title and nothing else.
            Assert.True(keptTitle.Length <= ToolHistory.Cap(given.GetRawText()).Length);
        }

        /// <summary>A first line that is no number is not the tool's shape: the result is cut to text, and nothing throws.</summary>
        [Fact]
        public void A_note_shaped_result_whose_first_line_is_no_number_is_cut_to_text_as_before()
        {
            foreach (JsonNode? firstLine in new JsonNode?[] { JsonValue.Create("1"), null, JsonValue.Create(1.5), JsonValue.Create(true) })
            {
                JsonElement given = NoteJson(new JsonObject
                {
                    ["noteId"] = "a1", ["title"] = "T", ["lines"] = 1, ["firstLine"] = firstLine, ["lastLine"] = 1, ["truncated"] = false,
                    ["text"] = new string('x', 30_000),
                    ["about"] = Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools.About,
                });

                string kept = Assert.IsType<string>(KeptOf(ToolNames.GetNote, given).Result);

                Assert.Equal(ToolHistory.Cap(given.GetRawText()), kept);
            }
        }

        /// <summary>The nine PC tools are cut as before, whatever their result looks like; so is a note tool's result of a shape it never gives.</summary>
        [Fact]
        public void Every_other_result_over_the_cap_is_cut_to_text_as_before()
        {
            JsonElement likeANote = NoteJson(new JsonObject
            {
                ["noteId"] = "a1", ["title"] = "T", ["lines"] = 1, ["firstLine"] = 1, ["lastLine"] = 1, ["truncated"] = false,
                ["text"] = new string('x', 30_000),
                ["about"] = Kil0bitSystemMonitor.Services.Pad.Ai.NoteTools.About,
            });
            JsonElement noText = NoteJson(new JsonObject { ["noteId"] = "a1", ["rows"] = new string('x', 30_000) });
            JsonElement notAnObject = JsonSerializer.SerializeToElement(new string('x', 30_000));

            foreach ((string tool, JsonElement result) in new[]
                     {
                         (ToolNames.GetHardware, likeANote), (ToolNames.GetNote, noText), (ToolNames.SearchNotes, noText), (ToolNames.GetNote, notAnObject),
                     })
            {
                string kept = Assert.IsType<string>(KeptOf(tool, result).Result);

                Assert.Equal(ToolHistory.Cap(result.GetRawText()), kept);
                Assert.Contains("MicaStats shortened this result", kept, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task A_huge_tool_result_is_not_sent_again_in_full_with_later_questions()
        {
            // A day of rows with a long path: get_history topProcesses at 500 points is far over the cap.
            string path = @"C:\Program Files\" + new string('p', 200) + @"\chrome.exe";
            for (int i = 0; i < 1440; i++)
            {
                _data.Rows.Add(new Kil0bitSystemMonitor.Services.History.HistoryRow
                {
                    Utc = _data.UtcNow.AddMinutes(i - 1440),
                    Seconds = 60,
                    TopCpuName = "chrome.exe",
                    TopCpuPath = path,
                    TopCpuPercent = 12.5f,
                });
            }
            _model.Call(ToolNames.GetHistory, new Dictionary<string, object?>
                  {
                      ["metric"] = "topProcesses",
                      ["from"] = "-1d",
                      ["maxPoints"] = 500,
                  })
                  .Reply("Chrome was busy all day.")
                  .Reply("Nothing else stood out.");
            AiAssistant assistant = Assistant();

            await AskAsync(assistant, "What was busy today?");
            await AskAsync(assistant, "Anything else?");

            // Within its own question the model saw the whole result...
            string seen = ResultText(Contents(_model.Requests[1]).OfType<FunctionResultContent>().Single());
            Assert.True(seen.Length > 100_000, "the tool returned " + seen.Length + " characters");
            // ...but the conversation keeps, and the next question sends, at most the cap.
            string kept = ResultText(_conversation.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single());
            string resent = ResultText(Contents(_model.Requests[2]).OfType<FunctionResultContent>().Single());
            Assert.True(kept.Length <= ToolHistory.MaxResultChars, "kept " + kept.Length + " characters");
            Assert.Equal(kept, resent);
        }

        // ----- limits and failures -----------------------------------------------------------

        [Fact]
        public async Task The_daily_limit_stops_a_question_before_any_request()
        {
            _limit = 1;
            _model.Reply("One.");
            AiAssistant assistant = Assistant();
            await AskAsync(assistant, "First");

            List<AssistantUpdate> updates = await AskAsync(assistant, "Second");

            AssistantUpdate error = updates[0];
            Assert.Equal(AssistantUpdateKind.Error, error.Kind);
            Assert.Contains("daily limit set in Settings > AI", error.Text, StringComparison.Ordinal);
            Assert.Equal(2, updates.Count);
            Assert.Single(_model.Requests);
            Assert.Equal(new[] { "First", "One." }, _conversation.Messages.Select(m => m.Text));
        }

        [Fact]
        public async Task A_provider_failure_is_one_error_and_the_question_is_not_kept()
        {
            _model.Fail(new HttpRequestException("No such host is known."));

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "CPU?");

            Assert.Equal(new[] { AssistantUpdateKind.Error, AssistantUpdateKind.Done }, updates.Select(u => u.Kind));
            Assert.Equal(AiErrorText.Unreachable, updates[0].Text);
            Assert.Empty(_conversation.Messages);
            Assert.Equal(1, _usage.UsedToday);
        }

        [Fact]
        public async Task A_failure_after_a_tool_round_leaves_nothing_behind()
        {
            _model.Call(ToolNames.GetLiveStatus).Fail(new HttpRequestException("Connection reset."));

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "CPU?");

            Assert.Contains(updates, u => u.Kind == AssistantUpdateKind.Error);
            Assert.Empty(_conversation.Messages);
        }

        [Fact]
        public async Task Cancelling_ends_quietly_and_forgets_the_question()
        {
            _model.Hang();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "Slow question", cts.Token);

            Assert.Single(updates);
            Assert.Empty(_conversation.Messages);
        }

        [Fact]
        public async Task A_silent_model_ends_with_the_timeout_sentence_and_forgets_the_question()
        {
            _model.Hang();

            List<AssistantUpdate> updates = await AskAsync(Assistant(silence: TimeSpan.FromMilliseconds(200)), "Slow question");

            Assert.Equal(new[] { AssistantUpdateKind.Error, AssistantUpdateKind.Done }, updates.Select(u => u.Kind));
            Assert.Equal(AiErrorText.TimedOut, updates[0].Text);
            Assert.Empty(_conversation.Messages);
        }

        [Fact]
        public async Task A_slow_model_that_keeps_answering_within_the_deadline_finishes()
        {
            TimeSpan pause = TimeSpan.FromMilliseconds(250);
            _model.Slow(pause, tool: ToolNames.GetLiveStatus).Slow(pause, tool: ToolNames.GetLiveStatus).Slow(pause, text: "Worth the wait.");

            // 3 x 250 ms is longer than the 400 ms deadline, so only re-arming on each update lets it finish.
            List<AssistantUpdate> updates = await AskAsync(Assistant(silence: TimeSpan.FromMilliseconds(400)), "Slow but steady");

            Assert.DoesNotContain(updates, u => u.Kind == AssistantUpdateKind.Error);
            Assert.Equal("Worth the wait.", TextOf(updates));
        }

        [Fact]
        public async Task Bad_tool_arguments_are_reported_to_the_model_which_can_try_again()
        {
            _model.Call(ToolNames.GetHistory).Call(ToolNames.GetHistory)
                  .Call(ToolNames.GetHistory, new Dictionary<string, object?> { ["metric"] = "cpu", ["from"] = "-1h" })
                  .Reply("Here is the history.");

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "CPU history?");

            Assert.DoesNotContain(updates, u => u.Kind == AssistantUpdateKind.Error);
            Assert.Equal("Here is the history.", TextOf(updates));
            FunctionResultContent failed = Contents(_model.Requests[1]).OfType<FunctionResultContent>().First();
            Assert.Contains("metric", failed.Result?.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_network_failure_through_the_real_openai_sdk_reads_as_unreachable()
        {
            var handler = new ScriptedHttpHandler(_ => throw new HttpRequestException("No such host is known."));
            var config = new AppConfig
            {
                AiProvider = AiProviders.OpenAiCompatible,
                AiCompatibleBaseUrl = "http://localhost:11434/v1",
                AiCompatibleModel = "gemma:2b",
            };
            AiClientResult local = AiProviderFactory.Create(config, new SecretStore(_env.PathOf("secrets.bin"), _ => { }), handler);

            List<AssistantUpdate> updates = await AskAsync(Assistant(client: local.Client), "How is my PC?");

            Assert.Equal(AssistantUpdateKind.Error, updates[0].Kind);
            Assert.Equal(AiErrorText.Unreachable, updates[0].Text);
            Assert.Empty(_conversation.Messages);
        }

        [Fact]
        public async Task An_empty_question_is_refused_without_counting()
        {
            List<AssistantUpdate> updates = await AskAsync(Assistant(), "   ");

            Assert.Equal(AiAssistant.EmptyQuestion, updates[0].Text);
            Assert.Equal(0, _usage.UsedToday);
            Assert.Empty(_model.Requests);
        }

        // ----- suggest_action ----------------------------------------------------------------

        [Fact]
        public async Task Suggest_action_records_a_button_and_does_nothing_else()
        {
            _model.Call(ToolNames.SuggestAction, new Dictionary<string, object?>
            {
                ["kind"] = "end_process",
                ["pid"] = 4242,
                ["createTime"] = 134037504000000000L,
                ["processName"] = "chrome.exe",
                ["reason"] = "It uses most of the CPU.",
            }).Reply("You could end chrome.exe.");

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "What is slowing me down?");

            SuggestedAction action = Assert.Single(updates, u => u.Kind == AssistantUpdateKind.Suggestion).Suggestion!;
            Assert.Equal(new SuggestedAction(SuggestedActionKind.EndProcess, "End chrome.exe (PID 4242)", "It uses most of the CPU.",
                4242, 134037504000000000L, "chrome.exe"), action);
            Assert.Equal(action, Assert.Single(_conversation.Suggestions));
            Assert.DoesNotContain(updates, u => u.Kind == AssistantUpdateKind.ToolUsed);
            JsonElement result = (JsonElement)Contents(_model.Requests[1]).OfType<FunctionResultContent>().Single().Result!;
            Assert.True(result.GetProperty("recorded").GetBoolean());
            Assert.Equal(0, _data.LatestCalls);
            Assert.Null(_data.LastTopRequest);
        }

        [Fact]
        public void An_end_process_label_from_the_model_is_replaced_by_the_process_and_pid()
        {
            SuggestedAction? disguised = AiToolFunctions.Build("end_process", "Refreshes the list", "Open Diagnostics",
                1234, 555L, "chrome.exe", out string? error);
            SuggestedAction? nameless = AiToolFunctions.Build("end_process", "Busy.", null, 77, 5L, null, out _);
            SuggestedAction? longName = AiToolFunctions.Build("end_process", "Busy.", null, 1234, 5L, new string('a', 100) + ".exe", out _);

            Assert.Null(error);
            Assert.Equal(SuggestedActionKind.EndProcess, disguised!.Kind);
            Assert.Equal("End chrome.exe (PID 1234)", disguised.Label);
            Assert.Equal("End PID 77", nameless!.Label);
            Assert.EndsWith(" (PID 1234)", longName!.Label, StringComparison.Ordinal);
            Assert.StartsWith("End aaaa", longName.Label, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_suggestion_without_its_target_is_refused_to_the_model()
        {
            _model.Call(ToolNames.SuggestAction, new Dictionary<string, object?> { ["kind"] = "end_process", ["reason"] = "Busy." })
                  .Reply("I could not suggest that.");

            List<AssistantUpdate> updates = await AskAsync(Assistant(), "End it");

            Assert.DoesNotContain(updates, u => u.Kind == AssistantUpdateKind.Suggestion);
            JsonElement result = (JsonElement)Contents(_model.Requests[1]).OfType<FunctionResultContent>().Single().Result!;
            Assert.Contains("pid and createTime", result.GetProperty("error").GetString(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Suggestions_are_cleared_when_the_next_question_starts()
        {
            _model.Call(ToolNames.SuggestAction, new Dictionary<string, object?> { ["kind"] = "open_diagnostics", ["reason"] = "See the reports." })
                  .Reply("Open Diagnostics.")
                  .Reply("Nothing else.");
            AiAssistant assistant = Assistant();
            await AskAsync(assistant, "Where are my reports?");
            Assert.Equal("Open Diagnostics", Assert.Single(_conversation.Suggestions).Label);

            await AskAsync(assistant, "Thanks");

            Assert.Empty(_conversation.Suggestions);
        }

        // ----- limited mode ------------------------------------------------------------------

        [Fact]
        public async Task An_endpoint_without_tools_answers_in_limited_mode_from_a_live_snapshot()
        {
            _data.Processes.Add(new ProcessInfo("chrome.exe", null, 4242, 1, 80f, 900, 0));
            _model.Fail(new ClientResultException("registry.ollama.ai/library/gemma:2b does not support tools"))
                  .Reply("From the snapshot: CPU 12%.")
                  .Reply("Still limited.");
            AiAssistant assistant = Assistant();

            List<AssistantUpdate> first = await AskAsync(assistant, "How is my PC?");

            Assert.Equal(AssistantUpdateKind.LimitedMode, first[0].Kind);
            Assert.Equal("From the snapshot: CPU 12%.", TextOf(first));
            ScriptedChatClient.Request retry = _model.Requests[1];
            Assert.Empty(retry.ToolNames);
            string sent = retry.Messages[^1].Text;
            Assert.StartsWith("How is my PC?", sent, StringComparison.Ordinal);
            Assert.Contains("Limited mode", sent, StringComparison.Ordinal);
            Assert.Contains("usagePercent", sent, StringComparison.Ordinal);
            Assert.Contains("chrome.exe", sent, StringComparison.Ordinal);
            Assert.Equal(new[] { "How is my PC?", "From the snapshot: CPU 12%." }, _conversation.Messages.Select(m => m.Text));
            Assert.Equal(1, _usage.UsedToday);

            List<AssistantUpdate> second = await AskAsync(assistant, "And now?");

            Assert.Equal(AssistantUpdateKind.LimitedMode, second[0].Kind);
            Assert.Equal(3, _model.Requests.Count);
            Assert.Equal(2, _usage.UsedToday);
        }

        [Fact]
        public async Task Claude_never_falls_back_to_limited_mode()
        {
            _model.Fail(new ClientResultException("tools are not supported"));

            List<AssistantUpdate> updates = await AskAsync(Assistant(isClaude: true), "CPU?");

            Assert.DoesNotContain(updates, u => u.Kind == AssistantUpdateKind.LimitedMode);
            Assert.Equal(AssistantUpdateKind.Error, updates[0].Kind);
            Assert.Empty(_conversation.Messages);
        }

        // ----- through the real SDKs, offline ------------------------------------------------

        [Fact]
        public async Task Claude_runs_the_tool_loop_with_a_cached_system_prompt()
        {
            var handler = new ScriptedHttpHandler(sent => (HttpStatusCode.OK, "text/event-stream",
                sent.Body.Contains("tool_result", StringComparison.Ordinal)
                    ? ScriptedHttpHandler.ClaudeStreamText
                    : ScriptedHttpHandler.ClaudeStreamToolUse));
            var secrets = new SecretStore(_env.PathOf("secrets.bin"), _ => { });
            secrets.Set(SecretNames.ClaudeKey, "sk-ant-test");
            AiClientResult claude = AiProviderFactory.Create(new AppConfig(), secrets, handler);

            List<AssistantUpdate> updates = await AskAsync(Assistant(isClaude: true, client: claude.Client), "How is my CPU?");

            Assert.Equal(ToolNames.GetLiveStatus, Assert.Single(updates, u => u.Kind == AssistantUpdateKind.ToolUsed).ToolName);
            Assert.Equal("Streamed ok", TextOf(updates));
            Assert.Equal(2, handler.Requests.Count);
            JsonNode first = JsonNode.Parse(handler.Requests[0].Body)!;
            JsonNode system = first["system"]![0]!;
            Assert.Equal(AiPrompts.System, system["text"]!.GetValue<string>());
            Assert.Equal("1h", system["cache_control"]!["ttl"]!.GetValue<string>());
            Assert.Equal(ToolNames.ReadOnly.Append(ToolNames.SuggestAction),
                first["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()));
            Assert.True(first["stream"]!.GetValue<bool>());
            JsonNode toolResult = JsonNode.Parse(handler.Requests[1].Body)!["messages"]!.AsArray()
                .SelectMany(m => m!["content"]!.AsArray())
                .First(c => c!["type"]!.GetValue<string>() == "tool_result")!;
            JsonNode sentResult = JsonNode.Parse(toolResult["content"]!.GetValue<string>())!;
            Assert.Equal(12.3, sentResult["cpu"]!["usagePercent"]!.GetValue<double>());
        }

        [Fact]
        public async Task With_notes_allowed_Claude_is_offered_the_note_tools_with_their_arguments()
        {
            var handler = new ScriptedHttpHandler(sent => (HttpStatusCode.OK, "text/event-stream",
                sent.Body.Contains("tool_result", StringComparison.Ordinal)
                    ? ScriptedHttpHandler.ClaudeStreamText
                    : ScriptedHttpHandler.ClaudeStreamToolUse));
            var secrets = new SecretStore(_env.PathOf("secrets.bin"), _ => { });
            secrets.Set(SecretNames.ClaudeKey, "sk-ant-test");
            AiClientResult claude = AiProviderFactory.Create(new AppConfig(), secrets, handler);

            List<AssistantUpdate> updates = await AskAsync(
                Assistant(isClaude: true, client: claude.Client, notes: NotesForAsk()), "How is my CPU?");

            Assert.Equal("Streamed ok", TextOf(updates));
            JsonNode first = JsonNode.Parse(handler.Requests[0].Body)!;
            JsonArray tools = first["tools"]!.AsArray();
            Assert.Equal(ToolNames.ReadOnly.Concat(ToolNames.Notes).Append(ToolNames.SuggestAction),
                tools.Select(t => t!["name"]!.GetValue<string>()));
            Assert.Equal(AiPrompts.System, first["system"]![0]!["text"]!.GetValue<string>());
            JsonNode search = tools.Single(t => t!["name"]!.GetValue<string>() == ToolNames.SearchNotes)!;
            Assert.Equal(new[] { "query" }, search["input_schema"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()));
            Assert.NotNull(search["input_schema"]!["properties"]!["limit"]);
            JsonNode get = tools.Single(t => t!["name"]!.GetValue<string>() == ToolNames.GetNote)!;
            Assert.Equal(new[] { "noteId" }, get["input_schema"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()));
        }

        [Fact]
        public async Task A_local_model_without_tools_answers_in_limited_mode_through_the_real_sdk()
        {
            var handler = new ScriptedHttpHandler(sent => JsonNode.Parse(sent.Body)!["tools"] != null
                ? (HttpStatusCode.BadRequest, "application/json", ScriptedHttpHandler.OllamaNoTools)
                : (HttpStatusCode.OK, "text/event-stream", ScriptedHttpHandler.OpenAiStreamText));
            var config = new AppConfig
            {
                AiProvider = AiProviders.OpenAiCompatible,
                AiCompatibleBaseUrl = "http://localhost:11434/v1",
                AiCompatibleModel = "gemma:2b",
            };
            AiClientResult local = AiProviderFactory.Create(config, new SecretStore(_env.PathOf("secrets.bin"), _ => { }), handler);

            List<AssistantUpdate> updates = await AskAsync(Assistant(client: local.Client), "How is my PC?");

            Assert.Equal(AssistantUpdateKind.LimitedMode, updates[0].Kind);
            Assert.Equal("Streamed", TextOf(updates));
            Assert.Equal(2, handler.Requests.Count);
            JsonNode retry = JsonNode.Parse(handler.Requests[1].Body)!;
            Assert.Null(retry["tools"]);
            Assert.Equal(2000, retry["max_tokens"]!.GetValue<int>());
            string question = retry["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
            Assert.StartsWith("How is my PC?", question, StringComparison.Ordinal);
            Assert.Contains("Limited mode", question, StringComparison.Ordinal);
        }
    }
}
