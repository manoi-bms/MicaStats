using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// A block whose error box shows an error a corrected source can cure (MicaPad AI part 2, spec
    /// 2.2): its opening and closing fence lines (1-based, as they are when asked), the word that
    /// names its language (<see cref="DiagramBlocks.WordOf"/>) and the message the box shows.
    /// </summary>
    internal sealed record DiagramFailure(int OpenLine, int CloseLine, string Kind, string Message);

    /// <summary>
    /// The diagram pictures of one Markdown document in one editor (spec section 4): each block's
    /// shown result and pending draw (kept per closing fence line), the pause after typing (R8),
    /// Hide code through the editor's folding, and the exports (R1). Nothing here waits on a draw:
    /// the renderer answers later and the block's line is drawn again then. Blocks out of view are
    /// never asked for, because the generator only runs for the lines in view.
    /// </summary>
    internal sealed class DiagramBoard
    {
        private const double MinPictureWidth = 120;
        private const double PictureMargin = 24;
        private static readonly TimeSpan ResizePause = TimeSpan.FromMilliseconds(200);

        private readonly TextEditor _editor;
        private readonly MarkdownDocumentCache _cache;
        private readonly FoldingController? _folding;
        private readonly DiagramServices _services;
        private readonly Func<PadPalette> _palette;
        private readonly TextDocument _document;
        private readonly DispatcherTimer _pauseTimer;
        private readonly DispatcherTimer _resizeTimer;
        private readonly Func<TextDocument, IEnumerable<NewFolding>> _hiddenFolds;
        private readonly ConditionalWeakTable<DocumentLine, BlockState> _states = new();
        private readonly List<TextAnchor> _hidden = new();
        private bool _attached = true;
        private bool _editing;
        private bool _warned;
        private int _generation;

        public DiagramBoard(TextEditor editor, MarkdownDocumentCache cache, FoldingController? folding, DiagramServices services, Func<PadPalette> palette)
        {
            _editor = editor;
            _cache = cache;
            _folding = folding;
            _services = services;
            _palette = palette;
            _document = editor.Document;

            _pauseTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = services.Pause };
            _pauseTimer.Tick += (s, e) =>
            {
                _pauseTimer.Stop();
                DrawDue();
            };
            _resizeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ResizePause };
            _resizeTimer.Tick += (s, e) =>
            {
                _resizeTimer.Stop();
                RedrawPictureLines();
            };

            _document.Changed += OnChanged;
            _editor.TextArea.TextView.SizeChanged += OnViewSizeChanged;
            _hiddenFolds = HiddenFolds;
            if (_folding != null) _folding.ExtraFolds = _hiddenFolds;
        }

        private enum DiagramExport
        {
            Copy,
            Png,
            Svg,
        }

        /// <summary>The document the pictures belong to.</summary>
        internal TextDocument Document => _document;

        /// <summary>How many draws this board asked for; for tests.</summary>
        internal int Draws { get; private set; }

        /// <summary>Stops following the document; a draw that finishes later changes nothing.</summary>
        public void Detach()
        {
            if (!_attached) return;
            _attached = false;
            _pauseTimer.Stop();
            _resizeTimer.Stop();
            _document.Changed -= OnChanged;
            _editor.TextArea.TextView.SizeChanged -= OnViewSizeChanged;
            if (_folding != null && ReferenceEquals(_folding.ExtraFolds, _hiddenFolds)) _folding.ExtraFolds = null;
            _hidden.Clear();
        }

        /// <summary>Kroki or its server changed: every picture is asked for again, passing failures included (R6).</summary>
        public void Refresh()
        {
            _generation++;
            _editor.TextArea.TextView.Redraw();
        }

        /// <summary>Typing paused: the pictures in view are drawn for their new text. The pause timer calls it; so do tests.</summary>
        internal void DrawDue()
        {
            _editing = false;
            RedrawPictureLines();
        }

        /// <summary>Logs once per board; a failure here never reaches the editor.</summary>
        internal void WarnOnce(string message)
        {
            if (_warned) return;
            _warned = true;
            try
            {
                _services.Warn(message);
            }
            catch (Exception)
            {
                // Logging is best effort.
            }
        }

        /// <summary>The diagram block <paramref name="closing"/> closes, or null.</summary>
        internal DiagramBlock? BlockClosedBy(DocumentLine closing)
        {
            if (!_attached || closing.IsDeleted) return null;
            int open = _cache.OpeningLineOf(_document, closing.LineNumber);
            return open == 0 ? null : Read(open, closing.LineNumber);
        }

        /// <summary>
        /// The failure the diagram or math block around line <paramref name="lineNumber"/> (1-based)
        /// shows now; its two fence lines are part of it. Null when the line is in no such block,
        /// the block shows its picture or is still being drawn, or its error is one no corrected
        /// source cures (<see cref="Fixable"/>). The AI menu asks it for the caret's line.
        /// </summary>
        internal DiagramFailure? FailureAt(int lineNumber)
        {
            if (!_attached || lineNumber < 1 || lineNumber > _document.LineCount) return null;
            int close = lineNumber;                                         // a closing fence, when it has an opening line
            if (_cache.OpeningLineOf(_document, lineNumber) == 0)
            {
                int inside = _cache.BlockOpeningOf(_document, lineNumber);  // 0 on an opening fence, and outside every block
                close = _cache.ClosingLineOf(_document, inside != 0 ? inside : lineNumber);
            }
            return close == 0 ? null : FailureOf(_document.GetLineByNumber(close));
        }

        /// <summary>
        /// The failure shown under the closing fence line <paramref name="closing"/>, with the
        /// block's lines and word as they are now, not as they were when its box was drawn; null
        /// when the line closes no diagram block any more, or its block shows no failure to fix.
        /// The line object follows its block through edits, so a menu built earlier asks with it
        /// at the click and gets the block where it is then.
        /// </summary>
        internal DiagramFailure? FailureOf(DocumentLine closing)
        {
            if (BlockClosedBy(closing) is not { } block) return null;
            if (!_states.TryGetValue(closing, out var state) || state.Shown is not { } shown || !Fixable(shown)) return null;
            return new DiagramFailure(block.OpenLine, block.CloseLine, DiagramBlocks.WordOf(TextOf, block.OpenLine), shown.Error ?? DiagramText.Failed);
        }

        /// <summary>
        /// True for an error about the block's own source, which a corrected source can cure: what
        /// the engine, or the Kroki server, said of it. Such an error is lasting (the same text
        /// fails the same way). Not the board's own notices (too large to draw, Kroki is off), and
        /// not a passing failure (no WebView2 Runtime, a server out of reach, a timeout): sending
        /// the source to AI would fix none of those.
        ///
        /// <para>
        /// A notice is known by the flag it was made with, not by asking the block and the
        /// settings as they are now: a notice stays in its box until the block is drawn again, and
        /// in between the block may have been made smaller, or Kroki turned on.
        /// </para>
        /// </summary>
        private static bool Fixable(DiagramResult? shown) => shown is { IsPicture: false, Lasting: true, IsNotice: false };

        /// <summary>
        /// Fix with AI on the error box under <paramref name="closing"/>. The window is told the
        /// block as it is at the click; a block that is gone by then, or shows no such error any
        /// more, asks for nothing.
        /// </summary>
        private void Fix(DocumentLine closing)
        {
            if (FailureOf(closing) is { } failure)
                _services.FixWithAi?.Invoke(failure.OpenLine, failure.CloseLine, failure.Kind, failure.Message);
        }

        /// <summary>
        /// What goes under <paramref name="closing"/>: the picture, the error, or "Drawing…". Asks
        /// for a drawing when one is due: not while typing, and not again for a text whose drawing
        /// already ended (R6).
        /// </summary>
        internal UIElement PictureFor(DocumentLine closing, DiagramBlock block)
        {
            var state = _states.GetValue(closing, _ => new BlockState());
            var palette = _palette();
            string? server = _services.KrokiServer();
            DiagramResult? result;
            if (block.TooLarge)
            {
                result = state.Shown = DiagramResult.Notice(DiagramText.TooLarge);
            }
            else if (block.Kind.NeedsKroki && server == null)
            {
                result = state.Shown = DiagramResult.Notice(DiagramText.NeedsKroki(block.Kind));
            }
            else
            {
                var request = RequestFor(block, palette, server);
                string key = request.Key;
                if (_services.Renderer.TryGetCached(key, out var cached))
                {
                    state.PendingKey = null;
                    result = state.Shown = cached;
                }
                else
                {
                    bool settled = state.SettledKey == key && state.SettledGeneration == _generation;
                    if (!_editing && !settled && state.PendingKey != key) Request(closing, state, request, key);
                    result = state.Shown;   // the previous picture stays until the new one is ready
                }
            }
            return new DiagramPicture(ViewOf(closing, block, result, palette));
        }

        /// <summary>True while the code of the block <paramref name="closing"/> closes is folded away.</summary>
        internal bool IsCodeHidden(DocumentLine closing)
        {
            if (_folding?.Manager is not { } manager || BlockClosedBy(closing) is not { } block || InnerRange(block) is not { } range) return false;
            return manager.GetFoldingsAt(range.Start).Any(f => f.EndOffset == range.End && f.IsFolded);
        }

        /// <summary>
        /// Hide code / Show code: folds the block's lines between its fences, or unfolds them. The
        /// fold follows edits (an anchor on the opening fence line) and is not saved.
        /// </summary>
        internal void SetCodeHidden(DocumentLine closing, bool hide)
        {
            if (_folding?.Manager is not { } manager || BlockClosedBy(closing) is not { } block || InnerRange(block) is not { } range) return;

            var opening = _document.GetLineByNumber(block.OpenLine);
            _hidden.RemoveAll(a => a.IsDeleted || a.Line == opening.LineNumber);
            if (hide)
            {
                var anchor = _document.CreateAnchor(opening.Offset);
                anchor.MovementType = AnchorMovementType.AfterInsertion;
                _hidden.Add(anchor);
            }
            _folding.Update();
            foreach (var section in manager.GetFoldingsAt(range.Start))
                if (section.EndOffset == range.End) section.IsFolded = hide;
            _editor.TextArea.TextView.Redraw(closing, DispatcherPriority.Normal);
        }

        private DiagramBlock? Read(int open, int close) => DiagramBlocks.Read(TextOf, open, close);

        /// <summary>The text of line <paramref name="number"/> (1-based).</summary>
        private string TextOf(int number) => _document.GetText(_document.GetLineByNumber(number));

        private static DiagramRequest RequestFor(DiagramBlock block, PadPalette palette, string? server) =>
            new(block.Kind, block.Source, palette.Name, DiagramRequest.Css(palette.Text), DiagramRequest.Css(palette.Background),
                block.Kind.NeedsKroki ? server : null);

        private void Request(DocumentLine closing, BlockState state, DiagramRequest request, string key)
        {
            state.PendingKey = key;
            Draws++;
            _ = AwaitDrawAsync(closing, state, request, key);
        }

        private async Task AwaitDrawAsync(DocumentLine closing, BlockState state, DiagramRequest request, string key)
        {
            int generation = _generation;
            DiagramResult result;
            bool completedAtOnce = true;   // a renderer that throws before returning a task is just as immediate
            try
            {
                var task = _services.Renderer.RenderAsync(request, closing);
                completedAtOnce = task.IsCompleted;
                result = await task;
            }
            catch (Exception ex)
            {
                WarnOnce(request.Kind.Name + " drawing failed (" + ex.GetType().Name + ")");
                result = DiagramResult.Failure(DiagramText.Failed, lasting: false);
            }

            if (result.IsReplaced || state.PendingKey != key) return;
            state.PendingKey = null;
            state.Shown = result;
            state.SettledKey = key;
            state.SettledGeneration = generation;
            // Answered at once: PictureFor is still building the line and reads the state right after
            // Request, so a redraw now would run inside the measure.
            if (!completedAtOnce && _attached && !closing.IsDeleted && ReferenceEquals(_editor.Document, _document))
                _editor.TextArea.TextView.Redraw(closing, DispatcherPriority.Normal);
        }

        private DiagramView ViewOf(DocumentLine closing, DiagramBlock block, DiagramResult? result, PadPalette palette)
        {
            var view = _editor.TextArea.TextView;
            double width = ((IScrollInfo)view).ViewportWidth;
            if (!(width > 0)) width = view.ActualWidth;
            return new DiagramView
            {
                Result = result,
                Palette = palette,
                MaxWidth = Math.Max(MinPictureWidth, width - PictureMargin),
                PixelsPerDip = VisualTreeHelper.GetDpi(view).PixelsPerDip,
                CanHideCode = _folding != null && block.CloseLine - block.OpenLine > 1,
                CodeHidden = IsCodeHidden(closing),
                ToggleCode = () => SetCodeHidden(closing, !IsCodeHidden(closing)),
                CopyPicture = () => _ = ExportAsync(closing, DiagramExport.Copy),
                SavePng = () => _ = ExportAsync(closing, DiagramExport.Png),
                SaveSvg = () => _ = ExportAsync(closing, DiagramExport.Svg),
                OpenLink = _services.OpenLink,
                // Only on an error a corrected source can cure, and only where something takes the request.
                FixWithAi = _services.FixWithAi != null && Fixable(result) ? () => Fix(closing) : null,
                AiOn = _services.AiOn,
                SetUpAi = _services.SetUpAi,
            };
        }

        private void OnChanged(object? sender, DocumentChangeEventArgs e)
        {
            if (!_attached || !ReferenceEquals(sender, _document)) return;
            _editing = true;
            _pauseTimer.Stop();
            _pauseTimer.Start();
        }

        private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_attached || !e.WidthChanged) return;
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }

        /// <summary>Builds again the lines in view that hold a picture, so each fits the width and shows its latest drawing.</summary>
        private void RedrawPictureLines()
        {
            if (!_attached) return;
            var view = _editor.TextArea.TextView;
            if (!view.VisualLinesValid) return;
            foreach (var line in view.VisualLines.ToList())
                if (line.Elements.OfType<DiagramElement>().Any()) view.Redraw(line, DispatcherPriority.Normal);
        }

        /// <summary>The offsets Hide code folds: from the end of the opening fence line to the end of the block's last line.</summary>
        private (int Start, int End)? InnerRange(DiagramBlock block)
        {
            if (block.CloseLine - block.OpenLine < 2) return null;
            return (_document.GetLineByNumber(block.OpenLine).EndOffset, _document.GetLineByNumber(block.CloseLine - 1).EndOffset);
        }

        /// <summary>
        /// The folds of the blocks whose code is hidden, for the folding recompute. They are not
        /// DefaultClosed (AvalonEdit applies that only to a manager first update): SetCodeHidden
        /// folds the section itself. A hidden block whose fence is retyped comes back unfolded, and
        /// its button then says Hide code, which is true.
        /// </summary>
        private IEnumerable<NewFolding> HiddenFolds(TextDocument document)
        {
            var folds = new List<NewFolding>();
            if (!_attached || !ReferenceEquals(document, _document)) return folds;

            _hidden.RemoveAll(a => a.IsDeleted);
            foreach (var anchor in _hidden)
            {
                int open = anchor.Line;
                int close = _cache.ClosingLineOf(document, open);
                if (close == 0 || Read(open, close) is not { } block || InnerRange(block) is not { } range) continue;
                folds.Add(new NewFolding(range.Start, range.End));
            }
            return folds;
        }

        private async Task ExportAsync(DocumentLine closing, DiagramExport what)
        {
            try
            {
                if (BlockClosedBy(closing) is not { TooLarge: false } block) return;
                string? server = _services.KrokiServer();
                if (block.Kind.NeedsKroki && server == null) return;

                // Exports are the light drawing (R1): a dark picture is unreadable on white paper.
                var request = RequestFor(block, PadPalette.Light, server);
                var result = _services.Renderer.TryGetCached(request.Key, out var cached)
                    ? cached
                    : await _services.Renderer.RenderAsync(request, new object());
                if (!result.IsPicture)
                {
                    _services.ShowStatus("The picture could not be made.");
                    return;
                }

                switch (what)
                {
                    case DiagramExport.Copy:
                        if (!_services.TrySetClipboardImage(result.Png!)) _services.ShowStatus("Clipboard busy, try again");
                        break;
                    case DiagramExport.Png:
                        Save(result.Png!, "diagram.png", "PNG picture (*.png)|*.png");
                        break;
                    default:
                        Save(Encoding.UTF8.GetBytes(result.Svg!), "diagram.svg", "SVG picture (*.svg)|*.svg");
                        break;
                }
            }
            catch (Exception ex)
            {
                _services.Warn("Exporting a diagram failed (" + ex.GetType().Name + ")");
                _services.ShowStatus("The picture could not be made.");
            }
        }

        private void Save(byte[] bytes, string name, string filter)
        {
            string? path = _services.AskSavePath(name, filter);
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                File.WriteAllBytes(path, bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                _services.Warn("Saving a diagram failed (" + ex.GetType().Name + ")");
                _services.ShowStatus("The picture could not be saved.");
            }
        }

        /// <summary>One block's pictures: the one shown, the drawing asked for, and the last text whose drawing ended.</summary>
        private sealed class BlockState
        {
            public DiagramResult? Shown;
            public string? PendingKey;
            public string? SettledKey;
            public int SettledGeneration;
        }
    }
}
