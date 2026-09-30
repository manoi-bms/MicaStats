using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Nodes;

namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// The running app's end of the tool pipe: answers <c>--mcp</c> bridge requests by calling a
/// <see cref="ToolInvoker"/>, up to <see cref="MaxClients"/> connections at a time.
///
/// <para>
/// The pipe admits only the current user and carries a medium integrity label (see
/// <see cref="ToolPipeNative"/>). The first instance is created with the first-instance flag,
/// so <see cref="Start"/> fails rather than share a name another process already serves, and
/// the next instance is always created before a connected one is handed off, so the name never
/// has zero instances while the server runs and cannot be taken over in between.
/// </para>
/// </summary>
public sealed class ToolPipeServer : IDisposable
{
    /// <summary>How many connections are served at once; a further client waits for a free slot.</summary>
    public const int MaxClients = 4;

    /// <summary>
    /// How long one tool may run on behalf of the pipe. Longer than the bridge's own 10 s wait,
    /// so the bridge reports the timeout; this only frees the slot of a tool that never returns.
    /// </summary>
    private static readonly TimeSpan ServerCallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a connection may sit without sending its next request before the server closes it.
    /// Without a bound, four idle connections would hold every slot and lock all callers out.
    /// </summary>
    private static readonly TimeSpan DefaultRequestWaitLimit = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;
    private readonly ToolInvoker _invoke;
    private readonly Action<string> _warn;
    private readonly string _sddl;
    private readonly object _gate = new();
    private readonly HashSet<NamedPipeServerStream> _open = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>The idle bound above; tests shorten it before <see cref="Start"/>.</summary>
    internal TimeSpan RequestWaitLimit { get; set; } = DefaultRequestWaitLimit;

    /// <summary>The pause after the first failure in a row; it doubles with each further one. Tests shorten it.</summary>
    internal TimeSpan RetryDelayFirst { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>The longest pause between retries, however long the failures go on. Tests shorten it.</summary>
    internal TimeSpan RetryDelayMax { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Tests only: runs before every pipe instance after the first is created, and may throw to fail it.</summary>
    internal Action? BeforeCreateInstance { get; set; }

    /// <summary>
    /// The pause after failure number <paramref name="failures"/> in a row (0 for the first):
    /// <paramref name="first"/>, doubled for each further failure, never above <paramref name="max"/>.
    /// </summary>
    internal static TimeSpan RetryDelay(int failures, TimeSpan first, TimeSpan max)
    {
        double ms = first.TotalMilliseconds * Math.Pow(2, Math.Clamp(failures, 0, 30));
        return ms >= max.TotalMilliseconds ? max : TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>A server for <paramref name="pipeName"/> (see <see cref="ToolPipeProtocol.DefaultPipeName"/>); nothing is created until <see cref="Start"/>.</summary>
    /// <param name="warn">Receives failure notes for the diagnostics log: never tool arguments or results.</param>
    public ToolPipeServer(string pipeName, ToolInvoker invoke, Action<string>? warn = null)
    {
        _pipeName = pipeName;
        _invoke = invoke;
        _warn = warn ?? (_ => { });
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        _sddl = ToolPipeNative.SddlFor(identity.User ?? throw new InvalidOperationException("The current Windows user has no SID."));
    }

    /// <summary>
    /// True from a successful <see cref="Start"/> until <see cref="Stop"/>: the accept loop logs
    /// and outlasts any other failure.
    /// </summary>
    public bool IsRunning
    {
        get { lock (_gate) return _loop is { IsCompleted: false }; }
    }

    /// <summary>
    /// Creates the pipe and starts accepting. Does nothing while already running. Throws
    /// <see cref="IOException"/> when the name is already served by another process (or another
    /// server) or the pipe cannot be created.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false }) return;

            // A previous run that ended without Stop leaves its token and instances behind.
            _cts?.Cancel();
            _cts = null;
            _loop = null;
            foreach (NamedPipeServerStream leftover in _open) leftover.Dispose();
            _open.Clear();

            NamedPipeServerStream first = CreateInstance(firstInstance: true);
            _open.Add(first);
            var cts = new CancellationTokenSource();
            var slots = new SemaphoreSlim(MaxClients, MaxClients);
            _cts = cts;
            _loop = Task.Run(() => AcceptLoopAsync(first, slots, cts.Token));
        }
    }

    /// <summary>Stops accepting, drops every connection and frees the pipe name. Safe to call repeatedly.</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? loop;
        NamedPipeServerStream[] open;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
            open = _open.ToArray();
            _open.Clear();
        }
        if (cts == null) return;

