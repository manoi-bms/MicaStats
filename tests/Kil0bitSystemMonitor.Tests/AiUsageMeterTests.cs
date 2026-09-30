using System;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using Kil0bitSystemMonitor.Services.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class AiUsageMeterTests
    {
        [Fact]
        public void Counts_questions_until_the_daily_limit()
        {
            using var env = new AiTestEnv();
            var meter = new UsageMeter(env.PathOf("ai-usage.json"), () => new DateTime(2026, 9, 30, 12, 0, 0));

            Assert.True(meter.TryConsume(2));
            Assert.True(meter.TryConsume(2));
            Assert.False(meter.TryConsume(2));
            Assert.Equal(2, meter.UsedToday);
        }

        [Fact]
        public void The_count_survives_a_restart()
        {
            using var env = new AiTestEnv();
            string path = env.PathOf("ai-usage.json");
            var first = new UsageMeter(path, () => new DateTime(2026, 9, 30, 12, 0, 0));
            first.TryConsume(2);
            first.TryConsume(2);

            var second = new UsageMeter(path, () => new DateTime(2026, 9, 30, 18, 0, 0));

            Assert.Equal(2, second.UsedToday);
            Assert.False(second.TryConsume(2));
        }

        [Fact]
        public void The_count_starts_again_at_local_midnight()
        {
            using var env = new AiTestEnv();
            DateTime now = new(2026, 9, 30, 23, 59, 0);
            var meter = new UsageMeter(env.PathOf("ai-usage.json"), () => now);
            meter.TryConsume(1);
            Assert.False(meter.TryConsume(1));
            Assert.Equal(new DateTime(2026, 10, 1), meter.ResetsAtLocal);

            now = new DateTime(2026, 10, 1, 0, 0, 1);

            Assert.Equal(0, meter.UsedToday);
            Assert.True(meter.TryConsume(1));
            Assert.Equal(new DateTime(2026, 10, 2), meter.ResetsAtLocal);
        }

        [Fact]
        public void A_damaged_file_counts_as_zero()
        {
            using var env = new AiTestEnv();
            string path = env.PathOf("ai-usage.json");
            File.WriteAllText(path, "not json {");

            var meter = new UsageMeter(path, () => new DateTime(2026, 9, 30, 12, 0, 0));

            Assert.Equal(0, meter.UsedToday);
            Assert.True(meter.TryConsume(1));
        }

        [Fact]
        public void The_file_keeps_a_gregorian_date_under_a_thai_culture()
        {
            using var env = new AiTestEnv();
            string path = env.PathOf("ai-usage.json");
            CultureInfo before = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("th-TH");
                var meter = new UsageMeter(path, () => new DateTime(2026, 9, 30, 12, 0, 0));

                meter.TryConsume(5);

                JsonNode saved = JsonNode.Parse(File.ReadAllText(path))!;
                Assert.Equal("2026-09-30", saved["date"]!.GetValue<string>());
                Assert.Equal(1, saved["count"]!.GetValue<int>());
            }
            finally
            {
                CultureInfo.CurrentCulture = before;
            }
        }
    }
}
