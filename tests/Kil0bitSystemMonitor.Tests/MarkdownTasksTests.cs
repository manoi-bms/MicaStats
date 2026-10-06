using System;
using System.Globalization;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class MarkdownTasksTests
    {
        private static readonly DateTimeOffset Morning = new(2026, 10, 6, 9, 5, 0, TimeSpan.FromHours(7));
        private static readonly DateTimeOffset Afternoon = new(2026, 10, 6, 15, 40, 0, TimeSpan.FromHours(7));

        private static string Apply(string line, TextEdit? edit)
        {
            Assert.NotNull(edit);
            var value = edit!.Value;
            return line.Substring(0, value.Offset) + value.Text + line.Substring(value.Offset + value.Length);
        }

        [Theory]
        [InlineData("- [] buy milk", true, false)]
        [InlineData("- [ ] buy milk", true, false)]
        [InlineData("* [x] buy milk", true, true)]
        [InlineData("  + [X] buy milk", true, true)]
        [InlineData("- [y] buy milk", false, false)]
        [InlineData("ordinary text", false, false)]
        public void Empty_standard_and_finished_boxes_are_recognized(string line, bool task, bool finished)
        {
            Assert.Equal(task, MarkdownTasks.IsTask(line));
            Assert.Equal(finished, MarkdownTasks.IsFinished(line));
        }

        [Theory]
        [InlineData("- [] buy milk", "buy milk")]
        [InlineData("- [ ]   buy milk  ", "buy milk")]
        [InlineData("* [x] buy milk", "buy milk")]
        [InlineData("  + [X] buy milk", "buy milk")]
        [InlineData("ordinary text", "")]
        public void Identity_is_the_trimmed_body_without_checkbox_state(string line, string expected) =>
            Assert.Equal(expected, MarkdownTasks.Identity(line));

        [Theory]
        [InlineData("- [] task", "- [x] task")]
        [InlineData("- [ ] task", "- [x] task")]
        [InlineData("- [x] task", "- [ ] task")]
        [InlineData("- [X] task", "- [ ] task")]
        public void Toggle_changes_only_the_box(string line, string expected) =>
            Assert.Equal(expected, Apply(line, MarkdownTasks.Toggle(line)));

        [Fact]
        public void Enter_continues_an_indented_task_as_plain_markdown()
        {
            const string line = "\t- [ ] first second";
            int caret = line.IndexOf(" second", StringComparison.Ordinal);

            var edit = MarkdownTasks.Continue(line, caret, "\r\n");
            string result = Apply(line, edit);

            Assert.Equal("\t- [ ] first\r\n\t- [ ] second", result);
            Assert.Equal(result.IndexOf("second", StringComparison.Ordinal), edit!.Value.SelectionStart);
            Assert.Equal(0, edit.Value.SelectionLength);
        }

        [Fact]
        public void Enter_on_a_blank_task_exits_the_list_and_keeps_its_indentation()
        {
            const string line = "    - [ ]   ";

            var edit = MarkdownTasks.Continue(line, line.Length, "\n");

            Assert.Equal("    ", Apply(line, edit));
            Assert.Equal(4, edit!.Value.SelectionStart);
        }

        [Fact]
        public void Continue_ignores_non_tasks_and_a_caret_inside_the_checkbox_prefix()
        {
            Assert.Null(MarkdownTasks.Continue("plain text", 5, "\n"));
            Assert.Null(MarkdownTasks.Continue("- [ ] task", 2, "\n"));
        }

        [Theory]
        [InlineData("- [ ] task (created: 2026-10-0)")]
        [InlineData("- [ ] task (created: 2026-13-40 25:99)")]
        [InlineData("- [ ] task (created: 2026-10-06 09:05; finished: bad)")]
        [InlineData("- [ ] task(created: 2026-10-06 09:05)")]
        [InlineData("ordinary text (created: 2026-10-06 09:05)")]
        public void Legacy_parser_rejects_incomplete_invalid_or_nonterminal_suffixes(string line) =>
            Assert.Null(MarkdownTasks.LegacyDatesOf(line, TimeSpan.FromHours(7)));

        [Fact]
        public void Legacy_parser_accepts_a_pending_task_without_a_finish_date()
        {
            const string line = "- [ ] ship it (created: 2026-10-06 09:05)";

            var legacy = MarkdownTasks.LegacyDatesOf(line, TimeSpan.FromHours(7));

            Assert.NotNull(legacy);
            Assert.Equal(Morning, legacy!.Value.Created);
            Assert.Null(legacy.Value.Finished);
        }

        [Fact]
        public void Legacy_parser_leaves_trailing_whitespace_outside_the_removal_range()
        {
            const string line = "- [x] ship it (created: 2026-10-06 09:05; finished: 2026-10-06 15:40) \t";

            var legacy = MarkdownTasks.LegacyDatesOf(line, TimeSpan.FromHours(7));

            Assert.NotNull(legacy);
            Assert.Equal(Morning, legacy!.Value.Created);
            Assert.Equal(Afternoon, legacy.Value.Finished);
            Assert.Equal("- [x] ship it \t", line.Remove(legacy.Value.Offset, legacy.Value.Length));
        }

        [Fact]
        public void Legacy_parser_returns_the_removable_suffix_and_supplied_local_offset()
        {
            const string line = "- [x] ship it (created: 2026-10-06 09:05; finished: 2026-10-06 15:40)";

            var legacy = MarkdownTasks.LegacyDatesOf(line, TimeSpan.FromHours(7));

            Assert.NotNull(legacy);
            Assert.Equal(line.IndexOf(" (created:", StringComparison.Ordinal), legacy!.Value.Offset);
            Assert.Equal(line.Length - legacy.Value.Offset, legacy.Value.Length);
            Assert.Equal(Morning, legacy.Value.Created);
            Assert.Equal(Afternoon, legacy.Value.Finished);
        }

        [Fact]
        public void Dates_are_formatted_invariantly_from_separate_task_metadata()
        {
            CultureInfo before = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
                var pending = new TaskDateRecord("task-1", 0, "ship it", Morning, null);
                var finished = pending with { Finished = Afternoon };

                Assert.Equal("(created: 2026-10-06 09:05)", MarkdownTasks.FormatDates(pending));
                Assert.Equal("(created: 2026-10-06 09:05; finished: 2026-10-06 15:40)", MarkdownTasks.FormatDates(finished));
            }
            finally
            {
                CultureInfo.CurrentCulture = before;
            }
        }
    }
}
