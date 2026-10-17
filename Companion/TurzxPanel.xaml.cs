using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Localization;
using Kil0bitSystemMonitor.Services.Turzx;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using UserControl = System.Windows.Controls.UserControl;

namespace Kil0bitSystemMonitor.Companion;

public partial class TurzxPanel : UserControl
{
    private readonly TurzxRenderer _fallbackRenderer = new();
    private TurzxDisplayService? _service;
    private AppConfig? _config;
    private CancellationTokenSource? _panelOperation;
    private bool _loading;
    private string? _operationMessage;
    private object[]? _operationMessageArgs;

    public TurzxPanel()
    {
        InitializeComponent();
        Unloaded += (_, _) => Detach();
    }

    public void Load(TurzxDisplayService? service, AppConfig config)
    {
        Detach();
        _service = service;
        _config = config;
        ClearOperationMessage();
        _loading = true;
        try
        {
            EnabledToggle.IsOn = config.TurzxEnabled;
            AutoConnectToggle.IsOn = config.TurzxAutoConnect;
            SelectTag(OrientationBox, config.TurzxOrientation);
            SelectTag(ViewModeBox, config.TurzxViewMode);
            BrightnessSlider.Value = config.TurzxBrightness;
        }
        finally
        {
            _loading = false;
        }

        if (_service != null) _service.Changed += OnServiceChanged;
        UiText.Provider.PropertyChanged += OnLanguageChanged;
        RefreshSnapshot();
    }

    public void Detach()
    {
        CancelPanelOperation();
        if (_service != null) _service.Changed -= OnServiceChanged;
        UiText.Provider.PropertyChanged -= OnLanguageChanged;
        _service = null;
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
        TurzxSnapshot? snapshot = _service?.Snapshot;
        bool enabled = _config?.TurzxEnabled == true;
        bool busy = _panelOperation != null;
        bool connected = enabled && snapshot?.State == TurzxConnectionState.Connected;

        if (_service == null)
        {
            ConnectionStatus.Text = UiText.Tr("Turzx display is off.");
            ConnectionDetail.Text = UiText.Tr("Turn on Turzx display support, scan for USB serial ports, and connect.");
        }
        else if (!enabled)
        {
            ConnectionStatus.Text = UiText.Tr("Turzx display is off.");
            ConnectionDetail.Text = UiText.Tr("Turn on Turzx display support, scan for USB serial ports, and connect.");
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
                        ? UiText.Format("Connected to {0} on {1}. Streaming live RGB565 telemetry frames.",
                            snapshot!.DeviceName, snapshot.PortName)
                        : UiText.Tr("Scan for USB ports, select the Turzx 3.5-inch IPS display, and connect.");
        }

        DisplayControlsCard.IsEnabled = enabled;
        ScanButton.IsEnabled = enabled && !busy && _service != null &&
                               snapshot?.State is not TurzxConnectionState.Discovering and not TurzxConnectionState.Connecting;
        DeviceList.IsEnabled = ScanButton.IsEnabled;
        ConnectButton.IsEnabled = ScanButton.IsEnabled && DeviceList.SelectedItem is TurzxDevice;
        DisconnectButton.IsEnabled = enabled && snapshot?.State is TurzxConnectionState.Connected or TurzxConnectionState.Connecting;
        TestPatternButton.IsEnabled = enabled && !busy && connected;
        AutoConnectToggle.IsEnabled = enabled;
        BrightnessText.Text = $"{Math.Round(BrightnessSlider.Value):0}%";

        UpdatePreviewImage();
    }

