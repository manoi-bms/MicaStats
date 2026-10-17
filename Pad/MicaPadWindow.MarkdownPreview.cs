using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Services.Pad;
using Control = System.Windows.Controls.Control;
using IScrollInfo = System.Windows.Controls.Primitives.IScrollInfo;

namespace Kil0bitSystemMonitor.Pad
{
    public partial class MicaPadWindow
    {
        private DispatcherTimer? _markdownPreviewTimer;
        private EventHandler? _markdownVaultVisibilityChanged;
        private TextDocument? _markdownPreviewDocument;
        private string? _markdownPreviewNoteId;
        private bool _updatingMarkdownPreview;
        private bool _markdownPreviewHidden;
        private MarkdownScrollOrigin _markdownScrollOrigin;
        private bool _markdownScrollQueued;
        private bool _applyingMarkdownScroll;
        private int _markdownScrollLifecycle;
        private double? _expectedMarkdownSourceOffset;
        private double? _expectedMarkdownPreviewOffset;

        private enum MarkdownScrollOrigin
        {
            None,
            Source,
            Preview,
        }

        internal bool CanMarkdownPreview => _shown != null && ReferenceEquals(_resolved.Language, PadLanguages.Markdown);
        internal bool MarkdownPreviewVisible => MarkdownPreviewPanel.Visibility == Visibility.Visible;
        internal int MarkdownScrollTransferCount { get; private set; }

