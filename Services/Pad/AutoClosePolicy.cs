namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>What typing a character does with auto-close on.</summary>
    public enum AutoCloseAction
    {
        /// <summary>Type it normally.</summary>
        Insert,
        /// <summary>Type it and its closer, caret between.</summary>
        Pair,
        /// <summary>The same closer is already next: move over it instead of typing a second.</summary>
        SkipOver,
        /// <summary>Put it before the selection and its closer after.</summary>
        Wrap,
    }

    /// <summary>
    /// The auto-close rules (spec 3.1). Pure: the handler passes the characters around the caret.
    /// Brackets and quotes pair only in front of whitespace, the end of the line or a closing
    /// character, never in front of a word; quotes also never right after a letter or digit, so
    /// "don't" types normally. <c>*</c> and <c>_</c> are never paired (Markdown).
    /// </summary>
    public static class AutoClosePolicy
    {
        /// <summary>The closing character of an opener, or null.</summary>
        public static char? CloserOf(char opener) => opener switch
        {
            '(' => ')',
            '[' => ']',
            '{' => '}',
            '"' => '"',
            '\'' => '\'',
            '`' => '`',
            _ => null,
        };

        public static AutoCloseAction OnType(char typed, char? before, char? after, bool hasSelection)
        {
            bool quote = typed is '"' or '\'' or '`';
            bool openBracket = typed is '(' or '[' or '{';
            bool closeBracket = typed is ')' or ']' or '}';

            if (hasSelection) return openBracket || quote ? AutoCloseAction.Wrap : AutoCloseAction.Insert;
            if ((closeBracket || quote) && after == typed) return AutoCloseAction.SkipOver;
            if (!openBracket && !quote) return AutoCloseAction.Insert;
            if (after is char a && !char.IsWhiteSpace(a) && a is not (')' or ']' or '}' or ',' or ';' or ':' or '.'))
                return AutoCloseAction.Insert;
            if (quote && before is char b && char.IsLetterOrDigit(b)) return AutoCloseAction.Insert;
            return AutoCloseAction.Pair;
        }

        /// <summary>True when Backspace sits between an opener and its closer: both go.</summary>
        public static bool DeletesPair(char? before, char? after) =>
            before is char b && CloserOf(b) is char closer && after == closer;
    }
}
