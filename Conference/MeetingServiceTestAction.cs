using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services.Conference;
using Kil0bitSystemMonitor.Services.Localization;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace Kil0bitSystemMonitor;

/// <summary>Owns an explicit test of one draft address, independent of saved meeting configuration.</summary>
internal sealed class MeetingServiceTestAction : IDisposable
{
    private readonly MeetingServiceKind _kind;
    private readonly TextBox _address;
    private readonly Button _button;
    private readonly TextBlock _status;
    private readonly Func<MeetingServiceKind, string, CancellationToken, Task<string>> _test;
    private CancellationTokenSource? _operation;
    private long _revision;
    private bool _disposed;

    internal MeetingServiceTestAction(MeetingServiceKind kind, TextBox address, Button button, TextBlock status,
        Func<MeetingServiceKind, string, CancellationToken, Task<string>>? test = null)
    {
        _kind = kind;
        _address = address;
        _button = button;
        _status = status;
        _test = test ?? ((service, url, token) => MeetingServiceConnectionTest.RunAsync(service, url, token));
        _address.TextChanged += AddressChanged;
        _button.Click += TestClicked;
        RefreshButton();
    }

    private async void TestClicked(object sender, System.Windows.RoutedEventArgs e) => await RunAsync();

    private void AddressChanged(object sender, TextChangedEventArgs e)
    {
        _revision++;
        _operation?.Cancel();
            _status.Text = UiText.Tr("Not tested for this address.");
        RefreshButton();
    }

    internal async Task RunAsync()
    {
        if (_disposed) return;
        if (_operation != null) { Cancel(); return; }
        if (!MeetingServiceEndpoints.TryNormalizeBaseUrl(_address.Text, out string url))
        {
            _status.Text = UiText.Tr("Enter a valid HTTP or HTTPS API base URL to test.");
            return;
        }

        using var operation = new CancellationTokenSource();
        _operation = operation;
        long revision = _revision;
        _status.Text = _kind == MeetingServiceKind.Tts
                ? UiText.Tr("Testing speech generation…")
                : UiText.Tr("Testing transcription with generated silence…");
        RefreshButton();
        var elapsed = Stopwatch.StartNew();
        try
        {
            string result = await _test(_kind, url, operation.Token);
            if (IsCurrent(operation, revision) && !operation.IsCancellationRequested)
                _status.Text = $"{result} ({elapsed.Elapsed.TotalSeconds:0.0} s)";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (IsCurrent(operation, revision)) _status.Text = UiText.Tr("Test cancelled.");
        }
        catch (MeetingException error)
        {
            if (IsCurrent(operation, revision) && !operation.IsCancellationRequested) _status.Text = error.Message;
        }
        catch
        {
            if (IsCurrent(operation, revision) && !operation.IsCancellationRequested)
                _status.Text = UiText.Tr("Test failed. Check the service address, API path and network, then try again.");
        }
        finally
        {
            if (ReferenceEquals(_operation, operation)) _operation = null;
            if (!_disposed) RefreshButton();
        }
    }

    private bool IsCurrent(CancellationTokenSource operation, long revision) =>
        !_disposed && ReferenceEquals(_operation, operation) && _revision == revision;

    private void RefreshButton()
    {
        _button.Content = UiText.Tr(_operation == null ? "Test connection" : "Cancel test");
        ButtonIcon.SetGlyph(_button, _operation == null ? "\uE703" : "\uE711");
        string service = _kind == MeetingServiceKind.Tts ? "TTS" : _kind == MeetingServiceKind.Asr1 ? "ASR1" : "ASR2";
        System.Windows.Automation.AutomationProperties.SetName(_button,
            _operation == null ? UiText.Format("Test {0} connection", service) : UiText.Format("Cancel {0} connection test", service));
        _button.IsEnabled = !_disposed && (_operation != null
            ? !_operation.IsCancellationRequested
            : MeetingServiceEndpoints.TryNormalizeBaseUrl(_address.Text, out _));
    }

    internal void Cancel()
    {
        if (_disposed || _operation == null) return;
        _operation.Cancel();
        _status.Text = UiText.Tr("Test cancelled.");
        RefreshButton();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _revision++;
        _operation?.Cancel();
        _address.TextChanged -= AddressChanged;
        _button.Click -= TestClicked;
    }
}
