using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>How a highlighting definition's color names map onto the MicaPad palette.</summary>
    public class SyntaxColorsTests
    {
        [Theory]
        [InlineData("Comment", SyntaxCategory.Comment)]
        [InlineData("DocComment", SyntaxCategory.Comment)]
        [InlineData("CommentTags", SyntaxCategory.Comment)]
        [InlineData("String", SyntaxCategory.String)]
        [InlineData("XmlString", SyntaxCategory.String)]
        [InlineData("Char", SyntaxCategory.String)]
        [InlineData("Character", SyntaxCategory.String)]
        [InlineData("AttributeValue", SyntaxCategory.String)]
        [InlineData("Regex", SyntaxCategory.String)]
        [InlineData("Keywords", SyntaxCategory.Keyword)]
        [InlineData("KeywordX", SyntaxCategory.Keyword)]
        [InlineData("JavaScriptKeyWords", SyntaxCategory.Keyword)]
        [InlineData("ValueTypeKeywords", SyntaxCategory.Keyword)]
        [InlineData("ThisOrBaseReference", SyntaxCategory.Keyword)]
        [InlineData("TrueFalse", SyntaxCategory.Keyword)]
        [InlineData("Bool", SyntaxCategory.Keyword)]
        [InlineData("Null", SyntaxCategory.Keyword)]
        [InlineData("Modifiers", SyntaxCategory.Keyword)]
        [InlineData("SelectionStatements", SyntaxCategory.Keyword)]          // PHP and Java: if, else, switch
        [InlineData("JumpStatements", SyntaxCategory.Keyword)]               // return, break
        [InlineData("ExceptionHandlingStatements", SyntaxCategory.Keyword)]
        [InlineData("ControlFlow", SyntaxCategory.Keyword)]                  // C++: if, else, switch
        [InlineData("ExceptionHandling", SyntaxCategory.Keyword)]            // C++: try, catch, throw
        [InlineData("Void", SyntaxCategory.Keyword)]                         // Java
        [InlineData("Package", SyntaxCategory.Keyword)]                      // Java: package, import
        [InlineData("Literals", SyntaxCategory.Keyword)]                     // Java: null
        [InlineData("JavaScriptLiterals", SyntaxCategory.Keyword)]           // true, false, null
        [InlineData("DateLiteral", SyntaxCategory.Text)]                     // VB: one literal, not "Literals"
        [InlineData("NumberLiteral", SyntaxCategory.Number)]
        [InlineData("Digits", SyntaxCategory.Number)]
        [InlineData("LogTimestamp", SyntaxCategory.Number)]
        [InlineData("ReferenceTypes", SyntaxCategory.Type)]
        [InlineData("DataTypes", SyntaxCategory.Type)]
        [InlineData("Class", SyntaxCategory.Type)]
        [InlineData("Preprocessor", SyntaxCategory.Preprocessor)]
        [InlineData("DocType", SyntaxCategory.Preprocessor)]
        [InlineData("Header", SyntaxCategory.Preprocessor)]
        [InlineData("XmlTag", SyntaxCategory.Tag)]
        [InlineData("Selector", SyntaxCategory.Tag)]
        [InlineData("Section", SyntaxCategory.Tag)]
        [InlineData("AttributeName", SyntaxCategory.Attribute)]
        [InlineData("Property", SyntaxCategory.Attribute)]
        [InlineData("FieldName", SyntaxCategory.Attribute)]
        [InlineData("KeyName", SyntaxCategory.Attribute)]
        [InlineData("Variable", SyntaxCategory.Attribute)]
        [InlineData("Punctuation", SyntaxCategory.Operator)]
        [InlineData("CurlyBraces", SyntaxCategory.Operator)]
        [InlineData("LogError", SyntaxCategory.Error)]
        [InlineData("LogWarning", SyntaxCategory.Warning)]
        [InlineData("LogInfo", SyntaxCategory.Info)]
        [InlineData("LogDebug", SyntaxCategory.Debug)]
        [InlineData("AddedText", SyntaxCategory.Added)]
        [InlineData("RemovedText", SyntaxCategory.Removed)]
        [InlineData("Function", SyntaxCategory.Function)]
        [InlineData("MethodCall", SyntaxCategory.Function)]
        [InlineData("MethodName", SyntaxCategory.Function)]
        [InlineData("FunctionCall", SyntaxCategory.Function)]
        [InlineData("JavaScriptGlobalFunctions", SyntaxCategory.Function)]
        [InlineData("Command", SyntaxCategory.Function)]                     // PowerShell cmdlets, as Prism colors them
        [InlineData("FunctionKeywords", SyntaxCategory.Keyword)]
        [InlineData("Friend", SyntaxCategory.Text)]
        [InlineData("", SyntaxCategory.Text)]
        [InlineData(null, SyntaxCategory.Text)]
        public void Color_names_map_to_categories(string? name, SyntaxCategory expected)
        {
            Assert.Equal(expected, SyntaxColors.Categorize(name));
        }

        [Fact]
        public void A_named_color_takes_the_palette_color_of_its_category()
        {
            Assert.Equal(PadPalette.Dark.SyntaxComment, SyntaxColors.Resolve("Comment", PadColor.Parse("#008000"), PadPalette.Dark));
            Assert.Equal(PadPalette.Light.SyntaxKeyword, SyntaxColors.Resolve("Keywords", null, PadPalette.Light));
            Assert.Equal(PadPalette.Light.SyntaxFunction, SyntaxColors.Resolve("MethodCall", PadColor.Parse("#FFFF00"), PadPalette.Light));
            Assert.Equal(PadPalette.Light.Text, SyntaxColors.Resolve("Friend", PadColor.Parse("#FFFF00"), PadPalette.Light));
        }

        [Fact]
        public void An_unnamed_color_keeps_its_hue_but_is_made_readable()
        {
            var yellow = PadColor.Parse("#FFFF00");   // unreadable on white
            var onLight = SyntaxColors.Resolve(null, yellow, PadPalette.Light)!.Value;
            Assert.True(PadColor.Contrast(onLight, PadPalette.Light.Background) >= 4.5);

            var navy = PadColor.Parse("#000080");     // unreadable on the dark page
            var onDark = SyntaxColors.Resolve("", navy, PadPalette.Dark)!.Value;
            Assert.True(PadColor.Contrast(onDark, PadPalette.Dark.Background) >= 4.5);
        }

        [Fact]
        public void A_readable_unnamed_color_is_kept_as_it_is()
        {
            var green = PadColor.Parse("#1A7F37");
            Assert.Equal(green, SyntaxColors.Resolve(null, green, PadPalette.Light));
        }

        [Fact]
        public void With_neither_name_nor_color_nothing_changes()
        {
            Assert.Null(SyntaxColors.Resolve(null, null, PadPalette.Dark));
        }

        [Fact]
        public void EnsureContrast_mixes_toward_the_text_color_only_as_far_as_needed()
        {
            var white = PadColor.Parse("#FFFFFF");
            var black = PadColor.Parse("#000000");
            var pale = PadColor.Parse("#EEEEEE");

            var result = pale.EnsureContrast(white, black, 4.5);

            Assert.True(PadColor.Contrast(result, white) >= 4.5);
            Assert.True(result.R > 0);                                         // not all the way to black
            Assert.Equal(black, pale.EnsureContrast(white, black, 30));       // impossible: ends at the target
        }

        [Theory]
        [InlineData("Dark")]
        [InlineData("Light")]
        public void Every_category_reads_well(string theme)
        {
            var palette = PadPalette.For(theme);
            foreach (SyntaxCategory category in System.Enum.GetValues(typeof(SyntaxCategory)))
                Assert.True(PadColor.Contrast(SyntaxColors.ColorOf(category, palette), palette.Background) >= 4.5, theme + " " + category);
        }
    }
}
