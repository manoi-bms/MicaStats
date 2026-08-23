using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kil0bitSystemMonitor.Services.Capture;

// System.Drawing / System.Windows.Forms are in global scope; bind the shared names to WPF.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Cursors = System.Windows.Input.Cursors;

namespace Kil0bitSystemMonitor.Controls
{
    /// <summary>
    /// The editing surface: the captured image plus its annotations, with the active tool bound
    /// to the mouse.
    ///
    /// <para>
    /// The canvas works in image pixels and applies a single zoom transform at draw time, so a
    /// mark placed at 40% zoom lands on exactly the pixel the user pointed at. Export runs the
    /// very same <see cref="AnnotationRenderer"/> at native resolution — the screen is a preview
    /// of the file, not a separate drawing path that might disagree with it.
    /// </para>
    /// </summary>
    public sealed class AnnotationCanvas : FrameworkElement
    {
        private readonly AnnotationRenderer.RedactionCache _redactions = new();

        private Annotation? _preview;
        private List<ImgPoint>? _stroke;
        private ImgPoint _dragStart;
        private bool _dragging;
        private PixelRect _cropPreview;

        public AnnotationCanvas()
        {
            Focusable = true;
            ClipToBounds = true;
            Cursor = Cursors.Cross;
        }

        public BitmapSource? Image { get; private set; }
        public AnnotationDocument? Document { get; private set; }

        public CaptureTool Tool { get; set; } = CaptureTool.Arrow;
        public string ColorHex { get; set; } = "#FF453A";
        public double Thickness { get; set; } = 3;
        public RedactStyle RedactStyle { get; set; } = RedactStyle.Pixelate;
        public double TextSize { get; set; } = 20;

        /// <summary>Display scale: device-independent units per image pixel.</summary>
        public double Zoom { get; private set; } = 1;

        /// <summary>The crop rectangle currently being dragged, empty when not cropping.</summary>
        public PixelRect PendingCrop => _cropPreview;

        public event Action? DocumentChanged;

        /// <summary>Raised when the text tool is clicked, so the host can show an input box.</summary>
        public event Action<ImgPoint, Point>? TextRequested;

        public void Load(BitmapSource image)
        {
            Image = image;
            Document = new AnnotationDocument(new PixelRect(0, 0, image.PixelWidth, image.PixelHeight));
            _redactions.Clear();
            _preview = null;
            _cropPreview = default;
            InvalidateMeasure();
            InvalidateVisual();
            DocumentChanged?.Invoke();
        }

        public void SetZoom(double zoom)
        {
            Zoom = Math.Clamp(zoom, 0.05, 8);
            InvalidateMeasure();
            InvalidateVisual();
        }

        /// <summary>Picks the zoom that fits the current crop inside the given viewport.</summary>
        public void ZoomToFit(Size viewport)
        {
            if (Document == null || viewport.Width <= 0 || viewport.Height <= 0) return;
            var crop = Document.Crop;
            if (crop.IsEmpty) return;
            // Never scale above 1: enlarging a screenshot only blurs it.
            SetZoom(Math.Min(1, Math.Min(viewport.Width / crop.Width, viewport.Height / crop.Height)));
        }

        public void CommitText(ImgPoint at, string text)
        {
            if (Document == null || string.IsNullOrWhiteSpace(text)) return;
            Document.Add(new TextAnnotation(at, text.Trim(), TextSize) { ColorHex = ColorHex, Thickness = Thickness });
            Changed();
        }

        public void ApplyPendingCrop()
        {
            if (Document == null || _cropPreview.IsEmpty) return;
            Document.ApplyCrop(_cropPreview);
            _cropPreview = default;
            Changed();
            InvalidateMeasure();
        }

        public void Undo() { if (Document?.Undo() == true) { Changed(); InvalidateMeasure(); } }
        public void Redo() { if (Document?.Redo() == true) { Changed(); InvalidateMeasure(); } }
        public void ClearAll() { Document?.Clear(); Changed(); }

        public void ResetCrop()
        {
            Document?.ResetCrop();
            _cropPreview = default;
            Changed();
            InvalidateMeasure();
        }

        private void Changed()
        {
            InvalidateVisual();
            DocumentChanged?.Invoke();
        }

        // ----- Layout ------------------------------------------------------------------------

        protected override Size MeasureOverride(Size availableSize)
        {
            if (Document == null) return new Size(0, 0);
            var crop = Document.Crop;
            return new Size(crop.Width * Zoom, crop.Height * Zoom);
        }

        /// <summary>Mouse position (DIP) to a point in the ORIGINAL image's pixel space.</summary>
        private ImgPoint ToImage(Point p)
        {
            var crop = Document?.Crop ?? default;
            return new ImgPoint(p.X / Zoom + crop.X, p.Y / Zoom + crop.Y);
        }

        // ----- Input -------------------------------------------------------------------------

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (Document == null || Image == null) { base.OnMouseLeftButtonDown(e); return; }
            Focus();
            var at = ToImage(e.GetPosition(this));

            switch (Tool)
            {
                case CaptureTool.Text:
                    TextRequested?.Invoke(at, e.GetPosition(this));
                    break;

                case CaptureTool.Step:
                    Document.Add(new StepAnnotation(at, Document.NextStepNumber, Math.Max(11, Thickness * 4.5))
                    { ColorHex = ColorHex, Thickness = Thickness });
                    Changed();
                    break;

                case CaptureTool.Pen:
                case CaptureTool.Highlighter:
                    _stroke = new List<ImgPoint> { at };
                    _dragging = true;
                    CaptureMouse();
                    break;

                default:
                    _dragStart = at;
                    _dragging = true;
                    CaptureMouse();
                    break;
            }
            base.OnMouseLeftButtonDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_dragging || Document == null) { base.OnMouseMove(e); return; }
            var at = ToImage(e.GetPosition(this));

