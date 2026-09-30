using System;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Everything a language adds to one editor, installed and removed together: syntax colors, or
    /// Markdown formatting (colorizer, background, bullets); folding comes in Task 8.
    /// <see cref="Apply"/> always removes the previous language first, so switching tabs never
    /// piles anything up.
    /// </summary>
    internal sealed class EditorLanguage
    {
        private readonly TextEditor _editor;
        private readonly Func<PadPalette> _palette;
        private ThemedHighlightingColorizer? _syntax;
        private MarkdownDocumentCache? _markdownCache;
        private MarkdownColorizer? _markdown;
        private MarkdownBackgroundRenderer? _markdownBackground;
        private BulletGenerator? _bullets;

        public EditorLanguage(TextEditor editor, Func<PadPalette> palette)
        {
            _editor = editor;
            _palette = palette;
        }

        /// <summary>The language applied now.</summary>
        public PadLanguage Current { get; private set; } = PadLanguages.Plain;

        /// <summary>True while syntax colors are installed.</summary>
        internal bool HasSyntaxColors => _syntax != null;

        /// <summary>True while Markdown formatting is installed.</summary>
        internal bool HasMarkdown => _markdown != null;

        /// <summary>Shows the editor's text in <paramref name="language"/>.</summary>
        public void Apply(PadLanguage language)
        {
            Clear();
            Current = language;
            var view = _editor.TextArea.TextView;

            if (ReferenceEquals(language, PadLanguages.Markdown))
            {
                _markdownCache = new MarkdownDocumentCache();
                _markdownCache.FencesChanged += OnFencesChanged;
                _markdown = new MarkdownColorizer(_markdownCache, _palette);
                _markdownBackground = new MarkdownBackgroundRenderer(_markdownCache, _palette);
                _bullets = new BulletGenerator(_markdownCache);
                view.LineTransformers.Add(_markdown);
                view.BackgroundRenderers.Add(_markdownBackground);
                view.ElementGenerators.Add(_bullets);
            }
            else if (PadHighlighting.For(language) is { } definition)
            {
                _syntax = new ThemedHighlightingColorizer(definition, _palette);
                view.LineTransformers.Add(_syntax);
            }
            Redraw();
        }

        /// <summary>Repaints; the colorizers read the palette as they draw, so a theme switch needs only this.</summary>
        public void Redraw() => _editor.TextArea.TextView.Redraw();

        /// <summary>A fence moved: lines far from the edit changed look, so repaint them all once the edit is done.</summary>
        private void OnFencesChanged() =>
            _editor.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Redraw));

        private void Clear()
        {
            var view = _editor.TextArea.TextView;
            if (_syntax != null)
            {
                view.LineTransformers.Remove(_syntax);
                _syntax = null;
            }
            if (_markdown != null)
            {
                view.LineTransformers.Remove(_markdown);
                view.BackgroundRenderers.Remove(_markdownBackground!);
                view.ElementGenerators.Remove(_bullets!);
                _markdownCache!.FencesChanged -= OnFencesChanged;
                _markdownCache.Detach();
                _markdown = null;
                _markdownBackground = null;
                _bullets = null;
                _markdownCache = null;
            }
        }
    }
}
