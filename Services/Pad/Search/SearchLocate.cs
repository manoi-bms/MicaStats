using System;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>
    /// Finds a result's passage in the note as it is now (spec 1): the recorded line when its text
    /// still starts there, else the nearest line with that text, else the recorded line kept inside
    /// the note.
    /// </summary>
    public static class SearchLocate
    {
        /// <param name="lineText">The text of a 1-based line.</param>
        public static int FindLine(int lineCount, Func<int, string> lineText, int recordedLine, string firstLineText)
        {
            if (lineCount <= 0) return 1;
            int clamped = Math.Clamp(recordedLine, 1, lineCount);
            if (firstLineText.Length == 0) return clamped;
            if (Matches(lineText(clamped), firstLineText)) return clamped;

            for (int distance = 1; distance < lineCount; distance++)
            {
                int up = clamped - distance, down = clamped + distance;
                if (up >= 1 && Matches(lineText(up), firstLineText)) return up;
                if (down <= lineCount && Matches(lineText(down), firstLineText)) return down;
                if (up < 1 && down > lineCount) break;
            }
            return clamped;
        }

        private static bool Matches(string line, string firstLineText) =>
            line.Trim().StartsWith(firstLineText, StringComparison.Ordinal);
    }
}
