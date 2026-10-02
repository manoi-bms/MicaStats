namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>
    /// One searchable piece of a note (spec 3.1). Lines are 1-based. <see cref="SentText"/> is what
    /// the keyword index reads and what the embedding server and the reranker are sent: the title
    /// and heading path, a blank line, then <see cref="Body"/>. <see cref="Hash"/> (lowercase hex
    /// SHA-256 of <see cref="SentText"/>) keys the passage's vector.
    /// </summary>
    public sealed record Passage(
        string NoteId,
        string Title,
        string Heading,
        int FirstLine,
        int LastLine,
        string FirstLineText,
        string Body,
        string SentText,
        string Hash);
}
