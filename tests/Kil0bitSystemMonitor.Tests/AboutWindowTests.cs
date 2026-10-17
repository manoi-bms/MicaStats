using System.Collections;
using System.IO;
using System.Resources;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Kil0bitSystemMonitor.Services.Localization;
using Control = System.Windows.Controls.Control;
using Size = System.Windows.Size;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

[Collection("UiTextGlobal")]
public sealed class AboutWindowTests
{
    [Fact]
    public void About_banner_is_packaged_and_decodes_at_full_resolution()
    {
        var assembly = typeof(SettingsWindow).Assembly;
        string generatedResources = Assert.Single(assembly.GetManifestResourceNames(),
            name => name.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase));

        using Stream manifest = Assert.IsAssignableFrom<Stream>(assembly.GetManifestResourceStream(generatedResources));
        using var reader = new ResourceReader(manifest);
        DictionaryEntry banner = Assert.Single(reader.Cast<DictionaryEntry>(), entry =>
            string.Equals(entry.Key?.ToString(), "assets/about-banner.png", StringComparison.OrdinalIgnoreCase));
        using Stream image = Assert.IsAssignableFrom<Stream>(banner.Value);
        BitmapFrame frame = BitmapFrame.Create(image, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        Assert.True(frame.PixelWidth >= 1600, $"Expected a high-resolution banner, got {frame.PixelWidth}px wide.");
        Assert.InRange((double)frame.PixelWidth / frame.PixelHeight, 2.9, 3.1);
    }

    [Fact]
    public void About_hero_keeps_real_version_binding_and_a_responsive_text_layer()
    {
        XElement about = AboutMarkup();
        XElement hero = Named(about, "AboutHero");
        XElement image = Assert.Single(hero.Descendants(), element => element.Name.LocalName == "ImageBrush");

        Assert.Equal("pack://application:,,,/MicaStats;component/Assets/about-banner.png", (string?)image.Attribute("ImageSource"));
        Assert.DoesNotContain(hero.Attributes(), attribute => attribute.Name.LocalName == "Width");
        Assert.DoesNotContain(hero.Attributes(), attribute => attribute.Name.LocalName == "Height");
        Assert.Contains(hero.Descendants(), element =>
            element.Name.LocalName == "TextBlock" && (string?)element.Attribute("Text") == "{Binding AppVersion}");
        Assert.Contains(hero.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && ((string?)element.Attribute("Text"))?.Contains("system monitoring", StringComparison.Ordinal) == true
            && (string?)element.Attribute("TextWrapping") == "Wrap");
    }

    [Fact]
    public void About_cards_use_theme_resources_and_width_safe_layout()
    {
        XElement about = AboutMarkup();
        XElement cards = Named(about, "AboutFeatureCards");
        XElement[] cardBorders = cards.Elements().Where(element => element.Name.LocalName == "Border").ToArray();

        Assert.Equal(3, cardBorders.Length);
        Assert.All(cardBorders, card =>
        {
            Assert.Equal("{DynamicResource SystemControlBackgroundChromeMediumLowBrush}", (string?)card.Attribute("Background"));
            Assert.Equal("{DynamicResource SystemControlElevationBorderBrush}", (string?)card.Attribute("BorderBrush"));
            Assert.DoesNotContain(card.Attributes(), attribute => attribute.Name.LocalName is "Width" or "MaxWidth");
            Assert.Contains(card.Descendants(), element =>
                element.Name.LocalName == "FontIcon" && !string.IsNullOrWhiteSpace((string?)element.Attribute("Glyph")));
            Assert.Contains(card.Descendants(), element =>
                element.Name.LocalName == "TextBlock" && (string?)element.Attribute("TextWrapping") == "Wrap");
        });
        Assert.DoesNotContain(cards.Descendants(), element => element.Name.LocalName == "UniformGrid");
    }

