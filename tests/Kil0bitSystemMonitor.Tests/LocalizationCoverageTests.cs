using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class LocalizationCoverageTests
{
    [Fact]
    public void Every_localized_Xaml_phrase_has_a_nonempty_Thai_translation()
    {
        var catalogs = ThaiCatalog();
        string root = PadWindowTests.RepoRoot();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj" or "tests" or "artifacts" or ".git")) continue;
            foreach (XAttribute attribute in XDocument.Load(file).Descendants().Attributes())
            {
                Match match = Regex.Match(attribute.Value, "^\\{loc:Text '(.*)'\\}$", RegexOptions.Singleline);
                if (match.Success) keys.Add(match.Groups[1].Value.Replace("\\'", "'").Replace("\\\\", "\\"));
            }
        }
        Assert.True(keys.Count > 500, "Expected coverage across the application, not a single window.");
        string[] missing = keys.Where(key => !catalogs.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value)).Order().ToArray();
        Assert.True(missing.Length == 0, "Missing Thai UI phrases: " + string.Join(" | ", missing));
    }

    [Fact]
    public void Thai_templates_preserve_their_English_format_arguments()
    {
        foreach ((string key, string value) in ThaiCatalog())
        {
            string[] EnglishArguments(string template) => Regex.Matches(template, @"(?<!\{)\{(\d+)(?:[^}]*)\}(?!\})")
                .Select(match => match.Groups[1].Value).Distinct().Order().ToArray();
            Assert.True(EnglishArguments(key).SequenceEqual(EnglishArguments(value)), $"Format arguments differ for: {key}");
        }
    }

    private static Dictionary<string, string> ThaiCatalog()
    {
        Assembly assembly = typeof(SettingsWindow).Assembly;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] names = assembly.GetManifestResourceNames().Where(name => name.Contains(".Localization.Catalogs.") && name.EndsWith(".json")).ToArray();
        foreach (string required in new[] { "app.json", "pad.json", "settings.json", "settings-dynamic.json" })
            Assert.Contains(names, name => name.EndsWith("." + required, StringComparison.Ordinal));
        foreach (string name in names)
        {
            using Stream stream = assembly.GetManifestResourceStream(name)!;
            foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!)
            {
                if (result.TryGetValue(pair.Key, out string? existing)) Assert.Equal(existing, pair.Value);
                result[pair.Key] = pair.Value;
            }
        }
        Assert.True(result.Count > 500);
        return result;
    }
}
