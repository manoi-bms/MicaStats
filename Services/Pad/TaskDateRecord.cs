using System;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// Dates tracked for one Markdown task. Stored in the note's encrypted metadata rather than
    /// in its editable Markdown text.
    /// </summary>
    public sealed record TaskDateRecord(
        string Id,
        int Offset,
        string Text,
        DateTimeOffset Created,
        DateTimeOffset? Finished);
}