        private void ConfigureMarkdownPreview()
        {
            _markdownPreviewTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            _markdownPreviewTimer.Tick += OnMarkdownPreviewTick;
            MarkdownPreviewPanel.CloseRequested += CloseMarkdownPreview;
            MarkdownPreviewPanel.RefreshRequested += FlushMarkdownPreview;
            MarkdownPreviewPanel.ProgrammaticScrollRequested += OnMarkdownProgrammaticPreviewScroll;
            Editor.TextArea.TextView.ScrollOffsetChanged += OnMarkdownSourceScrollChanged;
            MarkdownPreviewPanel.PreviewScroller.ScrollChanged += OnMarkdownPreviewScrollChanged;
            _workspace.NoteTextChanged += OnMarkdownPreviewNoteChanged;
            IsVisibleChanged += OnMarkdownWindowVisibilityChanged;
            _markdownVaultVisibilityChanged = (_, _) =>
            {
                if (VaultCard.IsOpen) UpdateMarkdownPreviewView();
                else Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    // Store completion closes the card before it scrubs the document. Restore only
                    // after that synchronous callback returns, so the old value is never rendered.
                    if (_exiting) return;
                    UpdateMarkdownPreviewView();
                    ApplyPaneLayout();
                }));
            };
            PaneVisibilityDescriptor.AddValueChanged(VaultCard.Root, _markdownVaultVisibilityChanged);
            UpdateMarkdownPreviewView();
        }

        private void DetachMarkdownPreview()
        {
            _markdownPreviewHidden = true;
            _workspace.NoteTextChanged -= OnMarkdownPreviewNoteChanged;
            IsVisibleChanged -= OnMarkdownWindowVisibilityChanged;
            MarkdownPreviewPanel.CloseRequested -= CloseMarkdownPreview;
            MarkdownPreviewPanel.RefreshRequested -= FlushMarkdownPreview;
            MarkdownPreviewPanel.ProgrammaticScrollRequested -= OnMarkdownProgrammaticPreviewScroll;
            Editor.TextArea.TextView.ScrollOffsetChanged -= OnMarkdownSourceScrollChanged;
            MarkdownPreviewPanel.PreviewScroller.ScrollChanged -= OnMarkdownPreviewScrollChanged;
            if (_markdownPreviewTimer != null)
            {
                _markdownPreviewTimer.Stop();
                _markdownPreviewTimer.Tick -= OnMarkdownPreviewTick;
            }
            if (_markdownVaultVisibilityChanged != null)
                PaneVisibilityDescriptor.RemoveValueChanged(VaultCard.Root, _markdownVaultVisibilityChanged);
            _markdownVaultVisibilityChanged = null;
            ClearMarkdownPreview();
        }

        private void ClearMarkdownPreview()
        {
            _markdownPreviewTimer?.Stop();
            _markdownScrollLifecycle++;
            _markdownScrollQueued = false;
            _markdownScrollOrigin = MarkdownScrollOrigin.None;
            _expectedMarkdownSourceOffset = null;
            _expectedMarkdownPreviewOffset = null;
            _markdownPreviewDocument = null;
            _markdownPreviewNoteId = null;
            MarkdownPreviewPanel.Clear();
        }

        private void UpdateMarkdownPreviewView(bool refresh = false)
        {
            if (_updatingMarkdownPreview || _markdownPreviewTimer == null) return;
            _updatingMarkdownPreview = true;
            try
            {
                bool available = CanMarkdownPreview && !VaultCard.IsOpen && !_exiting;
                bool visible = available && _config.PadMarkdownSplitPreview && !_markdownPreviewHidden
                    && SearchPanel.Visibility != Visibility.Visible && AiPanel.Visibility != Visibility.Visible
                    && HistoryPanel.Visibility != Visibility.Visible && PreviewPanel.Visibility != Visibility.Visible;
                MarkdownSplitButton.IsEnabled = available;
                MarkdownSplitButton.FontWeight = visible ? FontWeights.SemiBold : FontWeights.Normal;
                MarkdownSplitButton.SetResourceReference(Control.ForegroundProperty, visible ? "Pad.Accent" : "Pad.Muted");
                MarkdownSplitButton.ToolTip = !CanMarkdownPreview
                    ? "Choose Markdown from the language menu to use split preview"
                    : visible ? "Close split preview (Ctrl+Shift+M)" : "Edit source beside a live preview (Ctrl+Shift+M)";
                MarkdownSourceHeader.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                MarkdownPreviewPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                _language.Apply(_shown == null ? PadLanguages.Plain : _resolved.Effective, sourceOnly: visible);
                ApplyEditorFont();
                if (!visible)
                {
                    ClearMarkdownPreview();
                    return;
                }
                bool changed = !ReferenceEquals(_markdownPreviewDocument, Editor.Document)
                    || !string.Equals(_markdownPreviewNoteId, _shown?.Id, StringComparison.Ordinal);
                if (changed)
                {
                    ClearMarkdownPreview();
                    _markdownPreviewDocument = Editor.Document;
                    _markdownPreviewNoteId = _shown!.Id;
                }
                if (changed || refresh) RenderMarkdownPreview(resetScroll: changed);
            }
            finally
            {
                _updatingMarkdownPreview = false;
            }
        }

        private void OnMarkdownPreviewNoteChanged(OpenNote note)
        {
            if (!ReferenceEquals(note, _shown) || !MarkdownPreviewVisible || _exiting) return;
            MarkdownPreviewPanel.SetUpdating();
            _markdownPreviewTimer?.Stop();
            _markdownPreviewTimer?.Start();
        }

        private void OnMarkdownPreviewTick(object? sender, EventArgs e) => FlushMarkdownPreview();

        internal void FlushMarkdownPreview()
        {
            _markdownPreviewTimer?.Stop();
            if (!MarkdownPreviewVisible || _exiting || VaultCard.IsOpen || _markdownPreviewHidden) return;
            RenderMarkdownPreview(resetScroll: false);
        }

        private void RenderMarkdownPreview(bool resetScroll)
        {
            _markdownPreviewTimer?.Stop();
            var document = Editor.Document;
            if (!ReferenceEquals(document, _markdownPreviewDocument) || _shown?.Id != _markdownPreviewNoteId) return;
            int length = Math.Min(document.TextLength, MarkdownPreviewPane.MaxPreviewChars + 1);
            MarkdownPreviewPanel.Show(document.GetText(0, length), resetScroll, originalLength: document.TextLength,
                                      sourcePosition: SourcePositionAtViewport());
        }

        private void OnMarkdownProgrammaticPreviewScroll(double offset) => _expectedMarkdownPreviewOffset = offset;

        private void OnMarkdownSourceScrollChanged(object? sender, EventArgs e)
        {
            if (ConsumesExpectedOffset(ref _expectedMarkdownSourceOffset, Editor.TextArea.TextView.VerticalOffset)) return;
            QueueMarkdownScroll(MarkdownScrollOrigin.Source);
        }

        private void OnMarkdownPreviewScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (ConsumesExpectedOffset(ref _expectedMarkdownPreviewOffset,
                                       MarkdownPreviewPanel.ScrollOffset)) return;
            if (Math.Abs(e.VerticalChange) > 0.1) QueueMarkdownScroll(MarkdownScrollOrigin.Preview);
        }

        private static bool ConsumesExpectedOffset(ref double? expected, double actual)
        {
            if (expected is not double target) return false;
            expected = null;
            return Math.Abs(actual - target) <= 1;
        }

        private void QueueMarkdownScroll(MarkdownScrollOrigin origin)
        {
            if (_applyingMarkdownScroll || !MarkdownPreviewVisible || _exiting || VaultCard.IsOpen
                || _markdownPreviewHidden || _shown == null || MarkdownPreviewPanel.Shown.Length == 0) return;
            _markdownScrollOrigin = origin;
            if (_markdownScrollQueued) return;
            _markdownScrollQueued = true;
            int lifecycle = _markdownScrollLifecycle;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (lifecycle != _markdownScrollLifecycle) return;
                _markdownScrollQueued = false;
                ApplyMarkdownScroll();
            }));
        }

        private void ApplyMarkdownScroll()
        {
            MarkdownScrollOrigin origin = _markdownScrollOrigin;
            _markdownScrollOrigin = MarkdownScrollOrigin.None;
            if (origin == MarkdownScrollOrigin.None || !MarkdownPreviewVisible || _shown == null
                || _exiting || VaultCard.IsOpen || _markdownPreviewHidden) return;
            _applyingMarkdownScroll = true;
            try
            {
                if (origin == MarkdownScrollOrigin.Source)
                    _expectedMarkdownPreviewOffset = MarkdownPreviewPanel.ScrollToSourcePosition(SourcePositionAtViewport());
                else
                    ScrollSourceToPosition(MarkdownPreviewPanel.SourcePositionAtViewport());
                MarkdownScrollTransferCount++;
            }
            finally
            {
                _applyingMarkdownScroll = false;
            }
        }

        private double SourcePositionAtViewport()
        {
            var view = Editor.TextArea.TextView;
            if (Editor.Document.LineCount == 0) return 1;
            var line = view.GetDocumentLineByVisualTop(view.VerticalOffset + 1);
            double top = view.GetVisualTopByDocumentLine(line.LineNumber);
            double bottom = line.LineNumber < Editor.Document.LineCount
                ? view.GetVisualTopByDocumentLine(line.LineNumber + 1) : view.DocumentHeight;
            double fraction = bottom <= top ? 0 : Math.Clamp((view.VerticalOffset - top) / (bottom - top), 0, 0.999);
            return line.LineNumber + fraction;
        }

        private void ScrollSourceToPosition(double sourcePosition)
        {
            if (Editor.Document.LineCount == 0) return;
            int bounded = Math.Clamp((int)Math.Floor(sourcePosition), 1, Editor.Document.LineCount);
            var view = Editor.TextArea.TextView;
            double top = view.GetVisualTopByDocumentLine(bounded);
            double bottom = bounded < Editor.Document.LineCount
                ? view.GetVisualTopByDocumentLine(bounded + 1) : view.DocumentHeight;
            double offset = top + (bottom - top) * (sourcePosition - Math.Floor(sourcePosition));
            var scroll = (IScrollInfo)view;
            offset = Math.Clamp(offset, 0, Math.Max(0, scroll.ExtentHeight - scroll.ViewportHeight));
            _expectedMarkdownSourceOffset = offset;
            scroll.SetVerticalOffset(offset);
        }

        private void OnMarkdownWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            _markdownPreviewHidden = e.NewValue is not true;
            UpdateMarkdownPreviewView();
            ApplyPaneLayout();
        }

        private void CloseMarkdownPreview()
        {
            _config.PadMarkdownSplitPreview = false;
            UpdateMarkdownPreviewView();
            ApplyPaneLayout();
            Editor.Focus();
        }

        internal void ToggleMarkdownPreview()
        {
            if (!CanMarkdownPreview || VaultCard.IsOpen || _exiting) return;
            if (MarkdownPreviewVisible)
            {
                CloseMarkdownPreview();
                return;
            }
            CloseAi(focusEditor: false);
            CloseSearch();
            CloseHistory();
            _config.PadMarkdownSplitPreview = true;
            UpdateMarkdownPreviewView();
            ApplyPaneLayout();
            Editor.Focus();
        }

        private void OnMarkdownSplitClick(object sender, RoutedEventArgs e) => ToggleMarkdownPreview();

        private double MarkdownPreviewAreaWidth => Math.Max(0, EditorArea.ActualWidth - SplitterWidth);
        private static double MarkdownPreviewMinimum(double area) => Math.Min(180, area / 2);

        private void ApplyMarkdownPreviewLayout()
        {
            double area = MarkdownPreviewAreaWidth;
            if (area <= 0) area = Math.Max(0, Width - SplitterWidth);
            double minimum = MarkdownPreviewMinimum(area);
            PaneColumn.MinWidth = minimum;
            PaneColumn.MaxWidth = Math.Max(minimum, area - minimum);
            EditorColumn.MinWidth = 0;
            PaneColumn.Width = new GridLength(Math.Clamp(area * _config.PadMarkdownPreviewRatio, minimum, area - minimum));
        }

        private void DropMarkdownPreviewAfterStore(OpenNote note)
        {
            if (!ReferenceEquals(note, _shown)) return;
            ClearMarkdownPreview();
            UpdateMarkdownPreviewView(refresh: true);
        }
    }
}
