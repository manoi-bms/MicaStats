using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Kil0bitSystemMonitor.Ai;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Conference;
using Xunit;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Path = System.IO.Path;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;

namespace Kil0bitSystemMonitor.Tests;

public sealed class SettingsIconGuidelineTests
{
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Ai_and_search_actions_render_readable_icons_at_constrained_width(string theme) => UiThread.Run(() =>
    {
        var content = new StackPanel();
        var root = new Border
        {
            Width = 520,
            Padding = new Thickness(16),
            Background = new SolidColorBrush(theme == "Dark" ? Color.FromRgb(32, 32, 32) : Color.FromRgb(250, 250, 250)),
            Child = content,
        };
        ModernWpf.ThemeManager.SetRequestedTheme(root,
            theme == "Dark" ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);
        System.Windows.Documents.TextElement.SetForeground(root,
            new SolidColorBrush(theme == "Dark" ? Colors.White : Colors.Black));

        var ai = new AiSettingsPanel();
        var search = new SearchSettingsPanel { Margin = new Thickness(0, 16, 0, 0) };
        content.Children.Add(ai);
        content.Children.Add(search);
        LayoutToContent(root, 520);

        Button[] actions = Descendants<Button>(root)
            .Where(button => IsDisplayed(button, root) && !string.IsNullOrWhiteSpace(ButtonIcon.GetGlyph(button)))
            .ToArray();
        Assert.True(actions.Length >= 8, $"Expected representative AI and Search actions, found {actions.Length}.");
        foreach (Button action in actions) AssertReadableAndFits(action, root, 520);

        DecorativeIcon[] headings = Descendants<DecorativeIcon>(search)
            .Where(heading => heading.Text is "\uE721" or "\uE8CB")
            .ToArray();
        Assert.Equal(2, headings.Length);
        Assert.All(headings, heading =>
        {
            Assert.Null(UIElementAutomationPeer.CreatePeerForElement(heading));
            Assert.Equal(theme == "Dark" ? Colors.White : Colors.Black,
                Assert.IsType<SolidColorBrush>(heading.Foreground).Color);
        });
        SaveFixture(root, 520, "settings-ai-search-" + theme.ToLowerInvariant());
    });

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Settings_action_groups_use_semantic_icons_and_wrap_without_clipping(string theme) => UiThread.Run(() =>
    {
        XDocument markup = XDocument.Load(Path.Combine(PadWindowTests.RepoRoot(), "SettingsWindow.xaml"));
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Capture region now"] = "\uE722",
            ["Capture window"] = "\uE7C4",
            ["Capture scrolling"] = "\uECE7",
            ["Open folder"] = "\uE838",
            ["Open MicaPad"] = "\uE8A7",
            ["Open notes folder"] = "\uE838",
            ["Change PIN…"] = "\uE8D7",
            ["Lock now"] = "\uE72E",
            ["Reset vault…"] = "\uE74D",
            ["Download and install"] = "\uE896",
            ["Release notes"] = "\uE8A5",
            ["Skip this version"] = "\uE71A",
            ["Quit Application"] = "\uE7E8",
            ["Save & Close"] = "\uE74E",
        };

        var root = new Border
        {
            Width = 430,
            Padding = new Thickness(16),
            Background = new SolidColorBrush(theme == "Dark" ? Color.FromRgb(32, 32, 32) : Color.FromRgb(250, 250, 250)),
        };
        ModernWpf.ThemeManager.SetRequestedTheme(root,
            theme == "Dark" ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);
        var groups = new StackPanel();
        root.Child = groups;

        foreach (string[] labels in new[]
        {
            new[] { "Capture region now", "Capture window", "Capture scrolling", "Open folder" },
            new[] { "Open MicaPad", "Open notes folder" },
            new[] { "Change PIN…", "Lock now", "Reset vault…" },
            new[] { "Download and install", "Release notes", "Skip this version" },
            new[] { "Quit Application", "Save & Close" },
        })
        {
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            foreach (string label in labels)
            {
                XElement source = FindButton(markup, label);
                string glyph = source.Attributes().Single(attribute => attribute.Name.LocalName == "ButtonIcon.Glyph").Value;
                Assert.Equal(expected[label], glyph);
                if (label != "Quit Application" && label != "Save & Close")
                    Assert.Equal("WrapPanel", source.Parent?.Name.LocalName);

                var button = new Button { Content = label, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 6) };
                ButtonIcon.SetGlyph(button, glyph);
                row.Children.Add(button);
            }
            groups.Children.Add(row);
        }

