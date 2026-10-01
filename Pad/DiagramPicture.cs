using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kil0bitSystemMonitor.Services.Pad;

// UseWindowsForms puts System.Windows.Forms and System.Drawing in scope; these names exist in both.
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using TextBox = System.Windows.Controls.TextBox;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>What one picture under a fence shows and does; the board builds one for each drawing of its line.</summary>
    internal sealed class DiagramView
    {
        /// <summary>The picture or the error; null while the first drawing runs.</summary>
        public DiagramResult? Result { get; init; }

        public required PadPalette Palette { get; init; }

        /// <summary>The text area's width: a picture is scaled down to it, never up.</summary>
        public double MaxWidth { get; init; } = 600;

        /// <summary>The screen's pixels per device-independent pixel where the picture is shown: it is decoded with as many pixels as it is drawn with.</summary>
        public double PixelsPerDip { get; init; } = 1;

        /// <summary>True when the block has lines Hide code can fold.</summary>
        public bool CanHideCode { get; init; }

        /// <summary>True while the block's code is folded away.</summary>
        public bool CodeHidden { get; init; }

        public Action? ToggleCode { get; init; }

        public Action? CopyPicture { get; init; }

        public Action? SavePng { get; init; }

        public Action? SaveSvg { get; init; }

        /// <summary>Opens the error's help link (through the window's safe-link path).</summary>
        public Action<Uri>? OpenLink { get; init; }
    }

    /// <summary>
    /// One block's picture, a small "Drawing..." line while its first drawing runs, or its error in
    /// a box of the palette's alert color with selectable text (spec section 4). A picture has a
    /// Hide code / Show code button on hover and the right-click menu Copy picture, Save as PNG...,
    /// Save as SVG.... A Kroki picture sits on a white card in both themes.
    /// </summary>
    internal sealed class DiagramPicture : Border
    {
        internal const double PaperPadding = 8;

        /// <summary>True when <paramref name="element"/> is a picture or sits inside one (a right-click there must leave the caret and selection alone).</summary>
        internal static bool IsInside(DependencyObject? element)
        {
            while (element != null)
            {
                if (element is DiagramPicture) return true;
                element = (element is Visual || element is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : null)
                    ?? LogicalTreeHelper.GetParent(element);
            }
            return false;
        }

        /// <summary>How many decoded pictures stay in memory, the least recently used dropped first.</summary>
        internal const int DecodedCapacity = 24;

        /// <summary>
        /// Decoded pictures by result and width, so a line drawn again does not decode again. Small
        /// and bounded: a picture is decoded at the size it is shown, and the renderer's cache keeps
        /// the PNG to decode it again.
        /// </summary>
        private static readonly Dictionary<(DiagramResult Result, int Width), LinkedListNode<((DiagramResult Result, int Width) Key, BitmapSource Bitmap)>> s_decoded = new();
        private static readonly LinkedList<((DiagramResult Result, int Width) Key, BitmapSource Bitmap)> s_decodedOrder = new();

        public DiagramPicture(DiagramView view)
        {
            View = view;
            Margin = new Thickness(0, 4, 0, 8);
            HorizontalAlignment = HorizontalAlignment.Left;
            Cursor = Cursors.Arrow;
            if (view.Result == null) Child = DrawingText(view.Palette);
            else if (view.Result.IsPicture) Child = PictureOf(view, view.Result);
            else Child = ErrorOf(view, view.Result);
        }

        internal DiagramView View { get; }

        internal bool IsDrawing => View.Result == null;

        internal Image? Image { get; private set; }

        internal Button? CodeButton { get; private set; }

        internal TextBox? ErrorText { get; private set; }

        internal Hyperlink? HelpLink { get; private set; }

        /// <summary>How many decoded pictures are kept; for tests.</summary>
        internal static int DecodedCount
        {
            get { lock (s_decoded) return s_decoded.Count; }
        }

        /// <summary>
        /// The result's PNG as a frozen bitmap <paramref name="decodeWidth"/> pixels wide (its
        /// height follows; 0 decodes it at its own size), decoded once while it stays among the
        /// <see cref="DecodedCapacity"/> most recently used.
        /// </summary>
        internal static BitmapSource BitmapOf(DiagramResult result, int decodeWidth)
        {
            var key = (result, decodeWidth);
            lock (s_decoded)
            {
                if (s_decoded.TryGetValue(key, out var node))
                {
                    s_decodedOrder.Remove(node);
                    s_decodedOrder.AddFirst(node);
                    return node.Value.Bitmap;
                }
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
            bitmap.StreamSource = new MemoryStream(result.Png!);
            bitmap.EndInit();
            bitmap.Freeze();

            lock (s_decoded)
            {
                if (s_decoded.TryGetValue(key, out var raced)) return raced.Value.Bitmap;
                s_decoded[key] = s_decodedOrder.AddFirst((key, bitmap));
                while (s_decoded.Count > DecodedCapacity)
                {
                    var oldest = s_decodedOrder.Last!;
                    s_decodedOrder.RemoveLast();
                    s_decoded.Remove(oldest.Value.Key);
                }
            }
            return bitmap;
        }

        /// <summary>
        /// How many pixels wide to decode a picture shown <paramref name="shownWidth"/> wide: as many
        /// as the screen draws it with, never more than the PNG has; 0 (its own size) when the PNG's
        /// header cannot be read.
        /// </summary>
        private static int DecodeWidthOf(byte[] png, double shownWidth, double pixelsPerDip)
        {
            int pngWidth = PngWidth(png);
            if (pngWidth == 0) return 0;
            double wanted = Math.Ceiling(shownWidth * (pixelsPerDip > 0 ? pixelsPerDip : 1));
            return (int)Math.Clamp(wanted, 1, pngWidth);
        }

        /// <summary>A PNG's width in pixels from its header (bytes 16-19, big-endian), or 0 when it is not a PNG.</summary>
        private static int PngWidth(byte[] png)
        {
            if (png.Length < 24 || png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G') return 0;
            int width = png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19];
            return width > 0 ? width : 0;
        }

        private static UIElement DrawingText(PadPalette palette) => new TextBlock
        {
            Text = DiagramText.Drawing,
            FontSize = 12,
            FontStyle = FontStyles.Italic,
            Foreground = PadThemeApplier.ToBrush(palette.Muted),
        };

        private UIElement PictureOf(DiagramView view, DiagramResult result)
        {
            double room = view.MaxWidth - (result.Paper ? 2 * PaperPadding : 0);
            double width = Math.Max(1, Math.Min(result.Width, room));
            Image = new Image
            {
                Source = BitmapOf(result, DecodeWidthOf(result.Png!, width, view.PixelsPerDip)),
                Width = width,
                Height = width * result.Height / result.Width,
                Stretch = Stretch.Uniform,
            };
            RenderOptions.SetBitmapScalingMode(Image, BitmapScalingMode.HighQuality);

            UIElement content = Image;
            if (result.Paper)
            {
                content = new Border
                {
                    Background = Brushes.White,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(PaperPadding),
                    Child = Image,
                };
            }

            var grid = new Grid { Background = Brushes.Transparent };
            grid.Children.Add(content);
            if (view.CanHideCode && view.ToggleCode is { } toggle)
            {
                var button = new Button
                {
                    Content = view.CodeHidden ? "Show code" : "Hide code",
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(4),
                    Padding = new Thickness(8, 2, 8, 2),
                    FontSize = 12,
                    Visibility = Visibility.Hidden,
                };
                button.Click += (s, e) => toggle();
                grid.Children.Add(button);
                grid.MouseEnter += (s, e) => button.Visibility = Visibility.Visible;
                grid.MouseLeave += (s, e) => button.Visibility = Visibility.Hidden;
                CodeButton = button;
            }

            var menu = new ContextMenu();
            EditorMenus.Style(menu, view.Palette);
            ModernWpf.ThemeManager.SetRequestedTheme(menu, view.Palette.IsDark ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);
            menu.Items.Add(EditorMenus.Item("Copy picture", null, () => view.CopyPicture?.Invoke(), icon: "\uE8C8"));
            menu.Items.Add(EditorMenus.Item("Save as PNG\u2026", null, () => view.SavePng?.Invoke(), icon: "\uE74E"));
            menu.Items.Add(EditorMenus.Item("Save as SVG\u2026", null, () => view.SaveSvg?.Invoke(), icon: "\uE74E"));
            ContextMenu = menu;
            return grid;
        }

        private UIElement ErrorOf(DiagramView view, DiagramResult result)
        {
            var palette = view.Palette;
            ErrorText = new TextBox
            {
                // An explicit style: ModernWpf's would add its own border and focus look.
                Style = new Style(typeof(TextBox)),
                Text = result.Error ?? DiagramText.Failed,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = PadThemeApplier.ToBrush(palette.Text),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(0),
            };

            var panel = new StackPanel();
            panel.Children.Add(ErrorText);
            if (result.HelpLink is { } link)
            {
                HelpLink = new Hyperlink(new Run("Get the WebView2 Runtime")) { Foreground = PadThemeApplier.ToBrush(palette.MdLink) };
                HelpLink.Click += (s, e) => view.OpenLink?.Invoke(link);
                panel.Children.Add(new TextBlock(HelpLink) { Margin = new Thickness(0, 4, 0, 0), FontSize = 12 });
            }

            return new Border
            {
                Child = panel,
                MaxWidth = view.MaxWidth,
                BorderBrush = PadThemeApplier.ToBrush(palette.AlertRed),
                BorderThickness = new Thickness(1),
                Background = PadThemeApplier.ToBrush(palette.AlertRed with { A = 0x1F }),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
            };
        }
    }
}
