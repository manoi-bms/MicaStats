using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace MicaStats.Pi;

public class App : Application
{
    /// <summary>SIGUSR1 on Linux (all architectures .NET supports there).</summary>
    private const int SigUsr1 = 10;

    private PosixSignalRegistration? _toggleSignal;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The dropdown is hidden far more often than shown, so closing/hiding it
            // must never end the process; only an explicit Quit or a signal does.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var window = new MainWindow();
            bool startHidden = desktop.Args?.Contains("--hidden") == true;

            _toggleSignal = PosixSignalRegistration.Create((PosixSignal)SigUsr1, ctx =>
            {
                ctx.Cancel = true;
                Dispatcher.UIThread.Post(window.ToggleDropdown);
            });

            WritePidFile();
            desktop.Exit += (_, _) =>
            {
                _toggleSignal?.Dispose();
                DeletePidFile();
            };

            if (!startHidden) window.ShowDropdown();
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static string PidFilePath =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(),
                     "micastats-pi.pid");

    private static void WritePidFile()
    {
        try { File.WriteAllText(PidFilePath, Environment.ProcessId.ToString()); }
        catch { /* toggling still works by process name */ }
    }

    private static void DeletePidFile()
    {
        try { File.Delete(PidFilePath); }
        catch { /* already gone */ }
    }
}
