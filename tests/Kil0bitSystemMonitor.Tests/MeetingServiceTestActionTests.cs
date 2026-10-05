using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Conference;
using Xunit;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using TextBox = System.Windows.Controls.TextBox;

namespace Kil0bitSystemMonitor.Tests;

public class MeetingServiceTestActionTests
{
    [Fact]
    public Task Click_tests_only_the_current_draft_and_does_not_save_it() => UiThread.RunAsync(async () =>
    {
        var config = new AppConfig { MeetingAsr2BaseUrl = "https://saved.example/v1" };
        var address = new TextBox();
        var button = new Button();
        var status = new TextBlock();
        int calls = 0;
        using var action = new MeetingServiceTestAction(MeetingServiceKind.Asr2, address, button, status, (kind, url, token) =>
        {
            calls++;
            Assert.Equal(MeetingServiceKind.Asr2, kind);
            Assert.Equal("https://draft.example/custom/v1/", url);
            return Task.FromResult("Test succeeded.");
        });

        Assert.False(button.IsEnabled);
        await action.RunAsync();
        Assert.Equal(0, calls);
        address.Text = " https://draft.example/custom/v1 ";
        Assert.True(button.IsEnabled);
        Assert.Equal("Not tested for this address.", status.Text);
        Assert.Equal(0, calls);
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(1, calls);
        Assert.StartsWith("Test succeeded.", status.Text);
        Assert.Equal("Test connection", button.Content);
        Assert.Equal("https://saved.example/v1", config.MeetingAsr2BaseUrl);
    });

    [Fact]
    public Task A_second_click_cancels_without_a_second_request_or_late_success() => UiThread.RunAsync(async () =>
    {
        var address = new TextBox { Text = "https://tts.example/v1" };
        var button = new Button();
        var status = new TextBlock();
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;
        int calls = 0;
        using var action = new MeetingServiceTestAction(MeetingServiceKind.Tts, address, button, status, (_, _, token) =>
        {
            calls++;
            requestToken = token;
            return reply.Task;
        });

        Task pending = action.RunAsync();
        Assert.Equal("Cancel test", button.Content);
        Assert.Contains("speech generation", status.Text);
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.True(requestToken.IsCancellationRequested);
        Assert.False(button.IsEnabled);
        Assert.Equal(1, calls);
        reply.SetResult("Late success must not be shown.");
        await pending;
        Assert.Equal("Test cancelled.", status.Text);
        Assert.True(button.IsEnabled);
        Assert.Equal("Test connection", button.Content);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Editing_the_address_cancels_and_rejects_old_results(bool fail) => UiThread.RunAsync(async () =>
    {
        var address = new TextBox { Text = "https://old.example/v1" };
        var button = new Button();
        var status = new TextBlock();
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;
        int calls = 0;
        using var action = new MeetingServiceTestAction(MeetingServiceKind.Asr1, address, button, status, (_, url, token) =>
        {
            calls++;
            if (calls == 1) { requestToken = token; return reply.Task; }
            Assert.Equal("https://new.example/v1/", url);
            return Task.FromResult("New service succeeded.");
        });

        Task pending = action.RunAsync();
        address.Text = "https://new.example/v1";
        Assert.True(requestToken.IsCancellationRequested);
        if (fail) reply.SetException(new MeetingException("Old service failed."));
        else reply.SetResult("Old service succeeded.");
        await pending;
        Assert.Equal("Not tested for this address.", status.Text);
        await action.RunAsync();
        Assert.StartsWith("New service succeeded.", status.Text);
        address.Text = "";
        Assert.False(button.IsEnabled);
        Assert.DoesNotContain("succeeded", status.Text);
    });

    [Fact]
    public Task Closing_the_owner_cancels_detaches_and_ignores_late_results() => UiThread.RunAsync(async () =>
    {
        var address = new TextBox { Text = "https://asr.example/v1" };
        var button = new Button();
        var status = new TextBlock();
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;
        int calls = 0;
        using var action = new MeetingServiceTestAction(MeetingServiceKind.Asr2, address, button, status, (_, _, token) =>
        {
            calls++;
            requestToken = token;
            return reply.Task;
        });

        Task pending = action.RunAsync();
        string beforeClose = status.Text;
        action.Dispose();
        Assert.True(requestToken.IsCancellationRequested);
        reply.SetResult("Late success.");
        await pending;
        address.Text = "https://edited.example/v1";
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(1, calls);
        Assert.Equal(beforeClose, status.Text);
    });

    [Fact]
    public void Loading_a_saved_address_reports_untested_without_claiming_an_edit() => UiThread.Run(() =>
    {
        var address = new TextBox();
        var button = new Button();
        var status = new TextBlock();
        int calls = 0;
        using var action = new MeetingServiceTestAction(MeetingServiceKind.Asr2, address, button, status,
            (_, _, _) => { calls++; return Task.FromResult("Unexpected request."); });

        address.Text = "https://saved.example/v1";
        Assert.Equal("Not tested for this address.", status.Text);
        Assert.True(button.IsEnabled);
        Assert.Equal(0, calls);
    });

    [Fact]
    public Task Unexpected_failure_is_sanitized_and_the_button_can_retry() => UiThread.RunAsync(async () =>
    {
        var address = new TextBox { Text = "https://asr.example/v1" };
        var button = new Button();
        var status = new TextBlock();
        using var action = new MeetingServiceTestAction(MeetingServiceKind.Asr2, address, button, status,
            (_, _, _) => throw new InvalidOperationException("secret response at https://asr.example/v1"));

        await action.RunAsync();
        Assert.StartsWith("Test failed.", status.Text);
        Assert.DoesNotContain("secret", status.Text);
        Assert.DoesNotContain("asr.example", status.Text);
        Assert.True(button.IsEnabled);
    });
}
