using System.Globalization;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Markup;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Localization;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

[CollectionDefinition("UiTextGlobal", DisableParallelization = true)]
public sealed class UiTextGlobalCollection
{
}

[Collection("UiTextGlobal")]
public sealed class AppLocalizationTests
{
    [Fact]
    public void App_language_defaults_to_Thai_normalizes_invalid_values_and_round_trips()
    {
        var config = new AppConfig();
        Assert.Equal("th", config.AppLanguage);

        config.AppLanguage = " EN ";
        Assert.Equal("en", config.AppLanguage);

        var restored = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config));
        Assert.Equal("en", restored!.AppLanguage);

        Assert.Equal("th", JsonSerializer.Deserialize<AppConfig>("{}")!.AppLanguage);
        Assert.Equal("th", JsonSerializer.Deserialize<AppConfig>("{\"AppLanguage\":\"unknown\"}")!.AppLanguage);
    }

    [Fact]
    public void Unknown_phrases_fall_back_to_English_and_format_uses_the_current_culture()
    {
        UiThread.Run(() =>
        {
            string beforeLanguage = UiText.Language;
            CultureInfo beforeCulture = CultureInfo.CurrentCulture;
            try
            {
                UiText.SetLanguage("th");
                const string unknown = "A phrase deliberately absent from every catalog";
                Assert.Equal(unknown, UiText.Tr(unknown));
                Assert.Equal("Value 42", UiText.Format("Value {0}", 42));
                Assert.Same(beforeCulture, CultureInfo.CurrentCulture);
            }
            finally
            {
                UiText.SetLanguage(beforeLanguage);
            }
        });
    }

    [Fact]
    public void Changing_language_updates_an_existing_markup_binding_without_changing_culture()
    {
        UiThread.Run(() =>
        {
            string beforeLanguage = UiText.Language;
            CultureInfo beforeCulture = CultureInfo.CurrentCulture;
            try
            {
                UiText.SetLanguage("en");
                var target = (TextBlock)XamlReader.Parse("""
                    <TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                               xmlns:loc="clr-namespace:Kil0bitSystemMonitor.Services.Localization;assembly=MicaStats"
                               Text="{loc:Text 'App language'}" />
                    """);
                Assert.Equal("App language", target.Text);

                UiText.SetLanguage("th");
                Assert.NotEqual("App language", UiText.Tr("App language"));
                Assert.Equal(UiText.Tr("App language"), target.Text);
                Assert.Same(beforeCulture, CultureInfo.CurrentCulture);
            }
            finally
            {
                UiText.SetLanguage(beforeLanguage);
            }
        });
    }
}