        cts.Cancel();
        foreach (NamedPipeServerStream pipe in open) pipe.Dispose();
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The loop logs its own failures; stopping must not throw.
        }
    }

    /// <summary>Same as <see cref="Stop"/>.</summary>
    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(NamedPipeServerStream listening, SemaphoreSlim slots, CancellationToken ct)
    {
        // The loop ends only when the server is stopped, because a dead loop frees the name for a
        // squatter. Any other failure is logged once per run of failures, with its cause, and the
        // loop carries on after a pause that grows while the failures last, so a lasting fault
        // neither spins nor fills the log. A connection handed off ends the run.
        int failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? connected = null;
                bool slotTaken = false;
                try
                {
                    try
                    {
                        await listening.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    }
                    catch (IOException) when (!ct.IsCancellationRequested)
                    {
                        // A client connected and left before it was accepted: normal, not a failure.
                        // Replace the instance, creating the new one first so the name is never
                        // left without one.
                        NamedPipeServerStream dropped = listening;
                        listening = await NewListeningAsync(ct).ConfigureAwait(false);
                        Close(dropped);
                        continue;
                    }

                    connected = listening;
                    await slots.WaitAsync(ct).ConfigureAwait(false);
                    slotTaken = true;

                    // The next instance exists before this one is served (and later closed).
                    listening = await NewListeningAsync(ct).ConfigureAwait(false);
                    NamedPipeServerStream serving = connected;
                    connected = null;
                    slotTaken = false;
                    _ = Task.Run(() => ServeAsync(serving, slots, ct));
                    failures = 0;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    if (failures == 0) _warn("The tool pipe listener failed and keeps trying: " + Describe(ex));

                    // Whatever broke, a fresh instance waits before the old one closes.
                    NamedPipeServerStream stale = listening;
                    listening = await NewListeningAsync(ct).ConfigureAwait(false);
                    Close(stale);
                    if (connected != null && !ReferenceEquals(connected, stale)) Close(connected);
                    if (slotTaken) slots.Release();

                    await Task.Delay(RetryDelay(failures, RetryDelayFirst, RetryDelayMax), ct).ConfigureAwait(false);
                    failures++;
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Stopping: Stop cancels first, then disposes the instances under the loop.
        }
        finally
        {
            Close(listening);
        }
    }

    /// <summary>
    /// Creates the next waiting instance, retrying until it works or the server stops. A run of
    /// failures is logged once, with its cause, and the pauses between tries grow up to
    /// <see cref="RetryDelayMax"/>.
    /// </summary>
    private async Task<NamedPipeServerStream> NewListeningAsync(CancellationToken ct)
    {
        for (int failures = 0; ; failures++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return Track(CreateInstance(firstInstance: false));
            }
            catch (Exception ex)
            {
                if (failures == 0) _warn("The tool pipe could not create its next instance and keeps trying: " + Describe(ex));
                await Task.Delay(RetryDelay(failures, RetryDelayFirst, RetryDelayMax), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The exception type and message for the log. Pipe failures only: never tool arguments or results.</summary>
    private static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    private async Task ServeAsync(NamedPipeServerStream pipe, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                JsonObject? request;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    idle.CancelAfter(RequestWaitLimit);
                    try
                    {
                        request = await ToolPipeProtocol.ReadAsync(pipe, idle.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        return;   // idle too long: free the slot
                    }
                    catch (InvalidDataException ex)
                    {
                        await ToolPipeProtocol.WriteAsync(pipe, Failure("The request could not be read: " + ex.Message), ct).ConfigureAwait(false);
                        return;
                    }
                }
                if (request == null) return;   // the bridge hung up

                JsonObject reply = await AnswerAsync(request, ct).ConfigureAwait(false);
                await ToolPipeProtocol.WriteAsync(pipe, reply, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Stopping, or the bridge went away mid-call: nothing to answer.
        }
        catch (Exception ex)
        {
            _warn("A tool pipe connection failed: " + ex.GetType().Name);
        }
        finally
        {
            Close(pipe);
            slots.Release();
        }
    }

    private async Task<JsonObject> AnswerAsync(JsonObject request, CancellationToken ct)
    {
        if (request["v"] is not JsonValue versionValue || !versionValue.TryGetValue(out int version) || version != ToolPipeProtocol.Version)
        {
            string sent = request["v"]?.ToJsonString() ?? "none";
            return Failure("Tool pipe version mismatch: this MicaStats speaks version " +
                ToolPipeProtocol.Version.ToString(CultureInfo.InvariantCulture) + " but the caller sent " + sent +
                ". Update MicaStats so the bridge and the running app are the same version.");
        }

        if (request["tool"] is not JsonValue toolValue || !toolValue.TryGetValue(out string? tool) || string.IsNullOrEmpty(tool))
            return Failure("The request names no tool.");

        JsonNode? argsNode = request["args"];
        if (argsNode is not null and not JsonObject) return Failure("The tool arguments must be a JSON object.");
        request.Remove("args");   // detached, so the tool may keep or re-parent it

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ServerCallTimeout);
        try
        {
            JsonNode result = await _invoke(tool, (JsonObject?)argsNode, deadline.Token).ConfigureAwait(false);
            return new JsonObject
            {
                ["v"] = ToolPipeProtocol.Version,
                ["ok"] = true,
                ["result"] = result.Parent == null ? result : result.DeepClone(),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _warn("Tool " + tool + " took too long over the tool pipe");
            return Failure("The tool " + tool + " took too long.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _warn("Tool " + tool + " failed over the tool pipe: " + ex.GetType().Name);
            return Failure(ex.Message);
        }
    }

    private static JsonObject Failure(string error) => new()
    {
        ["v"] = ToolPipeProtocol.Version,
        ["ok"] = false,
        ["error"] = error,
    };

    private NamedPipeServerStream CreateInstance(bool firstInstance)
    {
        if (!firstInstance) BeforeCreateInstance?.Invoke();
        try
        {
            // Two more instances than served clients: the one waiting for the next caller, and the
            // replacement created before a connection that dropped before it was accepted is closed.
            var handle = ToolPipeNative.Create(_pipeName, _sddl, firstInstance, MaxClients + 2);
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        catch (Win32Exception ex) when (firstInstance && ex.NativeErrorCode is ToolPipeNative.ErrorAccessDenied or ToolPipeNative.ErrorPipeBusy)
        {
            throw new IOException("The tool pipe " + _pipeName + " is already in use by another process.", ex);
        }
        catch (Win32Exception ex)
        {
            throw new IOException("The tool pipe could not be created: " + ex.Message, ex);
        }
    }

    private NamedPipeServerStream Track(NamedPipeServerStream pipe)
    {
        lock (_gate) _open.Add(pipe);
        return pipe;
    }

    private void Close(NamedPipeServerStream pipe)
    {
        lock (_gate) _open.Remove(pipe);
        pipe.Dispose();
    }
}