            if (_stroke != null)
            {
                _stroke.Add(at);
                _preview = new StrokeAnnotation(new List<ImgPoint>(_stroke), Tool == CaptureTool.Highlighter)
                { ColorHex = ColorHex, Thickness = Thickness };
            }
            else if (Tool == CaptureTool.Crop)
            {
                _cropPreview = PixelRect.FromPoints(
                    (int)Math.Round(_dragStart.X), (int)Math.Round(_dragStart.Y),
                    (int)Math.Round(at.X), (int)Math.Round(at.Y));
            }
            else
            {
                _preview = BuildShape(_dragStart, at);
            }

            InvalidateVisual();
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (!_dragging || Document == null) { base.OnMouseLeftButtonUp(e); return; }
            _dragging = false;
            ReleaseMouseCapture();

            if (_stroke != null)
            {
                if (_stroke.Count > 0)
                    Document.Add(new StrokeAnnotation(_stroke, Tool == CaptureTool.Highlighter)
                    { ColorHex = ColorHex, Thickness = Thickness });
                _stroke = null;
                _preview = null;
                Changed();
            }
            else if (Tool == CaptureTool.Crop)
            {
                // The crop is left pending so the user can confirm it from the toolbar.
                InvalidateVisual();
            }
            else if (_preview != null)
            {
                // Discard accidental click-sized shapes.
                if (IsMeaningful(_preview)) Document.Add(_preview);
                _preview = null;
                Changed();
            }

            base.OnMouseLeftButtonUp(e);
        }

        private static bool IsMeaningful(Annotation a) => a switch
        {
            ShapeAnnotation s => Math.Abs(s.End.X - s.Start.X) > 2 || Math.Abs(s.End.Y - s.Start.Y) > 2,
            RedactAnnotation r => Math.Abs(r.End.X - r.Start.X) > 2 && Math.Abs(r.End.Y - r.Start.Y) > 2,
            _ => true,
        };

        private Annotation BuildShape(ImgPoint from, ImgPoint to)
        {
            if (Tool == CaptureTool.Redact)
                return new RedactAnnotation(from, to, RedactStyle)
                { ColorHex = ColorHex, Thickness = Thickness, Strength = Math.Max(6, Thickness * 4) };

            var shape = Tool switch
            {
                CaptureTool.Rectangle => CaptureTool.Rectangle,
                CaptureTool.Ellipse => CaptureTool.Ellipse,
                CaptureTool.Line => CaptureTool.Line,
                _ => CaptureTool.Arrow,
            };
            return new ShapeAnnotation(shape, from, to) { ColorHex = ColorHex, Thickness = Thickness };
        }

        // ----- Rendering ---------------------------------------------------------------------

        protected override void OnRender(DrawingContext dc)
        {
            if (Image == null || Document == null) return;
            var crop = Document.Crop;
            if (crop.IsEmpty) return;

            dc.PushClip(new RectangleGeometry(new Rect(0, 0, crop.Width * Zoom, crop.Height * Zoom)));
            dc.PushTransform(new ScaleTransform(Zoom, Zoom));
            dc.PushTransform(new TranslateTransform(-crop.X, -crop.Y));

            dc.DrawImage(Image, new Rect(0, 0, Image.PixelWidth, Image.PixelHeight));
            AnnotationRenderer.Render(dc, Image, Document.Items, _preview, _redactions);

            if (!_cropPreview.IsEmpty) DrawCropPreview(dc);

            dc.Pop();
            dc.Pop();
            dc.Pop();
        }

        /// <summary>Dims everything the pending crop would discard, so the result is obvious.</summary>
        private void DrawCropPreview(DrawingContext dc)
        {
            var full = Document!.Crop;
            var keep = new Rect(_cropPreview.X, _cropPreview.Y, _cropPreview.Width, _cropPreview.Height);
            var dim = new SolidColorBrush(Color.FromArgb(0x99, 0x08, 0x08, 0x0C));
            dim.Freeze();

            dc.DrawRectangle(dim, null, new Rect(full.X, full.Y, full.Width, keep.Top - full.Y));
            dc.DrawRectangle(dim, null, new Rect(full.X, keep.Bottom, full.Width, full.Bottom - keep.Bottom));
            dc.DrawRectangle(dim, null, new Rect(full.X, keep.Top, keep.Left - full.X, keep.Height));
            dc.DrawRectangle(dim, null, new Rect(keep.Right, keep.Top, full.Right - keep.Right, keep.Height));

            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x3F, 0xD2, 0xE4)), 1.5 / Zoom);
            pen.Freeze();
            dc.DrawRectangle(null, pen, keep);
        }

        /// <summary>
        /// Renders the finished image at native resolution: the crop region, its annotations,
        /// and nothing else — no zoom, no crop-preview dimming, no selection chrome.
        /// </summary>
        public BitmapSource? Export()
        {
            if (Image == null || Document == null) return null;
            var crop = Document.Crop;
            if (crop.IsEmpty) return null;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new TranslateTransform(-crop.X, -crop.Y));
                dc.DrawImage(Image, new Rect(0, 0, Image.PixelWidth, Image.PixelHeight));
                AnnotationRenderer.Render(dc, Image, Document.Items, null, _redactions);
                dc.Pop();
            }

            var rtb = new RenderTargetBitmap(crop.Width, crop.Height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }
    }
}
