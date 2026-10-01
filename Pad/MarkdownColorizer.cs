using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Markdown styled source (spec 2.3 and the Wiki.js spec 1-5): each run of a line gets the look
    /// <see cref="MarkdownStyles.LookOf"/> gives it - heading sizes, bold, italic, strike, link and
    /// marker colors, monospace code, raised and lowered scripts, dotted abbreviations. While the
    /// reading font is on (a mono family is given), code, table and front-matter lines are drawn in
    /// the editor's monospace font (R2). Lines inside a fence whose info word names a MicaPad
    /// language get that language's colors (<see cref="FenceHighlighter"/>); after an edit the rest
    /// of that block repaints, since a comment opened or a language named on one line recolors the
    /// lines below it. Only how text is drawn changes; the document is never touched.
    /// </summary>
    internal sealed class MarkdownColorizer : DocumentColorizingTransformer
    {
        private readonly MarkdownDocumentCache _cache;
        private readonly FenceHighlighter _fences;
        private readonly List<TextView> _views = new();
        private readonly Action<string>? _warn;
        private bool _fenceLogged;
        private readonly Func<PadPalette> _palette;
        private readonly Action<Exception> _onFailure;
        private readonly Func<FontFamily?> _monoFont;
        private readonly Dictionary<PadColor, Brush> _brushes = new();
        private readonly Dictionary<PadColor, TextDecoration> _dotted = new();

        /// <param name="onFailure">Told when a line cannot be formatted; that line is left as it is.</param>
        /// <param name="warn">Logs the one-time note that fence colors failed.</param>
        /// <param name="fenceDefinition">Test seam: the highlighting of a fence language id.</param>
        /// <param name="monoFont">The editor's monospace family while the reading font is on, else null.</param>
        public MarkdownColorizer(MarkdownDocumentCache cache, Func<PadPalette> palette, Action<Exception>? onFailure = null, Func<FontFamily?>? monoFont = null,
                                Action<string>? warn = null, Func<string?, IHighlightingDefinition?>? fenceDefinition = null)
        {
            _cache = cache;
            _warn = warn;
            _fences = new FenceHighlighter(cache, fenceDefinition, FenceFailed);
            _palette = palette;
            _onFailure = onFailure ?? (_ => { });
            _monoFont = monoFont ?? (() => null);
        }

        protected override void OnAddToTextView(TextView textView)
        {
            base.OnAddToTextView(textView);
            _views.Add(textView);
            if (_views.Count == 1) _cache.Edited += OnEdited;
        }

        protected override void OnRemoveFromTextView(TextView textView)
        {
            base.OnRemoveFromTextView(textView);
            _views.Remove(textView);
            if (_views.Count == 0) _cache.Edited -= OnEdited;
        }

        /// <summary>
        /// An edit is done and the structure follows it: AvalonEdit repaints only the edited lines, so
        /// the rest of a fenced block whose colors can follow from them repaints too (in the same
        /// way, before the next layout).
        /// </summary>
        private void OnEdited(TextDocument document, int first, int last)
        {
            try
            {
                if (_fences.LinesToRepaint(document, first, last) is not { } lines) return;
                int start = document.GetLineByNumber(lines.First).Offset;
                int end = document.GetLineByNumber(lines.Last).EndOffset;
                foreach (var view in _views)
                    if (ReferenceEquals(view.Document, document)) view.Redraw(start, end - start, DispatcherPriority.Normal);
            }
            catch (Exception ex)
            {
                FenceFailed(ex);
            }
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            try
            {
                var document = CurrentContext.Document;
                var facts = _cache.FactsOf(document, line.LineNumber);
                var md = MarkdownLineTokenizer.Tokenize(document.GetText(line), facts, _cache.AbbreviationsOf(document));
                var palette = _palette();
                var mono = _monoFont();
                double baseSize = CurrentContext.GlobalTextRunProperties.FontRenderingEmSize;

                if (mono != null && line.Length > 0 && (md.Block is MdBlock.Fence or MdBlock.Table or MdBlock.FrontMatter))
                    ChangeLinePart(line.Offset, line.EndOffset, element => SetFamily(element, mono));

                if (facts.Fence == MdFence.Inside) ColorizeFence(document, line, palette);

                foreach (var span in md.Spans)
                {
                    var look = MarkdownStyles.LookOf(span.Style, palette);
                    int start = line.Offset + span.Start;
                    ChangeLinePart(start, start + span.Length, element => Apply(element, look, baseSize, mono, palette));
                }
            }
            catch (Exception ex)
            {
                _onFailure(ex);
            }
        }

        /// <summary>The language colors of a line inside a fence. A failure here costs only this line its colors, never the note its Markdown look.</summary>
        private void ColorizeFence(TextDocument document, DocumentLine line, PadPalette palette)
        {
            try
            {
                if (_fences.HighlightLine(document, line.LineNumber) is not { } highlighted) return;
                foreach (var section in highlighted.Sections)
                {
                    if (section.Color == null || section.Length == 0) continue;
                    var color = section.Color;
                    ChangeLinePart(section.Offset, section.Offset + section.Length,
                        element => SyntaxPaint.Apply(element, color, palette, CurrentContext, BrushFor));
                }
            }
            catch (Exception ex)
            {
                FenceFailed(ex);
            }
        }

        private void FenceFailed(Exception ex)
        {
            if (_fenceLogged) return;
            _fenceLogged = true;
            try
            {
                _warn?.Invoke("Colors inside fenced code failed (" + ex.GetType().Name + "); those blocks are shown uncolored");
            }
            catch (Exception)
            {
                // Logging is best effort.
            }
        }

        private void Apply(VisualLineElement element, MdLook look, double baseSize, FontFamily? mono, PadPalette palette)
        {
            var properties = element.TextRunProperties;
            if (look.Foreground is PadColor foreground) properties.SetForegroundBrush(BrushFor(foreground));
            if (look.Background is PadColor background) properties.SetBackgroundBrush(BrushFor(background));
            if (look.SizeFactor != 1) properties.SetFontRenderingEmSize(baseSize * look.SizeFactor);
            if (look.Mono && mono != null) SetFamily(element, mono);

            if (look.Weight != MdWeight.Keep || look.Italic)
            {
                var face = properties.Typeface;
                var weight = look.Weight switch
                {
                    MdWeight.Bold => FontWeights.Bold,
                    MdWeight.SemiBold => FontWeights.SemiBold,
                    _ => face.Weight,
                };
                properties.SetTypeface(new Typeface(face.FontFamily, look.Italic ? FontStyles.Italic : face.Style, weight, face.Stretch));
            }

            if (look.Baseline != MdBaseline.Normal)
                properties.SetBaselineAlignment(look.Baseline == MdBaseline.Superscript ? BaselineAlignment.Superscript : BaselineAlignment.Subscript);
            if (look.Strike) AddDecorations(properties, TextDecorations.Strikethrough);
            if (look.Dotted) AddDecorations(properties, new[] { DottedUnderline(palette.MdMarker) });
        }

        private static void SetFamily(VisualLineElement element, FontFamily family)
        {
            var face = element.TextRunProperties.Typeface;
            element.TextRunProperties.SetTypeface(new Typeface(family, face.Style, face.Weight, face.Stretch));
        }

        private static void AddDecorations(VisualLineElementTextRunProperties properties, IEnumerable<TextDecoration> added)
        {
            var decorations = properties.TextDecorations == null
                ? new TextDecorationCollection()
                : new TextDecorationCollection(properties.TextDecorations);
            foreach (var decoration in added) decorations.Add(decoration);
            decorations.Freeze();
            properties.SetTextDecorations(decorations);
        }

        private TextDecoration DottedUnderline(PadColor color)
        {
            if (_dotted.TryGetValue(color, out var decoration)) return decoration;
            var pen = new Pen(BrushFor(color), 1) { DashStyle = DashStyles.Dot };
            pen.Freeze();
            decoration = new TextDecoration(TextDecorationLocation.Underline, pen, 0, TextDecorationUnit.FontRecommended, TextDecorationUnit.FontRecommended);
            decoration.Freeze();
            _dotted[color] = decoration;
            return decoration;
        }

        private Brush BrushFor(PadColor color)
        {
            if (!_brushes.TryGetValue(color, out var brush)) _brushes[color] = brush = PadThemeApplier.ToBrush(color);
            return brush;
        }
    }
}
