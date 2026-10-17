using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad.Ai
{
    /// <summary>A safe description of a possible credential. The plaintext value is deliberately absent.</summary>
    public sealed record PadAiCredentialCandidate(string Id, int Line, string Label, string Reason);

    internal sealed record PasswordCandidateMatch(
        string Id,
        int Line,
        string Label,
        string Reason,
        string Value)
    {
        public override string ToString() => $"{Id} line {Line}: {Label} ({Reason})";
    }

    /// <summary>Conservative, local-only discovery of explicitly labelled credential values.</summary>
    public static class PasswordCandidates
    {
        internal const int MaxCandidates = 32;
        private const int MinLength = 4;
        private const int MaxLength = 256;

        private static readonly Regex Assignment = new(
            @"(?:^|[\s{,;])(?<q>['""]?)(?<label>password|passwd|pwd|รหัสผ่าน|รหัส|secret|api[ _-]?key|token)\k<q>\s*[:=]\s*(?<value>""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex BasicAuth = new(
            @"https?://[^\s:/@]+:(?<value>[^\s/@]+)@(?<host>[A-Za-z0-9.-]+)(?::[0-9]{1,5})?(?:[/\s]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
        {
            "password", "password123", "passwd", "changeme", "change-me", "example", "example123",
            "your_password", "your-password", "your password", "replace_me", "replace-me", "redacted",
            "secret", "token", "api_key", "apikey", "none", "null", "test", "xxxxx", "********",
        };

        /// <summary>Finds safe candidate descriptions for a review UI. Plaintext values are never returned.</summary>
        public static IReadOnlyList<PadAiCredentialCandidate> Find(string? text) =>
            ScanAll(text).Take(MaxCandidates).Select(candidate => new PadAiCredentialCandidate(
                candidate.Id, candidate.Line, candidate.Label, candidate.Reason)).ToArray();

        /// <summary>All matches are retained internally so values beyond the review cap are still masked.</summary>
        internal static IReadOnlyList<PasswordCandidateMatch> ScanAll(string? text)
        {
            string source = text ?? "";
            string[] lines = Regex.Split(source, "\r\n|\n|\r");
            var result = new List<PasswordCandidateMatch>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                foreach (Match assignment in Assignment.Matches(line))
                {
                    string value = Unquote(assignment.Groups["value"].Value.Trim());
                    Add(result, seen, i + 1, NormalizeLabel(assignment.Groups["label"].Value),
                        "Explicitly labelled credential value", value);
                }

                foreach (Match url in BasicAuth.Matches(line))
                {
                    Add(result, seen, i + 1, "URL password", "Password in URL basic authentication",
                        url.Groups["value"].Value);
                }
            }
            return result;
        }

        private static void Add(List<PasswordCandidateMatch> result, HashSet<string> seen, int line,
                                string label, string reason, string value)
        {
            if (!Reasonable(value) || !seen.Add(value)) return;
            result.Add(new PasswordCandidateMatch(
                "credential-" + (result.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                line,
                Clip(label, 40),
                reason,
                value));
        }

        private static bool Reasonable(string value)
        {
            if (value.Length < MinLength || value.Length > MaxLength || string.IsNullOrWhiteSpace(value)) return false;
            if (value.Contains("{{secret:", StringComparison.OrdinalIgnoreCase)
                || value.Contains("[[CREDENTIAL_", StringComparison.OrdinalIgnoreCase)
                || value.Contains("[[LOCAL_SECRET_", StringComparison.OrdinalIgnoreCase)) return false;
            string trimmed = value.Trim();
            if ((trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith('}'))
                || (trimmed.StartsWith('%') && trimmed.EndsWith('%'))
                || (trimmed.StartsWith('<') && trimmed.EndsWith('>'))
                || trimmed is "..." or "…") return false;
            if (Placeholders.Contains(trimmed)) return false;
            if (value.All(c => c is '*' or '•' or 'x' or 'X' or '-' or '_')) return false;
            return !value.Any(char.IsControl);
        }

        private static string Unquote(string value)
        {
            if (value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"')
                return value[1..^1];
            return value;
        }

        private static string NormalizeLabel(string label) =>
            Regex.Replace(label.Trim(), "[ _-]+", " ").ToLowerInvariant();

        private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
    }
}
