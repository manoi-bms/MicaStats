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
    /// unicode escape so Thai and emoji survive; line endings of any kind become paragraphs. Other
    /// control characters become spaces, and what is not a character (U+FFFE, U+FFFF, a lone
    /// surrogate) a question mark, so the RTF is plain ASCII that every reader accepts.
    /// </summary>
    public static class RtfWriter
    {
        private readonly record struct Style(PadColor Foreground, PadColor? Background, bool Bold, bool Italic, bool Strike, double Size);

        public static string Write(string text, IReadOnlyList<RtfRun> runs, string fontFamily, double fontSizePx, PadColor foreground)
        {
            // Style ids per character (4 bytes each) into a small deduplicated table; with no runs
            // there is one style and no per-character array at all.
            var plain = new Style(foreground, null, false, false, false, 1);
            var table = new List<Style> { plain };
            int[]? ids = null;
            if (runs.Count > 0)
            {
                var known = new Dictionary<Style, int> { [plain] = 0 };
                ids = new int[text.Length];
                foreach (var run in runs)
                {
                    int end = Math.Min(text.Length, run.Start + run.Length);
                    for (int i = Math.Max(0, run.Start); i < end; i++)
                    {
                        var s = table[ids[i]];
                        var merged = new Style(
                            run.Foreground ?? s.Foreground,
                            run.Background ?? s.Background,
                            s.Bold || run.Bold,
                            s.Italic || run.Italic,
                            s.Strike || run.Strike,
                            run.SizeFactor != 1 ? run.SizeFactor : s.Size);
                        if (!known.TryGetValue(merged, out int id))
                        {
                            id = table.Count;
                            table.Add(merged);
                            known[merged] = id;
                        }
                        ids[i] = id;
                    }
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
            int current = -1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    body.Append(@"\par").Append("\r\n");
                    continue;
                }

                int styleId = ids == null ? 0 : ids[i];
                if (current != styleId)
                {
                    var style = table[styleId];
                    // Word formats Thai with the associated (complex-script) forms, \ab \ai \afs, and
                    // \plain resets them, so each property is written in both forms.
                    body.Append(@"\plain\f0")
                        .Append(@"\cf").Append(ColorIndex(style.Foreground).ToString(CultureInfo.InvariantCulture));
                    if (style.Bold) body.Append(@"\b\ab");
                    if (style.Italic) body.Append(@"\i\ai");
                    if (style.Strike) body.Append(@"\strike");
                    if (style.Background is PadColor bg) body.Append(@"\chcbpat").Append(ColorIndex(bg).ToString(CultureInfo.InvariantCulture));
                    int halfPoints = (int)Math.Round(baseHalfPoints * style.Size, MidpointRounding.AwayFromZero);
                    AppendSize(body, halfPoints).Append(' ');
                    current = styleId;
                }

                switch (c)
                {
                    case '\t': body.Append(@"\tab "); break;
                    case < ' ': body.Append(' '); break;          // a raw control character breaks readers (NUL ends the clipboard text)
                    default:
                        if (char.IsSurrogate(c))
                        {
                            // A pair is written as its two halves; a lone half is not a character.
                            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                            {
                                AppendChar(body, c);
                                AppendChar(body, text[++i]);
                            }
                            else body.Append('?');
                        }
                        else AppendChar(body, c);
                        break;
                }
            }

            var rtf = new StringBuilder();
            rtf.Append(@"{\rtf1\ansi\ansicpg1252\deff0");
            rtf.Append(@"{\fonttbl{\f0\fmodern ");
            foreach (char c in fontFamily) AppendChar(rtf, c < ' ' ? ' ' : c);
            rtf.Append(";}}");
            rtf.Append(@"{\colortbl ;");
            foreach (var c in colors)
                rtf.Append(@"\red").Append(c.R.ToString(CultureInfo.InvariantCulture))
                   .Append(@"\green").Append(c.G.ToString(CultureInfo.InvariantCulture))
                   .Append(@"\blue").Append(c.B.ToString(CultureInfo.InvariantCulture)).Append(';');
            rtf.Append('}');
            rtf.Append(@"\f0");
            AppendSize(rtf, baseHalfPoints).Append(' ');
            rtf.Append(body);
            rtf.Append('}');
            return rtf.ToString();
        }

        /// <summary>
        /// About how long the RTF of <paramref name="text"/> will be, for a size cap before building
        /// it: an ASCII character counts one, any other eight (its unicode escape).
        /// </summary>
        public static long EstimatedLength(string text)
        {
            long length = 0;
            foreach (char c in text) length += c < 0x80 ? 1 : 8;
            return length;
        }

        private static StringBuilder AppendSize(StringBuilder sb, int halfPoints)
        {
            string n = halfPoints.ToString(CultureInfo.InvariantCulture);
            return sb.Append(@"\fs").Append(n).Append(@"\afs").Append(n);
        }

        /// <summary>
        /// One character of text or of the font name: RTF's own \ { } escaped, anything outside
        /// ASCII as a signed unicode escape with a ? fallback, and the noncharacters U+FFFE and
        /// U+FFFF (which readers reject) as a plain ?.
        /// </summary>
        private static void AppendChar(StringBuilder sb, char c)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '{': sb.Append(@"\{"); break;
                case '}': sb.Append(@"\}"); break;
                case (char)0xFFFE or (char)0xFFFF: sb.Append('?'); break;
                default:
                    if (c < 0x80) sb.Append(c);
                    else sb.Append(@"\u").Append(((short)c).ToString(CultureInfo.InvariantCulture)).Append('?');
                    break;
            }
        }
    }
}
