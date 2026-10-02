using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

using Brush = System.Windows.Media.Brush;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// AvalonEdit's highlighting, painted from the MicaPad palette instead of the definition's own
    /// colors (chosen for white pages). A named color takes its category's palette color; an
    /// unnamed one keeps its hue, nudged until it reads at 4.5:1 (<see cref="SyntaxColors.Resolve"/>).
    /// Bold and italic are kept; definition backgrounds are dropped. The shared definitions are
    /// never modified: the palette is read at draw time, so a theme switch only needs a redraw.
    /// For a language that calls functions as <c>name(...)</c>, the highlighter also gives the names
    /// the definition leaves plain the Function color (<see cref="FunctionCallHighlighter"/>).
    /// </summary>
    internal sealed class ThemedHighlightingColorizer : HighlightingColorizer
    {
        private readonly Func<PadPalette> _palette;
        private readonly Action<Exception> _onFailure;
        private readonly bool _functionCalls;
        private readonly bool _tightCalls;
        private readonly Action<string>? _warn;
        private readonly Dictionary<PadColor, Brush> _brushes = new();

        /// <param name="onFailure">Told when highlighting fails; the text is left uncolored.</param>
        /// <param name="functionCalls">Color function names too (<see cref="PadLanguage.FunctionCalls"/>).</param>
        /// <param name="warn">Logs the one-time note that function colors failed; the other colors stay.</param>
        /// <param name="tightCalls">A call's <c>(</c> follows its name directly (<see cref="PadLanguage.TightCalls"/>).</param>
        public ThemedHighlightingColorizer(IHighlightingDefinition definition, Func<PadPalette> palette, Action<Exception>? onFailure = null,
                                           bool functionCalls = false, Action<string>? warn = null, bool tightCalls = false)
            : base(definition)
        {
            _palette = palette;
            _onFailure = onFailure ?? (_ => { });
            _functionCalls = functionCalls;
            _tightCalls = tightCalls;
            _warn = warn;
        }

        /// <summary>AvalonEdit's highlighter for the document, wrapped in the function pass when the language has one.</summary>
        protected override IHighlighter CreateHighlighter(TextView textView, TextDocument document)
        {
            var highlighter = base.CreateHighlighter(textView, document);
            return _functionCalls ? new FunctionCallHighlighter(highlighter, FunctionsFailed, _tightCalls) : highlighter;
        }

        /// <summary>The function pass threw (reported once per document): those lines are shown without function colors.</summary>
        private void FunctionsFailed(Exception ex)
        {
            try
            {
                _warn?.Invoke("Function colors failed (" + ex.GetType().Name + "); some lines are shown without them");
            }
            catch (Exception)
            {
                // Logging is best effort.
            }
        }

        /// <summary>The highlighter itself (AvalonEdit's rule engine) runs in here; a failure leaves the line uncolored.</summary>
        protected override void Colorize(ITextRunConstructionContext context)
        {
            try
            {
                base.Colorize(context);
            }
            catch (Exception ex)
            {
                _onFailure(ex);
            }
        }

        protected override void ApplyColorToElement(VisualLineElement element, HighlightingColor color)
        {
            try
            {
                SyntaxPaint.Apply(element, color, _palette(), CurrentContext, BrushFor);
            }
            catch (Exception ex)
            {
                _onFailure(ex);
            }
        }

        private Brush BrushFor(PadColor color)
        {
            if (!_brushes.TryGetValue(color, out var brush)) _brushes[color] = brush = PadThemeApplier.ToBrush(color);
            return brush;
        }
    }
}
