using System;
using System.IO;
using System.Runtime.CompilerServices;
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

        /// <summary>Each result's bitmap, decoded once however often its line is drawn.</summary>
        private static readonly ConditionalWeakTable<DiagramResult, BitmapSource> s_bitmaps = new();

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

        /// <summary>The result's PNG as a frozen bitmap.</summary>
        internal static BitmapSource BitmapOf(DiagramResult result) => s_bitmaps.GetValue(result, r =>
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(r.Png!);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        });

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
                Source = BitmapOf(result),
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
