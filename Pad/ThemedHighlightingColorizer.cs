using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
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
    /// </summary>
    internal sealed class ThemedHighlightingColorizer : HighlightingColorizer
    {
        private readonly Func<PadPalette> _palette;
        private readonly Action<Exception> _onFailure;
        private readonly Dictionary<PadColor, Brush> _brushes = new();

        /// <param name="onFailure">Told when highlighting fails; the text is left uncolored.</param>
        public ThemedHighlightingColorizer(IHighlightingDefinition definition, Func<PadPalette> palette, Action<Exception>? onFailure = null)
            : base(definition)
        {
            _palette = palette;
            _onFailure = onFailure ?? (_ => { });
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
