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
            new object[] { 8_192,     2000, 2048,  1638,   2048,    64000,  4096,   2048,  400,  2560,  8 },
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
        public void The_read_input_has_a_floor_of_a_thousand_or_a_quarter_of_a_small_window()
        {
            Assert.Equal(1000, AiBudget.For(4096, 0, 0).ReadInput);   // 2,048 less a 2,048 output is 0
            Assert.Equal(1000, AiBudget.For(4000, 0, 0).ReadInput);   // a quarter is 1,000
            Assert.Equal(999, AiBudget.For(3996, 0, 0).ReadInput);    // a quarter is 999: the floor gives way
            Assert.Equal(256, AiBudget.For(1024, 0, 0).ReadInput);
        }

        [Fact]
        public void The_read_input_leaves_its_floor_at_6098()
        {
            // W / 2 less the 2,048 output is 1,000 at 6,096 and 6,097 (6,097 / 2 rounds down): the floor and the rule agree.
            Assert.Equal(1000, AiBudget.For(6095, 0, 0).ReadInput);   // 3,047 - 2,048 = 999, the floor wins
            Assert.Equal(1000, AiBudget.For(6096, 0, 0).ReadInput);
            Assert.Equal(1000, AiBudget.For(6097, 0, 0).ReadInput);
            Assert.Equal(1001, AiBudget.For(6098, 0, 0).ReadInput);   // 3,049 - 2,048
        }

        [Fact]
        public void The_smallest_window_by_hand()
        {
            AiBudget b = AiBudget.For(1024, 0, 0);

            Assert.Equal(1024, b.ContextTokens);
            Assert.Equal(256, b.AskOutputTokens);      // 2,000 at least, but never over 1,024 / 4
            Assert.Equal(512, b.PadOutputTokens);      // 2,048 at least, but never over 1,024 / 2
            Assert.Equal(409, b.RewriteInput);         // 512 * 4 / 5
            Assert.Equal(256, b.ReadInput);            // 512 - 512 = 0; the floor is the smaller of 1,000 and 256
            Assert.Equal(64000, b.PadReplyChars);
            Assert.Equal(512, b.HistoryTokens);
            Assert.Equal(256, b.NoteReadTokens);       // 4,000 at least, but never over 256
            Assert.Equal(400, b.NoteReadLines);
            Assert.Equal(320, b.KeptResultTokens);     // 256 + 64
            Assert.Equal(8, b.NotesSources);
        }

        [Fact]
        public void A_window_over_two_million_is_two_million_in_WindowInUse_and_so_in_For()
        {
            // WindowInUse does the clamp; For takes its answer.
            Assert.Equal(2_000_000, AiBudget.WindowInUse(2_000_001, 0));
            Assert.Equal(2_000_000, AiBudget.WindowInUse(0, 2_000_001));
            Assert.Equal(2_000_000, AiBudget.WindowInUse(int.MaxValue, int.MaxValue));
            Assert.Equal(2_000_000, AiBudget.For(2_000_001, 0, 0).ContextTokens);
            Assert.Equal(AiBudget.For(2_000_000, 0, 0), AiBudget.For(int.MaxValue, 0, 0));
            Assert.Equal(AiBudget.For(2_000_000, 0, 0), AiBudget.For(0, 0, 2_000_001));
        }

        [Fact]
        public void Measure_counts_characters_without_a_window_and_estimated_tokens_with_one()
        {
            string thai = new string((char)0x0E01, 100);

            Assert.Equal(100, AiBudget.Standard.Measure(thai));
            Assert.Equal(40, AiBudget.Standard.Measure(new string('a', 40)));   // characters: not 10 tokens
            Assert.Equal(0, AiBudget.Standard.Measure(null));
            Assert.Equal(0, AiBudget.Standard.Measure(""));

            AiBudget b = AiBudget.For(8192, 0, 0);
            Assert.Equal(100, b.Measure(thai));
            Assert.Equal(10, b.Measure(new string('a', 40)));
            Assert.Equal(0, b.Measure(null));
            Assert.Equal(0, b.Measure(""));
        }

        /// <summary>Every window worth testing: the dense start, every boundary of every rule one below and one above, and a few hundred values between.</summary>
        private static List<int> SweepWindows()
        {
            var set = new SortedSet<int>();
            for (int w = 1024; w <= 20_000; w++) set.Add(w);
            // The boundaries: where a floor, a ceiling, a step or a "never over" begins or ends.
            foreach (int edge in new[]
            {
                1024, 2048, 4000, 4096, 6096, 6098, 8000, 8192, 16_000, 16_384, 32_000, 64_000, 65_536, 127_999, 128_000,
                256_000, 262_144, 999_999, 1_000_000, 1_024_000, 1_048_576, 2_000_000,
            })
                for (int d = -2; d <= 2; d++)
                    if (edge + d >= 1024 && edge + d <= 2_000_000) set.Add(edge + d);
            for (int w = 20_000; w <= 2_000_000; w += 6007) set.Add(w);
            return new List<int>(set);
        }

        private static int[] Numbers(AiBudget b) => new[]
        {
            b.AskOutputTokens, b.PadOutputTokens, b.RewriteInput, b.ReadInput, b.PadReplyChars, b.HistoryTokens,
            b.NoteReadTokens, b.NoteReadLines, b.KeptResultTokens, b.NotesSources,
        };

        [Fact]
        public void The_pieces_fit_the_window_for_every_window_with_and_without_a_reported_output()
        {
            foreach (int reported in new[] { 0, 256, 1000, 8192, 100_000 })
                foreach (int w in SweepWindows())
                {
                    AiBudget b = AiBudget.For(w, reported, 0);
                    string at = " at " + w + " / " + reported;

                    Assert.True(b.ReadInput + b.PadOutputTokens <= w, "read + output" + at);
                    Assert.True(b.RewriteInput + b.PadOutputTokens <= w, "rewrite + output" + at);
                    Assert.True(b.AskOutputTokens + b.NoteReadTokens <= w / 2, "ask output + a note read" + at);
                    Assert.True(b.KeptResultTokens >= b.NoteReadTokens, "kept" + at);
                    Assert.All(Numbers(b), n => Assert.True(n > 0, "a number is not positive" + at));
                }
        }

        [Fact]
        public void No_number_is_smaller_than_at_the_next_smaller_window()
        {
            // Found none that steps down: the "never over" parts only ever hold a number back at the small end, where it grows with the window.
            foreach (int reported in new[] { 0, 256, 1000, 8192, 100_000 })
            {
                int[]? before = null;
                int previous = 0;
                foreach (int w in SweepWindows())
                {
                    int[] now = Numbers(AiBudget.For(w, reported, 0));
                    if (before != null)
                        for (int i = 0; i < now.Length; i++)
                            Assert.True(now[i] >= before[i], "number " + i + " fell from " + before[i] + " at " + previous + " to " + now[i] + " at " + w + " / " + reported);
                    before = now;
                    previous = w;
                }
            }
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
                    Assert.InRange(b.ReadInput, 1, 1_000_000);
                    Assert.InRange(b.PadReplyChars, 64_000, 128_000);
                    Assert.InRange(b.HistoryTokens, 0, 1_000_000);
                    Assert.InRange(b.NoteReadTokens, b.InTokens ? 1 : 0, 64_000);
                    Assert.InRange(b.NoteReadLines, 400, 4000);
                    Assert.InRange(b.KeptResultTokens, 0, 80_000);
                    Assert.InRange(b.NotesSources, 8, 20);
                }
            }
        }
    }
}
