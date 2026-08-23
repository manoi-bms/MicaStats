using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Kil0bitSystemMonitor.Services.Capture;

// System.Drawing and System.Windows.Forms are in global scope (UseWindowsForms + ImplicitUsings),
// and both define these names. Bind them to the WPF types, as StatsPanelWindow.xaml.cs does.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using FlowDirection = System.Windows.FlowDirection;

namespace Kil0bitSystemMonitor.Controls
{
    /// <summary>
    /// Draws capture annotations into a <see cref="DrawingContext"/>.
    ///
    /// <para>
    /// The same routine paints the editor and the exported image, which is the point: a
    /// redaction that merely *looks* opaque on screen but exports differently would leak exactly
    /// what the user was hiding. Redactions are therefore baked into real bitmaps once and drawn
    /// as pixels in both paths, rather than relying on ambient render options or effects that
    /// apply in one path and not the other.
    /// </para>
    ///
    /// <para>All coordinates are ORIGINAL image pixels; the caller applies any crop transform.</para>
    /// </summary>
    public static class AnnotationRenderer
    {
        /// <summary>Baked redaction bitmaps, keyed by the annotation's value identity.</summary>
        public sealed class RedactionCache
        {
            private readonly Dictionary<RedactAnnotation, BitmapSource> _map = new();

            public BitmapSource? Get(RedactAnnotation key, BitmapSource source)
            {
                if (_map.TryGetValue(key, out var cached)) return cached;
                var baked = Bake(source, key);
                if (baked != null) _map[key] = baked;
                return baked;
            }

            public void Clear() => _map.Clear();
        }

        public static void Render(DrawingContext dc, BitmapSource baseImage,
                                  IReadOnlyList<Annotation> annotations, Annotation? preview,
                                  RedactionCache? cache = null)
        {
            if (dc == null) return;
            cache ??= new RedactionCache();

            foreach (var a in annotations ?? Array.Empty<Annotation>())
                Draw(dc, baseImage, a, cache);

            if (preview != null) Draw(dc, baseImage, preview, cache);
        }

        private static void Draw(DrawingContext dc, BitmapSource baseImage, Annotation a, RedactionCache cache)
        {
            switch (a)
            {
                case RedactAnnotation r: DrawRedaction(dc, baseImage, r, cache); break;
                case ShapeAnnotation s: DrawShape(dc, s); break;
                case StrokeAnnotation s: DrawStroke(dc, s); break;
                case TextAnnotation t: DrawText(dc, t); break;
                case StepAnnotation s: DrawStep(dc, s); break;
            }
        }

        // ----- Shapes ------------------------------------------------------------------------

        private static void DrawShape(DrawingContext dc, ShapeAnnotation s)
        {
            var pen = MakePen(s.ColorHex, s.Thickness);
            SolidColorBrush? brush = null;
            if (s.Filled)
            {
                brush = new SolidColorBrush(WithAlpha(ParseColor(s.ColorHex), 0.28));
                brush.Freeze();
            }

            switch (s.Shape)
            {
                case CaptureTool.Rectangle:
                    dc.DrawRectangle(brush, pen, ToRect(s.Start, s.End));
                    break;

                case CaptureTool.Ellipse:
                {
                    var r = ToRect(s.Start, s.End);
                    dc.DrawEllipse(brush, pen, new Point(r.X + r.Width / 2, r.Y + r.Height / 2),
                        r.Width / 2, r.Height / 2);
                    break;
                }

                case CaptureTool.Line:
                    dc.DrawLine(pen, ToPoint(s.Start), ToPoint(s.End));
                    break;

                case CaptureTool.Arrow:
                    DrawArrow(dc, s);
                    break;
            }
        }

