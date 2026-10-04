using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>A source of answer pictures that says what a test tells it to, and records what it was asked.</summary>
    internal sealed class FakeChatDiagrams : IChatDiagrams
    {
        /// <summary>Every <see cref="Get"/> so far, oldest first.</summary>
        public List<(string Source, bool Dark, Action? WhenDone)> Gets { get; } = new();

        /// <summary>The state for a source and a theme; "being drawn" until a test says otherwise.</summary>
        public Func<string, bool, ChatDiagramState> Answer { get; set; } = (_, _) => new ChatDiagramState(ChatDiagramStatus.Drawing);

        public int Cleared { get; private set; }

        /// <summary>Every <see cref="Forget"/> so far, oldest first.</summary>
        public List<(string Source, bool Dark)> Forgotten { get; } = new();

        public ChatDiagramState Get(string source, bool dark, Action? whenDone)
        {
            Gets.Add((source, dark, whenDone));
            return Answer(source, dark);
        }

        public void Forget(string source, bool dark) => Forgotten.Add((source, dark));

        public void Clear() => Cleared++;
    }

    /// <summary>What the tests of diagrams in answers share. No real drawing page, and no clipboard.</summary>
    internal static class ChatDiagramFakes
    {
        public const string Flow = "flowchart LR\n  a --> b";

        /// <summary>A closed fenced block.</summary>
        public static string Block(string source = Flow, string word = "mermaid") => "```" + word + "\n" + source + "\n```";

        /// <summary>A frozen bitmap of that many pixels, every one transparent.</summary>
        public static BitmapSource Bitmap(int width = 200, int height = 100)
        {
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>A real PNG of that many pixels, small as a file: one bit a pixel, every one black.</summary>
        public static byte[] Png(int width, int height)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Counted(width, height)));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        /// <summary>
        /// A frozen bitmap that reports that many pixels and costs one bit for each: a picture that
        /// counts as 15 MB (1,600 by 2,400 by 4 bytes) holds 480 KB here.
        /// </summary>
        public static BitmapSource Counted(int width, int height)
        {
            int stride = (width + 7) / 8;
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.BlackWhite, null, new byte[stride * height], stride);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>A drawn picture, as the engine makes them: twice as many pixels as its size.</summary>
        public static ChatDiagramState Drawn(double width = 100, double height = 50) =>
            new(ChatDiagramStatus.Drawn, Bitmap((int)(width * 2), (int)(height * 2)), width, height);

        public static ChatDiagramState Failed(string error, bool canRetry = false) => new(ChatDiagramStatus.Failed, Error: error, CanRetry: canRetry);

        /// <summary>Waits, with the dispatcher free, until <paramref name="done"/> is true; at most 10 s.</summary>
        public static async Task Until(Func<bool> done, string what)
        {
            var waited = Stopwatch.StartNew();
            while (!done())
            {
                Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "Timed out waiting for " + what);
                await Task.Delay(10);
            }
        }

        /// <summary>
        /// Ends draw <paramref name="index"/> and waits until its waiters were told. One more
        /// waiter is added behind the ones already there: waiters are told in order, so when this
        /// one is, every view's was told before it. The draw must still be waited for.
        /// </summary>
        public static async Task FinishAsync(ChatDiagrams diagrams, FakeRenderer renderer, int index, DiagramResult result)
        {
            var call = renderer.Calls[index];
            int draws = renderer.Calls.Count;
            bool told = false;
            Assert.Equal(ChatDiagramStatus.Drawing, diagrams.Get(call.Request.Source, call.Request.Dark, () => told = true).Status);
            Assert.Equal(draws, renderer.Calls.Count);
            renderer.Finish(index, result);
            await Until(() => told, "the end of the draw to reach the UI thread");
        }
    }

    /// <summary>
    /// The adapter between an answer's Mermaid blocks and MicaPad's drawing engine, over a renderer
    /// whose draws the test ends by hand. On the shared UI thread, where a picture is decoded.
    /// </summary>
    public class ChatDiagramsTests
    {
        private const string Flow = ChatDiagramFakes.Flow;

        private static (ChatDiagrams Diagrams, FakeRenderer Renderer, List<string> Warnings) New()
        {
            var renderer = new FakeRenderer();
            var warnings = new List<string>();
            var diagrams = new ChatDiagrams(() => renderer, () => true) { Warn = warnings.Add };
            return (diagrams, renderer, warnings);
        }

        /// <summary>A renderer that answers every draw at once and counts them; nothing is cached.</summary>
        private sealed class ImmediateRenderer : IDiagramRenderer
        {
            public Func<DiagramRequest, DiagramResult> Answer { get; set; } = _ => DiagramFakes.Picture();

            public List<DiagramRequest> Requests { get; } = new();

            public bool TryGetCached(string key, [MaybeNullWhen(false)] out DiagramResult result)
            {
                result = null;
                return false;
            }

            public Task<DiagramResult> RenderAsync(DiagramRequest request, object slot)
            {
                Requests.Add(request);
                return Task.FromResult(Answer(request));
            }

            public void Forget(string key, object? slot)
            {
            }
        }

        [Fact]
        public void While_the_setting_is_off_a_block_is_off_and_the_renderer_is_not_even_asked_for() => UiThread.Run(() =>
        {
            int asked = 0;
            var renderer = new FakeRenderer();
            var diagrams = new ChatDiagrams(() =>
            {
                asked++;
                return renderer;
            }, () => false);

            Assert.Equal(ChatDiagramStatus.Off, diagrams.Get(Flow, true, null).Status);

            Assert.Equal(0, asked);   // the app makes its renderer on first use: an answer shown while diagrams are off must not make it
            Assert.Empty(renderer.Calls);
        });

        [Fact]
        public void Without_a_renderer_a_block_is_off() => UiThread.Run(() =>
        {
            var diagrams = new ChatDiagrams(() => null, () => true);

            Assert.Equal(ChatDiagramStatus.Off, diagrams.Get(Flow, true, null).Status);
        });

        [Fact]
        public void The_setting_is_read_at_every_call() => UiThread.Run(() =>
        {
            bool enabled = false;
            var renderer = new FakeRenderer();
            var diagrams = new ChatDiagrams(() => renderer, () => enabled);
            Assert.Equal(ChatDiagramStatus.Off, diagrams.Get(Flow, true, null).Status);

            enabled = true;
            Assert.Equal(ChatDiagramStatus.Drawing, diagrams.Get(Flow, true, null).Status);

            enabled = false;
            Assert.Equal(ChatDiagramStatus.Off, diagrams.Get(Flow, true, null).Status);
            Assert.Single(renderer.Calls);
        });

        [Fact]
        public void A_source_over_the_limit_is_too_large_and_is_never_sent_to_the_renderer() => UiThread.Run(() =>
        {
            var (diagrams, renderer, _) = New();

            var state = diagrams.Get(new string('x', DiagramBlocks.MaxSourceLength + 1), true, null);

            Assert.Equal(ChatDiagramStatus.Failed, state.Status);
            Assert.Equal(DiagramText.TooLarge, state.Error);
            Assert.Null(state.Picture);
            Assert.Empty(renderer.Calls);

            Assert.Equal(ChatDiagramStatus.Drawing, diagrams.Get(new string('x', DiagramBlocks.MaxSourceLength), true, null).Status);
            Assert.Single(renderer.Calls);   // the limit itself is still drawn
        });

        [Fact]
        public void The_first_Get_starts_one_draw_and_ten_more_start_none() => UiThread.Run(() =>
        {
            var (diagrams, renderer, _) = New();

            Assert.Equal(ChatDiagramStatus.Drawing, diagrams.Get(Flow, true, null).Status);
            Assert.Single(renderer.Calls);

            for (int i = 0; i < 10; i++) Assert.Equal(ChatDiagramStatus.Drawing, diagrams.Get(Flow, true, () => { }).Status);

            Assert.Single(renderer.Calls);
        });

        [Fact]
        public void A_request_is_Mermaid_in_the_answers_theme_with_no_Kroki_server_and_a_slot_of_its_own() => UiThread.Run(() =>
        {
            var (diagrams, renderer, _) = New();

            diagrams.Get(Flow, true, null);
            diagrams.Get("pie\n  \"a\" : 1", false, null);

            var dark = renderer.Calls[0].Request;
            Assert.Same(DiagramKinds.FromWord("mermaid"), dark.Kind);
            Assert.Equal(DiagramEngine.Mermaid, dark.Kind.Engine);
            Assert.False(dark.Kind.NeedsKroki);
            Assert.Null(dark.KrokiServer);
            Assert.Equal(Flow, dark.Source);
            Assert.Equal(PadThemes.Dark, dark.Theme);
            Assert.True(dark.Dark);
            Assert.Equal(DiagramRequest.Css(AskPalette.Dark.Ink), dark.Foreground);
            Assert.Equal(DiagramRequest.Css(AskPalette.Dark.Background), dark.Background);

            var light = renderer.Calls[1].Request;
            Assert.Same(dark.Kind, light.Kind);
            Assert.Null(light.KrokiServer);
            Assert.Equal(PadThemes.Light, light.Theme);
            Assert.False(light.Dark);
            Assert.Equal(DiagramRequest.Css(AskPalette.Light.Ink), light.Foreground);
            Assert.Equal(DiagramRequest.Css(AskPalette.Light.Background), light.Background);

            // The renderer lets a newer request take the place of a waiting one with the same slot:
            // two diagrams of one answer must not cancel each other.
            Assert.NotSame(renderer.Calls[0].Slot, renderer.Calls[1].Slot);
            Assert.NotNull(renderer.Calls[0].Slot);
        });

        [Fact]
        public Task When_the_draw_ends_every_waiter_is_told_once_on_the_UI_thread_and_the_picture_is_kept_frozen() => UiThread.RunAsync(async () =>
        {
            var (diagrams, renderer, warnings) = New();
            int ui = Environment.CurrentManagedThreadId;
            var told = new List<(string Who, int Thread)>();
            Action first = () => told.Add(("first", Environment.CurrentManagedThreadId));
            Action second = () => told.Add(("second", Environment.CurrentManagedThreadId));

            diagrams.Get(Flow, true, first);
            diagrams.Get(Flow, true, null);                              // a caller that does not wait is fine
            for (int i = 0; i < 10; i++) diagrams.Get(Flow, true, second);   // ten rebuilds of one view give the same waiter ten times
            Assert.Empty(told);                                          // nothing is told before the draw ends

            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramFakes.Picture(width: 100, height: 50));

            Assert.Equal(new[] { ("first", ui), ("second", ui) }, told);

            var state = diagrams.Get(Flow, true, () => told.Add(("late", 0)));
            Assert.Equal(ChatDiagramStatus.Drawn, state.Status);
            var picture = Assert.IsAssignableFrom<BitmapSource>(state.Picture);
            Assert.True(picture.IsFrozen);
            Assert.Equal(100, state.Width);
            Assert.Equal(50, state.Height);
            Assert.Null(state.Error);

            // Decoded once: every later Get hands out the same bitmap, starts no draw and tells nobody.
            for (int i = 0; i < 10; i++) Assert.Same(picture, diagrams.Get(Flow, true, second).Picture);
            Assert.Single(renderer.Calls);
            Assert.Equal(2, told.Count);
            Assert.Empty(warnings);
        });

        /// <summary>What it is, the renderer's result, the message shown, and whether drawing it again may help.</summary>
        public static IEnumerable<object[]> Failures() => new[]
        {
            new object[] { "a syntax error", DiagramResult.Failure("Parse error on line 2", lasting: true), "Parse error on line 2", false },
            new object[] { "a draw that took too long", DiagramResult.Failure(DiagramText.TookTooLong, lasting: false), DiagramText.TookTooLong, true },
            new object[] { "an engine that stopped", DiagramResult.Failure(DiagramText.EngineStopped, lasting: false), DiagramText.EngineStopped, true },
            new object[] { "a missing runtime", DiagramResult.Failure(DiagramText.RuntimeMissing, lasting: false, DiagramText.RuntimeDownload), DiagramText.RuntimeMissing, true },
            new object[] { "a draw another took the place of", DiagramResult.Replaced, DiagramText.Failed, true },
        };

        [Theory]
        [MemberData(nameof(Failures))]
        public Task A_failure_of_any_kind_is_remembered_and_never_drawn_again(string what, DiagramResult failure, string message, bool canRetry) => UiThread.RunAsync(async () =>
        {
            var (diagrams, renderer, _) = New();
            int told = 0;
            diagrams.Get(Flow, true, () => told++);

            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, failure);

            Assert.Equal(1, told);
            for (int i = 0; i < 50; i++)
            {
                var state = diagrams.Get(Flow, true, () => told++);
                Assert.True(state.Status == ChatDiagramStatus.Failed, what + " is a failure");
                Assert.Equal(message, state.Error);
                Assert.Null(state.Picture);
                Assert.True(state.CanRetry == canRetry, what + (canRetry ? " may pass: it can be tried again" : " would fail the same way again"));
            }
            Assert.Single(renderer.Calls);   // what stops draw, fail, redraw, draw from going round for ever
            Assert.Equal(1, told);
        });

        [Fact]
        public Task Forget_drops_that_one_outcome_so_the_next_Get_starts_exactly_one_new_draw() => UiThread.RunAsync(async () =>
        {
            var (diagrams, renderer, _) = New();
            var passing = DiagramResult.Failure(DiagramText.TookTooLong, lasting: false);
            diagrams.Get(Flow, true, null);
            diagrams.Get(Flow, false, null);
            diagrams.Get("pie", true, null);
            for (int i = 0; i < 3; i++) await ChatDiagramFakes.FinishAsync(diagrams, renderer, i, passing);
            Assert.Equal(3, diagrams.FailuresKept);

            diagrams.Forget(Flow, true);

            // That key only: the same source in the other theme and the other source are still kept.
            Assert.Equal(2, diagrams.FailuresKept);
            Assert.Equal(ChatDiagramStatus.Failed, diagrams.Get(Flow, false, null).Status);
            Assert.Equal(ChatDiagramStatus.Failed, diagrams.Get("pie", true, null).Status);
            Assert.Equal(3, renderer.Calls.Count);   // forgetting draws nothing

            for (int i = 0; i < 10; i++) Assert.Equal(ChatDiagramStatus.Drawing, diagrams.Get(Flow, true, null).Status);
            Assert.Equal(4, renderer.Calls.Count);   // one draw for one press, however often the view is built
            Assert.Equal(Flow, renderer.Calls[3].Request.Source);
            Assert.True(renderer.Calls[3].Request.Dark);

            // It fails again: kept again, offered again, and nothing is drawn until the next press.
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 3, passing);
            for (int i = 0; i < 10; i++)
            {
                var again = diagrams.Get(Flow, true, null);
                Assert.Equal(ChatDiagramStatus.Failed, again.Status);
                Assert.True(again.CanRetry);
            }
            Assert.Equal(4, renderer.Calls.Count);

            diagrams.Forget(Flow, true);
            diagrams.Get(Flow, true, null);
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 4, DiagramFakes.Picture());
            Assert.Equal(ChatDiagramStatus.Drawn, diagrams.Get(Flow, true, null).Status);   // the engine was up this time
            Assert.Equal(5, renderer.Calls.Count);
        });

        [Fact]
        public Task Forget_leaves_a_draw_that_is_still_running_alone_and_never_throws() => UiThread.RunAsync(async () =>
        {
            var (diagrams, renderer, warnings) = New();
            int told = 0;
            diagrams.Get(Flow, true, () => told++);

            diagrams.Forget(Flow, true);                                              // nothing has ended yet: nothing to forget
            diagrams.Forget("never asked for", false);
            diagrams.Forget(new string('x', DiagramBlocks.MaxSourceLength + 1), true);

            Assert.Equal(ChatDiagramStatus.Drawing, diagrams.Get(Flow, true, null).Status);
            Assert.Single(renderer.Calls);                                            // the draw goes on; no second one
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramFakes.Picture());
            Assert.Equal(1, told);
            Assert.Equal(ChatDiagramStatus.Drawn, diagrams.Get(Flow, true, null).Status);
            Assert.Empty(warnings);

            diagrams.Forget(Flow, true);                                              // a picture can be forgotten too
            Assert.Equal(0, diagrams.PicturesKept);
        });

        [Fact]
        public void What_the_adapter_makes_of_its_own_failures_says_whether_trying_again_can_help() => UiThread.Run(() =>
        {
            // Too large, a picture without a size, a picture that cannot be decoded: the same source gives the same again.
            var (diagrams, _, _) = New();
            Assert.False(diagrams.Get(new string('x', DiagramBlocks.MaxSourceLength + 1), true, null).CanRetry);
            var immediate = new ImmediateRenderer { Answer = _ => DiagramResult.Picture(DiagramFakes.Png, DiagramFakes.Svg, 0, 0, paper: false) };
            Assert.False(new ChatDiagrams(() => immediate, () => true).Get(Flow, true, null).CanRetry);
            immediate.Answer = _ => DiagramResult.Picture(new byte[] { 1, 2, 3 }, DiagramFakes.Svg, 100, 50, paper: false);
            Assert.False(new ChatDiagrams(() => immediate, () => true) { Warn = _ => { } }.Get(Flow, true, null).CanRetry);

            // Something threw: it may well work the next time.
            var thrown = new ChatDiagrams(() => new ThrowingRenderer(), () => true) { Warn = _ => { } }.Get(Flow, true, null);
            Assert.Equal(ChatDiagramStatus.Failed, thrown.Status);
            Assert.True(thrown.CanRetry);
            Assert.True(new ChatDiagrams(() => throw new NotSupportedException("y"), () => true) { Warn = _ => { } }.Get(Flow, true, null).CanRetry);

            // A picture, a draw in progress and "off" are not failures.
            immediate.Answer = _ => DiagramFakes.Picture();
            Assert.False(new ChatDiagrams(() => immediate, () => true).Get(Flow, true, null).CanRetry);
            Assert.False(diagrams.Get(Flow, true, null).CanRetry);
            Assert.False(new ChatDiagrams(() => null, () => true).Get(Flow, true, null).CanRetry);
        });

        [Fact]
        public void A_renderer_that_was_disposed_gives_a_failure_that_can_be_tried_again_and_does_not_throw() => UiThread.Run(() =>
        {
            var page = new FakePage();
            var renderer = new DiagramRenderer(() => Task.FromResult<IDiagramPage>(page));
            renderer.Dispose();   // as at exit, with an answer still on screen
            var diagrams = new ChatDiagrams(() => renderer, () => true) { Warn = _ => { } };
            int told = 0;

            var state = diagrams.Get(Flow, true, () => told++);

            Assert.Equal(ChatDiagramStatus.Failed, state.Status);
            Assert.Equal(DiagramText.Failed, state.Error);
            Assert.True(state.CanRetry);
            Assert.Empty(page.Requests);
            Assert.Equal(ChatDiagramStatus.Failed, diagrams.Get(Flow, true, () => told++).Status);   // kept: not asked for again by itself
            Assert.Equal(0, told);
        });

        [Fact]
        public Task Clear_forgets_the_pictures_and_the_failures() => UiThread.RunAsync(async () =>
        {
            var (diagrams, renderer, _) = New();
            diagrams.Get(Flow, true, null);
            diagrams.Get("pie", true, null);
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramFakes.Picture());
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 1, DiagramResult.Failure(DiagramText.TookTooLong, lasting: false));
            var before = diagrams.Get(Flow, true, null).Picture;
            Assert.NotNull(before);
            Assert.Equal(ChatDiagramStatus.Failed, diagrams.Get("pie", true, null).Status);
            Assert.Equal(2, renderer.Calls.Count);

            diagrams.Clear();

            // Nothing is left of either, here or in the engine's own store: both are drawn again.
            Assert.Equal(0, diagrams.PicturesKept);
            Assert.Equal(0, diagrams.FailuresKept);
            Assert.Empty(renderer.Cache);
            Assert.Equal(ChatDiagramStatus.Drawing, diagrams.Get("pie", true, null).Status);
            Assert.Equal(3, renderer.Calls.Count);
            Assert.Equal(ChatDiagramStatus.Drawing, diagrams.Get(Flow, true, null).Status);
            Assert.Equal(4, renderer.Calls.Count);
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 3, DiagramFakes.Picture());
            var after = diagrams.Get(Flow, true, null);
            Assert.Equal(ChatDiagramStatus.Drawn, after.Status);
            Assert.NotSame(before, after.Picture);
        });

        // ---- the drawing engine forgets with it (the credential rule) ------------------------------

        private static string KeyOf(string source, bool dark = true) =>
            DiagramFakes.Request(source: source, theme: dark ? PadThemes.Dark : PadThemes.Light).Key;

        [Fact]
        public Task Clear_answers_the_draws_still_waiting_in_the_engine_and_the_one_being_drawn_is_kept_nowhere() => UiThread.RunAsync(async () =>
        {
            // The real queue, over a page the test answers by hand: the first of three diagrams is
            // being drawn (slowly) and two wait behind it, each holding its source.
            var page = new FakePage();
            using var renderer = new DiagramRenderer(() => Task.FromResult<IDiagramPage>(page));
            var diagrams = new ChatDiagrams(() => renderer, () => true) { Warn = _ => { } };
            string[] sources = { "pie\n  \"hunter2\" : 1", "pie\n  \"hunter2\" : 2", "pie\n  \"hunter2\" : 3" };
            var told = new List<string>();
            foreach (string source in sources) Assert.Equal(ChatDiagramStatus.Drawing, diagrams.Get(source, true, () => told.Add(source)).Status);
            await ChatDiagramFakes.Until(() => page.Requests.Count == 1, "the first draw to reach the page");

            diagrams.Clear();   // a credential was stored: it comes back without waiting for the page

            // The two that waited are answered at once, while the page still draws the first.
            await ChatDiagramFakes.Until(() => told.Count == 2, "the two waiting draws to be answered");
            Assert.Equal(new[] { sources[1], sources[2] }, told.OrderBy(s => s, StringComparer.Ordinal));
            Assert.Single(page.Requests);

            page.Finish(DiagramFakes.Drawn());
            await ChatDiagramFakes.Until(() => told.Count == 3, "the draw that was running to end");
            await Task.Delay(50);

            Assert.Equal(sources[0], Assert.Single(page.Requests).Source);   // the two that waited never reached the page
            Assert.All(sources, source => Assert.False(renderer.TryGetCached(KeyOf(source), out _)));
            Assert.Equal(0, renderer.CachedCount);                           // the one that ran is not in the engine's store
            Assert.Equal(0, diagrams.PicturesKept);                          // nor kept here
            Assert.Equal(0, diagrams.FailuresKept);
        });

        [Fact]
        public void After_Clear_the_engine_holds_nothing_the_adapter_asked_for_not_even_a_picture_the_adapter_had_dropped() => UiThread.Run(() =>
        {
            var page = new FakePage { Answer = _ => DiagramFakes.Drawn() };
            using var renderer = new DiagramRenderer(() => Task.FromResult<IDiagramPage>(page));
            BitmapSource heavy = ChatDiagramFakes.Counted(1600, 2400);   // counts as 15 MB: the ninth pushes the first out of the adapter
            var diagrams = new ChatDiagrams(() => renderer, () => true) { Warn = _ => { }, Decoder = (_, _, _) => heavy };

            // A note of the owner's drew two diagrams of its own; an answer shows the first of them too.
            var notesOwn = DiagramFakes.Request(source: "a note's own diagram");
            var shared = DiagramFakes.Request(source: "in a note and in an answer");
            Assert.True(renderer.RenderAsync(notesOwn, new object()).IsCompleted);
            Assert.True(renderer.RenderAsync(shared, new object()).IsCompleted);
            Assert.Equal(ChatDiagramStatus.Drawn, diagrams.Get(shared.Source, true, null).Status);   // found in the engine: no draw
            Assert.Equal(2, page.Requests.Count);

            for (int i = 0; i < 9; i++) Assert.Equal(ChatDiagramStatus.Drawn, diagrams.Get(Source(i), true, null).Status);
            Assert.Equal(8, diagrams.PicturesKept);                              // the shared one and number 0 were dropped here
            Assert.Equal(11, renderer.CachedCount);
            Assert.True(renderer.TryGetCached(KeyOf(Source(0)), out _));         // but the engine still has them

            diagrams.Clear();

            for (int i = 0; i < 9; i++) Assert.False(renderer.TryGetCached(KeyOf(Source(i)), out _), "diagram " + i.ToString(CultureInfo.InvariantCulture));
            Assert.False(renderer.TryGetCached(shared.Key, out _));              // the note draws it again when it next needs it
            Assert.True(renderer.TryGetCached(notesOwn.Key, out _));             // never in an answer: not the adapter's to forget
            Assert.Equal(1, renderer.CachedCount);

            // The set of what was asked for went with it: a second Clear asks the engine nothing.
            Assert.True(renderer.RenderAsync(shared, new object()).IsCompleted);
            diagrams.Clear();
            Assert.True(renderer.TryGetCached(shared.Key, out _));
        });

        [Fact]
        public void Try_again_forgets_the_engines_stored_result_for_that_key_only() => UiThread.Run(() =>
        {
            var page = new FakePage { Answer = _ => new PageDrawing(null, null, 0, 0, "Parse error on line 2") };
            using var renderer = new DiagramRenderer(() => Task.FromResult<IDiagramPage>(page));
            var diagrams = new ChatDiagrams(() => renderer, () => true) { Warn = _ => { } };
            Assert.Equal(ChatDiagramStatus.Failed, diagrams.Get(Flow, true, null).Status);
            Assert.Equal(ChatDiagramStatus.Failed, diagrams.Get(Flow, false, null).Status);
            Assert.Equal(ChatDiagramStatus.Failed, diagrams.Get("pie", true, null).Status);
            Assert.Equal(3, renderer.CachedCount);   // a syntax error lasts: the engine stores it

            diagrams.Forget(Flow, true);

            Assert.False(renderer.TryGetCached(KeyOf(Flow), out _));
            Assert.True(renderer.TryGetCached(KeyOf(Flow, dark: false), out _));
            Assert.True(renderer.TryGetCached(KeyOf("pie"), out _));
            Assert.Equal(2, diagrams.FailuresKept);

            Assert.Equal(ChatDiagramStatus.Failed, diagrams.Get(Flow, true, null).Status);
            Assert.Equal(4, page.Requests.Count);    // drawn again by the page, not handed back from the store
            Assert.Equal(ChatDiagramStatus.Failed, diagrams.Get("pie", true, null).Status);
            Assert.Equal(4, page.Requests.Count);
        });

        [Fact]
        public Task Try_again_leaves_a_draw_of_that_diagram_that_is_under_way_in_the_engine_alone() => UiThread.RunAsync(async () =>
        {
            var page = new FakePage();
            using var renderer = new DiagramRenderer(() => Task.FromResult<IDiagramPage>(page));
            var diagrams = new ChatDiagrams(() => renderer, () => true) { Warn = _ => { } };
            int told = 0;
            diagrams.Get(Flow, true, () => told++);
            diagrams.Get("pie", true, () => told++);              // waits behind it
            await ChatDiagramFakes.Until(() => page.Requests.Count == 1, "the draw to reach the page");

            diagrams.Forget(Flow, true);                          // a second answer's Try again, while the first one's draw runs
            diagrams.Forget("pie", true);

            page.Finish(DiagramFakes.Drawn());
            await ChatDiagramFakes.Until(() => page.Requests.Count == 2, "the waiting draw to reach the page");
            page.Finish(DiagramFakes.Drawn());
            await ChatDiagramFakes.Until(() => told == 2, "both draws to end");
            Assert.Equal(ChatDiagramStatus.Drawn, diagrams.Get(Flow, true, null).Status);
            Assert.Equal(ChatDiagramStatus.Drawn, diagrams.Get("pie", true, null).Status);
            Assert.Equal(2, page.Requests.Count);
        });

        [Fact]
        public void More_than_256_diagrams_since_the_last_Clear_are_forgotten_by_the_engine_as_they_leave_the_set() => UiThread.Run(() =>
        {
            // Only hashes are remembered, and no more than 256 of them. The one pushed out is
            // forgotten by the engine there and then, so a Clear never leaves one behind.
            Assert.Equal(256, ChatDiagrams.MaxRemembered);
            var renderer = new FakeRenderer();
            var diagrams = new ChatDiagrams(() => renderer, () => true) { Warn = _ => { } };
            for (int i = 0; i < ChatDiagrams.MaxRemembered; i++) diagrams.Get(Source(i), true, null);
            Assert.Equal(ChatDiagrams.MaxRemembered, diagrams.Remembered);
            Assert.Empty(renderer.Forgotten);

            diagrams.Get(Source(ChatDiagrams.MaxRemembered), true, null);

            Assert.Equal(ChatDiagrams.MaxRemembered, diagrams.Remembered);
            var first = Assert.Single(renderer.Forgotten);
            Assert.Equal(renderer.Calls[0].Request.Key, first.Key);
            Assert.Same(renderer.Calls[0].Slot, first.Slot);

            diagrams.Clear();

            Assert.Equal(0, diagrams.Remembered);
            Assert.Equal(ChatDiagrams.MaxRemembered + 1, renderer.Forgotten.Count);
            Assert.Equal(renderer.Calls.Select(c => c.Request.Key).OrderBy(k => k, StringComparer.Ordinal),
                         renderer.Forgotten.Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal));
            Assert.All(renderer.Forgotten, f => Assert.Equal(64, f.Key.Length));   // a SHA-256 in hex: never a source
        });

        [Fact]
        public void Clear_and_Try_again_do_not_throw_when_the_engine_cannot_forget_and_say_so_by_the_type_only() => UiThread.Run(() =>
        {
            var warnings = new List<string>();
            var diagrams = new ChatDiagrams(() => new ThrowingRenderer(), () => true) { Warn = warnings.Add };
            Assert.Equal(ChatDiagramStatus.Failed, diagrams.Get(Flow, true, null).Status);
            warnings.Clear();

            diagrams.Forget(Flow, true);
            diagrams.Clear();

            Assert.Equal(2, warnings.Count);
            Assert.All(warnings, warning =>
            {
                Assert.Contains("InvalidOperationException", warning, StringComparison.Ordinal);
                Assert.DoesNotContain("the key was", warning, StringComparison.Ordinal);
            });
            Assert.Equal(0, diagrams.FailuresKept);
            Assert.Equal(0, diagrams.Remembered);
        });

        [Fact]
        public void Clear_with_nothing_asked_for_does_not_even_make_the_engine() => UiThread.Run(() =>
        {
            int made = 0;
            var diagrams = new ChatDiagrams(() =>
            {
                made++;
                return new FakeRenderer();
            }, () => false);
            diagrams.Get(Flow, true, null);   // the setting is off: nothing is asked of the engine

            diagrams.Clear();
            diagrams.Forget(Flow, true);

            Assert.Equal(0, made);            // the app makes its engine (a browser page) on first use
        });

        [Fact]
        public Task A_draw_that_ends_after_Clear_is_not_kept_and_its_waiters_are_still_told_once() => UiThread.RunAsync(async () =>
        {
            var (diagrams, renderer, _) = New();
            int told = 0;
            diagrams.Get(Flow, true, () => told++);

            diagrams.Clear();
            renderer.Finish(0, DiagramResult.Failure(DiagramText.TookTooLong, lasting: false));
            await ChatDiagramFakes.Until(() => told > 0, "the waiter of a draw that was cleared");

            Assert.Equal(1, told);
            Assert.Single(renderer.Calls);
            Assert.Equal(ChatDiagramStatus.Drawing, diagrams.Get(Flow, true, null).Status);   // not remembered: asked for again
            Assert.Equal(2, renderer.Calls.Count);
        });

        [Fact]
        public Task Dark_and_light_are_drawn_and_kept_apart() => UiThread.RunAsync(async () =>
        {
            var (diagrams, renderer, _) = New();

            diagrams.Get(Flow, true, null);
            diagrams.Get(Flow, false, null);
            Assert.Equal(2, renderer.Calls.Count);
            Assert.NotEqual(renderer.Calls[0].Request.Key, renderer.Calls[1].Request.Key);

            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramFakes.Picture());

            Assert.Equal(ChatDiagramStatus.Drawn, diagrams.Get(Flow, true, null).Status);
            Assert.Equal(ChatDiagramStatus.Drawing, diagrams.Get(Flow, false, null).Status);

            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 1, DiagramResult.Failure("no", lasting: true));

            Assert.Equal(ChatDiagramStatus.Drawn, diagrams.Get(Flow, true, null).Status);
            Assert.Equal(ChatDiagramStatus.Failed, diagrams.Get(Flow, false, null).Status);
            Assert.Equal(2, renderer.Calls.Count);
        });

        private sealed class ThrowingRenderer : IDiagramRenderer
        {
            public int Calls { get; private set; }

            public bool TryGetCached(string key, [MaybeNullWhen(false)] out DiagramResult result)
            {
                result = null;
                return false;
            }

            public Task<DiagramResult> RenderAsync(DiagramRequest request, object slot)
            {
                Calls++;
                throw new InvalidOperationException("the source was " + request.Source);
            }

            public void Forget(string key, object? slot) => throw new InvalidOperationException("the key was " + key);
        }

        [Fact]
        public void A_renderer_that_throws_gives_a_failure_that_is_kept_and_logged_by_its_type_only() => UiThread.Run(() =>
        {
            var renderer = new ThrowingRenderer();
            var warnings = new List<string>();
            var diagrams = new ChatDiagrams(() => renderer, () => true) { Warn = warnings.Add };

            var state = diagrams.Get(Flow, true, () => { });

            Assert.Equal(ChatDiagramStatus.Failed, state.Status);
            Assert.Equal(DiagramText.Failed, state.Error);
            for (int i = 0; i < 10; i++) Assert.Equal(ChatDiagramStatus.Failed, diagrams.Get(Flow, true, () => { }).Status);
            Assert.Equal(1, renderer.Calls);
            string warning = Assert.Single(warnings);
            Assert.Contains("InvalidOperationException", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("flowchart", warning, StringComparison.Ordinal);   // never the message: it could quote the source
        });

        [Fact]
        public Task A_draw_whose_task_fails_is_a_failure_too() => UiThread.RunAsync(async () =>
        {
            var (diagrams, renderer, warnings) = New();
            int told = 0;
            diagrams.Get(Flow, true, () => told++);

            renderer.Calls[0].Done.TrySetException(new InvalidOperationException("the source was " + Flow));
            await ChatDiagramFakes.Until(() => told > 0, "the waiter of a failed draw");

            var state = diagrams.Get(Flow, true, null);
            Assert.Equal(ChatDiagramStatus.Failed, state.Status);
            Assert.Equal(DiagramText.Failed, state.Error);
            Assert.Single(renderer.Calls);
            string warning = Assert.Single(warnings);
            Assert.Contains("InvalidOperationException", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("flowchart", warning, StringComparison.Ordinal);
        });

        [Fact]
        public void A_setting_or_a_renderer_that_cannot_be_read_gives_a_failure_and_does_not_throw() => UiThread.Run(() =>
        {
            var warnings = new List<string>();
            var noSetting = new ChatDiagrams(() => new FakeRenderer(), () => throw new InvalidOperationException("x")) { Warn = warnings.Add };
            var noRenderer = new ChatDiagrams(() => throw new NotSupportedException("y"), () => true) { Warn = warnings.Add };

            Assert.Equal(ChatDiagramStatus.Failed, noSetting.Get(Flow, true, null).Status);
            Assert.Equal(DiagramText.Failed, noRenderer.Get(Flow, true, null).Error);

            Assert.Equal(2, warnings.Count);
            Assert.Contains("InvalidOperationException", warnings[0], StringComparison.Ordinal);
            Assert.Contains("NotSupportedException", warnings[1], StringComparison.Ordinal);

            // An answer is built ten times a second while it streams: each adapter says it once, not at every build.
            for (int i = 0; i < 20; i++)
            {
                Assert.Equal(ChatDiagramStatus.Failed, noSetting.Get(Flow, true, null).Status);
                Assert.Equal(ChatDiagramStatus.Failed, noRenderer.Get("pie", false, null).Status);
            }
            Assert.Equal(2, warnings.Count);
        });

        [Fact]
        public void A_picture_the_engine_already_has_is_shown_at_once_without_a_draw() => UiThread.Run(() =>
        {
            var (diagrams, renderer, _) = New();
            renderer.Cache[DiagramFakes.Request(source: Flow, theme: PadThemes.Dark).Key] = DiagramFakes.Picture(width: 80, height: 40);
            int told = 0;

            var state = diagrams.Get(Flow, true, () => told++);

            Assert.Equal(ChatDiagramStatus.Drawn, state.Status);
            Assert.Equal(80, state.Width);
            Assert.Empty(renderer.Calls);
            Assert.Same(state.Picture, diagrams.Get(Flow, true, () => told++).Picture);
            Assert.Equal(0, told);
        });

        [Fact]
        public void A_draw_that_is_answered_at_once_is_what_the_same_Get_returns_and_nobody_is_told() => UiThread.Run(() =>
        {
            var renderer = new ImmediateRenderer();
            var diagrams = new ChatDiagrams(() => renderer, () => true);
            int told = 0;

            var state = diagrams.Get(Flow, true, () => told++);

            Assert.Equal(ChatDiagramStatus.Drawn, state.Status);
            Assert.Same(state.Picture, diagrams.Get(Flow, true, () => told++).Picture);
            Assert.Single(renderer.Requests);
            Assert.Equal(0, told);   // the caller is still building its document: it has the answer already
        });

        // ---- memory: how large a picture is decoded, and what is kept ------------------------------

        private static string Source(int i) => "pie\n  \"a\" : " + i.ToString(CultureInfo.InvariantCulture);

        /// <summary>What the adapter keeps for a PNG, for a diagram of that size, at a display scaling.</summary>
        private static ChatDiagramState DrawnAt(byte[] png, double width, double height, Func<double>? scale)
        {
            var renderer = new ImmediateRenderer { Answer = _ => DiagramResult.Picture(png, DiagramFakes.Svg, width, height, paper: false) };
            var diagrams = new ChatDiagrams(() => renderer, () => true, scale) { Warn = _ => { } };
            var state = diagrams.Get(Flow, true, null);
            Assert.Equal(ChatDiagramStatus.Drawn, state.Status);
            Assert.True(state.Picture!.IsFrozen);
            return state;
        }

        private static (int Width, int Height) PixelsOf(ChatDiagramState state)
        {
            var bitmap = Assert.IsAssignableFrom<BitmapSource>(state.Picture);
            return (bitmap.PixelWidth, bitmap.PixelHeight);
        }

        [Theory]
        [InlineData(1.0)]
        [InlineData(2.0)]
        public void A_4000_by_3000_png_is_decoded_no_larger_than_the_cap_and_is_shown_as_large_as_before(double scale) => UiThread.Run(() =>
        {
            byte[] png = ChatDiagramFakes.Png(4000, 3000);   // a diagram of 2,000 by 1,500, drawn with twice as many pixels

            var state = DrawnAt(png, 2000, 1500, () => scale);

            Assert.Equal((1600, 1200), PixelsOf(state));     // 7.68 MB, not the 48 MB of the whole PNG
            Assert.Equal(2000, state.Width);
            Assert.Equal(1500, state.Height);
        });

        [Fact]
        public void A_tall_png_is_decoded_no_higher_than_the_cap() => UiThread.Run(() =>
        {
            byte[] png = ChatDiagramFakes.Png(2000, 4000);   // a diagram of 1,000 by 2,000

            Assert.Equal((1000, 2000), PixelsOf(DrawnAt(png, 1000, 2000, () => 1.0)));
            var doubled = DrawnAt(png, 1000, 2000, () => 2.0);
            Assert.Equal((1200, 2400), PixelsOf(doubled));   // 1,600 wide would be 3,200 high
            Assert.Equal(1000, doubled.Width);
            Assert.Equal(2000, doubled.Height);
        });

        [Theory]
        [InlineData(1.0, 400, 300)]
        [InlineData(1.5, 600, 450)]
        [InlineData(2.0, 800, 600)]
        [InlineData(3.0, 1200, 900)]
        [InlineData(10.0, 1200, 900)]                     // capped at 3
        [InlineData(0.5, 400, 300)]                       // below 1 counts as 1: never fewer pixels than the diagram's size
        [InlineData(-2.0, 400, 300)]
        [InlineData(double.NaN, 400, 300)]                // not finite counts as 1
        [InlineData(double.PositiveInfinity, 400, 300)]
        public void A_picture_is_decoded_with_as_many_pixels_as_the_display_scaling_needs(double scale, int width, int height) => UiThread.Run(() =>
        {
            byte[] png = ChatDiagramFakes.Png(2000, 1500);   // more pixels than any scaling asks for

            var state = DrawnAt(png, 400, 300, () => scale);

            Assert.Equal((width, height), PixelsOf(state));
            Assert.Equal(400, state.Width);                  // the shown size does not follow the scaling
            Assert.Equal(300, state.Height);
        });

        [Fact]
        public void Without_a_scaling_or_with_one_that_cannot_be_read_a_picture_is_decoded_at_its_own_size() => UiThread.Run(() =>
        {
            byte[] png = ChatDiagramFakes.Png(2000, 1500);

            Assert.Equal((400, 300), PixelsOf(DrawnAt(png, 400, 300, null)));
            Assert.Equal((400, 300), PixelsOf(DrawnAt(png, 400, 300, () => throw new InvalidOperationException("no screen"))));
        });

        [Fact]
        public void A_picture_is_never_decoded_with_more_pixels_than_its_png_has() => UiThread.Run(() =>
        {
            byte[] png = ChatDiagramFakes.Png(800, 600);     // as the engine draws a 400 by 300 diagram

            Assert.Equal((800, 600), PixelsOf(DrawnAt(png, 400, 300, () => 3.0)));
            Assert.Equal((800, 600), PixelsOf(DrawnAt(png, 400, 300, () => 2.0)));
            Assert.Equal((400, 300), PixelsOf(DrawnAt(png, 400, 300, () => 1.0)));
        });

        /// <summary>
        /// A cap can ask the decoder for a side of less than one pixel (a 4,000 by 1 picture at
        /// 1,600 wide is 0.4 high). The decoder gives one pixel there and does not throw, so a very
        /// thin picture is drawn like any other, within the caps.
        /// </summary>
        [Theory]
        [InlineData(1, 3000, 0.5, 1500.0, 1, 2400)]     // a hair of a line, higher than the height cap
        [InlineData(2, 4000, 1.0, 2000.0, 1, 2000)]
        [InlineData(4000, 1, 2000.0, 0.5, 1600, 1)]     // wider than the width cap, one pixel high
        [InlineData(4000, 2, 2000.0, 1.0, 1600, 1)]
        public void A_very_thin_picture_is_still_drawn_within_the_caps_with_its_thin_side_one_pixel(
            int pngWidth, int pngHeight, double width, double height, int decodedWidth, int decodedHeight) => UiThread.Run(() =>
        {
            byte[] png = ChatDiagramFakes.Png(pngWidth, pngHeight);

            var state = DrawnAt(png, width, height, () => 1.0);   // DrawnAt checks it is drawn, not a failure

            Assert.Equal((decodedWidth, decodedHeight), PixelsOf(state));
            Assert.Equal(width, state.Width);
            Assert.Equal(height, state.Height);
        });

        [Fact]
        public Task The_scaling_is_read_when_a_picture_is_decoded() => UiThread.RunAsync(async () =>
        {
            double scale = 1.0;
            var renderer = new FakeRenderer();
            var diagrams = new ChatDiagrams(() => renderer, () => true, () => scale) { Warn = _ => { } };
            diagrams.Get(Flow, true, null);
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramResult.Picture(ChatDiagramFakes.Png(2000, 1500), DiagramFakes.Svg, 400, 300, paper: false));
            Assert.Equal((400, 300), PixelsOf(diagrams.Get(Flow, true, null)));

            scale = 2.0;
            Assert.Equal((400, 300), PixelsOf(diagrams.Get(Flow, true, null)));   // kept as it was decoded
            diagrams.Clear();                                                     // forgotten, here and in the engine: it is drawn again
            Assert.Equal(ChatDiagramStatus.Drawing, diagrams.Get(Flow, true, null).Status);
            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 1, DiagramResult.Picture(ChatDiagramFakes.Png(2000, 1500), DiagramFakes.Svg, 400, 300, paper: false));
            Assert.Equal((800, 600), PixelsOf(diagrams.Get(Flow, true, null)));   // and decoded for the scaling as it is now
        });

        [Fact]
        public void The_app_reads_the_primary_screens_scaling_as_a_number_from_100_to_500_percent()
        {
            // What App hands the adapter. Asked of Windows itself: no window, no file, nothing shown.
            double scale = App.PrimaryScreenScale();

            Assert.True(double.IsFinite(scale));
            Assert.InRange(scale, 1.0, 5.0);
        }

        /// <summary>An adapter over a renderer that answers at once, whose decodes are counted and give a bitmap of that many pixels.</summary>
        private static (ChatDiagrams Diagrams, ImmediateRenderer Renderer, Func<int> Decodes) Counting(int pixelWidth, int pixelHeight)
        {
            var renderer = new ImmediateRenderer();
            int decodes = 0;
            BitmapSource picture = ChatDiagramFakes.Counted(pixelWidth, pixelHeight);
            var diagrams = new ChatDiagrams(() => renderer, () => true)
            {
                Warn = _ => { },
                Decoder = (_, _, _) =>
                {
                    decodes++;
                    return picture;
                },
            };
            return (diagrams, renderer, () => decodes);
        }

        [Fact]
        public void Of_nine_pictures_of_15_MB_the_eight_used_last_stay_and_a_rebuild_of_their_answer_decodes_and_draws_nothing() => UiThread.Run(() =>
        {
            const long each = 1600L * 2400 * 4;   // the largest a picture is decoded at: 15,360,000 bytes
            var (diagrams, renderer, decodes) = Counting(1600, 2400);

            for (int i = 0; i < 9; i++) Assert.Equal(ChatDiagramStatus.Drawn, diagrams.Get(Source(i), true, null).Status);

            Assert.Equal(8, ChatDiagrams.AlwaysKept);
            Assert.Equal(8, diagrams.PicturesKept);           // over 64 MB, and still eight: one answer never loses its own
            Assert.Equal(8 * each, diagrams.PictureBytes);
            Assert.True(diagrams.PictureBytes > ChatDiagrams.MaxPictureBytes);
            Assert.Equal(9, decodes());
            Assert.Equal(9, renderer.Requests.Count);

            // An answer that holds the eight used last is built again and again: no decode, no draw.
            for (int rebuild = 0; rebuild < 5; rebuild++)
                for (int i = 1; i < 9; i++) Assert.Equal(ChatDiagramStatus.Drawn, diagrams.Get(Source(i), true, null).Status);
            Assert.Equal(9, decodes());
            Assert.Equal(9, renderer.Requests.Count);

            diagrams.Get(Source(0), true, null);              // the first went, and is drawn and decoded again
            Assert.Equal(10, decodes());
            Assert.Equal(10, renderer.Requests.Count);
            Assert.Equal(8, diagrams.PicturesKept);
        });

        [Fact]
        public void Smaller_pictures_are_kept_up_to_64_MB_and_the_least_recently_used_goes_first() => UiThread.Run(() =>
        {
            const long each = 1000L * 1500 * 4;   // 6,000,000 bytes: eleven fit in 64 MB, twelve do not
            var (diagrams, renderer, decodes) = Counting(1000, 1500);
            Assert.Equal(64L * 1024 * 1024, ChatDiagrams.MaxPictureBytes);

            for (int i = 0; i < 11; i++) diagrams.Get(Source(i), true, null);
            Assert.Equal(11, diagrams.PicturesKept);
            Assert.Equal(11 * each, diagrams.PictureBytes);

            diagrams.Get(Source(0), true, null);              // used again: the oldest is now number 1
            diagrams.Get(Source(11), true, null);             // one more than fits

            Assert.Equal(11, diagrams.PicturesKept);
            Assert.Equal(11 * each, diagrams.PictureBytes);
            Assert.Equal(12, decodes());
            diagrams.Get(Source(0), true, null);
            for (int i = 2; i < 12; i++) diagrams.Get(Source(i), true, null);
            Assert.Equal(12, renderer.Requests.Count);        // all of those are still kept
            diagrams.Get(Source(1), true, null);
            Assert.Equal(13, renderer.Requests.Count);        // number 1 was dropped, and is asked for again

            diagrams.Clear();
            Assert.Equal(0, diagrams.PicturesKept);
            Assert.Equal(0, diagrams.PictureBytes);
        });

        [Fact]
        public void Pictures_do_not_push_out_a_failure_and_a_hundred_failures_do_not_push_out_a_picture() => UiThread.Run(() =>
        {
            var (diagrams, renderer, decodes) = Counting(1600, 2400);
            renderer.Answer = request => request.Source.StartsWith("no", StringComparison.Ordinal)
                ? DiagramResult.Failure("Parse error", lasting: true)
                : DiagramFakes.Picture();

            Assert.Equal(ChatDiagramStatus.Failed, diagrams.Get("no 0", true, null).Status);
            for (int i = 0; i < 20; i++) diagrams.Get(Source(i), true, null);
            Assert.Equal(21, renderer.Requests.Count);
            Assert.Equal(ChatDiagramStatus.Failed, diagrams.Get("no 0", true, null).Status);
            Assert.Equal(21, renderer.Requests.Count);        // twenty pictures later the failure is still kept: no new draw
            Assert.Equal(1, diagrams.FailuresKept);

            for (int i = 1; i <= 100; i++) diagrams.Get("no " + i.ToString(CultureInfo.InvariantCulture), true, null);
            Assert.Equal(121, renderer.Requests.Count);
            Assert.Equal(ChatDiagrams.MaxFailures, diagrams.FailuresKept);
            Assert.Equal(8, diagrams.PicturesKept);
            for (int i = 12; i < 20; i++) Assert.Equal(ChatDiagramStatus.Drawn, diagrams.Get(Source(i), true, null).Status);
            Assert.Equal(121, renderer.Requests.Count);       // a hundred failures later the pictures are still kept
            Assert.Equal(20, decodes());
        });

        [Fact]
        public void At_most_64_failures_are_kept_and_the_least_recently_used_goes_first() => UiThread.Run(() =>
        {
            var renderer = new ImmediateRenderer { Answer = _ => DiagramResult.Failure("no", lasting: true) };
            var diagrams = new ChatDiagrams(() => renderer, () => true);

            Assert.Equal(64, ChatDiagrams.MaxFailures);
            for (int i = 0; i < ChatDiagrams.MaxFailures; i++) diagrams.Get(Source(i), true, null);
            Assert.Equal(64, renderer.Requests.Count);

            diagrams.Get(Source(0), true, null);    // used again: the oldest is now number 1
            diagrams.Get(Source(100), true, null);  // one more than fits
            Assert.Equal(65, renderer.Requests.Count);
            Assert.Equal(64, diagrams.FailuresKept);

            diagrams.Get(Source(0), true, null);
            for (int i = 2; i < ChatDiagrams.MaxFailures; i++) diagrams.Get(Source(i), true, null);
            Assert.Equal(65, renderer.Requests.Count);   // all of those are still kept

            diagrams.Get(Source(1), true, null);
            Assert.Equal(66, renderer.Requests.Count);   // number 1 was dropped, and is asked for again
        });

        [Theory]
        [InlineData(0, 50)]
        [InlineData(100, 0)]
        [InlineData(double.NaN, 50)]
        [InlineData(100, double.NaN)]
        [InlineData(double.PositiveInfinity, 50)]
        [InlineData(-100, 50)]
        public void A_picture_without_a_size_is_a_failure_and_nothing_is_decoded(double width, double height) => UiThread.Run(() =>
        {
            var (diagrams, renderer, decodes) = Counting(200, 100);
            renderer.Answer = _ => DiagramResult.Picture(DiagramFakes.Png, DiagramFakes.Svg, width, height, paper: false);

            var state = diagrams.Get(Flow, true, null);

            Assert.Equal(ChatDiagramStatus.Failed, state.Status);
            Assert.Equal(DiagramText.Failed, state.Error);
            Assert.Null(state.Picture);
            Assert.Equal(0, decodes());
            Assert.Equal(ChatDiagramStatus.Failed, diagrams.Get(Flow, true, null).Status);
            Assert.Single(renderer.Requests);             // kept: the same result would come again
        });

        [Fact]
        public Task A_waiter_that_throws_does_not_stop_the_others() => UiThread.RunAsync(async () =>
        {
            var (diagrams, renderer, warnings) = New();
            int told = 0;
            diagrams.Get(Flow, true, () => throw new InvalidOperationException("the answer was secret"));
            diagrams.Get(Flow, true, () => told++);

            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramFakes.Picture());

            Assert.Equal(1, told);
            Assert.Equal(ChatDiagramStatus.Drawn, diagrams.Get(Flow, true, null).Status);
            string warning = Assert.Single(warnings);
            Assert.Contains("InvalidOperationException", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", warning, StringComparison.Ordinal);
        });

        [Fact]
        public Task A_picture_that_cannot_be_decoded_is_a_failure() => UiThread.RunAsync(async () =>
        {
            var (diagrams, renderer, warnings) = New();
            diagrams.Get(Flow, true, null);

            await ChatDiagramFakes.FinishAsync(diagrams, renderer, 0, DiagramResult.Picture(new byte[] { 1, 2, 3 }, DiagramFakes.Svg, 100, 50, paper: false));

            var state = diagrams.Get(Flow, true, null);
            Assert.Equal(ChatDiagramStatus.Failed, state.Status);
            Assert.Equal(DiagramText.Failed, state.Error);
            Assert.Single(warnings);
        });

        [Fact]
        public Task Over_the_real_renderer_three_diagrams_of_one_answer_are_all_drawn_and_none_takes_anothers_place() => UiThread.RunAsync(async () =>
        {
            // The real queue, over a page the test answers by hand: one draw runs and two wait. The
            // renderer drops a waiting draw when a newer one comes with the same slot.
            var page = new FakePage();
            using var renderer = new DiagramRenderer(() => Task.FromResult<IDiagramPage>(page));
            var diagrams = new ChatDiagrams(() => renderer, () => true) { Warn = _ => { } };
            string[] sources = { "pie\n  \"a\" : 1", "pie\n  \"b\" : 2", "pie\n  \"c\" : 3" };
            int told = 0;
            Action redraw = () => told++;

            foreach (string source in sources) Assert.Equal(ChatDiagramStatus.Drawing, diagrams.Get(source, true, redraw).Status);

            for (int drawn = 1; drawn <= sources.Length; drawn++)
            {
                await ChatDiagramFakes.Until(() => page.Requests.Count == drawn, "draw " + drawn.ToString(CultureInfo.InvariantCulture) + " to reach the page");
                page.Finish(DiagramFakes.Drawn());
            }
            await ChatDiagramFakes.Until(() => told == sources.Length, "all three draws to end");

            Assert.All(sources, source => Assert.Equal(ChatDiagramStatus.Drawn, diagrams.Get(source, true, redraw).Status));
            Assert.All(page.Requests, request =>
            {
                Assert.Equal("mermaid", request.Kind);
                Assert.True(request.Dark);
            });
            Assert.Equal(sources, page.Requests.Select(r => r.Source));

            // Each is kept: asking again draws nothing and tells nobody.
            Assert.Equal(3, page.Requests.Count);
            Assert.Equal(3, told);
            Assert.Equal(3, renderer.CachedCount);

            // Forgotten here, they are forgotten by the engine too: nothing drawn for an answer outlives it.
            diagrams.Clear();
            Assert.Equal(0, renderer.CachedCount);
            Assert.Equal(ChatDiagramStatus.Drawing, diagrams.Get(sources[0], true, redraw).Status);
            await ChatDiagramFakes.Until(() => page.Requests.Count == 4, "the draw after Clear to reach the page");
            page.Finish(DiagramFakes.Drawn());
            await ChatDiagramFakes.Until(() => told == 4, "the draw after Clear to end");
            Assert.Equal(ChatDiagramStatus.Drawn, diagrams.Get(sources[0], true, redraw).Status);
        });

        /// <summary>
        /// How the app wires answer diagrams cannot be run in a test (it is the running app), and
        /// every other test builds its own adapter: "always on" in place of the Draw diagrams
        /// setting, or an adapter set after the first MicaPad window was built (a window's answer
        /// boxes take <see cref="ChatDiagrams.Current"/> as they are made), would pass them all.
        /// So the wiring is read from the source, white space aside.
        /// </summary>
        [Fact]
        public void The_app_wires_answer_diagrams_to_the_Draw_diagrams_setting_before_any_MicaPad_window_and_disposes_the_renderer_at_exit()
        {
            static string Source(string name) => System.Text.RegularExpressions.Regex.Replace(
                File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), name)), @"\s+", " ");
            static int Count(string text, string part) => text.Split(part, StringSplitOptions.None).Length - 1;
            string ai = Source("App.Ai.cs");
            string app = Source("App.xaml.cs");

            // Set once, in StartAi: the renderer made on first use, the setting read at every call, the screen's scaling.
            const string wiring = "AppConfig diagramSettings = config.Config; Kil0bitSystemMonitor.Ai.ChatDiagrams.Current = "
                + "new Kil0bitSystemMonitor.Ai.ChatDiagrams(DiagramRendererOnFirstUse, () => diagramSettings.PadDiagrams, PrimaryScreenScale);";
            int startAi = ai.IndexOf("internal static void StartAi(", StringComparison.Ordinal);
            int afterStartAi = ai.IndexOf("internal static double PrimaryScreenScale()", StringComparison.Ordinal);
            int set = ai.IndexOf(wiring, StringComparison.Ordinal);
            Assert.True(startAi > 0 && afterStartAi > startAi, "StartAi, then the method after it");
            Assert.True(set > startAi && set < afterStartAi, "ChatDiagrams.Current is set in StartAi, from the Draw diagrams setting and the app's one renderer");
            Assert.Equal(1, Count(ai, "ChatDiagrams.Current =") + Count(app, "ChatDiagrams.Current ="));
            Assert.Equal(1, Count(ai, "new Kil0bitSystemMonitor.Ai.ChatDiagrams("));
            Assert.Equal(1, Count(ai, "PadDiagrams"));

            // Startup: StartAi comes before MicaPad is opened, at once (--pad) or once startup has settled.
            int startup = app.IndexOf("protected override void OnStartup(", StringComparison.Ordinal);
            int afterStartup = app.IndexOf("private void StartDiagnostics(", StringComparison.Ordinal);
            Assert.True(startup > 0 && afterStartup > startup, "OnStartup, then the method after it");
            string starting = app.Substring(startup, afterStartup - startup);
            // The hotkey's handler opens MicaPad when the key is pressed, which is no call at startup.
            Assert.Equal(1, Count(starting, "() => OpenPad(null)"));
            string calls = starting.Replace("() => OpenPad(null)", "", StringComparison.Ordinal);
            int started = calls.IndexOf("StartAi(config, m_telemetry, m_history, Dispatcher);", StringComparison.Ordinal);
            Assert.True(started > 0, "startup calls StartAi");
            Assert.Equal(1, Count(calls, "StartAi("));
            Assert.True(Count(calls, "OpenPad(") >= 1 && calls.IndexOf("OpenPad(", StringComparison.Ordinal) > started, "no MicaPad window is opened before StartAi");
            Assert.True(calls.IndexOf("ReopenPadIfItWasOpen(", StringComparison.Ordinal) > started, "nor is MicaPad brought back before it");

            // The one renderer, for MicaPad's windows and for answers; none is made once exit began.
            Assert.Contains("internal static Kil0bitSystemMonitor.Services.Pad.DiagramRenderer? DiagramRendererOnFirstUse() { if (s_diagrams != null || s_diagramsClosed) return s_diagrams;",
                            app, StringComparison.Ordinal);
            Assert.Equal(1, Count(app, "s_diagrams = new Kil0bitSystemMonitor.Services.Pad.DiagramRenderer("));

            // Exit: the closed flag first, so nothing makes a new renderer, then the renderer is disposed.
            int exit = app.IndexOf("protected override void OnExit(", StringComparison.Ordinal);
            int closed = app.IndexOf("s_diagramsClosed = true;", exit, StringComparison.Ordinal);
            int disposed = app.IndexOf("s_diagrams?.Dispose();", exit, StringComparison.Ordinal);
            Assert.True(exit > 0 && closed > exit, "OnExit sets the closed flag");
            Assert.True(disposed > closed, "and disposes the renderer after it");
            Assert.Equal(1, Count(app, "s_diagramsClosed = true;"));
        }

        [Fact]
        public Task A_draw_that_ends_on_another_thread_is_brought_back_to_the_UI_thread() => UiThread.RunAsync(async () =>
        {
            var (diagrams, renderer, _) = New();
            int ui = Environment.CurrentManagedThreadId;
            int toldOn = 0;
            diagrams.Get(Flow, true, () => toldOn = Environment.CurrentManagedThreadId);

            // The real renderer ends a draw from wherever its page answered.
            await Task.Run(() => renderer.Calls[0].Done.TrySetResult(DiagramFakes.Picture()));
            await ChatDiagramFakes.Until(() => toldOn != 0, "the waiter");

            Assert.Equal(ui, toldOn);
        });
    }
}
