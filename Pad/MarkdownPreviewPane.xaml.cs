using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Localization;

using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;
using IScrollInfo = System.Windows.Controls.Primitives.IScrollInfo;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>A selectable, local Markdown preview for MicaPad's split editor.</summary>
    public sealed partial class MarkdownPreviewPane : UserControl
    {
        internal const int MaxPreviewChars = 50_000;
        private const int MaxPreviewLines = 2_000;

        private static readonly Regex LimitedSyntax = new(
            @"!\[[^\]]*\]\(|<[A-Za-z!/][^>]*>|(?<!\\)\${1,2}\S",
            RegexOptions.CultureInvariant);

        private int _scrollRestoreVersion;
        private readonly List<PreviewAnchor> _anchors = new();
        private FlowDocument? _anchorDocument;
        private double _anchorWidth = double.NaN;
        private double _anchorExtent = double.NaN;
        private int _lastAnchorLine = 1;
        private double _lastAnchorPosition = 1;
        private bool _renderFailed;
        private string _statusKey = "Live";
        private bool _rendering;
        private double? _pendingAnchorPosition;
        private bool _restoringAnchor;

        private sealed class PreviewAnchor
        {
            internal PreviewAnchor(int start, int end, TextElement element)
            {
                Start = start;
                End = Math.Max(start, end);
                Element = element;
            }

            internal int Start { get; }
            internal int End { get; }
            internal TextElement Element { get; }
            internal double Top { get; set; }
            internal double Bottom { get; set; }
            internal double[]? CodeLineTops { get; set; }
            internal TextBox? CodeBox { get; set; }
        }

        public MarkdownPreviewPane()
        {
            InitializeComponent();
            WeakEventManager<INotifyPropertyChanged, PropertyChangedEventArgs>.AddHandler(
                UiText.Provider, nameof(INotifyPropertyChanged.PropertyChanged), OnUiLanguageChanged);
            ResultBox.MarkdownDocumentFactory = MarkdownPreviewDocument.Build;
            ResultBox.Warn = OnRenderWarning;
            ResultBox.RenderFailed += OnRenderFailed;
            ResultBox.DocumentReplaced += OnPreviewDocumentChanged;
            SizeChanged += OnPreviewSizeChanged;
            LayoutUpdated += OnPreviewLayoutUpdated;
            Unloaded += (_, _) => Clear();
            ApplyTheme(dark: true);
            ShowEmptyState();
        }

        public event Action? CloseRequested;
        public event Action? RefreshRequested;
        internal event Action<double>? ProgrammaticScrollRequested;

        /// <summary>The bounded Markdown source currently held by the preview.</summary>
        internal string Shown { get; private set; } = "";

        /// <summary>True when the input exceeded the bounded preview size.</summary>
        internal bool IsLimited { get; private set; }

        /// <summary>The shared rendered body, exposed for focused UI tests.</summary>
        internal PadAnswerBox PreviewBox => ResultBox;

        // ScrollViewer publishes its cached metrics after draining deferred commands. The
        // presenter owns the current viewport, including changes during a layout pass.
        private IScrollInfo? ScrollInfo => PreviewScroller.Template?.FindName(
            "PART_ScrollContentPresenter", PreviewScroller) as IScrollInfo;
        internal double ScrollOffset => ScrollInfo?.VerticalOffset ?? PreviewScroller.VerticalOffset;
        private double ScrollExtent => ScrollInfo?.ExtentHeight ?? PreviewScroller.ExtentHeight;
        private double ScrollMaximum => Math.Max(0, ScrollExtent
            - (ScrollInfo?.ViewportHeight ?? PreviewScroller.ViewportHeight));
        private bool HasPreviewGeometry => ScrollInfo is { ViewportWidth: > 0, ViewportHeight: > 0 }
            && ResultBox.ActualWidth > 0 && ResultBox.IsArrangeValid;

        /// <summary>How many source-driven document builds occurred; focused tests ensure scrolling never rebuilds.</summary>
        internal int RenderCount { get; private set; }
        internal int AnchorCollectionBuildCount { get; private set; }
        internal int AnchorGeometryBuildCount { get; private set; }

        /// <summary>Renders Markdown immediately. The window owns typing debounce and note identity.</summary>
        public void Show(string markdown, bool resetScroll = false, int originalLength = 0,
                         int sourceLine = 0, double sourcePosition = 0)
        {
            markdown ??= "";
            int fullLength = Math.Max(markdown.Length, originalLength);
            double anchorLine = resetScroll ? 1 : sourcePosition > 0 ? sourcePosition
                : sourceLine > 0 ? sourceLine : SourcePositionAtViewport();
            string shown = Bound(markdown, fullLength, out bool limited);
            bool rebuild = _renderFailed || !string.Equals(Shown, shown, StringComparison.Ordinal);
            Shown = shown;
            IsLimited = limited;

            LimitText.Visibility = limited ? Visibility.Visible : Visibility.Collapsed;
            LimitText.Text = limited
                ? UiText.Format("Preview limited to the first {0:N0} of {1:N0} characters. The source is unchanged.", Shown.Length, fullLength)
                : "";
            CapabilityText.Visibility = LimitedSyntax.IsMatch(Shown) ? Visibility.Visible : Visibility.Collapsed;

            EmptyState.Visibility = Shown.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            PreviewScroller.Visibility = Shown.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (rebuild)
            {
                _renderFailed = false;
                _rendering = true;
                if (Shown.Length == 0) ResultBox.Clear();
                else
                {
                    try
                    {
                        ResultBox.ShowMarkdown(Shown);
                        RenderCount++;
                    }
                    finally
                    {
                        _rendering = false;
                    }
                }
                _rendering = false;
                InvalidateAnchors();
            }
            if (!_renderFailed) SetStatus(limited ? "Limited" : "Live");

            int restoreVersion = ++_scrollRestoreVersion;
            if (resetScroll)
            {
                SetScrollOffset(0);
                ScheduleAnchorRestore(1);
            }
            else if (rebuild) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (restoreVersion == _scrollRestoreVersion && Shown.Length > 0)
                    ScrollToSourcePosition(anchorLine);
            }));
        }

        /// <summary>The source line represented at the top of the rendered viewport.</summary>
        internal int SourceLineAtViewport() => (int)Math.Floor(SourcePositionAtViewport());

        internal double SourcePositionAtViewport()
        {
            if (!HasPreviewGeometry) return _lastAnchorPosition;
            EnsureAnchors();
            if (_anchors.Count == 0) return _lastAnchorPosition;
            double position = ScrollOffset + 12;
            int index = AnchorAtPosition(position);
            PreviewAnchor anchor = _anchors[index];
            if (anchor.CodeLineTops is { Length: > 0 } codeLines)
            {
                int codeIndex = LineAtPosition(codeLines, position);
                double bottom = codeIndex + 1 < codeLines.Length ? codeLines[codeIndex + 1] : anchor.Bottom;
                double within = bottom <= codeLines[codeIndex] ? 0
                    : Math.Clamp((position - codeLines[codeIndex]) / (bottom - codeLines[codeIndex]), 0, 0.999);
                _lastAnchorPosition = Math.Clamp(anchor.Start + 1 + codeIndex + within,
                                                 anchor.Start, anchor.End + 0.999);
                _lastAnchorLine = (int)Math.Floor(_lastAnchorPosition);
                return _lastAnchorPosition;
            }
            double fraction = anchor.Bottom <= anchor.Top ? 0
                : Math.Clamp((position - anchor.Top) / (anchor.Bottom - anchor.Top), 0, 1);
            _lastAnchorPosition = anchor.Start + fraction * (anchor.End - anchor.Start + 1);
            _lastAnchorLine = (int)Math.Floor(_lastAnchorPosition);
            return _lastAnchorPosition;
        }

        /// <summary>Moves the rendered viewport to the closest semantic block at or before a source line.</summary>
        internal double ScrollToSourceLine(int sourceLine) => ScrollToSourcePosition(sourceLine);

        internal double ScrollToSourcePosition(double sourcePosition)
        {
            if (!HasPreviewGeometry)
            {
                _lastAnchorPosition = sourcePosition;
                if (Shown.Length > 0) _pendingAnchorPosition = sourcePosition;
                return ScrollOffset;
            }
            EnsureAnchors();
            if (_anchors.Count == 0) return ScrollOffset;
            int sourceLine = Math.Max(1, (int)Math.Floor(sourcePosition));
            int index = AnchorForSourceLine(sourceLine);
            PreviewAnchor target = _anchors[index];
            double position;
            if (target.CodeLineTops is { Length: > 0 } codeLines && sourceLine > target.Start)
            {
                int codeIndex = Math.Clamp(sourceLine - target.Start - 1, 0, codeLines.Length - 1);
                double bottom = codeIndex + 1 < codeLines.Length ? codeLines[codeIndex + 1] : target.Bottom;
                position = codeLines[codeIndex] + (bottom - codeLines[codeIndex]) * (sourcePosition - sourceLine);
            }
            else
            {
                double fraction = Math.Clamp((sourcePosition - target.Start) / (target.End - target.Start + 1), 0, 1);
                position = target.Top + fraction * (target.Bottom - target.Top);
            }
            _lastAnchorLine = sourceLine;
            _lastAnchorPosition = sourcePosition;
            return SetScrollOffset(position - 12);
        }

        private double SetScrollOffset(double requested, bool notify = true)
        {
            if (ScrollInfo is not { } scroll) return ScrollOffset;
            double offset = Math.Clamp(requested, 0, ScrollMaximum);
            if (notify) ProgrammaticScrollRequested?.Invoke(offset);
            scroll.SetVerticalOffset(offset);
            return offset;
        }

        private void EnsureAnchors()
        {
            if (!ReferenceEquals(_anchorDocument, ResultBox.Document))
            {
                _anchors.Clear();
                _anchors.AddRange(ChatDocument.All<TextElement>(ResultBox.Document)
                    .Select(element => new PreviewAnchor(MarkdownPreviewDocument.SourceLine(element),
                                                         MarkdownPreviewDocument.SourceEndLine(element), element))
                    .Where(anchor => anchor.Start > 0)
                    .OrderBy(anchor => anchor.Start));
                foreach (PreviewAnchor anchor in _anchors)
                    if (anchor.Element is BlockUIContainer container)
                        anchor.CodeBox = VisualDescendant<TextBox>(container.Child);
                AnchorCollectionBuildCount++;
                _anchorDocument = ResultBox.Document;
                _anchorWidth = double.NaN;
                _anchorExtent = double.NaN;
            }
            if (_anchors.Count == 0) return;
            double width = ResultBox.ActualWidth;
            double extent = ScrollExtent;
            if (Math.Abs(width - _anchorWidth) <= 0.5 && Math.Abs(extent - _anchorExtent) <= 0.5) return;
            RebuildAnchorGeometry(width, extent);
        }

        private void RebuildAnchorGeometry(double width, double extent)
        {
            AnchorGeometryBuildCount++;
            double viewportOffset = ScrollOffset;
            foreach (PreviewAnchor anchor in _anchors)
            {
                if (anchor.Element is BlockUIContainer { Child: FrameworkElement child } && child.IsArrangeValid)
                {
                    anchor.Top = viewportOffset + child.TranslatePoint(new Point(), PreviewScroller).Y;
                    anchor.Bottom = anchor.Top + child.ActualHeight;
                }
                else
                {
                    Rect start = anchor.Element.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                    Rect end = anchor.Element.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
                    anchor.Top = viewportOffset + ResultBox.TranslatePoint(new Point(start.Left, start.Top), PreviewScroller).Y;
                    anchor.Bottom = viewportOffset + ResultBox.TranslatePoint(new Point(end.Right, end.Bottom), PreviewScroller).Y;
                }
                anchor.CodeLineTops = CodeLineTops(anchor.CodeBox, viewportOffset);
            }
            for (int i = 0; i < _anchors.Count - 1; i++)
                _anchors[i].Bottom = Math.Max(_anchors[i].Top + 1, _anchors[i + 1].Top);
            PreviewAnchor last = _anchors[^1];
            last.Bottom = Math.Max(last.Top + 1, Math.Max(last.Bottom, extent));
            _anchorWidth = width;
            _anchorExtent = extent;
        }

        private double[]? CodeLineTops(TextBox? code, double viewportOffset)
        {
            // A diagram keeps its source TextBox collapsed until Source is chosen. Asking
            // WPF for character rectangles in that hidden box repeatedly forces layout.
            if (code == null || !code.IsVisible || !code.IsArrangeValid || code.Text.Length == 0) return null;
            var starts = new List<int> { 0 };
            for (int i = 0; i < code.Text.Length; i++)
                if (code.Text[i] == '\n' && i + 1 < code.Text.Length) starts.Add(i + 1);
            var tops = new double[starts.Count];
            for (int i = 0; i < starts.Count; i++)
            {
                Rect rect = code.GetRectFromCharacterIndex(starts[i]);
                tops[i] = viewportOffset + code.TranslatePoint(new Point(rect.Left, rect.Top), PreviewScroller).Y;
            }
            return tops;
        }

        private static T? VisualDescendant<T>(DependencyObject? root) where T : DependencyObject
        {
            if (root == null) return null;
            if (root is T match) return match;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                if (VisualDescendant<T>(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
            return null;
        }

        private int AnchorForSourceLine(int line)
        {
            int low = 0, high = _anchors.Count - 1;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (_anchors[middle].Start <= line) low = middle;
                else high = middle - 1;
            }
            return low;
        }

        private int AnchorAtPosition(double position)
        {
            int low = 0, high = _anchors.Count - 1;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (_anchors[middle].Top <= position) low = middle;
                else high = middle - 1;
            }
            return low;
        }

        private static int LineAtPosition(IReadOnlyList<double> lines, double position)
        {
            int low = 0, high = lines.Count - 1;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (lines[middle] <= position) low = middle;
                else high = middle - 1;
            }
            return low;
        }

        private void InvalidateAnchors()
        {
            _anchorDocument = null;
            _anchors.Clear();
            _anchorWidth = double.NaN;
            _anchorExtent = double.NaN;
        }

        private void OnRenderFailed()
        {
            _renderFailed = true;
            SetStatus("Error · shown as text");
        }

        private void OnPreviewDocumentChanged()
        {
            InvalidateAnchors();
            if (!_rendering) ScheduleAnchorRestore(_lastAnchorPosition);
        }

        private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) <= 0.5) return;
            _anchorWidth = double.NaN;
            ScheduleAnchorRestore(_lastAnchorPosition);
        }

        private void ScheduleAnchorRestore(double sourceLine)
        {
            if (Shown.Length == 0 || Visibility != Visibility.Visible) return;
            _pendingAnchorPosition ??= sourceLine;
        }

        private void OnPreviewLayoutUpdated(object? sender, EventArgs e)
        {
            if (_restoringAnchor || !HasPreviewGeometry || _pendingAnchorPosition is not double sourcePosition || Shown.Length == 0) return;
            _pendingAnchorPosition = null;
            _restoringAnchor = true;
            try
            {
                _anchorWidth = double.NaN;
                ScrollToSourcePosition(sourcePosition);
            }
            finally
            {
                _restoringAnchor = false;
            }
        }

        /// <summary>Releases the displayed note and all diagram/redraw state retained by the body.</summary>
        public void Clear()
        {
            Shown = "";
            IsLimited = false;
            _scrollRestoreVersion++;
            _renderFailed = false;
            _lastAnchorLine = 1;
            _lastAnchorPosition = 1;
            _pendingAnchorPosition = null;
            ResultBox.Clear();
            InvalidateAnchors();
            SetScrollOffset(0, notify: false);
            LimitText.Text = "";
            LimitText.Visibility = Visibility.Collapsed;
            CapabilityText.Visibility = Visibility.Collapsed;
            ShowEmptyState();
        }

        /// <summary>Shows that the editor has a debounced refresh waiting.</summary>
        public void SetUpdating() => SetStatus("Updating…");

        public void ApplyTheme(bool dark)
        {
            double anchorLine = _pendingAnchorPosition ?? _lastAnchorPosition;
            PadThemeApplier.ApplyResources(Resources, dark ? PadPalette.Dark : PadPalette.Light);
            ModernWpf.ThemeManager.SetRequestedTheme(this, dark ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);
            ResultBox.ApplyTheme(dark);
            _anchorWidth = double.NaN;
            ScheduleAnchorRestore(anchorLine);
        }

        private static string Bound(string text, int originalLength, out bool limited)
        {
            int length = Math.Min(text.Length, MaxPreviewChars);
            int lines = 1;
            for (int i = 0; i < length; i++)
            {
                if (text[i] != '\n' || ++lines <= MaxPreviewLines) continue;
                length = i;
                if (length > 0 && text[length - 1] == '\r') length--;
                break;
            }
            limited = originalLength > length || text.Length > length;
            if (!limited) return text;
            if (length > 0 && length < text.Length
                && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length])) length--;
            return text.Substring(0, length);
        }

        private void ShowEmptyState()
        {
            EmptyState.Visibility = Visibility.Visible;
            PreviewScroller.Visibility = Visibility.Collapsed;
            SetStatus("Live");
        }

        private void OnRenderWarning(string message)
        {
            try
            {
                DiagnosticsLog.Warn("pad", message);
            }
            catch (Exception)
            {
                // Logging is best effort; the readable source fallback is already visible.
            }
            SetStatus("Error · shown as text");
        }

        private void SetStatus(string status)
        {
            _statusKey = status;
            StatusText.Text = UiText.Tr(status);
            StatusText.ToolTip = UiText.Tr(status switch
            {
                "Updating…" => "A preview refresh is waiting for typing to pause.",
                "Limited" => "Only the bounded beginning of this long note is rendered.",
                "Error · shown as text" => "Markdown rendering failed, so the source is shown as readable text.",
                _ => "The preview matches the latest rendered source.",
            });
        }

        private void OnUiLanguageChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(UiTextProvider.Language)) SetStatus(_statusKey);
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
        private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    }
}