        /// <summary>
        /// A line with a solid triangular head. The head scales with stroke width so a thick
        /// arrow does not end in a pinpoint, and the shaft stops short of the tip so the two
        /// do not overlap into a blob.
        /// </summary>
        private static void DrawArrow(DrawingContext dc, ShapeAnnotation s)
        {
            Point from = ToPoint(s.Start), to = ToPoint(s.End);
            double dx = to.X - from.X, dy = to.Y - from.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.5) return;

            double ux = dx / len, uy = dy / len;
            double head = Math.Max(9, s.Thickness * 3.6);
            head = Math.Min(head, len);

            var shaftEnd = new Point(to.X - ux * head * 0.85, to.Y - uy * head * 0.85);
            dc.DrawLine(MakePen(s.ColorHex, s.Thickness), from, shaftEnd);

            double halfWidth = head * 0.42;
            var left = new Point(to.X - ux * head - uy * halfWidth, to.Y - uy * head + ux * halfWidth);
            var right = new Point(to.X - ux * head + uy * halfWidth, to.Y - uy * head - ux * halfWidth);

            var figure = new PathFigure { StartPoint = to, IsClosed = true, IsFilled = true };
            figure.Segments.Add(new LineSegment(left, false));
            figure.Segments.Add(new LineSegment(right, false));
            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            geo.Freeze();

            var fill = new SolidColorBrush(ParseColor(s.ColorHex));
            fill.Freeze();
            dc.DrawGeometry(fill, null, geo);
        }

