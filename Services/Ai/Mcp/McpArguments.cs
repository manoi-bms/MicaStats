namespace Kil0bitSystemMonitor.Services.Ai.Mcp;

/// <summary>
/// Recognises <c>MicaStats.exe --mcp</c>, the command line Claude Desktop and Claude Code use to
/// start the stdio bridge.
///
/// <para>
/// Looser than <see cref="Kil0bitSystemMonitor.Services.KillArguments"/> on purpose: the bridge
/// grants nothing (it serves read-only data to the same user), so the flag is found anywhere on
/// the line and in any case, and a client that adds its own arguments still reaches it.
/// </para>
/// </summary>
public static class McpArguments
{
    /// <summary>The switch that selects the stdio bridge.</summary>
    public const string Flag = "--mcp";

    /// <summary>True when any argument is <see cref="Flag"/> (case-insensitive).</summary>
    public static bool TryParse(IReadOnlyList<string> args)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, Flag, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
