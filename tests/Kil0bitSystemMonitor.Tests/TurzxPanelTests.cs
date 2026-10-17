using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kil0bitSystemMonitor.Companion;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Localization;
using Kil0bitSystemMonitor.Services.Turzx;
using Xunit;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;
using ToggleSwitch = ModernWpf.Controls.ToggleSwitch;

namespace Kil0bitSystemMonitor.Tests;

[Collection("UiTextGlobal")]
public sealed class TurzxPanelTests
{
    [Theory]
    [InlineData("en", "Dark", 760)]
    [InlineData("th", "Light", 420)]
    public void Panel_renders_wrapped_labeled_actions_without_hardware(string language, string theme, int width) => UiThread.Run(() =>
    {
        string previous = UiText.Provider.Language;
        UiText.SetLanguage(language);
        var panel = new TurzxPanel();
        var root = new System.Windows.Controls.Border
        {
            Width = width,
            Padding = new Thickness(16),
            Child = panel,
            Background = new SolidColorBrush(theme == "Dark" ? Color.FromRgb(32, 32, 32) : Color.FromRgb(250, 250, 250)),
        };
        ModernWpf.ThemeManager.SetRequestedTheme(root, theme == "Dark" ? ModernWpf.ElementTheme.Dark : ModernWpf.ElementTheme.Light);
        System.Windows.Documents.TextElement.SetForeground(root, new SolidColorBrush(theme == "Dark" ? Colors.White : Colors.Black));
        try
        {
            panel.Load(null, new AppConfig());
            root.Measure(new Size(width, double.PositiveInfinity));
            root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height));
            root.UpdateLayout();

            Button[] buttons = Descendants<Button>(panel).Where(button =>
                !string.IsNullOrWhiteSpace(ButtonIcon.GetGlyph(button)) ||
                (!string.IsNullOrEmpty(button.Name) && ReferenceEquals(panel.FindName(button.Name), button))).ToArray();
            Assert.True(buttons.Length >= 4);
            Assert.All(buttons, button => Assert.False(string.IsNullOrWhiteSpace(ButtonIcon.GetGlyph(button))));
            Assert.All(buttons.Where(button => button.IsVisible), button =>
            {
                Rect bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                Assert.True(bounds.Right <= width + 1, $"Action exceeds panel width: {button.Content}");
            });
            Assert.False(((Button)panel.FindName("ScanButton")).IsEnabled);

            string folder = Path.Combine(PadWindowTests.RepoRoot(), "artifacts", "ui-fixtures");
            Directory.CreateDirectory(folder);
            var image = new RenderTargetBitmap(width, (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create(Path.Combine(folder, $"turzx-{language}-{theme.ToLowerInvariant()}-{width}.png"));
            encoder.Save(output);
        }
        finally
        {
            panel.Detach();
            UiText.SetLanguage(previous);
        }
    });

    [Fact]
    public Task Connected_panel_updates_orientation_view_mode_and_detaches_cleanly() => UiThread.RunAsync(async () =>
    {
        var config = new AppConfig { TurzxEnabled = true };
        using var service = new TurzxDisplayService(config, new FakeTransport(), startBackground: false);
        await service.ConnectAsync(new TurzxDevice("COM10", "Turzx 3.5\" IPS Display", @"USB\VID_1A86&PID_5722\USB35INCHIPSV2"));

        var panel = new TurzxPanel();
        panel.Load(service, config);
        try
        {
            Assert.True(((Button)panel.FindName("TestPatternButton")).IsEnabled);
            var orientationBox = (ComboBox)panel.FindName("OrientationBox");
            var viewModeBox = (ComboBox)panel.FindName("ViewModeBox");

            orientationBox.SelectedIndex = 1; // Portrait
            Assert.Equal("Portrait", config.TurzxOrientation);

            viewModeBox.SelectedIndex = 1; // Thermal
            Assert.Equal("Thermal", config.TurzxViewMode);

            viewModeBox.SelectedIndex = 4; // Splash
            Assert.Equal("Splash", config.TurzxViewMode);

            ((ToggleSwitch)panel.FindName("EnabledToggle")).IsOn = false;
            Assert.False(config.TurzxEnabled);
            Assert.False(((Button)panel.FindName("TestPatternButton")).IsEnabled);
        }
        finally
        {
            panel.Detach();
        }
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is T result) yield return result;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }

    private sealed class FakeTransport : ITurzxTransport
    {
        public event Action? Disconnected { add { } remove { } }
        public Task<IReadOnlyList<TurzxDevice>> DiscoverAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<TurzxDevice>>(Array.Empty<TurzxDevice>());
        public Task ConnectAsync(TurzxDevice device, CancellationToken token) => Task.CompletedTask;
        public Task WriteAsync(byte[] payload, CancellationToken token) => Task.CompletedTask;
        public Task WriteRectAsync(ushort[] frame, int frameWidth, TurzxRect rect, CancellationToken token) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
