using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// The wire format between the <c>--mcp</c> bridge and the running MicaStats: each message is a
/// 4-byte little-endian length followed by that many bytes of UTF-8 JSON.
///
/// <para>
/// A length prefix rather than one JSON object per line, so a reader always knows how much to
/// wait for and can refuse an oversized frame before allocating it. A request is
/// <c>{"v":1,"tool":"...","args":{...}}</c>; a reply is <c>{"v":1,"ok":true,"result":...}</c> or
/// <c>{"v":1,"ok":false,"error":"..."}</c>.
/// </para>
/// </summary>
public static class ToolPipeProtocol
{
    /// <summary>
    /// The protocol version both ends put in <c>"v"</c>. A bridge from one MicaStats version talking
    /// to a running app from another is refused with a clear error instead of guessing.
    /// </summary>
    public const int Version = 1;

    /// <summary>The largest frame either end writes or accepts (1 MB), so a bad peer cannot make the other allocate without limit.</summary>
    public const int MaxFrameBytes = 1_048_576;

    /// <summary>
    /// The pipe name for the current Windows user: <c>MicaStats.Tools.</c> plus the user's SID.
    /// The SID keeps two signed-in users apart (fast user switching, a terminal server); the
    /// pipe's security descriptor is what actually keeps everyone else out.
    /// </summary>
    public static string DefaultPipeName()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return "MicaStats.Tools." + (identity.User?.Value ?? "anonymous");
    }

    /// <summary>Writes one frame. Throws <see cref="InvalidDataException"/> (and writes nothing) when the message is over <see cref="MaxFrameBytes"/>.</summary>
    public static async Task WriteAsync(Stream stream, JsonObject message, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
        if (body.Length > MaxFrameBytes)
            throw new InvalidDataException("The message is " + body.Length.ToString(CultureInfo.InvariantCulture) +
                " bytes, over the tool pipe limit of " + MaxFrameBytes.ToString(CultureInfo.InvariantCulture) + " bytes.");

        // Header and body in one write, so a reader never sees a header without its body behind it.
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one frame. Null when the stream ends cleanly before a new frame (the peer hung up);
    /// <see cref="InvalidDataException"/> for a frame that is cut short, empty, over
    /// <see cref="MaxFrameBytes"/>, not JSON, or not a JSON object.
    /// </summary>
    public static async Task<JsonObject?> ReadAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        int got = 0;
        while (got < header.Length)
        {
            int read = await stream.ReadAsync(header.AsMemory(got), ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (got == 0) return null;
                throw new InvalidDataException("The tool pipe frame header was cut short.");
            }
            got += read;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameBytes)
            throw new InvalidDataException("The tool pipe frame length " + length.ToString(CultureInfo.InvariantCulture) + " is not allowed.");

        byte[] body = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("The tool pipe frame was cut short.", ex);
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The tool pipe frame is not JSON.", ex);
        }
        return node as JsonObject ?? throw new InvalidDataException("The tool pipe frame is not a JSON object.");
    }
}
