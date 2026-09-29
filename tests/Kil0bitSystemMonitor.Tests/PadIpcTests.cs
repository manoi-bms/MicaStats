using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// WM_COPYDATA arrives from any process on the desktop, so the reader must reject anything
    /// that is not exactly a MicaPad open request.
    /// </summary>
    public class PadIpcTests
    {
        private static bool Read(IntPtr tag, string payload, int? byteCount, out string path)
        {
            IntPtr text = Marshal.StringToHGlobalUni(payload);
            IntPtr block = Marshal.AllocHGlobal(Marshal.SizeOf<PadIpc.CopyDataStruct>());
            try
            {
                var data = new PadIpc.CopyDataStruct
                {
                    dwData = tag,
                    cbData = byteCount ?? payload.Length * 2,
                    lpData = text,
                };
                Marshal.StructureToPtr(data, block, false);
                return PadIpc.TryRead(block, out path);
            }
            finally
            {
                Marshal.FreeHGlobal(block);
                Marshal.FreeHGlobal(text);
            }
        }

        [Fact]
        public void A_valid_message_carries_the_path()
        {
            Assert.True(Read(PadIpc.Tag, @"C:\a b\x.txt", null, out string path));
            Assert.Equal(@"C:\a b\x.txt", path);
        }

        [Fact]
        public void An_empty_payload_means_just_open()
        {
            Assert.True(Read(PadIpc.Tag, "", null, out string path));
            Assert.Equal("", path);
        }

        [Fact]
        public void Another_programs_copydata_is_ignored()
        {
            Assert.False(Read(new IntPtr(0x1234), "x", null, out _));
        }

        [Fact]
        public void An_odd_byte_count_is_rejected()
        {
            Assert.False(Read(PadIpc.Tag, "ab", 3, out _));
        }

        [Fact]
        public void An_oversized_payload_is_rejected_before_it_is_read()
        {
            Assert.False(Read(PadIpc.Tag, "x", (PadIpc.MaxChars + 1) * 2, out _));
        }

        [Fact]
        public void A_relative_path_is_rejected()
        {
            Assert.False(Read(PadIpc.Tag, @"notes\x.txt", null, out _));
        }

        [Fact]
        public void A_null_message_is_rejected()
        {
            Assert.False(PadIpc.TryRead(IntPtr.Zero, out _));
        }

        [Fact]
        public void The_running_window_is_waited_for_while_it_starts()
        {
            int calls = 0;
            var waits = new List<TimeSpan>();

            IntPtr found = PadIpc.FindWithRetry(() => ++calls < 3 ? IntPtr.Zero : new IntPtr(42),
                                                20, TimeSpan.FromMilliseconds(250), waits.Add);

            Assert.Equal(new IntPtr(42), found);
            Assert.Equal(3, calls);
            Assert.Equal(new[] { TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250) }, waits);
        }

        [Fact]
        public void A_window_that_never_appears_gives_up_after_the_last_attempt()
        {
            int calls = 0;
            var waits = new List<TimeSpan>();

            IntPtr found = PadIpc.FindWithRetry(() => { calls++; return IntPtr.Zero; },
                                                4, TimeSpan.FromMilliseconds(250), waits.Add);

            Assert.Equal(IntPtr.Zero, found);
            Assert.Equal(4, calls);
            Assert.Equal(3, waits.Count);   // no wait after the last attempt
        }
    }
}
