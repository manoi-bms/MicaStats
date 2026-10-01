using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>
    /// One <c>![alt](source "title" =WxH)</c> in a line (spec 6.3): where it starts, its length, its
    /// parts, and the size it asks for in device-independent pixels (either side may be absent).
    /// </summary>
    public sealed record ImageRef(int Start, int Length, string Alt, string Source, string? Title, double? Width, double? Height);

    /// <summary>Where an image's bytes come from.</summary>
    public enum ImageOrigin
    {
        File,
        Data,
        Web,
    }

    /// <summary>
    /// An image source resolved: a file's full path, a data URI or a web address, with the key the
    /// image cache uses (a data URI by its hash); or, in <see cref="Error"/>, why it cannot be shown.
    /// <see cref="Source"/> is the source as the note writes it.
    /// </summary>
    public sealed record ImageLocation(ImageOrigin Origin, string Source, string Address, string Key, string? Error);

    /// <summary>What loading gave: the bytes (SVG text when <see cref="Svg"/>), or an error that lasts (the same load gives it again) or passes.</summary>
    public sealed record ImageLoad(byte[]? Bytes, bool Svg, string? Error, bool Lasting);

    /// <summary>The words MicaPad shows about images (spec 6.3).</summary>
    public static class ImageText
    {
        public const string Loading = "Loading\u2026";
        public const string WebOff = "Web images are off \u2014 turn them on in Settings \u2192 MicaPad.";
        public const string CouldNotRead = "The image could not be read.";
        public const string NeedsSavedFile = "A relative path needs a saved file.";

        /// <summary>"Image not found: {source}", the source as the note writes it.</summary>
        public static string NotFound(string source) => "Image not found: " + source;

        /// <summary>"The image could not be downloaded ({host})."</summary>
        public static string CouldNotDownload(string host) => "The image could not be downloaded (" + host + ").";
    }

    /// <summary>
    /// Where image previews come from (spec 6.3): finding <c>![...](...)</c> in a line outside code
    /// spans, resolving a source against the tab's file folder (R12), and loading it - a file (20 MB
    /// at most, read on the thread pool), a <c>data:image/...;base64,</c> address, or a web address
    /// while web images are allowed (one GET, 10 s, 10 MB at most, no cookies; R14). Never throws for
    /// a missing file or a failed download: those are <see cref="ImageLoad"/> errors.
    /// </summary>
    public sealed class ImageSources : IDisposable
    {
        public const long MaxFileBytes = 20L * 1024 * 1024;
        public const int MaxWebBytes = 10 * 1024 * 1024;

        /// <summary>Longer lines are not searched (R15): a data address of about 150 KB still fits.</summary>
        public const int MaxLineLength = 200_000;

        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

        private const int SvgSniffBytes = 4096;
        private static readonly Regex DataRx = new(@"^data:image/[a-z0-9.+-]+(;[a-z0-9._=-]+)*;base64,", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex SchemeRx = new(@"^[A-Za-z][A-Za-z0-9+.-]+:", RegexOptions.CultureInvariant);

        private readonly HttpClient _http;

        /// <param name="handler">Tests pass a fake web; null uses <see cref="CreateHandler"/>.</param>
        public ImageSources(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
        {
            _http = handler == null ? new HttpClient(CreateHandler()) : new HttpClient(handler, disposeHandler: false);
            _http.Timeout = timeout ?? DefaultTimeout;
            _http.MaxResponseContentBufferSize = MaxWebBytes;
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("MicaPad");
        }

        /// <summary>The system's network settings (its proxy included), no cookies, at most 5 redirects (R14).</summary>
        internal static HttpMessageHandler CreateHandler() => new SocketsHttpHandler { UseCookies = false, MaxAutomaticRedirections = 5 };

        /// <summary>Every image of the line, in order. Escaped (<c>\!</c>) and code-span images are text.</summary>
        public static IReadOnlyList<ImageRef> Find(string line)
        {
            var found = new List<ImageRef>();
            string s = line ?? "";
            if (s.Length > MaxLineLength || !s.Contains("![", StringComparison.Ordinal)) return found;

            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\')
                {
                    i += 2;   // an escaped character, \! included, is text
                    continue;
                }
                if (c == '`')
                {
                    i = SkipCode(s, i);
                    continue;
                }
                if (c == '!' && i + 1 < s.Length && s[i + 1] == '[' && TryImage(s, i) is { } image)
                {
                    found.Add(image);
                    i += image.Length;
                    continue;
                }
                i++;
            }
            return found;
        }

        /// <summary>
        /// Where <paramref name="source"/> points (R12): a data address, a web address (only while
        /// <paramref name="webAllowed"/>), a <c>file:</c> address, a full path, or a path relative to
        /// <paramref name="baseFolder"/> (the file tab's folder; null in a note). Anything else is an error.
        /// </summary>
        public static ImageLocation Resolve(string source, string? baseFolder, bool webAllowed)
        {
            string text = (source ?? "").Trim();
            if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return DataRx.IsMatch(text) ? new ImageLocation(ImageOrigin.Data, text, text, "data\0" + Hash(text), null) : Refused(text, ImageText.CouldNotRead);

            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(text, UriKind.Absolute, out var web) || web.Host.Length == 0) return Refused(text, ImageText.NotFound(text));
                if (!webAllowed) return Refused(text, ImageText.WebOff);
                return new ImageLocation(ImageOrigin.Web, text, web.AbsoluteUri, "web\0" + web.AbsoluteUri, null);
            }

            string? path;
            if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                path = Uri.TryCreate(text, UriKind.Absolute, out var file) && file.IsFile ? file.LocalPath : null;
            }
            else
            {
                string decoded = Uri.UnescapeDataString(text);
                if (Path.IsPathFullyQualified(decoded)) path = decoded;
                else if (Path.IsPathRooted(decoded) || SchemeRx.IsMatch(decoded)) path = null;   // "/uploads/a.png" lives on a wiki's server
                else if (baseFolder == null) return Refused(text, ImageText.NeedsSavedFile);
                else path = Path.Combine(baseFolder, decoded);
            }

            if (path == null || path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal))
                return Refused(text, ImageText.NotFound(text));
            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
            {
                return Refused(text, ImageText.NotFound(text));
            }
            return new ImageLocation(ImageOrigin.File, text, path, "file\0" + path, null);
        }

        /// <summary>Loads the image's bytes: a file on the thread pool, a data address at once, a download through the HTTP client.</summary>
        public Task<ImageLoad> LoadAsync(ImageLocation location, CancellationToken cancel = default)
        {
            if (location.Error != null) return Task.FromResult(Failed(location.Error, lasting: true));
            return location.Origin switch
            {
                ImageOrigin.Data => Task.FromResult(ReadData(location.Address)),
                ImageOrigin.Web => DownloadAsync(location.Address, cancel),
                _ => Task.Run(() => ReadFile(location), cancel),
            };
        }

        /// <summary>True when the bytes are SVG text (R11): after a byte order mark and spaces they start with &lt;, and the first 4 KB hold &lt;svg.</summary>
        public static bool LooksLikeSvg(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return false;
            string head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, SvgSniffBytes)).TrimStart((char)0xFEFF, ' ', '\t', '\r', '\n');
            return head.StartsWith('<') && head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose() => _http.Dispose();

        private static ImageLocation Refused(string source, string error) => new(ImageOrigin.File, source, "", "", error);

        private static ImageLoad Loaded(byte[] bytes) => new(bytes, LooksLikeSvg(bytes), null, Lasting: true);

        private static ImageLoad Failed(string error, bool lasting) => new(null, false, error, lasting);

        private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        private static string HostOf(string address) =>
            Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Authority : address;

        private static ImageLoad ReadData(string uri)
        {
            try
            {
                return Loaded(Convert.FromBase64String(uri.Substring(uri.IndexOf(',') + 1)));
            }
            catch (FormatException)
            {
                return Failed(ImageText.CouldNotRead, lasting: true);
            }
        }

        private static ImageLoad ReadFile(ImageLocation location)
        {
            try
            {
                using var stream = new FileStream(location.Address, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaxFileBytes) return Failed(ImageText.CouldNotRead, lasting: true);
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                return Loaded(bytes);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return Failed(ImageText.NotFound(location.Source), lasting: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
            {
                return Failed(ImageText.CouldNotRead, lasting: false);   // locked or unreadable now; it may be readable later
            }
        }

        private async Task<ImageLoad> DownloadAsync(string address, CancellationToken cancel)
        {
            string failed = ImageText.CouldNotDownload(HostOf(address));
            try
            {
                // MaxResponseContentBufferSize refuses an answer over 10 MB while it is read.
                using var response = await _http.GetAsync(address, cancel).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return Failed(failed, lasting: false);
                byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
                return bytes.Length > MaxWebBytes ? Failed(failed, lasting: false) : Loaded(bytes);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
            {
                return Failed(failed, lasting: false);
            }
        }

        /// <summary>The image at <paramref name="start"/> (its <c>!</c>), or null when the text there is no image.</summary>
        private static ImageRef? TryImage(string s, int start)
        {
            var alt = new StringBuilder();
            int i = start + 2;
            while (i < s.Length && s[i] != ']')
            {
                if (s[i] == '[') return null;
                if (s[i] == '\\' && i + 1 < s.Length) i++;
                alt.Append(s[i]);
                i++;
            }
            if (i + 1 >= s.Length || s[i + 1] != '(') return null;
            i += 2;
            SkipSpaces(s, ref i);

            string source;
            if (i < s.Length && s[i] == '<')
            {
                int close = s.IndexOf('>', i + 1);
                if (close < 0) return null;
                source = s.Substring(i + 1, close - i - 1);
                i = close + 1;
            }
            else
            {
                // Up to a space or the ")" that closes the image; parentheses inside stay as pairs.
                int begin = i;
                int depth = 0;
                while (i < s.Length && !char.IsWhiteSpace(s[i]))
                {
                    if (s[i] == '(') depth++;
                    else if (s[i] == ')' && depth-- == 0) break;
                    i++;
                }
                source = s.Substring(begin, i - begin);
            }
            if (source.Trim().Length == 0) return null;

            string? title = null;
            double? width = null;
            double? height = null;
            bool sized = false;
            while (true)
            {
                int before = i;
                SkipSpaces(s, ref i);
                if (i >= s.Length) return null;
                if (s[i] == ')') break;
                if (i == before) return null;   // a title or a size follows a space
                if ((s[i] == '"' || s[i] == '\'') && title == null && !sized)
                {
                    int close = s.IndexOf(s[i], i + 1);
                    if (close < 0) return null;
                    title = s.Substring(i + 1, close - i - 1);
                    i = close + 1;
                }
                else if (s[i] == '=' && !sized && TrySize(s, ref i, out width, out height))
                {
                    sized = true;
                }
                else
                {
                    return null;
                }
            }
            return new ImageRef(start, i + 1 - start, alt.ToString(), source, title, width, height);
        }

        /// <summary>Wiki.js's <c>=WxH</c> at <paramref name="i"/>: <c>=200x</c>, <c>=x120</c>, <c>=200x120</c>, each side 1 to 99999.</summary>
        private static bool TrySize(string s, ref int i, out double? width, out double? height)
        {
            width = null;
            height = null;
            int j = i + 1;
            int w = Digits(s, ref j);
            if (j >= s.Length || s[j] != 'x') return false;
            j++;
            int h = Digits(s, ref j);
            if ((w < 0 && h < 0) || w == 0 || h == 0) return false;
            if (j < s.Length && s[j] != ')' && !IsSpace(s[j])) return false;
            if (w > 0) width = w;
            if (h > 0) height = h;
            i = j;
            return true;
        }

        /// <summary>A run of 1 to 5 digits as a number; -1 when there is none, 0 when it is longer (no size).</summary>
        private static int Digits(string s, ref int j)
        {
            int start = j;
            while (j < s.Length && s[j] is >= '0' and <= '9') j++;
            int count = j - start;
            if (count == 0) return -1;
            if (count > 5) return 0;
            return int.Parse(s.AsSpan(start, count), NumberStyles.None, CultureInfo.InvariantCulture);
        }

        private static bool IsSpace(char c) => c is ' ' or '\t';

        private static void SkipSpaces(string s, ref int i)
        {
            while (i < s.Length && IsSpace(s[i])) i++;
        }

        /// <summary>Past a backtick code span (a run closed by a run of the same length), or past the run when it never closes.</summary>
        private static int SkipCode(string s, int i)
        {
            int n = 0;
            while (i + n < s.Length && s[i + n] == '`') n++;
            int j = i + n;
            while (j < s.Length)
            {
                if (s[j] != '`')
                {
                    j++;
                    continue;
                }
                int m = 0;
                while (j + m < s.Length && s[j + m] == '`') m++;
                if (m == n) return j + m;
                j += m;
            }
            return i + n;
        }
    }
}
