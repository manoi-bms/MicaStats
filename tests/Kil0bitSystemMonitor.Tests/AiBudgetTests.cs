using System;
using System.Collections.Generic;
using Kil0bitSystemMonitor.Services.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The limits that follow a model's context window, and today's limits when it is not known.</summary>
    public class AiBudgetTests
    {
        [Fact]
        public void Standard_is_exactly_todays_eleven_numbers()
        {
            AiBudget s = AiBudget.Standard;

            Assert.Equal(0, s.ContextTokens);
            Assert.Equal(2000, s.AskOutputTokens);
            Assert.Equal(4096, s.PadOutputTokens);
            Assert.Equal(8000, s.RewriteInput);
            Assert.Equal(24000, s.ReadInput);
            Assert.Equal(64000, s.PadReplyChars);
            Assert.Equal(48000, s.HistoryTokens);
            Assert.Equal(0, s.NoteReadTokens);
            Assert.Equal(400, s.NoteReadLines);
            Assert.Equal(0, s.KeptResultTokens);
            Assert.Equal(8, s.NotesSources);
            Assert.False(s.InTokens);
        }

        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(500, 0, 0)]
        [InlineData(0, 1023, 0)]
        [InlineData(1000, 1000, 0)]
        public void With_no_window_the_budget_is_Standard(int reported, int output, int user) =>
            Assert.Same(AiBudget.Standard, AiBudget.For(reported, output, user));

        [Fact]
        public void A_reported_output_with_no_window_changes_nothing()
        {
            Assert.Same(AiBudget.Standard, AiBudget.For(0, 8192, 0));
        }

        // Window, then the eleven numbers in the record's order.
        public static IEnumerable<object[]> Columns() => new[]
        {
            //            W        ask    pad    rewrite read     reply   history note   lines kept   passages
            new object[] { 8_192,     2000, 2048,  1638,   2048,    64000,  4096,   4000,  400,  5000,  8 },
            new object[] { 32_000,    4000, 8000,  6400,   8000,    64000,  16000,  4000,  1000, 5000,  12 },
            new object[] { 128_000,   16000, 32000, 25600, 32000,   128000, 64000,  8000,  2000, 10000, 20 },
            new object[] { 262_144,   16000, 32000, 25600, 99072,   128000, 131072, 16384, 2000, 20480, 20 },
            new object[] { 1_048_576, 16000, 32000, 25600, 492288,  128000, 524288, 64000, 4000, 80000, 20 },
        };

        [Theory]
        [MemberData(nameof(Columns))]
        public void The_budget_at_a_window_follows_the_table(int window, int ask, int pad, int rewrite, int read,
            int reply, int history, int note, int lines, int kept, int passages)
        {
            AiBudget b = AiBudget.For(window, 0, 0);

            Assert.Equal(window, b.ContextTokens);
            Assert.True(b.InTokens);
            Assert.Equal(ask, b.AskOutputTokens);
            Assert.Equal(pad, b.PadOutputTokens);
            Assert.Equal(rewrite, b.RewriteInput);
            Assert.Equal(read, b.ReadInput);
            Assert.Equal(reply, b.PadReplyChars);
            Assert.Equal(history, b.HistoryTokens);
            Assert.Equal(note, b.NoteReadTokens);
            Assert.Equal(lines, b.NoteReadLines);
            Assert.Equal(kept, b.KeptResultTokens);
            Assert.Equal(passages, b.NotesSources);
        }

        [Fact]
        public void The_user_s_number_alone_is_the_window()
        {
            Assert.Equal(AiBudget.For(262_144, 0, 0), AiBudget.For(0, 0, 262_144));
        }

        [Fact]
        public void A_reported_output_caps_both_outputs_and_the_rewrite_that_follows_from_it()
        {
            AiBudget b = AiBudget.For(262_144, 8192, 0);

            Assert.Equal(8192, b.AskOutputTokens);
            Assert.Equal(8192, b.PadOutputTokens);
            Assert.Equal(6553, b.RewriteInput);                 // 8192 * 4 / 5
            Assert.Equal(131_072 - 8192, b.ReadInput);          // the read input follows the capped output
            Assert.Equal(64000, b.PadReplyChars);               // 4 * 8192 is under the floor
        }

        [Fact]
        public void A_reported_output_never_raises_an_output()
        {
            AiBudget b = AiBudget.For(262_144, 100_000, 0);

            Assert.Equal(16000, b.AskOutputTokens);
            Assert.Equal(32000, b.PadOutputTokens);
        }

        [Fact]
        public void A_reported_output_may_take_an_output_below_its_floor()
        {
            AiBudget b = AiBudget.For(8192, 1000, 0);

            Assert.Equal(1000, b.AskOutputTokens);
            Assert.Equal(1000, b.PadOutputTokens);
            Assert.Equal(800, b.RewriteInput);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(1)]
        [InlineData(4)]
        [InlineData(255)]
        public void A_reported_output_under_256_counts_as_not_reported(int output)
        {
            // 1 to 4 would otherwise make the rewrite input zero.
            Assert.Equal(AiBudget.For(8192, 0, 0), AiBudget.For(8192, output, 0));
            Assert.Equal(AiBudget.For(262_144, 0, 0), AiBudget.For(262_144, output, 0));
            Assert.True(AiBudget.For(8192, output, 0).RewriteInput > 0);
        }

        [Fact]
        public void A_reported_output_of_256_is_the_smallest_that_caps()
        {
            AiBudget b = AiBudget.For(8192, 256, 0);

            Assert.Equal(256, b.AskOutputTokens);
            Assert.Equal(256, b.PadOutputTokens);
            Assert.Equal(204, b.RewriteInput);                  // 256 * 4 / 5
        }

        [Fact]
        public void The_read_input_has_a_floor_of_a_thousand()
        {
            // 2,048 / 2 = 1,024 less a 2,048 output would be negative.
            Assert.Equal(1000, AiBudget.For(2048, 0, 0).ReadInput);
            Assert.Equal(1000, AiBudget.For(1024, 0, 0).ReadInput);
        }

        [Fact]
        public void The_passages_and_the_lines_step_up_at_their_windows()
        {
            Assert.Equal(8, AiBudget.For(31_999, 0, 0).NotesSources);
            Assert.Equal(12, AiBudget.For(32_000, 0, 0).NotesSources);
            Assert.Equal(12, AiBudget.For(127_999, 0, 0).NotesSources);
            Assert.Equal(20, AiBudget.For(128_000, 0, 0).NotesSources);

            Assert.Equal(400, AiBudget.For(31_999, 0, 0).NoteReadLines);
            Assert.Equal(1000, AiBudget.For(32_000, 0, 0).NoteReadLines);
            Assert.Equal(1000, AiBudget.For(127_999, 0, 0).NoteReadLines);
            Assert.Equal(2000, AiBudget.For(128_000, 0, 0).NoteReadLines);
            Assert.Equal(2000, AiBudget.For(999_999, 0, 0).NoteReadLines);
            Assert.Equal(4000, AiBudget.For(1_000_000, 0, 0).NoteReadLines);
        }

        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(262_144, 0, 262_144)]
        [InlineData(0, 100_000, 100_000)]
        [InlineData(200_000, 100_000, 100_000)]   // the smaller of the two
        [InlineData(100_000, 200_000, 100_000)]
        [InlineData(100_000, 100_000, 100_000)]
        public void The_window_in_use_is_the_smaller_of_what_is_given(int reported, int user, int expected) =>
            Assert.Equal(expected, AiBudget.WindowInUse(reported, user));

        [Theory]
        [InlineData(1023, 0, 0)]
        [InlineData(0, 1023, 0)]
        [InlineData(-5, -9, 0)]
        [InlineData(1024, 0, 1024)]
        [InlineData(0, 1024, 1024)]
        [InlineData(500, 50_000, 50_000)]          // a number under 1,024 counts as none
        [InlineData(50_000, 500, 50_000)]
        [InlineData(500, 600, 0)]
        [InlineData(2_000_001, 0, 2_000_000)]
        [InlineData(0, int.MaxValue, 2_000_000)]
        [InlineData(int.MaxValue, int.MaxValue, 2_000_000)]
        [InlineData(int.MaxValue, 5000, 5000)]
        public void A_window_under_1024_is_none_and_one_over_two_million_is_two_million(int reported, int user, int expected) =>
            Assert.Equal(expected, AiBudget.WindowInUse(reported, user));

        [Fact]
        public void A_window_that_does_not_count_gives_Standard_whichever_source_it_came_from()
        {
            Assert.Same(AiBudget.Standard, AiBudget.For(100, 0, 100));
            Assert.Equal(2_000_000, AiBudget.For(int.MaxValue, 0, 0).ContextTokens);
        }

        [Fact]
        public void No_number_passes_its_ceiling_for_any_window_and_none_goes_negative()
        {
            var windows = new SortedSet<long> { 0, 1, int.MaxValue, int.MaxValue - 1 };
            for (int p = 0; p < 31; p++)
                foreach (long d in new long[] { -1, 0, 1 })
                    windows.Add((1L << p) + d);
            foreach (int w in new[] { 1000, 1023, 1024, 31_999, 32_000, 127_999, 128_000, 999_999, 1_000_000, 2_000_000, 2_000_001 })
                windows.Add(w);

            foreach (long w in windows)
            {
                if (w < 0 || w > int.MaxValue) continue;
                foreach (int output in new[] { 0, 5, 1000, 8192, 100_000, int.MaxValue })
                {
                    AiBudget b = AiBudget.For((int)w, output, 0);
                    string at = " at " + w + " / " + output;

                    Assert.True(b.ContextTokens >= 0 && b.ContextTokens <= 2_000_000, "window" + at);
                    Assert.InRange(b.AskOutputTokens, 1, 16_000);
                    Assert.InRange(b.PadOutputTokens, 1, 32_000);
                    Assert.InRange(b.RewriteInput, 1, 25_600);
                    Assert.InRange(b.ReadInput, 1000, 1_000_000);
                    Assert.InRange(b.PadReplyChars, 64_000, 128_000);
                    Assert.InRange(b.HistoryTokens, 0, 1_000_000);
                    Assert.InRange(b.NoteReadTokens, b.InTokens ? 4000 : 0, 64_000);
                    Assert.InRange(b.NoteReadLines, 400, 4000);
                    Assert.InRange(b.KeptResultTokens, 0, 80_000);
                    Assert.InRange(b.NotesSources, 8, 20);
                }
            }
        }
    }
}
