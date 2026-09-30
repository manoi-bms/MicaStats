using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Ai.Mcp;
using Kil0bitSystemMonitor.Services.Ai.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

/// <summary>
/// Local HTTP mode on a free loopback port: the SDK's HTTP client for the happy path, raw
/// requests for every refusal. Nothing leaves 127.0.0.1.
/// </summary>
public class McpHttpHostTests
{
    private const string Token = "test-token-0123456789";
    private const string ListTools = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}";

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string Endpoint(int port) => "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/mcp";

    private static McpHttpHost StartHost(int port, Func<string?>? token = null)
    {
        ToolInvoker tools = (tool, args, ct) => Task.FromResult<JsonNode>(new JsonObject { ["tool"] = tool });
        var host = new McpHttpHost(port, token ?? (() => Token), () => McpToolSet.CreateOptions(tools, "1.0.0"));
        Assert.True(host.TryStart(out string? problem), problem);
        return host;
    }

    private static async Task<HttpResponseMessage> SendAsync(int port, HttpMethod method, string? bearer, string body = ListTools,
        string? host = null, string? origin = null)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(method, Endpoint(port));
        if (method != HttpMethod.Get) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (host != null) request.Headers.Host = host;
        if (origin != null) request.Headers.Add("Origin", origin);
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task An_mcp_client_with_the_token_lists_and_calls_the_tools()
    {
        int port = FreePort();
        using McpHttpHost host = StartHost(port);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(Endpoint(port)),
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + Token },
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using McpClient client = await McpClient.CreateAsync(transport, cancellationToken: cts.Token);
        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: cts.Token);
        CallToolResult result = await client.CallToolAsync(ToolNames.GetBattery, cancellationToken: cts.Token);

        Assert.True(host.IsRunning);
        Assert.Equal(ToolNames.ReadOnly.OrderBy(n => n, StringComparer.Ordinal), tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("{\"tool\":\"get_battery\"}", string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    [InlineData("")]
    public async Task A_request_without_the_right_token_gets_401(string? bearer)
    {
        int port = FreePort();
        using McpHttpHost host = StartHost(port);

        using HttpResponseMessage response = await SendAsync(port, HttpMethod.Post, bearer);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_stored_token_every_request_gets_401()
    {
        int port = FreePort();
        using McpHttpHost host = StartHost(port, token: () => null);

        using HttpResponseMessage response = await SendAsync(port, HttpMethod.Post, Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_rebinding_host_header_gets_403_even_with_the_token()
    {
        int port = FreePort();
        using McpHttpHost host = StartHost(port);

        using HttpResponseMessage response = await SendAsync(port, HttpMethod.Post, Token,
            host: "evil.example:" + port.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("http://localhost.evil.example")]
    [InlineData("null")]
    public async Task A_foreign_origin_gets_403_even_with_the_token(string origin)
    {
        int port = FreePort();
        using McpHttpHost host = StartHost(port);

        using HttpResponseMessage response = await SendAsync(port, HttpMethod.Post, Token, origin: origin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_gets_405()
    {
        int port = FreePort();
        using McpHttpHost host = StartHost(port);

        using HttpResponseMessage response = await SendAsync(port, HttpMethod.Get, Token);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task A_body_over_one_megabyte_gets_413()
    {
        int port = FreePort();
        using McpHttpHost host = StartHost(port);

        using HttpResponseMessage response = await SendAsync(port, HttpMethod.Post, Token, body: new string('x', McpHttpHost.MaxBodyBytes + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_body_that_is_not_json_rpc_gets_400()
    {
        int port = FreePort();
        using McpHttpHost host = StartHost(port);

        using HttpResponseMessage response = await SendAsync(port, HttpMethod.Post, Token, body: "not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void A_port_in_use_is_reported_and_nothing_listens()
    {
        var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        try
        {
            int port = ((IPEndPoint)holder.LocalEndpoint).Port;
            using var host = new McpHttpHost(port, () => Token, () => McpToolSet.CreateOptions((t, a, ct) => Task.FromResult<JsonNode>(new JsonObject()), "1.0.0"));

            Assert.False(host.TryStart(out string? problem));
            Assert.Equal("Port " + port.ToString(CultureInfo.InvariantCulture) + " is in use.", problem);
            Assert.False(host.IsRunning);
        }
        finally
        {
            holder.Stop();
        }
    }

    [Fact]
    public void Stop_frees_the_port_for_the_next_host()
    {
        int port = FreePort();
        McpHttpHost first = StartHost(port);

        first.Stop();

        Assert.False(first.IsRunning);
        using McpHttpHost second = StartHost(port);
        Assert.True(second.IsRunning);
        first.Dispose();
    }
}
