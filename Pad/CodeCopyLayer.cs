using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

// UseWindowsForms puts System.Windows.Forms and System.Drawing in scope; these names exist in both.
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Control = System.Windows.Controls.Control;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using SystemFonts = System.Windows.SystemFonts;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// The Copy button of fenced blocks (ruling R5): a layer over the text holding one small button,
    /// shown at the top-right of the fenced, <c>$$</c> or diagram block under the mouse, on the
    /// block's first visible line. A click hands the block's inside lines, exactly as they are in
    /// the note, to the copy action; the button takes no focus, so the caret and selection stay
    /// where they were. Scrolling, an edit or the mouse leaving the text hides it until the mouse
    /// moves again.
    /// </summary>
    internal sealed class CodeCopyLayer : FrameworkElement
    {
        /// <summary>The gap between the button and the text view's right edge, and between the line's top and the button.</summary>
        private const double RightGap = 4;
        private const double TopGap = 2;

        private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
        private static readonly Size Unbounded = new(double.PositiveInfinity, double.PositiveInfinity);

        private readonly TextView _view;
        private readonly MarkdownDocumentCache _cache;
        private readonly Func<PadPalette> _palette;
        private readonly Action<string> _copy;
        private readonly Action<Exception> _onFailure;
        private readonly Button _button;
        private TextDocument? _document;
        private PadPalette? _paintedWith;
        private Brush? _rest;
        private Brush? _hover;
        private int _opening;
        private double _top;

        /// <summary>The width the text view last laid the layer out at (during that layout its own ActualWidth is still the old one).</summary>
        private double _width;

        /// <param name="copy">Takes the code of the block whose button was clicked.</param>
        /// <param name="onFailure">Told when placing the button or copying failed; the button hides and the note is untouched.</param>
        public CodeCopyLayer(TextView view, MarkdownDocumentCache cache, Func<PadPalette> palette, Action<string> copy, Action<Exception> onFailure)
        {
            _view = view;
            _cache = cache;
            _palette = palette;
            _copy = copy;
            _onFailure = onFailure;

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = "\uE8C8", FontFamily = IconFont, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = "Copy", VerticalAlignment = VerticalAlignment.Center });
            // The font size and padding of a diagram picture's Hide code button, in MicaPad's own colors.
            _button = new Button
            {
                Content = content,
                ToolTip = "Copy code",
                Focusable = false,
                Cursor = Cursors.Arrow,
                Template = BoxTemplate(),
                FontFamily = SystemFonts.MessageFontFamily,
                FontSize = 12,
                Padding = new Thickness(8, 2, 8, 2),
                BorderThickness = new Thickness(1),
                Visibility = Visibility.Collapsed,
            };
            AutomationProperties.SetName(_button, "Copy code");
            _button.Click += (s, e) => Copy();
            _button.MouseEnter += (s, e) => PaintBackground();
            _button.MouseLeave += (s, e) => PaintBackground();
            // No editor menu over the button (the window's right-click handler leaves the caret alone there too).
            _button.ContextMenuOpening += (s, e) => e.Handled = true;
            AddVisualChild(_button);
            Paint();

            _view.MouseMove += OnMouseMove;
            _view.MouseLeave += OnHide;
            _view.ScrollOffsetChanged += OnHide;
            _view.DocumentChanged += OnDocumentChanged;
            Follow(_view.Document);
        }

        /// <summary>True while the button is shown.</summary>
        internal bool ButtonShown => _button.Visibility == Visibility.Visible;

        /// <summary>The opening line of the block the button copies, or 0 while it is hidden.</summary>
        internal int BlockOpeningLine => ButtonShown ? _opening : 0;

        /// <summary>Where the button sits, in text-view coordinates; empty while it is hidden.</summary>
        internal Rect ButtonBounds
        {
            get
            {
                if (!ButtonShown) return Rect.Empty;
                var size = _button.DesiredSize;
                return new Rect(Math.Max(0, _width - RightGap - size.Width), _top + TopGap, size.Width, size.Height);
            }
        }

        /// <summary>The button itself; for tests.</summary>
        internal Button Button => _button;

        /// <summary>True when <paramref name="element"/> is the button or sits inside it (a right-click there must leave the caret and selection alone).</summary>
        internal static bool IsInside(DependencyObject? element)
        {
            while (element != null)
            {
                if (element is CodeCopyLayer) return true;
                element = (element is Visual ? VisualTreeHelper.GetParent(element) : null) ?? LogicalTreeHelper.GetParent(element);
            }
            return false;
        }

        /// <summary>What the mouse over <paramref name="point"/> (text-view coordinates) does: the button for the block of the line there, or none.</summary>
        internal void MouseAt(Point point)
        {
            // Over the button itself: it can cover the top of a line outside its block (a small zoom,
            // or only the closing fence in view), and must not hide as the mouse moves onto it.
            if (ButtonShown && ButtonBounds.Contains(point)) return;
            VisualLine? visual = null;
            try
            {
                if (point.Y >= 0) visual = _view.GetVisualLineFromVisualTop(point.Y + _view.VerticalOffset);
            }
            catch (Exception ex)
            {
                _onFailure(ex);
            }
            if (visual == null) Hide();
            else ShowFor(visual.FirstDocumentLine.LineNumber);
        }

        /// <summary>
        /// What a mouse move over line <paramref name="lineNumber"/> does: shows the button for the
        /// fenced block the line belongs to (its fences included), or hides it when the line is in
        /// none or the block has no code.
        /// </summary>
        internal void ShowFor(int lineNumber)
        {
            try
            {
                var document = _view.Document;
                if (document == null || BlockAt(document, lineNumber) is not { } block || !FirstVisibleTop(block.Opening, block.Last, out double top))
                {
                    Hide();
                    return;
                }
                if (ButtonShown && _opening == block.Opening && _top == top) return;
                _opening = block.Opening;
                _top = top;
                _button.Visibility = Visibility.Visible;
                _button.Measure(Unbounded);
                InvalidateArrange();
            }
            catch (Exception ex)
            {
                Hide();
                _onFailure(ex);
            }
        }

        /// <summary>Clicks the button, as the mouse does.</summary>
        internal void Click() => _button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, _button));

        /// <summary>
        /// Paints the button in the palette's colors: the popup's box, border and soft text, and the
        /// flat buttons' hover tint over the box under the mouse. A theme switch reaches it through
        /// <see cref="EditorLanguage.Redraw"/>.
        /// </summary>
        internal void Paint()
        {
            var palette = _palette();
            if (ReferenceEquals(palette, _paintedWith)) return;
            _paintedWith = palette;
            _rest = PadThemeApplier.ToBrush(palette.Popup);
            _hover = PadThemeApplier.ToBrush(palette.Hover.Over(palette.Popup));
            _button.BorderBrush = PadThemeApplier.ToBrush(palette.PopupBorder);
            _button.Foreground = PadThemeApplier.ToBrush(palette.TextSoft);
            PaintBackground();
        }

        /// <summary>Stops following the text view and its document.</summary>
        public void Detach()
        {
            Hide();
            _view.MouseMove -= OnMouseMove;
            _view.MouseLeave -= OnHide;
            _view.ScrollOffsetChanged -= OnHide;
            _view.DocumentChanged -= OnDocumentChanged;
            Follow(null);
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) =>
            index == 0 ? _button : throw new ArgumentOutOfRangeException(nameof(index));

        /// <summary>Takes no room: the text view lays every layer over its whole area.</summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            _button.Measure(Unbounded);
            return default;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _width = finalSize.Width;
            var bounds = ButtonBounds;
            _button.Arrange(bounds.IsEmpty ? default : bounds);
            return finalSize;
        }

        /// <summary>
        /// The fenced or <c>$$</c> block line <paramref name="lineNumber"/> is part of: its opening
        /// line, its last inside line and its last line (the closing fence, or the note's last line
        /// when it never closes). Null when the line is in no block or the block has no code: no
        /// inside lines, or only blank ones.
        /// </summary>
        private (int Opening, int LastInside, int Last)? BlockAt(TextDocument document, int lineNumber)
        {
            if (lineNumber < 1 || lineNumber > document.LineCount) return null;
            int opening = _cache.KindOf(document, lineNumber) switch
            {
                MdFence.Inside => _cache.BlockOpeningOf(document, lineNumber),
                MdFence.Delimiter => _cache.OpeningLineOf(document, lineNumber) is > 0 and int opened ? opened : lineNumber,
                _ => 0,
            };
            if (opening == 0) return null;
            int closing = _cache.ClosingLineOf(document, opening);
            int last = closing > 0 ? closing : document.LineCount;
            int lastInside = closing > 0 ? closing - 1 : document.LineCount;
            // An unclosed block runs to the note's end; the empty line after a final line break is none of its code.
            if (closing == 0 && lastInside > opening && document.GetLineByNumber(lastInside).Length == 0) lastInside--;
            return lastInside > opening && HasCode(document, opening + 1, lastInside) ? (opening, lastInside, last) : null;
        }

        /// <summary>True when a line from <paramref name="from"/> to <paramref name="to"/> holds more than white space; stops at the first that does.</summary>
        private static bool HasCode(TextDocument document, int from, int to)
        {
            for (int number = from; number <= to; number++)
            {
                var line = document.GetLineByNumber(number);
                for (int offset = line.Offset; offset < line.EndOffset; offset++)
                    if (!char.IsWhiteSpace(document.GetCharAt(offset))) return true;
            }
            return false;
        }

        /// <summary>The top of the block's first visible line on screen (never above the view's top); false when no line of it is shown.</summary>
        private bool FirstVisibleTop(int opening, int last, out double top)
        {
            top = 0;
            // An edit or a selection change since the last render left the lines to be built again.
            _view.EnsureVisualLines();
            foreach (var visual in _view.VisualLines)
            {
                if (visual.LastDocumentLine.LineNumber < opening) continue;
                if (visual.FirstDocumentLine.LineNumber > last) return false;
                top = Math.Max(0, visual.VisualTop - _view.VerticalOffset);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Hands the shown block's inside lines to the copy action: their own line breaks kept, none
        /// after the last (an unclosed block leaves out the empty line after the note's final break).
        /// </summary>
        private void Copy()
        {
            try
            {
                var document = _view.Document;
                if (document == null || !ButtonShown || BlockAt(document, _opening) is not { } block) return;
                var first = document.GetLineByNumber(block.Opening + 1);
                var last = document.GetLineByNumber(block.LastInside);
                _copy(document.GetText(first.Offset, last.EndOffset - first.Offset));
            }
            catch (Exception ex)
            {
                _onFailure(ex);
            }
        }

        private void Hide()
        {
            if (!ButtonShown) return;
            _button.Visibility = Visibility.Collapsed;
            _opening = 0;
        }

        private void PaintBackground() => _button.Background = _button.IsMouseOver ? _hover : _rest;

        private void OnMouseMove(object sender, MouseEventArgs e) => MouseAt(e.GetPosition(_view));

        private void OnHide(object? sender, EventArgs e) => Hide();

        private void OnDocumentChanged(object? sender, EventArgs e)
        {
            Hide();
            Follow(_view.Document);
        }

        private void OnTextChanged(object? sender, DocumentChangeEventArgs e) => Hide();

        /// <summary>Hides the button on every edit of <paramref name="document"/> (and stops watching the one before).</summary>
        private void Follow(TextDocument? document)
        {
            if (_document != null) _document.Changed -= OnTextChanged;
            _document = document;
            if (_document != null) _document.Changed += OnTextChanged;
        }

        /// <summary>A rounded box like MicaPad's flat buttons, drawn with the button's own brushes.</summary>
        private static ControlTemplate BoxTemplate()
        {
            var box = new FrameworkElementFactory(typeof(Border));
            box.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            box.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            box.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            box.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            box.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            box.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            return new ControlTemplate(typeof(Button)) { VisualTree = box };
        }
    }
}
