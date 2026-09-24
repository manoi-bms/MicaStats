using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Watchdog;

namespace Kil0bitSystemMonitor.ViewModels
{
    /// <summary>
    /// The four facts about the selected process that need a handle: path, command line, user,
    /// elevation.
    ///
    /// <para>
    /// Read for one row, when it is selected, on a background task — never for the list, which
    /// must not open a handle per row. A read that finishes after the user has already selected
    /// something else is discarded, so the pane can never show one process's command line under
    /// another's name.
    /// </para>
    /// </summary>
    public sealed class ProcessDetailViewModel : INotifyPropertyChanged
    {
        private const string Prompt = "Select a process to see its path, command line and account.";

        private int _version;
        private string _title = Prompt;
        private string _imagePath = "";
        private string _commandLine = "";
        private string _user = "";
        private string _elevated = "";

        /// <summary>The selected row's name and pid, or the prompt when nothing is selected.</summary>
        public string Title { get => _title; private set => Set(ref _title, value); }

        /// <summary>The full image path, or a reason it could not be read.</summary>
        public string ImagePath { get => _imagePath; private set => Set(ref _imagePath, value); }

        /// <summary>The command line, or a reason it could not be read.</summary>
        public string CommandLine { get => _commandLine; private set => Set(ref _commandLine, value); }

        /// <summary>DOMAIN\name, the SID, or a reason the token could not be read.</summary>
        public string User { get => _user; private set => Set(ref _user, value); }

        /// <summary>"Yes", "No", "Unknown", or a reason, mirroring <see cref="User"/>.</summary>
        public string Elevated { get => _elevated; private set => Set(ref _elevated, value); }

        /// <summary>Back to the prompt, and any read in flight is discarded.</summary>
        public void Clear()
        {
            Interlocked.Increment(ref _version);
            Title = Prompt;
            ImagePath = CommandLine = User = Elevated = "";
        }

        /// <summary>
        /// For a selection of several rows: there is no single process to describe, and reading
        /// several would open a handle per row. Says what End task will do instead.
        /// </summary>
        public void ShowMany(int count)
        {
            Interlocked.Increment(ref _version);
            Title = count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " processes selected — End task ends all of them, after a preview.";
            ImagePath = CommandLine = User = Elevated = "";
        }

        /// <summary>
        /// Shows the row's identity at once and fills the four facts when the read returns.
        /// </summary>
        public void Load(ProcessRow row)
        {
            int version = Interlocked.Increment(ref _version);
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            int pid = row.Pid;
            long created = row.CreateTime;

            Title = row.Name + "   ·   PID " + pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ImagePath = CommandLine = User = Elevated = "Reading…";

            Task.Run(() =>
            {
                string path, cmd, user, elevated;
                try
                {
                    // Identity first: the PID may already belong to a different process.
                    if (ProcessControl.HasExited(pid, created))
                    {
                        path = cmd = user = elevated = "Process has exited";
                    }
                    else
                    {
                        if (!ProcessDetails.TryRead(pid, out path, out cmd))
                            path = cmd = "Unavailable — protected, or already exited";
                        else if (cmd.Length == 0)
                            cmd = "Unavailable";

                        if (ProcessAccountReader.TryRead(pid, out var account, out string reason))
                        {
                            user = account.User;
                            elevated = account.Elevated switch { true => "Yes", false => "No", _ => "Unknown" };
                        }
                        else
                        {
                            user = elevated = reason;
                        }
                    }
                }
                catch (Exception ex)
                {
                    path = cmd = user = elevated = "Unavailable";
                    DiagnosticsLog.Error("processes", "Reading process details failed", ex);
                }

                dispatcher?.BeginInvoke(new Action(() =>
                {
                    if (version != Volatile.Read(ref _version)) return;   // superseded
                    ImagePath = path;
                    CommandLine = cmd;
                    User = user;
                    Elevated = elevated;
                }));
            });
        }

        /// <inheritdoc />
        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set(ref string field, string value, [CallerMemberName] string? name = null)
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
