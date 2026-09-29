using System;
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
        public void A_null_message_is_rejected()
        {
            Assert.False(PadIpc.TryRead(IntPtr.Zero, out _));
        }
    }
}
