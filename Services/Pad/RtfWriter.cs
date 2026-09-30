using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>A styled stretch of text for <see cref="RtfWriter"/>; runs may overlap (later ones win on color).</summary>
    public readonly record struct RtfRun(int Start, int Length, PadColor? Foreground, PadColor? Background,
                                         bool Bold, bool Italic, bool Strike, double SizeFactor);

    /// <summary>
    /// Text plus styled runs to RTF (spec 4.4): one font, a color table, bold, italic, strike,
    /// character shading and relative sizes. Every character outside ASCII is written as a
    /// unicode escape so Thai and emoji survive; line endings of any kind become paragraphs.
    /// </summary>
    public static class RtfWriter
    {
        private readonly record struct Style(PadColor Foreground, PadColor? Background, bool Bold, bool Italic, bool Strike, double Size);

        public static string Write(string text, IReadOnlyList<RtfRun> runs, string fontFamily, double fontSizePx, PadColor foreground)
        {
            var styles = new Style[text.Length];
            var plain = new Style(foreground, null, false, false, false, 1);
            for (int i = 0; i < styles.Length; i++) styles[i] = plain;
            foreach (var run in runs)
            {
                int end = Math.Min(text.Length, run.Start + run.Length);
                for (int i = Math.Max(0, run.Start); i < end; i++)
                {
                    var s = styles[i];
                    styles[i] = new Style(
                        run.Foreground ?? s.Foreground,
                        run.Background ?? s.Background,
                        s.Bold || run.Bold,
                        s.Italic || run.Italic,
                        s.Strike || run.Strike,
                        run.SizeFactor != 1 ? run.SizeFactor : s.Size);
                }
            }

            var colors = new List<PadColor> { foreground };
            int ColorIndex(PadColor c)
            {
                int i = colors.IndexOf(c);
                if (i < 0) { colors.Add(c); i = colors.Count - 1; }
                return i + 1;                                     // \colortbl starts with the "auto" entry
            }

            int baseHalfPoints = (int)Math.Round(fontSizePx * 0.75 * 2, MidpointRounding.AwayFromZero);
            var body = new StringBuilder();
            Style? current = null;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    body.Append(@"\par").Append("\r\n");
                    continue;
                }

                var style = styles[i];
                if (current != style)
                {
                    body.Append(@"\plain\f0")
                        .Append(@"\cf").Append(ColorIndex(style.Foreground).ToString(CultureInfo.InvariantCulture));
                    if (style.Bold) body.Append(@"\b");
                    if (style.Italic) body.Append(@"\i");
                    if (style.Strike) body.Append(@"\strike");
                    if (style.Background is PadColor bg) body.Append(@"\chcbpat").Append(ColorIndex(bg).ToString(CultureInfo.InvariantCulture));
                    int halfPoints = (int)Math.Round(baseHalfPoints * style.Size, MidpointRounding.AwayFromZero);
                    body.Append(@"\fs").Append(halfPoints.ToString(CultureInfo.InvariantCulture)).Append(' ');
                    current = style;
                }

                switch (c)
                {
                    case '\\': body.Append(@"\\"); break;
                    case '{': body.Append(@"\{"); break;
                    case '}': body.Append(@"\}"); break;
                    case '\t': body.Append(@"\tab "); break;
                    default:
                        if (c < 0x80) body.Append(c);
                        else body.Append(@"\u").Append(((short)c).ToString(CultureInfo.InvariantCulture)).Append('?');
                        break;
                }
            }

            var rtf = new StringBuilder();
            rtf.Append(@"{\rtf1\ansi\ansicpg1252\deff0");
            rtf.Append(@"{\fonttbl{\f0\fmodern ").Append(Escape(fontFamily)).Append(";}}");
            rtf.Append(@"{\colortbl ;");
            foreach (var c in colors)
                rtf.Append(@"\red").Append(c.R.ToString(CultureInfo.InvariantCulture))
                   .Append(@"\green").Append(c.G.ToString(CultureInfo.InvariantCulture))
                   .Append(@"\blue").Append(c.B.ToString(CultureInfo.InvariantCulture)).Append(';');
            rtf.Append('}');
            rtf.Append(@"\f0\fs").Append(baseHalfPoints.ToString(CultureInfo.InvariantCulture)).Append(' ');
            rtf.Append(body);
            rtf.Append('}');
            return rtf.ToString();
        }

        private static string Escape(string s) => s.Replace(@"\", @"\\").Replace("{", @"\{").Replace("}", @"\}");
    }
}
