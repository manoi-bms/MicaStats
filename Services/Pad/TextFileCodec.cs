using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>How a file-backed note's text becomes bytes when it is saved to its file.</summary>
    public enum PadEncoding
    {
        /// <summary>UTF-8 without a byte order mark.</summary>
        Utf8,
        /// <summary>UTF-8 preceded by EF BB BF.</summary>
        Utf8Bom,
        /// <summary>UTF-16 little endian preceded by FF FE.</summary>
        Utf16Le,
        /// <summary>UTF-16 big endian preceded by FE FF.</summary>
        Utf16Be,
        /// <summary>A legacy code page, recorded beside it: cp874 on Thai Windows, cp1252 on Western.</summary>
        Ansi,
    }

    /// <summary>A line break style.</summary>
    public enum LineEnding
    {
        /// <summary>Windows: carriage return then line feed.</summary>
        CrLf,
        /// <summary>Unix: line feed alone.</summary>
        Lf,
        /// <summary>Classic Mac: carriage return alone.</summary>
        Cr,
    }

    /// <summary>Why a file was not opened as text.</summary>
    public enum DecodeFailure
    {
        /// <summary>It was opened.</summary>
        None,
        /// <summary>Larger than <see cref="TextFileCodec.MaxFileBytes"/>.</summary>
        TooLarge,
        /// <summary>A NUL byte in the first <see cref="TextFileCodec.BinaryProbeBytes"/> with no UTF-16 byte order mark.</summary>
        Binary,
    }

    /// <summary>
    /// A decoded file: its text, and everything needed to write it back byte for byte.
    /// <see cref="Lossless"/> is false when some bytes could not be decoded and now read U+FFFD:
    /// saving such a file cannot give back the original bytes, so the caller warns first.
    /// </summary>
    public sealed record DecodedText(string Text, PadEncoding Encoding, int CodePage, LineEnding LineEnding, bool Lossless);

    /// <summary>
    /// Turns file bytes into text and back without changing what the user did not change.
    ///
    /// <para>
    /// The failure this exists to prevent is silent: an editor that reads a Thai cp874 file as
    /// UTF-8 shows replacement characters, and the first save destroys the original. So the
    /// order is fixed — byte order mark, then strict UTF-8, then the system code page — and
    /// <see cref="Encode"/> is the exact inverse of <see cref="TryDecode"/> for every encoding it
    /// can report.
    /// </para>
    /// </summary>
    public static class TextFileCodec
    {
        /// <summary>Files larger than this are refused rather than opened slowly.</summary>
        public const long MaxFileBytes = 50L * 1024 * 1024;

        /// <summary>How far into a file to look for the NUL byte that marks it as binary.</summary>
        public const int BinaryProbeBytes = 8192;

        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        private static readonly UnicodeEncoding StrictUtf16Le = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
        private static readonly UnicodeEncoding StrictUtf16Be = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        /// <summary>
        /// Makes legacy code pages such as 874 and 1252 available. .NET 8 ships only the Unicode
        /// encodings unless this provider is registered. Safe to call any number of times from any
        /// thread: registration is synchronized, and registering the same provider again does nothing.
        /// </summary>
        public static void EnsureCodePages() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        /// <summary>The system ANSI code page (the "language for non-Unicode programs"), not the user's display culture.</summary>
        public static int SystemAnsiCodePage => (int)GetACP();

        /// <summary>
        /// Decodes a whole file. Returns false with <paramref name="failure"/> set when the file is
        /// too large or looks binary.
        /// </summary>
        public static bool TryDecode(byte[] bytes, int ansiCodePage, out DecodedText? result, out DecodeFailure failure)
        {
            result = null;
            failure = DecodeFailure.None;

            if (bytes.LongLength > MaxFileBytes)
            {
                failure = DecodeFailure.TooLarge;
                return false;
            }

            // A byte order mark is authoritative. A stray bad sequence later in such a file still
            // opens, as U+FFFD, but the result is marked not lossless so the caller can warn that
            // saving will write the replacement characters.
            if (StartsWith(bytes, 0xEF, 0xBB, 0xBF))
                return Decode(bytes, 3, StrictUtf8, Encoding.UTF8, PadEncoding.Utf8Bom, 0, out result);
            if (StartsWith(bytes, 0xFF, 0xFE))
                return Decode(bytes, 2, StrictUtf16Le, Encoding.Unicode, PadEncoding.Utf16Le, 0, out result);
            if (StartsWith(bytes, 0xFE, 0xFF))
                return Decode(bytes, 2, StrictUtf16Be, Encoding.BigEndianUnicode, PadEncoding.Utf16Be, 0, out result);

            int probe = Math.Min(bytes.Length, BinaryProbeBytes);
            if (Array.IndexOf(bytes, (byte)0, 0, probe) >= 0)
            {
                failure = DecodeFailure.Binary;
                return false;
            }

            try
            {
                return Finish(StrictUtf8.GetString(bytes), PadEncoding.Utf8, 0, lossless: true, out result);
            }
            catch (DecoderFallbackException)
            {
                // Not UTF-8: fall through to the code page every legacy editor on this machine used.
            }

            EnsureCodePages();
            var strictAnsi = Encoding.GetEncoding(ansiCodePage, EncoderFallback.ReplacementFallback, DecoderFallback.ExceptionFallback);
            return Decode(bytes, 0, strictAnsi, Encoding.GetEncoding(ansiCodePage), PadEncoding.Ansi, ansiCodePage, out result);
        }

        /// <summary>The bytes for <paramref name="text"/>, including the byte order mark the encoding calls for.</summary>
        public static byte[] Encode(string text, PadEncoding encoding, int codePage)
        {
            switch (encoding)
            {
                case PadEncoding.Utf8:
                    return Utf8NoBom.GetBytes(text);
                case PadEncoding.Utf8Bom:
                    return Concat(new byte[] { 0xEF, 0xBB, 0xBF }, Utf8NoBom.GetBytes(text));
                case PadEncoding.Utf16Le:
                    return Concat(new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes(text));
                case PadEncoding.Utf16Be:
                    return Concat(new byte[] { 0xFE, 0xFF }, Encoding.BigEndianUnicode.GetBytes(text));
                default:
                    EnsureCodePages();
                    return Encoding.GetEncoding(codePage).GetBytes(text);
            }
        }

        /// <summary>
        /// True when every character survives <see cref="Encode"/>. Checked before a save so a
        /// Thai character typed into a cp1252 file is caught instead of saved as "?".
        /// </summary>
        public static bool CanEncodeLosslessly(string text, PadEncoding encoding, int codePage)
        {
            try
            {
                Encoding strict = encoding switch
                {
                    PadEncoding.Ansi => StrictCodePage(codePage),
                    PadEncoding.Utf16Le => StrictUtf16Le,
                    PadEncoding.Utf16Be => StrictUtf16Be,
                    _ => StrictUtf8,
                };
                strict.GetByteCount(text);
                return true;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }

        /// <summary>The most frequent line break in <paramref name="text"/>; CRLF when there is none or on a tie.</summary>
        public static LineEnding DetectLineEnding(string text)
        {
            int crlf = 0, lf = 0, cr = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                    else cr++;
                }
                else if (c == '\n')
                {
                    lf++;
                }
            }

            if (crlf >= lf && crlf >= cr) return LineEnding.CrLf;
            return lf >= cr ? LineEnding.Lf : LineEnding.Cr;
        }

        /// <summary>The characters of one line break.</summary>
        public static string NewLineOf(LineEnding ending) => ending switch
        {
            LineEnding.Lf => "\n",
            LineEnding.Cr => "\r",
            _ => "\r\n",
        };

        /// <summary>Rewrites every line break, of any style, as <paramref name="target"/>.</summary>
        public static string ConvertLineEndings(string text, LineEnding target) =>
            Regex.Replace(text, "\r\n|\r|\n", NewLineOf(target));

        /// <summary>Status-bar name of an encoding.</summary>
        public static string Describe(PadEncoding encoding, int codePage) => encoding switch
        {
            PadEncoding.Utf8 => "UTF-8",
            PadEncoding.Utf8Bom => "UTF-8 BOM",
            PadEncoding.Utf16Le => "UTF-16 LE",
            PadEncoding.Utf16Be => "UTF-16 BE",
            _ => "ANSI " + codePage.ToString(CultureInfo.InvariantCulture),
        };

        /// <summary>Status-bar name of a line ending.</summary>
        public static string Describe(LineEnding ending) => ending switch
        {
            LineEnding.Lf => "LF",
            LineEnding.Cr => "CR",
            _ => "CRLF",
        };

        private static Encoding StrictCodePage(int codePage)
        {
            EnsureCodePages();
            return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        }

        /// <summary>Decodes strictly when the bytes allow it; otherwise leniently, marking the result as not lossless.</summary>
        private static bool Decode(byte[] bytes, int offset, Encoding strict, Encoding lenient,
                                   PadEncoding encoding, int codePage, out DecodedText? result)
        {
            int count = bytes.Length - offset;
            try
            {
                return Finish(strict.GetString(bytes, offset, count), encoding, codePage, lossless: true, out result);
            }
            catch (DecoderFallbackException)
            {
                return Finish(lenient.GetString(bytes, offset, count), encoding, codePage, lossless: false, out result);
            }
        }

        private static bool Finish(string text, PadEncoding encoding, int codePage, bool lossless, out DecodedText? result)
        {
            result = new DecodedText(text, encoding, codePage, DetectLineEnding(text), lossless);
            return true;
        }

        private static bool StartsWith(byte[] bytes, params byte[] prefix)
        {
            if (bytes.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
                if (bytes[i] != prefix[i]) return false;
            return true;
        }

        private static byte[] Concat(byte[] first, byte[] second)
        {
            var result = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, result, 0, first.Length);
            Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
            return result;
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetACP();
    }
}
