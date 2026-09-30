using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;

namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// Nothing answered on the tool pipe: MicaStats is not running, is not serving the pipe (MCP
/// is not set to the stdio bridge), or every slot stayed busy for the whole connect wait. The
/// bridge answers from files on disk instead.
/// </summary>
public sealed class ToolPipeUnavailableException : Exception
{
    /// <summary>Creates the exception with a sentence for the diagnostics log.</summary>
    public ToolPipeUnavailableException(string message) : base(message)
    {
    }
}

/// <summary>
/// The bridge's end of the tool pipe: one connection per call, so a MicaStats restart between
/// two calls costs nothing.
/// </summary>
public static class ToolPipeClient
{
    /// <summary>How long to wait for the pipe to accept the connection before calling MicaStats absent.</summary>
    private const int ConnectTimeoutMs = 1000;

    /// <summary>
    /// Runs <paramref name="tool"/> in the running MicaStats and returns its result, detached from
    /// the reply. Throws <see cref="ToolPipeUnavailableException"/> when nothing accepts the
    /// connection within 1 s; <see cref="TimeoutException"/> when no reply arrives within
    /// <paramref name="timeout"/>; <see cref="InvalidOperationException"/> carrying the server's
    /// sentence for <c>ok:false</c>, a version mismatch, or a broken or unreadable reply; and
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// </summary>
    public static async Task<JsonNode> CallAsync(string pipeName, string tool, JsonObject? args, TimeSpan timeout, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMs, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ToolPipeUnavailableException("Nothing answered on the MicaStats tool pipe.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new ToolPipeUnavailableException("The MicaStats tool pipe refused this user.");
        }
        catch (IOException ex)
        {
            throw new ToolPipeUnavailableException("The MicaStats tool pipe could not be opened: " + ex.Message);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        JsonObject? reply;
        try
        {
            await ToolPipeProtocol.WriteAsync(pipe, new JsonObject
            {
                ["v"] = ToolPipeProtocol.Version,
                ["tool"] = tool,
                ["args"] = args?.DeepClone(),
            }, deadline.Token).ConfigureAwait(false);
            reply = await ToolPipeProtocol.ReadAsync(pipe, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("MicaStats did not answer within " +
                timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s.");
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("The connection to MicaStats was lost before it answered.", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException("MicaStats sent a reply that could not be read.", ex);
        }

        if (reply == null) throw new InvalidOperationException("MicaStats closed the tool pipe without answering.");

        if (reply["v"] is not JsonValue versionValue || !versionValue.TryGetValue(out int version) || version != ToolPipeProtocol.Version)
            throw new InvalidOperationException("Tool pipe version mismatch: this bridge speaks version " +
                ToolPipeProtocol.Version.ToString(CultureInfo.InvariantCulture) + " but MicaStats answered with " +
                (reply["v"]?.ToJsonString() ?? "none") + ". Update MicaStats so the bridge and the running app are the same version.");

        bool ok = reply["ok"] is JsonValue okValue && okValue.TryGetValue(out bool okFlag) && okFlag;
        if (!ok)
        {
            string error = reply["error"] is JsonValue errorValue && errorValue.TryGetValue(out string? text) && !string.IsNullOrEmpty(text)
                ? text
                : "MicaStats reported an error without a message.";
            throw new InvalidOperationException(error);
        }

        JsonNode? result = reply["result"];
        if (result == null) throw new InvalidOperationException("MicaStats answered without a result.");
        reply.Remove("result");
        return result;
    }
}
