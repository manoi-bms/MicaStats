using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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
            Assert.Same(DiagramPicture.BitmapOf(result), DiagramPicture.BitmapOf(result));   // decoded once
            Assert.Equal(1, DiagramPicture.BitmapOf(result).PixelWidth);
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
