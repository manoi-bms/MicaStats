using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.IO;
using System.Windows;
using Application = System.Windows.Application;

namespace Kil0bitSystemMonitor.Services.Localization;

/// <summary>
/// Application UI translations. English phrases are stable keys so a missing catalog entry stays
/// useful and readable; user content, Markdown, logs and persisted values never pass through here.
/// </summary>
public static class UiText
{
    private static readonly UiTextProvider s_provider = new();

    public static UiTextProvider Provider => s_provider;

    public static string Language => s_provider.Language;

    public static string Tr(string english) => s_provider.Translate(english);

    public static string Format(string english, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Tr(english), args);

    /// <summary>Changes UI language without changing the process or thread culture.</summary>
    public static void SetLanguage(string? language)
    {
        string normalized = Normalize(language);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess() && !dispatcher.HasShutdownStarted)
        {
            dispatcher.Invoke(() => s_provider.SetLanguage(normalized));
            return;
        }

        s_provider.SetLanguage(normalized);
    }

    internal static string Normalize(string? language) =>
        string.Equals(language?.Trim(), "en", StringComparison.OrdinalIgnoreCase) ? "en" : "th";
}

/// <summary>The single observable source used by every live XAML localization binding.</summary>
public sealed class UiTextProvider : INotifyPropertyChanged
{
    private readonly IReadOnlyDictionary<string, string> _thai = LoadThaiCatalogs();
    private string _language = "en";

    public string Language => _language;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Translate(string? english)
    {
        if (string.IsNullOrEmpty(english)) return english ?? string.Empty;
        return _language == "th" && _thai.TryGetValue(english, out string? thai) && !string.IsNullOrWhiteSpace(thai)
            ? thai
            : english;
    }

    internal void SetLanguage(string language)
    {
        if (_language == language) return;
        _language = language;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }

    private static IReadOnlyDictionary<string, string> LoadThaiCatalogs()
    {
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            Assembly assembly = typeof(UiTextProvider).Assembly;
            string[] names = assembly.GetManifestResourceNames()
                         .Where(name => name.Contains(".Localization.Catalogs.", StringComparison.Ordinal) &&
                                        name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (names.Length == 0) WarnCatalog("No embedded UI catalogs", null);
            foreach (string name in names)
            {
                try
                {
                    using Stream? stream = assembly.GetManifestResourceStream(name);
                    if (stream == null) { WarnCatalog(name, null); continue; }
                    var catalog = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
                    if (catalog == null) { WarnCatalog(name, null); continue; }
                    foreach ((string english, string thai) in catalog)
                    {
                        if (!string.IsNullOrWhiteSpace(english) && !string.IsNullOrWhiteSpace(thai))
                            translations[english] = thai;
                    }
                }
                catch (Exception ex) { WarnCatalog(name, ex); }
            }
        }
        catch (Exception ex)
        {
            WarnCatalog("Embedded UI catalogs", ex);
        }

        return translations;
    }

    private static void WarnCatalog(string resource, Exception? error)
    {
        try
        {
            DiagnosticsLog.Warn("localization", "UI catalog unavailable: " + resource +
                (error == null ? string.Empty : " (" + error.GetType().Name + ")"));
        }
        catch { /* Diagnostics must not prevent the readable English fallback. */ }
    }
}
