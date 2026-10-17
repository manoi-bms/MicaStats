namespace Kil0bitSystemMonitor.Pad
{
    public sealed class AiToolReviewRow(string id, string title, string detail, string glyph)
    {
        public string Id { get; } = id;
        public string Title { get; } = title;
        public string Detail { get; } = detail;
        public string Glyph { get; } = glyph;
        public bool Selected { get; set; } = true;
    }
}
