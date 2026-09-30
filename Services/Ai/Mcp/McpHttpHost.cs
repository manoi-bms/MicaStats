using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// Local HTTP mode: MCP (streamable HTTP, stateless) at <c>http://127.0.0.1:&lt;port&gt;/mcp</c>
/// on <see cref="HttpListener"/>, so no ASP.NET Core runtime is needed.
///
/// <para>
/// http.sys delivers every request that reaches 127.0.0.1:port to this listener whatever its
/// Host header says, so the checks here are the whole defence: loopback callers only, a Host
/// and (when sent) an Origin of 127.0.0.1 or localhost, against DNS rebinding from a web page
/// (403), then the bearer token (401), POST only (405) and bodies up to
/// <see cref="MaxBodyBytes"/> (413). Each POST gets a fresh stateless server, so nothing is
/// kept between requests.
/// </para>
/// </summary>
public sealed class McpHttpHost : IDisposable
{
    /// <summary>The largest request body read (1 MB); a larger one is refused with 413.</summary>
    public const int MaxBodyBytes = 1_048_576;

    private const int ErrorSharingViolation = 32;   // a socket already holds the port
    private const int ErrorAlreadyExists = 183;     // another http.sys listener has the prefix

    private readonly int _port;
    private readonly Func<string?> _token;
    private readonly Func<McpServerOptions> _options;
    private readonly Action<string> _warn;
    private readonly object _gate = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>A host for <paramref name="port"/>; nothing listens until <see cref="TryStart"/>.</summary>
    /// <param name="port">The loopback port to listen on.</param>
    /// <param name="token">
    /// The bearer token, asked on every request so a token regenerated in Settings applies at
    /// once. Null or empty refuses every request.
    /// </param>
    /// <param name="options">Server options for one request; see <see cref="McpToolSet.CreateOptions"/>.</param>
    /// <param name="warn">Receives failure notes for the diagnostics log: never request bodies, results or the token.</param>
    public McpHttpHost(int port, Func<string?> token, Func<McpServerOptions> options, Action<string>? warn = null)
    {
        _port = port;
        _token = token;
        _options = options;
        _warn = warn ?? (_ => { });
    }

