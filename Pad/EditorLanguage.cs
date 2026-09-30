using System;
using ICSharpCode.AvalonEdit;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Everything a language adds to one editor, installed and removed together: syntax colors now,
    /// Markdown formatting (Task 6) and folding (Task 8) later. <see cref="Apply"/> always removes
    /// the previous language first, so switching tabs never piles colorizers up.
    /// </summary>
    internal sealed class EditorLanguage
    {
        private readonly TextEditor _editor;
        private readonly Func<PadPalette> _palette;
        private ThemedHighlightingColorizer? _syntax;

        public EditorLanguage(TextEditor editor, Func<PadPalette> palette)
        {
            _editor = editor;
            _palette = palette;
        }

        /// <summary>The language applied now.</summary>
        public PadLanguage Current { get; private set; } = PadLanguages.Plain;

        /// <summary>True while syntax colors are installed.</summary>
        internal bool HasSyntaxColors => _syntax != null;

        /// <summary>Shows the editor's text in <paramref name="language"/>.</summary>
        public void Apply(PadLanguage language)
        {
            Clear();
            Current = language;
            if (PadHighlighting.For(language) is { } definition)
            {
                _syntax = new ThemedHighlightingColorizer(definition, _palette);
                _editor.TextArea.TextView.LineTransformers.Add(_syntax);
            }
            Redraw();
        }

        /// <summary>Repaints; the colorizers read the palette as they draw, so a theme switch needs only this.</summary>
        public void Redraw() => _editor.TextArea.TextView.Redraw();

        private void Clear()
        {
            if (_syntax != null)
            {
                _editor.TextArea.TextView.LineTransformers.Remove(_syntax);
                _syntax = null;
            }
        }
    }
}
