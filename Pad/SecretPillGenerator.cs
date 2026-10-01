using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Draws each <c>{{secret:ID}}</c> reference as one pill: a lock and the credential's label, or
    /// "Missing credential" when the vault has no such id. The pill stands for the whole reference,
    /// so the caret steps over it and Backspace or Delete next to it removes it whole; copying still
    /// copies the reference text. Lines longer than 4,000 characters are not scanned (as links).
    /// </summary>
    internal sealed class SecretPillGenerator : VisualLineElementGenerator
    {
        private const int MaxLineLength = 4000;
        private readonly Func<string, CredentialInfo?> _find;
        private readonly Func<PadPalette> _palette;
        private readonly Func<double> _fontSize;

        public SecretPillGenerator(Func<string, CredentialInfo?> find, Func<PadPalette> palette, Func<double> fontSize)
        {
            _find = find;
            _palette = palette;
            _fontSize = fontSize;
        }

        /// <summary>What a pill says.</summary>
        internal static string LabelOf(CredentialInfo? info) =>
            info == null ? "Missing credential" : info.Label.Length == 0 ? "Credential" : info.Label;

        /// <summary>A pill's tooltip.</summary>
        internal static string ToolTipOf(string id, CredentialInfo? info) =>
            info == null ? "No stored credential " + id : "Stored credential " + id + " \u2014 right-click for options";

        public override int GetFirstInterestedOffset(int startOffset)
        {
            var document = CurrentContext.Document;
            var line = document.GetLineByOffset(startOffset);
            if (line.Length > MaxLineLength) return -1;

            string text = document.GetText(line.Offset, line.Length);
            foreach (var reference in SecretTokens.Find(text))
            {
                int offset = line.Offset + reference.Offset;
                if (offset >= startOffset) return offset;
            }
            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var document = CurrentContext.Document;
            var line = document.GetLineByOffset(offset);
            string text = document.GetText(line.Offset, line.Length);
            foreach (var reference in SecretTokens.Find(text))
            {
                if (line.Offset + reference.Offset != offset) continue;
                return new InlineObjectElement(reference.Length, Pill(reference.Id));
            }
            return null;
        }

        private UIElement Pill(string id)
        {
            var palette = _palette();
            var info = _find(id);
            double size = Math.Max(8, _fontSize() * 0.9);
            var foreground = PadThemeApplier.ToBrush(info == null ? palette.PillMissingText : palette.PillText);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = "\uE72E",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = size * 0.85,
                Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            });
            row.Children.Add(new TextBlock { Text = LabelOf(info), FontSize = size, Foreground = foreground, VerticalAlignment = VerticalAlignment.Center });

            return new Border
            {
                Child = row,
                Background = PadThemeApplier.ToBrush(palette.PillBack),
                BorderBrush = PadThemeApplier.ToBrush(palette.PillBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 0, 6, 0),
                Margin = new Thickness(1, 0, 1, 0),
                ToolTip = ToolTipOf(id, info),
                Cursor = System.Windows.Input.Cursors.Arrow,
            };
        }
    }
}
