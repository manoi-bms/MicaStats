using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using Kil0bitSystemMonitor.Services.Pad;
using Microsoft.Web.WebView2.Core;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// The hidden browser page that draws Mermaid, Graphviz and Markmap and turns any SVG into a
    /// PNG (spec 2.1-2.3, R5). It lives on a popup window that is never shown, with the WebView2
    /// controller hidden, so it is in no window's visual tree. Only
    /// https://micapad-diagrams.invalid/ (the app's Diagrams folder) loads: the page's
    /// Content-Security-Policy and this class's request filter (403) each refuse everything else.
    /// WebView2 runs in its own processes, so MicaStats' software drawing is unaffected. Use it on
    /// the UI thread.
    /// </summary>
    internal sealed class DiagramPage : IDiagramPage
    {
        internal const string HostName = "micapad-diagrams.invalid";
        internal const string PageUrl = "https://" + HostName + "/render.html";
        private const int PageWidth = 1600;
        private const int PageHeight = 1200;
        private static readonly TimeSpan ReadyLimit = TimeSpan.FromSeconds(15);

        private readonly CoreWebView2Environment _environment;
        private readonly HwndSource _host;
        private readonly CoreWebView2Controller _controller;
        private readonly CoreWebView2 _core;
        private readonly Dictionary<int, TaskCompletionSource<PageDrawing>> _pending = new();
        private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _nextId;
        private bool _disposed;

        private DiagramPage(CoreWebView2Environment environment, HwndSource host, CoreWebView2Controller controller)
        {
            _environment = environment;
            _host = host;
            _controller = controller;
            _core = controller.CoreWebView2;
        }

        /// <summary>%LOCALAPPDATA%\MicaStats\WebView2: the browser's own files (spec 2.1).</summary>
        public static string DefaultUserDataFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicaStats", "WebView2");

        /// <summary>The bundled scripts, copied beside MicaStats.exe.</summary>
        public static string ScriptsFolder => Path.Combine(AppContext.BaseDirectory, "Diagrams");

        public bool IsBroken { get; private set; }

        /// <summary>How many requests the filter refused; for tests.</summary>
        internal int RefusedRequests { get; private set; }

        /// <summary>Starts the browser, loads the page and waits until its scripts are ready (about a second).</summary>
        /// <exception cref="DiagramRuntimeMissingException">The WebView2 Runtime is not installed.</exception>
        public static async Task<IDiagramPage> CreateAsync(string userDataFolder, string scriptsFolder)
        {
            CoreWebView2Environment environment;
            try
            {
                // Crash dumps stay on this PC: a dump of the drawing process can hold note text.
                environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder,
                    new CoreWebView2EnvironmentOptions { IsCustomCrashReportingEnabled = true });
            }
            catch (WebView2RuntimeNotFoundException ex)
            {
                throw new DiagramRuntimeMissingException(ex);
            }

            var host = new HwndSource(new HwndSourceParameters("MicaPad diagrams")
            {
                WindowStyle = unchecked((int)0x80000000),   // WS_POPUP, never WS_VISIBLE
                ExtendedWindowStyle = 0x08000080,           // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW: no taskbar button
                Width = PageWidth,
                Height = PageHeight,
            });
            CoreWebView2Controller controller;
            try
            {
                controller = await environment.CreateCoreWebView2ControllerAsync(host.Handle);
            }
            catch
            {
                host.Dispose();
                throw;
            }

            var page = new DiagramPage(environment, host, controller);
            try
            {
                await page.LoadAsync(scriptsFolder);
            }
            catch
            {
                page.Dispose();
                throw;
            }
            return page;
        }

        private async Task LoadAsync(string scriptsFolder)
        {
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, PageWidth, PageHeight);
            _controller.IsVisible = false;
            _controller.DefaultBackgroundColor = System.Drawing.Color.Transparent;

            var settings = _core.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            try
            {
                settings.IsReputationCheckingRequired = false;   // no SmartScreen lookups: the page loads only its own files
            }
            catch (NotImplementedException)
            {
                // A runtime older than the setting: its SmartScreen checks nothing the 403 filter lets through.
            }

            _core.SetVirtualHostNameToFolderMapping(HostName, scriptsFolder, CoreWebView2HostResourceAccessKind.Deny);
            _core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            _core.WebResourceRequested += OnResourceRequested;
            _core.WebMessageReceived += OnMessage;
            _core.ProcessFailed += OnProcessFailed;
            _core.NewWindowRequested += OnNewWindow;
            _core.Navigate(PageUrl);

            if (await Task.WhenAny(_ready.Task, Task.Delay(ReadyLimit)) != _ready.Task)
                throw new TimeoutException("The diagram page did not load.");
        }

        public Task<PageDrawing> DrawAsync(PageRequest request, CancellationToken cancel)
        {
            if (_disposed || IsBroken) return Task.FromResult(new PageDrawing(null, null, 0, 0, DiagramText.EngineStopped));

            int id = ++_nextId;
            var done = new TaskCompletionSource<PageDrawing>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pending) _pending[id] = done;
            cancel.Register(() =>
            {
                lock (_pending) _pending.Remove(id);
                done.TrySetCanceled(cancel);
            });

            _core.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                id,
                kind = request.Kind,
                source = request.Source,
                dark = request.Dark,
                fg = request.Foreground,
                bg = request.Background,
                image = request.Image,
            }));
            return done.Task;
        }

        /// <summary>Runs a script in the page and returns its value as DevTools JSON; tests use it to show the page has no network.</summary>
        internal Task<string> EvaluateForTestAsync(string expression) =>
            _core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true }));

        private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && string.Equals(uri.Host, HostName, StringComparison.OrdinalIgnoreCase)) return;

            RefusedRequests++;
            e.Response = _environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
        }

        private void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;

        private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!e.Source.StartsWith("https://" + HostName + "/", StringComparison.OrdinalIgnoreCase)) return;

            int id;
            PageDrawing drawing;
            try
            {
                using var json = JsonDocument.Parse(e.WebMessageAsJson);
                var root = json.RootElement;
                if (root.TryGetProperty("ready", out _))
                {
                    _ready.TrySetResult(true);
                    return;
                }
                id = root.GetProperty("id").GetInt32();
                drawing = root.GetProperty("ok").GetBoolean()
                    ? new PageDrawing(root.GetProperty("svg").GetString(),
                                      Convert.FromBase64String(root.GetProperty("png").GetString() ?? ""),
                                      root.GetProperty("width").GetDouble(),
                                      root.GetProperty("height").GetDouble(),
                                      null)
                    : new PageDrawing(null, null, 0, 0, root.GetProperty("error").GetString() ?? DiagramText.Failed);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                return;   // a malformed answer: the draw it was for runs into the time limit
            }

            TaskCompletionSource<PageDrawing>? done;
            lock (_pending)
            {
                if (!_pending.Remove(id, out done)) return;
            }
            done.TrySetResult(drawing);
        }

        private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
        {
            IsBroken = true;
            FailPending();
        }

        private void FailPending()
        {
            List<TaskCompletionSource<PageDrawing>> left;
            lock (_pending)
            {
                left = new List<TaskCompletionSource<PageDrawing>>(_pending.Values);
                _pending.Clear();
            }
            foreach (var done in left) done.TrySetResult(new PageDrawing(null, null, 0, 0, DiagramText.EngineStopped));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            FailPending();
            try
            {
                _core.WebResourceRequested -= OnResourceRequested;
                _core.WebMessageReceived -= OnMessage;
                _core.ProcessFailed -= OnProcessFailed;
                _core.NewWindowRequested -= OnNewWindow;
                _controller.Close();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // The browser process is already gone.
            }
            _host.Dispose();
        }
    }
}
