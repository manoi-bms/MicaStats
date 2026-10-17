using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Localization;
using Kil0bitSystemMonitor.Services.StackChan;
using Kil0bitSystemMonitor.Services.StackChan.Presence;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Kil0bitSystemMonitor.Companion;

public partial class StackChanPanel : UserControl
{
    private static readonly Regex RgbPattern = new("^#?[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);

    private StackChanService? _service;
    private AppConfig? _config;
    private CancellationTokenSource? _panelOperation;
    private bool _loading;
    private bool _panelGestureActive;
    private string? _operationMessage;
    private object[]? _operationMessageArgs;
    private int _gestureVersion;
    private PresenceService? _presence;
    private bool _previewSubscribed, _previewPending, _directionChecking;
    private string? _directionMessage;
    private WriteableBitmap? _previewBitmap;

    /// <summary>The quiet USB port lookup that pre-selects the remembered robot; tests await it.</summary>
    internal Task RememberedDeviceTask { get; private set; } = Task.CompletedTask;

    public StackChanPanel()
    {
        InitializeComponent();
        YawSlider.ValueChanged += OnMotionSliderChanged;
        PitchSlider.ValueChanged += OnMotionSliderChanged;
        SpeedSlider.ValueChanged += OnMotionSliderChanged;
        IsVisibleChanged += (_, _) => UpdatePreviewSubscription();
        Unloaded += (_, _) => Detach();
    }

    public void Load(StackChanService? service, AppConfig config, PresenceService? presence = null)
    {
        Detach();
        _service = service;
        _config = config;
        ClearOperationMessage();
        _loading = true;
        try
        {
            EnabledToggle.IsOn = config.StackChanEnabled;
            SelectConnectionMode(config.StackChanUseUsb);
            AutoReconnectToggle.IsOn = config.StackChanAutoReconnect;
            AutoStartCompanionToggle.IsOn = config.StackChanAutoStartCompanion;
            ReactLoadToggle.IsOn = config.StackChanReactToLoad;
            ReactAlertsToggle.IsOn = config.StackChanReactToAlerts;
            ReactAiToggle.IsOn = config.StackChanReactToAi;
            ReactMeetingToggle.IsOn = config.StackChanReactToMeeting;
            AutoMotionToggle.IsOn = config.StackChanAllowAutoMotion;
            QuietModeToggle.IsOn = config.StackChanQuietMode;
            BrightnessSlider.Value = config.StackChanLedBrightness;
            SelectInterval(config.StackChanUpdateSeconds);
            PresenceToggle.IsOn = config.StackChanPresenceEnabled;
            GuardVaultToggle.IsOn = config.StackChanGuardVault;
            GuardBlurToggle.IsOn = config.StackChanGuardBlur;
            LockWindowsToggle.IsOn = config.StackChanLockWindowsWhenAway;
            SelectMinutes(LockWindowsMinutesBox, config.StackChanLockWindowsMinutes);
            SelectMinutes(BreakMinutesBox, config.StackChanBreakMinutes);
        }
        finally
        {
            _loading = false;
        }

        _presence = presence;
        _directionMessage = null;
        if (_service != null) _service.Changed += OnServiceChanged;
        if (_presence != null) _presence.Changed += OnServiceChanged;
        UiText.Provider.PropertyChanged += OnLanguageChanged;
        UpdatePreviewSubscription();
        RefreshSnapshot();
        if (_service != null && config.StackChanEnabled && config.StackChanUseUsb && DeviceList.Items.Count == 0)
            RememberedDeviceTask = PreselectRememberedAsync(_service);
    }

    /// <summary>The robot is remembered by its USB identity; list the ports and select it, so Start is one click.</summary>
    private async Task PreselectRememberedAsync(StackChanService service)
    {
        IReadOnlyList<StackChanDevice> devices;
        try { devices = await service.FindUsbPortsAsync(); }
        catch (Exception) { return; } // The Find USB ports button still works.
        if (!ReferenceEquals(_service, service) || _panelOperation != null || DeviceList.Items.Count > 0) return;
        foreach (var device in devices) DeviceList.Items.Add(device);
        DeviceList.SelectedItem = devices.FirstOrDefault(device =>
            string.Equals(device.AddressText, _config?.StackChanDeviceAddress, StringComparison.OrdinalIgnoreCase));
        RefreshSnapshot();
    }

    public void Detach()
    {
        CancelPanelOperation();
        _gestureVersion++;
        if (_panelGestureActive) _service?.CancelGesture();
        _panelGestureActive = false;
        if (_service != null) _service.Changed -= OnServiceChanged;
        if (_presence != null)
        {
            _presence.Changed -= OnServiceChanged;
            if (_previewSubscribed) _presence.Preview -= OnPreview;
        }
        _previewSubscribed = false;
        UiText.Provider.PropertyChanged -= OnLanguageChanged;
        _service = null;
        _presence = null;
        _config = null;
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UiTextProvider.Language)) RefreshSnapshot();
    }

    private void OnServiceChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshSnapshot));
            return;
        }
        RefreshSnapshot();
    }

    private void RefreshSnapshot()
    {
        StackChanSnapshot? snapshot = _service?.Snapshot;
        bool enabled = _config?.StackChanEnabled == true;
        bool usbMode = _config?.StackChanUseUsb == true;
        bool busy = _panelOperation != null;
        bool connected = snapshot?.State == StackChanConnectionState.Connected;
        StackChanCapabilities capabilities = snapshot?.Capabilities ?? StackChanCapabilities.None;
        bool avatar = enabled && !busy && connected && capabilities.HasFlag(StackChanCapabilities.Avatar);
        bool rgb = enabled && !busy && connected && capabilities.HasFlag(StackChanCapabilities.Rgb);
        bool motion = enabled && !busy && connected && capabilities.HasFlag(StackChanCapabilities.Motion) && _config?.StackChanQuietMode != true;

        if (_service == null)
        {
            ConnectionStatus.Text = UiText.Tr("StackChan support is unavailable in this build.");
            ConnectionDetail.Text = UiText.Tr("Restart MicaStats after installing a build with Bluetooth companion support.");
        }
        else if (!enabled)
        {
            ConnectionStatus.Text = UiText.Tr("StackChan companion is off.");
            ConnectionDetail.Text = UiText.Tr(usbMode
                ? "All companion controls are off. Turn it on, then find the StackChan USB serial port."
                : "All companion controls are off. Turn it on, start the robot control mode, then scan.");
        }
        else
        {
            ConnectionStatus.Text = UiText.Tr(snapshot?.Status ?? "Disconnected");
            ConnectionDetail.Text = !string.IsNullOrWhiteSpace(_operationMessage)
                ? _operationMessageArgs is { Length: > 0 }
                    ? UiText.Format(_operationMessage, _operationMessageArgs)
                    : UiText.Tr(_operationMessage)
                : !string.IsNullOrWhiteSpace(snapshot?.Error)
                ? UiText.Tr(snapshot!.Error!)
                : connected
                    ? UiText.Format("Connected to {0}. Available controls: {1}. Companion state: {2}.",
                        snapshot!.DeviceName, DescribeCapabilities(capabilities), UiText.Tr(snapshot.Activity))
                    : UiText.Tr(usbMode
                        ? "Choose a USB serial port. Connect joins a running companion; Start temporary USB companion can load it into memory."
                        : snapshot?.Firmware == StackChanFirmware.UiFlow2
                            ? "Run the supplied UIFlow companion script on StackChan while connecting."
                            : "Open DANCE on StackChan and keep it visible while connecting.");
        }

        ConnectionGuideText.Text = UiText.Tr(usbMode
            ? "USB control requires the UIFlow 2.0 StackChan firmware. Connect to a running companion or temporarily start the bundled script. USB mode does not flash or change robot files."
            : "Factory firmware: open DANCE. UIFlow 2.0: run the MicaStats companion script. Control uses Bluetooth LE; USB supplies power and debug.");
        ScanButton.Content = UiText.Tr(usbMode ? "Find USB ports" : "Scan for StackChan");
        UsbGuidanceText.Visibility = usbMode ? Visibility.Visible : Visibility.Collapsed;

        ReactionControls.IsEnabled = enabled;
        FaceControls.IsEnabled = enabled;
        HeadControls.IsEnabled = enabled;
        ConnectionModeBox.IsEnabled = enabled && !busy;
        ScanButton.IsEnabled = enabled && !busy && _service != null && snapshot?.State is not StackChanConnectionState.Discovering and not StackChanConnectionState.Connecting;
        DeviceList.IsEnabled = ScanButton.IsEnabled;
        bool selectedModeDevice = DeviceList.SelectedItem is StackChanDevice selected &&
                                  selected.Connection == (usbMode ? StackChanConnectionKind.Usb : StackChanConnectionKind.Bluetooth);
        ConnectButton.IsEnabled = ScanButton.IsEnabled && selectedModeDevice;
        StartUsbCompanionButton.Visibility = usbMode ? Visibility.Visible : Visibility.Collapsed;
        StartUsbCompanionButton.IsEnabled = enabled && usbMode && !busy && !connected &&
                                            DeviceList.SelectedItem is StackChanDevice { Connection: StackChanConnectionKind.Usb };
        DisconnectButton.IsEnabled = snapshot?.State is StackChanConnectionState.Connected or StackChanConnectionState.Connecting;
        AutoReconnectToggle.IsEnabled = enabled && !string.IsNullOrWhiteSpace(_config?.StackChanDeviceAddress);
        AutoStartCompanionToggle.Visibility = usbMode ? Visibility.Visible : Visibility.Collapsed;
        AutoStartCompanionToggle.IsEnabled = enabled && usbMode;
        CancelOperationButton.Visibility = busy && (snapshot?.State is StackChanConnectionState.Discovering or StackChanConnectionState.Connecting)
            ? Visibility.Visible : Visibility.Collapsed;
        CancelOperationButton.IsEnabled = CancelOperationButton.Visibility == Visibility.Visible;

        foreach (Button button in new[] { NeutralFaceButton, HappyFaceButton, BusyFaceButton, ConcernedFaceButton, SleepyFaceButton, ApplyAvatarButton })
            button.IsEnabled = avatar;
        SetRgbButton.IsEnabled = rgb;
        foreach (Button button in new[] { MoveButton, NeutralHeadButton, NodButton, ShakeButton, LookAroundButton })
            button.IsEnabled = motion;
        CancelGestureButton.IsEnabled = enabled && connected && capabilities.HasFlag(StackChanCapabilities.Motion);
        AutoMotionToggle.IsEnabled = enabled && (!connected || capabilities.HasFlag(StackChanCapabilities.Motion));
        MotionAvailabilityText.Text = connected && snapshot?.Connection == StackChanConnectionKind.Usb &&
                                      !capabilities.HasFlag(StackChanCapabilities.Motion)
            ? UiText.Tr("Head movement is unavailable because saved servo calibration could not be verified. Complete the official servo-zero calibration, then restart the temporary companion.")
            : UiText.Tr("Calibrate the servos before moving the head. Automatic gestures are gentle and run at most once every 90 seconds.");
        FirmwareModeText.Text = connected && capabilities.HasFlag(StackChanCapabilities.Dashboard)
            ? UiText.Tr("MicaStats UIFlow 2.0 companion is connected. Rotating screens, live stats, event alerts, and animated LEDs are available. Head controls require saved servo calibration.")
            : UiText.Tr("Factory DANCE firmware supports face, lights, and head control. It does not support live numeric stats or PC audio.");

        BrightnessText.Text = $"{Math.Round(BrightnessSlider.Value):0}%";
        UpdateMotionLabels();
        RefreshPresence(enabled, usbMode, connected, capabilities, snapshot?.Connection == StackChanConnectionKind.Usb);
    }

    private void RefreshPresence(bool enabled, bool usbMode, bool connected, StackChanCapabilities capabilities, bool usbLink)
    {
        bool available = enabled && usbMode && _presence != null;
        bool on = available && _config?.StackChanPresenceEnabled == true;
        PresenceToggle.IsEnabled = available;
        PresenceOptions.IsEnabled = on;
        LockWindowsMinutesBox.IsEnabled = on && LockWindowsToggle.IsOn;
        DirectionCheckButton.IsEnabled = on && connected && usbLink && !_directionChecking &&
            capabilities.HasFlag(StackChanCapabilities.Motion) && capabilities.HasFlag(StackChanCapabilities.Dashboard) &&
            _config?.StackChanQuietMode != true;
        if (_directionMessage != null) DirectionCheckText.Text = UiText.Tr(_directionMessage);
        else if (_config?.StackChanHeadDirectionChecked == true)
            DirectionCheckText.Text = UiText.Tr("Head direction saved. StackChan will now turn toward you.");

        PresenceStatus? status = on ? _presence!.Status : null;
        CameraStateText.Text = UiText.Tr(status?.CameraOn == true ? "Camera on" : "Camera off");
        PresenceStateText.Text = status?.Problem is { } problem ? UiText.Tr(problem)
            : status is not { CameraOn: true } ? UiText.Tr(on
                ? "Waiting for the USB companion to send pictures."
                : "Turn on presence detection to let StackChan look for you.")
            : status.State switch
            {
                PresenceState.Present when status.Faces > 1 => UiText.Format("You are here, with {0} faces in view.", status.Faces),
                PresenceState.Present when status.Face is { } face => UiText.Format(
                    "You are here. Your face is {0}% across and {1}% down the picture; centred is 50% and 50%.",
                    (int)Math.Round(face.CenterX * 100), (int)Math.Round(face.CenterY * 100)),
                PresenceState.Present => UiText.Tr("You are here."),
                PresenceState.Away => UiText.Tr("Nobody at the desk. StackChan is resting."),
                _ => UiText.Tr("Waiting for the camera.")
            };
        PresenceGpuText.Text = status == null || string.IsNullOrEmpty(status.AdapterName)
            ? UiText.Tr("Face detection starts on the best GPU when the camera turns on.")
            : status.OnGpu
                ? UiText.Format("{0} · {1} ms per picture · {2} pictures per second", status.AdapterName,
                    status.Milliseconds.ToString("0.0", CultureInfo.InvariantCulture), status.Fps.ToString("0.0", CultureInfo.InvariantCulture))
                : UiText.Format("CPU (no compatible GPU) · {0} ms per picture",
                    status.Milliseconds.ToString("0.0", CultureInfo.InvariantCulture));
    }

    private void UpdatePreviewSubscription()
    {
        bool wanted = IsVisible && _presence != null;
        if (wanted == _previewSubscribed) return;
        if (wanted) _presence!.Preview += OnPreview;
        else if (_presence != null) _presence.Preview -= OnPreview;
        _previewSubscribed = wanted;
        if (!wanted) PresenceBoxes.Children.Clear();
    }

    private void OnPreview(StackChanFrame frame, IReadOnlyList<DetectedFace> faces)
    {
        // Detection thread: skip a frame rather than queue a backlog of pictures on the UI thread.
        if (_previewPending) return;
        _previewPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _previewPending = false;
            if (_previewSubscribed) DrawPreview(frame.Jpeg, faces);
        }));
    }

    private void DrawPreview(byte[] jpeg, IReadOnlyList<DetectedFace> faces)
    {
        try
        {
            using var stream = new System.IO.MemoryStream(jpeg, writable: false);
            BitmapSource decoded = new JpegBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
            var bgr = new FormatConvertedBitmap(decoded, System.Windows.Media.PixelFormats.Bgr32, null, 0);
            if (_previewBitmap == null || _previewBitmap.PixelWidth != bgr.PixelWidth || _previewBitmap.PixelHeight != bgr.PixelHeight)
            {
                _previewBitmap = new WriteableBitmap(bgr.PixelWidth, bgr.PixelHeight, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
                PresencePreview.Source = _previewBitmap;
            }
            var pixels = new byte[bgr.PixelWidth * bgr.PixelHeight * 4];
            bgr.CopyPixels(pixels, bgr.PixelWidth * 4, 0);
            _previewBitmap.WritePixels(new Int32Rect(0, 0, bgr.PixelWidth, bgr.PixelHeight), pixels, bgr.PixelWidth * 4, 0);
        }
        catch (Exception) { return; } // A damaged picture simply is not shown.

        PresenceBoxes.Children.Clear();
        double width = PresencePreview.ActualWidth, height = PresencePreview.ActualHeight;
        var accent = (System.Windows.Media.Brush)FindResource("SystemControlHighlightAccentBrush");
        foreach (var face in faces)
        {
            var box = new System.Windows.Shapes.Rectangle
            {
                Width = face.Width * width, Height = face.Height * height,
                Stroke = accent, StrokeThickness = 2, RadiusX = 3, RadiusY = 3
            };
            Canvas.SetLeft(box, (face.CenterX - face.Width / 2) * width);
            Canvas.SetTop(box, (face.CenterY - face.Height / 2) * height);
            PresenceBoxes.Children.Add(box);
        }
    }

    private void OnPresenceSettingsToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _config == null) return;
        _config.StackChanPresenceEnabled = PresenceToggle.IsOn;
        _config.StackChanGuardVault = GuardVaultToggle.IsOn;
        _config.StackChanGuardBlur = GuardBlurToggle.IsOn;
        _config.StackChanLockWindowsWhenAway = LockWindowsToggle.IsOn;
        RefreshSnapshot();
    }

    private void OnPresenceMinutesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _config == null || sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string value } } box ||
            !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes)) return;
        if (ReferenceEquals(box, LockWindowsMinutesBox)) _config.StackChanLockWindowsMinutes = minutes;
        else _config.StackChanBreakMinutes = minutes;
    }

    private static void SelectMinutes(ComboBox box, int minutes)
    {
        ComboBoxItem? best = null;
        foreach (ComboBoxItem item in box.Items)
        {
            if (item.Tag is string tag && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) &&
                (best == null || Math.Abs(value - minutes) < Math.Abs(int.Parse((string)best.Tag, CultureInfo.InvariantCulture) - minutes)))
                best = item;
        }
        box.SelectedItem = best;
    }

    private async void OnDirectionCheck(object sender, RoutedEventArgs e)
    {
        PresenceService? presence = _presence;
        if (presence == null || _directionChecking) return;
        _directionChecking = true;
        _directionMessage = "Checking. Keep your face in view while StackChan nods and turns a little.";
        RefreshSnapshot();
        try
        {
            _directionMessage = await presence.CheckDirectionAsync() switch
            {
                DirectionCheckResult.Saved => "Head direction saved. StackChan will now turn toward you.",
                DirectionCheckResult.NoFace => "StackChan could not see your face. Sit in front of it, check the preview, and try again.",
                DirectionCheckResult.Inconclusive => "The picture barely moved. Sit a little closer and try again.",
                _ => "Turn on presence detection, connect the USB companion, and switch off Quiet mode first."
            };
        }
        catch (Exception)
        {
            _directionMessage = "The head check stopped. Check the USB connection and try again.";
        }
        finally
        {
            _directionChecking = false;
            if (ReferenceEquals(_presence, presence)) RefreshSnapshot();
        }
    }

    private static string DescribeCapabilities(StackChanCapabilities capabilities)
    {
        var names = new List<string>();
        if (capabilities.HasFlag(StackChanCapabilities.Avatar)) names.Add(UiText.Tr("face"));
        if (capabilities.HasFlag(StackChanCapabilities.Rgb)) names.Add(UiText.Tr("LEDs"));
        if (capabilities.HasFlag(StackChanCapabilities.Motion)) names.Add(UiText.Tr("head movement"));
        if (capabilities.HasFlag(StackChanCapabilities.Dashboard)) names.Add(UiText.Tr("live stats screen"));
        return names.Count == 0 ? UiText.Tr("none") : string.Join(", ", names);
    }

    private void OnSettingsToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _config == null) return;
        ClearOperationMessage();
        _config.StackChanEnabled = EnabledToggle.IsOn;
        _config.StackChanAutoReconnect = AutoReconnectToggle.IsOn;
        _config.StackChanAutoStartCompanion = AutoStartCompanionToggle.IsOn;
        _config.StackChanReactToLoad = ReactLoadToggle.IsOn;
        _config.StackChanReactToAlerts = ReactAlertsToggle.IsOn;
        _config.StackChanReactToAi = ReactAiToggle.IsOn;
        _config.StackChanReactToMeeting = ReactMeetingToggle.IsOn;
        _config.StackChanAllowAutoMotion = AutoMotionToggle.IsOn;
        _config.StackChanQuietMode = QuietModeToggle.IsOn;
        if (!_config.StackChanEnabled) CancelPanelOperation();
        if (_config.StackChanQuietMode)
        {
            _gestureVersion++;
            _service?.CancelGesture();
            _panelGestureActive = false;
        }
        _service?.RefreshSettings();
        RefreshSnapshot();
    }

    private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        BrightnessText.Text = $"{Math.Round(e.NewValue):0}%";
        if (_loading || _config == null) return;
        ClearOperationMessage();
        _config.StackChanLedBrightness = (int)Math.Round(e.NewValue);
        _service?.RefreshSettings();
    }

    private void OnUpdateIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _config == null || UpdateIntervalBox.SelectedItem is not ComboBoxItem { Tag: string value } ||
            !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)) return;
        ClearOperationMessage();
        _config.StackChanUpdateSeconds = seconds;
        _service?.RefreshSettings();
    }

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ClearOperationMessage();
        RefreshSnapshot();
    }

    private void OnConnectionModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _config == null || ConnectionModeBox.SelectedItem is not ComboBoxItem { Tag: string mode }) return;
        bool useUsb = string.Equals(mode, "Usb", StringComparison.Ordinal);
        if (_config.StackChanUseUsb == useUsb) { RefreshSnapshot(); return; }
        ClearOperationMessage();
        CancelPanelOperation();
        DeviceList.Items.Clear();
        _config.StackChanUseUsb = useUsb;
        _service?.RefreshSettings();
        RefreshSnapshot();
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        StackChanService? service = _service;
        if (service == null || _config?.StackChanEnabled != true || _panelOperation != null) return;
        CancellationTokenSource operation = BeginPanelOperation();
        DeviceList.Items.Clear();
        bool usbMode = _config.StackChanUseUsb;
        SetOperationMessage(usbMode ? "Finding StackChan USB serial ports…" : "Scanning nearby Bluetooth LE devices…");
        RefreshSnapshot();
        try
        {
            IReadOnlyList<StackChanDevice> devices = await service.DiscoverAsync(operation.Token);
            if (!IsCurrent(service, operation)) return;
            StackChanConnectionKind requested = usbMode ? StackChanConnectionKind.Usb : StackChanConnectionKind.Bluetooth;
            foreach (StackChanDevice device in devices.Where(device => device.Connection == requested)) DeviceList.Items.Add(device);
            SelectRememberedDevice();
            if (DeviceList.SelectedIndex < 0 && DeviceList.Items.Count > 0) DeviceList.SelectedIndex = 0;
            int count = DeviceList.Items.Count;
            if (count == 0)
                SetOperationMessage(usbMode
                    ? "No StackChan USB serial port was found. Check the USB cable and Windows device connection, then try again."
                    : "No compatible robot was found. Open DANCE or run the UIFlow 2.0 companion script, keep StackChan nearby, and scan again.");
            else
                SetOperationMessage(usbMode
                    ? "Found {0} StackChan USB port(s). Select one, then connect or start the temporary companion."
                    : "Found {0} compatible device(s). Select one and connect.", count);
        }
        catch (OperationCanceledException) { if (ReferenceEquals(_service, service)) ClearOperationMessage(); }
        catch (Exception ex) { if (IsCurrent(service, operation)) ShowOperationError(service, ex); }
        finally
        {
            bool refresh = IsCurrent(service, operation);
            EndPanelOperation(operation);
            if (refresh) RefreshSnapshot();
        }
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        StackChanService? service = _service;
        if (service == null || _config?.StackChanEnabled != true || _panelOperation != null ||
            DeviceList.SelectedItem is not StackChanDevice device) return;
        StackChanConnectionKind requested = _config.StackChanUseUsb ? StackChanConnectionKind.Usb : StackChanConnectionKind.Bluetooth;
        if (device.Connection != requested) return;
        CancellationTokenSource operation = BeginPanelOperation();
        try
        {
            await service.ConnectAsync(device, operation.Token);
            if (IsCurrent(service, operation) && _config != null)
            {
                _config.StackChanDeviceAddress = device.AddressText;
                _config.StackChanDeviceName = device.Name;
                service.RefreshSettings();
                ClearOperationMessage();
            }
        }
        catch (OperationCanceledException) { if (ReferenceEquals(_service, service)) ClearOperationMessage(); }
        catch (Exception ex) { if (IsCurrent(service, operation)) ShowOperationError(service, ex); }
        finally
        {
            bool refresh = IsCurrent(service, operation);
            EndPanelOperation(operation);
            if (refresh) RefreshSnapshot();
        }
    }

    private async void OnStartUsbCompanion(object sender, RoutedEventArgs e)
    {
        StackChanService? service = _service;
        if (service == null || _config?.StackChanEnabled != true || !_config.StackChanUseUsb ||
            _panelOperation != null || service.Snapshot.State == StackChanConnectionState.Connected ||
            DeviceList.SelectedItem is not StackChanDevice { Connection: StackChanConnectionKind.Usb } device) return;
        CancellationTokenSource operation = BeginPanelOperation();
        SetOperationMessage("Starting the temporary USB companion in robot memory…");
        RefreshSnapshot();
        try
        {
            await service.StartUsbCompanionAsync(device, operation.Token);
            if (IsCurrent(service, operation) && _config != null)
            {
                _config.StackChanDeviceAddress = device.AddressText;
                _config.StackChanDeviceName = device.Name;
                ClearOperationMessage();
            }
        }
        catch (OperationCanceledException) { if (ReferenceEquals(_service, service)) ClearOperationMessage(); }
        catch (Exception ex) { if (IsCurrent(service, operation)) ShowOperationError(service, ex); }
        finally
        {
            bool refresh = IsCurrent(service, operation);
            EndPanelOperation(operation);
            if (refresh) RefreshSnapshot();
        }
    }

    private async void OnDisconnect(object sender, RoutedEventArgs e)
    {
        StackChanService? service = _service;
        if (service == null) return;
        ClearOperationMessage();
        CancelPanelOperation();
        _panelGestureActive = false;
        try { await service.DisconnectAsync(); }
        catch (Exception ex) { if (ReferenceEquals(_service, service)) ShowOperationError(service, ex); }
        if (ReferenceEquals(_service, service)) RefreshSnapshot();
    }

    private void OnCancelOperation(object sender, RoutedEventArgs e)
    {
        if (_panelOperation == null || _service?.Snapshot.State is not (StackChanConnectionState.Discovering or StackChanConnectionState.Connecting))
            return;
        ClearOperationMessage();
        CancelPanelOperation();
        RefreshSnapshot();
    }

    private async void OnFace(object sender, RoutedEventArgs e)
    {
        StackChanService? service = _service;
        if (service == null || sender is not Button { Tag: string faceName } ||
            !Enum.TryParse(faceName, out StackChanFace face) || !CanManual(service, StackChanCapabilities.Avatar)) return;
        await RunCommandAsync(service, (current, token) => current.SetFaceAsync(face, token));
    }

    private async void OnSetRgb(object sender, RoutedEventArgs e)
    {
        StackChanService? service = _service;
        if (service == null || !CanManual(service, StackChanCapabilities.Rgb)) return;
        string left = NormalizeColor(LeftColorBox.Text);
        string right = NormalizeColor(RightColorBox.Text);
        if (left.Length == 0 || right.Length == 0 || !TryDouble(RgbDurationBox.Text, 0, 10, out double duration))
        {
            ShowValidationError("Enter two six-digit RGB colors and a transition from 0 to 10 seconds.");
            return;
        }
        await RunCommandAsync(service, (current, token) => current.SetRgbAsync(left, right, duration, token));
    }

    private async void OnApplyAvatar(object sender, RoutedEventArgs e)
    {
        StackChanService? service = _service;
        if (service == null || !CanManual(service, StackChanCapabilities.Avatar)) return;
        if (!TryFeature(LeftEyeX, LeftEyeY, LeftEyeRotation, LeftEyeWeight, LeftEyeSize, out StackChanFeature left) ||
            !TryFeature(RightEyeX, RightEyeY, RightEyeRotation, RightEyeWeight, RightEyeSize, out StackChanFeature right) ||
            !TryFeature(MouthX, MouthY, MouthRotation, MouthWeight, MouthSize, out StackChanFeature mouth))
        {
            ShowValidationError("One or more face values are outside the allowed range.");
            return;
        }
        var avatar = new StackChanAvatar(left, right, mouth);
        await RunCommandAsync(service, (current, token) => current.SetAvatarAsync(avatar, token));
    }

    private async void OnMove(object sender, RoutedEventArgs e)
    {
        StackChanService? service = _service;
        if (service == null || !CanManual(service, StackChanCapabilities.Motion, motion: true)) return;
        double yaw = YawSlider.Value, pitch = PitchSlider.Value;
        int speed = (int)Math.Round(SpeedSlider.Value);
        await RunCommandAsync(service, (current, token) => current.MoveAsync(yaw, pitch, speed, token));
    }

    private async void OnCenter(object sender, RoutedEventArgs e)
    {
        StackChanService? service = _service;
        if (service == null || !CanManual(service, StackChanCapabilities.Motion, motion: true)) return;
        YawSlider.Value = 0;
        PitchSlider.Value = 45;
        int speed = (int)Math.Round(SpeedSlider.Value);
        await RunCommandAsync(service, (current, token) => current.MoveAsync(0, 45, speed, token));
    }

    private async void OnGesture(object sender, RoutedEventArgs e)
    {
        StackChanService? service = _service;
        if (service == null || sender is not Button { Tag: string gestureName } ||
            !Enum.TryParse(gestureName, out StackChanGesture gesture) ||
            !CanManual(service, StackChanCapabilities.Motion, motion: true)) return;
        int gestureVersion = ++_gestureVersion;
        _panelGestureActive = true;
        try { await RunCommandAsync(service, (current, token) => current.GestureAsync(gesture, token)); }
        finally { if (_gestureVersion == gestureVersion) _panelGestureActive = false; }
    }

    private void OnCancelGesture(object sender, RoutedEventArgs e)
    {
        _service?.CancelGesture();
        _gestureVersion++;
        _panelGestureActive = false;
        SetOperationMessage("Remaining gesture steps were cancelled.");
        RefreshSnapshot();
    }

    private void OnOpenUiFlowGuide(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://docs.m5stack.com/en/uiflow2/stackchan/program",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetOperationMessage("Could not open the UIFlow 2.0 setup guide.");
            System.Diagnostics.Debug.WriteLine($"Could not open StackChan UIFlow guide: {ex.GetType().Name}");
            RefreshSnapshot();
        }
    }

    private CancellationTokenSource? _commandOperation;
    internal Task LastCommandTask { get; private set; } = Task.CompletedTask;

    private Task RunCommandAsync(StackChanService service, Func<StackChanService, CancellationToken, Task> command)
    {
        if (_panelOperation != null || _commandOperation != null || !ReferenceEquals(_service, service)) return Task.CompletedTask;
        ClearOperationMessage();
        var operation = new CancellationTokenSource();
        _commandOperation = operation;
        Task task = ExecuteCommandAsync(service, operation, command);
        LastCommandTask = task;
        return task;
    }

    private async Task ExecuteCommandAsync(StackChanService service, CancellationTokenSource operation, Func<StackChanService, CancellationToken, Task> command)
    {
        try { await command(service, operation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (ReferenceEquals(_service, service) && ReferenceEquals(_commandOperation, operation) && _config != null) ShowOperationError(service, ex); }
        finally
        {
            bool refresh = ReferenceEquals(_service, service) && ReferenceEquals(_commandOperation, operation) && _config != null;
            if (ReferenceEquals(_commandOperation, operation)) _commandOperation = null;
            operation.Dispose();
            if (refresh) RefreshSnapshot();
        }
    }

    private CancellationTokenSource BeginPanelOperation()
    {
        if (_panelOperation != null)
            throw new InvalidOperationException("A StackChan panel operation is already running.");
        ClearOperationMessage();
        _panelOperation = new CancellationTokenSource();
        RefreshSnapshot();
        return _panelOperation;
    }

    private void EndPanelOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_panelOperation, operation)) _panelOperation = null;
        operation.Dispose();
    }

    private void CancelPanelOperation()
    {
        CancellationTokenSource? cmd = _commandOperation;
        _commandOperation = null;
        if (cmd != null)
        {
            cmd.Cancel();
            cmd.Dispose();
        }
        CancellationTokenSource? operation = _panelOperation;
        _panelOperation = null;
        if (operation == null) return;
        operation.Cancel();
        operation.Dispose();
    }

    private bool IsCurrent(StackChanService service, CancellationTokenSource operation) =>
        ReferenceEquals(_service, service) && ReferenceEquals(_panelOperation, operation) && _config != null;

    private bool CanManual(StackChanService service, StackChanCapabilities capability, bool motion = false)
    {
        if (!ReferenceEquals(_service, service) || _config?.StackChanEnabled != true || _panelOperation != null || _commandOperation != null)
            return false;
        StackChanSnapshot snapshot = service.Snapshot;
        return snapshot.State == StackChanConnectionState.Connected && snapshot.Capabilities.HasFlag(capability) &&
               (!motion || _config?.StackChanQuietMode != true);
    }

    private void ShowOperationError(StackChanService service, Exception ex)
    {
        string? safeError = service.Snapshot.Error;
        SetOperationMessage(string.IsNullOrWhiteSpace(safeError)
            ? _config?.StackChanUseUsb == true
                ? "The StackChan USB operation failed. Check the cable, port, and companion script, then try again."
                : "The StackChan operation failed. Check Bluetooth and the robot companion mode, then try again."
            : safeError);
        System.Diagnostics.Debug.WriteLine($"StackChan settings operation failed: {ex.GetType().Name}");
    }

    private void ShowValidationError(string key)
    {
        SetOperationMessage(key);
        RefreshSnapshot();
    }

    private void SetOperationMessage(string key, params object[] args)
    {
        _operationMessage = key;
        _operationMessageArgs = args.Length == 0 ? null : args;
    }

    private void ClearOperationMessage()
    {
        _operationMessage = null;
        _operationMessageArgs = null;
    }

    private void SelectRememberedDevice()
    {
        string remembered = _config?.StackChanDeviceAddress ?? string.Empty;
        foreach (object item in DeviceList.Items)
        {
            if (item is StackChanDevice device && string.Equals(device.AddressText, remembered, StringComparison.OrdinalIgnoreCase))
            {
                DeviceList.SelectedItem = item;
                return;
            }
        }
    }

    private void SelectInterval(int seconds)
    {
        ComboBoxItem? nearest = null;
        int nearestDistance = int.MaxValue;
        foreach (object item in UpdateIntervalBox.Items)
        {
            if (item is not ComboBoxItem { Tag: string value } option || !int.TryParse(value, out int optionSeconds)) continue;
            int distance = Math.Abs(optionSeconds - seconds);
            if (distance >= nearestDistance) continue;
            nearest = option;
            nearestDistance = distance;
        }
        UpdateIntervalBox.SelectedItem = nearest;
    }

    private void SelectConnectionMode(bool useUsb)
    {
        string requested = useUsb ? "Usb" : "Bluetooth";
        foreach (object item in ConnectionModeBox.Items)
        {
            if (item is ComboBoxItem { Tag: string mode } option &&
                string.Equals(mode, requested, StringComparison.Ordinal))
            {
                ConnectionModeBox.SelectedItem = option;
                return;
            }
        }
        ConnectionModeBox.SelectedIndex = 0;
    }

    private static string NormalizeColor(string value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return RgbPattern.IsMatch(trimmed) ? "#" + trimmed.TrimStart('#').ToUpperInvariant() : string.Empty;
    }

    private static bool TryDouble(string value, double minimum, double maximum, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && result >= minimum && result <= maximum;

    private static bool TryInt(TextBox box, int minimum, int maximum, out int result) =>
        int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) && result >= minimum && result <= maximum;

    private static bool TryFeature(TextBox x, TextBox y, TextBox rotation, TextBox weight, TextBox size, out StackChanFeature feature)
    {
        int xValue = 0, yValue = 0, rotationValue = 0, weightValue = 0, sizeValue = 0;
        bool valid = TryInt(x, -100, 100, out xValue) & TryInt(y, -100, 100, out yValue) &
                     TryInt(rotation, 0, 3600, out rotationValue) & TryInt(weight, 0, 100, out weightValue) &
                     TryInt(size, -100, 100, out sizeValue);
        feature = valid ? new StackChanFeature(xValue, yValue, rotationValue, weightValue, sizeValue) : new StackChanFeature();
        return valid;
    }

    private void OnMotionSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loading) ClearOperationMessage();
        UpdateMotionLabels();
        if (!_loading && _config != null) RefreshSnapshot();
    }

    private void UpdateMotionLabels()
    {
        if (YawText == null) return;
        YawText.Text = $"{Math.Round(YawSlider.Value):0}°";
        PitchText.Text = $"{Math.Round(PitchSlider.Value):0}°";
        SpeedText.Text = $"{Math.Round(SpeedSlider.Value):0}";
    }
}