    private void UpdatePreviewImage()
    {
        try
        {
            ushort[] pixels;
            int width;
            int height;

            if (_service != null)
            {
                (pixels, width, height) = _service.GetPreviewFrame();
            }
            else
            {
                TurzxOrientation orientation = TurzxDimensions.ParseOrientation(_config?.TurzxOrientation);
                TurzxViewMode viewMode = TurzxDimensions.ParseViewMode(_config?.TurzxViewMode);
                (width, height) = TurzxDimensions.ForOrientation(orientation);
                var demoMetrics = new SystemMetrics
                {
                    CpuUsage = 34,
                    CpuTemperature = 54,
                    CpuFrequencyGhz = 4.6f,
                    GpuUsage = 42,
                    GpuTemperature = 58,
                    RamPercent = 52,
                };
                pixels = _fallbackRenderer.Render(demoMetrics, orientation, viewMode, DateTime.Now);
            }

            byte[] bgra = TurzxRenderer.ToBgra32(pixels);
            BitmapSource bitmap = BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                bgra,
                width * 4);
            bitmap.Freeze();
            PreviewImage.Source = bitmap;
        }
        catch
        {
            // Keep previous preview image if frame conversion fails.
        }
    }

    private void OnSettingsToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _config == null) return;
        ClearOperationMessage();
        _config.TurzxEnabled = EnabledToggle.IsOn;
        _config.TurzxAutoConnect = AutoConnectToggle.IsOn;
        if (!_config.TurzxEnabled) CancelPanelOperation();
        _service?.RefreshSettings();
        RefreshSnapshot();
    }

    private void OnOrientationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _config == null || OrientationBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        ClearOperationMessage();
        _config.TurzxOrientation = tag;
        _service?.RefreshSettings();
        RefreshSnapshot();
    }

    private void OnViewModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _config == null || ViewModeBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        ClearOperationMessage();
        _config.TurzxViewMode = tag;
        _service?.RefreshSettings();
        RefreshSnapshot();
    }

    private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessText != null)
            BrightnessText.Text = $"{Math.Round(e.NewValue):0}%";
        if (_loading || _config == null) return;
        ClearOperationMessage();
        _config.TurzxBrightness = (int)Math.Round(e.NewValue);
        _service?.RefreshSettings();
    }

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ClearOperationMessage();
        RefreshSnapshot();
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        TurzxDisplayService? service = _service;
        if (service == null || _config?.TurzxEnabled != true || _panelOperation != null) return;
        CancellationTokenSource operation = BeginPanelOperation();
        DeviceList.Items.Clear();
        SetOperationMessage("Finding Turzx USB serial ports…");
        RefreshSnapshot();
        try
        {
            IReadOnlyList<TurzxDevice> devices = await service.DiscoverAsync(operation.Token);
            if (!IsCurrent(service, operation)) return;
            foreach (TurzxDevice device in devices) DeviceList.Items.Add(device);
            SelectRememberedDevice();
            if (DeviceList.SelectedIndex < 0 && DeviceList.Items.Count > 0) DeviceList.SelectedIndex = 0;
            int count = DeviceList.Items.Count;
            if (count == 0)
                SetOperationMessage("No Turzx USB display found. Check the USB-C cable and try again.");
            else
                SetOperationMessage("Found {0} Turzx USB port(s). Select one and connect.", count);
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
        TurzxDisplayService? service = _service;
        if (service == null || _config?.TurzxEnabled != true || _panelOperation != null ||
            DeviceList.SelectedItem is not TurzxDevice device) return;
        CancellationTokenSource operation = BeginPanelOperation();
        try
        {
            await service.ConnectAsync(device, operation.Token);
            if (IsCurrent(service, operation) && _config != null)
            {
                _config.TurzxPortName = device.PortName;
                _config.TurzxDeviceName = device.Name;
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

    private async void OnDisconnect(object sender, RoutedEventArgs e)
    {
        TurzxDisplayService? service = _service;
        if (service == null) return;
        ClearOperationMessage();
        CancelPanelOperation();
        try { await service.DisconnectAsync(); }
        catch (Exception ex) { if (ReferenceEquals(_service, service)) ShowOperationError(service, ex); }
        if (ReferenceEquals(_service, service)) RefreshSnapshot();
    }

    private async void OnTestPattern(object sender, RoutedEventArgs e)
    {
        TurzxDisplayService? service = _service;
        if (service == null || _config?.TurzxEnabled != true || _panelOperation != null) return;
        CancellationTokenSource operation = BeginPanelOperation();
        try
        {
            await service.SendTestPatternAsync(operation.Token);
            if (IsCurrent(service, operation))
                SetOperationMessage("Sent calibration test pattern to the Turzx display.");
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

    private CancellationTokenSource BeginPanelOperation()
    {
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
        CancellationTokenSource? operation = _panelOperation;
        _panelOperation = null;
        if (operation == null) return;
        operation.Cancel();
        operation.Dispose();
    }

    private bool IsCurrent(TurzxDisplayService service, CancellationTokenSource operation) =>
        ReferenceEquals(_service, service) && ReferenceEquals(_panelOperation, operation) && _config != null;

    private void ShowOperationError(TurzxDisplayService service, Exception ex)
    {
        string? safeError = service.Snapshot.Error;
        SetOperationMessage(string.IsNullOrWhiteSpace(safeError)
            ? "The Turzx USB display operation failed. Check the USB cable and port, then try again."
            : safeError);
        System.Diagnostics.Debug.WriteLine($"Turzx settings operation failed: {ex.GetType().Name}");
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
        string remembered = _config?.TurzxPortName ?? string.Empty;
        foreach (object item in DeviceList.Items)
        {
            if (item is TurzxDevice device && string.Equals(device.PortName, remembered, StringComparison.OrdinalIgnoreCase))
            {
                DeviceList.SelectedItem = item;
                return;
            }
        }
    }

    private static void SelectTag(System.Windows.Controls.ComboBox box, string? requested)
    {
        foreach (object item in box.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } option &&
                string.Equals(tag, requested, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = option;
                return;
            }
        }
        if (box.Items.Count > 0)
            box.SelectedIndex = 0;
    }
}