        private static void DrawStroke(DrawingContext dc, StrokeAnnotation s)
        {
            if (s.Points == null || s.Points.Count == 0) return;

            var color = ParseColor(s.ColorHex);
            // A highlighter is translucent and wide; a pen is opaque and narrow.
            var brush = new SolidColorBrush(s.Highlighter ? WithAlpha(color, 0.35) : color);
            brush.Freeze();
            var pen = new Pen(brush, s.Highlighter ? Math.Max(10, s.Thickness * 5) : s.Thickness)
            {
                StartLineCap = s.Highlighter ? PenLineCap.Flat : PenLineCap.Round,
                EndLineCap = s.Highlighter ? PenLineCap.Flat : PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();

            if (s.Points.Count == 1)
            {
                dc.DrawEllipse(brush, null, ToPoint(s.Points[0]), pen.Thickness / 2, pen.Thickness / 2);
                return;
            }

            var figure = new PathFigure { StartPoint = ToPoint(s.Points[0]), IsClosed = false, IsFilled = false };
            for (int i = 1; i < s.Points.Count; i++)
                figure.Segments.Add(new LineSegment(ToPoint(s.Points[i]), true));

            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        private static void DrawText(DrawingContext dc, TextAnnotation t)
        {
            if (string.IsNullOrEmpty(t.Text)) return;
            var ft = BuildText(t.Text, t.FontSize, ParseColor(t.ColorHex));

            // A dark plate behind the glyphs keeps light text legible over a light screenshot.
            var plate = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
            plate.Freeze();
            var pad = Math.Max(2, t.FontSize * 0.18);
            dc.DrawRoundedRectangle(plate, null,
                new Rect(t.Origin.X - pad, t.Origin.Y - pad / 2, ft.Width + pad * 2, ft.Height + pad), 3, 3);

            dc.DrawText(ft, ToPoint(t.Origin));
        }

        private static void DrawStep(DrawingContext dc, StepAnnotation s)
        {
            var fill = new SolidColorBrush(ParseColor(s.ColorHex));
            fill.Freeze();
            var ring = new Pen(Brushes.White, Math.Max(1.5, s.Radius * 0.12));
            ring.Freeze();

            var center = ToPoint(s.Center);
            dc.DrawEllipse(fill, ring, center, s.Radius, s.Radius);

            var ft = BuildText(s.Number.ToString(CultureInfo.InvariantCulture), s.Radius * 1.15, Colors.White);
            ft.SetFontWeight(FontWeights.Bold);
            dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
        }

        private static FormattedText BuildText(string text, double size, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"),
                    FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                Math.Max(6, size),
                brush,
                96);
        }

        // ----- Redaction ---------------------------------------------------------------------

        private static void DrawRedaction(DrawingContext dc, BitmapSource baseImage,
                                          RedactAnnotation r, RedactionCache cache)
        {
            var rect = ToRect(r.Start, r.End);
            if (rect.Width < 1 || rect.Height < 1) return;

            if (r.Style != RedactStyle.Solid)
            {
                var baked = cache.Get(r, baseImage);
                if (baked != null)
                {
                    dc.DrawImage(baked, rect);
                    return;
                }
            }

            // Obscuring is the whole point: for the solid style, and if an effect could not be
            // produced, paint an opaque block rather than leave the content readable.
            var solid = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x14));
            solid.Freeze();
            dc.DrawRectangle(solid, null, rect);
        }

        /// <summary>
        /// Produces the obscured bitmap for one redaction. Pixelation is a downscale followed by
        /// a nearest-neighbour upscale; blur renders the region through a WPF blur effect. Both
        /// are baked to real pixels so the result cannot differ between screen and export.
        /// </summary>
        private static BitmapSource? Bake(BitmapSource source, RedactAnnotation r)
        {
            try
            {
                if (source == null) return null;
                var rect = ToRect(r.Start, r.End);

                int x = (int)Math.Round(rect.X);
                int y = (int)Math.Round(rect.Y);
                int w = (int)Math.Round(rect.Width);
                int h = (int)Math.Round(rect.Height);

                // Clamp to the image, or CroppedBitmap throws.
                x = Math.Clamp(x, 0, Math.Max(0, source.PixelWidth - 1));
                y = Math.Clamp(y, 0, Math.Max(0, source.PixelHeight - 1));
                w = Math.Clamp(w, 1, source.PixelWidth - x);
                h = Math.Clamp(h, 1, source.PixelHeight - y);
                if (w < 1 || h < 1) return null;

                var cropped = new CroppedBitmap(source, new Int32Rect(x, y, w, h));
                cropped.Freeze();

                double strength = Math.Max(2, r.Strength);
                var visual = new DrawingVisual();

                if (r.Style == RedactStyle.Pixelate)
                {
                    int smallW = Math.Max(1, (int)(w / strength));
                    int smallH = Math.Max(1, (int)(h / strength));

                    var small = new TransformedBitmap(cropped,
                        new ScaleTransform((double)smallW / w, (double)smallH / h));
                    small.Freeze();

                    RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
                    using (var dc = visual.RenderOpen())
                        dc.DrawImage(small, new Rect(0, 0, w, h));
                }
                else
                {
                    using (var dc = visual.RenderOpen())
                        dc.DrawImage(cropped, new Rect(0, 0, w, h));
                    visual.Effect = new BlurEffect { Radius = strength, KernelType = KernelType.Gaussian };
                }

                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(visual);
                rtb.Freeze();
                return rtb;
            }
            catch (Exception ex)
            {
                Services.DiagnosticsLog.Warn("capture", "Redaction bake failed: " + ex.Message);
                return null;
            }
        }

        // ----- Helpers -----------------------------------------------------------------------

        public static Point ToPoint(ImgPoint p) => new(p.X, p.Y);

        public static Rect ToRect(ImgPoint a, ImgPoint b) =>
            new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

        public static Color ParseColor(string? hex)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hex) &&
                    ColorConverter.ConvertFromString(hex) is Color c) return c;
            }
            catch { }
            return Color.FromRgb(0xFF, 0x45, 0x3A);
        }

        private static Color WithAlpha(Color c, double alpha) =>
            Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), c.R, c.G, c.B);

        private static Pen MakePen(string hex, double thickness)
        {
            var brush = new SolidColorBrush(ParseColor(hex));
            brush.Freeze();
            var pen = new Pen(brush, Math.Max(0.5, thickness))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();
            return pen;
        }
    }
}
