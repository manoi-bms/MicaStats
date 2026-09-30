using System;
using System.Linq;
using System.Windows;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Marking every occurrence of the selected word.</summary>
    public class OccurrenceTests
    {
        [Theory]
        [InlineData("cat cat category cat", 0, 3, true)]
        [InlineData("cat cat category cat", 8, 3, false)]     // "cat" inside "category"
        [InlineData("cat", 0, 2, false)]                       // part of a word
        [InlineData("a b", 0, 3, false)]                       // not one word
        [InlineData("snake_case x", 0, 10, true)]
        [InlineData("x", 0, 0, false)]
        public void Only_exactly_one_whole_word_counts(string text, int start, int length, bool expected)
        {
            Assert.Equal(expected, OccurrenceFinder.IsWholeWordSelection(text, start, length));
        }

        [Theory]
        [InlineData("ที่ นี่", 0, 3, true)]
        [InlineData("ที่", 0, 1, false)]
        public void Thai_marks_belong_to_the_word(string text, int start, int length, bool expected)
        {
            Assert.Equal(expected, OccurrenceFinder.IsWholeWordSelection(text, start, length));
        }

        [Fact]
        public void An_edit_clears_the_marks_at_once() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "cat cat";
            window.Editor.Select(0, 3);
            window.RefreshOccurrences();
            Assert.Equal(2, window.OccurrenceMarks.Offsets.Count);

            window.Editor.Document.Insert(0, "x ");
            Assert.Empty(window.OccurrenceMarks.Offsets);
            Assert.Equal(Visibility.Collapsed, window.OccurrenceText.Visibility);

            window.Editor.Select(2, 3);
            window.RefreshOccurrences();
            Assert.Equal(new[] { 2, 6 }, window.OccurrenceMarks.Offsets);
        });

        [Fact]
        public void Occurrences_are_whole_words_and_case_sensitive()
        {
            var (offsets, capped) = OccurrenceFinder.FindAll("cat Cat cat_x cat cats cat", "cat");
            Assert.Equal(new[] { 0, 14, 23 }, offsets);
            Assert.False(capped);
        }

        [Fact]
        public void Counting_stops_at_ten_thousand()
        {
            string text = string.Join(" ", Enumerable.Repeat("w", OccurrenceFinder.Cap + 5));
            var (offsets, capped) = OccurrenceFinder.FindAll(text, "w");
            Assert.Equal(OccurrenceFinder.Cap, offsets.Count);
            Assert.True(capped);
        }

        [Theory]
        [InlineData(1, false, "1 match")]
        [InlineData(5, false, "5 matches")]
        [InlineData(1234, false, "1,234 matches")]
        [InlineData(10000, true, "10,000+ matches")]
        public void Status_text(int count, bool capped, string expected)
        {
            Assert.Equal(expected, OccurrenceFinder.Describe(count, capped));
        }

        [Fact]
        public void Selecting_a_word_marks_it_and_counts_in_the_status_bar() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "cat cat category cat";
            window.Editor.Select(0, 3);
            window.RefreshOccurrences();

            Assert.Equal(Visibility.Visible, window.OccurrenceText.Visibility);
            Assert.Equal("3 matches", window.OccurrenceText.Text);
            Assert.Equal(new[] { 0, 4, 17 }, window.OccurrenceMarks.Offsets);

            window.Editor.Select(8, 3);        // inside "category": not a whole word
            window.RefreshOccurrences();
            Assert.Equal(Visibility.Collapsed, window.OccurrenceText.Visibility);
            Assert.Empty(window.OccurrenceMarks.Offsets);
        });

        [Fact]
        public void A_note_over_the_size_limit_marks_nothing() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.Editor.Document.Text = "cat " + new string('x', PadLanguages.MaxFormattedChars);
            window.Editor.Select(0, 3);
            window.RefreshOccurrences();
            Assert.Empty(window.OccurrenceMarks.Offsets);
            Assert.Equal(Visibility.Collapsed, window.OccurrenceText.Visibility);
        });

        [Fact]
        public void The_marks_follow_the_theme() => PadLanguageWindowTests.WithWindow((window, env, config) =>
        {
            window.ToggleTheme();
            var fill = Assert.IsAssignableFrom<System.Windows.Media.SolidColorBrush>(window.OccurrenceMarks.Fill).Color;
            var expected = PadPalette.Light.Occurrence;
            Assert.Equal(System.Windows.Media.Color.FromArgb(expected.A, expected.R, expected.G, expected.B), fill);
        });
    }
}
