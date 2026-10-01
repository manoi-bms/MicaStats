using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Pad;

using FontFamily = System.Windows.Media.FontFamily;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Everything a language adds to one editor, installed and removed together: syntax colors, or
    /// Markdown formatting (colorizer, background, bullets, emoji, diagram pictures), and folding.
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
        private EmojiGenerator? _emoji;
        private DiagramBoard? _diagramBoard;
        private DiagramGenerator? _diagramGenerator;
        private readonly FoldingController? _folding;
        private TextDocument? _appliedTo;
        private bool _emojiLogged;

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

        /// <summary>What diagram pictures need (spec Part 3); set by the window. Null: no pictures (the history preview).</summary>
        internal DiagramServices? Diagrams { get; set; }

        /// <summary>The pictures of the shown Markdown document, or null.</summary>
        internal DiagramBoard? DiagramBoard => _diagramBoard;

        /// <summary>The editor's monospace family while the reading font is on (the window sets it); null otherwise.</summary>
        internal FontFamily? MonoFont { get; set; }

        /// <summary>The emoji codes of a line (a test seam); null uses <see cref="Emoji.Find"/>.</summary>
        internal Func<string, IReadOnlyList<(int Start, int Length, string Glyph)>>? EmojiLookup { get; set; }

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
                _markdownCache.StructureChanged += OnStructureChanged;
                _markdown = new MarkdownColorizer(_markdownCache, _palette, ReportFailure, () => MonoFont, message => Warn(message));
                _markdownBackground = new MarkdownBackgroundRenderer(_markdownCache, _palette, ReportFailure);
                _bullets = new BulletGenerator(_markdownCache, ReportFailure);
                view.LineTransformers.Add(_markdown);
                // First, so the fence shading is drawn under AvalonEdit's current-line highlight.
                view.BackgroundRenderers.Insert(0, _markdownBackground);
                view.ElementGenerators.Add(_bullets);
                _emoji = new EmojiGenerator(_markdownCache, EmojiFailed, EmojiLookup);
                view.ElementGenerators.Add(_emoji);
                InstallDiagrams();
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
        /// Draw diagrams, Kroki or the Kroki server changed in Settings: puts the pictures in or
        /// takes them out, or asks every picture again, without touching the rest of the formatting.
        /// </summary>
        internal void RefreshDiagrams()
        {
            if (_markdown == null) return;
            bool wanted = Diagrams != null && Diagrams.Enabled();
            if (!wanted)
            {
                if (_diagramBoard == null) return;
                RemoveDiagrams();
                _folding?.Update();
            }
            else if (_diagramBoard == null)
            {
                InstallDiagrams();
                _folding?.Update();
            }
            else
            {
                _diagramBoard.Refresh();
            }
            Redraw();
        }

        private void InstallDiagrams()
        {
            if (Diagrams is not { } services || !services.Enabled() || _markdownCache == null) return;
            _diagramBoard = new DiagramBoard(_editor, _markdownCache, _folding, services, _palette);
            _diagramGenerator = new DiagramGenerator(_markdownCache, _diagramBoard);
            _editor.TextArea.TextView.ElementGenerators.Add(_diagramGenerator);
        }

        private void RemoveDiagrams()
        {
            if (_diagramBoard == null) return;
            _editor.TextArea.TextView.ElementGenerators.Remove(_diagramGenerator!);
            _diagramBoard.Detach();
            _diagramBoard = null;
            _diagramGenerator = null;
        }

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

        /// <summary>
        /// Emoji failed (a missing or corrupt table): the generator already stopped showing any, so
        /// the codes stay text and the note keeps its Markdown look. Logged once per editor, with the
        /// exception type only.
        /// </summary>
        private void EmojiFailed(Exception ex)
        {
            if (_emojiLogged) return;
            _emojiLogged = true;
            try
            {
                Warn("Emoji failed (" + ex.GetType().Name + "); :codes: are shown as text");
            }
            catch (Exception)
            {
                // Logging is best effort.
            }
        }

        /// <summary>The structure moved (a fence, a table, a heading underline): lines far from the edit changed look, so repaint them all once the edit is done.</summary>
        private void OnStructureChanged() =>
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
                RemoveDiagrams();
                view.LineTransformers.Remove(_markdown);
                view.BackgroundRenderers.Remove(_markdownBackground!);
                view.ElementGenerators.Remove(_bullets!);
                view.ElementGenerators.Remove(_emoji!);
                _markdownCache!.StructureChanged -= OnStructureChanged;
                _markdownCache.Detach();
                _markdown = null;
                _markdownBackground = null;
                _bullets = null;
                _emoji = null;
                _markdownCache = null;
            }
        }
    }
}
