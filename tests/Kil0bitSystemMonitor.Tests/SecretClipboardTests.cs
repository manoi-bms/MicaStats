using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Kil0bitSystemMonitor.Pad;
using Xunit;
using DataFormats = System.Windows.DataFormats;
using IDataObject = System.Windows.IDataObject;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>A copied value stays out of clipboard history and is cleared after 30 s unless something else was copied.</summary>
    public class SecretClipboardTests
    {
        private sealed class Fake : IDisposable
        {
            private readonly Func<IDataObject, bool> _set = SecretClipboard.SetData;
            private readonly Func<uint> _seq = SecretClipboard.SequenceNumber;
            private readonly Action _clear = SecretClipboard.Clear;
            private readonly Action<TimeSpan, Action> _after = SecretClipboard.After;

            public List<IDataObject> Set { get; } = new();
            public uint Sequence { get; set; } = 7;
            public int Cleared { get; private set; }
            public List<(TimeSpan Delay, Action Then)> Scheduled { get; } = new();

            public Fake()
            {
                SecretClipboard.SetData = data => { Set.Add(data); Sequence++; return true; };
                SecretClipboard.SequenceNumber = () => Sequence;
                SecretClipboard.Clear = () => Cleared++;
                SecretClipboard.After = (delay, then) => Scheduled.Add((delay, then));
            }

            public void Dispose()
            {
                SecretClipboard.SetData = _set;
                SecretClipboard.SequenceNumber = _seq;
                SecretClipboard.Clear = _clear;
                SecretClipboard.After = _after;
            }
        }

        [Fact]
        public void A_copy_is_kept_out_of_history_and_cloud_sync() => UiThread.Run(() =>
        {
            using var fake = new Fake();

            Assert.True(SecretClipboard.Copy("hunter2"));

            var data = Assert.Single(fake.Set);
            Assert.Equal("hunter2", data.GetData(DataFormats.UnicodeText));
            Assert.True(data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing"));
            Assert.Equal(new byte[4], ((MemoryStream)data.GetData("CanIncludeInClipboardHistory")).ToArray());
            Assert.Equal(new byte[4], ((MemoryStream)data.GetData("CanUploadToCloudClipboard")).ToArray());
            Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(fake.Scheduled).Delay);
        });

        [Fact]
        public void After_30_seconds_it_is_cleared_if_nothing_else_was_copied() => UiThread.Run(() =>
        {
            using var fake = new Fake();
            SecretClipboard.Copy("hunter2");

            fake.Scheduled[0].Then();

            Assert.Equal(1, fake.Cleared);
        });

        [Fact]
        public void Something_copied_since_is_left_alone() => UiThread.Run(() =>
        {
            using var fake = new Fake();
            SecretClipboard.Copy("hunter2");
            fake.Sequence++;   // the user copied something else

            fake.Scheduled[0].Then();
            SecretClipboard.ClearIfStillOurs();

            Assert.Equal(0, fake.Cleared);
        });

        [Fact]
        public void At_exit_a_value_still_on_the_clipboard_is_cleared() => UiThread.Run(() =>
        {
            using var fake = new Fake();
            SecretClipboard.Copy("hunter2");

            SecretClipboard.ClearIfStillOurs();
            fake.Scheduled[0].Then();   // the timer firing later finds nothing of ours

            Assert.Equal(1, fake.Cleared);
        });

        [Fact]
        public void A_busy_clipboard_reports_failure() => UiThread.Run(() =>
        {
            using var fake = new Fake();
            SecretClipboard.SetData = _ => false;

            Assert.False(SecretClipboard.Copy("hunter2"));
            Assert.Empty(fake.Scheduled);
        });
    }
}
