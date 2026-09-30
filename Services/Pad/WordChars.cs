using System.Globalization;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// What counts as part of a word, for occurrence marking and for auto-close: letters, digits,
    /// underscores, and combining marks. Thai vowels and tone marks (่ ้ ิ ี ุ ู ั ็ ์ …) are combining
    /// marks, so they belong to the word they sit on.
    /// </summary>
    public static class WordChars
    {
        public static bool IsWordChar(char c)
        {
            if (char.IsLetterOrDigit(c) || c == '_') return true;
            var category = char.GetUnicodeCategory(c);
            return category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.SpacingCombiningMark;
        }
    }
}
