using System.Buffers.Binary;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Ai.Mcp;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

/// <summary>The tool pipe frame: 4-byte little-endian length, then UTF-8 JSON, nothing over 1 MB.</summary>
public class McpPipeProtocolTests
{
    private static MemoryStream Raw(int declaredLength, byte[] body)
    {
        var stream = new MemoryStream();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, declaredLength);
        stream.Write(header);
        stream.Write(body);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task A_message_is_written_as_a_little_endian_length_and_utf8_json()
    {
        using var stream = new MemoryStream();

        await ToolPipeProtocol.WriteAsync(stream, new JsonObject { ["v"] = 1, ["tool"] = "get_history" }, CancellationToken.None);

        byte[] bytes = stream.ToArray();
        string json = "{\"v\":1,\"tool\":\"get_history\"}";
        Assert.Equal(json.Length, BinaryPrimitives.ReadInt32LittleEndian(bytes));
        Assert.Equal(json, Encoding.UTF8.GetString(bytes, 4, bytes.Length - 4));
    }

    [Fact]
    public async Task Frames_round_trip_in_order_and_the_end_of_the_stream_reads_as_null()
    {
        using var stream = new MemoryStream();
        await ToolPipeProtocol.WriteAsync(stream, new JsonObject { ["n"] = 1, ["name"] = "\u0E17\u0E14\u0E2A\u0E2D\u0E1A.exe" }, CancellationToken.None);
        await ToolPipeProtocol.WriteAsync(stream, new JsonObject { ["n"] = 2 }, CancellationToken.None);
        stream.Position = 0;

        JsonObject? first = await ToolPipeProtocol.ReadAsync(stream, CancellationToken.None);
        JsonObject? second = await ToolPipeProtocol.ReadAsync(stream, CancellationToken.None);
        JsonObject? end = await ToolPipeProtocol.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(1, (int?)first!["n"]);
        Assert.Equal("\u0E17\u0E14\u0E2A\u0E2D\u0E1A.exe", (string?)first["name"]);
        Assert.Equal(2, (int?)second!["n"]);
        Assert.Null(end);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ToolPipeProtocol.MaxFrameBytes + 1)]
    public async Task A_frame_length_outside_the_limits_is_refused_before_reading_the_body(int declared)
    {
        using MemoryStream stream = Raw(declared, Array.Empty<byte>());

        await Assert.ThrowsAsync<InvalidDataException>(() => ToolPipeProtocol.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task A_cut_header_is_refused()
    {
        using var stream = new MemoryStream(new byte[] { 5, 0 });

        await Assert.ThrowsAsync<InvalidDataException>(() => ToolPipeProtocol.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task A_cut_body_is_refused()
    {
        using MemoryStream stream = Raw(20, Encoding.UTF8.GetBytes("{\"v\":1}"));

        await Assert.ThrowsAsync<InvalidDataException>(() => ToolPipeProtocol.ReadAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    public async Task A_body_that_is_not_a_json_object_is_refused(string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        using MemoryStream stream = Raw(bytes.Length, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => ToolPipeProtocol.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task A_message_over_one_megabyte_is_refused_and_nothing_is_written()
    {
        using var stream = new MemoryStream();
        var huge = new JsonObject { ["text"] = new string('x', ToolPipeProtocol.MaxFrameBytes) };

        await Assert.ThrowsAsync<InvalidDataException>(() => ToolPipeProtocol.WriteAsync(stream, huge, CancellationToken.None));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void The_default_pipe_name_carries_the_current_user_sid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();

        Assert.Equal("MicaStats.Tools." + identity.User!.Value, ToolPipeProtocol.DefaultPipeName());
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"args\":{\"x\":1,\"x\":2}}")]
    [InlineData("{\"list\":[{\"k\":1,\"k\":2}]}")]
    public async Task A_body_with_duplicate_keys_is_refused_as_invalid_data(string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        using MemoryStream stream = Raw(bytes.Length, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => ToolPipeProtocol.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task A_body_that_is_not_valid_utf8_is_refused_as_invalid_data()
    {
        byte[] bytes = { (byte)'{', (byte)'"', (byte)'a', (byte)'"', (byte)':', (byte)'"', 0xFF, 0xFE, (byte)'"', (byte)'}' };
        using MemoryStream stream = Raw(bytes.Length, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => ToolPipeProtocol.ReadAsync(stream, CancellationToken.None));
    }
}