    [Theory]
    [InlineData("th", true, 380)]
    [InlineData("th", false, 760)]
    [InlineData("en", true, 760)]
    [InlineData("en", false, 380)]
    public void About_renders_in_both_languages_and_themes_without_clipping(string language, bool dark, int width)
    {
        UiThread.Run(() =>
        {
            string previous = UiText.Language;
            try
            {
                UiText.SetLanguage(language);
                // Loose XAML must load the controls assembly before resolving its XML namespace.
                _ = typeof(ModernWpf.Controls.HyperlinkButton).Assembly.FullName;
                XDocument document = XDocument.Load(Path.Combine(PadWindowTests.RepoRoot(), "SettingsWindow.xaml"));
                var about = new XElement(Named(document.Root!, "AboutSection"));
                about.SetAttributeValue("Visibility", "Visible");
                foreach (XAttribute ns in document.Root!.Attributes().Where(a => a.IsNamespaceDeclaration))
                    about.SetAttributeValue(ns.Name, ns.Value.StartsWith("clr-namespace:", StringComparison.Ordinal)
                        && !ns.Value.Contains(";assembly=", StringComparison.Ordinal)
                        ? ns.Value + ";assembly=MicaStats" : ns.Value);
                var content = (StackPanel)XamlReader.Parse(about.ToString());
                content.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "SystemControlForegroundBaseHighBrush");
                content.DataContext = new { AppVersion = "1.15.0" };
                var scroll = new ScrollViewer { Content = content, Width = width, Height = 680,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                scroll.SetResourceReference(Control.BackgroundProperty, "SystemControlBackgroundAltHighBrush");
                var host = new Window { Content = scroll, SizeToContent = SizeToContent.WidthAndHeight,
                    ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
                ModernWpf.ThemeManager.SetRequestedTheme(host, dark ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);
                try
                {
                host.Show();
                host.UpdateLayout();
                PadLanguageWindowTests.Pump();
                Assert.IsType<SolidColorBrush>(content.TryFindResource("SystemControlHighlightAccentBrush"));
                Assert.True(scroll.ExtentHeight > scroll.ViewportHeight);
                Assert.InRange(scroll.ExtentWidth, 1, width + 1);
                Assert.True(content.ActualHeight > 500);
                var hero = (Border)content.FindName("AboutHero");
                Assert.InRange(hero.ActualWidth, 1, width);
                Assert.True(hero.ActualHeight >= 180);

                string? folder = Environment.GetEnvironmentVariable("MICAPAD_MENU_SCREENSHOTS");
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    Directory.CreateDirectory(folder);
                    var bitmap = new RenderTargetBitmap(width, 680, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(scroll);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(folder, $"about-{language}-{(dark ? "dark" : "light")}-{width}.png"));
                    encoder.Save(file);
                }
                }
                finally { host.Close(); }
            }
            finally { UiText.SetLanguage(previous); }
        });
    }

    [Fact]
    public void About_project_actions_preserve_destinations_and_show_icons()
    {
        XElement links = Named(AboutMarkup(), "AboutProjectLinks");
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "https://github.com/manoi-bms/MicaStats",
            "https://github.com/manoi-bms/MicaStats/releases",
            "https://github.com/manoi-bms/MicaStats/issues",
            "https://github.com/kil0bit-kb/kil0bit-system-monitor",
        };
        XElement[] actions = links.Elements().Where(element => element.Name.LocalName == "HyperlinkButton").ToArray();

        Assert.True(expected.SetEquals(actions.Select(action => (string)action.Attribute("NavigateUri")!)));
        Assert.All(actions, action =>
        {
            Assert.Contains(action.Descendants(), element =>
                element.Name.LocalName == "FontIcon" && !string.IsNullOrWhiteSpace((string?)element.Attribute("Glyph")));
            Assert.Contains(action.Descendants(), element =>
                element.Name.LocalName == "TextBlock" && !string.IsNullOrWhiteSpace((string?)element.Attribute("Text")));
        });
    }

    [Fact]
    public void About_credits_keep_authorship_and_mit_attribution()
    {
        string copy = string.Join(" ", AboutMarkup().Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .Where(text => !string.IsNullOrWhiteSpace(text)));

        Assert.Contains("Chaiyaporn Suratemeekul (manoi-bms)", copy, StringComparison.Ordinal);
        Assert.Contains("kil0bit System Monitor by KB", copy, StringComparison.Ordinal);
        Assert.Contains("MIT License", copy, StringComparison.Ordinal);
    }

    private static XElement AboutMarkup()
    {
        XDocument document = XDocument.Load(Path.Combine(PadWindowTests.RepoRoot(), "SettingsWindow.xaml"));
        return Named(document.Root!, "AboutSection");
    }

    private static XElement Named(XElement root, string name) => Assert.Single(root.DescendantsAndSelf(), element =>
        element.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == name));
}
