using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Pad
{
    /// <summary>What a Kroki server gave: the SVG, or an error that lasts (a syntax error) or passes (no answer).</summary>
    public sealed record KrokiResult(string? Svg, string? Error, bool Lasting);

    /// <summary>
    /// Asks a Kroki server to draw one diagram (spec section 3): <c>POST {server}/{type}/svg</c>
    /// with the block's text — only that — as <c>text/plain; charset=utf-8</c>, 10 s at most.
    /// A 400 answer's first line is the error; anything else that fails says the server could not
    /// be reached. Answers over 16 MB are refused (R7).
    /// </summary>
    public sealed class KrokiClient : IDisposable
    {
        public const string DefaultServer = "https://kroki.io";
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
        internal const int MaxAnswerBytes = 16 * 1024 * 1024;
        private const int MaxErrorLength = 500;

        private readonly HttpClient _http;

        /// <param name="handler">Tests pass a fake server; null uses the system's network settings.</param>
        public KrokiClient(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
        {
            _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            _http.Timeout = timeout ?? DefaultTimeout;
            _http.MaxResponseContentBufferSize = MaxAnswerBytes;
        }

        /// <summary>
        /// An http or https address with a host, no user name, query or fragment, without its
        /// trailing slash; false for anything else (Settings refuses it).
        /// </summary>
        public static bool TryParseServer(string? text, [NotNullWhen(true)] out string? server)
        {
            server = null;
            if (!Uri.TryCreate((text ?? "").Trim(), UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            if (uri.Host.Length == 0 || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
            server = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            return true;
        }

        /// <summary>The server's host (and port) as messages name it: "kroki.io", "localhost:8000".</summary>
        public static string HostOf(string server) =>
            Uri.TryCreate(server, UriKind.Absolute, out var uri) ? uri.Authority : server;

        /// <summary>Draws <paramref name="source"/> as a <paramref name="type"/> diagram. Never throws for a network failure.</summary>
        public async Task<KrokiResult> DrawAsync(string server, string type, string source, CancellationToken cancel = default)
        {
            string unreachable = DiagramText.Unreachable(HostOf(server));
            try
            {
                using var content = new StringContent(source, Encoding.UTF8, "text/plain");
                using var response = await _http.PostAsync(server.TrimEnd('/') + "/" + type + "/svg", content, cancel);
                string body = await response.Content.ReadAsStringAsync(cancel);
                if (response.IsSuccessStatusCode) return new KrokiResult(body, null, Lasting: true);
                if (response.StatusCode == HttpStatusCode.BadRequest)
                    return new KrokiResult(null, FirstLine(body) ?? DiagramText.Failed, Lasting: true);
                return new KrokiResult(null, unreachable, Lasting: false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                return new KrokiResult(null, unreachable, Lasting: false);
            }
        }

        public void Dispose() => _http.Dispose();

        private static string? FirstLine(string body)
        {
            foreach (string line in body.Split('\n'))
            {
                string text = line.Trim();
                if (text.Length > 0) return text.Length > MaxErrorLength ? text.Substring(0, MaxErrorLength) : text;
            }
            return null;
        }
    }
}
