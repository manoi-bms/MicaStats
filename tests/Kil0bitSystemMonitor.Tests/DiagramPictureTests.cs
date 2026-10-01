using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

// UseWindowsForms puts System.Windows.Forms and System.Drawing in scope; these names exist in both.
using Brush = System.Windows.Media.Brush;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The picture under a closing fence (spec section 4): where it sits, and what it shows and offers.</summary>
    public class DiagramPictureTests
    {
        /// <summary>Puts one diagram element at the end of a line, as the diagram generator does.</summary>
        private sealed class AtLineEnd : VisualLineElementGenerator
        {
            public int Line { get; init; }
            public UIElement Picture { get; init; } = null!;

            public override int GetFirstInterestedOffset(int startOffset)
            {
                var line = CurrentContext.Document.GetLineByNumber(Line);
                return line.EndOffset >= startOffset && line.EndOffset <= CurrentContext.VisualLine.LastDocumentLine.EndOffset ? line.EndOffset : -1;
            }

            public override VisualLineElement ConstructElement(int offset) => new DiagramElement(Picture);
        }

        private static DiagramView View(DiagramResult? result, double maxWidth = 600, PadPalette? palette = null) =>
            new() { Result = result, Palette = palette ?? PadPalette.Dark, MaxWidth = maxWidth };

        private static bool Same(PadColor expected, Brush brush) =>
            brush is SolidColorBrush solid && solid.Color == Color.FromArgb(expected.A, expected.R, expected.G, expected.B);

        [Fact]
        public void The_picture_gets_its_own_row_under_the_fence_and_the_caret_stays_before_it() => UiThread.Run(() =>
        {
            var view = new TextView { Document = new TextDocument("```mermaid\nA-->B\n```\nafter") };
            var picture = new Border { Width = 300, Height = 120 };
            view.ElementGenerators.Add(new AtLineEnd { Line = 3, Picture = picture });
            view.Measure(new Size(600, 400));
            view.Arrange(new Rect(0, 0, 600, 400));
            view.UpdateLayout();
            view.EnsureVisualLines();

            var line = view.GetVisualLine(3)!;
            Assert.Equal(2, line.TextLines.Count);
            var top = picture.TranslatePoint(new Point(0, 0), view);
            Assert.Equal(line.VisualTop + line.TextLines[0].Height, top.Y, 1);
            Assert.Equal(0, top.X, 1);
            Assert.True(line.Height >= 120);

            var element = line.Elements.OfType<DiagramElement>().Single();
            int end = line.GetVisualColumn(line.LastDocumentLine.EndOffset - line.FirstDocumentLine.Offset);
            Assert.Equal(element.VisualColumn, end);
            Assert.Equal(-1, element.GetNextCaretPosition(element.VisualColumn, LogicalDirection.Forward, CaretPositioningMode.Normal));
            Assert.Equal(element.VisualColumn, element.GetNextCaretPosition(element.VisualColumn + 2, LogicalDirection.Backward, CaretPositioningMode.Normal));
            Assert.Equal(0, element.DocumentLength);
        });

        [Fact]
        public void While_drawing_it_says_so() => UiThread.Run(() =>
        {
            var picture = new DiagramPicture(View(null));

            Assert.True(picture.IsDrawing);
            Assert.Equal("Drawing\u2026", Assert.IsType<TextBlock>(picture.Child).Text);
            Assert.Null(picture.ContextMenu);
            Assert.Null(picture.CodeButton);
        });

        [Fact]
        public void A_picture_fits_the_width_and_is_never_enlarged() => UiThread.Run(() =>
        {
            var wide = new DiagramPicture(View(DiagramFakes.Picture(1000, 500), maxWidth: 400));
            var small = new DiagramPicture(View(DiagramFakes.Picture(100, 50), maxWidth: 400));

            Assert.Equal(400, wide.Image!.Width);
            Assert.Equal(200, wide.Image.Height);
            Assert.Equal(100, small.Image!.Width);
            Assert.Equal(50, small.Image.Height);
            Assert.False(wide.IsDrawing);
            Assert.NotNull(wide.ContextMenu);

            var result = DiagramFakes.Picture();
            Assert.Same(DiagramPicture.BitmapOf(result, 1), DiagramPicture.BitmapOf(result, 1));   // decoded once
            Assert.Equal(1, DiagramPicture.BitmapOf(result, 1).PixelWidth);
        });

        /// <summary>A transparent PNG of <paramref name="width"/> x <paramref name="height"/> pixels.</summary>
        private static byte[] PngOf(int width, int height)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null)));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        [Fact]
        public void A_picture_is_decoded_at_the_size_it_is_drawn_and_never_larger_than_its_png() => UiThread.Run(() =>
        {
            // A 100 x 50 picture comes as a 200 x 100 PNG (2x).
            var result = DiagramResult.Picture(PngOf(200, 100), DiagramFakes.Svg, 100, 50, paper: false);

            // Shown 60 wide on a 150% screen: 90 pixels are enough.
            var narrow = new DiagramPicture(new DiagramView { Result = result, Palette = PadPalette.Dark, MaxWidth = 60, PixelsPerDip = 1.5 });
            var decoded = Assert.IsAssignableFrom<BitmapSource>(narrow.Image!.Source);
            Assert.Equal(60, narrow.Image.Width);
            Assert.Equal(90, decoded.PixelWidth);
            Assert.Equal(45, decoded.PixelHeight);

            // Shown 100 wide on a 250% screen would want 250: the PNG has 200.
            var full = new DiagramPicture(new DiagramView { Result = result, Palette = PadPalette.Dark, MaxWidth = 600, PixelsPerDip = 2.5 });
            Assert.Equal(200, ((BitmapSource)full.Image!.Source).PixelWidth);
        });

        [Fact]
        public void A_picture_is_decoded_once_per_width_and_at_most_24_stay_decoded() => UiThread.Run(() =>
        {
            var result = DiagramResult.Picture(PngOf(200, 100), DiagramFakes.Svg, 100, 50, paper: false);
            var at90 = DiagramPicture.BitmapOf(result, 90);
            Assert.Same(at90, DiagramPicture.BitmapOf(result, 90));
            Assert.Equal(90, at90.PixelWidth);
            Assert.NotSame(at90, DiagramPicture.BitmapOf(result, 120));
            Assert.Equal(120, DiagramPicture.BitmapOf(result, 120).PixelWidth);

            var first = DiagramFakes.Picture();
            var oldest = DiagramPicture.BitmapOf(first, 1);
            var last = first;
            var newest = oldest;
            for (int i = 0; i < 30; i++)
            {
                last = DiagramFakes.Picture();
                newest = DiagramPicture.BitmapOf(last, 1);
                Assert.True(DiagramPicture.DecodedCount <= 24);
            }

            Assert.Equal(24, DiagramPicture.DecodedCount);
            Assert.Same(newest, DiagramPicture.BitmapOf(last, 1));       // the most recent stays
            Assert.NotSame(oldest, DiagramPicture.BitmapOf(first, 1));   // the oldest was dropped and is decoded again
            Assert.Equal(24, DiagramPicture.DecodedCount);
        });

        [Fact]
        public void A_kroki_picture_sits_on_a_white_card_in_the_dark_theme() => UiThread.Run(() =>
        {
            var picture = new DiagramPicture(View(DiagramFakes.Picture(100, 50, paper: true), palette: PadPalette.Dark));

            var card = Assert.IsType<Border>(picture.Image!.Parent);
            Assert.Equal(Colors.White, ((SolidColorBrush)card.Background).Color);
            Assert.Equal(new Thickness(DiagramPicture.PaperPadding), card.Padding);
        });

        [Fact]
        public void An_error_shows_its_message_selectable_in_the_alert_color() => UiThread.Run(() =>
        {
            var picture = new DiagramPicture(View(DiagramResult.Failure("Parse error on line 3:\n---^", lasting: true)));

            Assert.Equal("Parse error on line 3:\n---^", picture.ErrorText!.Text);
            Assert.True(picture.ErrorText.IsReadOnly);
            var box = Assert.IsType<Border>(picture.Child);
            Assert.True(Same(PadPalette.Dark.AlertRed, box.BorderBrush));
            Assert.Null(picture.ContextMenu);
            Assert.Null(picture.HelpLink);
            Assert.Null(picture.Image);
        });

        [Fact]
        public void The_missing_runtime_error_links_to_the_download() => UiThread.Run(() =>
        {
            Uri? opened = null;
            var picture = new DiagramPicture(new DiagramView
            {
                Result = DiagramResult.Failure(DiagramText.RuntimeMissing, lasting: false, DiagramText.RuntimeDownload),
                Palette = PadPalette.Light,
                OpenLink = uri => opened = uri,
            });

            picture.HelpLink!.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));

            Assert.Equal(DiagramText.RuntimeDownload, opened);
            Assert.Equal(DiagramText.RuntimeMissing, picture.ErrorText!.Text);
        });

        [Fact]
        public void Hide_code_shows_on_hover_and_says_what_it_will_do() => UiThread.Run(() =>
        {
            int toggled = 0;
            var picture = new DiagramPicture(new DiagramView
            {
                Result = DiagramFakes.Picture(),
                Palette = PadPalette.Dark,
                CanHideCode = true,
                ToggleCode = () => toggled++,
            });

            Assert.Equal("Hide code", picture.CodeButton!.Content);
            Assert.Equal(Visibility.Hidden, picture.CodeButton.Visibility);
            var hover = (UIElement)picture.Child;
            hover.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            Assert.Equal(Visibility.Visible, picture.CodeButton.Visibility);
            hover.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            Assert.Equal(Visibility.Hidden, picture.CodeButton.Visibility);

            picture.CodeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(1, toggled);

            var hidden = new DiagramPicture(new DiagramView { Result = DiagramFakes.Picture(), Palette = PadPalette.Dark, CanHideCode = true, CodeHidden = true, ToggleCode = () => { } });
            Assert.Equal("Show code", hidden.CodeButton!.Content);
            Assert.Null(new DiagramPicture(new DiagramView { Result = DiagramFakes.Picture(), Palette = PadPalette.Dark, ToggleCode = () => { } }).CodeButton);
        });

        [Fact]
        public void The_picture_menu_copies_and_saves() => UiThread.Run(() =>
        {
            var done = new List<string>();
            var picture = new DiagramPicture(new DiagramView
            {
                Result = DiagramFakes.Picture(),
                Palette = PadPalette.Light,
                CopyPicture = () => done.Add("copy"),
                SavePng = () => done.Add("png"),
                SaveSvg = () => done.Add("svg"),
            });

            var items = picture.ContextMenu!.Items.OfType<MenuItem>().ToList();
            Assert.Equal(new[] { "Copy picture", "Save as PNG\u2026", "Save as SVG\u2026" }, items.Select(i => (string)i.Header));
            foreach (var item in items) item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Equal(new[] { "copy", "png", "svg" }, done);
        });
    }
}
