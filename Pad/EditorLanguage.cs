using System;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Everything a language adds to one editor, installed and removed together: syntax colors, or
    /// Markdown formatting (colorizer, background, bullets), and folding.
    /// <see cref="Apply"/> removes the previous language first, so switching tabs never piles
    /// anything up; asked again for what is already shown, it does nothing.
    /// </summary>
    internal sealed class EditorLanguage
    {
        private static readonly object FailedMark = new();

        private readonly TextEditor _editor;
        private readonly Func<PadPalette> _palette;
        private ThemedHighlightingColorizer? _syntax;
        private MarkdownDocumentCache? _markdownCache;
        private MarkdownColorizer? _markdown;
        private MarkdownBackgroundRenderer? _markdownBackground;
        private BulletGenerator? _bullets;
        private readonly FoldingController? _folding;
        private TextDocument? _appliedTo;

        /// <summary>Documents whose formatting failed; weak, so a closed tab's document can go.</summary>
        private readonly ConditionalWeakTable<TextDocument, object> _failed = new();

        public EditorLanguage(TextEditor editor, Func<PadPalette> palette, bool folds)
        {
            _editor = editor;
            _palette = palette;
            _folding = folds ? new FoldingController(editor) : null;
        }

        /// <summary>Where a formatting failure is logged. Tests replace it, so they never write the real log.</summary>
        internal Action<string> Warn { get; set; } = message => DiagnosticsLog.Warn("pad", message);

        /// <summary>The folding of this editor, or null when it never folds (the history preview).</summary>
        internal FoldingController? Folding => _folding;

        /// <summary>The language applied now.</summary>
        public PadLanguage Current { get; private set; } = PadLanguages.Plain;

        /// <summary>True while syntax colors are installed.</summary>
        internal bool HasSyntaxColors => _syntax != null;

        /// <summary>True while Markdown formatting is installed.</summary>
        internal bool HasMarkdown => _markdown != null;

        /// <summary>
        /// Shows the editor's text in <paramref name="language"/>, or as Plain text if formatting
        /// already failed for this document. The same language on the same document it was applied
        /// to is left as it is: re-installing would unfold everything and recompute a big file's
        /// folds on every save.
        /// </summary>
        public void Apply(PadLanguage language)
        {
            var document = _editor.Document;
            if (document != null && _failed.TryGetValue(document, out _)) language = PadLanguages.Plain;
            if (ReferenceEquals(language, Current) && ReferenceEquals(document, _appliedTo)) return;

            Clear();
            Current = language;
            _appliedTo = document;
            var view = _editor.TextArea.TextView;

            if (ReferenceEquals(language, PadLanguages.Markdown))
            {
                _markdownCache = new MarkdownDocumentCache(ReportFailure);
                _markdownCache.FencesChanged += OnFencesChanged;
                _markdown = new MarkdownColorizer(_markdownCache, _palette, ReportFailure);
                _markdownBackground = new MarkdownBackgroundRenderer(_markdownCache, _palette, ReportFailure);
                _bullets = new BulletGenerator(_markdownCache, ReportFailure);
                view.LineTransformers.Add(_markdown);
                view.BackgroundRenderers.Add(_markdownBackground);
                view.ElementGenerators.Add(_bullets);
            }
            else if (PadHighlighting.For(language) is { } definition)
            {
                _syntax = new ThemedHighlightingColorizer(definition, _palette, ReportFailure);
                view.LineTransformers.Add(_syntax);
            }
            _folding?.Attach(language);
            Redraw();
        }

        /// <summary>Repaints; the colorizers read the palette as they draw, so a theme switch needs only this.</summary>
        public void Redraw() => _editor.TextArea.TextView.Redraw();

        /// <summary>
        /// A colorizer, renderer, the bullets or the fence cache failed (spec "Error handling"). The
        /// hook has already returned safely; this logs once per document and shows that document as
        /// Plain text. The hook may be mid-render or mid-change, so the switch waits for the dispatcher.
        /// </summary>
        internal void ReportFailure(Exception ex)
        {
            var document = _editor.Document;
            if (document == null || _failed.TryGetValue(document, out _)) return;
            _failed.Add(document, FailedMark);

            try
            {
                Warn(Current.Name + " formatting failed (" + ex.GetType().Name + ": " + ex.Message + "); the note is shown as plain text");
            }
            catch (Exception)
            {
                // Logging is best effort; it must not throw into rendering either.
            }

            _editor.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(_editor.Document, document)) Apply(Current);
            }));
        }

        /// <summary>A fence moved: lines far from the edit changed look, so repaint them all once the edit is done.</summary>
        private void OnFencesChanged() =>
            _editor.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Redraw));

        private void Clear()
        {
            _folding?.Detach();
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
