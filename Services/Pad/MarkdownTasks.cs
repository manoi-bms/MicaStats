using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>Pure Markdown checkbox syntax and task-date presentation helpers.</summary>
    public static class MarkdownTasks
    {
        public const string DateFormat = "yyyy-MM-dd HH:mm";
        private static readonly Regex TaskRx = new(@"^[ \t]*[-*+][ \t]+(?<box>\[[ xX]?\])(?=[ \t]|$)[ \t]?", RegexOptions.CultureInvariant);
        private static readonly Regex LegacyDatesRx = new(@" (?<dates>\(created: (?<created>\d{4}-\d{2}-\d{2} \d{2}:\d{2})(?:; finished: (?<finished>\d{4}-\d{2}-\d{2} \d{2}:\d{2}))?\))(?=[ \t]*$)", RegexOptions.CultureInvariant);

        public static bool IsTask(string line) => TaskRx.IsMatch(line);
        public static bool IsFinished(string line) => TaskRx.Match(line).Groups["box"].Value is "[x]" or "[X]";

        /// <summary>The trimmed task body, independent of checkbox state; empty for a non-task line.</summary>
        public static string Identity(string line)
        {
            var task = TaskRx.Match(line);
            return task.Success ? line.Substring(task.Length).Trim() : "";
        }

        public static TextEdit? Toggle(string line)
        {
            var task = TaskRx.Match(line);
            if (!task.Success) return null;
            var box = task.Groups["box"];
            return new TextEdit(box.Index, box.Length, IsFinished(line) ? "[ ]" : "[x]", box.Index, 0);
        }

        /// <summary>Continues a task using Markdown text only; an empty item exits the list.</summary>
        public static TextEdit? Continue(string line, int caretOffset, string newline)
        {
            var task = TaskRx.Match(line);
            if (!task.Success || caretOffset < task.Length) return null;

            string body = line.Substring(task.Length);
            string indent = line.Substring(0, line.Length - line.TrimStart(' ', '\t').Length);
            if (string.IsNullOrWhiteSpace(body))
                return new TextEdit(0, line.Length, indent, indent.Length, 0);

            int split = Math.Clamp(caretOffset - task.Length, 0, body.Length);
            string left = line.Substring(0, task.Length) + body.Substring(0, split);
            string nextPrefix = indent + "- [ ] ";
            string right = nextPrefix + body.Substring(split).TrimStart(' ', '\t');
            string replacement = left + newline + right;
            return new TextEdit(0, line.Length, replacement, left.Length + newline.Length + nextPrefix.Length, 0);
        }

        /// <summary>Formats separately stored dates for read-only presentation.</summary>
        public static string FormatDates(TaskDateRecord task)
        {
            string result = "(created: " + task.Created.ToString(DateFormat, CultureInfo.InvariantCulture);
            if (task.Finished is { } finished)
                result += "; finished: " + finished.ToString(DateFormat, CultureInfo.InvariantCulture);
            return result + ")";
        }

        /// <summary>Recognizes the exact terminal suffix written by the former inline-date implementation.</summary>
        public static LegacyTaskDates? LegacyDatesOf(string line, TimeSpan offset)
        {
            if (!IsTask(line)) return null;
            var match = LegacyDatesRx.Match(line);
            if (!match.Success
                || !TryDate(match.Groups["created"].Value, offset, out var created))
                return null;

            DateTimeOffset? finished = null;
            if (match.Groups["finished"].Success)
            {
                if (!TryDate(match.Groups["finished"].Value, offset, out var parsedFinished))
                    return null;
                finished = parsedFinished;
            }

            return new LegacyTaskDates(match.Index, match.Length, created, finished);
        }

        /// <summary>
        /// Removes valid suffixes written by the former inline-date feature from ordinary task
        /// lines. Markdown examples and structural content are left byte-for-byte alone.
        /// </summary>
        internal static string RemoveLegacyDateSuffixes(string text, out int removed)
        {
            text ??= "";
            var lines = new List<string>();
            var starts = new List<int>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] is not ('\r' or '\n')) continue;
                starts.Add(start);
                lines.Add(text.Substring(start, i - start));
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
            starts.Add(start);
            lines.Add(text.Substring(start));

            MdLineFacts[] facts = MarkdownStructure.Scan(lines).Facts;
            StringBuilder? result = null;
            removed = 0;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                MdLineFacts fact = facts[i];
                if (fact.Fence != MdFence.None || fact.FrontMatter || fact.Table != MdTableRole.None) continue;
                if (LegacyDatesOf(lines[i], TimeSpan.Zero) is not { } dates) continue;

                result ??= new StringBuilder(text);
                result.Remove(starts[i] + dates.Offset, dates.Length);
                removed++;
            }
            return result?.ToString() ?? text;
        }

        private static bool TryDate(string value, TimeSpan offset, out DateTimeOffset result)
        {
            result = default;
            if (!DateTime.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return false;
            try
            {
                result = new DateTimeOffset(parsed, offset);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    public readonly record struct LegacyTaskDates(
        int Offset,
        int Length,
        DateTimeOffset Created,
        DateTimeOffset? Finished);
}
