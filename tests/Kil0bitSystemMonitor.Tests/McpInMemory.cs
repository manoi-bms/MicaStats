using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kil0bitSystemMonitor.Tests;

/// <summary>
/// An MCP server and the SDK's own client joined by two in-memory pipes: the real protocol,
/// with no process and no stdio.
/// </summary>
internal sealed class McpInMemory : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
    private McpServer? _server;
    private Task? _run;

    private McpInMemory()
    {
    }

    /// <summary>The connected client.</summary>
    public McpClient Client { get; private set; } = null!;

    /// <summary>Starts a server with <paramref name="options"/> and connects a client to it.</summary>
    public static async Task<McpInMemory> ConnectAsync(McpServerOptions options)
    {
        var session = new McpInMemory();
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var serverTransport = new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), "test");
        session._server = McpServer.Create(serverTransport, options);
        session._run = session._server.RunAsync(session._cts.Token);
        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(), serverOutput: serverToClient.Reader.AsStream());
        session.Client = await McpClient.CreateAsync(clientTransport, cancellationToken: session._cts.Token);
        return session;
    }

    /// <summary>Calls a tool and returns its text content.</summary>
    public async Task<string> CallTextAsync(string tool, Dictionary<string, object?>? args = null)
    {
        CallToolResult result = await Client.CallToolAsync(tool, args, cancellationToken: _cts.Token);
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
    }

    /// <summary>Disconnects the client and stops the server.</summary>
    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        _cts.Cancel();
        try
        {
            if (_run != null) await _run;
        }
        catch (OperationCanceledException)
        {
        }
        if (_server != null) await _server.DisposeAsync();
        _cts.Dispose();
    }
}
