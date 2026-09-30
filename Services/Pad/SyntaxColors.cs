using System;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>What a highlighting color is, whatever the definition calls it (spec 2.2).</summary>
    public enum SyntaxCategory
    {
        Text,
        Comment,
        String,
        Keyword,
        Number,
        Type,
        Preprocessor,
        Tag,
        Attribute,
        Operator,
        Error,
        Warning,
        Info,
        Debug,
        Added,
        Removed,
    }

    /// <summary>
    /// Maps the color names in AvalonEdit's definitions (and MicaPad's own) onto the palette.
    /// AvalonEdit's colors were chosen for white pages; MicaPad paints each named color with its
    /// category's palette color instead, so every language reads in both themes.
    /// </summary>
    public static class SyntaxColors
    {
        /// <summary>
        /// Tried top to bottom; the first row with a name contained in the color name (ignoring case)
        /// wins, so "KeywordX" is a keyword before "Key" could make it an attribute, and
        /// "AttributeValue" a string before "Value" could make it a keyword.
        /// </summary>
        private static readonly (SyntaxCategory Category, string[] Names)[] Rows =
        {
            (SyntaxCategory.Comment, new[] { "Comment" }),
            (SyntaxCategory.String, new[] { "String", "Char", "Verbatim", "Regex", "AttributeValue" }),
            (SyntaxCategory.Keyword, new[] { "Keyword", "Modifier", "Visibility", "Access", "This", "Null", "True", "False", "Bool", "Value" }),
            (SyntaxCategory.Number, new[] { "Number", "Digit", "Timestamp" }),
            (SyntaxCategory.Preprocessor, new[] { "Preprocessor", "Directive", "Region", "DocType", "XmlDeclaration", "Header", "Position" }),
            (SyntaxCategory.Type, new[] { "Type", "Class", "Reference" }),
            (SyntaxCategory.Tag, new[] { "Tag", "Element", "Selector", "Section" }),
            (SyntaxCategory.Attribute, new[] { "Attribute", "Property", "Key", "FieldName", "Variable" }),
            (SyntaxCategory.Operator, new[] { "Operator", "Punctuation", "Brace" }),
            (SyntaxCategory.Error, new[] { "Error", "Fatal" }),
            (SyntaxCategory.Warning, new[] { "Warn" }),
            (SyntaxCategory.Info, new[] { "Info" }),
            (SyntaxCategory.Debug, new[] { "Debug", "Trace" }),
            (SyntaxCategory.Added, new[] { "Added" }),
            (SyntaxCategory.Removed, new[] { "Removed" }),
        };

        /// <summary>The category of a definition's color name; an empty or unknown name is Text.</summary>
        public static SyntaxCategory Categorize(string? colorName)
        {
            if (string.IsNullOrEmpty(colorName)) return SyntaxCategory.Text;
            foreach (var (category, names) in Rows)
                foreach (string name in names)
                    if (colorName.Contains(name, StringComparison.OrdinalIgnoreCase)) return category;
            return SyntaxCategory.Text;
        }

        /// <summary>The palette color of a category.</summary>
        public static PadColor ColorOf(SyntaxCategory category, PadPalette palette) => category switch
        {
            SyntaxCategory.Comment => palette.SyntaxComment,
            SyntaxCategory.String => palette.SyntaxString,
            SyntaxCategory.Keyword => palette.SyntaxKeyword,
            SyntaxCategory.Number => palette.SyntaxNumber,
            SyntaxCategory.Type => palette.SyntaxType,
            SyntaxCategory.Preprocessor => palette.SyntaxPreprocessor,
            SyntaxCategory.Tag => palette.SyntaxTag,
            SyntaxCategory.Attribute => palette.SyntaxAttribute,
            SyntaxCategory.Operator => palette.SyntaxOperator,
            SyntaxCategory.Error => palette.LogError,
            SyntaxCategory.Warning => palette.LogWarning,
            SyntaxCategory.Info => palette.LogInfo,
            SyntaxCategory.Debug => palette.LogDebug,
            SyntaxCategory.Added => palette.DiffAdded,
            SyntaxCategory.Removed => palette.DiffRemoved,
            _ => palette.Text,
        };

        /// <summary>
        /// The color to paint a highlighting color with. A named color takes its category's palette
        /// color. An unnamed one (a definition's inline color) keeps its own hue, moved toward the
        /// text color until it reads at 4.5:1. With neither, null: leave the text as it is.
        /// </summary>
        public static PadColor? Resolve(string? colorName, PadColor? original, PadPalette palette)
        {
            if (!string.IsNullOrEmpty(colorName)) return ColorOf(Categorize(colorName), palette);
            return original?.EnsureContrast(palette.Background, palette.Text, 4.5);
        }
    }
}
