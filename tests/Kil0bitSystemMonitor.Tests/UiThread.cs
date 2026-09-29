using System;
using System.Threading;
using System.Windows.Threading;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// One STA thread with a WPF Application carrying the app's ModernWpf resources, shared by
    /// every UI test. WPF allows one Application per process, and a window must be built on the
    /// thread that owns the resources it looks up.
    /// </summary>
    internal static class UiThread
    {
        private static readonly Lazy<Dispatcher> s_dispatcher = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Runs <paramref name="action"/> on the UI thread; its exceptions surface here.</summary>
        public static void Run(Action action) => s_dispatcher.Value.Invoke(action);

        private static Dispatcher Start()
        {
            Dispatcher? dispatcher = null;
            Exception? failure = null;
            using var ready = new ManualResetEventSlim();

            var thread = new Thread(() =>
            {
                try
                {
                    var app = System.Windows.Application.Current
                              ?? new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                    var resources = new System.Windows.ResourceDictionary();
                    resources.MergedDictionaries.Add(new ModernWpf.ThemeResources());
                    resources.MergedDictionaries.Add(new ModernWpf.Controls.XamlControlsResources());
                    app.Resources = resources;
                    dispatcher = Dispatcher.CurrentDispatcher;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    ready.Set();
                }

                if (failure == null) Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "UI tests",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();

            if (failure != null) throw new InvalidOperationException("The UI test thread could not start", failure);
            return dispatcher!;
        }
    }
}
