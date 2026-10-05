using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace Kil0bitSystemMonitor.Conference;

using Services.Conference;

/// <summary>Selectable conversation text with compact source/time labels and incremental updates.</summary>
public sealed class MeetingTranscriptView : RichTextBox
{
    private readonly List<object> _rows = new();
    private readonly List<Block> _blocks = new();

    public MeetingTranscriptView()
    {
        IsReadOnly = true;
        IsUndoEnabled = false;
        Document.Blocks.Clear();
        Document.PagePadding = new Thickness(0);
        Document.LineHeight = 22;
        Document.FontSize = 14;
        Document.SetResourceReference(TextElement.ForegroundProperty, "Ask.Ink");
    }

    internal string Text => new TextRange(Document.ContentStart, Document.ContentEnd).Text;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Document.PagePadding = new Thickness(0);
    }

    internal void SetTranscript(IReadOnlyList<MeetingSegment> segments, IReadOnlyList<MeetingGap> gaps)
    {
        var rows = segments.Select(segment => (segment.Start, Value: (object)segment))
            .Concat(gaps.Select(gap => (gap.Start, Value: (object)gap)))
            .OrderBy(row => row.Start).Select(row => row.Value).ToArray();
        int unchanged = 0;
        while (unchanged < rows.Length && unchanged < _rows.Count && Equals(rows[unchanged], _rows[unchanged]))
            unchanged++;
        if (unchanged == rows.Length && unchanged == _rows.Count) return;

        double offset = VerticalOffset;
        bool followLatest = Selection.IsEmpty && offset + ViewportHeight >= ExtentHeight - 8;
        BeginChange();
        try
        {
            // Preserve unchanged blocks and selections during normal appends. A late ASR result
            // or a completed listening gap rebuilds only the affected chronological tail.
            for (int i = _blocks.Count - 1; i >= unchanged; i--)
            {
                Document.Blocks.Remove(_blocks[i]);
                _blocks.RemoveAt(i);
                _rows.RemoveAt(i);
            }
            for (int i = unchanged; i < rows.Length; i++)
            {
                Block block = rows[i] is MeetingSegment segment ? CreateSegment(segment) : CreateGap((MeetingGap)rows[i]);
                Document.Blocks.Add(block);
                _blocks.Add(block);
                _rows.Add(rows[i]);
            }
        }
        finally { EndChange(); }

        if (followLatest) ScrollToEnd();
        else ScrollToVerticalOffset(offset);
    }

    private static Block CreateSegment(MeetingSegment segment)
    {
        var section = new Section { Margin = new Thickness(0, 0, 0, 12) };
        var header = new Paragraph { Margin = new Thickness(0, 0, 0, 2), FontSize = 11, LineHeight = 16 };
        var source = new Run(segment.Source == MeetingSource.Microphone ? "🎤 Microphone" : "🔊 Conference audio")
        {
            FontWeight = FontWeights.SemiBold,
            ToolTip = $"Source reference: {segment.Id}",
        };
        source.SetResourceReference(TextElement.ForegroundProperty, "Ask.Accent");
        header.Inlines.Add(source);
        header.Inlines.Add(new Run($"   {Time(segment.Start)}–{Time(segment.Start + segment.Duration)}"));
        header.SetResourceReference(TextElement.ForegroundProperty, "Ask.Muted");
        section.Blocks.Add(header);

        var comparison = segment.Comparison;
        string preferred = comparison == null ? "" : comparison.PreferredService == AsrService.Asr2 ? "ASR2: " : "ASR1: ";
        section.Blocks.Add(new Paragraph(new Run(preferred + segment.Text)) { Margin = new Thickness(0) });
        if (comparison != null)
        {
            bool agreement = comparison.Asr1Succeeded && comparison.Asr2Succeeded &&
                comparison.Asr1Text.Length > 0 && comparison.Asr2Text.Length > 0 && comparison.AlternativeText == null;
            if (agreement)
                header.Inlines.Add(new Run("   · Both ASR services agree"));
            else
            {
                var notice = new Paragraph(new Run("⚠ " + comparison.Notice))
                {
                    FontSize = 11,
                    LineHeight = 16,
                    Margin = new Thickness(0, 4, 0, 0),
                };
                notice.SetResourceReference(TextElement.ForegroundProperty, "Ask.Amber");
                section.Blocks.Add(notice);
            }
            if (comparison.AlternativeText is { } alternative)
            {
                var alternate = new Paragraph(new Run("ASR1 alternative: " + alternative))
                {
                    FontSize = 13,
                    Margin = new Thickness(0, 2, 0, 0),
                };
                alternate.SetResourceReference(TextElement.ForegroundProperty, "Ask.Muted");
                section.Blocks.Add(alternate);
            }
        }
        return section;
    }

    private static Block CreateGap(MeetingGap gap)
    {
        var paragraph = new Paragraph(new Run($"⏸ Listening gap   {Time(gap.Start)}–{(gap.End is { } end ? Time(end) : "ongoing")} · Not transcribed"))
        {
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 12),
        };
        paragraph.SetResourceReference(TextElement.ForegroundProperty, "Ask.Amber");
        return paragraph;
    }

    private static string Time(TimeSpan time) => time.ToString(time.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
}
