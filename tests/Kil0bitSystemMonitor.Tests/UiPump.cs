using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Threading;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Waits for a task on the UI test thread while that thread's dispatcher keeps running, so
    /// the task's awaits (which resume on the dispatcher) can finish. Call inside
    /// <see cref="UiThread.Run"/>; a plain Wait there would deadlock.
    /// </summary>
    internal static class UiPump
    {
        /// <summary>Pumps until <paramref name="task"/> completes, then rethrows its failure if any.</summary>
        public static void Wait(Task task, int timeoutMs = 10_000)
        {
            var frame = new DispatcherFrame();
            Task.WhenAny(task, Task.Delay(timeoutMs))
                .ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);

            Assert.True(task.IsCompleted,
                "The task did not finish within " + timeoutMs.ToString(CultureInfo.InvariantCulture) + " ms");
            task.GetAwaiter().GetResult();
        }
    }
}
