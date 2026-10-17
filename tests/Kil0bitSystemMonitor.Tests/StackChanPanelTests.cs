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
using Kil0bitSystemMonitor.Services.StackChan;
using Kil0bitSystemMonitor.Services.StackChan.Presence;
using Xunit;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;
using ToggleSwitch = ModernWpf.Controls.ToggleSwitch;

namespace Kil0bitSystemMonitor.Tests;

[Collection("UiTextGlobal")]
public sealed class StackChanPanelTests
{
    [Theory]
    [InlineData("en", "Dark", 760)]
    [InlineData("th", "Light", 420)]
    public void Panel_renders_wrapped_labeled_actions_without_hardware(string language, string theme, int width) => UiThread.Run(() =>
    {
        string previous = UiText.Provider.Language;
        UiText.SetLanguage(language);
        var panel = new StackChanPanel();
        var root = new System.Windows.Controls.Border { Width = width, Padding = new Thickness(16), Child = panel,
            Background = new SolidColorBrush(theme == "Dark" ? Color.FromRgb(32, 32, 32) : Color.FromRgb(250, 250, 250)) };
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
            Assert.True(buttons.Length >= 15);
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
            using var output = File.Create(Path.Combine(folder, $"stackchan-{language}-{theme.ToLowerInvariant()}-{width}.png"));
            encoder.Save(output);
        }
        finally { panel.Detach(); UiText.SetLanguage(previous); }
    });

    [Fact]
    public Task Connected_panel_disables_head_controls_in_quiet_mode_and_detaches_cleanly() => UiThread.RunAsync(async () =>
    {
        var config = new AppConfig { StackChanEnabled = true };
        using var service = new StackChanService(config, new FakeTransport(), startBackground: false);
        await service.ConnectAsync(new StackChanDevice(0x112233445566, "Fixture robot"));
        var panel = new StackChanPanel();
        panel.Load(service, config);
        try
        {
            Assert.True(((Button)panel.FindName("HappyFaceButton")).IsEnabled);
            Assert.True(((Button)panel.FindName("MoveButton")).IsEnabled);
            ((ToggleSwitch)panel.FindName("QuietModeToggle")).IsOn = true;
            Assert.True(config.StackChanQuietMode);
            Assert.False(((Button)panel.FindName("MoveButton")).IsEnabled);
            Assert.True(((Button)panel.FindName("HappyFaceButton")).IsEnabled);
            ((ToggleSwitch)panel.FindName("EnabledToggle")).IsOn = false;
            Assert.False(((Button)panel.FindName("HappyFaceButton")).IsEnabled);
        }
        finally { panel.Detach(); }
    });

    [Fact]
    public Task Usb_panel_explains_missing_calibration_and_disables_automatic_motion() => UiThread.RunAsync(async () =>
    {
        var config = new AppConfig { StackChanEnabled = true, StackChanUseUsb = true };
        var transport = new FakeTransport { Capabilities = StackChanCapabilities.Avatar |
            StackChanCapabilities.Rgb | StackChanCapabilities.Dashboard };
        using var service = new StackChanService(config, transport, startBackground: false);
        await service.ConnectAsync(new StackChanDevice(0x112233445566, "USB fixture",
            StackChanFirmware.UiFlow2, StackChanConnectionKind.Usb, "COM9"));
        var panel = new StackChanPanel();
        panel.Load(service, config);
        try
        {
            Assert.True(((Button)panel.FindName("HappyFaceButton")).IsEnabled);
            Assert.False(((Button)panel.FindName("MoveButton")).IsEnabled);
            Assert.False(((ToggleSwitch)panel.FindName("AutoMotionToggle")).IsEnabled);
            Assert.Equal(UiText.Tr("Head movement is unavailable because saved servo calibration could not be verified. Complete the official servo-zero calibration, then restart the temporary companion."),
                ((System.Windows.Controls.TextBlock)panel.FindName("MotionAvailabilityText")).Text);
        }
        finally { panel.Detach(); }
    });

    [Fact]
    public void Connection_mode_persists_and_usb_starter_requires_an_enabled_usb_selection() => UiThread.Run(() =>
    {
        var config = new AppConfig { StackChanEnabled = true };
        using var service = new StackChanService(config, new FakeTransport(), startBackground: false);
        var panel = new StackChanPanel();
        panel.Load(service, config);
        try
        {
            var mode = (ComboBox)panel.FindName("ConnectionModeBox");
            var devices = (ComboBox)panel.FindName("DeviceList");
            var starter = (Button)panel.FindName("StartUsbCompanionButton");

            Assert.False(config.StackChanUseUsb);
            Assert.Equal(Visibility.Collapsed, starter.Visibility);

            mode.SelectedIndex = 1;
            Assert.True(config.StackChanUseUsb);
            Assert.Equal(Visibility.Visible, starter.Visibility);
            Assert.False(starter.IsEnabled);

            devices.Items.Add(new StackChanDevice(0x112233445566, "Bluetooth fixture"));
            devices.SelectedIndex = 0;
            Assert.False(starter.IsEnabled);

            devices.Items.Add(new StackChanDevice(0, "USB fixture", StackChanFirmware.UiFlow2,
                StackChanConnectionKind.Usb, "COM8"));
            devices.SelectedIndex = 1;
            Assert.True(starter.IsEnabled);

            ((ToggleSwitch)panel.FindName("EnabledToggle")).IsOn = false;
            Assert.False(starter.IsEnabled);

            mode.SelectedIndex = 0;
            Assert.False(config.StackChanUseUsb);
            Assert.Empty(devices.Items);
            Assert.Equal(Visibility.Collapsed, starter.Visibility);
        }
        finally { panel.Detach(); }
    });

    [Fact]
    public Task Panel_detaches_automatically_when_unloaded() => UiThread.RunAsync(async () =>
    {
        var config = new AppConfig { StackChanEnabled = true };
        using var service = new StackChanService(config, new FakeTransport(), startBackground: false);
        var panel = new StackChanPanel();
        panel.Load(service, config);

        panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        await service.ConnectAsync(new StackChanDevice(0x112233445566, "After unload"));

        // Because Unloaded detached the service listener, the panel's MoveButton remains disabled until reloaded.
        Assert.False(((Button)panel.FindName("MoveButton")).IsEnabled);

        panel.Load(service, config);
        Assert.True(((Button)panel.FindName("MoveButton")).IsEnabled);
        panel.Detach();
    });

    [Fact]
    public Task Quick_manual_command_does_not_disable_connection_controls_while_in_flight() => UiThread.RunAsync(async () =>
    {
        var config = new AppConfig { StackChanEnabled = true };
        var writeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new GatedWriteTransport(writeGate.Task);
        using var service = new StackChanService(config, transport, startBackground: false);
        var panel = new StackChanPanel();
        try
        {
            panel.Load(service, config);
            await service.ConnectAsync(new StackChanDevice(0x112233445566, "Stack-chan"));

            var happyButton = (Button)panel.FindName("HappyFaceButton");
            happyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            // While the face write is still in flight, connection discovery controls stay enabled.
            Assert.True(((Button)panel.FindName("ScanButton")).IsEnabled);
            Assert.True(((ComboBox)panel.FindName("DeviceList")).IsEnabled);

            writeGate.SetResult();
            await panel.LastCommandTask;
        }
        finally { panel.Detach(); }
    });

    private static readonly StackChanDevice UsbFixture = new(0x112233445566, "USB fixture",
        StackChanFirmware.UiFlow2, StackChanConnectionKind.Usb, "COM9");
    private const StackChanCapabilities Everything = StackChanCapabilities.Avatar | StackChanCapabilities.Rgb |
        StackChanCapabilities.Motion | StackChanCapabilities.Dashboard;

    private static (StackChanService Robot, PresenceService Presence, PresenceFrames Frames) Presence(AppConfig config)
    {
        var robot = new StackChanService(config, new FakeTransport { Capabilities = Everything }, startBackground: false);
        var frames = new PresenceFrames();
        var presence = new PresenceService(config, robot, frames, _ => new PresenceDetector(), TimeProvider.System,
            new NoPresenceActions(), startBackground: false);
        return (robot, presence, frames);
    }

    [Fact]
    public Task Usb_panel_preselects_the_remembered_robot() => UiThread.RunAsync(async () =>
    {
        var config = new AppConfig { StackChanEnabled = true, StackChanUseUsb = true,
            StackChanDeviceAddress = UsbFixture.AddressText };
        var other = new StackChanDevice(0xAABBCCDDEEFF, "Other USB", StackChanFirmware.UiFlow2, StackChanConnectionKind.Usb, "COM3");
        using var robot = new StackChanService(config, new FakeTransport { Devices = [other, UsbFixture] }, startBackground: false);
        var panel = new StackChanPanel();
        panel.Load(robot, config);
        try
        {
            await panel.RememberedDeviceTask;
            var devices = (ComboBox)panel.FindName("DeviceList");
            Assert.Equal(UsbFixture, devices.SelectedItem);
            Assert.True(((Button)panel.FindName("StartUsbCompanionButton")).IsEnabled);
            Assert.Equal("Disconnected", robot.Snapshot.Status);
        }
        finally { panel.Detach(); }
    });

    [Fact]
    public void Auto_start_switch_is_on_by_default_and_saves() => UiThread.Run(() =>
    {
        var config = new AppConfig { StackChanEnabled = true, StackChanUseUsb = true };
        using var robot = new StackChanService(config, new FakeTransport(), startBackground: false);
        var panel = new StackChanPanel();
        panel.Load(robot, config);
        try
        {
            var toggle = (ToggleSwitch)panel.FindName("AutoStartCompanionToggle");
            Assert.True(toggle.IsOn);
            toggle.IsOn = false;
            Assert.False(config.StackChanAutoStartCompanion);
        }
        finally { panel.Detach(); }
    });

    [Fact]
    public void Presence_toggle_needs_usb_mode() => UiThread.Run(() =>
    {
        var config = new AppConfig { StackChanEnabled = true };
        var (robot, presence, _) = Presence(config);
        var panel = new StackChanPanel();
        panel.Load(robot, config, presence);
        try
        {
            var toggle = (ToggleSwitch)panel.FindName("PresenceToggle");
            Assert.False(toggle.IsEnabled);

            ((ComboBox)panel.FindName("ConnectionModeBox")).SelectedIndex = 1;
            Assert.True(toggle.IsEnabled);
            toggle.IsOn = true;
            Assert.True(config.StackChanPresenceEnabled);
        }
        finally { panel.Detach(); presence.Dispose(); robot.Dispose(); }
    });

    [Fact]
    public void Preview_subscribes_only_while_visible() => UiThread.Run(() =>
    {
        var config = new AppConfig { StackChanEnabled = true, StackChanUseUsb = true, StackChanPresenceEnabled = true };
        var (robot, presence, _) = Presence(config);
        var panel = new StackChanPanel();
        var window = new Window { Content = panel, Left = -20000, Top = -20000, Width = 800, Height = 600,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        try
        {
            panel.Load(robot, config, presence);
            Assert.False(presence.HasPreviewSubscribers);
            window.Show();
            Assert.True(presence.HasPreviewSubscribers);
            window.Hide();
            Assert.False(presence.HasPreviewSubscribers);
            window.Show();
            panel.Detach();
            Assert.False(presence.HasPreviewSubscribers);
        }
        finally { window.Close(); presence.Dispose(); robot.Dispose(); }
    });

    [Fact]
    public Task Direction_check_needs_presence_motion_and_no_quiet() => UiThread.RunAsync(async () =>
    {
        var config = new AppConfig { StackChanEnabled = true, StackChanUseUsb = true, StackChanPresenceEnabled = true };
        var (robot, presence, _) = Presence(config);
        var panel = new StackChanPanel();
        panel.Load(robot, config, presence);
        try
        {
            var button = (Button)panel.FindName("DirectionCheckButton");
            Assert.False(button.IsEnabled);
            await robot.ConnectAsync(UsbFixture);
            Assert.True(button.IsEnabled);
            ((ToggleSwitch)panel.FindName("QuietModeToggle")).IsOn = true;
            Assert.False(button.IsEnabled);
        }
        finally { panel.Detach(); presence.Dispose(); robot.Dispose(); }
    });

    [Fact]
    public Task Gpu_text_shows_adapter_and_timing() => UiThread.RunAsync(async () =>
    {
        var config = new AppConfig { StackChanEnabled = true, StackChanUseUsb = true, StackChanPresenceEnabled = true };
        var (robot, presence, frames) = Presence(config);
        var panel = new StackChanPanel();
        panel.Load(robot, config, presence);
        try
        {
            await robot.ConnectAsync(UsbFixture);
            await presence.TickAsync();
            for (int i = 0; i < 2; i++)
            {
                frames.Raise();
                await presence.WhenIdleAsync();
            }
            await presence.TickAsync();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            Assert.Equal(UiText.Format("You are here. Your face is {0}% across and {1}% down the picture; centred is 50% and 50%.", 50, 50),
                ((System.Windows.Controls.TextBlock)panel.FindName("PresenceStateText")).Text);
            string text = ((System.Windows.Controls.TextBlock)panel.FindName("PresenceGpuText")).Text;
            Assert.Contains("Fixture GPU", text, StringComparison.Ordinal);
            Assert.Contains("3.5", text, StringComparison.Ordinal);
            Assert.Equal(UiText.Tr("Camera on"), ((System.Windows.Controls.TextBlock)panel.FindName("CameraStateText")).Text);
        }
        finally { panel.Detach(); presence.Dispose(); robot.Dispose(); }
    });

    [Fact]
    public Task Presence_problem_is_shown() => UiThread.RunAsync(async () =>
    {
        var config = new AppConfig { StackChanEnabled = true, StackChanUseUsb = true, StackChanPresenceEnabled = true };
        var robot = new StackChanService(config, new FakeTransport { Capabilities = Everything }, startBackground: false);
        var frames = new PresenceFrames();
        var presence = new PresenceService(config, robot, frames, _ => new PresenceDetector { Throw = true },
            TimeProvider.System, new NoPresenceActions(), startBackground: false);
        var panel = new StackChanPanel();
        panel.Load(robot, config, presence);
        try
        {
            await robot.ConnectAsync(UsbFixture);
            await presence.TickAsync();
            for (int i = 0; i < 6; i++)
            {
                frames.Raise();
                await presence.WhenIdleAsync();
            }
            await presence.TickAsync();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            Assert.Equal(UiText.Tr("The GPU stopped answering, so face detection moved to the CPU."),
                ((System.Windows.Controls.TextBlock)panel.FindName("PresenceStateText")).Text);
        }
        finally { panel.Detach(); presence.Dispose(); robot.Dispose(); }
    });

    private sealed class PresenceFrames : IStackChanFrameSource
    {
        public event Action<StackChanFrame>? FrameReceived;
        public void Raise() => FrameReceived?.Invoke(new StackChanFrame(0, [0xFF, 0xD8, 0xFF, 0xD9], DateTimeOffset.UtcNow));
    }

    private sealed class PresenceDetector : IFaceDetector
    {
        public bool Throw { get; init; }
        public string AdapterName => "Fixture GPU";
        public bool OnGpu => true;
        public IReadOnlyList<DetectedFace> Detect(byte[] jpeg, out double milliseconds, out bool usable)
        {
            if (Throw) throw new InvalidOperationException("Synthetic device removed.");
            milliseconds = 3.5;
            usable = true;
            return [new DetectedFace(0.5f, 0.5f, 0.3f, 0.4f, 0.9f)];
        }
        public void Dispose() { }
    }

    private sealed class NoPresenceActions : IPresenceActions
    {
        public TimeSpan UserIdleFor() => TimeSpan.Zero;
        public void LockVault() { }
        public void ShowGuard(bool on) { }
        public void LockWindows() { }
        public void ShowBreakToast() { }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is T result) yield return result;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }

    private sealed class FakeTransport : IStackChanTransport
    {
        public StackChanCapabilities Capabilities { get; set; } = StackChanCapabilities.Avatar |
            StackChanCapabilities.Rgb | StackChanCapabilities.Motion;
        public IReadOnlyList<StackChanDevice> Devices { get; set; } = [];
        public event Action? Disconnected { add { } remove { } }
        public Task<IReadOnlyList<StackChanDevice>> DiscoverAsync(TimeSpan duration, CancellationToken token) =>
            Task.FromResult(Devices);
        public Task<StackChanCapabilities> ConnectAsync(StackChanDevice device, CancellationToken token) =>
            Task.FromResult(Capabilities);
        public Task WriteAsync(StackChanChannel channel, byte[] json, CancellationToken token) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class GatedWriteTransport : IStackChanTransport
    {
        private readonly Task _writeGate;

        public GatedWriteTransport(Task writeGate) => _writeGate = writeGate;

        public StackChanCapabilities Capabilities { get; } = StackChanCapabilities.Avatar |
            StackChanCapabilities.Rgb | StackChanCapabilities.Motion;
        public event Action? Disconnected { add { } remove { } }
        public Task<IReadOnlyList<StackChanDevice>> DiscoverAsync(TimeSpan duration, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<StackChanDevice>>([]);
        public Task<StackChanCapabilities> ConnectAsync(StackChanDevice device, CancellationToken token) =>
            Task.FromResult(Capabilities);
        public Task WriteAsync(StackChanChannel channel, byte[] json, CancellationToken token) =>
            _writeGate.WaitAsync(token);
        public Task DisconnectAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
