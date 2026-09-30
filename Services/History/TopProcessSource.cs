using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services.Ai.Tools;

namespace Kil0bitSystemMonitor.Services.History
{
    /// <summary>Where <see cref="HistoryRecorder"/> gets each minute's top processes; a fake in tests.</summary>
    public interface ITopProcessSource
    {
        /// <summary>
        /// The busiest processes right now: null when no ranking arrived in time or
        /// <paramref name="ct"/> was cancelled. May throw on any other failure; the caller
        /// (<see cref="HistoryRecorder"/>) logs that once.
        /// </summary>
        Task<TopProcessSample?> SampleAsync(CancellationToken ct);
    }

    /// <summary>
    /// Reads the top processes from the shared <see cref="ProcessSampler"/> with a short lease.
    ///
    /// <para>
    /// The sampler only runs while someone holds a lease, and CPU share is a difference between
    /// two samples two seconds apart. So each call retains it, waits until it has CPU data (at
    /// once when the slowdown recorder or an open window already holds a lease, about two seconds
    /// otherwise, never longer than the timeout), reads the two rankings, and releases it. Nothing
    /// samples continuously on the history's behalf.
    /// </para>
    /// </summary>
    public sealed class SamplerTopProcessSource : ITopProcessSource
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
        private static readonly Lazy<Redactor> CurrentUser = new(Redactor.ForCurrentUser);

        private readonly ProcessSampler _sampler;
        private readonly TimeSpan _timeout;
        private readonly Func<int, string?> _pathOf;

        /// <param name="sampler">Normally <c>App.SharedProcessSampler</c>.</param>
        /// <param name="timeout">How long to wait for CPU data; 6 seconds when null.</param>
        public SamplerTopProcessSource(ProcessSampler sampler, TimeSpan? timeout = null)
            : this(sampler, timeout, null)
        {
        }

        /// <summary>Test seam: <paramref name="pathOf"/> replaces the exe path lookup by pid.</summary>
        internal SamplerTopProcessSource(ProcessSampler sampler, TimeSpan? timeout, Func<int, string?>? pathOf)
        {
            _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
            _timeout = timeout ?? TimeSpan.FromSeconds(6);
            _pathOf = pathOf ?? ProcessPaths.TryGetPath;
        }

        /// <inheritdoc/>
        public async Task<TopProcessSample?> SampleAsync(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return null;

            _sampler.Retain();
            try
            {
                var waited = Stopwatch.StartNew();
                while (!_sampler.HasCpuData)
                {
                    if (waited.Elapsed >= _timeout) return null;
                    await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                }
                return Read();
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            finally
            {
                _sampler.Release();
            }
        }

        private TopProcessSample? Read()
        {
            ProcessUsage? cpu = FirstOf(_sampler.TopByCpu);
            ProcessUsage? ram = FirstOf(_sampler.TopByRam);
            if (cpu == null && ram == null) return null;

            // Stored redacted (spec: "redacted path"); tool output is redacted again on the way out.
            string? path = cpu == null ? null : _pathOf(cpu.Pid);
            return new TopProcessSample(
                cpu?.Name,
                path == null ? null : CurrentUser.Value.Redact(path),
                cpu?.CpuPercent,
                ram?.Name,
                ram == null ? null : (float)(ram.WorkingSet / 1024d / 1024d));
        }

        private static ProcessUsage? FirstOf(IReadOnlyList<ProcessUsage> ranking) => ranking.Count > 0 ? ranking[0] : null;
    }
}