    /// <summary>True between a successful <see cref="TryStart"/> and <see cref="Stop"/>.</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _listener is { IsListening: true }; }
    }

    /// <summary>
    /// Starts listening. False, with a sentence for Settings in <paramref name="problem"/>, when
    /// the port is taken ("Port 47831 is in use.") or listening fails otherwise. True (and no
    /// problem) when already running.
    /// </summary>
    public bool TryStart(out string? problem)
    {
        lock (_gate)
        {
            problem = null;
            if (_listener != null) return true;

            string portText = _port.ToString(CultureInfo.InvariantCulture);
            var listener = new HttpListener();
            try
            {
                // 127.0.0.1, not localhost: a localhost prefix makes http.sys listen on every interface.
                listener.Prefixes.Add("http://127.0.0.1:" + portText + "/mcp/");
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                listener.Close();
                problem = ex.ErrorCode is ErrorSharingViolation or ErrorAlreadyExists
                    ? "Port " + portText + " is in use."
                    : "Local HTTP could not start on port " + portText + ": " + ex.Message;
                return false;
            }
            catch (ArgumentException)
            {
                listener.Close();
                problem = "Port " + portText + " is not a valid port.";
                return false;
            }

            var cts = new CancellationTokenSource();
            _listener = listener;
            _cts = cts;
            _loop = Task.Run(() => AcceptLoopAsync(listener, cts.Token));
            return true;
        }
    }

    /// <summary>Stops listening and frees the port. Safe to call repeatedly; <see cref="TryStart"/> may follow.</summary>
    public void Stop()
    {
        HttpListener? listener;
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            listener = _listener;
            cts = _cts;
            loop = _loop;
            _listener = null;
            _cts = null;
            _loop = null;
        }
        if (listener == null) return;

        // Not disposed: requests still finishing hold its token.
        cts?.Cancel();
        try { listener.Stop(); } catch (ObjectDisposedException) { }
        listener.Close();
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

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested || !listener.IsListening) return;   // Stop closed the listener
                _warn("Local HTTP MCP could not accept a request: " + ex.GetType().Name);
                continue;
            }
            _ = Task.Run(() => HandleAsync(context, ct));
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;
        try
        {
            if (!IsLocalCaller(request))
            {
                response.StatusCode = 403;
                return;
            }
            if (!HasToken(request.Headers["Authorization"]))
            {
                response.StatusCode = 401;
                response.AddHeader("WWW-Authenticate", "Bearer");
                return;
            }
            if (request.HttpMethod != "POST")
            {
                // No standalone server-to-client stream and no sessions to delete: clients accept 405.
                response.StatusCode = 405;
                response.AddHeader("Allow", "POST");
                return;
            }

            byte[]? body = await ReadBodyAsync(request.InputStream, ct).ConfigureAwait(false);
            if (body == null)
            {
                response.StatusCode = 413;
                return;
            }

            JsonRpcMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<JsonRpcMessage>(body, McpJsonUtilities.DefaultOptions);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                message = null;
            }
            if (message == null)
            {
                response.StatusCode = 400;
                return;
            }

            await ServeAsync(message, response, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) _warn("A local HTTP MCP request failed: " + ex.GetType().Name);
            try { response.StatusCode = 500; } catch (InvalidOperationException) { }   // too late once the body started
        }
        finally
        {
            try { response.Close(); } catch (Exception) { }   // the caller may be gone already
        }
    }

    /// <summary>Loopback caller, a Host of 127.0.0.1 or localhost on this port, and no foreign Origin.</summary>
    private bool IsLocalCaller(HttpListenerRequest request)
    {
        if (request.RemoteEndPoint is not { } remote || !IPAddress.IsLoopback(remote.Address)) return false;

        // Url is built from the Host header, which a rebinding page controls.
        Uri? url = request.Url;
        if (url == null || url.Port != _port || !IsLoopbackName(url.Host)) return false;

        string? origin = request.Headers["Origin"];
        if (origin == null) return true;   // not a browser, or a same-origin request
        return Uri.TryCreate(origin, UriKind.Absolute, out Uri? originUri) &&
               (originUri.Scheme == Uri.UriSchemeHttp || originUri.Scheme == Uri.UriSchemeHttps) &&
               IsLoopbackName(originUri.Host);
    }

    private static bool IsLoopbackName(string host) =>
        host == "127.0.0.1" || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>A <c>Bearer</c> header equal to the current token, compared in constant time.</summary>
    private bool HasToken(string? authorization)
    {
        const string Scheme = "Bearer ";
        if (authorization == null || !authorization.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return false;

        string? expected;
        try
        {
            expected = _token();
        }
        catch (Exception ex)
        {
            _warn("The local HTTP MCP token could not be read: " + ex.GetType().Name);
            return false;
        }
        if (string.IsNullOrEmpty(expected)) return false;

        byte[] given = Encoding.UTF8.GetBytes(authorization.Substring(Scheme.Length).Trim());
        return CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>
    /// The whole body, or null once it passes <see cref="MaxBodyBytes"/>. Reads rather than
    /// trusting Content-Length, which a chunked request does not send.
    /// </summary>
    private static async Task<byte[]?> ReadBodyAsync(Stream input, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > MaxBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }
    }

    /// <summary>One stateless MCP exchange: a fresh transport and server for this request only.</summary>
    private async Task ServeAsync(JsonRpcMessage message, HttpListenerResponse response, CancellationToken ct)
    {
        var transport = new StreamableHttpServerTransport { Stateless = true };
        McpServer server = McpServer.Create(transport, _options());
        try
        {
            _ = server.RunAsync(ct);
            bool wrote = await transport.HandlePostRequestAsync(message, response.OutputStream, _ =>
            {
                // Called before the first byte of the reply, while headers can still change.
                response.ContentType = "text/event-stream";
                response.Headers["Cache-Control"] = "no-cache";
                return ValueTask.CompletedTask;
            }, ct).ConfigureAwait(false);

            // A notification or a response from the client: accepted, nothing to send back.
            if (!wrote) response.StatusCode = 202;
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}
