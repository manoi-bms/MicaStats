using System;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Diagnostics;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>The last raised alerts, kept in memory for the list_alerts tool.</summary>
    public class AiRecentAlertsTests
    {
        private static AlertEvent Event(int n) =>
            new(AlertRule.Defaults[0], n, "", new DateTime(2026, 9, 30, 10, 0, 0).AddMinutes(n));

        [Fact]
        public void Nothing_is_recent_before_an_alert_fires()
        {
            using var history = new MetricsHistory(capacity: 8);
            using var monitor = new AlertMonitor(history, battery: null);

            Assert.Empty(monitor.Recent);
        }

        [Fact]
        public void Recent_lists_the_newest_first_and_keeps_fifty()
        {
            using var history = new MetricsHistory(capacity: 8);
            using var monitor = new AlertMonitor(history, battery: null);

            for (int i = 0; i < 60; i++) monitor.Remember(Event(i));

            var recent = monitor.Recent;
            Assert.Equal(50, recent.Count);
            Assert.Equal(59d, recent[0].Value);
            Assert.Equal(10d, recent[49].Value);
        }

        [Fact]
        public void Recent_is_a_copy()
        {
            using var history = new MetricsHistory(capacity: 8);
            using var monitor = new AlertMonitor(history, battery: null);
            monitor.Remember(Event(1));

            var before = monitor.Recent;
            monitor.Remember(Event(2));

            Assert.Single(before);
            Assert.Equal(2, monitor.Recent.Count);
        }

        [Fact]
        public void Alerts_can_be_remembered_and_read_from_many_threads()
        {
            using var history = new MetricsHistory(capacity: 8);
            using var monitor = new AlertMonitor(history, battery: null);

            Parallel.For(0, 500, i =>
            {
                monitor.Remember(Event(i));
                Assert.InRange(monitor.Recent.Count, 1, 50);
            });

            Assert.Equal(50, monitor.Recent.Count);
        }
    }
}
