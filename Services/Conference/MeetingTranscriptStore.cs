using System.IO;
using System.Text;
using System.Text.Json;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Services.Conference;

/// <summary>Only received text and its source metadata; never audio or provider settings.</summary>
public sealed record MeetingTranscriptSnapshot(
    int Version, string Id, DateTimeOffset StartedAt,
    MeetingSegment[] Segments, MeetingGap[] Gaps);

/// <summary>Local meeting history using the same durable file protocol as MicaPad.</summary>
public sealed class MeetingTranscriptStore
{
    private const long MaxFileBytes = 32 * 1024 * 1024;
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicaStats", "Meetings");

    public MeetingTranscriptStore(string root) => Root = Path.GetFullPath(root);
    public string Root { get; }

    public void Save(MeetingTranscriptSnapshot snapshot)
    {
        Validate(snapshot);
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, snapshot.Id);
        AtomicFile.Write(path + ".json", JsonSerializer.SerializeToUtf8Bytes(snapshot));
        AtomicFile.Write(path + ".md", Encoding.UTF8.GetBytes(
            MeetingSession.ExportMarkdown(snapshot.Segments, snapshot.Gaps)));
    }

    public MeetingTranscriptSnapshot? LoadLatest(out bool recoveryWarning)
    {
        recoveryWarning = false;
        try
        {
            if (!Directory.Exists(Root)) return null;
            // A .ready file can be the first save of a meeting interrupted before rename.
            var candidates = Directory.EnumerateFiles(Root, "meeting-*.json*")
                .Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".json.ready", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Select(path => path.EndsWith(AtomicFile.ReadySuffix, StringComparison.OrdinalIgnoreCase)
                    ? path[..^AtomicFile.ReadySuffix.Length] : path)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var path in candidates)
            {
                try
                {
                    var ready = path + AtomicFile.ReadySuffix;
                    if (new FileInfo(File.Exists(ready) ? ready : path).Length > MaxFileBytes)
                        throw new InvalidDataException();
                    var snapshot = JsonSerializer.Deserialize<MeetingTranscriptSnapshot>(AtomicFile.ReadText(path) ?? "null");
                    if (snapshot == null) throw new InvalidDataException();
                    Validate(snapshot);
                    if (Path.GetFileNameWithoutExtension(path) != snapshot.Id) throw new InvalidDataException();
                    if (snapshot.Segments.Length > 0 || snapshot.Gaps.Length > 0) return snapshot;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                    recoveryWarning = true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            recoveryWarning = true;
        }
        return null;
    }

    private static void Validate(MeetingTranscriptSnapshot snapshot)
    {
        if (snapshot.Version != 1 || snapshot.Id == null || !snapshot.Id.StartsWith("meeting-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(snapshot.Id[8..], "N", out _) || snapshot.Segments == null || snapshot.Gaps == null ||
            snapshot.Segments.Length > 100_000 || snapshot.Gaps.Length > 100_000)
            throw new InvalidDataException("Invalid meeting recovery data.");
        long characters = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in snapshot.Segments)
        {
            if (segment == null || string.IsNullOrEmpty(segment.Id) || !ids.Add(segment.Id) || segment.Text == null ||
                !Enum.IsDefined(segment.Source) || segment.Start < TimeSpan.Zero || segment.Duration < TimeSpan.Zero ||
                segment.Start > TimeSpan.FromDays(1) || segment.Duration > TimeSpan.FromDays(1))
                throw new InvalidDataException("Invalid meeting segment.");
            characters += segment.Comparison is { } comparison
                ? (long)comparison.Asr1Text.Length + comparison.Asr2Text.Length : segment.Text.Length;
        }
        if (characters > 2_000_000 || snapshot.Gaps.Any(gap => gap == null || gap.Start < TimeSpan.Zero ||
                gap.Start > TimeSpan.FromDays(1) || gap.End < gap.Start || gap.End > TimeSpan.FromDays(1)))
            throw new InvalidDataException("Invalid meeting recovery data.");
    }
}
