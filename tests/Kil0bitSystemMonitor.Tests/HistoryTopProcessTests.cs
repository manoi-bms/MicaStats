using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.History;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Exe paths by pid and the once-a-minute top-process sample. Against the live system, like
    /// ProcessSamplerTests: the sampler walks a kernel structure that only reality can check.
    /// </summary>
    public class HistoryTopProcessTests
    {
        [Fact]
        public void The_path_of_a_running_process_is_found()
        {
            string? path = ProcessPaths.TryGetPath(Environment.ProcessId);

            Assert.Equal(Environment.ProcessPath, path, ignoreCase: true);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-4)]
        [InlineData(int.MaxValue)]
        public void There_is_no_path_for_an_impossible_pid(int pid) => Assert.Null(ProcessPaths.TryGetPath(pid));

        [Fact]
        public async Task The_sampler_source_names_the_top_processes_and_releases_its_lease()
        {
            using var sampler = new ProcessSampler();
            var source = new SamplerTopProcessSource(sampler, TimeSpan.FromSeconds(8));

            TopProcessSample? sample = await source.SampleAsync(CancellationToken.None);

            Assert.NotNull(sample);
            Assert.False(string.IsNullOrWhiteSpace(sample!.CpuName));
            Assert.False(string.IsNullOrWhiteSpace(sample.RamName));
            Assert.InRange(sample.CpuPercent!.Value, 0f, 100f);
            Assert.True(sample.RamMb > 0);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.DoesNotContain(profile, sample.CpuPath ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.False(sampler.Enabled);   // the lease went back
        }

        [Fact]
        public async Task A_lease_someone_else_holds_is_left_alone()
        {
            using var sampler = new ProcessSampler();
            sampler.Retain();   // as the slowdown recorder does all day

            await new SamplerTopProcessSource(sampler, TimeSpan.FromSeconds(8)).SampleAsync(CancellationToken.None);

            Assert.True(sampler.Enabled);
            sampler.Release();
            Assert.False(sampler.Enabled);
        }

        [Fact]
        public async Task No_data_in_time_gives_null()
        {
            var sampler = new ProcessSampler();
            sampler.Dispose();   // a disposed sampler never produces a sample
            var waited = Stopwatch.StartNew();

            TopProcessSample? sample = await new SamplerTopProcessSource(sampler, TimeSpan.FromMilliseconds(300))
                .SampleAsync(CancellationToken.None);

            Assert.Null(sample);
            Assert.True(waited.Elapsed >= TimeSpan.FromMilliseconds(300));
        }

        [Fact]
        public async Task A_cancelled_sample_gives_null_and_releases_the_lease()
        {
            using var sampler = new ProcessSampler();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            TopProcessSample? sample = await new SamplerTopProcessSource(sampler, TimeSpan.FromSeconds(8)).SampleAsync(cts.Token);

            Assert.Null(sample);
            Assert.False(sampler.Enabled);
        }

        [Fact]
        public async Task A_failure_while_reading_propagates_and_the_lease_still_goes_back()
        {
            using var sampler = new ProcessSampler();
            var source = new SamplerTopProcessSource(sampler, TimeSpan.FromSeconds(8),
                _ => throw new InvalidOperationException("path lookup broke"));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.SampleAsync(CancellationToken.None));

            Assert.Equal("path lookup broke", ex.Message);
            Assert.False(sampler.Enabled);
        }
    }
}
