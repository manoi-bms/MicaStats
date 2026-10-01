using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using IDataObject = System.Windows.IDataObject;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Puts a credential's value on the clipboard the way password managers do: marked so Windows
    /// clipboard history (Win+V) and cloud clipboard skip it, and cleared after
    /// <see cref="ClearSeconds"/> seconds - but only if the clipboard still holds it, judged by the
    /// clipboard sequence number, so something the user copied since is never wiped. At exit a value
    /// still on the clipboard is cleared too.
    /// </summary>
    internal static class SecretClipboard
    {
        /// <summary>How long a copied value stays.</summary>
        public const int ClearSeconds = 30;

        private static uint? s_ours;

        /// <summary>Puts data on the clipboard; false when it stays busy. Tests replace it.</summary>
        internal static Func<IDataObject, bool> SetData { get; set; } = data =>
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(data, copy: true);
                    return true;
                }
                catch (COMException)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
            return false;
        };

        /// <summary>The clipboard's change counter. Tests replace it.</summary>
        internal static Func<uint> SequenceNumber { get; set; } = GetClipboardSequenceNumber;

        /// <summary>Empties the clipboard. Tests replace it.</summary>
        internal static Action Clear { get; set; } = () =>
        {
            try
            {
                Clipboard.Clear();
            }
            catch (COMException)
            {
                // Busy: the value stays; nothing better to do.
            }
        };

        /// <summary>Runs an action after a delay on the UI thread. Tests replace it.</summary>
        internal static Action<TimeSpan, Action> After { get; set; } = (delay, then) =>
        {
            var timer = new DispatcherTimer { Interval = delay };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                then();
            };
            timer.Start();
        };

        /// <summary>Copies <paramref name="value"/>; false when the clipboard stayed busy.</summary>
        public static bool Copy(string value)
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, value);
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[1]));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
            if (!SetData(data)) return false;

            uint ours = SequenceNumber();
            s_ours = ours;
            After(TimeSpan.FromSeconds(ClearSeconds), () => ClearIf(ours));
            return true;
        }

        /// <summary>Clears the clipboard if it still holds the last value copied here (at exit).</summary>
        public static void ClearIfStillOurs()
        {
            if (s_ours is uint ours) ClearIf(ours);
        }

        private static void ClearIf(uint ours)
        {
            if (s_ours != ours) return;
            s_ours = null;
            if (SequenceNumber() == ours) Clear();
        }

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();
    }
}
