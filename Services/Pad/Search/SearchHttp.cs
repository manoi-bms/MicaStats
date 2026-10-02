using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kil0bitSystemMonitor.Services.Pad.Search
{
    /// <summary>
    /// The one POST both clients make (spec 3.3): JSON in, JSON out, the system proxy, no
    /// redirects, answers over <see cref="MaxAnswerBytes"/> refused, a per-call timeout. Network
    /// problems become a <see cref="SearchFailure"/>; only the caller's cancellation throws.
    /// </summary>
    internal static class SearchHttp
    {
        public const int MaxAnswerBytes = 32 * 1024 * 1024;

        public static HttpClient CreateClient(HttpMessageHandler? handler)
        {
            var http = handler == null
                ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
                : new HttpClient(handler, disposeHandler: false);
            http.Timeout = Timeout.InfiniteTimeSpan;   // each call has its own
            return http;
        }

        public static SearchFailure Classify(int status) => status switch
        {
            401 or 403 => SearchFailure.KeyRefused,
            429 => SearchFailure.RateLimited,
            >= 300 and < 400 => SearchFailure.Unreachable,
            >= 500 => SearchFailure.ServerError,
            _ => SearchFailure.Rejected,
        };

        /// <summary>The parsed answer (the caller disposes it), or null with the failure.</summary>
        public static async Task<(JsonDocument? Json, SearchFailure Failure, int? Status)> PostAsync(
            HttpClient http, SearchServer server, string route, object body, TimeSpan timeout, CancellationToken cancel)
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timer.CancelAfter(timeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, server.BaseUrl.TrimEnd('/') + route)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrEmpty(server.Key))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Key);

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timer.Token).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode) return (null, Classify(status), status);

                using var stream = await response.Content.ReadAsStreamAsync(timer.Token).ConfigureAwait(false);
                using var answer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, timer.Token).ConfigureAwait(false)) > 0)
                {
                    if (answer.Length + read > MaxAnswerBytes) return (null, SearchFailure.BadAnswer, status);
                    answer.Write(chunk, 0, read);
                }

                try { return (JsonDocument.Parse(answer.ToArray()), SearchFailure.None, status); }
                catch (JsonException) { return (null, SearchFailure.BadAnswer, status); }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return (null, SearchFailure.TimedOut, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                return (null, SearchFailure.Unreachable, null);
            }
        }
    }
}
