using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Folding for one editor (spec 2.5): installs AvalonEdit's FoldingManager (and so the fold
    /// margin) only for a language that folds, and recomputes 500 ms after the last edit. The
    /// installed manager itself opens a fold the caret moves into (AvalonEdit's
    /// FoldingManagerInstallation), so Find, Go to line and restore never leave it hidden. Fold
    /// state is not saved.
    /// </summary>
    internal sealed class FoldingController
    {
        private readonly TextEditor _editor;
        private readonly DispatcherTimer _timer;
        private FoldingManager? _manager;
        private TextDocument? _document;
        private PadLanguage _language = PadLanguages.Plain;

        public FoldingController(TextEditor editor)
        {
            _editor = editor;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (s, e) =>
            {
                _timer.Stop();
                Update();
            };
        }

        /// <summary>True while folding is installed.</summary>
        internal bool IsActive => _manager != null;

        /// <summary>The manager while folding is installed; for tests.</summary>
        internal FoldingManager? Manager => _manager;

        /// <summary>Installs folding for <paramref name="language"/> on the editor's current document, or nothing if it does not fold.</summary>
        public void Attach(PadLanguage language)
        {
            Detach();
            _language = language;
            if (language.Fold == PadFoldKind.None) return;

            _manager = FoldingManager.Install(_editor.TextArea);
            _document = _editor.Document;
            _document.Changed += OnChanged;
            Update();
        }

        /// <summary>Removes folding and its margin.</summary>
        public void Detach()
        {
            _timer.Stop();
            if (_document != null) _document.Changed -= OnChanged;
            _document = null;
            if (_manager != null)
            {
                FoldingManager.Uninstall(_manager);
                _manager = null;
            }
        }

        /// <summary>Recomputes the folds now. A failure is logged and leaves the old folds in place.</summary>
        internal void Update()
        {
            if (_manager == null || _document == null) return;
            try
            {
                var foldings = Compute(_document, _language, out int firstError);
                _manager.UpdateFoldings(foldings, firstError);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Warn("pad", "Folding could not be updated (" + ex.GetType().Name + ")");
            }
        }

        private void OnChanged(object? sender, DocumentChangeEventArgs e)
        {
            // A change that detached folding still reaches this handler (AvalonEdit's handler snapshot).
            if (!ReferenceEquals(sender, _document)) return;
            _timer.Stop();
            _timer.Start();
        }

        /// <summary>The folds of the document; <paramref name="firstError"/> is where XML stopped parsing, or -1.</summary>
        private static IEnumerable<NewFolding> Compute(TextDocument document, PadLanguage language, out int firstError)
        {
            firstError = -1;
            switch (language.Fold)
            {
                case PadFoldKind.Xml:
                    return new XmlFoldingStrategy().CreateNewFoldings(document, out firstError);
                case PadFoldKind.Braces:
                    return BraceFolding.Compute(document.Text, BraceSyntax.For(language.Id)).Select(f => new NewFolding(f.Start, f.End));
                case PadFoldKind.Headings:
                    return HeadingFolding.Compute(document.Text).Select(f => new NewFolding(f.Start, f.End));
                default:
                    return Array.Empty<NewFolding>();
            }
        }
    }
}
