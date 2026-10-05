using System;
using System.Text;

namespace Kil0bitSystemMonitor.Services.Conference;

public sealed record MeetingAsrComparison
{
    public MeetingAsrComparison(
        string asr2Text,
        string asr1Text,
        bool asr2Succeeded,
        bool asr1Succeeded)
    {
        Asr2Succeeded = asr2Succeeded;
        Asr1Succeeded = asr1Succeeded;
        Asr2Text = asr2Succeeded ? (asr2Text ?? string.Empty).Trim() : string.Empty;
        Asr1Text = asr1Succeeded ? (asr1Text ?? string.Empty).Trim() : string.Empty;
    }

    public string Asr2Text { get; }
    public string Asr1Text { get; }
    public bool Asr2Succeeded { get; }
    public bool Asr1Succeeded { get; }

    public AsrService PreferredService =>
        Asr2Text.Length > 0 || Asr1Text.Length == 0 ? AsrService.Asr2 : AsrService.Asr1;

    public string? AlternativeText =>
        Asr2Text.Length > 0 && Asr1Text.Length > 0 && !ReadingsMatch(Asr2Text, Asr1Text)
            ? Asr1Text
            : null;

    public string Notice
    {
        get
        {
            if (!Asr2Succeeded && !Asr1Succeeded)
                return "Both services were unavailable.";
            if (!Asr2Succeeded)
                return Asr1Text.Length > 0
                    ? "ASR2 was unavailable; showing the ASR1 reading."
                    : "ASR2 was unavailable; ASR1 returned no speech.";
            if (!Asr1Succeeded)
                return Asr2Text.Length > 0
                    ? "ASR1 was unavailable; showing the ASR2 reading."
                    : "ASR1 was unavailable; ASR2 returned no speech.";
            if (Asr2Text.Length == 0)
                return Asr1Text.Length == 0
                    ? "Both services returned no speech."
                    : "ASR2 returned no speech; showing the ASR1 reading.";
            if (Asr1Text.Length == 0)
                return "ASR1 returned no speech; showing the ASR2 reading.";
            return ReadingsMatch(Asr2Text, Asr1Text)
                ? "ASR2 and ASR1 produced the same reading."
                : "ASR2 and ASR1 produced different readings.";
        }
    }

    internal static bool ReadingsMatch(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string text)
    {
        text ??= string.Empty;
        string formC;
        try
        {
            formC = text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Provider text is data. Preserve malformed UTF-16 conservatively instead of
            // letting comparison metadata discard an otherwise usable transcript segment.
            formC = text;
        }
        var normalized = new StringBuilder(formC.Length);
        var pendingSpace = false;
        foreach (var value in formC)
        {
            if (char.IsWhiteSpace(value))
            {
                pendingSpace = normalized.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                normalized.Append(' ');
                pendingSpace = false;
            }
            normalized.Append(value);
        }
        return normalized.ToString();
    }
}
