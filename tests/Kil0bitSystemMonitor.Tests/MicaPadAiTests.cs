using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Kil0bitSystemMonitor.Services.Pad.Search;
using Microsoft.Extensions.AI;
using Xunit;

using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// AI actions in a MicaPad window built on the shared UI thread (never shown), over a scripted
    /// model: the AI menu, Ctrl+Shift+A, a request and its pane, the anchors that follow the source
    /// text while the note is edited, Replace selection, Insert below and Copy, and what ends a
    /// request. No network, no real clipboard, no %APPDATA%.
    /// </summary>
    public class MicaPadAiTests
    {
        private const ModifierKeys CtrlShift = ModifierKeys.Control | ModifierKeys.Shift;

        private const string AiOff = "Turn on Settings → MicaPad → AI to use AI here";
        private const string TextChanged = "The text changed since the request; use Insert below or Copy";
        private const string NotShown = "Show the note this came from to apply it";
        private const string ReadOnly = "This note is read-only";
        private const string NoText = "There is no text to work on";

        /// <summary>A note of three lines; the tests select its middle one.</summary>
        private const string Note = "Intro\nbad text here\nOutro";
        private const string Picked = "bad text here";

        /// <summary>A window, its fakes and what they recorded.</summary>
        private sealed class Harness
        {
            /// <param name="renderer">Draws the window's diagrams; null (most tests) means no pictures.</param>
            /// <param name="config">The window's settings; null (most tests) means a new config's defaults.</param>
            public Harness(PadTestEnv env, IDiagramRenderer? renderer = null, AppConfig? config = null)
            {
                Env = env;
                Usage = new UsageMeter(env.FileOf("ai-usage.json"), () => new DateTime(2026, 10, 3, 12, 0, 0));
                Client = Model;
                // The window reads the app's renderer, a static, as it is built. The UI tests of other
                // classes share this thread and that static, and one of them may hold its own
                // renderer there while it pumps the dispatcher, which is when this window can be
                // built. So the static is set here either way (null: this window draws nothing),
                // for the construction alone with no await in between, and then put back.
                IDiagramRenderer? others = MicaPadWindow.DiagramRenderer;
                MicaPadWindow.DiagramRenderer = renderer;
                try
                {
                    Window = new MicaPadWindow(env.Workspace, config ?? new AppConfig());
                }
                finally
                {
                    MicaPadWindow.DiagramRenderer = others;
                }
                Window.AiEnabled = () => AiOn;
                Window.AiRunnerFactory = () =>
                {
                    RunnersBuilt++;
                    return new PadAiRunner(() => new AiClientResult(Client, Problem, false), Usage, () => Limit);
                };
                Window.AiCopy = Copied.Add;
                Window.OpenPadSettings = () => SettingsOpened++;
                Window.AiLog = Log.Add;
                Window.AiDestination = () => Destination;
            }

            /// <summary>Where the settings say the text goes; "" names nowhere, so the lines other tests read stay as they were.</summary>
            public string Destination { get; set; } = "";

            public PadTestEnv Env { get; }

            public MicaPadWindow Window { get; }

            public AiPane Pane => Window.AiPanel;

            public TextEditor Editor => Window.Editor;

            public UsageMeter Usage { get; }

            /// <summary>The model every runner talks to unless <see cref="Client"/> names another.</summary>
            public ScriptedChatClient Model { get; } = new();

            public IChatClient? Client { get; set; }

            /// <summary>What the provider says is wrong; with a null <see cref="Client"/>, no request can be made.</summary>
            public string? Problem { get; set; }

            public int Limit { get; set; } = 100;

            public bool AiOn { get; set; } = true;

            public List<string> Copied { get; } = new();

            public List<string> Log { get; } = new();

            public int SettingsOpened { get; set; }

            /// <summary>How many times the window asked for a runner.</summary>
            public int RunnersBuilt { get; set; }

            /// <summary>The note the window shows.</summary>
            public OpenNote Shown => Env.Workspace.Active!;

            /// <summary>The user message of a request the scripted model received.</summary>
            public string Sent(int index = 0) => Model.Requests[index].Messages[1].Text;
        }

        /// <summary>
        /// A model that streams <c>first</c>, says it was reached, waits for <see cref="Gate"/>,
        /// then streams the rest: the test acts while the request is in flight.
        /// </summary>
        private sealed class GatedModel : IChatClient
        {
            private readonly string _first;
            private readonly string _rest;

            public GatedModel(string first, string rest)
            {
                _first = first;
                _rest = rest;
            }

            public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>The reply ends at the output cap.</summary>
            public bool CutShort { get; init; }

            /// <summary>Why the provider says the reply ended, on its last piece; null says nothing.</summary>
            public ChatFinishReason? Finish { get; init; }

            /// <summary>A callback on the request's token throws, so cancelling the request throws at the caller.</summary>
            public bool ThrowOnCancel { get; init; }

            /// <summary>True once the request was cancelled while it waited.</summary>
            public bool Cancelled { get; private set; }

            public List<string> Sent { get; } = new();

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                                                       CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
                ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                Sent.Add(messages.Last().Text);
                return Stream(cancellationToken);
            }

            private async IAsyncEnumerable<ChatResponseUpdate> Stream([EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, _first);
                using CancellationTokenRegistration thrower = ThrowOnCancel
                    ? ct.Register(() => throw new InvalidOperationException("a cancel callback threw"))
                    : default;
                Reached.TrySetResult();
                try
                {
                    await Gate.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    Cancelled = true;
                    throw;
                }
                var last = new ChatResponseUpdate(ChatRole.Assistant, _rest);
                if (CutShort) last.FinishReason = ChatFinishReason.Length;
                if (Finish is { } finish) last.FinishReason = finish;
                yield return last;
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }

        /// <summary>
        /// Runs an async test on the UI thread over a loaded window; a test that hangs fails after a
        /// minute. With a <paramref name="renderer"/>, the window draws its diagrams through it.
        /// </summary>
        private static async Task OnUiAsync(Func<Harness, Task> test, IDiagramRenderer? renderer = null, AppConfig? config = null)
        {
            Task body = UiThread.RunAsync(async () =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
                var h = new Harness(env, renderer, config);
                try
                {
                    h.Window.LoadSession();
                    await test(h);
                }
                finally
                {
                    h.Window.CloseForExit();
                }
            });
            await body.WaitAsync(TimeSpan.FromSeconds(60));
        }

        private static Task OnUi(Action<Harness> test) => OnUiAsync(h =>
        {
            test(h);
            return Task.CompletedTask;
        });

        /// <summary>Lets the dispatcher run until <paramref name="done"/>, for at most ten seconds.</summary>
        private static async Task Until(Func<bool> done, string what)
        {
            var waited = Stopwatch.StartNew();
            while (!done())
            {
                Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "Timed out waiting for " + what);
                await Task.Delay(10);
            }
        }

        /// <summary>Waits until the gated model has streamed its first piece into the window.</summary>
        private static Task Reached(GatedModel model) => model.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        /// <summary>Waits for a request started by a click (which nothing awaits) to end.</summary>
        private static Task Finished(Harness h, AiSession? after = null) =>
            Until(() => h.Window.AiSessionNow is { Finished: true } now && !ReferenceEquals(now, after), "the request to end");

        /// <summary>Sets the shown note's text and selects <paramref name="select"/> in it, or nothing.</summary>
        private static void Write(Harness h, string text, string? select = null)
        {
            h.Editor.Document.Text = text;
            if (select == null) h.Editor.Select(0, 0);
            else h.Editor.Select(text.IndexOf(select, StringComparison.Ordinal), select.Length);
        }

        private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        /// <summary>The AI submenu of a freshly built editor menu.</summary>
        private static MenuItem AiMenu(Harness h)
        {
            h.Window.RefreshEditorMenu();
            return PadMenuTests.ItemOf(h.Window.EditorMenu, "AI");
        }

        private static string[] Headers(MenuItem item) =>
            item.Items.Cast<object>().Select(i => i is MenuItem m ? (string)m.Header : "-").ToArray();

        private static MenuItem Sub(MenuItem menu, string header) =>
            menu.Items.OfType<MenuItem>().Single(m => (string)m.Header == header);

        private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);

        // ---- what the app runs with: every other test here replaces these --------------------------

        /// <summary>Runs a test over a window built as the app builds it, with nothing replaced.</summary>
        private static Task OnUiWithDefaults(Func<MicaPadWindow, PadTestEnv, Task> test)
        {
            Task body = UiThread.RunAsync(async () =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
                var window = new MicaPadWindow(env.Workspace, new AppConfig { PadAiEnabled = true });   // not the app's config: it must not count
                var logged = new List<string>();
                window.AiLog = logged.Add;                        // nothing reaches the real log; no line is expected either
                try
                {
                    window.LoadSession();
                    await test(window, env);
                    Assert.Empty(logged);
                }
                finally
                {
                    window.CloseForExit();
                }
            });
            return body.WaitAsync(TimeSpan.FromSeconds(60));
        }

        [Fact]
        public Task By_default_AI_is_read_from_the_apps_settings_and_is_off_while_there_are_none() => OnUiWithDefaults((window, env) =>
        {
            Assert.Null(App.ConfigService);                       // the tests never start the app: there are no settings to read

            Assert.False(window.AiEnabled());                     // a setting that is not there is off, never on
            Assert.Equal("", window.AiDestination());

            // What the default reads: Settings → MicaPad → AI and nothing else.
            Assert.False(MicaPadWindow.AiOnIn(null));
            Assert.False(MicaPadWindow.AiOnIn(new AppConfig()));  // off until the user turns it on
            Assert.False(MicaPadWindow.AiOnIn(new AppConfig { AiAssistantEnabled = true }));
            Assert.True(MicaPadWindow.AiOnIn(new AppConfig { PadAiEnabled = true, AiAssistantEnabled = false }));
            return Task.CompletedTask;
        });

        [Fact]
        public Task By_default_the_runner_is_the_one_the_app_builds() => OnUiWithDefaults((window, env) =>
        {
            System.Reflection.MethodInfo? apps = typeof(App).GetMethod(nameof(App.CreatePadAiRunner),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(apps);

            Assert.Equal(apps, window.AiRunnerFactory.Method);    // the shared key, provider and daily count: not a runner of its own
            Assert.Null(window.AiRunnerFactory());                // and before the settings load it builds none
            return Task.CompletedTask;
        });

        [Fact]
        public Task By_default_nothing_is_sent_from_the_menu_the_shortcut_a_direct_call_or_Ask() => OnUiWithDefaults(async (window, env) =>
        {
            window.Editor.Document.Text = Note;
            window.Editor.Select(Note.IndexOf(Picked, StringComparison.Ordinal), Picked.Length);

            window.RefreshEditorMenu();
            Assert.Equal(new[] { "Set up AI…" }, Headers(PadMenuTests.ItemOf(window.EditorMenu, "AI")));

            await window.RunAiAsync(PadAiAction.Improve);
            Assert.Null(window.AiSessionNow);
            Assert.Equal(Visibility.Collapsed, window.AiPanel.Visibility);
            Assert.Equal(AiOff, window.StatusMessage.Text);

            Assert.True(window.HandleShortcut(Key.A, CtrlShift));
            Assert.Null(window.AiSessionNow);
            Assert.Equal(Visibility.Collapsed, window.AiPanel.Visibility);

            AskStart start = await WithSearch(null, () => window.SearchPanel.Ask!("vpn", CancellationToken.None));
            Assert.Null(start.Answer);
            Assert.Equal(NotesQuestion.AiOff, start.Instead);
        });

        // ---- the menu ------------------------------------------------------------------------

        [Fact]
        public Task With_AI_off_the_AI_menu_holds_only_Set_up_AI_which_opens_the_MicaPad_settings() => OnUi(h =>
        {
            h.AiOn = false;
            Write(h, Note, Picked);

            h.Window.RefreshEditorMenu();
            string[] top = PadMenuTests.Headers(h.Window.EditorMenu);
            Assert.Equal("Tools", top[Array.IndexOf(top, "AI") - 1]);   // right after Tools
            MenuItem ai = PadMenuTests.ItemOf(h.Window.EditorMenu, "AI");
            Assert.Equal(new[] { "Set up AI…" }, Headers(ai));

            PadMenuTests.Click(Sub(ai, "Set up AI…"));

            Assert.Equal(1, h.SettingsOpened);
            Assert.Empty(h.Model.Requests);
            Assert.Null(h.Window.AiSessionNow);
            Assert.Equal(Visibility.Collapsed, h.Pane.Visibility);
        });

        [Fact]
        public Task With_AI_on_the_menu_lists_the_nine_actions_and_a_rewrite_waits_for_a_selection() => OnUi(h =>
        {
            Write(h, Note);

            MenuItem ai = AiMenu(h);
            Assert.Equal(new[]
            {
                "Improve writing", "Fix spelling and grammar", "Make shorter", "Translate to English", "Translate to Thai",
                "Summarize", "Explain", "Draw as diagram", "-", "Ask AI…",
            }, Headers(ai));
            Assert.Equal("Ctrl+Shift+A", Sub(ai, "Ask AI…").InputGestureText);
            foreach (string rewrite in new[] { "Improve writing", "Fix spelling and grammar", "Make shorter", "Translate to English", "Translate to Thai" })
                Assert.False(Sub(ai, rewrite).IsEnabled, rewrite);
            foreach (string other in new[] { "Summarize", "Explain", "Draw as diagram", "Ask AI…" })
                Assert.True(Sub(ai, other).IsEnabled, other);

            h.Editor.Select(6, Picked.Length);
            Assert.All(AiMenu(h).Items.OfType<MenuItem>(), item => Assert.True(item.IsEnabled, (string)item.Header));
            Assert.Equal(0, h.SettingsOpened);
        });

        [Fact]
        public Task A_menu_item_runs_its_action() => OnUiAsync(async h =>
        {
            Write(h, "The cat sat.");
            h.Model.Reply("- a cat sat");

            PadMenuTests.Click(Sub(AiMenu(h), "Summarize"));
            await Finished(h);

            Assert.Equal(PadAiPrompts.ForAction(PadAiAction.Summarize.Instruction, "The cat sat."), h.Sent());
            Assert.Equal("- a cat sat", h.Pane.ResultBox.Shown);
        });

        [Fact]
        public Task Ask_AI_in_the_menu_opens_the_pane_on_its_instruction_box() => OnUi(h =>
        {
            Write(h, Note, Picked);

            PadMenuTests.Click(Sub(AiMenu(h), "Ask AI…"));

            Assert.Equal(Visibility.Visible, h.Pane.Visibility);
            Assert.Equal(Visibility.Visible, h.Pane.InstructionBox.Visibility);
            Assert.True(h.Window.AiSessionNow!.AwaitingInstruction);
            Assert.Empty(h.Model.Requests);
        });

        // ---- a request and its result ----------------------------------------------------------

        [Fact]
        public Task Improve_on_a_selection_sends_the_prompt_shows_the_reply_and_Replace_is_one_undo_step() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Model.Reply("Better text here.");

            await h.Window.RunAiAsync(PadAiAction.Improve);

            ScriptedChatClient.Request request = Assert.Single(h.Model.Requests);
            Assert.Equal(PadAiPrompts.System, request.Messages[0].Text);
            Assert.Equal(PadAiPrompts.ForAction(PadAiAction.Improve.Instruction, Picked), request.Messages[1].Text);
            Assert.Equal(Visibility.Visible, h.Pane.Visibility);
            Assert.Equal("Improve writing", h.Pane.TitleText.Text);
            Assert.Equal("Selection, 13 characters", h.Pane.SourceText.Text);
            Assert.Equal("Better text here.", h.Pane.ResultBox.Shown);
            Assert.Equal(Visibility.Collapsed, h.Pane.StopButton.Visibility);
            Assert.Equal(Visibility.Visible, h.Pane.ReplaceButton.Visibility);
            Assert.True(h.Pane.ReplaceButton.IsEnabled);
            Assert.Equal(Note, h.Editor.Document.Text);          // nothing changes before a click

            Click(h.Pane.ReplaceButton);

            Assert.Equal("Intro\nBetter text here.\nOutro", h.Editor.Document.Text);
            Assert.Equal("Better text here.", h.Editor.SelectedText);
            Assert.Equal("Replaced the selection", h.Pane.StatusText.Text);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);

            h.Editor.Undo();                                     // one Ctrl+Z
            Assert.Equal(Note, h.Editor.Document.Text);
        });

        [Fact]
        public Task Summarize_with_no_selection_runs_on_the_whole_note_and_offers_Insert_and_Copy_but_not_Replace() => OnUiAsync(async h =>
        {
            Write(h, "alpha\nbeta\n");
            h.Model.Reply("- sum");

            await h.Window.RunAiAsync(PadAiAction.Summarize);

            Assert.Equal(PadAiPrompts.ForAction(PadAiAction.Summarize.Instruction, "alpha\nbeta\n"), h.Sent());
            Assert.Equal("Summarize", h.Pane.TitleText.Text);
            Assert.Equal("Whole note, 11 characters", h.Pane.SourceText.Text);
            Assert.Equal("- sum", h.Pane.ResultBox.Shown);
            Assert.Equal(Visibility.Collapsed, h.Pane.ReplaceButton.Visibility);
            Assert.True(h.Pane.InsertButton.IsEnabled);
            Assert.True(h.Pane.CopyButton.IsEnabled);

            Click(h.Pane.ReplaceButton);                         // not offered: even a forced click does nothing
            Assert.Equal("alpha\nbeta\n", h.Editor.Document.Text);

            Click(h.Pane.InsertButton);                          // at the end of the note
            Assert.Equal("alpha\nbeta\n\n- sum\n", h.Editor.Document.Text);
            Assert.Equal("- sum", h.Editor.SelectedText);
        });

        [Fact]
        public Task A_rewrite_that_is_too_long_shows_the_refusal_and_makes_no_request() => OnUiAsync(async h =>
        {
            string text = new string('x', PadAiAction.RewriteMaxChars + 1);
            Write(h, text, text);

            await h.Window.RunAiAsync(PadAiAction.Improve);

            Assert.Empty(h.Model.Requests);
            Assert.Equal(0, h.Usage.UsedToday);
            Assert.Equal(Visibility.Visible, h.Pane.Visibility);
            Assert.Equal("Select less text: at most 8,000 characters for a rewrite", h.Pane.StatusText.Text);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Assert.False(h.Pane.InsertButton.IsEnabled);
            Assert.False(h.Pane.RetryButton.IsEnabled);
            Assert.Equal(new[] { "AI improve: 0 chars in the request, 0 chars back, refused" }, h.Log);
        });

        [Fact]
        public Task Insert_below_adds_the_result_after_the_sources_last_line_as_one_undo_step() => OnUiAsync(async h =>
        {
            Write(h, "one\r\ntwo\r\nthree", "two");              // the note's own line ending is used
            h.Model.Reply("TWO");
            await h.Window.RunAiAsync(PadAiAction.Improve);

            Click(h.Pane.InsertButton);

            Assert.Equal("one\r\ntwo\r\n\r\nTWO\r\nthree", h.Editor.Document.Text);
            Assert.Equal("TWO", h.Editor.SelectedText);
            Assert.Equal("Inserted below", h.Pane.StatusText.Text);

            h.Editor.Undo();
            Assert.Equal("one\r\ntwo\r\nthree", h.Editor.Document.Text);
        });

        [Fact]
        public Task Insert_below_after_Replace_lands_under_the_new_text() => OnUiAsync(async h =>
        {
            Write(h, "one\ntwo\nthree", "two");
            h.Model.Reply("TWO\nand a half");
            await h.Window.RunAiAsync(PadAiAction.Improve);

            Click(h.Pane.ReplaceButton);
            Click(h.Pane.InsertButton);

            Assert.Equal("one\nTWO\nand a half\n\nTWO\nand a half\nthree", h.Editor.Document.Text);
        });

        [Fact]
        public Task Copy_hands_the_result_to_the_copy_action_and_says_so() => OnUiAsync(async h =>
        {
            Write(h, "The cat sat.");
            h.Model.Reply("\n- a cat sat\n\n");
            await h.Window.RunAiAsync(PadAiAction.Summarize);

            Click(h.Pane.CopyButton);

            Assert.Equal(new[] { "- a cat sat" }, h.Copied);
            Assert.Equal(h.Window.AiSessionNow!.ResultForNote, h.Copied[0]);
            Assert.Equal("Copied", h.Window.StatusMessage.Text);
            Assert.Equal(Visibility.Visible, h.Window.StatusMessage.Visibility);
            Assert.Equal("The cat sat.", h.Editor.Document.Text);
        });

        [Fact]
        public Task Try_again_runs_the_same_action_in_a_new_session_and_without_a_selection_on_the_same_text() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Model.Reply("First try").Reply("Second try").Reply("Third try");
            await h.Window.RunAiAsync(PadAiAction.Improve);
            AiSession first = h.Window.AiSessionNow!;

            Click(h.Pane.RetryButton);
            await Finished(h, after: first);

            AiSession second = h.Window.AiSessionNow!;
            Assert.NotSame(first, second);                        // a session runs once
            Assert.Equal(2, h.Model.Requests.Count);
            Assert.Equal(h.Sent(0), h.Sent(1));
            Assert.Equal("Second try", h.Pane.ResultBox.Shown);

            h.Editor.Select(0, 0);                                // the selection is gone; the text is where it was
            Click(h.Pane.RetryButton);
            await Finished(h, after: second);

            Assert.Equal(h.Sent(0), h.Sent(2));
            Assert.Equal("Selection, 13 characters", h.Pane.SourceText.Text);
            Click(h.Pane.ReplaceButton);
            Assert.Equal("Intro\nThird try\nOutro", h.Editor.Document.Text);
        });

        [Fact]
        public Task A_new_action_cancels_the_one_still_running() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            var slow = new GatedModel("Slow", " reply");
            h.Client = slow;
            Task first = h.Window.RunAiAsync(PadAiAction.Improve);
            await Reached(slow);

            h.Client = h.Model;
            h.Model.Reply("- summary");
            await h.Window.RunAiAsync(PadAiAction.Summarize);
            await first;

            Assert.True(slow.Cancelled);
            Assert.Equal("Summarize", h.Pane.TitleText.Text);
            Assert.Equal("- summary", h.Pane.ResultBox.Shown);
            Assert.Equal("", h.Pane.StatusText.Text);             // the first one's end left the pane alone
            Assert.Equal(Visibility.Collapsed, h.Pane.StopButton.Visibility);
        });

        [Fact]
        public Task The_log_line_has_the_action_the_counts_and_the_outcome_and_never_the_text() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Model.Reply("Good text");

            await h.Window.RunAiAsync(PadAiAction.Improve);

            int sent = PadAiPrompts.ForAction(PadAiAction.Improve.Instruction, Picked).Length;
            Assert.Equal(new[] { "AI improve: " + Count(sent) + " chars in the request, 9 chars back, ok" }, h.Log);
        });

        // ---- where the text goes, said where the action runs ------------------------------------

        [Fact]
        public Task The_source_line_ends_with_where_the_text_goes() => OnUiAsync(async h =>
        {
            h.Destination = "api.anthropic.com";
            Write(h, Note, Picked);
            h.Model.Reply("Good text").Reply("ok");

            await h.Window.RunAiAsync(PadAiAction.Improve);
            Assert.Equal("Selection, 13 characters · to api.anthropic.com", h.Pane.SourceText.Text);

            h.Window.ToggleAi();                                  // Ask AI: said before anything is sent
            Assert.Equal("Selection, 13 characters · to api.anthropic.com", h.Pane.SourceText.Text);
            Assert.Single(h.Model.Requests);

            h.Destination = "this PC";                            // the provider is changed in Settings while the pane waits
            h.Pane.InstructionBox.Text = "shorter";
            Assert.True(h.Pane.HandleInstructionKey(Key.Enter, ModifierKeys.None));
            await Finished(h);

            Assert.Equal("Selection, 13 characters · to this PC", h.Pane.SourceText.Text);   // read again when the request goes out
        });

        [Fact]
        public Task A_destination_that_cannot_be_read_is_not_named_and_never_stops_the_action() => OnUiAsync(async h =>
        {
            var warned = new List<string>();
            h.Window.Warn = warned.Add;
            h.Window.AiDestination = () => throw new InvalidOperationException("no settings at gpu.example");
            Write(h, Note, Picked);
            h.Model.Reply("Good text");

            await h.Window.RunAiAsync(PadAiAction.Improve);

            Assert.Equal("Selection, 13 characters", h.Pane.SourceText.Text);
            Assert.Equal("Good text", h.Pane.ResultBox.Shown);
            Assert.Contains("InvalidOperationException", Assert.Single(warned), StringComparison.Ordinal);
            Assert.DoesNotContain("gpu.example", warned[0], StringComparison.Ordinal);   // the type only
        });

        [Fact]
        public Task The_notes_status_names_where_the_passages_go() => OnUiWithSearch(async (h, search) =>
        {
            h.Destination = "api.anthropic.com";
            await Index(h, search, VpnNote);
            var model = new GatedModel("Use the", " office wifi [1].");
            h.Client = model;
            Task ask = AskNotes(h, search, "vpn");
            await Reached(model);
            SearchPane pane = h.Window.SearchPanel;

            Assert.Equal("Words · Answering from 1 passage · api.anthropic.com", pane.StatusText.Text);

            model.Gate.SetResult();
            await ask;

            Assert.Equal("Words · Answered from 1 passage · api.anthropic.com", pane.StatusText.Text);
        });

        // ---- review focus 1: a credential in the selection -------------------------------------

        [Fact]
        public Task A_credential_in_the_selection_is_sent_as_a_placeholder_and_comes_back_as_its_pill() => OnUiAsync(async h =>
        {
            h.Env.Vault.Load();
            h.Env.Vault.Create("246810");
            string id = h.Env.Vault.Add("hunter2", "Bank", null);
            string pill = SecretTokens.Format(id);
            string text = "login with " + pill + " pls";
            Write(h, text, text);
            h.Model.Reply("Please log in with [[CREDENTIAL_1]].");

            await h.Window.RunAiAsync(PadAiAction.Improve);

            ScriptedChatClient.Request request = Assert.Single(h.Model.Requests);
            Assert.Equal(PadAiPrompts.ForAction(PadAiAction.Improve.Instruction, "login with [[CREDENTIAL_1]] pls"), request.Messages[1].Text);
            string everything = string.Join("\n", request.Messages.Select(m => m.Text).Concat(h.Log));
            Assert.DoesNotContain("{{secret:", everything, StringComparison.Ordinal);
            Assert.DoesNotContain(id, everything, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", everything, StringComparison.Ordinal);

            Assert.Equal("Please log in with " + pill + ".", h.Pane.ResultBox.Shown);   // the pill is back
            Assert.True(h.Pane.ReplaceButton.IsEnabled);
            Click(h.Pane.ReplaceButton);
            Assert.Equal("Please log in with " + pill + ".", h.Editor.Document.Text);
        });

        [Fact]
        public Task A_reply_that_lost_the_credential_cannot_replace_the_selection() => OnUiAsync(async h =>
        {
            const string text = "login with {{secret:K7Q2M9XD}} pls";
            Write(h, text, text);
            h.Model.Reply("Please log in.");

            await h.Window.RunAiAsync(PadAiAction.Improve);

            Assert.Contains("[[CREDENTIAL_1]]", h.Sent(), StringComparison.Ordinal);
            Assert.DoesNotContain("{{secret:", h.Sent(), StringComparison.Ordinal);
            Assert.Equal(Visibility.Visible, h.Pane.ReplaceButton.Visibility);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Assert.Equal(SecretMask.Lost, h.Pane.StatusText.Text);

            Click(h.Pane.ReplaceButton);                          // even a forced click keeps the pill
            Assert.Equal(text, h.Editor.Document.Text);
        });

        // ---- review focus 2: the note is edited while the answer streams -----------------------

        [Fact]
        public Task Text_typed_around_the_selection_while_the_request_runs_does_not_move_the_replacement() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            var model = new GatedModel("Good ", "text");
            h.Client = model;
            Task run = h.Window.RunAiAsync(PadAiAction.Improve);
            await Reached(model);

            var document = h.Editor.Document;
            document.Insert(0, "A new first line\n");                                  // far before it
            int start = document.Text.IndexOf(Picked, StringComparison.Ordinal);
            document.Insert(start, ">> ");                                             // right at its start
            document.Insert(start + 3 + Picked.Length, " <<");                         // right at its end
            document.Insert(document.TextLength, "\nA new last line");                 // after it
            model.Gate.SetResult();
            await run;

            Assert.Equal("", h.Pane.StatusText.Text);
            Assert.True(h.Pane.ReplaceButton.IsEnabled);
            Click(h.Pane.ReplaceButton);

            Assert.Equal("A new first line\nIntro\n>> Good text <<\nOutro\nA new last line", document.Text);
            Assert.Equal("Good text", h.Editor.SelectedText);
        });

        [Fact]
        public Task Text_typed_inside_the_selection_refuses_Replace_and_Insert_below_still_works() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            var model = new GatedModel("Good ", "text");
            h.Client = model;
            Task run = h.Window.RunAiAsync(PadAiAction.Improve);
            await Reached(model);

            h.Editor.Document.Insert(Note.IndexOf("text", StringComparison.Ordinal), "new ");   // "bad new text here"
            model.Gate.SetResult();
            await run;

            Assert.Equal(Visibility.Visible, h.Pane.ReplaceButton.Visibility);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Assert.Equal(TextChanged, h.Pane.StatusText.Text);
            Click(h.Pane.ReplaceButton);                          // even a forced click replaces nothing
            Assert.Equal("Intro\nbad new text here\nOutro", h.Editor.Document.Text);

            Assert.True(h.Pane.InsertButton.IsEnabled);
            Click(h.Pane.InsertButton);
            Assert.Equal("Intro\nbad new text here\n\nGood text\nOutro", h.Editor.Document.Text);
        });

        [Fact]
        public Task An_edit_inside_the_source_after_the_reply_turns_Replace_off_at_once_and_undoing_it_turns_it_back_on() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Model.Reply("Good text");
            await h.Window.RunAiAsync(PadAiAction.Improve);
            Assert.True(h.Pane.ReplaceButton.IsEnabled);

            h.Editor.Document.Insert(Note.IndexOf("text", StringComparison.Ordinal), "x");

            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Assert.Equal(TextChanged, h.Pane.StatusText.Text);

            h.Editor.Undo();

            Assert.True(h.Pane.ReplaceButton.IsEnabled);
            Assert.Equal("", h.Pane.StatusText.Text);
        });

        [Fact]
        public Task Deleting_the_source_text_refuses_Replace() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Model.Reply("Good text");
            await h.Window.RunAiAsync(PadAiAction.Improve);

            h.Editor.Document.Remove(3, Note.Length - 6);         // from inside "Intro" to inside "Outro"
            h.Editor.Document.Insert(3, Picked);                  // the same words typed again are not the source

            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Assert.Equal(TextChanged, h.Pane.StatusText.Text);
            Click(h.Pane.ReplaceButton);
            Assert.Equal("Int" + Picked + "tro", h.Editor.Document.Text);
        });

        // ---- review focus 3: stopped, failed or cut short ---------------------------------------

        [Fact]
        public Task A_stopped_request_keeps_its_text_and_cannot_replace_the_selection() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            var model = new GatedModel("Good", " text");
            h.Client = model;
            Task run = h.Window.RunAiAsync(PadAiAction.Improve);
            await Reached(model);
            Assert.Equal(Visibility.Visible, h.Pane.StopButton.Visibility);
            Assert.True(h.Window.AiSessionNow!.Running);

            Click(h.Pane.StopButton);
            await run;

            Assert.True(model.Cancelled);
            Assert.Equal("Stopped", h.Pane.StatusText.Text);
            Assert.Equal("Good", h.Pane.ResultBox.Shown);
            Assert.Equal(Visibility.Collapsed, h.Pane.StopButton.Visibility);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Click(h.Pane.ReplaceButton);
            Assert.Equal(Note, h.Editor.Document.Text);
            Assert.True(h.Pane.InsertButton.IsEnabled);           // a partial reply may still be inserted below
            Assert.True(h.Pane.RetryButton.IsEnabled);
            Assert.EndsWith(", 4 chars back, stopped", Assert.Single(h.Log), StringComparison.Ordinal);
        });

        [Fact]
        public Task A_reply_cut_short_at_the_length_limit_cannot_replace_the_selection() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            var model = new GatedModel("Good", " te") { CutShort = true };
            model.Gate.SetResult();
            h.Client = model;

            await h.Window.RunAiAsync(PadAiAction.Improve);

            Assert.Equal("Cut short at the length limit", h.Pane.StatusText.Text);
            Assert.Equal("Good te", h.Pane.ResultBox.Shown);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Click(h.Pane.ReplaceButton);
            Assert.Equal(Note, h.Editor.Document.Text);
            Assert.EndsWith(", cut short", Assert.Single(h.Log), StringComparison.Ordinal);
        });

        [Fact]
        public Task A_reply_the_provider_stopped_with_its_content_filter_cannot_replace_the_selection() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            var model = new GatedModel("Good", " te") { Finish = ChatFinishReason.ContentFilter };
            model.Gate.SetResult();
            h.Client = model;

            await h.Window.RunAiAsync(PadAiAction.Improve);

            Assert.Equal("The AI provider stopped the reply (content filter).", h.Pane.StatusText.Text);
            Assert.Equal("Good te", h.Pane.ResultBox.Shown);      // what came stays, to read and to copy
            Assert.True(h.Pane.CopyButton.IsEnabled);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);         // but half a reply is never put into the note
            Assert.False(h.Pane.InsertButton.IsEnabled);
            Click(h.Pane.ReplaceButton);
            Click(h.Pane.InsertButton);
            Assert.Equal(Note, h.Editor.Document.Text);
            Assert.EndsWith(", failed", Assert.Single(h.Log), StringComparison.Ordinal);
        });

        [Fact]
        public Task A_reply_that_ended_for_a_reason_the_app_does_not_know_cannot_replace_the_selection() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            var model = new GatedModel("Good", " te") { Finish = new ChatFinishReason("recitation") };
            model.Gate.SetResult();
            h.Client = model;

            await h.Window.RunAiAsync(PadAiAction.Improve);

            Assert.Equal("Cut short at the length limit", h.Pane.StatusText.Text);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Click(h.Pane.ReplaceButton);
            Assert.Equal(Note, h.Editor.Document.Text);
        });

        [Fact]
        public Task A_failed_request_shows_the_error_and_can_neither_replace_nor_insert() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            var boom = new InvalidOperationException("boom");
            h.Model.Fail(boom);

            await h.Window.RunAiAsync(PadAiAction.Improve);

            Assert.Equal(AiErrorText.Describe(boom), h.Pane.StatusText.Text);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Assert.False(h.Pane.InsertButton.IsEnabled);
            Assert.True(h.Pane.RetryButton.IsEnabled);
            Click(h.Pane.ReplaceButton);
            Click(h.Pane.InsertButton);
            Assert.Equal(Note, h.Editor.Document.Text);
            Assert.EndsWith(", 0 chars back, failed", Assert.Single(h.Log), StringComparison.Ordinal);
            Assert.DoesNotContain("boom", h.Log[0], StringComparison.Ordinal);
        });

        // ---- review focus 4: the source note is switched away from or closed --------------------

        [Fact]
        public Task On_another_tab_Replace_and_Insert_are_off_and_that_note_is_never_edited() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            OpenNote source = h.Shown;
            h.Model.Reply("Good text");
            await h.Window.RunAiAsync(PadAiAction.Improve);

            h.Window.NewTab();
            OpenNote other = h.Shown;
            Write(h, "another note entirely, longer than the first one");

            Assert.Equal(Visibility.Visible, h.Pane.Visibility);  // switching tabs keeps the pane and its result
            Assert.Equal("Good text", h.Pane.ResultBox.Shown);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Assert.False(h.Pane.InsertButton.IsEnabled);
            Assert.False(h.Pane.RetryButton.IsEnabled);           // nor is this note's text sent in its place
            Assert.Equal(NotShown, h.Pane.StatusText.Text);
            Click(h.Pane.ReplaceButton);                          // even forced clicks edit nothing
            Click(h.Pane.InsertButton);
            Assert.Equal("another note entirely, longer than the first one", other.TextProvider());
            Assert.Equal(Note, source.TextProvider());

            h.Window.SelectTab(0);                                // back on the source note

            Assert.True(h.Pane.ReplaceButton.IsEnabled);
            Assert.True(h.Pane.InsertButton.IsEnabled);
            Assert.Equal("", h.Pane.StatusText.Text);
            Click(h.Pane.ReplaceButton);
            Assert.Equal("Intro\nGood text\nOutro", source.TextProvider());
            Assert.Equal("another note entirely, longer than the first one", other.TextProvider());
        });

        [Fact]
        public Task Switching_tabs_while_the_answer_streams_keeps_the_request_and_edits_no_other_note() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            OpenNote source = h.Shown;
            var model = new GatedModel("Good ", "text");
            h.Client = model;
            Task run = h.Window.RunAiAsync(PadAiAction.Improve);
            await Reached(model);

            h.Window.NewTab();
            OpenNote other = h.Shown;
            Write(h, "the other note");
            model.Gate.SetResult();
            await run;

            Assert.False(model.Cancelled);
            Assert.Equal("Good text", h.Pane.ResultBox.Shown);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Assert.False(h.Pane.InsertButton.IsEnabled);
            Click(h.Pane.ReplaceButton);
            Click(h.Pane.InsertButton);
            Assert.Equal("the other note", other.TextProvider());
            Assert.Equal(Note, source.TextProvider());

            h.Window.SelectTab(0);
            Assert.True(h.Pane.ReplaceButton.IsEnabled);
        });

        [Fact]
        public Task Closing_the_source_note_cancels_the_request_and_closes_the_pane() => OnUiAsync(async h =>
        {
            Write(h, "the other note");
            OpenNote other = h.Shown;
            h.Window.NewTab();
            Write(h, Note, Picked);
            OpenNote source = h.Shown;
            var model = new GatedModel("Good ", "text");
            h.Client = model;
            Task run = h.Window.RunAiAsync(PadAiAction.Improve);
            await Reached(model);

            h.Window.CloseTab(source);
            await run;

            Assert.True(model.Cancelled);
            Assert.Equal(Visibility.Collapsed, h.Pane.Visibility);
            Assert.Null(h.Window.AiSessionNow);
            Assert.Same(other, h.Shown);
            Assert.Equal("the other note", other.TextProvider());
        });

        [Fact]
        public Task Closing_another_note_leaves_the_request_alone() => OnUiAsync(async h =>
        {
            Write(h, "the other note");
            OpenNote other = h.Shown;
            h.Window.NewTab();
            Write(h, Note, Picked);
            var model = new GatedModel("Good ", "text");
            h.Client = model;
            Task run = h.Window.RunAiAsync(PadAiAction.Improve);
            await Reached(model);

            h.Window.CloseTab(other);
            model.Gate.SetResult();
            await run;

            Assert.False(model.Cancelled);
            Assert.Equal(Visibility.Visible, h.Pane.Visibility);
            Assert.True(h.Pane.ReplaceButton.IsEnabled);
        });

        [Fact]
        public Task Closing_the_window_cancels_the_request() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            var model = new GatedModel("Good ", "text");
            h.Client = model;
            Task run = h.Window.RunAiAsync(PadAiAction.Improve);
            await Reached(model);

            h.Window.CloseForExit();
            await run;

            Assert.True(model.Cancelled);
            Assert.Null(h.Window.AiSessionNow);
            Assert.Equal(Note, h.Shown.TextProvider());
        });

        [Fact]
        public Task Hiding_the_last_window_with_its_close_button_stops_the_request() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            var model = new GatedModel("Good", " text");
            h.Client = model;
            Task run = h.Window.RunAiAsync(PadAiAction.Improve);
            await Reached(model);

            h.Window.CloseByUser();
            await run;

            Assert.True(h.Window.IsHiddenByClose);
            Assert.True(model.Cancelled);
            Assert.Equal("Stopped", h.Pane.StatusText.Text);      // nothing runs behind a hidden window
        });

        [Fact]
        public Task The_close_button_of_the_pane_cancels_the_request_and_closes_the_pane() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            var model = new GatedModel("Good ", "text");
            h.Client = model;
            Task run = h.Window.RunAiAsync(PadAiAction.Improve);
            await Reached(model);

            Click(h.Pane.CloseButton);
            await run;

            Assert.True(model.Cancelled);
            Assert.Equal(Visibility.Collapsed, h.Pane.Visibility);
            Assert.Null(h.Window.AiSessionNow);
            Assert.Equal(Note, h.Editor.Document.Text);
        });

        // ---- review focus 5: AI off, no key, the daily limit -------------------------------------

        [Fact]
        public Task With_AI_off_Ctrl_Shift_A_sends_nothing_and_says_how_to_turn_it_on() => OnUi(h =>
        {
            h.AiOn = false;
            Write(h, Note, Picked);

            Assert.True(h.Window.HandleShortcut(Key.A, CtrlShift));

            Assert.Equal(AiOff, h.Window.StatusMessage.Text);
            Assert.Equal(Visibility.Visible, h.Window.StatusMessage.Visibility);
            Assert.Equal(Visibility.Collapsed, h.Pane.Visibility);
            Assert.Null(h.Window.AiSessionNow);
            Assert.Empty(h.Model.Requests);
            Assert.Equal(0, h.Usage.UsedToday);
        });

        [Fact]
        public Task With_AI_off_a_direct_call_builds_no_runner_counts_nothing_and_sends_nothing() => OnUiAsync(async h =>
        {
            h.AiOn = false;
            Write(h, Note, Picked);

            await h.Window.RunAiAsync(PadAiAction.Improve);

            Assert.Empty(h.Model.Requests);
            Assert.Equal(0, h.Usage.UsedToday);
            Assert.Equal(0, h.RunnersBuilt);
            Assert.Null(h.Window.AiSessionNow);
            Assert.Equal(Visibility.Collapsed, h.Pane.Visibility);
            Assert.Equal(AiOff, h.Window.StatusMessage.Text);   // the user is told why
            Assert.Empty(h.Log);
        });

        [Fact]
        public Task With_AI_turned_off_while_the_pane_waits_an_instruction_entered_sends_nothing() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Window.ToggleAi();
            AiSession waiting = h.Window.AiSessionNow!;
            Assert.True(waiting.AwaitingInstruction);
            h.AiOn = false;

            h.Pane.InstructionBox.Text = "make it positive";
            Assert.True(h.Pane.HandleInstructionKey(Key.Enter, ModifierKeys.None));
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.Empty(h.Model.Requests);
            Assert.Equal(0, h.Usage.UsedToday);
            Assert.Equal(0, h.RunnersBuilt);
            Assert.Same(waiting, h.Window.AiSessionNow);         // nothing started
            Assert.Equal(AiOff, h.Window.StatusMessage.Text);
            Assert.Equal(Visibility.Visible, h.Window.StatusMessage.Visibility);
        });

        [Fact]
        public Task With_AI_turned_off_before_Try_again_nothing_is_sent() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Model.Reply("Good text");
            await h.Window.RunAiAsync(PadAiAction.Improve);
            AiSession done = h.Window.AiSessionNow!;
            Assert.Equal(1, h.Usage.UsedToday);
            h.AiOn = false;

            Click(h.Pane.RetryButton);
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.Single(h.Model.Requests);
            Assert.Equal(1, h.Usage.UsedToday);
            Assert.Equal(1, h.RunnersBuilt);
            Assert.Same(done, h.Window.AiSessionNow);
            Assert.Equal(AiOff, h.Window.StatusMessage.Text);
        });

        [Fact]
        public Task With_AI_turned_off_between_the_click_and_the_request_the_pane_says_so_and_nothing_is_sent() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            int asked = 0;
            h.Window.AiEnabled = () => asked++ == 0;              // on at the click, off when the request is about to start

            await h.Window.RunAiAsync(PadAiAction.Improve);

            Assert.Equal(2, asked);                               // asked again right before the request
            Assert.Empty(h.Model.Requests);
            Assert.Equal(0, h.Usage.UsedToday);
            Assert.Equal(0, h.RunnersBuilt);
            Assert.Equal(AiOff, h.Pane.StatusText.Text);
            Assert.False(h.Window.AiSessionNow!.Running);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Assert.Equal(new[] { "AI improve: 0 chars in the request, 0 chars back, failed" }, h.Log);
        });

        [Fact]
        public Task A_setting_that_cannot_be_read_counts_as_off() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            var warned = new List<string>();
            h.Window.Warn = warned.Add;
            h.Window.AiEnabled = () => throw new InvalidOperationException("no settings");

            Assert.Equal(new[] { "Set up AI…" }, Headers(AiMenu(h)));
            await h.Window.RunAiAsync(PadAiAction.Improve);
            Assert.True(h.Window.HandleShortcut(Key.A, CtrlShift));

            Assert.Empty(h.Model.Requests);
            Assert.Equal(0, h.RunnersBuilt);
            Assert.Null(h.Window.AiSessionNow);
            Assert.Equal(AiOff, h.Window.StatusMessage.Text);
            Assert.All(warned, line => Assert.DoesNotContain("no settings", line, StringComparison.Ordinal));
        });

        [Fact]
        public Task With_AI_off_Ctrl_Shift_A_still_closes_an_open_pane() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Model.Reply("Good text");
            await h.Window.RunAiAsync(PadAiAction.Improve);
            h.AiOn = false;
            h.Window.AiPaneHasFocus = () => true;

            Assert.True(h.Window.HandleShortcut(Key.A, CtrlShift));

            Assert.Equal(Visibility.Collapsed, h.Pane.Visibility);
            Assert.Single(h.Model.Requests);
        });

        [Fact]
        public Task A_provider_without_a_key_shows_its_problem_in_the_pane_and_sends_nothing() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Client = null;
            h.Problem = "Add an API key in Settings > AI.";

            await h.Window.RunAiAsync(PadAiAction.Improve);

            Assert.Equal(Visibility.Visible, h.Pane.Visibility);
            Assert.Equal("Add an API key in Settings > AI.", h.Pane.StatusText.Text);
            Assert.Empty(h.Model.Requests);
            Assert.Equal(0, h.Usage.UsedToday);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Assert.False(h.Pane.InsertButton.IsEnabled);
            Assert.Equal(Note, h.Editor.Document.Text);
        });

        [Fact]
        public Task At_the_daily_limit_the_pane_says_so_and_nothing_is_sent() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Limit = 1;
            Assert.True(h.Usage.TryConsume(1));

            await h.Window.RunAiAsync(PadAiAction.Improve);

            Assert.Equal(AiAssistant.LimitText(1), h.Pane.StatusText.Text);
            Assert.Empty(h.Model.Requests);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);
        });

        [Fact]
        public Task Before_the_settings_load_the_pane_says_AI_is_not_available_yet() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Window.AiRunnerFactory = () => null;

            await h.Window.RunAiAsync(PadAiAction.Improve);

            Assert.Equal("AI is not available yet", h.Pane.StatusText.Text);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Assert.True(h.Pane.RetryButton.IsEnabled);
            Assert.Equal(new[] { "AI improve: 0 chars in the request, 0 chars back, failed" }, h.Log);
        });

        [Fact]
        public Task A_runner_that_cannot_be_built_fails_in_the_pane_and_never_throws_into_the_window() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            var warned = new List<string>();
            h.Window.Warn = warned.Add;
            var broken = new InvalidOperationException("the settings are broken");
            h.Window.AiRunnerFactory = () => throw broken;

            await h.Window.RunAiAsync(PadAiAction.Improve);

            Assert.Equal(AiErrorText.Describe(broken), h.Pane.StatusText.Text);
            Assert.False(h.Window.AiSessionNow!.Running);
            Assert.Contains("InvalidOperationException", Assert.Single(warned), StringComparison.Ordinal);
            Assert.DoesNotContain("broken", warned[0], StringComparison.Ordinal);   // the type only
        });

        // ---- Ask AI and Ctrl+Shift+A --------------------------------------------------------------

        [Fact]
        public Task Ctrl_Shift_A_opens_the_pane_on_Ask_AI_with_the_instruction_box_focused() => OnUi(h =>
        {
            Write(h, Note, Picked);

            Assert.True(h.Window.HandleShortcut(Key.A, CtrlShift));

            Assert.Equal(Visibility.Visible, h.Pane.Visibility);
            Assert.Equal("Ask AI", h.Pane.TitleText.Text);
            Assert.Equal("Selection, 13 characters", h.Pane.SourceText.Text);
            Assert.Equal(Visibility.Visible, h.Pane.InstructionBox.Visibility);
            Assert.Same(h.Pane.InstructionBox, FocusManager.GetFocusedElement(h.Window));
            Assert.True(h.Window.AiSessionNow!.AwaitingInstruction);
            Assert.Empty(h.Model.Requests);                       // nothing is sent until an instruction is entered
            Assert.Empty(h.Log);
        });

        [Fact]
        public Task Ctrl_Shift_A_again_with_the_focus_in_the_pane_closes_it() => OnUi(h =>
        {
            Write(h, Note, Picked);
            bool focusInPane = false;
            h.Window.AiPaneHasFocus = () => focusInPane;
            h.Window.ToggleAi();
            AiSession asked = h.Window.AiSessionNow!;

            h.Window.ToggleAi();                                  // the focus is in the editor: Ask AI again
            Assert.Equal(Visibility.Visible, h.Pane.Visibility);
            Assert.NotSame(asked, h.Window.AiSessionNow);
            Assert.True(h.Window.AiSessionNow!.AwaitingInstruction);

            focusInPane = true;
            h.Window.ToggleAi();
            Assert.Equal(Visibility.Collapsed, h.Pane.Visibility);
            Assert.Null(h.Window.AiSessionNow);
        });

        [Fact]
        public Task An_instruction_entered_in_the_pane_runs_on_the_selection_and_its_result_can_replace_it() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Model.Reply("good text there");
            h.Window.ToggleAi();

            h.Pane.InstructionBox.Text = "  make it positive ";
            Assert.True(h.Pane.HandleInstructionKey(Key.Enter, ModifierKeys.None));
            await Finished(h);

            Assert.Equal(PadAiPrompts.ForAction("make it positive", Picked), h.Sent());
            Assert.Equal(Visibility.Collapsed, h.Pane.InstructionBox.Visibility);
            Assert.Equal("good text there", h.Pane.ResultBox.Shown);
            Assert.Equal(Visibility.Visible, h.Pane.ReplaceButton.Visibility);
            Assert.True(h.Pane.ReplaceButton.IsEnabled);
            Click(h.Pane.ReplaceButton);
            Assert.Equal("Intro\ngood text there\nOutro", h.Editor.Document.Text);
            Assert.StartsWith("AI ask: ", Assert.Single(h.Log), StringComparison.Ordinal);
            Assert.DoesNotContain("positive", h.Log[0], StringComparison.Ordinal);
        });

        [Fact]
        public Task An_instruction_entered_after_the_selection_was_dropped_still_runs_on_the_text_the_pane_named() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Model.Reply("good text there");
            h.Window.ToggleAi();
            h.Editor.Select(0, 0);                                // a click in the note on the way to the pane

            h.Pane.InstructionBox.Text = "make it positive";
            Assert.True(h.Pane.HandleInstructionKey(Key.Enter, ModifierKeys.None));
            await Finished(h);

            Assert.Equal(PadAiPrompts.ForAction("make it positive", Picked), h.Sent());   // not the whole note
            Assert.Equal("Selection, 13 characters", h.Pane.SourceText.Text);
        });

        [Fact]
        public Task Ask_AI_with_no_selection_takes_the_whole_note() => OnUiAsync(async h =>
        {
            Write(h, Note);
            h.Model.Reply("It is a short note.");
            h.Window.ToggleAi();
            Assert.Equal("Whole note, 25 characters", h.Pane.SourceText.Text);

            h.Pane.InstructionBox.Text = "what is this?";
            Assert.True(h.Pane.HandleInstructionKey(Key.Enter, ModifierKeys.None));
            await Finished(h);

            Assert.Equal(PadAiPrompts.ForAction("what is this?", Note), h.Sent());
            Assert.Equal(Visibility.Collapsed, h.Pane.ReplaceButton.Visibility);
            Assert.True(h.Pane.InsertButton.IsEnabled);
        });

        [Fact]
        public Task Ask_AI_on_text_over_the_limit_shows_the_refusal_at_once_and_no_instruction_box() => OnUi(h =>
        {
            Write(h, new string('x', PadAiAction.ReadMaxChars + 1));    // the whole note, one character too long

            Assert.True(h.Window.HandleShortcut(Key.A, CtrlShift));

            Assert.Equal(Visibility.Visible, h.Pane.Visibility);
            Assert.Equal("Ask AI", h.Pane.TitleText.Text);
            Assert.Equal("Select less text: at most 24,000 characters", h.Pane.StatusText.Text);
            Assert.Equal(Visibility.Visible, h.Pane.StatusText.Visibility);
            Assert.Equal(Visibility.Collapsed, h.Pane.InstructionBox.Visibility);   // not a box that could never run
            Assert.False(h.Window.AiSessionNow!.AwaitingInstruction);
            Assert.False(h.Pane.RetryButton.IsEnabled);

            h.Pane.InstructionBox.Text = "summarize";                   // even a forced Enter sends nothing
            h.Pane.HandleInstructionKey(Key.Enter, ModifierKeys.None);
            Assert.Empty(h.Model.Requests);
            Assert.Equal(0, h.Usage.UsedToday);
            Assert.Equal(new[] { "AI ask: 0 chars in the request, 0 chars back, refused" }, h.Log);
        });

        [Fact]
        public Task A_read_result_on_another_tab_says_why_Insert_below_and_Try_again_are_off() => OnUiAsync(async h =>
        {
            Write(h, Note);
            h.Model.Reply("- a summary");
            await h.Window.RunAiAsync(PadAiAction.Summarize);
            Assert.Equal("", h.Pane.StatusText.Text);

            h.Window.NewTab();

            Assert.False(h.Pane.InsertButton.IsEnabled);
            Assert.False(h.Pane.RetryButton.IsEnabled);
            Assert.True(h.Pane.CopyButton.IsEnabled);
            Assert.Equal(NotShown, h.Pane.StatusText.Text);             // not two dead buttons and no word why
            Assert.Equal(Visibility.Visible, h.Pane.StatusText.Visibility);

            h.Window.SelectTab(0);

            Assert.True(h.Pane.InsertButton.IsEnabled);
            Assert.Equal("", h.Pane.StatusText.Text);
        });

        [Fact]
        public Task With_nothing_to_work_on_the_status_bar_says_so_and_the_pane_stays_closed() => OnUiAsync(async h =>
        {
            Write(h, "");

            Assert.True(h.Window.HandleShortcut(Key.A, CtrlShift));
            Assert.Equal(NoText, h.Window.StatusMessage.Text);
            Assert.Equal(Visibility.Collapsed, h.Pane.Visibility);

            Write(h, "  \n\n ");                                  // blank is nothing too
            await h.Window.RunAiAsync(PadAiAction.Summarize);
            Assert.Null(h.Window.AiSessionNow);
            Assert.Empty(h.Model.Requests);
        });

        // ---- refusals in the status bar -----------------------------------------------------------

        [Fact]
        public Task A_rewrite_on_a_read_only_editor_is_refused_in_the_status_bar() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Editor.IsReadOnly = true;

            await h.Window.RunAiAsync(PadAiAction.Improve);

            Assert.Equal(ReadOnly, h.Window.StatusMessage.Text);
            Assert.Equal(Visibility.Collapsed, h.Pane.Visibility);
            Assert.Null(h.Window.AiSessionNow);
            Assert.Empty(h.Model.Requests);
        });

        [Fact]
        public Task A_read_action_on_a_read_only_editor_runs_and_offers_Copy_only() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Editor.IsReadOnly = true;
            h.Model.Reply("- bad text");

            await h.Window.RunAiAsync(PadAiAction.Summarize);

            Assert.Equal("- bad text", h.Pane.ResultBox.Shown);
            Assert.False(h.Pane.InsertButton.IsEnabled);
            Assert.True(h.Pane.CopyButton.IsEnabled);
            Click(h.Pane.InsertButton);
            Assert.Equal(Note, h.Editor.Document.Text);
        });

        [Fact]
        public Task While_the_history_preview_covers_the_note_the_result_cannot_be_applied() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Model.Reply("Good text");
            await h.Window.RunAiAsync(PadAiAction.Improve);

            h.Window.PreviewPanel.Visibility = Visibility.Visible;

            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Assert.False(h.Pane.InsertButton.IsEnabled);
            Assert.Equal(ReadOnly, h.Pane.StatusText.Text);
            Click(h.Pane.ReplaceButton);
            Click(h.Pane.InsertButton);
            Assert.Equal(Note, h.Editor.Document.Text);

            h.Window.PreviewPanel.Visibility = Visibility.Collapsed;

            Assert.True(h.Pane.ReplaceButton.IsEnabled);
            Assert.True(h.Pane.InsertButton.IsEnabled);
        });

        [Fact]
        public Task A_rectangle_selection_is_refused_with_the_words_the_Tools_use() => OnUiAsync(async h =>
        {
            Write(h, "123456\n789012");
            var area = h.Editor.TextArea;
            // An unshown window has no layout, and a rectangle is measured in visual columns.
            h.Window.Measure(new System.Windows.Size(800, 600));
            h.Window.Arrange(new Rect(0, 0, 800, 600));
            h.Window.UpdateLayout();
            area.TextView.EnsureVisualLines();
            area.Selection = new RectangleSelection(area, new TextViewPosition(1, 2), new TextViewPosition(2, 4));

            await h.Window.RunAiAsync(PadAiAction.Summarize);

            Assert.Equal(EditorMenus.RectangleRefused, h.Window.StatusMessage.Text);
            Assert.Equal(Visibility.Collapsed, h.Pane.Visibility);
            Assert.Empty(h.Model.Requests);
        });

        // ---- the pane among the others ------------------------------------------------------------

        [Fact]
        public Task Opening_the_AI_pane_closes_History_and_Search_and_opening_either_closes_the_AI_pane() => OnUi(h =>
        {
            Write(h, Note, Picked);
            Assert.Equal(Visibility.Collapsed, h.Pane.Visibility);   // built closed

            Assert.True(h.Window.HandleShortcut(Key.H, CtrlShift));
            Assert.Equal(Visibility.Visible, h.Window.HistoryPanel.Visibility);
            h.Window.ToggleAi();
            Assert.Equal(Visibility.Visible, h.Pane.Visibility);
            Assert.Equal(Visibility.Collapsed, h.Window.HistoryPanel.Visibility);

            h.Window.ToggleSearch();
            Assert.Equal(Visibility.Visible, h.Window.SearchPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, h.Pane.Visibility);
            Assert.Null(h.Window.AiSessionNow);

            h.Window.ToggleAi();
            Assert.Equal(Visibility.Visible, h.Pane.Visibility);
            Assert.Equal(Visibility.Collapsed, h.Window.SearchPanel.Visibility);

            Assert.True(h.Window.HandleShortcut(Key.H, CtrlShift));
            Assert.Equal(Visibility.Visible, h.Window.HistoryPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, h.Pane.Visibility);
            Assert.Null(h.Window.AiSessionNow);
        });

        [Fact]
        public Task Opening_Search_while_a_request_runs_cancels_it() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            var model = new GatedModel("Good ", "text");
            h.Client = model;
            Task run = h.Window.RunAiAsync(PadAiAction.Improve);
            await Reached(model);

            h.Window.ToggleSearch();
            await run;

            Assert.True(model.Cancelled);
            Assert.Equal(Visibility.Collapsed, h.Pane.Visibility);
        });

        [Fact]
        public Task The_AI_pane_follows_the_pads_theme() => OnUi(h =>
        {
            Assert.Equal(ModernWpf.ElementTheme.Dark, ModernWpf.ThemeManager.GetRequestedTheme(h.Pane));

            h.Window.ToggleTheme();

            Assert.Equal(ModernWpf.ElementTheme.Light, ModernWpf.ThemeManager.GetRequestedTheme(h.Pane));
            Assert.Equal(PadThemeApplier.ToColor(PadPalette.Light.Chrome), ((System.Windows.Media.SolidColorBrush)h.Pane.Background).Color);
        });

        // ---- Try again and an entered instruction send only the text the pane names -------------------

        private const string ShowSource = "Show the note this came from to try again";
        private const string SourceGone = "The text this ran on is gone; select text and run the action again";

        [Fact]
        public Task Try_again_while_another_note_is_shown_sends_nothing_of_that_note() => OnUiAsync(async h =>
        {
            Write(h, Note);
            h.Model.Reply("- a summary").Reply("- another summary");
            await h.Window.RunAiAsync(PadAiAction.Summarize);          // the whole of note A
            AiSession done = h.Window.AiSessionNow!;
            Assert.Equal(1, h.Usage.UsedToday);

            h.Window.NewTab();
            Write(h, "note B holds something private");
            Assert.False(h.Pane.RetryButton.IsEnabled);                 // not offered while note B is shown
            Click(h.Pane.RetryButton);                                  // and a forced click sends nothing
            h.Editor.Select(0, 6);                                      // nor with a selection in note B
            Click(h.Pane.RetryButton);
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.Single(h.Model.Requests);
            Assert.Equal(1, h.Usage.UsedToday);
            Assert.Equal(1, h.RunnersBuilt);
            Assert.Same(done, h.Window.AiSessionNow);
            Assert.Equal(ShowSource, h.Window.StatusMessage.Text);
            Assert.Equal(Visibility.Visible, h.Window.StatusMessage.Visibility);

            h.Window.SelectTab(0);                                      // back on note A it runs again, on note A
            Assert.True(h.Pane.RetryButton.IsEnabled);
            Click(h.Pane.RetryButton);
            await Finished(h, after: done);

            Assert.Equal(h.Sent(0), h.Sent(1));
            Assert.All(h.Model.Requests, r => Assert.DoesNotContain("note B", r.Messages[1].Text, StringComparison.Ordinal));
        });

        [Fact]
        public Task An_instruction_entered_while_another_note_is_shown_sends_nothing_of_that_note() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Model.Reply("ok");
            h.Window.ToggleAi();                                        // Ask AI on 13 characters of note A
            AiSession waiting = h.Window.AiSessionNow!;
            Assert.Equal("Selection, 13 characters", h.Pane.SourceText.Text);

            h.Window.NewTab();
            Write(h, "note B holds something private");
            h.Pane.InstructionBox.Text = "summarize";
            Assert.True(h.Pane.HandleInstructionKey(Key.Enter, ModifierKeys.None));
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.Empty(h.Model.Requests);
            Assert.Equal(0, h.Usage.UsedToday);
            Assert.Equal(0, h.RunnersBuilt);
            Assert.Same(waiting, h.Window.AiSessionNow);
            Assert.Equal(ShowSource, h.Window.StatusMessage.Text);

            h.Window.SelectTab(0);                                      // on note A it runs, on the 13 characters
            Assert.True(h.Pane.HandleInstructionKey(Key.Enter, ModifierKeys.None));
            await Finished(h);

            Assert.Equal(PadAiPrompts.ForAction("summarize", Picked), Assert.Single(h.Model.Requests).Messages[1].Text);
        });

        [Fact]
        public Task Try_again_after_the_source_text_was_deleted_sends_nothing_instead_of_the_whole_note() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Model.Reply("- bad text");
            await h.Window.RunAiAsync(PadAiAction.Summarize);          // a read action on a selection
            AiSession done = h.Window.AiSessionNow!;

            h.Editor.Document.Remove(Note.IndexOf(Picked, StringComparison.Ordinal), Picked.Length);
            h.Editor.Select(0, 0);
            Click(h.Pane.RetryButton);
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.Single(h.Model.Requests);                            // not "Intro\n\nOutro", the whole note
            Assert.Equal(1, h.Usage.UsedToday);
            Assert.Same(done, h.Window.AiSessionNow);
            Assert.Equal(SourceGone, h.Window.StatusMessage.Text);
        });

        [Fact]
        public Task An_instruction_entered_after_the_source_text_was_deleted_sends_nothing() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Window.ToggleAi();
            AiSession waiting = h.Window.AiSessionNow!;

            h.Editor.Document.Remove(Note.IndexOf(Picked, StringComparison.Ordinal), Picked.Length);
            h.Pane.InstructionBox.Text = "what is this?";
            Assert.True(h.Pane.HandleInstructionKey(Key.Enter, ModifierKeys.None));
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.Empty(h.Model.Requests);
            Assert.Equal(0, h.Usage.UsedToday);
            Assert.Same(waiting, h.Window.AiSessionNow);
            Assert.Equal(SourceGone, h.Window.StatusMessage.Text);
        });

        [Fact]
        public Task Try_again_takes_the_text_the_pane_names_whatever_is_selected_now() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Model.Reply("- one").Reply("- two").Reply("- three").Reply("- four");
            await h.Window.RunAiAsync(PadAiAction.Summarize);
            AiSession first = h.Window.AiSessionNow!;

            h.Editor.Select(0, 5);                                      // "Intro" is selected now
            Click(h.Pane.RetryButton);
            await Finished(h, after: first);

            Assert.Equal(h.Sent(0), h.Sent(1));                         // still the 13 characters the pane names
            Assert.Equal("Selection, 13 characters", h.Pane.SourceText.Text);

            h.Editor.Select(0, 0);
            await h.Window.RunAiAsync(PadAiAction.Summarize);          // the whole note, from the menu
            AiSession whole = h.Window.AiSessionNow!;
            h.Editor.Select(0, 5);
            Click(h.Pane.RetryButton);
            await Finished(h, after: whole);

            Assert.Equal(PadAiPrompts.ForAction(PadAiAction.Summarize.Instruction, Note), h.Sent(3));
            Assert.Equal("Whole note, 25 characters", h.Pane.SourceText.Text);
        });

        [Theory]
        [InlineData(false)]   // a whole note: the whole of the other note would go out
        [InlineData(true)]    // a selection: the same offsets of the other note would
        public Task A_rerun_whose_note_is_switched_away_before_its_text_is_read_sends_nothing(bool selection) => OnUiAsync(async h =>
        {
            Write(h, Note, selection ? Picked : null);
            h.Model.Reply("- a summary");
            await h.Window.RunAiAsync(PadAiAction.Summarize);
            AiSession done = h.Window.AiSessionNow!;
            h.Window.NewTab();
            Write(h, "note B holds something private");
            h.Window.SelectTab(0);                                      // note A is shown at the click

            // Between the check at the click and the reading of the text the setting is asked
            // once more; this one shows note B when it is. The text is read behind a second check.
            int asked = 0;
            h.Window.AiEnabled = () =>
            {
                if (++asked == 2) h.Window.SelectTab(1);
                return true;
            };
            Click(h.Pane.RetryButton);
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.True(asked >= 2, "the setting was asked " + asked + " times");
            Assert.Single(h.Model.Requests);
            Assert.Equal(1, h.Usage.UsedToday);
            Assert.Equal(1, h.RunnersBuilt);
            Assert.Same(done, h.Window.AiSessionNow);
            Assert.Equal(ShowSource, h.Window.StatusMessage.Text);
        });

        // ---- line endings ------------------------------------------------------------------------------

        [Fact]
        public Task A_reply_takes_the_notes_line_endings_when_it_replaces_the_selection() => OnUiAsync(async h =>
        {
            const string note = "one\r\ntwo\r\nthree\r\nfour";
            Write(h, note, "two\r\nthree");
            h.Model.Reply("a\nb\nc\nd\ne");                             // a model writes LF
            await h.Window.RunAiAsync(PadAiAction.Improve);

            Click(h.Pane.ReplaceButton);

            Assert.Equal("one\r\na\r\nb\r\nc\r\nd\r\ne\r\nfour", h.Editor.Document.Text);   // no lone LF
            Assert.Equal("a\r\nb\r\nc\r\nd\r\ne", h.Editor.SelectedText);                  // exactly the inserted text

            Click(h.Pane.InsertButton);                                 // the anchors hold all of it, carriage returns too
            Assert.Equal("one\r\na\r\nb\r\nc\r\nd\r\ne\r\n\r\na\r\nb\r\nc\r\nd\r\ne\r\nfour", h.Editor.Document.Text);

            h.Editor.Undo();
            h.Editor.Undo();
            Assert.Equal(note, h.Editor.Document.Text);
        });

        [Fact]
        public Task A_reply_takes_the_notes_line_endings_when_it_is_inserted_below() => OnUiAsync(async h =>
        {
            Write(h, "one\r\ntwo\r\nthree", "two");
            h.Model.Reply("- a\n- b\r- c");                             // LF, and a lone CR
            await h.Window.RunAiAsync(PadAiAction.Summarize);

            Click(h.Pane.InsertButton);

            Assert.Equal("one\r\ntwo\r\n\r\n- a\r\n- b\r\n- c\r\nthree", h.Editor.Document.Text);
            Assert.Equal("- a\r\n- b\r\n- c", h.Editor.SelectedText);
        });

        [Fact]
        public Task A_CRLF_reply_into_an_LF_note_becomes_LF() => OnUiAsync(async h =>
        {
            Write(h, "one\ntwo\nthree", "two");
            h.Model.Reply("A\r\nB");
            await h.Window.RunAiAsync(PadAiAction.Improve);

            Click(h.Pane.ReplaceButton);

            Assert.Equal("one\nA\nB\nthree", h.Editor.Document.Text);
            Assert.Equal("A\nB", h.Editor.SelectedText);
        });

        // ---- Insert below once the source's place is gone ---------------------------------------------

        [Fact]
        public Task After_the_note_was_replaced_whole_Insert_below_goes_to_the_carets_line_not_under_line_one() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Model.Reply("Good text");
            await h.Window.RunAiAsync(PadAiAction.Improve);

            h.Window.ReplaceShownText("alpha\nbeta\ngamma");            // a reload, a restored version: the anchors collapse
            h.Editor.CaretOffset = "alpha\nbe".Length;                  // the caret is in "beta"
            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Assert.True(h.Pane.InsertButton.IsEnabled);

            Click(h.Pane.InsertButton);

            Assert.Equal("alpha\nbeta\n\nGood text\ngamma", h.Editor.Document.Text);
            Assert.Equal("Good text", h.Editor.SelectedText);
        });

        // ---- undo of a Replace ---------------------------------------------------------------------------

        [Theory]
        [InlineData("Intro\nbad text here\nOutro", "bad text here", "Better text here.")]
        [InlineData("Do X. Then do Y. Also Z.\nnext", "Do X. Then do Y. Also Z.", "Do X. Then do Y.")]   // the end of its line was cut
        [InlineData("one\ntwo\nand more\nthree", "two\nand more", "two")]                                  // only its last line was cut: the undo types at its end
        [InlineData("one\nintro\ntwo\nthree", "intro\ntwo", "two")]                                        // only its first line was cut: the undo types at its start
        [InlineData("one\r\ntwo\r\nthree\r\nfour", "two\r\nthree", "TWO")]                                 // fewer lines
        [InlineData("one\ntwo\nthree", "two", "two\nand more")]                                            // the original and more
        public Task Undoing_a_Replace_offers_Replace_again(string note, string picked, string reply) => OnUiAsync(async h =>
        {
            Write(h, note, picked);
            h.Model.Reply(reply);
            await h.Window.RunAiAsync(PadAiAction.Improve);
            Click(h.Pane.ReplaceButton);
            string replaced = h.Editor.Document.Text;
            Assert.NotEqual(note, replaced);
            Assert.Equal("Replaced the selection", h.Pane.StatusText.Text);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);

            h.Editor.Undo();                                            // Ctrl+Z

            Assert.Equal(note, h.Editor.Document.Text);
            Assert.True(h.Pane.ReplaceButton.IsEnabled);
            Assert.Equal("", h.Pane.StatusText.Text);

            Click(h.Pane.ReplaceButton);                                // and it replaces the same text again
            Assert.Equal(replaced, h.Editor.Document.Text);
            Assert.Equal("Replaced the selection", h.Pane.StatusText.Text);
        });

        [Theory]
        [InlineData("Intro\nbad text here\nOutro", "bad text here", "Better text here.")]
        [InlineData("Do X. Then do Y. Also Z.\nnext", "Do X. Then do Y. Also Z.", "Do X. Then do Y.")]   // the end of its line was cut
        [InlineData("one\ntwo\nand more\nthree", "two\nand more", "two")]                                  // only its last line was cut
        [InlineData("one\nintro\ntwo\nthree", "intro\ntwo", "two")]                                        // only its first line was cut
        [InlineData("one\r\ntwo\r\nthree\r\nfour", "two\r\nthree", "TWO")]                                 // fewer lines
        [InlineData("one\ntwo\nthree", "two", "two\nand more")]                                            // the original and more
        public Task Redoing_a_Replace_says_Replaced_again_and_not_that_the_text_changed(string note, string picked, string reply) => OnUiAsync(async h =>
        {
            Write(h, note, picked);
            h.Model.Reply(reply);
            await h.Window.RunAiAsync(PadAiAction.Improve);
            Click(h.Pane.ReplaceButton);
            string replaced = h.Editor.Document.Text;

            h.Editor.Undo();                                            // Ctrl+Z
            Assert.Equal(note, h.Editor.Document.Text);
            h.Editor.Redo();                                            // Ctrl+Y: the result is in the note again

            Assert.Equal(replaced, h.Editor.Document.Text);
            Assert.Equal("Replaced the selection", h.Pane.StatusText.Text);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);               // there is nothing left to replace
            Click(h.Pane.ReplaceButton);
            Assert.Equal(replaced, h.Editor.Document.Text);

            h.Editor.Undo();                                            // and back once more: it is offered again
            Assert.Equal(note, h.Editor.Document.Text);
            Assert.True(h.Pane.ReplaceButton.IsEnabled);
            Assert.Equal("", h.Pane.StatusText.Text);

            h.Editor.Redo();
            Click(h.Pane.InsertButton);                                 // after a redo, Insert below lands under the result
            string newline = note.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            string written = reply.Replace("\n", newline, StringComparison.Ordinal);
            int under = replaced.IndexOf(written, StringComparison.Ordinal) + written.Length;
            Assert.Equal(replaced.Insert(under, newline + newline + written), h.Editor.Document.Text);
        });

        [Fact]
        public Task Insert_below_never_turns_Replace_off_and_its_status_goes_with_the_next_redraw() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Model.Reply("Good text");
            await h.Window.RunAiAsync(PadAiAction.Improve);

            Click(h.Pane.InsertButton);

            Assert.Equal("Intro\nbad text here\n\nGood text\nOutro", h.Editor.Document.Text);
            Assert.Equal("Inserted below", h.Pane.StatusText.Text);
            Assert.True(h.Pane.ReplaceButton.IsEnabled);                // the selection is still the text that was sent

            h.Editor.Undo();                                            // the paragraph is taken back

            Assert.Equal(Note, h.Editor.Document.Text);
            Assert.True(h.Pane.ReplaceButton.IsEnabled);                // Replace is offered, as before the insert
            Assert.Equal("", h.Pane.StatusText.Text);                   // and nothing says "Inserted below" any more

            Click(h.Pane.ReplaceButton);
            Assert.Equal("Intro\nGood text\nOutro", h.Editor.Document.Text);
            Assert.Equal("Replaced the selection", h.Pane.StatusText.Text);
        });

        [Fact]
        public Task Insert_below_keeps_Replace_governed_by_the_usual_rules() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Model.Reply("Good text");
            await h.Window.RunAiAsync(PadAiAction.Improve);
            h.Editor.Document.Insert(Note.IndexOf("text", StringComparison.Ordinal), "x");   // the source changed: Replace is off
            Assert.Equal(TextChanged, h.Pane.StatusText.Text);

            Click(h.Pane.InsertButton);

            Assert.Equal("Inserted below", h.Pane.StatusText.Text);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);               // and an insert does not turn it on either
            h.Window.NewTab();                                          // any redraw: the status is the session's own again
            h.Window.SelectTab(0);
            Assert.Equal(TextChanged, h.Pane.StatusText.Text);
        });

        [Fact]
        public Task Text_typed_next_to_a_replaced_selection_does_not_look_like_an_undo() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Model.Reply("Better text here.");
            await h.Window.RunAiAsync(PadAiAction.Improve);
            Click(h.Pane.ReplaceButton);

            h.Editor.Document.Insert(0, "A new first line\n");          // an edit elsewhere

            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Assert.Equal("Replaced the selection", h.Pane.StatusText.Text);
        });

        // ---- nothing thrown into typing or closing -------------------------------------------------------

        [Fact]
        public Task A_failure_while_drawing_the_pane_never_escapes_into_typing_or_switching_tabs() => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            h.Model.Reply("Good text");
            await h.Window.RunAiAsync(PadAiAction.Improve);
            var warned = new List<string>();
            h.Window.Warn = warned.Add;
            h.Window.AiDraw = _ => throw new InvalidOperationException("the pane broke");

            h.Editor.Document.Insert(Note.IndexOf("text", StringComparison.Ordinal), "x");   // typing in the source redraws the pane
            int afterTyping = warned.Count;
            h.Window.PreviewPanel.Visibility = Visibility.Visible;                            // so does the history preview
            h.Window.PreviewPanel.Visibility = Visibility.Collapsed;
            int afterPreview = warned.Count;
            h.Window.NewTab();                                                                // and another tab shown
            h.Window.SelectTab(0);

            Assert.Equal("Intro\nbad xtext here\nOutro", h.Editor.Document.Text);
            Assert.True(afterTyping >= 1, "typing");
            Assert.True(afterPreview >= afterTyping + 2, "the preview");
            Assert.True(warned.Count >= afterPreview + 2, "switching tabs");
            Assert.All(warned, line =>
            {
                Assert.Contains("InvalidOperationException", line, StringComparison.Ordinal);
                Assert.DoesNotContain("the pane broke", line, StringComparison.Ordinal);   // the type only
            });
        });

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public Task A_cancel_that_throws_never_escapes_from_hiding_or_closing_the_window(bool hide) => OnUiAsync(async h =>
        {
            Write(h, Note, Picked);
            var model = new GatedModel("Good ", "text") { ThrowOnCancel = true };
            h.Client = model;
            Task run = h.Window.RunAiAsync(PadAiAction.Improve);
            await Reached(model);
            var warned = new List<string>();
            h.Window.Warn = warned.Add;

            if (hide) h.Window.CloseByUser();                           // the last window hides: the OnClosing path
            else h.Window.CloseForExit();
            await run;

            Assert.True(model.Cancelled);
            Assert.Contains("AggregateException", Assert.Single(warned), StringComparison.Ordinal);
        });

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public Task A_cancel_that_throws_in_an_answer_from_notes_never_escapes_from_hiding_or_closing_the_window(bool hide) => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);
            var model = new GatedModel("Use the", " office wifi") { ThrowOnCancel = true };
            h.Client = model;
            Task ask = AskNotes(h, search, "vpn");
            await Reached(model);
            var warned = new List<string>();
            h.Window.Warn = warned.Add;

            if (hide) h.Window.CloseByUser();
            else h.Window.CloseForExit();
            await ask;

            Assert.True(model.Cancelled);
            Assert.Contains(warned, line => line.Contains("AggregateException", StringComparison.Ordinal));
        });

        // ---- a selection that cuts a credential marker -----------------------------------------------------

        [Theory]
        [InlineData("M9XD}} now", "[[CREDENTIAL_1]] now", "login ok {{secret:K7Q2M9XD}}")]              // starts inside a marker
        [InlineData("login {{secret:K7Q2", "login [[CREDENTIAL_1]]", "ok {{secret:K7Q2M9XD}} now")]    // ends inside one
        [InlineData("ret:K7Q2M9", "[[CREDENTIAL_1]]", "login ok {{secret:K7Q2M9XD}} now")]             // lies inside one
        public Task A_selection_that_cuts_a_credential_marker_takes_the_whole_marker(string picked, string sent, string after) => OnUiAsync(async h =>
        {
            Write(h, "login {{secret:K7Q2M9XD}} now", picked);
            h.Model.Reply("ok [[CREDENTIAL_1]]");

            await h.Window.RunAiAsync(PadAiAction.Improve);

            string message = Assert.Single(h.Model.Requests).Messages[1].Text;
            Assert.Equal(PadAiPrompts.ForAction(PadAiAction.Improve.Instruction, sent), message);
            foreach (string part in new[] { "K7Q2", "M9XD", "secret:", "{{", "}}" })
                Assert.DoesNotContain(part, message, StringComparison.Ordinal);      // no part of the id

            Click(h.Pane.ReplaceButton);                                // the marker is replaced whole and comes back whole
            Assert.Equal(after, h.Editor.Document.Text);
        });

        // ---- storing a credential takes its plain value out of what AI holds ------------------------------

        /// <summary>Stores the selected text as a credential, as the editor menu's Store as credential and its card do.</summary>
        private static string Store(Harness h, MicaPadWindow window, string value)
        {
            string text = window.Editor.Document.Text;
            window.Editor.Select(text.IndexOf(value, StringComparison.Ordinal), value.Length);
            window.StoreSelection();
            Click(window.VaultCard.PrimaryButton);
            return Assert.Single(h.Env.Vault.Credentials).Id;
        }

        private static void NewVault(Harness h)
        {
            h.Env.Vault.Load();
            h.Env.Vault.Create("246810");
        }

        [Fact]
        public Task Storing_a_credential_closes_the_AI_pane_of_that_note_and_leaves_no_session() => OnUiAsync(async h =>
        {
            NewVault(h);
            const string text = "the login is hunter2 today";
            Write(h, text, text);
            h.Model.Reply("Today the login is hunter2.");
            await h.Window.RunAiAsync(PadAiAction.Improve);
            h.Pane.ChangesToggle.IsChecked = true;                // the Changes view holds the value twice
            Assert.NotEmpty(h.Pane.ChangesList.Items);

            string id = Store(h, h.Window, "hunter2");

            Assert.Equal("the login is " + SecretTokens.Format(id) + " today", h.Editor.Document.Text);
            Assert.Equal(Visibility.Collapsed, h.Pane.Visibility);
            Assert.Null(h.Window.AiSessionNow);
            Assert.Equal("", h.Pane.ResultBox.Shown);             // nothing of the request is left in the pane
            Assert.Empty(h.Pane.ChangesList.Items);
            Click(h.Pane.InsertButton);                           // forced clicks: there is nothing to put back
            Click(h.Pane.ReplaceButton);
            Click(h.Pane.CopyButton);
            Assert.DoesNotContain("hunter2", h.Editor.Document.Text, StringComparison.Ordinal);
            Assert.Empty(h.Copied);
        });

        [Fact]
        public Task Storing_a_credential_while_the_reply_streams_cancels_the_request() => OnUiAsync(async h =>
        {
            NewVault(h);
            const string text = "the login is hunter2 today";
            Write(h, text, text);
            var model = new GatedModel("Today the login is hunter2", ".");
            h.Client = model;
            Task run = h.Window.RunAiAsync(PadAiAction.Improve);
            await Reached(model);

            Store(h, h.Window, "hunter2");
            await run;

            Assert.True(model.Cancelled);
            Assert.Equal(Visibility.Collapsed, h.Pane.Visibility);
            Assert.Null(h.Window.AiSessionNow);
            Assert.Equal("", h.Pane.ResultBox.Shown);             // and its end drew nothing back
        });

        [Fact]
        public Task Storing_a_credential_in_another_note_leaves_the_AI_pane_alone() => OnUiAsync(async h =>
        {
            NewVault(h);
            Write(h, Note, Picked);
            h.Model.Reply("Good text");
            await h.Window.RunAiAsync(PadAiAction.Improve);
            AiSession done = h.Window.AiSessionNow!;

            h.Window.NewTab();
            Write(h, "the login is hunter2 today");
            Store(h, h.Window, "hunter2");

            Assert.Equal(Visibility.Visible, h.Pane.Visibility);
            Assert.Same(done, h.Window.AiSessionNow);
            Assert.Equal("Good text", h.Pane.ResultBox.Shown);
        });

        /// <summary>
        /// Runs a test over the harness's window and a second window of the same workspace, which
        /// has one note of its own and the same fakes. Neither is ever shown.
        /// </summary>
        private static Task OnUiWithTwoWindows(Func<Harness, MicaPadWindow, Task> test) => OnUiAsync(async h =>
        {
            PadWindowState state = h.Env.Workspace.NewWindow(h.Window.WindowId);
            h.Env.Workspace.NewNote(state.Id);
            var second = new MicaPadWindow(h.Env.Workspace, new AppConfig(), state.Id)
            {
                AiEnabled = () => h.AiOn,
                AiRunnerFactory = h.Window.AiRunnerFactory,
                AiCopy = h.Copied.Add,
                AiLog = h.Log.Add,
            };
            try
            {
                second.LoadSession();
                await test(h, second);
            }
            finally
            {
                foreach (MicaPadWindow open in MicaPadWindow.WindowsOf(h.Env.Workspace).Where(w => !ReferenceEquals(w, h.Window)).ToList())
                    open.CloseForExit();
                second.CloseForExit();
            }
        });

        /// <summary>
        /// Moves a tab to another window, which would bring that window to the front: showing is
        /// replaced for the move alone (the UI tests of other classes share this thread and the static).
        /// </summary>
        private static void Move(MicaPadWindow from, OpenNote note, MicaPadWindow to)
        {
            Action<MicaPadWindow> show = MicaPadWindow.ShowWindow;
            MicaPadWindow.ShowWindow = _ => { };
            try
            {
                from.MoveToWindow(note, to);
            }
            finally
            {
                MicaPadWindow.ShowWindow = show;
            }
        }

        // ---- review focus 4, across windows: the source tab moves away while the reply streams -------------

        [Fact]
        public Task A_tab_moved_to_another_window_while_the_reply_streams_is_edited_nowhere_and_the_result_applies_once_it_is_back() => OnUiWithTwoWindows(async (h, second) =>
        {
            Write(h, Note, Picked);
            OpenNote source = h.Env.Workspace.ActiveIn(h.Window.WindowId)!;
            h.Window.NewTab();                                    // the first window keeps this tab when the source leaves
            OpenNote stays = h.Env.Workspace.ActiveIn(h.Window.WindowId)!;
            Write(h, "the note that stays, longer than the source so its offsets are all there");
            h.Window.SelectTab(0);
            h.Editor.Select(Note.IndexOf(Picked, StringComparison.Ordinal), Picked.Length);
            OpenNote others = h.Env.Workspace.ActiveIn(second.WindowId)!;
            second.Editor.Document.Text = "the second window's own note, also longer than the source text";
            var model = new GatedModel("Good ", "text");
            h.Client = model;
            Task run = h.Window.RunAiAsync(PadAiAction.Improve);
            await Reached(model);

            Move(h.Window, source, second);                       // mid-stream: the source is now a tab of the second window
            Assert.Same(stays, h.Env.Workspace.ActiveIn(h.Window.WindowId));
            model.Gate.SetResult();
            await run;

            // The request went on and its result is in the first window's pane, where nothing can be applied.
            Assert.False(model.Cancelled);
            Assert.Equal("Good text", h.Pane.ResultBox.Shown);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Assert.False(h.Pane.InsertButton.IsEnabled);
            Assert.False(h.Pane.RetryButton.IsEnabled);
            Assert.Equal(NotShown, h.Pane.StatusText.Text);
            Click(h.Pane.ReplaceButton);                          // even forced clicks edit no note, here or there
            Click(h.Pane.InsertButton);
            Click(h.Pane.RetryButton);
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.Equal(Note, source.TextProvider());
            Assert.Equal("the note that stays, longer than the source so its offsets are all there", stays.TextProvider());
            Assert.Equal("the second window's own note, also longer than the source text", others.TextProvider());
            Assert.Single(model.Sent);
            Assert.Equal(Visibility.Collapsed, second.AiPanel.Visibility);   // the other window has no pane for it
            Assert.Null(second.AiSessionNow);

            Move(second, source, h.Window);                       // moved back: the source is shown where its request was made

            Assert.Same(source, h.Env.Workspace.ActiveIn(h.Window.WindowId));
            Assert.True(h.Pane.ReplaceButton.IsEnabled);
            Assert.Equal("", h.Pane.StatusText.Text);
            Click(h.Pane.ReplaceButton);
            Assert.Equal("Intro\nGood text\nOutro", source.TextProvider());
            Assert.Equal("the note that stays, longer than the source so its offsets are all there", stays.TextProvider());
            Assert.Equal("the second window's own note, also longer than the source text", others.TextProvider());
        });

        [Fact]
        public Task Storing_a_credential_closes_the_AI_pane_of_the_window_the_note_was_moved_from() => OnUiWithTwoWindows(async (h, second) =>
        {
            NewVault(h);
            const string text = "the login is hunter2 today";
            Write(h, text, text);
            OpenNote source = h.Env.Workspace.ActiveIn(h.Window.WindowId)!;
            h.Model.Reply("Today the login is hunter2.");
            await h.Window.RunAiAsync(PadAiAction.Improve);
            h.Window.NewTab();                                    // the window keeps a tab when the source leaves
            h.Window.SelectTab(0);
            Assert.Same(source, h.Env.Workspace.ActiveIn(h.Window.WindowId));

            Move(h.Window, source, second);                       // the pane stays in the first window, with its result
            Assert.Equal(Visibility.Visible, h.Pane.Visibility);
            Assert.Equal("Today the login is hunter2.", h.Pane.ResultBox.Shown);

            Store(h, second, "hunter2");                          // stored where the note is now

            Assert.DoesNotContain("hunter2", source.TextProvider(), StringComparison.Ordinal);
            Assert.Equal(Visibility.Collapsed, h.Pane.Visibility);
            Assert.Null(h.Window.AiSessionNow);
            Assert.Equal("", h.Pane.ResultBox.Shown);
        });

        [Theory]
        [InlineData(false)]   // the answer is on screen
        [InlineData(true)]    // it still streams in
        public Task Storing_a_credential_clears_an_answer_from_notes(bool streaming) => OnUiWithSearch(async (h, search) =>
        {
            NewVault(h);
            await Index(h, search, "# Bank\nthe vpn login is hunter2 today");
            var model = new GatedModel("The login is hunter2", " [1].");
            if (!streaming) model.Gate.SetResult();
            h.Client = model;
            Task ask = AskNotes(h, search, "vpn");
            if (streaming) await Reached(model);
            else await ask;
            SearchPane pane = h.Window.SearchPanel;
            Assert.StartsWith("The login is hunter2", pane.AnswerBox.Shown, StringComparison.Ordinal);
            Assert.StartsWith("Words · Answer", pane.StatusText.Text, StringComparison.Ordinal);

            Store(h, h.Window, "hunter2");
            await ask;

            Assert.Equal(streaming, model.Cancelled);
            Assert.Equal(Visibility.Collapsed, pane.AnswerPanel.Visibility);
            Assert.Equal("", pane.AnswerBox.Shown);
            Assert.Equal("", pane.AnswerNote.Text);               // no "Stopped" either: the answer is gone, not ended
            Assert.Equal("Words", pane.StatusText.Text);          // nor a status that says there is one
            Click(pane.AnswerCopy);                               // even a forced click copies nothing
            Assert.Empty(h.Copied);
        });

        // ---- ask your notes: the Search pane's Ask (spec 4) ----------------------------------------

        /// <summary>A note of two sections; only the first is about the vpn.</summary>
        private const string VpnNote = "# Net\nthe vpn needs the office wifi\n\n# Food\nlunch is noodles";

        /// <summary>Runs a test over a window and a words-only search of its store.</summary>
        private static Task OnUiWithSearch(Func<Harness, NoteSearchService, Task> test) => OnUiAsync(async h =>
        {
            using var search = new NoteSearchService(h.Env.Store, () => SearchSettings.Off, warn: _ => { });
            await test(h, search);
        });

        /// <summary>Writes the shown note and indexes it, as the feeder does after an edit.</summary>
        private static async Task Index(Harness h, NoteSearchService search, string text)
        {
            Write(h, text);
            OpenNote note = h.Shown;
            search.Indexer.SetNote(note.Id, note.Title, text, DateTime.UtcNow);
            await search.Indexer.WhenIdle();
        }

        /// <summary>
        /// Runs <paramref name="call"/> with <paramref name="search"/> as MicaPad's search and
        /// <paramref name="feeder"/> as its feeder. The window reads those statics before its
        /// first await, so they are swapped in for the call only: the UI tests of other classes
        /// share this thread and the statics.
        /// </summary>
        private static T WithSearch<T>(NoteSearchService? search, Func<T> call, SearchFeeder? feeder = null)
        {
            NoteSearchService? original = MicaPadWindow.SearchService;
            SearchFeeder? originalFeeder = MicaPadWindow.SearchFeeder;
            MicaPadWindow.SearchService = search;
            MicaPadWindow.SearchFeeder = feeder;
            try
            {
                return call();
            }
            finally
            {
                MicaPadWindow.SearchService = original;
                MicaPadWindow.SearchFeeder = originalFeeder;
            }
        }

        /// <summary>Opens Search notes, types the question and asks, as Ctrl+Enter does. The task ends when the answer does.</summary>
        private static Task AskNotes(Harness h, NoteSearchService search, string question, SearchFeeder? feeder = null) => WithSearch(search, () =>
        {
            SearchPane pane = h.Window.SearchPanel;
            if (pane.Visibility != Visibility.Visible) h.Window.ToggleSearch();
            pane.QueryBox.Text = question;
            return pane.AskNowAsync();
        }, feeder);

        /// <summary>The passages a question is answered from: what the search finds, at most eight.</summary>
        private static async Task<IReadOnlyList<Passage>> SourcesOf(NoteSearchService search, string question) =>
            NotesQuestion.Sources((await search.Search.SearchAsync(question, CancellationToken.None)).Hits);

        [Fact]
        public Task With_AI_off_Ask_searches_as_usual_says_how_to_turn_AI_on_and_sends_nothing() => OnUiWithSearch(async (h, search) =>
        {
            h.AiOn = false;
            await Index(h, search, VpnNote);

            await AskNotes(h, search, "vpn");

            SearchPane pane = h.Window.SearchPanel;
            Assert.Null(Assert.Single(pane.Rows).Source);         // the normal search ran
            Assert.Equal("Words", pane.StatusText.Text);
            Assert.Equal(Visibility.Visible, pane.AnswerPanel.Visibility);
            Assert.Equal(NotesQuestion.AiOff, pane.AnswerBox.Shown);   // the user is told why
            Assert.Empty(h.Model.Requests);
            Assert.Equal(0, h.Usage.UsedToday);
            Assert.Equal(0, h.RunnersBuilt);
            Assert.Empty(h.Log);
        });

        [Fact]
        public Task With_AI_turned_off_between_the_search_and_the_request_Ask_says_so_and_sends_nothing() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);
            int asked = 0;
            h.Window.AiEnabled = () => asked++ == 0;              // on at the click, off once the search is back

            await AskNotes(h, search, "vpn");

            SearchPane pane = h.Window.SearchPanel;
            Assert.Equal(2, asked);                               // asked again before the request
            Assert.Equal(NotesQuestion.AiOff, pane.AnswerBox.Shown);
            Assert.Null(Assert.Single(pane.Rows).Source);         // no answer, so no source numbers
            Assert.Equal("Words", pane.StatusText.Text);
            Assert.Empty(h.Model.Requests);
            Assert.Equal(0, h.Usage.UsedToday);
            Assert.Equal(0, h.RunnersBuilt);
            Assert.Equal(new[] { "AI ask-notes: 1 sources, 0 chars in the request, AI off" }, h.Log);
        });

        [Fact]
        public Task With_AI_turned_off_before_the_answer_is_read_the_request_is_never_made() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);
            AskStart start = await WithSearch(search, () => h.Window.SearchPanel.Ask!("vpn", CancellationToken.None));
            Assert.NotNull(start.Answer);                         // AI was on up to here: nothing is sent before the answer is read
            Assert.Null(start.Instead);
            Assert.Equal("Words", start.Status);
            Assert.Equal("Words · Answering from 1 passage", start.Answering);
            Assert.Equal("Words · Answered from 1 passage", start.Answered);
            Assert.Empty(h.Model.Requests);

            h.AiOn = false;
            var updates = new List<PadAiUpdate>();
            await foreach (PadAiUpdate update in start.Answer!) updates.Add(update);

            Assert.Equal(new[] { new PadAiUpdate(PadAiUpdateKind.Error, NotesQuestion.AiOff), new PadAiUpdate(PadAiUpdateKind.Done) }, updates);
            Assert.Empty(h.Model.Requests);
            Assert.Equal(0, h.Usage.UsedToday);
            Assert.Equal(new[] { "AI ask-notes: 1 sources, 0 chars in the request, AI off" }, h.Log);
        });

        [Fact]
        public Task A_setting_that_cannot_be_read_counts_as_off_for_Ask_too() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);
            var warned = new List<string>();
            h.Window.Warn = warned.Add;
            h.Window.AiEnabled = () => throw new InvalidOperationException("no settings");

            await AskNotes(h, search, "vpn");

            Assert.Equal(NotesQuestion.AiOff, h.Window.SearchPanel.AnswerBox.Shown);
            Assert.Single(h.Window.SearchPanel.Rows);
            Assert.Empty(h.Model.Requests);
            Assert.Equal(0, h.RunnersBuilt);
            Assert.All(warned, line => Assert.DoesNotContain("no settings", line, StringComparison.Ordinal));
        });

        [Fact]
        public Task Ask_sends_the_question_and_the_numbered_passages_and_shows_the_answer() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);
            h.Model.Reply("Use the **office wifi** [1].");

            await AskNotes(h, search, "vpn");

            IReadOnlyList<Passage> sources = await SourcesOf(search, "vpn");
            Assert.Single(sources);
            ScriptedChatClient.Request request = Assert.Single(h.Model.Requests);
            Assert.Equal(PadAiPrompts.System, request.Messages[0].Text);
            Assert.Equal(NotesQuestion.Message("vpn", sources), request.Messages[1].Text);
            Assert.Contains("[1]", request.Messages[1].Text, StringComparison.Ordinal);
            Assert.Contains("the vpn needs the office wifi", request.Messages[1].Text, StringComparison.Ordinal);
            Assert.DoesNotContain("noodles", request.Messages[1].Text, StringComparison.Ordinal);   // only what the search found

            SearchPane pane = h.Window.SearchPanel;
            Assert.Equal(Visibility.Visible, pane.AnswerPanel.Visibility);
            Assert.Equal("Use the **office wifi** [1].", pane.AnswerBox.Shown);
            Assert.Equal(Visibility.Collapsed, pane.AnswerStop.Visibility);
            Assert.Equal(Visibility.Collapsed, pane.AnswerNote.Visibility);
            Assert.Equal(1, Assert.Single(pane.Rows).Source);
            Assert.Equal("Words · Answered from 1 passage", pane.StatusText.Text);
            Assert.Equal(1, h.Usage.UsedToday);                   // one answer counts one against the daily limit
            Assert.Equal(VpnNote, h.Editor.Document.Text);        // an answer never touches a note
        });

        [Fact]
        public Task A_credential_in_a_note_and_in_the_question_reaches_the_model_as_credential() => OnUiWithSearch(async (h, search) =>
        {
            h.Env.Vault.Load();
            h.Env.Vault.Create("246810");
            string id = h.Env.Vault.Add("hunter2", "Bank", null);
            string pill = SecretTokens.Format(id);
            await Index(h, search, "# Bank\nthe bank login is " + pill + " on the vpn");
            h.Model.Reply("It is a stored credential [1].");

            await AskNotes(h, search, "what is the bank login " + pill);

            ScriptedChatClient.Request request = Assert.Single(h.Model.Requests);
            string sent = request.Messages[1].Text;
            Assert.StartsWith("Question: what is the bank login [credential]\n", sent, StringComparison.Ordinal);
            Assert.Contains("the bank login is [credential] on the vpn", sent, StringComparison.Ordinal);
            string everything = string.Join("\n", request.Messages.Select(m => m.Text).Concat(h.Log));
            Assert.DoesNotContain("{{secret:", everything, StringComparison.Ordinal);
            Assert.DoesNotContain(id, everything, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", everything, StringComparison.Ordinal);
        });

        [Fact]
        public Task A_credential_cut_short_by_a_notes_automatic_title_reaches_the_model_as_credential() => OnUiWithSearch(async (h, search) =>
        {
            // The feeder titles a scratch note by the first 30 characters of its first line: here they end inside the reference.
            Write(h, "db password is {{secret:K7Q2M9XD}}\nit opens the vpn");
            using var feeder = new SearchFeeder(h.Env.Workspace, search.Indexer);
            await search.Indexer.WhenIdle();
            h.Model.Reply("It is stored [1].");

            await AskNotes(h, search, "vpn", feeder);

            string sent = Assert.Single(h.Model.Requests).Messages[1].Text;
            Assert.Contains("[1] db password is [credential] (lines 1–2)", sent, StringComparison.Ordinal);
            foreach (string part in new[] { "{{secret", "secret:", "K7Q2", "Q2M9", "M9XD" })
                Assert.DoesNotContain(part, sent, StringComparison.Ordinal);   // no part of the id, in the header either
        });

        /// <summary>A reranker that keeps the order it is given and records every query it is sent.</summary>
        private sealed class RecordingReranker : IReranker
        {
            public List<string> Queries { get; } = new();

            public Task<RerankResult> RerankAsync(SearchServer server, string query, IReadOnlyList<string> documents, int topN,
                                                  TimeSpan timeout, CancellationToken cancel)
            {
                lock (Queries) Queries.Add(query);
                var ranked = Enumerable.Range(0, documents.Count).Select(index => new RerankScore(index, 1.0 / (index + 1))).ToList();
                return Task.FromResult(new RerankResult(ranked, SearchFailure.None, 200));
            }
        }

        [Theory]
        [InlineData("vpn login {{secret:K7Q2")]     // ends inside the reference
        [InlineData("M9XD}} works on the vpn")]     // starts inside it
        public Task A_selection_that_cuts_a_credential_becomes_a_query_that_reaches_every_server_as_credential(string picked) => OnUiAsync(async h =>
        {
            var embedder = new SearchIndexerTests.FakeEmbedder();
            var reranker = new RecordingReranker();
            var settings = new SearchSettings(true, new SearchServer("http://gpu/v1", "m", null), true, new SearchServer("http://gpu/v1", "r", null));
            using var search = new NoteSearchService(h.Env.Store, () => settings, embedder, reranker, warn: _ => { });
            const string text = "the vpn login {{secret:K7Q2M9XD}} works on the vpn";
            await Index(h, search, text);
            h.Editor.Select(text.IndexOf(picked, StringComparison.Ordinal), picked.Length);
            h.Model.Reply("It is stored [1].");

            // Ctrl+Shift+F makes the one-line selection the query; Ask then answers it.
            await WithSearch(search, () =>
            {
                h.Window.ToggleSearch();
                return h.Window.SearchPanel.AskNowAsync();
            });

            string query = h.Window.SearchPanel.QueryBox.Text;
            Assert.Contains("{{secret:K7Q2M9XD}}", query, StringComparison.Ordinal);   // widened to the whole reference: half of one is never the query
            string cleaned = NotePassages.WithoutSecrets(query);
            Assert.Contains("[credential]", cleaned, StringComparison.Ordinal);

            string question = Assert.Single(h.Model.Requests).Messages[1].Text;
            Assert.StartsWith("Question: " + cleaned + "\n", question, StringComparison.Ordinal);
            List<string> embedded;
            lock (embedder.Batches) embedded = embedder.Batches.SelectMany(batch => batch).ToList();
            Assert.Contains(cleaned, embedded);
            Assert.NotEmpty(reranker.Queries);
            Assert.All(reranker.Queries, q => Assert.Equal(cleaned, q));
            foreach (string sent in embedded.Concat(reranker.Queries).Append(question))
                foreach (string part in new[] { "K7Q2", "M9XD", "{{secret", "}}" })
                    Assert.DoesNotContain(part, sent, StringComparison.Ordinal);       // no part of the id reaches a server
        });

        [Fact]
        public Task With_no_hits_Ask_says_there_is_nothing_to_answer_from_and_makes_no_request() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);

            await AskNotes(h, search, "zebra");

            SearchPane pane = h.Window.SearchPanel;
            Assert.Empty(pane.Rows);
            Assert.Equal(NotesQuestion.NoSources, pane.AnswerBox.Shown);
            Assert.Equal("Words · No notes found", pane.StatusText.Text);
            Assert.Empty(h.Model.Requests);
            Assert.Equal(0, h.Usage.UsedToday);                   // nothing is counted
            Assert.Equal(0, h.RunnersBuilt);
            Assert.Equal(new[] { "AI ask-notes: 0 sources, 0 chars in the request, no sources" }, h.Log);
        });

        [Fact]
        public Task While_search_is_not_ready_Ask_says_so_and_makes_no_request() => OnUiAsync(async h =>
        {
            Write(h, VpnNote);

            AskStart start = await WithSearch(null, () => h.Window.SearchPanel.Ask!("vpn", CancellationToken.None));

            Assert.Empty(start.Rows);
            Assert.Equal("Search is not ready yet.", start.Status);
            Assert.Null(start.Answer);
            Assert.Equal("Search is not ready yet.", start.Instead);   // not "nothing matches": nothing was searched
            Assert.Equal(0, h.RunnersBuilt);
            Assert.Empty(h.Model.Requests);
        });

        [Fact]
        public Task Text_removed_from_the_open_note_right_before_Ask_is_in_no_request() => OnUiWithSearch(async (h, search) =>
        {
            Write(h, "# Net\nthe vpn needs the office wifi\nthe vpn door code is 4471");
            using var feeder = new SearchFeeder(h.Env.Workspace, search.Indexer);   // indexes the open notes, then each edit 2 s after it
            await search.Indexer.WhenIdle();
            h.Window.ToggleSearch();                              // the pane is open already: opening it is not what brings the index up to date
            h.Model.Reply("Use the office wifi [1].");

            h.Editor.Document.Text = "# Net\nthe vpn needs the office wifi";   // deleted a moment ago: the index still holds the code
            await AskNotes(h, search, "vpn", feeder);

            string sent = Assert.Single(h.Model.Requests).Messages[1].Text;
            Assert.Contains("the vpn needs the office wifi", sent, StringComparison.Ordinal);
            Assert.DoesNotContain("4471", sent, StringComparison.Ordinal);
            Assert.DoesNotContain("door code", sent, StringComparison.Ordinal);
        });

        [Fact]
        public Task The_status_says_Answering_while_the_answer_streams_and_Answered_only_after_a_clean_end() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);
            var model = new GatedModel("Use the", " office wifi [1].");
            h.Client = model;
            Task ask = AskNotes(h, search, "vpn");
            await Reached(model);
            SearchPane pane = h.Window.SearchPanel;

            Assert.Equal("Words · Answering from 1 passage", pane.StatusText.Text);
            Assert.Equal(1, Assert.Single(pane.Rows).Source);     // the sources are numbered from the start

            model.Gate.SetResult();
            await ask;

            Assert.Equal("Words · Answered from 1 passage", pane.StatusText.Text);
            Assert.Equal("Use the office wifi [1].", pane.AnswerBox.Shown);
        });

        [Fact]
        public Task A_held_Ctrl_Enter_and_a_second_Ask_for_the_question_being_answered_send_one_request_and_count_one_use() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);
            var model = new GatedModel("Use the", " office wifi [1].");
            h.Client = model;
            SearchPane pane = h.Window.SearchPanel;
            h.Window.ToggleSearch();
            pane.QueryBox.Text = "vpn";

            // The window reads its search when a question starts, so each step that could start one runs with it in place.
            Assert.True(WithSearch(search, () => pane.HandleQueryKey(Key.Enter, ModifierKeys.Control)));   // Ctrl+Enter goes down
            await Reached(model);                                 // the request is out and its answer streams in
            await WithSearch(search, () =>
            {
                for (int i = 0; i < 5; i++)
                    Assert.True(pane.HandleQueryKey(Key.Enter, ModifierKeys.Control, repeat: true));       // the key is still held: five repeats
                Click(pane.AskButton);                            // then a double click on Ask
                Click(pane.AskButton);
                return pane.AskNowAsync();
            });
            model.Gate.SetResult();
            await Until(() => pane.AnswerCopy.Visibility == Visibility.Visible, "the answer to end");

            Assert.Single(model.Sent);                            // one request
            Assert.Equal(1, h.Usage.UsedToday);                   // one use of the daily limit
            Assert.Equal(1, h.RunnersBuilt);
            Assert.False(model.Cancelled);
            Assert.Equal("Use the office wifi [1].", pane.AnswerBox.Shown);
            Assert.Equal("Words · Answered from 1 passage", pane.StatusText.Text);
        });

        [Fact]
        public Task The_first_eight_rows_carry_their_source_numbers_and_the_status_ends_with_the_count() => OnUiWithSearch(async (h, search) =>
        {
            // Four notes of three passages each: twelve hits, of which the first eight are sources.
            for (int n = 1; n <= 4; n++)
            {
                if (n > 1) h.Window.NewTab();
                await Index(h, search, string.Join("\n\n", Enumerable.Range(1, 3).Select(p => "# Part " + Count(n) + "." + Count(p) + "\nthe vpn fact " + Count(n) + "." + Count(p))));
            }
            h.Model.Reply("See [1] and [8].");

            await AskNotes(h, search, "vpn");

            SearchPane pane = h.Window.SearchPanel;
            Assert.Equal(new int?[] { 1, 2, 3, 4, 5, 6, 7, 8, null, null, null, null }, pane.Rows.Select(r => r.Source).ToArray());
            Assert.Equal("Words · Answered from 8 passages", pane.StatusText.Text);
            Assert.EndsWith(NotesQuestion.Status(8), pane.StatusText.Text, StringComparison.Ordinal);

            IReadOnlyList<Passage> sources = await SourcesOf(search, "vpn");
            Assert.Equal(8, sources.Count);
            Assert.Equal(NotesQuestion.Message("vpn", sources), h.Sent());
            Assert.Contains("\n\n[8] ", h.Sent(), StringComparison.Ordinal);
            Assert.DoesNotContain("\n\n[9] ", h.Sent(), StringComparison.Ordinal);
            // Row i is source i + 1: the citation [n] in the answer is the row numbered n.
            Assert.Equal(sources.Select(s => (s.NoteId, s.FirstLine)).ToArray(), pane.Rows.Take(8).Select(r => (r.NoteId, r.FirstLine)).ToArray());
        });

        [Fact]
        public Task A_click_on_a_numbered_row_still_opens_the_note_at_its_passage() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, "one\ntwo\n\n# Part\nneedle in here\nafter");
            OpenNote source = h.Shown;
            h.Window.NewTab();                                    // another note is shown when the row is clicked
            Write(h, "elsewhere");
            h.Model.Reply("It is in the part [1].");
            await AskNotes(h, search, "needle");
            SearchPane pane = h.Window.SearchPanel;
            Assert.Equal(1, Assert.Single(pane.Rows).Source);

            // An unshown window lays nothing out, and a click lands on a row's container: the pane is laid out by hand.
            pane.Measure(new System.Windows.Size(300, 600));
            pane.Arrange(new Rect(0, 0, 300, 600));
            pane.UpdateLayout();
            var item = Assert.IsType<ListBoxItem>(pane.Results.ItemContainerGenerator.ContainerFromIndex(0));
            // The bubbling MouseUp: WPF turns it into MouseLeftButtonUp on each element it passes, the list among them.
            item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });

            Assert.Same(source, h.Shown);
            Assert.Equal("# Part\nneedle in here\nafter", h.Editor.SelectedText.Replace("\r\n", "\n", StringComparison.Ordinal));
            Assert.Equal(Visibility.Visible, pane.AnswerPanel.Visibility);   // the answer stays while its sources are read
            Assert.Equal("It is in the part [1].", pane.AnswerBox.Shown);
        });

        [Fact]
        public Task Before_the_settings_load_Ask_says_AI_is_not_available_yet() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);
            h.Window.AiRunnerFactory = () => null;

            await AskNotes(h, search, "vpn");

            SearchPane pane = h.Window.SearchPanel;
            Assert.Equal("AI is not available yet", pane.AnswerBox.Shown);
            Assert.Null(Assert.Single(pane.Rows).Source);
            Assert.Equal("Words", pane.StatusText.Text);
            Assert.Empty(h.Model.Requests);
            Assert.Equal(new[] { "AI ask-notes: 1 sources, 0 chars in the request, not available" }, h.Log);
        });

        [Fact]
        public Task A_runner_that_cannot_be_built_is_told_in_the_answer_area_and_never_thrown_into_the_window() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);
            var warned = new List<string>();
            h.Window.Warn = warned.Add;
            var broken = new InvalidOperationException("the settings are broken");
            h.Window.AiRunnerFactory = () => throw broken;

            await AskNotes(h, search, "vpn");

            SearchPane pane = h.Window.SearchPanel;
            Assert.Equal(AiErrorText.Describe(broken), pane.AnswerBox.Shown);
            Assert.Null(Assert.Single(pane.Rows).Source);
            Assert.Contains("InvalidOperationException", Assert.Single(warned), StringComparison.Ordinal);
            Assert.DoesNotContain("broken", warned[0], StringComparison.Ordinal);   // the type only
            Assert.Equal(new[] { "AI ask-notes: 1 sources, 0 chars in the request, failed" }, h.Log);
        });

        [Fact]
        public Task At_the_daily_limit_Ask_says_so_and_sends_nothing() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);
            h.Limit = 1;
            Assert.True(h.Usage.TryConsume(1));

            await AskNotes(h, search, "vpn");

            SearchPane pane = h.Window.SearchPanel;
            Assert.Equal(AiAssistant.LimitText(1), pane.AnswerNote.Text);
            Assert.Equal(Visibility.Visible, pane.AnswerNote.Visibility);
            Assert.Equal("", pane.AnswerBox.Shown);
            Assert.Equal("Words", pane.StatusText.Text);          // the status claims no answer
            Assert.Empty(h.Model.Requests);
            Assert.EndsWith(" chars in the request, failed", Assert.Single(h.Log), StringComparison.Ordinal);
        });

        [Fact]
        public Task Without_a_key_Ask_shows_the_providers_problem_and_sends_nothing() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);
            h.Client = null;
            h.Problem = "Add an API key in Settings > AI.";

            await AskNotes(h, search, "vpn");

            SearchPane pane = h.Window.SearchPanel;
            Assert.Equal("Add an API key in Settings > AI.", pane.AnswerNote.Text);
            Assert.Equal(Visibility.Collapsed, pane.AnswerStop.Visibility);
            Assert.Empty(h.Model.Requests);
            Assert.Equal(0, h.Usage.UsedToday);
        });

        [Fact]
        public Task The_Ask_log_line_has_the_sources_the_characters_sent_and_the_outcome_and_never_the_question_or_the_answer() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);
            h.Model.Reply("Use the office wifi [1].");

            await AskNotes(h, search, "vpn password");

            int sent = NotesQuestion.Message("vpn password", await SourcesOf(search, "vpn password")).Length;
            string line = Assert.Single(h.Log);
            Assert.Equal("AI ask-notes: 1 sources, " + Count(sent) + " chars in the request, ok", line);
            Assert.DoesNotContain("password", line, StringComparison.Ordinal);
            Assert.DoesNotContain("wifi", line, StringComparison.Ordinal);
        });

        [Fact]
        public Task Stop_in_the_answer_area_cancels_the_request_and_keeps_the_partial_answer() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);
            var model = new GatedModel("Use the", " office wifi");
            h.Client = model;
            Task ask = AskNotes(h, search, "vpn");
            await Reached(model);
            SearchPane pane = h.Window.SearchPanel;
            Assert.Equal(Visibility.Visible, pane.AnswerStop.Visibility);
            Assert.Equal("Use the", pane.AnswerBox.Shown);

            Click(pane.AnswerStop);
            await ask;

            Assert.True(model.Cancelled);
            Assert.Equal("Use the", pane.AnswerBox.Shown);
            Assert.Equal("Stopped", pane.AnswerNote.Text);
            Assert.Equal("Words", pane.StatusText.Text);          // a stopped answer is not "Answered"
            Assert.Equal(Visibility.Collapsed, pane.AnswerStop.Visibility);
            Assert.EndsWith(" chars in the request, stopped", Assert.Single(h.Log), StringComparison.Ordinal);
        });

        [Fact]
        public Task An_answer_cut_short_at_the_length_limit_says_so() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);
            var model = new GatedModel("Use the", " office wi") { CutShort = true };
            model.Gate.SetResult();
            h.Client = model;

            await AskNotes(h, search, "vpn");

            SearchPane pane = h.Window.SearchPanel;
            Assert.Equal("Use the office wi", pane.AnswerBox.Shown);
            Assert.Equal("Cut short at the length limit", pane.AnswerNote.Text);
            Assert.Equal("Words", pane.StatusText.Text);
            Assert.EndsWith(" chars in the request, cut short", Assert.Single(h.Log), StringComparison.Ordinal);
        });

        [Fact]
        public Task An_answer_the_provider_stopped_with_its_content_filter_is_not_called_answered() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);
            var model = new GatedModel("Use the", " office wi") { Finish = ChatFinishReason.ContentFilter };
            model.Gate.SetResult();
            h.Client = model;

            await AskNotes(h, search, "vpn");

            SearchPane pane = h.Window.SearchPanel;
            Assert.Equal("Use the office wi", pane.AnswerBox.Shown);
            Assert.Equal("The AI provider stopped the reply (content filter).", pane.AnswerNote.Text);
            Assert.Equal(Visibility.Visible, pane.AnswerNote.Visibility);
            Assert.Equal("Words", pane.StatusText.Text);          // not "Answered"
            Assert.EndsWith(" chars in the request, failed", Assert.Single(h.Log), StringComparison.Ordinal);
        });

        [Fact]
        public Task Closing_Search_notes_stops_a_running_answer() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);
            var model = new GatedModel("Use the", " office wifi");
            h.Client = model;
            Task ask = AskNotes(h, search, "vpn");
            await Reached(model);

            h.Window.ToggleSearch();                              // Ctrl+Shift+F closes the pane
            await ask;

            Assert.True(model.Cancelled);                         // nothing runs where nobody sees it
            Assert.Equal(Visibility.Collapsed, h.Window.SearchPanel.Visibility);
        });

        [Fact]
        public Task Opening_the_AI_pane_stops_a_running_answer() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);
            var model = new GatedModel("Use the", " office wifi");
            h.Client = model;
            Task ask = AskNotes(h, search, "vpn");
            await Reached(model);

            h.Window.ToggleAi();                                  // the AI pane takes the column
            await ask;

            Assert.True(model.Cancelled);
            Assert.Equal(Visibility.Collapsed, h.Window.SearchPanel.Visibility);
            Assert.Equal(Visibility.Visible, h.Pane.Visibility);
        });

        [Fact]
        public Task Hiding_the_last_window_with_its_close_button_stops_a_running_answer() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);
            var model = new GatedModel("Use the", " office wifi");
            h.Client = model;
            Task ask = AskNotes(h, search, "vpn");
            await Reached(model);

            h.Window.CloseByUser();
            await ask;

            Assert.True(h.Window.IsHiddenByClose);
            Assert.True(model.Cancelled);                         // nothing runs behind a hidden window
            Assert.Equal("Stopped", h.Window.SearchPanel.AnswerNote.Text);
        });

        [Fact]
        public Task Closing_the_window_cancels_a_running_answer() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);
            var model = new GatedModel("Use the", " office wifi");
            h.Client = model;
            Task ask = AskNotes(h, search, "vpn");
            await Reached(model);

            h.Window.CloseForExit();
            await ask;

            Assert.True(model.Cancelled);
        });

        [Fact]
        public Task Copy_in_the_answer_area_hands_the_raw_answer_to_the_copy_action_and_says_so() => OnUiWithSearch(async (h, search) =>
        {
            await Index(h, search, VpnNote);
            h.Model.Reply("Use the **office wifi** [1].");
            await AskNotes(h, search, "vpn");

            Click(h.Window.SearchPanel.AnswerCopy);

            Assert.Equal(new[] { "Use the **office wifi** [1]." }, h.Copied);
            Assert.Equal("Copied", h.Window.StatusMessage.Text);
            Assert.Equal(Visibility.Visible, h.Window.StatusMessage.Visibility);
        });

        [Fact]
        public Task The_window_leaves_Ctrl_Enter_to_the_Search_pane() => OnUi(h =>
        {
            h.Window.ToggleSearch();

            Assert.False(h.Window.HandleShortcut(Key.Enter, ModifierKeys.Control));   // not a window shortcut: the query box gets it
            Assert.Equal("Answer from your notes (Ctrl+Enter)", h.Window.SearchPanel.AskButton.ToolTip);
        });

        [Fact]
        public Task The_answer_area_follows_the_pads_theme() => OnUi(h =>
        {
            PadAnswerBox box = h.Window.SearchPanel.AnswerBox;
            Assert.Equal(PadThemeApplier.ToColor(AskPalette.Dark.Ink), ((System.Windows.Media.SolidColorBrush)box.Foreground).Color);

            h.Window.ToggleTheme();

            Assert.Equal(PadThemeApplier.ToColor(AskPalette.Light.Ink), ((System.Windows.Media.SolidColorBrush)box.Foreground).Color);
        });

        // ---- diagram help (part 2, spec 2): Draw as diagram --------------------------------------

        private const string DrawnReply = "```mermaid\nflowchart LR\n  login --> pay --> ship\n```";

        /// <summary>The text the AI pane's result box shows, as the user reads it.</summary>
        private static string Rendered(AiPane pane)
        {
            var document = pane.ResultBox.Document;
            return new System.Windows.Documents.TextRange(document.ContentStart, document.ContentEnd).Text
                .TrimEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        [Fact]
        public Task Draw_as_diagram_on_a_selection_sends_its_instruction_shows_the_block_as_plain_text_and_Insert_below_is_one_undo_step() => OnUiAsync(async h =>
        {
            const string note = "Checkout\nlogin, then pay, then ship\nEnd";
            Write(h, note, "login, then pay, then ship");
            h.Model.Reply(DrawnReply);

            await h.Window.RunAiAsync(PadAiAction.Diagram);

            Assert.Equal(PadAiPrompts.ForAction(PadAiAction.Diagram.Instruction, "login, then pay, then ship"), h.Sent());
            Assert.Equal("Draw as diagram", h.Pane.TitleText.Text);
            Assert.Equal("Selection, 26 characters", h.Pane.SourceText.Text);
            Assert.Equal(DrawnReply, h.Pane.ResultBox.Shown);
            Assert.Equal(DrawnReply, Rendered(h.Pane));               // the fenced block as it would be inserted, not drawn as Markdown
            Assert.Equal(Visibility.Visible, h.Pane.ReplaceButton.Visibility);   // offered for a selection, as Ask AI does
            Assert.True(h.Pane.ReplaceButton.IsEnabled);
            Assert.True(h.Pane.InsertButton.IsEnabled);
            Assert.Equal(note, h.Editor.Document.Text);              // nothing changes before a click

            Click(h.Pane.InsertButton);

            Assert.Equal("Checkout\nlogin, then pay, then ship\n\n" + DrawnReply + "\nEnd", h.Editor.Document.Text);
            Assert.Equal(DrawnReply, h.Editor.SelectedText);
            h.Editor.Undo();                                          // one Ctrl+Z
            Assert.Equal(note, h.Editor.Document.Text);
            Assert.StartsWith("AI diagram: ", Assert.Single(h.Log), StringComparison.Ordinal);
        });

        [Fact]
        public Task Draw_as_diagram_with_no_selection_takes_the_whole_note_and_offers_Insert_below_but_not_Replace() => OnUiAsync(async h =>
        {
            Write(h, "login\npay\nship");
            h.Model.Reply(DrawnReply);

            await h.Window.RunAiAsync(PadAiAction.Diagram);

            Assert.Equal(PadAiPrompts.ForAction(PadAiAction.Diagram.Instruction, "login\npay\nship"), h.Sent());
            Assert.Equal("Whole note, 14 characters", h.Pane.SourceText.Text);
            Assert.Equal(Visibility.Collapsed, h.Pane.ReplaceButton.Visibility);
            Assert.True(h.Pane.InsertButton.IsEnabled);

            Click(h.Pane.InsertButton);                               // at the end of the note

            Assert.Equal("login\npay\nship\n\n" + DrawnReply, h.Editor.Document.Text);
        });

        [Fact]
        public Task Draw_as_diagram_in_the_menu_runs_at_once_and_never_asks_for_an_instruction() => OnUiAsync(async h =>
        {
            Write(h, "login\npay\nship");
            h.Model.Reply(DrawnReply);

            PadMenuTests.Click(Sub(AiMenu(h), "Draw as diagram"));

            Assert.False(h.Window.AiSessionNow!.AwaitingInstruction);
            Assert.Equal(Visibility.Collapsed, h.Pane.InstructionBox.Visibility);
            await Finished(h);
            Assert.Single(h.Model.Requests);
            Assert.Equal(DrawnReply, h.Pane.ResultBox.Shown);
            Assert.Equal(Visibility.Collapsed, h.Pane.InstructionBox.Visibility);
        });

        [Fact]
        public Task Draw_as_diagram_over_24000_characters_shows_the_refusal_and_sends_nothing() => OnUiAsync(async h =>
        {
            Write(h, new string('x', PadAiAction.ReadMaxChars + 1));

            await h.Window.RunAiAsync(PadAiAction.Diagram);

            Assert.Empty(h.Model.Requests);
            Assert.Equal("Select less text: at most 24,000 characters", h.Pane.StatusText.Text);
        });

        [Fact]
        public Task With_AI_off_Draw_as_diagram_sends_nothing() => OnUiAsync(async h =>
        {
            h.AiOn = false;
            Write(h, "login\npay\nship");

            await h.Window.RunAiAsync(PadAiAction.Diagram);

            Assert.Empty(h.Model.Requests);
            Assert.Equal(0, h.RunnersBuilt);
            Assert.Null(h.Window.AiSessionNow);
            Assert.Equal(AiOff, h.Window.StatusMessage.Text);
        });

        // ---- diagram help (part 2, spec 2): Fix with AI ------------------------------------------

        /// <summary>
        /// A note with two Mermaid blocks, on lines 1 to 4 and 6 to 10. The tests make the second
        /// one fail; its source is lines 7 to 9.
        /// </summary>
        private const string TwoDiagrams = "```mermaid\nflowchart LR\n  a --> b\n```\nbetween\n```mermaid\nflowchart LR\n  c --> d --\n  d --> e\n```\nafter";
        private const string FailingSource = "flowchart LR\n  c --> d --\n  d --> e";
        private const string FixedSource = "flowchart LR\n  c --> d\n  d --> e";
        private const string RendererMessage = "Parse error on line 2:\n  c --> d --\n-----------^\nExpecting 'NODE', got 'NEWLINE'";
        private const string DiagramGone = "That diagram is no longer there";

        /// <summary>What a request holds between its note tags: the text that was sent as data.</summary>
        private static string NoteBody(string userMessage)
        {
            const string open = "\n\n<note>\n", close = "\n</note>";
            int at = userMessage.IndexOf(open, StringComparison.Ordinal);
            Assert.True(at >= 0, "the request has no note tag");
            Assert.EndsWith(close, userMessage, StringComparison.Ordinal);
            int start = at + open.Length;
            return userMessage.Substring(start, userMessage.Length - close.Length - start);
        }

        /// <summary>Runs a test over a window that draws its diagrams through a fake renderer.</summary>
        private static Task OnUiWithDiagrams(Func<Harness, FakeRenderer, Task> test)
        {
            var renderer = new FakeRenderer();
            return OnUiAsync(h => test(h, renderer), renderer);
        }

        /// <summary>The picture or the error box under line <paramref name="line"/>, once the editor is laid out; null when it has none.</summary>
        private static DiagramPicture? PictureUnder(Harness h, int line)
        {
            PadLanguageWindowTests.Render(h.Window);
            return h.Editor.TextArea.TextView.GetVisualLine(line)?.Elements.OfType<DiagramElement>().SingleOrDefault()?.Picture as DiagramPicture;
        }

        /// <summary>The first item of an error box's menu: Fix with AI, or Set up AI….</summary>
        private static MenuItem FixEntry(DiagramPicture box) => box.ContextMenu!.Items.OfType<MenuItem>().First();

        /// <summary>Writes <see cref="TwoDiagrams"/> and draws it: the first block gets a picture, the second the renderer's error.</summary>
        private static async Task WriteTwoDiagramsTheSecondFailing(Harness h, FakeRenderer renderer)
        {
            Write(h, TwoDiagrams);
            PadLanguageWindowTests.Render(h.Window);
            h.Window.LanguageView.DiagramBoard!.DrawDue();            // as if typing had paused
            PadLanguageWindowTests.Render(h.Window);
            Assert.Equal(2, renderer.Calls.Count);
            renderer.Finish(0, DiagramFakes.Picture());
            renderer.Finish(1, DiagramResult.Failure(RendererMessage, lasting: true));
            await Dispatcher.Yield(DispatcherPriority.Background);    // the drawings' continuations
            Assert.Equal(RendererMessage, PictureUnder(h, 10)!.ErrorText!.Text);
            Assert.NotNull(PictureUnder(h, 4)!.Image);
        }

        private static void PutCaretOnLine(Harness h, int line) => h.Editor.CaretOffset = h.Editor.Document.GetLineByNumber(line).Offset;

        /// <summary>Nothing was sent, built or counted, the pane is closed and the selection is where it was.</summary>
        private static void AssertNothingRan(Harness h)
        {
            Assert.Empty(h.Model.Requests);
            Assert.Equal(0, h.Usage.UsedToday);
            Assert.Equal(0, h.RunnersBuilt);
            Assert.Null(h.Window.AiSessionNow);
            Assert.Equal(Visibility.Collapsed, h.Pane.Visibility);
            Assert.Equal(0, h.Editor.SelectionLength);
            Assert.Empty(h.Log);
        }

        // ---- review focus 5 (part 2): only the failing block's source is sent and replaced ---------

        [Fact]
        public Task Fix_diagram_sends_only_the_failing_blocks_source_and_Replace_changes_only_those_lines() => OnUiAsync(async h =>
        {
            Write(h, TwoDiagrams);
            h.Model.Reply(FixedSource);

            await h.Window.FixDiagramAsync(6, 10, "mermaid", RendererMessage);

            // What was sent: the instruction and, between the note tags, exactly the second block's source.
            ScriptedChatClient.Request request = Assert.Single(h.Model.Requests);
            string sent = request.Messages[1].Text;
            Assert.Equal(PadAiPrompts.System, request.Messages[0].Text);
            Assert.Equal(PadAiPrompts.ForAction(PadAiAction.FixDiagram("mermaid", RendererMessage).Instruction, FailingSource), sent);
            Assert.Equal(FailingSource, NoteBody(sent));
            Assert.DoesNotContain("```", sent, StringComparison.Ordinal);                   // not a fence
            foreach (string elsewhere in new[] { "a --> b", "between", "after" })           // not the other block, not the text around
                Assert.DoesNotContain(elsewhere, sent, StringComparison.Ordinal);

            // The user sees what is sent and what will be replaced: it is selected, and the pane names it.
            Assert.Equal(FailingSource, h.Editor.SelectedText);
            Assert.Equal("Fix diagram", h.Pane.TitleText.Text);
            Assert.Equal("Selection, " + Count(FailingSource.Length) + " characters", h.Pane.SourceText.Text);
            Assert.Equal(FixedSource, h.Pane.ResultBox.Shown);
            Assert.Equal(Visibility.Visible, h.Pane.ChangesToggle.Visibility);              // a rewrite: Changes shows the diff
            Assert.True(h.Pane.ReplaceButton.IsEnabled);
            Assert.Equal(TwoDiagrams, h.Editor.Document.Text);                              // nothing changes before a click

            Click(h.Pane.ReplaceButton);

            string[] before = TwoDiagrams.Split('\n'), after = h.Editor.Document.Text.Split('\n');
            Assert.Equal(before.Length, after.Length);
            for (int i = 0; i < before.Length; i++)
            {
                if (i == 7) Assert.Equal("  c --> d", after[i]);                            // line 8, the one the fix changed
                else Assert.Equal(before[i], after[i]);                                     // both fence pairs, the first block and the text
            }
            Assert.Equal(FixedSource, h.Editor.SelectedText);

            h.Editor.Undo();                                                                // one Ctrl+Z
            Assert.Equal(TwoDiagrams, h.Editor.Document.Text);

            // The log has the action and the counts, never the renderer's message or the source.
            Assert.Equal(new[] { "AI fix-diagram: " + Count(sent.Length) + " chars in the request, " + Count(FixedSource.Length) + " chars back, ok" }, h.Log);
        });

        [Fact]
        public Task Fix_with_AI_on_the_error_box_runs_on_the_failing_block_alone_and_MicaPad_draws_the_corrected_source() => OnUiWithDiagrams(async (h, renderer) =>
        {
            await WriteTwoDiagramsTheSecondFailing(h, renderer);
            h.Model.Reply(FixedSource);
            MenuItem entry = FixEntry(PictureUnder(h, 10)!);
            Assert.Equal("Fix with AI", entry.Header);

            PadMenuTests.Click(entry);
            await Finished(h);

            string sent = Assert.Single(h.Model.Requests).Messages[1].Text;
            Assert.Equal(PadAiPrompts.ForAction(PadAiAction.FixDiagram("mermaid", RendererMessage).Instruction, FailingSource), sent);
            Assert.Equal(FailingSource, NoteBody(sent));
            Assert.Equal(FailingSource, h.Editor.SelectedText);
            Assert.Equal("Fix diagram", h.Pane.TitleText.Text);

            Click(h.Pane.ReplaceButton);
            Assert.Equal(TwoDiagrams.Replace(FailingSource, FixedSource, StringComparison.Ordinal), h.Editor.Document.Text);

            h.Window.LanguageView.DiagramBoard!.DrawDue();            // typing paused: the block is drawn again
            PadLanguageWindowTests.Render(h.Window);
            Assert.Equal(3, renderer.Calls.Count);                    // the first block's picture came from the cache
            Assert.Equal(FixedSource, renderer.Calls[2].Request.Source);
        });

        [Fact]
        public Task The_AI_menu_has_Fix_diagram_only_while_the_caret_is_in_a_block_that_shows_an_error() => OnUiWithDiagrams(async (h, renderer) =>
        {
            await WriteTwoDiagramsTheSecondFailing(h, renderer);
            h.Model.Reply(FixedSource);

            foreach (int line in new[] { 2, 4, 5, 11 })              // a block that renders, the text between and the text after
            {
                PutCaretOnLine(h, line);
                Assert.DoesNotContain("Fix diagram", Headers(AiMenu(h)));
            }
            foreach (int line in new[] { 6, 7, 9, 10 })              // the failing block, its fences included
            {
                PutCaretOnLine(h, line);
                Assert.Contains("Fix diagram", Headers(AiMenu(h)));
            }

            PutCaretOnLine(h, 8);
            MenuItem ai = AiMenu(h);
            Assert.Equal(new[]
            {
                "Improve writing", "Fix spelling and grammar", "Make shorter", "Translate to English", "Translate to Thai",
                "Summarize", "Explain", "Draw as diagram", "Fix diagram", "-", "Ask AI…",
            }, Headers(ai));

            PadMenuTests.Click(Sub(ai, "Fix diagram"));
            await Finished(h);

            Assert.Equal(FailingSource, NoteBody(Assert.Single(h.Model.Requests).Messages[1].Text));
            Assert.Equal(PadAiPrompts.ForAction(PadAiAction.FixDiagram("mermaid", RendererMessage).Instruction, FailingSource), h.Sent());
            Assert.Equal(FailingSource, h.Editor.SelectedText);
        });

        [Fact]
        public Task Without_diagram_pictures_the_AI_menu_has_no_Fix_diagram() => OnUi(h =>
        {
            Write(h, TwoDiagrams);
            PutCaretOnLine(h, 8);

            Assert.Null(h.Window.LanguageView.DiagramBoard);          // this window draws no diagrams: no block shows an error
            Assert.DoesNotContain("Fix diagram", Headers(AiMenu(h)));
        });

        [Fact]
        public Task With_AI_off_the_error_box_reads_Set_up_AI_the_menu_has_no_Fix_diagram_and_nothing_is_sent() => OnUiWithDiagrams(async (h, renderer) =>
        {
            h.AiOn = false;
            await WriteTwoDiagramsTheSecondFailing(h, renderer);
            PutCaretOnLine(h, 8);
            h.Editor.Select(h.Editor.CaretOffset, 0);

            Assert.Equal(new[] { "Set up AI…" }, Headers(AiMenu(h)));
            MenuItem entry = FixEntry(PictureUnder(h, 10)!);
            Assert.Equal("Set up AI…", entry.Header);

            PadMenuTests.Click(entry);
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.Equal(1, h.SettingsOpened);                        // Settings → MicaPad
            AssertNothingRan(h);
        });

        [Fact]
        public Task With_AI_off_Fix_diagram_sends_nothing_builds_no_runner_counts_nothing_and_selects_nothing() => OnUiAsync(async h =>
        {
            h.AiOn = false;
            Write(h, TwoDiagrams);

            await h.Window.FixDiagramAsync(6, 10, "mermaid", RendererMessage);

            AssertNothingRan(h);                                      // the gate comes first: not even the selection moves
            Assert.Equal(AiOff, h.Window.StatusMessage.Text);         // the user is told why
        });

        [Fact]
        public Task With_AI_off_a_diagram_that_is_gone_is_still_answered_with_how_to_turn_AI_on() => OnUiAsync(async h =>
        {
            h.AiOn = false;
            Write(h, "no diagram here");

            await h.Window.FixDiagramAsync(6, 10, "mermaid", RendererMessage);

            AssertNothingRan(h);
            Assert.Equal(AiOff, h.Window.StatusMessage.Text);         // the note is not even read while AI is off
        });

        [Fact]
        public Task With_AI_turned_off_between_the_click_and_the_request_Fix_diagram_sends_nothing() => OnUiAsync(async h =>
        {
            Write(h, TwoDiagrams);
            int asked = 0;
            h.Window.AiEnabled = () => asked++ == 0;              // on at the click, off when the request is about to start

            await h.Window.FixDiagramAsync(6, 10, "mermaid", RendererMessage);

            Assert.Equal(2, asked);                               // the same two checks as every action: no way round them
            Assert.Empty(h.Model.Requests);
            Assert.Equal(0, h.Usage.UsedToday);
            Assert.Equal(0, h.RunnersBuilt);
            Assert.Equal(AiOff, h.Pane.StatusText.Text);
            Assert.Equal(new[] { "AI fix-diagram: 0 chars in the request, 0 chars back, failed" }, h.Log);
            Assert.Equal(TwoDiagrams, h.Editor.Document.Text);
        });

        [Theory]
        [InlineData(6, 9)]                                            // line 9 is no fence
        [InlineData(5, 10)]                                           // line 5 is no fence
        [InlineData(4, 6)]                                            // one block's closing fence and the next one's opening
        [InlineData(1, 10)]                                           // two fences, not of one block
        [InlineData(7, 9)]
        [InlineData(0, 4)]
        [InlineData(6, 99)]
        [InlineData(10, 6)]
        public Task Lines_that_no_longer_hold_a_diagrams_fence_pair_say_so_and_send_nothing(int open, int close) => OnUiAsync(async h =>
        {
            Write(h, TwoDiagrams);

            await h.Window.FixDiagramAsync(open, close, "mermaid", RendererMessage);

            AssertNothingRan(h);
            Assert.Equal(DiagramGone, h.Window.StatusMessage.Text);
            Assert.Equal(Visibility.Visible, h.Window.StatusMessage.Visibility);
            Assert.Equal(TwoDiagrams, h.Editor.Document.Text);
        });

        [Fact]
        public Task A_diagram_edited_away_between_the_click_and_the_call_says_so_and_sends_nothing() => OnUiAsync(async h =>
        {
            Write(h, TwoDiagrams);
            h.Editor.Document.Insert(0, "# Title\n");                // the block moved a line down: 6 and 10 are not its fences now
            h.Editor.Select(0, 0);

            await h.Window.FixDiagramAsync(6, 10, "mermaid", RendererMessage);

            AssertNothingRan(h);
            Assert.Equal(DiagramGone, h.Window.StatusMessage.Text);

            Write(h, "```js\nlet a = 1;\n```");                       // the lines hold a fence pair, of ordinary code
            await h.Window.FixDiagramAsync(1, 3, "js", RendererMessage);

            AssertNothingRan(h);
            Assert.Equal(DiagramGone, h.Window.StatusMessage.Text);
        });

        [Theory]
        [InlineData("text\n```mermaid\n```\nafter", 2, 3)]            // nothing between the fences
        [InlineData("text\n```mermaid\n   \n\n```\nafter", 2, 5)]     // blank lines only
        [InlineData("```kroki\nmermaid\n```", 1, 3)]                  // only its type line
        public Task An_empty_diagram_says_there_is_no_text_and_sends_nothing(string note, int open, int close) => OnUiAsync(async h =>
        {
            Write(h, note);

            await h.Window.FixDiagramAsync(open, close, "mermaid", "No diagram type detected");

            AssertNothingRan(h);
            Assert.Equal(NoText, h.Window.StatusMessage.Text);
            Assert.Equal(note, h.Editor.Document.Text);
        });

        [Theory]
        [InlineData("Energy:\r\n$$\r\nE = mc^{2\r\n$$\r\nafter", 2, 4, "math", "E = mc^{2")]                                   // a $$ block
        [InlineData("```kroki\nplantuml\n@startuml\na -> \n@enduml\n```\nafter", 1, 6, "plantuml", "@startuml\na -> \n@enduml")]   // not its type line
        [InlineData("~~~DOT my graph\r\ndigraph { a -> }\r\n  x\r\n~~~", 1, 4, "DOT", "digraph { a -> }\r\n  x")]
        public Task Fix_diagram_takes_the_lines_between_the_fences_of_any_kind_of_block(string note, int open, int close, string kind, string source) => OnUiAsync(async h =>
        {
            Write(h, note);
            h.Model.Reply("fixed");

            await h.Window.FixDiagramAsync(open, close, kind, "Parse error");

            Assert.Equal(source, h.Editor.SelectedText);
            Assert.Equal(source, NoteBody(h.Sent()));
            Assert.StartsWith("Task: This " + kind.ToLowerInvariant() + " block does not render.", h.Sent(), StringComparison.Ordinal);

            Click(h.Pane.ReplaceButton);

            Assert.Equal(note.Replace(source, "fixed", StringComparison.Ordinal), h.Editor.Document.Text);   // the fences, the type line and the rest are as they were
        });

        [Fact]
        public Task A_credential_in_the_failing_block_or_in_the_renderers_message_never_leaves() => OnUiAsync(async h =>
        {
            Write(h, "```mermaid\nflowchart LR\n  a --> {{secret:K7Q2M9XD}} --\n```");
            h.Model.Reply("flowchart LR\n  a --> [[CREDENTIAL_1]]");

            await h.Window.FixDiagramAsync(1, 4, "mermaid", "Parse error on line 2:\n  a --> {{secret:K7Q2M9XD}} --");

            ScriptedChatClient.Request request = Assert.Single(h.Model.Requests);
            string everything = string.Join("\n", request.Messages.Select(m => m.Text).Concat(h.Log));
            Assert.DoesNotContain("{{secret", everything, StringComparison.Ordinal);
            Assert.DoesNotContain("K7Q2M9XD", everything, StringComparison.Ordinal);
            Assert.Contains("a -- [credential] --", h.Sent().Substring(0, h.Sent().IndexOf("<note>", StringComparison.Ordinal)), StringComparison.Ordinal);
            Assert.Equal("flowchart LR\n  a --> [[CREDENTIAL_1]] --", NoteBody(h.Sent()));

            Click(h.Pane.ReplaceButton);

            Assert.Equal("```mermaid\nflowchart LR\n  a --> {{secret:K7Q2M9XD}}\n```", h.Editor.Document.Text);   // the pill is back
        });

        [Fact]
        public Task Fix_diagram_on_a_read_only_note_is_refused_and_selects_nothing() => OnUiAsync(async h =>
        {
            Write(h, TwoDiagrams);
            h.Editor.IsReadOnly = true;

            await h.Window.FixDiagramAsync(6, 10, "mermaid", RendererMessage);

            AssertNothingRan(h);
            Assert.Equal(ReadOnly, h.Window.StatusMessage.Text);
        });

        [Fact]
        public Task A_failing_block_over_8000_characters_shows_the_rewrite_refusal_and_sends_nothing() => OnUiAsync(async h =>
        {
            Write(h, "```mermaid\nflowchart LR\n" + new string('x', PadAiAction.RewriteMaxChars) + "\n```");

            await h.Window.FixDiagramAsync(1, 4, "mermaid", "Parse error");

            Assert.Empty(h.Model.Requests);
            Assert.Equal(0, h.Usage.UsedToday);
            Assert.Equal("Select less text: at most 8,000 characters for a rewrite", h.Pane.StatusText.Text);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);
        });

        // ---- fix round 1: a credential the renderer cut, a reply that holds a fence, folds, the menu ----

        private const string HoldsFence = "The result holds a code fence, so it cannot replace the diagram's source";

        /// <summary>The opening and closing fence lines (1-based) of every closed block of the shown note.</summary>
        private static (int Open, int Close)[] BlocksOf(Harness h)
        {
            string[] lines = h.Editor.Document.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            int[] openings = FenceTracker.Openings(FenceTracker.Classify(lines));
            return openings.Select((open, i) => (Open: open, Close: i + 1)).Where(b => b.Open != 0).ToArray();
        }

        [Fact]
        public Task A_credential_the_renderer_cut_at_its_start_never_leaves_in_its_message() => OnUiAsync(async h =>
        {
            Write(h, "```mermaid\nflowchart LR\n  x --> {{secret:K7Q2M9XD}}  A->B\n```");
            h.Model.Reply("flowchart LR\n  x --> [[CREDENTIAL_1]]\n  A --> B");

            // Mermaid quotes the last 20 characters before the error: the pill arrives without its start.
            await h.Window.FixDiagramAsync(1, 4, "mermaid", "Parse error on line 2:\n...et:K7Q2M9XD}}  A->B\n----------------------^");

            ScriptedChatClient.Request request = Assert.Single(h.Model.Requests);
            string everything = string.Join("\n", request.Messages.Select(m => m.Text).Concat(h.Log));
            foreach (string part in new[] { "K7Q2M9XD", "K7Q2", "M9XD", "{{secret" })
                Assert.DoesNotContain(part, everything, StringComparison.Ordinal);
            Assert.Contains("...[credential] A- B", h.Sent(), StringComparison.Ordinal);
        });

        /// <summary>
        /// A renderer that reads the source token by token quotes a pill its own way: one brace,
        /// none, the id alone. The window knows the ids of the block's own pills, and none of them
        /// leaves in the message, whatever stands around it.
        /// </summary>
        [Theory]
        [InlineData("Parse error on line 2: got '{secret:K7Q2M9XD}'")]
        [InlineData("Parse error on line 2: got 'secret:K7Q2M9XD}'")]
        [InlineData("Lexical error on line 2: {secret:K7Q2M9XD} ok")]
        [InlineData("syntax error in line 2 near K7Q2M9XD")]
        public Task An_id_of_the_blocks_own_credential_never_leaves_however_the_renderer_quotes_it(string message) => OnUiAsync(async h =>
        {
            Write(h, "```mermaid\nflowchart LR\n  x --> {{secret:K7Q2M9XD}}  A->B\n```");
            h.Model.Reply("flowchart LR\n  x --> [[CREDENTIAL_1]]\n  A --> B");

            await h.Window.FixDiagramAsync(1, 4, "mermaid", message);

            ScriptedChatClient.Request request = Assert.Single(h.Model.Requests);
            string everything = string.Join("\n", request.Messages.Select(m => m.Text).Concat(h.Log));
            Assert.DoesNotContain("K7Q2M9XD", everything, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[credential]", h.Sent().Substring(0, h.Sent().IndexOf("<note>", StringComparison.Ordinal)), StringComparison.Ordinal);
            Assert.Equal("flowchart LR\n  x --> [[CREDENTIAL_1]]  A->B", NoteBody(h.Sent()));   // the source itself is masked, as before

            // Try again sends the same cleaned instruction.
            AiSession first = h.Window.AiSessionNow!;
            h.Model.Reply("flowchart LR\n  x --> [[CREDENTIAL_1]]\n  A --> B");
            Click(h.Pane.RetryButton);
            await Finished(h, after: first);
            Assert.Equal(h.Sent(0), h.Sent(1));
        });

        [Fact]
        public Task A_fix_that_comes_back_in_a_code_fence_is_unwrapped_and_Replace_changes_only_the_source_lines() => OnUiAsync(async h =>
        {
            Write(h, TwoDiagrams);
            h.Model.Reply("```mermaid\n" + FixedSource + "\n```\n");

            await h.Window.FixDiagramAsync(6, 10, "mermaid", RendererMessage);

            Assert.Equal(FixedSource, h.Pane.ResultBox.Shown);        // shown as it will be written: no fence
            Assert.Equal("", h.Pane.StatusText.Text);
            Assert.True(h.Pane.ReplaceButton.IsEnabled);

            Click(h.Pane.ReplaceButton);

            Assert.Equal(TwoDiagrams.Replace(FailingSource, FixedSource, StringComparison.Ordinal), h.Editor.Document.Text);
            Assert.Equal(new[] { (1, 4), (6, 10) }, BlocksOf(h));     // the note's two blocks, where they were
        });

        [Fact]
        public Task A_fix_with_a_closing_fence_in_the_middle_cannot_replace_and_says_why() => OnUiAsync(async h =>
        {
            Write(h, TwoDiagrams);
            h.Model.Reply(FixedSource + "\n```\nThe second arrow had no target.");

            await h.Window.FixDiagramAsync(6, 10, "mermaid", RendererMessage);

            Assert.Equal(Visibility.Visible, h.Pane.ReplaceButton.Visibility);
            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Assert.Equal(HoldsFence, h.Pane.StatusText.Text);
            Assert.True(h.Pane.CopyButton.IsEnabled);                 // the text is still there to take

            Click(h.Pane.ReplaceButton);                              // even a forced click leaves the block whole
            Assert.Equal(TwoDiagrams, h.Editor.Document.Text);
            Assert.Equal(new[] { (1, 4), (6, 10) }, BlocksOf(h));
        });

        [Theory]
        [InlineData("Energy:\n$$\nE = mc^{2\n$$\nafter", 2, 4, "math", "E = mc^{2}\n$$\nSo it is.")]                                // a math block and $$
        [InlineData("~~~~mermaid\nflowchart LR\n  a --> b --\n~~~~\nafter", 1, 4, "mermaid", "flowchart LR\n  a --> b\n~~~~~\nDone.")]   // a tilde block and a longer tilde line
        [InlineData("```kroki\nplantuml\n@startuml\na -> \n@enduml\n```", 1, 6, "plantuml", "@startuml\na -> b\n@enduml\n````")]         // the fence of a kroki block, on the last line
        public Task A_reply_that_would_close_the_blocks_own_fence_cannot_replace(string note, int open, int close, string kind, string reply) => OnUiAsync(async h =>
        {
            Write(h, note);
            h.Model.Reply(reply);

            await h.Window.FixDiagramAsync(open, close, kind, "Parse error");

            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Assert.Equal(HoldsFence, h.Pane.StatusText.Text);
            Click(h.Pane.ReplaceButton);
            Assert.Equal(note, h.Editor.Document.Text);
        });

        [Theory]
        [InlineData("~~~~markmap\n# Rot\n~~~~\nafter", 1, 3, "# Root\n```js\ncode\n```")]      // backticks in a tilde block are its text
        [InlineData("````markmap\n# Rot\n````\nafter", 1, 3, "# Root\n```\ncode\n```")]        // and so is a fence shorter than the block's own
        [InlineData("$$\nx^{2\n$$\nafter", 1, 3, "x^{2}\n$$$")]                                // a math block closes on exactly $$
        public Task A_reply_whose_fence_like_lines_cannot_close_the_block_replaces_its_source(string note, int open, int close, string reply) => OnUiAsync(async h =>
        {
            Write(h, note);
            h.Model.Reply(reply);

            await h.Window.FixDiagramAsync(open, close, "markmap", "Parse error");

            Assert.Equal("", h.Pane.StatusText.Text);
            Assert.True(h.Pane.ReplaceButton.IsEnabled);
            Click(h.Pane.ReplaceButton);

            (int Open, int Close) block = BlocksOf(h).First();
            Assert.Equal(open, block.Open);                           // the block still ends on its own closing fence
            Assert.Equal("after", h.Editor.Document.GetText(h.Editor.Document.GetLineByNumber(block.Close + 1)));
        });

        // ---- the block's fence is read again (final review, D1) ----------------------------------

        private const string FourBackticks = "````markmap\n# Rot\n````\nafter";
        private const string ThreeBackticks = "```markmap\n# Rot\n```\nafter";

        /// <summary>A reply that is text of the block while its fence is four backticks, and closes it once the fence is three.</summary>
        private const string HoldsThree = "# Root\n```\ncode\n```";

        /// <summary>Takes one character off both fences of the block on lines 1 to 3; the source between them is not touched.</summary>
        private static void ShortenFences(Harness h)
        {
            var document = h.Editor.Document;
            document.Remove(document.GetLineByNumber(3).Offset, 1);
            document.Remove(0, 1);
        }

        [Fact]
        public Task A_fix_checked_against_four_backticks_cannot_replace_once_the_fences_are_shortened_to_three() => OnUiAsync(async h =>
        {
            Write(h, FourBackticks);
            h.Model.Reply(HoldsThree);

            await h.Window.FixDiagramAsync(1, 3, "markmap", "Parse error");
            Assert.True(h.Pane.ReplaceButton.IsEnabled);
            Assert.Equal("", h.Pane.StatusText.Text);

            ShortenFences(h);
            Assert.Equal(ThreeBackticks, h.Editor.Document.Text);

            Assert.False(h.Pane.ReplaceButton.IsEnabled);              // said at once, as the fence is edited
            Assert.Equal(HoldsFence, h.Pane.StatusText.Text);
            Click(h.Pane.ReplaceButton);                               // and even a forced click leaves the block whole
            Assert.Equal(ThreeBackticks, h.Editor.Document.Text);
            Assert.Equal(new[] { (1, 3) }, BlocksOf(h));

            // The other way round: with four backticks again, the same result is text of the block.
            h.Editor.Document.Insert(h.Editor.Document.GetLineByNumber(3).Offset, "`");
            h.Editor.Document.Insert(0, "`");
            Assert.True(h.Pane.ReplaceButton.IsEnabled);
            Assert.Equal("", h.Pane.StatusText.Text);

            Click(h.Pane.ReplaceButton);

            Assert.Equal("````markmap\n" + HoldsThree + "\n````\nafter", h.Editor.Document.Text);
            Assert.Equal(new[] { (1, 6) }, BlocksOf(h));               // one block still, ending on its own fence
        });

        [Fact]
        public Task Try_again_after_a_fix_checks_the_new_reply_against_the_fence_as_it_is_now() => OnUiAsync(async h =>
        {
            Write(h, FourBackticks);
            h.Model.Reply("first try").Reply(HoldsThree);
            await h.Window.FixDiagramAsync(1, 3, "markmap", "Parse error");
            AiSession first = h.Window.AiSessionNow!;
            Assert.Equal("````", first.Action.BlockFence);
            ShortenFences(h);

            Click(h.Pane.RetryButton);
            await Finished(h, after: first);

            Assert.Equal("```", h.Window.AiSessionNow!.Action.BlockFence);   // read again from the note
            Assert.Equal(h.Sent(0), h.Sent(1));                        // the same instruction on the same source
            Assert.False(h.Pane.ReplaceButton.IsEnabled);
            Assert.Equal(HoldsFence, h.Pane.StatusText.Text);
            Click(h.Pane.ReplaceButton);
            Assert.Equal(ThreeBackticks, h.Editor.Document.Text);
        });

        /// <summary>Deletes both fence lines of the block on lines 1 to 4, so its source stands in no block.</summary>
        private static void DeleteFences(Harness h)
        {
            var document = h.Editor.Document;
            var closing = document.GetLineByNumber(4);
            document.Remove(closing.Offset - 1, closing.Length + 1);   // with the line break before it
            document.Remove(0, document.GetLineByNumber(1).TotalLength);
        }

        [Fact]
        public Task A_fix_whose_block_lost_its_fences_cannot_put_a_fence_line_into_the_note_and_cannot_be_tried_again() => OnUiAsync(async h =>
        {
            Write(h, "```mermaid\nflowchart LR\n  a --> b --\n```\nafter");
            h.Model.Reply("flowchart LR\n  a --> b\n~~~\nx\n~~~");   // tildes are text inside a backtick block
            await h.Window.FixDiagramAsync(1, 4, "mermaid", "Parse error");
            Assert.True(h.Pane.ReplaceButton.IsEnabled);
            AiSession first = h.Window.AiSessionNow!;

            DeleteFences(h);
            const string bare = "flowchart LR\n  a --> b --\nafter";
            Assert.Equal(bare, h.Editor.Document.Text);

            Assert.False(h.Pane.ReplaceButton.IsEnabled);              // outside a block, a fence line would open one that never closes
            Assert.Equal(HoldsFence, h.Pane.StatusText.Text);
            Click(h.Pane.ReplaceButton);
            Assert.Equal(bare, h.Editor.Document.Text);

            Click(h.Pane.RetryButton);                                 // there is no diagram to fix any more
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.Same(first, h.Window.AiSessionNow);
            Assert.Single(h.Model.Requests);
            Assert.Equal(DiagramGone, h.Window.StatusMessage.Text);
        });

        [Fact]
        public Task A_fix_with_no_fence_line_still_replaces_its_source_after_the_blocks_fences_are_deleted() => OnUiAsync(async h =>
        {
            Write(h, "```mermaid\nflowchart LR\n  a --> b --\n```\nafter");
            h.Model.Reply("flowchart LR\n  a --> b");
            await h.Window.FixDiagramAsync(1, 4, "mermaid", "Parse error");

            DeleteFences(h);

            Assert.True(h.Pane.ReplaceButton.IsEnabled);
            Assert.Equal("", h.Pane.StatusText.Text);
            Click(h.Pane.ReplaceButton);
            Assert.Equal("flowchart LR\n  a --> b\nafter", h.Editor.Document.Text);
        });

        // ---- Draw as diagram where a diagram is drawn (final review, D2) --------------------------

        private static readonly string[] WithoutDiagram =
        {
            "Improve writing", "Fix spelling and grammar", "Make shorter", "Translate to English", "Translate to Thai",
            "Summarize", "Explain", "-", "Ask AI…",
        };

        [Fact]
        public Task The_AI_menu_offers_Draw_as_diagram_only_in_a_note_shown_as_Markdown() => OnUi(h =>
        {
            Write(h, "login\npay\nship");
            Assert.Same(PadLanguages.Markdown, h.Window.ShownLanguage.Effective);
            Assert.Contains("Draw as diagram", Headers(AiMenu(h)));    // a note: Markdown

            // Chosen as Plain text or as code: a fenced block is never drawn there.
            foreach (string language in new[] { "plain", "csharp" })
            {
                h.Window.ChooseLanguage(h.Shown, language);
                Assert.Equal(WithoutDiagram, Headers(AiMenu(h)));      // the other actions are as they were
            }
            h.Window.ChooseLanguage(h.Shown, null);
            Assert.Contains("Draw as diagram", Headers(AiMenu(h)));

            // A source file: Insert below would write a fence into the code.
            PadLanguageWindowTests.OpenFile(h.Window, h.Env, "Program.cs", "class Program { }");
            Assert.Equal("csharp", h.Window.ShownLanguage.Effective.Id);
            Assert.Equal(WithoutDiagram, Headers(AiMenu(h)));
            Assert.True(Sub(AiMenu(h), "Summarize").IsEnabled);

            // A .md file is Markdown, and so is a .txt file while Markdown formatting is on.
            PadLanguageWindowTests.OpenFile(h.Window, h.Env, "plan.md", "login\npay");
            Assert.Contains("Draw as diagram", Headers(AiMenu(h)));
            PadLanguageWindowTests.OpenFile(h.Window, h.Env, "plan.txt", "login\npay");
            Assert.Same(PadLanguages.Markdown, h.Window.ShownLanguage.Effective);
            Assert.Contains("Draw as diagram", Headers(AiMenu(h)));
        });

        [Fact]
        public Task With_Markdown_formatting_off_a_note_and_a_txt_file_are_plain_text_and_have_no_Draw_as_diagram() => OnUiAsync(h =>
        {
            Write(h, "login\npay\nship");
            Assert.Same(PadLanguages.Plain, h.Window.ShownLanguage.Effective);
            Assert.Equal(WithoutDiagram, Headers(AiMenu(h)));

            PadLanguageWindowTests.OpenFile(h.Window, h.Env, "plan.txt", "login\npay");
            Assert.Same(PadLanguages.Plain, h.Window.ShownLanguage.Effective);
            Assert.Equal(WithoutDiagram, Headers(AiMenu(h)));
            return Task.CompletedTask;
        }, config: new AppConfig { PadMarkdown = false });

        [Fact]
        public Task The_pane_offers_no_Insert_below_for_a_fix() => OnUiAsync(async h =>
        {
            Write(h, TwoDiagrams);
            h.Model.Reply(FixedSource).Reply("- two blocks");

            await h.Window.FixDiagramAsync(6, 10, "mermaid", RendererMessage);

            Assert.Equal(Visibility.Collapsed, h.Pane.InsertButton.Visibility);   // it would land inside the block, under the old source
            Assert.Equal(Visibility.Visible, h.Pane.ReplaceButton.Visibility);
            Assert.Equal(Visibility.Visible, h.Pane.CopyButton.Visibility);
            Click(h.Pane.InsertButton);                               // even a forced click inserts nothing
            Assert.Equal(TwoDiagrams, h.Editor.Document.Text);

            h.Editor.Select(0, 0);
            await h.Window.RunAiAsync(PadAiAction.Summarize);         // the next action offers it again
            Assert.Equal(Visibility.Visible, h.Pane.InsertButton.Visibility);
            Assert.True(h.Pane.InsertButton.IsEnabled);
        });

        [Fact]
        public Task Fix_diagram_unfolds_the_fold_that_hides_the_blocks_source_and_no_other() => OnUiWithDiagrams(async (h, renderer) =>
        {
            await WriteTwoDiagramsTheSecondFailing(h, renderer);
            h.Model.Reply(FixedSource);
            DiagramBoard board = h.Window.LanguageView.DiagramBoard!;
            var document = h.Editor.Document;
            board.SetCodeHidden(document.GetLineByNumber(4), true);   // Hide code on both blocks
            board.SetCodeHidden(document.GetLineByNumber(10), true);
            Assert.True(board.IsCodeHidden(document.GetLineByNumber(4)));
            Assert.True(board.IsCodeHidden(document.GetLineByNumber(10)));

            await h.Window.FixDiagramAsync(6, 10, "mermaid", RendererMessage);

            Assert.False(board.IsCodeHidden(document.GetLineByNumber(10)));   // what is sent is in sight
            Assert.True(board.IsCodeHidden(document.GetLineByNumber(4)));     // the other block stays as it was
            Assert.Equal(FailingSource, h.Editor.SelectedText);
            Assert.Equal(FailingSource, NoteBody(Assert.Single(h.Model.Requests).Messages[1].Text));
            Assert.DoesNotContain(h.Window.LanguageView.Folding!.Manager!.AllFoldings,
                f => f.IsFolded && f.StartOffset < h.Editor.SelectionStart + h.Editor.SelectionLength && f.EndOffset > h.Editor.SelectionStart);
        });

        [Fact]
        public Task A_refused_fix_unfolds_nothing() => OnUiWithDiagrams(async (h, renderer) =>
        {
            await WriteTwoDiagramsTheSecondFailing(h, renderer);
            DiagramBoard board = h.Window.LanguageView.DiagramBoard!;
            var closing = h.Editor.Document.GetLineByNumber(10);
            board.SetCodeHidden(closing, true);
            h.Editor.Select(0, 0);
            h.AiOn = false;

            await h.Window.FixDiagramAsync(6, 10, "mermaid", RendererMessage);

            AssertNothingRan(h);
            Assert.True(board.IsCodeHidden(closing));                 // with AI off the note is left as it is
        });

        [Fact]
        public Task Fix_diagram_in_the_AI_menu_asks_the_board_again_at_the_click() => OnUiWithDiagrams(async (h, renderer) =>
        {
            await WriteTwoDiagramsTheSecondFailing(h, renderer);
            h.Model.Reply(FixedSource);
            PutCaretOnLine(h, 8);
            MenuItem fix = Sub(AiMenu(h), "Fix diagram");             // built while the block was on lines 6 to 10

            h.Editor.Document.Insert(0, "# Title\n\n");               // it moved two lines down before the click
            PadMenuTests.Click(fix);
            await Until(() => h.Window.AiSessionNow is { Finished: true } || h.Window.StatusMessage.Text == DiagramGone, "the fix to end");

            Assert.Equal(FailingSource, NoteBody(Assert.Single(h.Model.Requests).Messages[1].Text));
            Assert.Equal(FailingSource, h.Editor.SelectedText);
            Assert.Equal(8, h.Editor.Document.GetLineByOffset(h.Editor.SelectionStart).LineNumber - 1);   // under its opening fence, where that is now
        });

        [Fact]
        public Task Fix_diagram_in_the_AI_menu_for_a_block_that_is_gone_at_the_click_says_so_and_sends_nothing() => OnUiWithDiagrams(async (h, renderer) =>
        {
            await WriteTwoDiagramsTheSecondFailing(h, renderer);
            PutCaretOnLine(h, 8);
            MenuItem fix = Sub(AiMenu(h), "Fix diagram");
            var document = h.Editor.Document;

            var opening = document.GetLineByNumber(6);
            document.Remove(opening.Offset, opening.Length);          // its opening fence is deleted before the click
            h.Editor.Select(0, 0);
            PadMenuTests.Click(fix);
            await Dispatcher.Yield(DispatcherPriority.Background);

            AssertNothingRan(h);
            Assert.Equal(DiagramGone, h.Window.StatusMessage.Text);
        });

        [Fact]
        public Task Try_again_after_a_fix_sends_the_same_instruction_on_the_same_block() => OnUiAsync(async h =>
        {
            Write(h, TwoDiagrams);
            h.Model.Reply("first try").Reply(FixedSource);
            await h.Window.FixDiagramAsync(6, 10, "mermaid", RendererMessage);
            AiSession first = h.Window.AiSessionNow!;
            h.Editor.Select(0, 0);                                // a click elsewhere in the note

            Click(h.Pane.RetryButton);
            await Finished(h, after: first);

            Assert.Equal(2, h.Model.Requests.Count);
            Assert.Equal(h.Sent(0), h.Sent(1));
            Assert.Equal(FailingSource, NoteBody(h.Sent(1)));
            Click(h.Pane.ReplaceButton);
            Assert.Equal(TwoDiagrams.Replace(FailingSource, FixedSource, StringComparison.Ordinal), h.Editor.Document.Text);
        });
    }
}
