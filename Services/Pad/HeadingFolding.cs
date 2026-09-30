using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Markdown folds: a heading folds from the end of its line to the last non-blank line before
    /// the next heading of the same or higher level; a fenced block folds from the end of its
    /// opening line to the end of its closing line. Headings inside fences are not headings.
    /// </summary>
    public static class HeadingFolding
    {
        private static readonly Regex HeadingRx = new(@"^ {0,3}(#{1,6})(?:[ \t]|$)", RegexOptions.CultureInvariant);

        public static IReadOnlyList<FoldRange> Compute(string text)
        {
            var lines = Lines(text);
            var texts = new string[lines.Count];
            for (int k = 0; k < lines.Count; k++) texts[k] = text.Substring(lines[k].Start, lines[k].End - lines[k].Start);
            var fences = FenceTracker.Classify(texts);
            var folds = new List<FoldRange>();

            for (int i = 0; i < lines.Count; i++)
            {
                int level = fences[i] == MdFence.None ? LevelOf(texts[i]) : 0;
                if (level == 0) continue;
                int last = lines.Count - 1;
                for (int j = i + 1; j < lines.Count; j++)
                {
                    int other = fences[j] == MdFence.None ? LevelOf(texts[j]) : 0;
                    if (other > 0 && other <= level) { last = j - 1; break; }
                }
                while (last > i && string.IsNullOrWhiteSpace(texts[last])) last--;
                if (last > i) folds.Add(new FoldRange(lines[i].End, lines[last].End));
            }

            for (int i = 0; i < lines.Count; i++)
            {
                if (fences[i] != MdFence.Delimiter) continue;
                int close = i + 1;
                while (close < lines.Count && fences[close] != MdFence.Delimiter) close++;
                int last = close < lines.Count ? close : lines.Count - 1;
                if (last > i) folds.Add(new FoldRange(lines[i].End, lines[last].End));
                i = close;
            }

            folds.Sort((a, b) => a.Start.CompareTo(b.Start));
            return folds;
        }

        private static int LevelOf(string line)
        {
            var m = HeadingRx.Match(line);
            return m.Success ? m.Groups[1].Length : 0;
        }

        /// <summary>Each line's start and end (before its line break).</summary>
        private static List<(int Start, int End)> Lines(string text)
        {
            var lines = new List<(int, int)>();
            int start = 0;
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c != '\r' && c != '\n') { i++; continue; }
                lines.Add((start, i));
                i += c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                start = i;
            }
            lines.Add((start, text.Length));
            return lines;
        }
    }
}
