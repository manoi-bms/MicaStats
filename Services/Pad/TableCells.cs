using System.Collections.Generic;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>The cells and separating pipes of a Markdown table line (spec 2, 3).</summary>
    public static class TableCells
    {
        /// <summary>
        /// The cells of a table line: the outer pipes are dropped, the rest is split on pipes not
        /// escaped by a backslash, and each cell is trimmed. An escaped pipe stays in its cell as <c>\|</c>.
        /// </summary>
        public static List<string> Split(string line)
        {
            string s = (line ?? "").Trim();
            if (s.StartsWith('|')) s = s.Substring(1);
            if (s.EndsWith('|') && !s.EndsWith("\\|")) s = s.Substring(0, s.Length - 1);

            var cells = new List<string>();
            var cell = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '|')
                {
                    cell.Append("\\|");
                    i++;
                    continue;
                }
                if (s[i] == '|')
                {
                    cells.Add(cell.ToString().Trim());
                    cell.Clear();
                    continue;
                }
                cell.Append(s[i]);
            }
            cells.Add(cell.ToString().Trim());
            return cells;
        }

        /// <summary>The offsets of the pipes that separate cells (not escaped by a backslash).</summary>
        public static List<int> Pipes(string line)
        {
            var pipes = new List<int>();
            string s = line ?? "";
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '|')
                {
                    i++;
                    continue;
                }
                if (s[i] == '|') pipes.Add(i);
            }
            return pipes;
        }
    }
}
