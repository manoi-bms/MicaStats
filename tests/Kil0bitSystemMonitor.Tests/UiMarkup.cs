using System.Xml.Linq;
using System.IO;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

/// <summary>Reads the English key carried by a live <c>loc:Text</c> markup extension.</summary>
internal static class UiMarkup
{
    internal static XDocument Load(string file) =>
        XDocument.Load(Path.Combine(PadWindowTests.RepoRoot(), file), LoadOptions.PreserveWhitespace);

    internal static string? EnglishKey(XAttribute? attribute) => EnglishKey(attribute?.Value);

    internal static string? EnglishKey(string? value)
    {
        const string prefix = "{loc:Text '";
        if (value == null || !value.StartsWith(prefix, StringComparison.Ordinal) ||
            !value.EndsWith("'}", StringComparison.Ordinal)) return null;
        return value[prefix.Length..^2].Replace("\\'", "'", StringComparison.Ordinal)
                                        .Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    internal static XElement Named(XDocument markup, string name) => Assert.Single(markup.Descendants(), element =>
        element.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == name));

    internal static XElement Localized(XContainer scope, string attributeName, string english) =>
        Assert.Single(scope.Descendants(), element => EnglishKey(element.Attributes()
            .SingleOrDefault(attribute => attribute.Name.LocalName == attributeName)) == english);

    internal static void ContainsLocalized(XContainer scope, string attributeName, string englishPart) =>
        Assert.Contains(scope.Descendants(), element => EnglishKey(element.Attributes()
            .SingleOrDefault(attribute => attribute.Name.LocalName == attributeName))?.Contains(englishPart, StringComparison.Ordinal) == true);
}
