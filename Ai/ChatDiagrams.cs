using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>Where the picture of one Mermaid block in an answer stands.</summary>
    internal enum ChatDiagramStatus
    {
        /// <summary>Diagrams are not drawn (the setting is off, or there is nothing to draw them with): the block is code.</summary>
        Off,

        /// <summary>The picture is being drawn.</summary>
        Drawing,

        /// <summary>The picture is there.</summary>
        Drawn,

        /// <summary>It could not be drawn; <see cref="ChatDiagramState.Error"/> says why.</summary>
        Failed,
    }

    /// <summary>
    /// The picture of one Mermaid block, or why there is none. <see cref="Width"/> and
    /// <see cref="Height"/> are the picture's own size in device-independent pixels: it is shown at
    /// most that large, however many pixels the bitmap has (the engine draws it with twice as many).
    /// </summary>
    internal sealed record ChatDiagramState(ChatDiagramStatus Status, ImageSource? Picture = null, double Width = 0, double Height = 0, string? Error = null);

    /// <summary>The pictures of the Mermaid blocks in AI answers: the app's <see cref="ChatDiagrams"/>, or a test's fake.</summary>
    internal interface IChatDiagrams
    {
        /// <summary>
        /// The state of the picture for this Mermaid source in this theme. Starts a draw when none was started.
        /// <paramref name="whenDone"/> is called once, on the UI thread, when a draw this call is waiting for ends.
        /// </summary>
        ChatDiagramState Get(string source, bool dark, Action? whenDone);

        /// <summary>Forgets every picture and every remembered failure.</summary>
        void Clear();
    }

    /// <summary>
    /// Gets the pictures of an answer's Mermaid blocks from MicaPad's drawing engine
    /// (<see cref="IDiagramRenderer"/>: a hidden browser page that loads only the app's bundled
    /// files), and tells whoever waits when one is ready.
    ///
    /// <para>
    /// An answer is built again from its whole text about ten times a second while it streams, so
    /// <see cref="Get"/> is asked for the same block over and over. It starts one draw for a
    /// source and a theme, and remembers how that draw ended: a picture, or a failure of any kind.
    /// A failure is never tried again until <see cref="Clear"/>; otherwise a draw that fails would
    /// tell its view, the view would build its document again, and that would ask for the draw
    /// again, for ever.
    /// </para>
    ///
    /// <para>
    /// Only Mermaid, and only on this PC: every request has no Kroki server, so an answer's text is
    /// never posted anywhere. Nothing throws. Everything here runs on the UI thread: Get and Clear
    /// are called there, and a draw that ends elsewhere is brought back to it before anything is
    /// remembered or anyone is told.
    /// </para>
    /// </summary>
    internal sealed class ChatDiagrams : IChatDiagrams
    {
        /// <summary>How many Mermaid blocks of one answer are drawn; the ones after them are code.</summary>
        public const int MaxPerAnswer = 8;

        /// <summary>
        /// How many outcomes are remembered, the least recently used dropped first. More than one
        /// document asks for (<see cref="MaxPerAnswer"/>): a document being rebuilt never pushes
        /// out an outcome it is about to ask for again.
        /// </summary>
        internal const int MaxKept = 32;

        private static readonly ChatDiagramState Off = new(ChatDiagramStatus.Off);
        private static readonly ChatDiagramState Drawing = new(ChatDiagramStatus.Drawing);

        /// <summary>The one kind drawn in answers. MicaPad's other fence words (dot, markmap, the Kroki types) stay code.</summary>
        private static readonly DiagramKind? Mermaid = DiagramKinds.FromWord("mermaid");

        private readonly Func<IDiagramRenderer?> _renderer;
        private readonly Func<bool> _enabled;
        private readonly Dictionary<string, LinkedListNode<(string Key, ChatDiagramState State)>> _kept = new(StringComparer.Ordinal);
        private readonly LinkedList<(string Key, ChatDiagramState State)> _order = new();
        private readonly Dictionary<string, List<Action>> _waiting = new(StringComparer.Ordinal);

        /// <param name="renderer">The drawing engine, made on first use; null when there is none.</param>
        /// <param name="enabled">Settings → MicaPad → Draw diagrams, read at every call.</param>
        public ChatDiagrams(Func<IDiagramRenderer?> renderer, Func<bool> enabled)
        {
            _renderer = renderer;
            _enabled = enabled;
        }

        /// <summary>The app's own, set at startup; null before that and in tests.</summary>
        internal static IChatDiagrams? Current { get; set; }

        /// <summary>Where a failure is reported, by its type only. Tests replace it so nothing reaches the real log.</summary>
        internal Action<string> Warn { get; set; } = message => DiagnosticsLog.Warn("ai", message);

        /// <inheritdoc />
        public ChatDiagramState Get(string source, bool dark, Action? whenDone)
        {
            string? key = null;
            try
            {
                // The setting first: while it is off the engine is not even made.
                if (!_enabled() || Mermaid is not { } kind || _renderer() is not { } renderer) return Off;
                if (source.Length > DiagramBlocks.MaxSourceLength) return Failed(DiagramText.TooLarge);

                DiagramRequest request = RequestFor(kind, source, dark);
                key = request.Key;
                if (_kept.TryGetValue(key, out var kept))
                {
                    _order.Remove(kept);
                    _order.AddFirst(kept);
                    return kept.Value.State;
                }
                if (_waiting.TryGetValue(key, out List<Action>? waiters))
                {
                    Add(waiters, whenDone);
                    return Drawing;
                }
                // Drawn before, for a note or for an answer whose picture was dropped here: no draw, and nobody to tell.
                if (renderer.TryGetCached(key, out DiagramResult? cached)) return Keep(key, StateOf(cached));

                waiters = new List<Action>();
                _waiting.Add(key, waiters);
                // A slot of its own: the renderer lets a newer request take the place of a waiting
                // one with the same slot, and two diagrams of one answer must not cancel each other.
                Task<DiagramResult> draw = renderer.RenderAsync(request, new object());
                if (draw.IsCompleted)
                {
                    // Answered at once: the caller is still building its document and has the answer
                    // in what this returns, so nobody is told.
                    _waiting.Remove(key);
                    return Keep(key, StateOf(Ended(draw)));
                }

                Add(waiters, whenDone);
                _ = FollowAsync(draw, key, waiters, Dispatcher.CurrentDispatcher);
                return Drawing;
            }
            catch (Exception ex)
            {
                // The type only: the message could quote the source.
                Report("Asking for a diagram in an answer failed (" + ex.GetType().Name + ")");
                ChatDiagramState failed = Failed(DiagramText.Failed);
                if (key == null) return failed;
                _waiting.Remove(key);
                return Keep(key, failed);
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// A draw still running is forgotten too: its outcome is not kept when it ends. Whoever
        /// waits for it is still told then, and asks again if it still shows that block. The
        /// drawing engine's own store is not emptied (it also holds the pictures of the notes).
        /// </remarks>
        public void Clear()
        {
            _kept.Clear();
            _order.Clear();
            _waiting.Clear();
        }

        /// <summary>
        /// The request for one Mermaid block, built as MicaPad's editor builds its own
        /// (<c>DiagramBoard.RequestFor</c>): the kind, the source, the theme's name and its text and
        /// background colors. The colors are the answer's (<see cref="AskPalette"/>), and there is
        /// never a Kroki server.
        /// </summary>
        private static DiagramRequest RequestFor(DiagramKind kind, string source, bool dark)
        {
            AskPalette palette = dark ? AskPalette.Dark : AskPalette.Light;
            return new DiagramRequest(kind, source, palette.Name, DiagramRequest.Css(palette.Ink), DiagramRequest.Css(palette.Background), KrokiServer: null);
        }

        /// <summary>Waits for a draw, then goes back to the UI thread to remember how it ended and tell the waiters.</summary>
        private async Task FollowAsync(Task<DiagramResult> draw, string key, List<Action> waiters, Dispatcher ui)
        {
            DiagramResult? result = null;
            try
            {
                result = await draw.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Report("Drawing a diagram for an answer failed (" + ex.GetType().Name + ")");
            }

            try
            {
                // Always through the dispatcher, even when this is the UI thread already: a waiter
                // builds a document, and must not run inside whatever ended the draw.
                await ui.InvokeAsync(() => Finish(key, waiters, result));
            }
            catch (Exception ex)
            {
                // The dispatcher is shutting down: nobody is left to tell.
                Report("A diagram's end could not be told (" + ex.GetType().Name + ")");
            }
        }

        /// <summary>A draw ended (UI thread): its outcome is kept unless <see cref="Clear"/> came between, and each waiter is told once.</summary>
        private void Finish(string key, List<Action> waiters, DiagramResult? result)
        {
            try
            {
                if (_waiting.TryGetValue(key, out List<Action>? current) && ReferenceEquals(current, waiters))
                {
                    _waiting.Remove(key);
                    Keep(key, StateOf(result));
                }
            }
            catch (Exception ex)
            {
                Report("Keeping a diagram for an answer failed (" + ex.GetType().Name + ")");
            }

            foreach (Action done in waiters.ToArray())
            {
                try
                {
                    done();
                }
                catch (Exception ex)
                {
                    // A view that cannot draw itself again must not stop the others from being told.
                    Report("Drawing an answer again for its diagram failed (" + ex.GetType().Name + ")");
                }
            }
            waiters.Clear();
        }

        /// <summary>The result of a draw that has ended, or null when its task failed.</summary>
        private DiagramResult? Ended(Task<DiagramResult> draw)
        {
            if (draw.IsCompletedSuccessfully) return draw.Result;
            Report("Drawing a diagram for an answer failed (" + (draw.Exception?.GetBaseException().GetType().Name ?? "cancelled") + ")");
            return null;
        }

        /// <summary>
        /// What a draw's result is to an answer. A picture is decoded here, once, and kept frozen.
        /// Everything else is a failure with the renderer's message: a syntax error, a missing
        /// WebView2 Runtime, a draw that took too long, and a result that says nothing at all.
        /// </summary>
        private ChatDiagramState StateOf(DiagramResult? result)
        {
            if (result is not { IsPicture: true }) return Failed(result?.Error ?? DiagramText.Failed);
            if (!(result.Width > 0) || !(result.Height > 0)) return Failed(DiagramText.Failed);
            try
            {
                return new ChatDiagramState(ChatDiagramStatus.Drawn, Decode(result.Png!), result.Width, result.Height);
            }
            catch (Exception ex)
            {
                Report("A diagram's picture could not be read (" + ex.GetType().Name + ")");
                return Failed(DiagramText.Failed);
            }
        }

        /// <summary>
        /// The PNG as a frozen bitmap, decoded the way <c>DiagramPicture.BitmapOf</c> decodes it, at
        /// its own pixel size: the engine draws a diagram with twice as many pixels as its size, and
        /// one picture may be shown in several windows on screens with different scaling, so there
        /// is no one screen to decode it for. Shown no larger than the size the result names, it is
        /// sharp up to 200% scaling. Not through <c>BitmapOf</c> itself: that keeps what it decodes
        /// in a store of its own, which <see cref="Clear"/> could not empty.
        /// </summary>
        private static BitmapSource Decode(byte[] png)
        {
            var decoded = new BitmapImage();
            decoded.BeginInit();
            decoded.CacheOption = BitmapCacheOption.OnLoad;
            decoded.StreamSource = new MemoryStream(png);
            decoded.EndInit();
            decoded.Freeze();
            return decoded;
        }

        private static ChatDiagramState Failed(string error) => new(ChatDiagramStatus.Failed, Error: error);

        /// <summary>Remembers an outcome as the most recently used, and drops the least recently used over <see cref="MaxKept"/>.</summary>
        private ChatDiagramState Keep(string key, ChatDiagramState state)
        {
            if (_kept.Remove(key, out var old)) _order.Remove(old);
            _kept[key] = _order.AddFirst((key, state));
            while (_kept.Count > MaxKept && _order.Last is { } last)
            {
                _order.RemoveLast();
                _kept.Remove(last.Value.Key);
            }
            return state;
        }

        /// <summary>
        /// Adds a waiter, once: a view gives the same redraw at every rebuild, ten times a second
        /// for as long as the draw takes, and is told once when it ends.
        /// </summary>
        private static void Add(List<Action> waiters, Action? whenDone)
        {
            if (whenDone != null && !waiters.Contains(whenDone)) waiters.Add(whenDone);
        }

        private void Report(string message)
        {
            try
            {
                Warn(message);
            }
            catch (Exception)
            {
                // Logging is best effort; it must not throw into a render either.
            }
        }
    }
}
