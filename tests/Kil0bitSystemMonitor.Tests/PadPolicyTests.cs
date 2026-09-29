using System;
using System.Collections.Generic;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Small pure decisions MicaPad makes: titles, command lines, what a disk change means.</summary>
    public class PadPolicyTests
    {
        [Theory]
        [InlineData("Shopping list\nmilk", "Shopping list")]
        [InlineData("  \r\n\t  Meeting notes  \r\nagenda", "Meeting notes")]
        [InlineData("", "Untitled 3")]
        [InlineData(" \r\n \n ", "Untitled 3")]
        [InlineData("123456789012345678901234567890XYZ", "123456789012345678901234567890")]
        public void A_scratch_title_is_the_first_non_empty_line(string text, string expected)
        {
            Assert.Equal(expected, NoteTitle.FromText(text, 3));
        }

        [Fact]
        public void A_title_never_splits_a_surrogate_pair()
        {
            // 29 letters then an emoji (two UTF-16 units) straddling the 30-character cut.
            string text = new string('a', 29) + "\U0001F600" + "tail";

            Assert.Equal(new string('a', 29), NoteTitle.FromText(text, 1));
        }

        [Fact]
        public void A_file_title_is_the_file_name()
        {
            Assert.Equal("config.ini", NoteTitle.ForFile(@"D:\work\config.ini"));
        }

        [Theory]
        [InlineData(new[] { "--pad" }, true, null)]
        [InlineData(new[] { "--pad", @"C:\a b\x.txt" }, true, @"C:\a b\x.txt")]
        [InlineData(new[] { "--startup", "--pad" }, true, null)]
        [InlineData(new[] { "--pad", "--startup" }, true, null)]
        [InlineData(new[] { "--PAD", "notes.txt" }, true, "notes.txt")]
        [InlineData(new[] { "--startup" }, false, null)]
        [InlineData(new string[0], false, null)]
        public void Pad_arguments_are_parsed(string[] args, bool expected, string? expectedPath)
        {
            Assert.Equal(expected, PadArguments.TryParse(args, out string? path));
            Assert.Equal(expectedPath, path);
        }

        private static readonly SourceStamp Before = new(new DateTime(2026, 9, 29, 1, 0, 0, DateTimeKind.Utc), 100);
        private static readonly SourceStamp After = new(new DateTime(2026, 9, 29, 1, 5, 0, DateTimeKind.Utc), 120);

        public static IEnumerable<object?[]> DiskCases() => new[]
        {
            new object?[] { Before, Before, false, DiskChangeAction.None },
            new object?[] { Before, Before, true, DiskChangeAction.None },
            new object?[] { Before, After, false, DiskChangeAction.ReloadSilently },
            new object?[] { Before, After, true, DiskChangeAction.AskReloadOrKeep },
            new object?[] { Before, null, false, DiskChangeAction.AskSaveAsOrKeepAsNote },
            new object?[] { Before, null, true, DiskChangeAction.AskSaveAsOrKeepAsNote },
            new object?[] { null, After, true, DiskChangeAction.None },
        };

        [Theory]
        [MemberData(nameof(DiskCases))]
        public void A_disk_change_is_decided_by_the_table(SourceStamp? recorded, SourceStamp? current, bool unsaved, DiskChangeAction expected)
        {
            Assert.Equal(expected, DiskChangePolicy.Decide(recorded, current, unsaved));
        }

        [Fact]
        public void A_missing_file_has_no_stamp()
        {
            Assert.Null(SourceStamp.Read(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt")));
        }
    }
}