        var meeting = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach ((XElement source, string expectedGlyph) in new[]
        {
            (FindNamedButton(markup, "TestMeetingAsr2Button"), "\uE703"),
            (FindButton(markup, "Save service settings"), "\uE74E"),
        })
        {
            string glyph = source.Attributes().Single(attribute => attribute.Name.LocalName == "ButtonIcon.Glyph").Value;
            Assert.Equal(expectedGlyph, glyph);
            var button = new Button
            {
                Content = source.Attribute("Content")!.Value,
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 6),
            };
            ButtonIcon.SetGlyph(button, glyph);
            meeting.Children.Add(button);
        }
        groups.Children.Add(meeting);

        LayoutToContent(root, 430);
        foreach (Button action in Descendants<Button>(root)) AssertReadableAndFits(action, root, 430);
        SaveFixture(root, 430, "settings-action-groups-" + theme.ToLowerInvariant());
    });

    [Fact]
    public Task Meeting_test_action_switches_icon_label_and_accessible_name_while_running() => UiThread.RunAsync(async () =>
    {
        var address = new TextBox { Text = "https://asr.example/v1" };
        var button = new Button();
        var status = new TextBlock();
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var action = new MeetingServiceTestAction(MeetingServiceKind.Asr2, address, button, status,
            (_, _, _) => reply.Task);

        Layout(button, 220, 44);
        AssertActionState(button, "Test connection", "\uE703", "Test ASR2 connection");

        Task pending = action.RunAsync();
        Layout(button, 220, 44);
        AssertActionState(button, "Cancel test", "\uE711", "Cancel ASR2 connection test");

        action.Cancel();
        reply.SetResult("A late result must be ignored.");
        await pending;
        Layout(button, 220, 44);
        AssertActionState(button, "Test connection", "\uE703", "Test ASR2 connection");
    });

    private static XElement FindButton(XDocument markup, string content) => Assert.Single(markup.Descendants(),
        element => element.Name.LocalName == "Button" && (string?)element.Attribute("Content") == content);

    private static XElement FindNamedButton(XDocument markup, string name) => Assert.Single(markup.Descendants(),
        element => element.Name.LocalName == "Button" && element.Attributes()
            .Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == name));

    private static void AssertActionState(Button button, string content, string glyph, string accessibleName)
    {
        Assert.Equal(content, button.Content);
        Assert.Equal(glyph, ButtonIcon.GetGlyph(button));
        Assert.Equal(accessibleName, new ButtonAutomationPeer(button).GetName());
        var label = Assert.Single(Descendants<ContentPresenter>(button), item => item.Name == "ActionLabel");
        Assert.Equal(content, label.Content);
        Assert.True(label.ActualWidth > 0);
    }

    private static void AssertReadableAndFits(Button button, FrameworkElement root, double width)
    {
        Assert.False(string.IsNullOrWhiteSpace(ButtonIcon.GetGlyph(button)));
        var glyph = Assert.Single(Descendants<DecorativeIcon>(button));
        Assert.False(string.IsNullOrWhiteSpace(glyph.Text));
        var label = Assert.Single(Descendants<ContentPresenter>(button), item => item.Name == "ActionLabel");
        Assert.Equal(button.Content, label.Content);
        Assert.True(label.ActualWidth > 0, $"Hidden action label: {button.Content}");
        var naturalLabel = new TextBlock
        {
            Text = button.Content?.ToString() ?? string.Empty,
            FontFamily = button.FontFamily,
            FontSize = button.FontSize,
            FontStretch = button.FontStretch,
            FontStyle = button.FontStyle,
            FontWeight = button.FontWeight,
        };
        naturalLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.True(label.ActualWidth + 1 >= naturalLabel.DesiredSize.Width,
            $"Clipped action label: {button.Content} ({label.ActualWidth:0.#} < {naturalLabel.DesiredSize.Width:0.#})");
        Assert.False(string.IsNullOrWhiteSpace(new ButtonAutomationPeer(button).GetName()));
        Rect bounds = button.TransformToAncestor(root).TransformBounds(new Rect(new Point(), button.RenderSize));
        Assert.InRange(bounds.Left, 0, width);
        Assert.InRange(bounds.Right, 0, width + 1);
    }

    private static void LayoutToContent(FrameworkElement element, int width)
    {
        element.Measure(new Size(width, double.PositiveInfinity));
        int height = Math.Max(1, (int)Math.Ceiling(element.DesiredSize.Height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        PadLanguageWindowTests.Pump();
    }

    private static void Layout(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        PadLanguageWindowTests.Pump();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static bool IsDisplayed(DependencyObject element, DependencyObject root)
    {
        for (DependencyObject? current = element; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement ui && ui.Visibility != Visibility.Visible) return false;
            if (ReferenceEquals(current, root)) return true;
        }
        return false;
    }

    private static void SaveFixture(Visual root, int width, string name)
    {
        string? folder = Environment.GetEnvironmentVariable("MICAPAD_ICON_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder)) return;
        int height = Math.Max(1, (int)Math.Ceiling(((FrameworkElement)root).ActualHeight));
        Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(stream);
    }
}
