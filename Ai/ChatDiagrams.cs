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
    /// <see cref="Height"/> are the diagram's own size in device-independent pixels: it is shown at
    /// most that large, however many pixels the bitmap was decoded with. <see cref="CanRetry"/> is
    /// true for a failure that may pass (the engine was still starting, a draw took too long, the
    /// runtime is missing): drawing it again may give the picture. A failure the same source gives
    /// again (a syntax error, a source too large) has it false.
    /// </summary>
    internal sealed record ChatDiagramState(ChatDiagramStatus Status, ImageSource? Picture = null, double Width = 0, double Height = 0, string? Error = null,
                                            bool CanRetry = false);

    /// <summary>The pictures of the Mermaid blocks in AI answers: the app's <see cref="ChatDiagrams"/>, or a test's fake.</summary>
    internal interface IChatDiagrams
    {
        /// <summary>
        /// The state of the picture for this Mermaid source in this theme. Starts a draw when none was started.
        /// <paramref name="whenDone"/> is called once, on the UI thread, when a draw this call is waiting for ends.
        /// </summary>
        ChatDiagramState Get(string source, bool dark, Action? whenDone);

        /// <summary>
        /// Forgets how the draw of this source in this theme ended, so the next <see cref="Get"/>
        /// draws it again. One press of Try again is one call, and so one draw.
        /// </summary>
        void Forget(string source, bool dark);

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
    /// A failure is never tried again by itself; otherwise a draw that fails would tell its view,
    /// the view would build its document again, and that would ask for the draw again, for ever.
    /// It is tried again when the user asks (<see cref="Forget"/>, behind the Try again button of
    /// a failure that may pass: one press, one draw) and after <see cref="Clear"/>.
    /// </para>
    ///
    /// <para>
    /// What it keeps. A picture is decoded once, with no more pixels than the screen draws it with
    /// and never over <see cref="MaxDecodeWidth"/> by <see cref="MaxDecodeHeight"/>. Pictures are
    /// kept up to <see cref="MaxPictureBytes"/>, and always the <see cref="AlwaysKept"/> used last,
    /// so an answer that is built again finds its own pictures and decodes nothing. Failures are
    /// kept apart, up to <see cref="MaxFailures"/>: a picture never pushes a failure out, nor a
    /// failure a picture. A picture dropped here stays with the answers that still show it, until
    /// those are built again or go.
    /// </para>
    ///
    /// <para>
    /// Only Mermaid, and only on this PC: every request has no Kroki server, so an answer's text is
    /// never posted anywhere. Nothing throws. Everything here runs on the UI thread: Get, Forget
    /// and Clear are called there, and a draw that ends elsewhere is brought back to it before
    /// anything is remembered or anyone is told.
    /// </para>
    /// </summary>
    internal sealed class ChatDiagrams : IChatDiagrams
    {
        /// <summary>How many Mermaid blocks of one answer are drawn; the ones after them are code.</summary>
        public const int MaxPerAnswer = 8;

        /// <summary>The most pixels a picture is decoded with, across and down: 15.36 MB at the most.</summary>
        internal const int MaxDecodeWidth = 1600;
        internal const int MaxDecodeHeight = 2400;

        /// <summary>How many bytes of pictures are kept (pixels across, by pixels down, by 4), the least recently used dropped first.</summary>
        internal const long MaxPictureBytes = 64L * 1024 * 1024;

        /// <summary>
        /// The pictures used last that are kept whatever they weigh: as many as one answer shows
        /// (<see cref="MaxPerAnswer"/>). Without it an answer of eight tall diagrams would drop its
        /// own pictures and decode them again at every rebuild.
        /// </summary>
        internal const int AlwaysKept = MaxPerAnswer;

        /// <summary>How many failures are kept, the least recently used dropped first.</summary>
        internal const int MaxFailures = 64;

        /// <summary>The largest display scaling a picture is decoded for.</summary>
        internal const double MaxScale = 3;

        private static readonly ChatDiagramState Off = new(ChatDiagramStatus.Off);
        private static readonly ChatDiagramState Drawing = new(ChatDiagramStatus.Drawing);

        /// <summary>The one kind drawn in answers. MicaPad's other fence words (dot, markmap, the Kroki types) stay code.</summary>
        private static readonly DiagramKind? Mermaid = DiagramKinds.FromWord("mermaid");

        private readonly Func<IDiagramRenderer?> _renderer;
        private readonly Func<bool> _enabled;
        private readonly Func<double>? _scale;
        private readonly Kept _pictures = new();
        private readonly Kept _failures = new();
        private readonly Dictionary<string, List<Action>> _waiting = new(StringComparer.Ordinal);

        /// <summary>True once a failure to read the setting or the engine was reported; it is not reported again.</summary>
        private bool _unreadableSaid;

        /// <param name="renderer">The drawing engine, made on first use; null when there is none.</param>
        /// <param name="enabled">Settings → MicaPad → Draw diagrams, read at every call.</param>
        /// <param name="scale">
        /// The display scaling a picture is decoded for (1.5 at 150%), read at each decode. Null is
        /// 1; a value that is not finite or is below 1 counts as 1, and one above
        /// <see cref="MaxScale"/> as that.
        /// </param>
        public ChatDiagrams(Func<IDiagramRenderer?> renderer, Func<bool> enabled, Func<double>? scale = null)
        {
            _renderer = renderer;
            _enabled = enabled;
            _scale = scale;
        }

        /// <summary>The app's own, set at startup; null before that and in tests.</summary>
        internal static IChatDiagrams? Current { get; set; }

        /// <summary>Where a failure is reported, by its type only. Tests replace it so nothing reaches the real log.</summary>
        internal Action<string> Warn { get; set; } = message => DiagnosticsLog.Warn("ai", message);

        /// <summary>
        /// Decodes a PNG into a frozen bitmap that many pixels across, or that many down (the other
        /// side follows; both 0 is the PNG's own size). Tests replace it to count the decodes.
        /// </summary>
        internal Func<byte[], int, int, BitmapSource> Decoder { get; set; } = DecodePng;

        /// <summary>How many pictures are kept; for tests.</summary>
        internal int PicturesKept => _pictures.Count;

        /// <summary>The bytes of the pictures kept; for tests.</summary>
        internal long PictureBytes => _pictures.Bytes;

        /// <summary>How many failures are kept; for tests.</summary>
        internal int FailuresKept => _failures.Count;

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
                if (_pictures.TryUse(key, out ChatDiagramState? kept) || _failures.TryUse(key, out kept)) return kept;
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
                string message = "Asking for a diagram in an answer failed (" + ex.GetType().Name + ")";
                ChatDiagramState failed = Failed(DiagramText.Failed, canRetry: true);   // it may well work the next time
                if (key == null)
                {
                    // The setting or the engine could not be read. Nothing is kept for that (there
                    // is no key yet), so every build of the answer comes here again, ten times a
                    // second while it streams: it is said once.
                    if (!_unreadableSaid)
                    {
                        _unreadableSaid = true;
                        Report(message);
                    }
                    return failed;
                }

                Report(message);   // kept under its key below, so this is once for each draw
                _waiting.Remove(key);
                return Keep(key, failed);
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Only an outcome is forgotten. A draw still running goes on, and its outcome is kept
        /// when it ends: forgetting starts nothing and stops nothing. Never throws.
        /// </remarks>
        public void Forget(string source, bool dark)
        {
            try
            {
                if (Mermaid is not { } kind || source.Length > DiagramBlocks.MaxSourceLength) return;
                string key = RequestFor(kind, source, dark).Key;
                _pictures.Remove(key);
                _failures.Remove(key);
            }
            catch (Exception ex)
            {
                Report("Forgetting a diagram in an answer failed (" + ex.GetType().Name + ")");
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
            _pictures.Clear();
            _failures.Clear();
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
        /// WebView2 Runtime, a draw that took too long, and a result that says nothing at all. So
        /// is a picture without a size, or one that cannot be decoded.
        ///
        /// <para>
        /// A failure can be tried again when the renderer says the same request may give another
        /// result (it is not <see cref="DiagramResult.Lasting"/>: a timeout, an engine that stopped
        /// or was still starting, a missing runtime, a draw another took the place of), and when
        /// there is no result at all (the draw threw). A lasting error is the source's own, and a
        /// picture that cannot be used comes back the same from the engine's store: neither can.
        /// </para>
        /// </summary>
        private ChatDiagramState StateOf(DiagramResult? result)
        {
            if (result is not { IsPicture: true }) return Failed(result?.Error ?? DiagramText.Failed, canRetry: result is not { Lasting: true });
            if (!IsSize(result.Width) || !IsSize(result.Height)) return Failed(DiagramText.Failed);
            try
            {
                return new ChatDiagramState(ChatDiagramStatus.Drawn, Decode(result.Png!, result.Width), result.Width, result.Height);
            }
            catch (Exception ex)
            {
                Report("A diagram's picture could not be read (" + ex.GetType().Name + ")");
                return Failed(DiagramText.Failed);
            }
        }

        private static bool IsSize(double side) => double.IsFinite(side) && side > 0;

        /// <summary>
        /// The PNG as a frozen bitmap with as many pixels as the screen draws the diagram with: its
        /// width times the display scaling, as MicaPad's <c>DiagramPicture.DecodeWidthOf</c> takes
        /// it; never more than the PNG has (the engine draws it with twice the diagram's size), and
        /// never over <see cref="MaxDecodeWidth"/> by <see cref="MaxDecodeHeight"/>. The PNG's size
        /// is read from its header, and the decoder is told the size wanted, so the whole PNG (up
        /// to 4,096 a side, 64 MB) is never laid out in memory. Not through
        /// <c>DiagramPicture.BitmapOf</c>: that keeps what it decodes in a store of its own, which
        /// <see cref="Clear"/> could not empty.
        /// </summary>
        private BitmapSource Decode(byte[] png, double width)
        {
            if (!TryPngSize(png, out int fullWidth, out int fullHeight)) throw new InvalidDataException("Not a PNG");

            int across = (int)Math.Max(1, Math.Min(Math.Min(Math.Ceiling(width * Scale()), MaxDecodeWidth), fullWidth));
            // A tall picture: the height is what the cap holds, and the width follows it.
            if ((double)fullHeight * across / fullWidth > MaxDecodeHeight) return Decoder(png, 0, MaxDecodeHeight);
            return Decoder(png, across < fullWidth ? across : 0, 0);
        }

        /// <summary>The display scaling to decode for: 1 to <see cref="MaxScale"/>; 1 when none was given or it cannot be read.</summary>
        private double Scale()
        {
            double scale = 1;
            try
            {
                if (_scale != null) scale = _scale();
            }
            catch (Exception ex)
            {
                Report("Reading the display scaling for a diagram failed (" + ex.GetType().Name + ")");
            }
            return double.IsFinite(scale) && scale >= 1 ? Math.Min(scale, MaxScale) : 1;
        }

        /// <summary>A PNG's size in pixels from its header (bytes 16 to 23, big-endian), as <c>DiagramPicture.PngWidth</c> reads the width.</summary>
        private static bool TryPngSize(byte[] png, out int width, out int height)
        {
            width = height = 0;
            if (png.Length < 24 || png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G') return false;
            width = png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19];
            height = png[20] << 24 | png[21] << 16 | png[22] << 8 | png[23];
            return width > 0 && height > 0;
        }

        /// <summary>Decodes as <c>DiagramPicture.BitmapOf</c> does: loaded at once, at the size asked for, and frozen.</summary>
        private static BitmapSource DecodePng(byte[] png, int decodeWidth, int decodeHeight)
        {
            var decoded = new BitmapImage();
            decoded.BeginInit();
            decoded.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) decoded.DecodePixelWidth = decodeWidth;
            else if (decodeHeight > 0) decoded.DecodePixelHeight = decodeHeight;
            decoded.StreamSource = new MemoryStream(png);
            decoded.EndInit();
            decoded.Freeze();
            return decoded;
        }

        private static ChatDiagramState Failed(string error, bool canRetry = false) => new(ChatDiagramStatus.Failed, Error: error, CanRetry: canRetry);

        /// <summary>
        /// Remembers an outcome as the most recently used of its kind. Pictures over
        /// <see cref="MaxPictureBytes"/> go, the least recently used first, but never the
        /// <see cref="AlwaysKept"/> used last; failures over <see cref="MaxFailures"/> go the same way.
        /// </summary>
        private ChatDiagramState Keep(string key, ChatDiagramState state)
        {
            if (state.Status == ChatDiagramStatus.Drawn)
            {
                _failures.Remove(key);
                _pictures.Put(key, state, BytesOf(state.Picture));
                while (_pictures.Bytes > MaxPictureBytes && _pictures.Count > AlwaysKept) _pictures.DropOldest();
            }
            else
            {
                _pictures.Remove(key);
                _failures.Put(key, state, 0);
                while (_failures.Count > MaxFailures) _failures.DropOldest();
            }
            return state;
        }

        /// <summary>What a picture weighs here: four bytes a pixel.</summary>
        private static long BytesOf(ImageSource? picture) =>
            picture is BitmapSource bitmap ? (long)bitmap.PixelWidth * bitmap.PixelHeight * 4 : 0;

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

        /// <summary>Outcomes by key, the most recently used first, with what they weigh together.</summary>
        private sealed class Kept
        {
            private readonly Dictionary<string, LinkedListNode<(string Key, ChatDiagramState State, long Bytes)>> _byKey = new(StringComparer.Ordinal);
            private readonly LinkedList<(string Key, ChatDiagramState State, long Bytes)> _order = new();

            public int Count => _byKey.Count;

            public long Bytes { get; private set; }

            /// <summary>The outcome for <paramref name="key"/>, which is now the most recently used.</summary>
            public bool TryUse(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ChatDiagramState? state)
            {
                if (!_byKey.TryGetValue(key, out var node))
                {
                    state = null;
                    return false;
                }
                _order.Remove(node);
                _order.AddFirst(node);
                state = node.Value.State;
                return true;
            }

            public void Put(string key, ChatDiagramState state, long bytes)
            {
                Remove(key);
                _byKey[key] = _order.AddFirst((key, state, bytes));
                Bytes += bytes;
            }

            public void Remove(string key)
            {
                if (!_byKey.Remove(key, out var node)) return;
                _order.Remove(node);
                Bytes -= node.Value.Bytes;
            }

            public void DropOldest()
            {
                if (_order.Last is { } oldest) Remove(oldest.Value.Key);
            }

            public void Clear()
            {
                _byKey.Clear();
                _order.Clear();
                Bytes = 0;
            }
        }
    }
}
