using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Asking the provider for its models (spec 2026-10-05, 1.1 and 1.2). What comes back is data from a
    /// server the user pointed the app at, so every test here feeds it something a server could say.
    /// No test reaches the network: each request is answered by a handler in this file.
    /// In the Claude collection because the Claude client clears process-wide environment variables.
    /// </summary>
    [Collection("AnthropicEnv")]
    public class ModelCatalogTests
    {
        private const string Key = "sk-test-key-0123456789";
        private const string OtherKey = "sk-other-key-9876543210";

        private const string NotAList = "The server's answer was not a list of models.";
        private const string TooLong = "The server's answer was too long to read: over 4 MB.";
        private const string TimedOut = "The AI service did not answer for 15 seconds. Try again.";
        private const string Cancelled = "Loading the model list was cancelled.";
        private const string Failed = "The model list could not be loaded.";
        private const string KeyRejected = "The key was rejected. Check it in Settings > AI.";
        private const string Busy = "The AI service is busy or rate-limited. Wait a minute and try again.";
        private const string Unreachable = "Could not reach the AI service. Check the network, or the base URL in Settings > AI, and try again.";
        private const string NoKey = "Add an API key in Settings > AI.";
        private const string BadUrl = "Enter a valid http or https base URL in Settings > AI.";

        // ----- Helpers ------------------------------------------------------------------------

        /// <summary>Answers every request from a script and records what was asked. Never the network.</summary>
        private sealed class Recorder : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _answer;

            public Recorder(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) => _answer = answer;

            public sealed record Call(string Method, Uri Address, IReadOnlyDictionary<string, string> Headers)
            {
                public string Header(string name) => Headers.TryGetValue(name, out string? value) ? value : "";
            }

            public List<Call> Calls { get; } = new();

            public bool Disposed { get; private set; }

            public static Recorder Answering(string body, HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json") =>
                new((_, _) => Task.FromResult(Reply(body, status, contentType)));

            public static Recorder Failing(Func<Exception> failure) =>
                new((_, _) => Task.FromException<HttpResponseMessage>(failure()));

            public static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json")
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
                // A tiny retry-after, so a status the Claude SDK retries does not wait out its backoff.
                response.Headers.TryAddWithoutValidation("retry-after-ms", "1");
                response.Headers.TryAddWithoutValidation("retry-after", "0");
                return response;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
                lock (Calls) Calls.Add(new Call(request.Method.Method, request.RequestUri!, headers));
                return _answer(request, cancellationToken);
            }

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }

        /// <summary>A body with no declared length that never ends by itself (it gives up at 64 MB so a bug cannot hang the run).</summary>
        private sealed class EndlessSpaces : Stream
        {
            private const long GiveUpAt = 64L * 1024 * 1024;

            public long Given { get; private set; }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (Given >= GiveUpAt) return 0;
                Array.Fill(buffer, (byte)' ', offset, count);
                Given += count;
                return count;
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private static SecretStore Secrets(AiTestEnv env, string? claudeKey = null, string? compatibleKey = null)
        {
            var store = new SecretStore(env.PathOf("secrets.bin"), _ => { });
            if (claudeKey != null) store.Set(SecretNames.ClaudeKey, claudeKey);
            if (compatibleKey != null) store.Set(SecretNames.CompatibleKey, compatibleKey);
            return store;
        }

        private static AppConfig Compatible(string baseUrl, string model = "") => new()
        {
            AiProvider = AiProviders.OpenAiCompatible,
            AiCompatibleBaseUrl = baseUrl,
            AiCompatibleModel = model,
        };

        /// <summary>One model of a compatible list: an id, then any other members as written.</summary>
        private static string Model(string id, string members = "") =>
            "{\"id\":\"" + id + "\",\"object\":\"model\"" + (members.Length > 0 ? "," + members : "") + "}";

        private static string Data(params string[] models) => "{\"object\":\"list\",\"data\":[" + string.Join(",", models) + "]}";

        /// <summary>A JSON escape for one UTF-16 unit, written so that no escape appears in this file.</summary>
        private static string J(int unit) => "\\" + "u" + unit.ToString("x4", CultureInfo.InvariantCulture);

        private static string N(long number) => number.ToString(CultureInfo.InvariantCulture);

        private static string ClaudeModel(string id, long? window = 200_000, long? output = 64_000) =>
            "{\"type\":\"model\",\"id\":\"" + id + "\",\"display_name\":\"" + id + "\",\"created_at\":\"2026-02-19T00:00:00Z\"" +
            (window is long w ? ",\"max_input_tokens\":" + N(w) : "") +
            (output is long o ? ",\"max_tokens\":" + N(o) : "") + "}";

        private static string ClaudePage(bool more, string firstId, string lastId, params string[] models) =>
            "{\"data\":[" + string.Join(",", models) + "],\"has_more\":" + (more ? "true" : "false") +
            ",\"first_id\":\"" + firstId + "\",\"last_id\":\"" + lastId + "\"}";

        private static AiModelInfo OnlyModel(string json) => Assert.Single(ModelCatalog.ParseCompatible(json));

        /// <summary>A failed listing: no models, the sentence, the host, and never the key in what is shown.</summary>
        private static void AssertProblem(AiModelList list, string sentence, string host)
        {
            Assert.Empty(list.Models);
            Assert.Equal(sentence, list.Problem);
            Assert.Equal(host, list.Host);
            Assert.DoesNotContain(Key, list.Problem!, StringComparison.Ordinal);
            Assert.DoesNotContain(Key, list.Host, StringComparison.Ordinal);
        }

        // ----- The compatible list as it is parsed: field names ---------------------------------

        [Fact]
        public void A_real_vllm_answer_gives_the_id_and_the_window()
        {
            const string json =
                "{\"object\":\"list\",\"data\":[{\"id\":\"some-model\",\"object\":\"model\",\"created\":1791109799,\"owned_by\":\"vllm\"," +
                "\"root\":\"org/Some-Model\",\"parent\":null,\"max_model_len\":262144," +
                "\"permission\":[{\"id\":\"modelperm-x\",\"object\":\"model_permission\"}]}]}";

            Assert.Equal(new AiModelInfo("some-model", 262_144, 0), OnlyModel(json));
        }

        [Theory]
        [InlineData("max_model_len")]
        [InlineData("context_length")]
        [InlineData("context_window")]
        [InlineData("max_context_length")]
        [InlineData("max_input_tokens")]
        public void Each_window_field_name_is_read(string field)
        {
            Assert.Equal(32_768, OnlyModel(Data(Model("m", "\"" + field + "\":32768"))).ContextTokens);
        }

        [Fact]
        public void The_window_of_a_llama_cpp_server_is_read_from_meta()
        {
            Assert.Equal(131_072, OnlyModel(Data(Model("m", "\"meta\":{\"n_vocab\":32000,\"n_ctx_train\":131072}"))).ContextTokens);
            // meta of another shape is no window, not a failure.
            Assert.Equal(0, OnlyModel(Data(Model("m", "\"meta\":131072"))).ContextTokens);
            Assert.Equal(0, OnlyModel(Data(Model("m", "\"meta\":null"))).ContextTokens);
            Assert.Equal(0, OnlyModel(Data(Model("m", "\"meta\":[{\"n_ctx_train\":131072}]"))).ContextTokens);
        }

        [Theory]
        [InlineData(0, 10_000)]
        [InlineData(1, 20_000)]
        [InlineData(2, 30_000)]
        [InlineData(3, 40_000)]
        [InlineData(4, 50_000)]
        [InlineData(5, 60_000)]
        public void The_window_is_the_first_field_in_the_spec_order(int from, int expected)
        {
            // Written in reverse, so the order of the names decides and not the order in the answer.
            string[] members =
            {
                "\"max_model_len\":10000",
                "\"context_length\":20000",
                "\"context_window\":30000",
                "\"max_context_length\":40000",
                "\"max_input_tokens\":50000",
                "\"meta\":{\"n_ctx_train\":60000}",
            };
            string json = Data(Model("m", string.Join(",", members.Skip(from).Reverse())));

            Assert.Equal(expected, OnlyModel(json).ContextTokens);
        }

        [Theory]
        [InlineData("\"max_output_tokens\":4096")]
        [InlineData("\"max_completion_tokens\":4096")]
        [InlineData("\"top_provider\":{\"context_length\":8192,\"max_completion_tokens\":4096}")]
        public void Each_output_field_name_is_read(string member)
        {
            Assert.Equal(4096, OnlyModel(Data(Model("m", member))).MaxOutputTokens);
        }

        [Theory]
        [InlineData(0, 1000)]
        [InlineData(1, 2000)]
        [InlineData(2, 3000)]
        public void The_output_is_the_first_field_in_the_spec_order(int from, int expected)
        {
            string[] members =
            {
                "\"max_output_tokens\":1000",
                "\"max_completion_tokens\":2000",
                "\"top_provider\":{\"max_completion_tokens\":3000}",
            };
            string json = Data(Model("m", string.Join(",", members.Skip(from).Reverse())));

            Assert.Equal(expected, OnlyModel(json).MaxOutputTokens);
        }

        [Fact]
        public void An_openrouter_model_gives_both_numbers()
        {
            string json = Data(Model("vendor/model-x",
                "\"name\":\"Vendor: Model X\",\"context_length\":200000,\"pricing\":{\"prompt\":\"0.000003\"}," +
                "\"top_provider\":{\"context_length\":200000,\"max_completion_tokens\":64000,\"is_moderated\":false}"));

            Assert.Equal(new AiModelInfo("vendor/model-x", 200_000, 64_000), OnlyModel(json));
        }

        [Fact]
        public void A_server_that_says_nothing_about_size_gives_ids_with_no_limits()
        {
            string json = "{\"object\":\"list\",\"data\":[" +
                          "{\"id\":\"model-a\",\"object\":\"model\",\"created\":1686935002,\"owned_by\":\"library\"}," +
                          "{\"id\":\"model-b\",\"object\":\"model\",\"created\":1686935003,\"owned_by\":\"library\"}]}";

            Assert.Equal(new[] { new AiModelInfo("model-a", 0, 0), new AiModelInfo("model-b", 0, 0) }, ModelCatalog.ParseCompatible(json));
        }

        [Theory]
        [InlineData("null")]
        [InlineData("\"abc\"")]
        [InlineData("12")]
        [InlineData("-1")]
        [InlineData("1.5")]
        [InlineData("{}")]
        public void A_field_with_an_unusable_value_is_passed_over_for_the_next_one(string bad)
        {
            Assert.Equal(8192, OnlyModel(Data(Model("m", "\"max_model_len\":" + bad + ",\"context_length\":8192"))).ContextTokens);
            Assert.Equal(4096, OnlyModel(Data(Model("m", "\"max_output_tokens\":" + bad + ",\"max_completion_tokens\":4096"))).MaxOutputTokens);
        }

        // ----- The shape of the answer ----------------------------------------------------------

        [Fact]
        public void The_models_are_under_data_or_the_answer_is_itself_the_array()
        {
            string model = Model("m", "\"context_length\":8192");

            Assert.Equal(new AiModelInfo("m", 8192, 0), OnlyModel("{\"data\":[" + model + "]}"));
            Assert.Equal(new AiModelInfo("m", 8192, 0), OnlyModel("[" + model + "]"));
            Assert.Equal(new AiModelInfo("m", 8192, 0), OnlyModel("  \r\n [" + model + "] \n"));
        }

        [Theory]
        [InlineData("{\"data\":[]}")]
        [InlineData("{\"object\":\"list\",\"data\":[]}")]
        [InlineData("[]")]
        public void An_empty_list_is_a_list(string json)
        {
            Assert.True(ModelCatalog.TryParseCompatible(Encoding.UTF8.GetBytes(json), out IReadOnlyList<AiModelInfo> models));
            Assert.Empty(models);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("null")]
        [InlineData("true")]
        [InlineData("5")]
        [InlineData("\"models\"")]
        [InlineData("{}")]
        [InlineData("{\"data\":null}")]
        [InlineData("{\"data\":{}}")]
        [InlineData("{\"data\":\"m\"}")]
        [InlineData("{\"data\":5}")]
        [InlineData("{\"models\":[{\"id\":\"m\"}]}")]
        [InlineData("{\"error\":{\"message\":\"Not found\"}}")]
        [InlineData("<html><body><h1>404 Not Found</h1></body></html>")]
        [InlineData("<!DOCTYPE html><script>alert(1)</script>")]
        [InlineData("{\"data\":[")]
        [InlineData("[{\"id\":\"m\"}")]
        [InlineData("[{\"id\":\"m\"}] trailing")]
        [InlineData("{\"data\":[{\"id\":\"m\",\"context_length\":NaN}]}")]
        [InlineData("{\"data\":[{\"id\":\"m\",\"context_length\":Infinity}]}")]
        [InlineData("{'data':[]}")]
        public void What_is_not_a_list_of_models_gives_none_and_never_throws(string json)
        {
            Assert.False(ModelCatalog.TryParseCompatible(Encoding.UTF8.GetBytes(json), out IReadOnlyList<AiModelInfo> models));
            Assert.Empty(models);
            Assert.Empty(ModelCatalog.ParseCompatible(json));
        }

        [Fact]
        public void An_answer_nested_too_deep_or_of_invalid_bytes_gives_none_and_never_throws()
        {
            Assert.Empty(ModelCatalog.ParseCompatible(new string('[', 100_000)));
            Assert.Empty(ModelCatalog.ParseCompatible("{\"data\":" + new string('[', 5000) + new string(']', 5000) + "}"));

            // 0xFF is never valid UTF-8, inside a string or outside one.
            byte[] bytes = Encoding.UTF8.GetBytes("[{\"id\":\"m?\"}]");
            bytes[Array.IndexOf(bytes, (byte)'?')] = 0xFF;
            ModelCatalog.TryParseCompatible(bytes, out IReadOnlyList<AiModelInfo> models);
            Assert.Empty(models);

            Assert.False(ModelCatalog.TryParseCompatible(new byte[] { 0xFF, 0xFE, 0x00 }, out models));
            Assert.Empty(models);
            Assert.False(ModelCatalog.TryParseCompatible(ReadOnlyMemory<byte>.Empty, out models));
            Assert.Empty(models);
        }

        [Fact]
        public void An_odd_entry_is_left_out_and_the_others_are_kept()
        {
            string json = "{\"data\":[null,5,\"text\",true,[],{},{\"object\":\"model\"},{\"id\":null},{\"id\":7},{\"id\":{\"name\":\"m\"}},{\"id\":[\"m\"]}," +
                          "{\"id\":\"\"},{\"id\":\"   \"}," + Model("good-1", "\"context_length\":8192") + "," +
                          "{\"id\":\"good-2\",\"context_length\":{\"tokens\":8192},\"max_output_tokens\":[4096],\"top_provider\":\"none\",\"meta\":7}]}";

            Assert.Equal(new[] { new AiModelInfo("good-1", 8192, 0), new AiModelInfo("good-2", 0, 0) }, ModelCatalog.ParseCompatible(json));
        }

        // ----- Numbers ---------------------------------------------------------------------------

        [Theory]
        [InlineData("1024", 1024)]
        [InlineData("8192", 8192)]
        [InlineData("262144", 262_144)]
        [InlineData("2000000", 2_000_000)]
        [InlineData("2000001", 2_000_000)]                   // over two million counts as two million
        [InlineData("2147483648", 2_000_000)]                // past int
        [InlineData("9223372036854775807", 2_000_000)]       // the largest long
        [InlineData("\"8192\"", 8192)]                       // a string of digits
        [InlineData("\"0008192\"", 8192)]
        [InlineData("\"2000001\"", 2_000_000)]
        [InlineData("\"9223372036854775807\"", 2_000_000)]
        public void A_window_is_a_whole_number_from_1024_and_at_most_two_million(string written, int expected)
        {
            Assert.Equal(expected, OnlyModel(Data(Model("m", "\"context_length\":" + written))).ContextTokens);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-1")]
        [InlineData("-262144")]
        [InlineData("1")]
        [InlineData("1023")]
        [InlineData("1.5")]
        [InlineData("8192.5")]
        [InlineData("262144.0")]                             // written as a fraction: not a whole number as written
        [InlineData("1e5")]
        [InlineData("1e30")]
        [InlineData("1E400")]
        [InlineData("-1e30")]
        [InlineData("9223372036854775808")]                  // one past the largest long
        [InlineData("99999999999999999999999999999999")]
        [InlineData("null")]
        [InlineData("true")]
        [InlineData("false")]
        [InlineData("[]")]
        [InlineData("[8192]")]
        [InlineData("{}")]
        [InlineData("{\"tokens\":8192}")]
        [InlineData("\"\"")]
        [InlineData("\"abc\"")]
        [InlineData("\"NaN\"")]
        [InlineData("\"Infinity\"")]
        [InlineData("\"8192 tokens\"")]
        [InlineData("\" 8192\"")]
        [InlineData("\"8192 \"")]
        [InlineData("\"+8192\"")]
        [InlineData("\"-8192\"")]
        [InlineData("\"8,192\"")]
        [InlineData("\"8192.0\"")]
        [InlineData("\"8e3\"")]
        [InlineData("\"0x2000\"")]
        [InlineData("\"1023\"")]
        [InlineData("\"0\"")]
        [InlineData("\"9223372036854775808\"")]
        [InlineData("\"99999999999999999999999999999999\"")]
        public void Anything_else_as_a_window_is_not_reported(string written)
        {
            AiModelInfo model = OnlyModel(Data(Model("m", "\"context_length\":" + written)));

            Assert.Equal("m", model.Id);
            Assert.Equal(0, model.ContextTokens);
        }

        [Fact]
        public void Digits_of_another_script_are_not_a_number()
        {
            // Arabic-Indic and Thai digits: digits to a reader, not to the limits.
            string arabic = new string(new[] { (char)0x0668, (char)0x0661, (char)0x0669, (char)0x0662 });
            string thai = new string(new[] { (char)0x0E58, (char)0x0E51, (char)0x0E59, (char)0x0E52 });

            Assert.Equal(0, OnlyModel(Data(Model("m", "\"context_length\":\"" + arabic + "\""))).ContextTokens);
            Assert.Equal(0, OnlyModel(Data(Model("m", "\"context_length\":\"" + thai + "\""))).ContextTokens);
        }

        [Theory]
        [InlineData("256", 256)]
        [InlineData("4096", 4096)]
        [InlineData("\"4096\"", 4096)]
        [InlineData("2000000", 2_000_000)]
        [InlineData("3000000", 2_000_000)]
        [InlineData("9223372036854775807", 2_000_000)]
        [InlineData("0", 0)]
        [InlineData("-1", 0)]
        [InlineData("1", 0)]
        [InlineData("255", 0)]
        [InlineData("1.5", 0)]
        [InlineData("1e30", 0)]
        [InlineData("\"abc\"", 0)]
        [InlineData("null", 0)]
        [InlineData("9223372036854775808", 0)]
        public void An_output_is_a_whole_number_from_256_or_not_reported(string written, int expected)
        {
            // No window here, so nothing but the range bounds the output.
            Assert.Equal(expected, OnlyModel(Data(Model("m", "\"max_output_tokens\":" + written))).MaxOutputTokens);
        }

        [Theory]
        [InlineData(8192, 4096, 4096)]
        [InlineData(8192, 8192, 8192)]
        [InlineData(8192, 8193, 8192)]
        [InlineData(8192, 2_000_000, 8192)]
        [InlineData(1024, 4096, 1024)]
        public void An_output_is_never_above_the_window_when_both_are_known(int window, int output, int expected)
        {
            AiModelInfo model = OnlyModel(Data(Model("m", "\"context_length\":" + N(window) + ",\"max_output_tokens\":" + N(output))));

            Assert.Equal(window, model.ContextTokens);
            Assert.Equal(expected, model.MaxOutputTokens);
        }

        [Fact]
        public void No_number_a_server_can_write_passes_two_million_or_goes_negative()
        {
            string[] numbers =
            {
                "0", "-0", "1", "-1", "255", "256", "1023", "1024", "2000000", "2000001", "2147483647", "2147483648", "4294967296",
                "9223372036854775807", "9223372036854775808", "-9223372036854775808", "-9223372036854775809", "1e9", "1.0", "0.5",
                "\"2147483648\"", "\"4294967297\"", "\"18446744073709551616\"", "\"-5\"",
            };

            foreach (string window in numbers)
            {
                foreach (string output in numbers)
                {
                    AiModelInfo model = OnlyModel(Data(Model("m", "\"context_length\":" + window + ",\"max_output_tokens\":" + output)));
                    string at = " at " + window + " / " + output;

                    Assert.True(model.ContextTokens == 0 || (model.ContextTokens >= 1024 && model.ContextTokens <= 2_000_000), "window" + at);
                    Assert.True(model.MaxOutputTokens == 0 || (model.MaxOutputTokens >= 256 && model.MaxOutputTokens <= 2_000_000), "output" + at);
                    Assert.True(model.ContextTokens == 0 || model.MaxOutputTokens <= model.ContextTokens, "output over the window" + at);
                }
            }
        }

        // ----- Ids -------------------------------------------------------------------------------

        [Fact]
        public void An_ordinary_id_is_kept_as_it_is()
        {
            Assert.Equal("org/Some-Model_v2.5:latest@q4 (8bit)", OnlyModel(Data(Model("org/Some-Model_v2.5:latest@q4 (8bit)"))).Id);
            // Thai, with its vowel and tone marks, is text like any other.
            const string thai = "โมเดล-ภาษาไทย-น้ำ";
            Assert.Equal(thai, OnlyModel(Data(Model(thai))).Id);
            Assert.Equal(thai, ModelCatalog.CleanId(thai));
        }

        [Fact]
        public void An_id_is_text_and_markup_in_it_is_kept_as_text()
        {
            // Nothing is stripped or decoded: the settings page shows an id as plain text.
            const string id = "<b onclick=alert(1)>model</b>&amp;{{secret:1}}";

            Assert.Equal(id, OnlyModel(Data(Model(id))).Id);
        }

        [Fact]
        public void Control_characters_are_removed_from_an_id()
        {
            string escaped = "mo" + J(0x0000) + J(0x0007) + "\\n\\r\\t" + J(0x001B) + "[31m" + J(0x007F) + J(0x0085) + J(0x009F) + "del";

            Assert.Equal("mo[31mdel", OnlyModel(Data(Model(escaped))).Id);

            var all = new StringBuilder("a");
            for (int c = 0; c < 0x20; c++) all.Append((char)c);
            for (int c = 0x7F; c <= 0x9F; c++) all.Append((char)c);
            all.Append('z');
            Assert.Equal("az", ModelCatalog.CleanId(all.ToString()));
        }

        [Theory]
        [InlineData(0x00AD)]   // soft hyphen
        [InlineData(0x061C)]   // Arabic letter mark
        [InlineData(0x200B)]   // zero width space
        [InlineData(0x200D)]   // zero width joiner
        [InlineData(0x200E)]   // left-to-right mark
        [InlineData(0x200F)]   // right-to-left mark
        [InlineData(0x202A)]   // left-to-right embedding
        [InlineData(0x202D)]   // left-to-right override
        [InlineData(0x202E)]   // right-to-left override
        [InlineData(0x2060)]   // word joiner
        [InlineData(0x2066)]   // left-to-right isolate
        [InlineData(0x2069)]   // pop directional isolate
        [InlineData(0xFEFF)]   // byte order mark
        [InlineData(0x2028)]   // line separator
        [InlineData(0x2029)]   // paragraph separator
        public void Format_characters_and_line_separators_are_removed_from_an_id(int unit)
        {
            // Both as a JSON escape and as the character itself.
            Assert.Equal("model-a", OnlyModel(Data(Model("mod" + J(unit) + "el-a"))).Id);
            Assert.Equal("model-a", OnlyModel(Data(Model("mod" + (char)unit + "el-a"))).Id);
            Assert.Equal("model-a", ModelCatalog.CleanId((char)unit + "model" + (char)unit + "-a" + (char)unit));
        }

        [Fact]
        public void A_format_character_outside_the_basic_plane_is_removed_too()
        {
            // U+E0001 (language tag) and U+E0041 (tag letter A) are format characters written as surrogate pairs.
            string tags = char.ConvertFromUtf32(0xE0001) + char.ConvertFromUtf32(0xE0041);

            Assert.Equal("model", ModelCatalog.CleanId("mo" + tags + "del"));
            Assert.Equal("model", OnlyModel(Data(Model("mo" + tags + "del"))).Id);
        }

        [Fact]
        public void A_character_outside_the_basic_plane_that_is_text_is_kept()
        {
            string id = "model-" + char.ConvertFromUtf32(0x1F600) + "-" + char.ConvertFromUtf32(0x20BB7);

            Assert.Equal(id, OnlyModel(Data(Model(id))).Id);
            Assert.Equal(id, OnlyModel(Data(Model("model-" + J(0xD83D) + J(0xDE00) + "-" + J(0xD842) + J(0xDFB7)))).Id);
        }

        [Fact]
        public void Half_a_surrogate_pair_never_ends_up_in_an_id()
        {
            // As text handed to the cleaning: the half is dropped.
            Assert.Equal("model", ModelCatalog.CleanId("mo" + (char)0xD83D + "del"));
            Assert.Equal("model", ModelCatalog.CleanId("mo" + (char)0xDE00 + "del" + (char)0xD83D));
            Assert.Equal("", ModelCatalog.CleanId(new string((char)0xD83D, 3)));

            // As a JSON escape it is not text at all: that model is left out, the others stay.
            IReadOnlyList<AiModelInfo> models = ModelCatalog.ParseCompatible(Data(Model("mo" + J(0xD83D) + "del"), Model("good")));
            Assert.Equal("good", Assert.Single(models).Id);
        }

        [Fact]
        public void Surrounding_white_space_is_trimmed_from_an_id()
        {
            Assert.Equal("model a", OnlyModel(Data(Model("   model a   "))).Id);
            Assert.Equal("model", ModelCatalog.CleanId((char)0x00A0 + " " + (char)0x3000 + "model" + (char)0x2003 + " "));
            // White space that only shows once the characters around it are gone.
            Assert.Equal("model", ModelCatalog.CleanId((char)0x202E + "  model  " + (char)0x200B));
            Assert.Equal("model", ModelCatalog.CleanId("\tmodel\r\n"));
        }

        [Fact]
        public void An_id_is_at_most_200_characters()
        {
            Assert.Equal(new string('m', 200), OnlyModel(Data(Model(new string('m', 5000)))).Id);
            Assert.Equal(new string('m', 200), OnlyModel(Data(Model(new string('m', 200)))).Id);
            Assert.Equal(new string('m', 199), OnlyModel(Data(Model(new string('m', 199)))).Id);
            Assert.Equal(new string('m', 200), ModelCatalog.CleanId(new string('m', 4_000_000)));

            // Characters that are removed do not count towards the 200.
            string padded = string.Concat(Enumerable.Repeat((char)0x200B + "m", 300));
            Assert.Equal(new string('m', 200), ModelCatalog.CleanId(padded));

            // The cut does not leave white space at the end.
            Assert.Equal(new string('m', 190), ModelCatalog.CleanId(new string('m', 190) + new string(' ', 20) + "tail"));
        }

        [Fact]
        public void An_id_cut_at_200_never_ends_in_half_a_surrogate_pair()
        {
            string pair = char.ConvertFromUtf32(0x1F600);

            // The pair would be units 200 and 201: it is left out whole.
            string cut = ModelCatalog.CleanId(new string('m', 199) + pair + "tail");
            Assert.Equal(new string('m', 199), cut);

            // The pair is units 199 and 200: it fits whole.
            string fits = ModelCatalog.CleanId(new string('m', 198) + pair + "tail");
            Assert.Equal(new string('m', 198) + pair, fits);
            Assert.Equal(200, fits.Length);

            // A long run of pairs: every unit kept has its other half.
            string run = ModelCatalog.CleanId("m" + string.Concat(Enumerable.Repeat(pair, 400)));
            Assert.Equal(199, run.Length);
            Assert.False(char.IsHighSurrogate(run[^1]));
            Assert.Equal(run, new string(run.EnumerateRunes().SelectMany(r => r.ToString()).ToArray()));
            Assert.Equal(-1, run.IndexOf((char)0xFFFD));

            Assert.Equal(cut, OnlyModel(Data(Model(new string('m', 199) + pair + "tail"))).Id);
        }

        [Fact]
        public void An_id_that_is_empty_after_cleaning_drops_the_model()
        {
            string json = Data(
                Model(""),
                Model("   "),
                Model(J(0x0000) + J(0x0007)),
                Model(J(0x202E) + " " + J(0x200B)),
                Model("kept"));

            Assert.Equal("kept", Assert.Single(ModelCatalog.ParseCompatible(json)).Id);
            Assert.Equal("", ModelCatalog.CleanId(null));
            Assert.Equal("", ModelCatalog.CleanId(""));
        }

        [Fact]
        public void A_second_model_with_the_same_id_is_dropped()
        {
            string json = Data(
                Model("a", "\"context_length\":8192"),
                Model("b"),
                Model("a", "\"context_length\":32768"),
                Model(" a "),                               // the same id once cleaned
                Model("a" + J(0x200B)),
                Model("A"));                                // another id: case matters

            Assert.Equal(new[] { new AiModelInfo("a", 8192, 0), new AiModelInfo("b", 0, 0), new AiModelInfo("A", 0, 0) },
                         ModelCatalog.ParseCompatible(json));
        }

        // ----- How many -------------------------------------------------------------------------

        [Fact]
        public void At_most_500_models_are_kept_in_the_provider_s_order()
        {
            var json = new StringBuilder("{\"object\":\"list\",\"data\":[");
            for (int i = 0; i < 10_000; i++)
            {
                if (i > 0) json.Append(',');
                json.Append(Model("model-" + N(i), "\"context_length\":" + N(8192 + i)));
            }
            json.Append("]}");
            string text = json.ToString();
            ModelCatalog.ParseCompatible(Data(Model("warm-up")));

            var watch = Stopwatch.StartNew();
            IReadOnlyList<AiModelInfo> models = ModelCatalog.ParseCompatible(text);
            watch.Stop();

            Assert.Equal(500, models.Count);
            Assert.Equal(ModelCatalog.MaxModels, models.Count);
            for (int i = 0; i < models.Count; i++) Assert.Equal(new AiModelInfo("model-" + N(i), 8192 + i, 0), models[i]);
            // A generous bound: it takes a few milliseconds.
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "10,000 models took " + watch.Elapsed);
        }

        [Fact]
        public void Entries_that_are_dropped_do_not_count_towards_the_500()
        {
            var json = new StringBuilder("[");
            for (int i = 0; i < 2000; i++) json.Append("null,{\"id\":\"\"},").Append(Model("same")).Append(',');
            for (int i = 0; i < 600; i++) json.Append(Model("model-" + N(i))).Append(i < 599 ? "," : "");
            json.Append(']');

            IReadOnlyList<AiModelInfo> models = ModelCatalog.ParseCompatible(json.ToString());

            Assert.Equal(500, models.Count);
            Assert.Equal("same", models[0].Id);
            Assert.Equal("model-498", models[499].Id);
        }

        // ----- The request to a compatible server ---------------------------------------------

        [Fact]
        public async Task The_request_is_a_get_of_the_base_url_with_models_added()
        {
            using var env = new AiTestEnv();
            var handler = Recorder.Answering(Data(Model("llama3.2")));

            AiModelList list = await ModelCatalog.ListAsync(Compatible("http://localhost:11434/v1"), Secrets(env), handler, CancellationToken.None);

            Recorder.Call call = Assert.Single(handler.Calls);
            Assert.Equal("GET", call.Method);
            Assert.Equal("http://localhost:11434/v1/models", call.Address.AbsoluteUri);
            Assert.Equal("application/json", call.Header("Accept"));
            Assert.Null(list.Problem);
            Assert.Equal("localhost", list.Host);
            Assert.Equal(new AiModelInfo("llama3.2", 0, 0), Assert.Single(list.Models));
        }

        [Theory]
        [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1/models")]
        [InlineData("http://localhost:11434/v1/", "http://localhost:11434/v1/models")]
        [InlineData("https://llm.example.com/api/openai/v1", "https://llm.example.com/api/openai/v1/models")]
        [InlineData("https://llm.example.com/api/openai/v1/", "https://llm.example.com/api/openai/v1/models")]
        [InlineData("https://llm.example.com", "https://llm.example.com/models")]
        [InlineData("https://llm.example.com/", "https://llm.example.com/models")]
        [InlineData("https://LLM.Example.com:8443/V1", "https://llm.example.com:8443/V1/models")]
        [InlineData("  https://llm.example.com/v1  ", "https://llm.example.com/v1/models")]
        [InlineData("https://llm.example.com/v1?api-version=2026-01", "https://llm.example.com/v1/models?api-version=2026-01")]
        [InlineData("https://llm.example.com/v1/#section", "https://llm.example.com/v1/models")]
        public async Task One_trailing_slash_is_removed_and_a_path_is_kept(string baseUrl, string asked)
        {
            using var env = new AiTestEnv();
            var handler = Recorder.Answering(Data(Model("m")));

            AiModelList list = await ModelCatalog.ListAsync(Compatible(baseUrl), Secrets(env), handler, CancellationToken.None);

            Assert.Equal(asked, Assert.Single(handler.Calls).Address.AbsoluteUri);
            Assert.Null(list.Problem);
        }

        [Fact]
        public async Task A_saved_key_is_sent_as_a_bearer_token()
        {
            using var env = new AiTestEnv();
            var handler = Recorder.Answering(Data(Model("m")));

            await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env, compatibleKey: "  " + Key + " \n"), handler, CancellationToken.None);

            Assert.Equal("Bearer " + Key, Assert.Single(handler.Calls).Header("Authorization"));
        }

        [Fact]
        public async Task Without_a_saved_key_no_authorization_is_sent_at_all()
        {
            using var env = new AiTestEnv();
            var handler = Recorder.Answering(Data(Model("m")));

            // A Claude key is saved, and it is not this server's.
            AiModelList list = await ModelCatalog.ListAsync(Compatible("http://localhost:11434/v1"), Secrets(env, claudeKey: OtherKey), handler, CancellationToken.None);

            Recorder.Call call = Assert.Single(handler.Calls);
            Assert.False(call.Headers.ContainsKey("Authorization"));     // not the chat client's placeholder "Bearer none" either
            Assert.DoesNotContain(OtherKey, string.Concat(call.Headers.Values), StringComparison.Ordinal);
            Assert.Null(list.Problem);
        }

        [Fact]
        public async Task Nothing_that_names_the_user_or_the_app_is_sent()
        {
            using var env = new AiTestEnv();
            using var noKeyEnv = new AiTestEnv();       // its own key file: nothing is saved in it
            var withKey = Recorder.Answering(Data(Model("m")));
            var withoutKey = Recorder.Answering(Data(Model("m")));

            await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1", "some-model"), Secrets(env, compatibleKey: Key), withKey, CancellationToken.None);
            await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1", "some-model"), Secrets(noKeyEnv), withoutKey, CancellationToken.None);

            Assert.Equal(new[] { "Accept", "Authorization" }, Assert.Single(withKey.Calls).Headers.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal(new[] { "Accept" }, Assert.Single(withoutKey.Calls).Headers.Keys);
            // The model that is configured is not part of the question either.
            Assert.DoesNotContain("some-model", Assert.Single(withKey.Calls).Address.AbsoluteUri, StringComparison.Ordinal);
        }

        [Fact]
        public async Task The_host_asked_is_the_configured_one_and_the_key_goes_to_no_other()
        {
            using var env = new AiTestEnv();
            // The answer names other hosts: they are data, never an address to ask.
            var handler = Recorder.Answering(Data(Model("m",
                "\"root\":\"https://elsewhere.example.net/v1/models\",\"next\":\"https://elsewhere.example.net/v1/models?page=2\"")));

            AiModelList list = await ModelCatalog.ListAsync(Compatible("https://llm.example.com:8443/v1"), Secrets(env, claudeKey: OtherKey, compatibleKey: Key), handler, CancellationToken.None);

            Recorder.Call call = Assert.Single(handler.Calls);
            Assert.Equal("https", call.Address.Scheme);
            Assert.Equal("llm.example.com", call.Address.Host);
            Assert.Equal(8443, call.Address.Port);
            Assert.Equal("Bearer " + Key, call.Header("Authorization"));
            Assert.DoesNotContain(OtherKey, string.Concat(call.Headers.Values), StringComparison.Ordinal);
            Assert.Equal("llm.example.com", list.Host);
            Assert.Null(list.Problem);
        }

        [Theory]
        [InlineData(301)]
        [InlineData(302)]
        [InlineData(307)]
        [InlineData(308)]
        public async Task A_redirect_is_not_followed(int status)
        {
            using var env = new AiTestEnv();
            var handler = new Recorder((_, _) =>
            {
                HttpResponseMessage response = Recorder.Reply("", (HttpStatusCode)status);
                response.Headers.Location = new Uri("https://elsewhere.example.net/v1/models");
                return Task.FromResult(response);
            });

            AiModelList list = await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env, compatibleKey: Key), handler, CancellationToken.None);

            Assert.Equal("llm.example.com", Assert.Single(handler.Calls).Address.Host);
            AssertProblem(list, "The AI service said: HTTP " + N(status) + ".", "llm.example.com");
        }

        [Fact]
        public void The_network_handler_of_a_list_request_follows_no_redirect()
        {
            using HttpMessageHandler handler = ModelCatalog.NewNetworkHandler();

            Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
        }

        [Fact]
        public async Task A_handler_given_by_the_caller_is_never_disposed()
        {
            using var env = new AiTestEnv();
            var compatible = Recorder.Answering(Data(Model("m")));
            var claude = Recorder.Answering(ClaudePage(false, "claude-a", "claude-a", ClaudeModel("claude-a")));

            await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env), compatible, CancellationToken.None);
            await ModelCatalog.ListAsync(new AppConfig(), Secrets(env, claudeKey: Key), claude, CancellationToken.None);

            Assert.Single(compatible.Calls);
            Assert.Single(claude.Calls);
            Assert.False(compatible.Disposed);
            Assert.False(claude.Disposed);
        }

        [Fact]
        public async Task A_compatible_server_is_listed_before_any_model_is_chosen()
        {
            using var env = new AiTestEnv();
            var handler = Recorder.Answering(Data(Model("model-a", "\"max_model_len\":262144"), Model("model-b")));

            AiModelList list = await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1", model: ""), Secrets(env), handler, CancellationToken.None);

            Assert.Null(list.Problem);
            Assert.Equal(new[] { new AiModelInfo("model-a", 262_144, 0), new AiModelInfo("model-b", 0, 0) }, list.Models);
        }

        [Theory]
        [InlineData("ftp://llm.example.com/v1")]
        [InlineData("file:///C:/models")]
        [InlineData("llm.example.com/v1")]
        [InlineData("//llm.example.com/v1")]
        [InlineData("javascript:alert(1)")]
        [InlineData("http://")]
        public async Task A_base_url_that_is_not_http_or_https_sends_nothing(string baseUrl)
        {
            using var env = new AiTestEnv();
            var handler = Recorder.Answering(Data(Model("m")));

            AiModelList list = await ModelCatalog.ListAsync(Compatible(baseUrl), Secrets(env, compatibleKey: Key), handler, CancellationToken.None);

            Assert.Empty(handler.Calls);
            AssertProblem(list, BadUrl, "");
        }

        // ----- The answer of a compatible server ----------------------------------------------

        [Fact]
        public async Task A_byte_order_mark_before_the_answer_is_accepted()
        {
            using var env = new AiTestEnv();
            var handler = Recorder.Answering((char)0xFEFF + Data(Model("m", "\"context_length\":8192")));

            AiModelList list = await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env), handler, CancellationToken.None);

            Assert.Null(list.Problem);
            Assert.Equal(new AiModelInfo("m", 8192, 0), Assert.Single(list.Models));
        }

        [Fact]
        public async Task The_content_type_does_not_decide_what_is_a_list()
        {
            using var env = new AiTestEnv();
            var handler = Recorder.Answering(Data(Model("m")), contentType: "text/plain");

            AiModelList list = await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env), handler, CancellationToken.None);

            Assert.Null(list.Problem);
            Assert.Equal("m", Assert.Single(list.Models).Id);
        }

        [Theory]
        [InlineData("")]
        [InlineData("null")]
        [InlineData("\"models\"")]
        [InlineData("5")]
        [InlineData("{}")]
        [InlineData("{\"data\":{\"id\":\"m\"}}")]
        [InlineData("<html><head><title>Sign in</title></head><body><form action=\"/login\"></form></body></html>")]
        [InlineData("{\"data\":[{\"id\":\"m\",\"context_length\":NaN}]}")]
        [InlineData("{\"data\":[{\"id\":\"m\"}")]
        public async Task An_answer_that_is_not_a_list_is_said_in_a_sentence(string body)
        {
            using var env = new AiTestEnv();
            var handler = Recorder.Answering(body, contentType: body.StartsWith('<') ? "text/html" : "application/json");

            AiModelList list = await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env, compatibleKey: Key), handler, CancellationToken.None);

            Assert.Single(handler.Calls);
            AssertProblem(list, NotAList, "llm.example.com");
        }

        [Fact]
        public async Task An_empty_list_is_no_problem()
        {
            using var env = new AiTestEnv();
            var handler = Recorder.Answering("{\"object\":\"list\",\"data\":[]}");

            AiModelList list = await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env), handler, CancellationToken.None);

            Assert.Null(list.Problem);
            Assert.Empty(list.Models);
            Assert.Equal("llm.example.com", list.Host);
        }

        [Fact]
        public async Task An_answer_of_exactly_4_MB_is_read_and_one_byte_more_is_not()
        {
            using var env = new AiTestEnv();
            string model = Model("m", "\"context_length\":8192");
            string Padded(int length) => "{\"data\":[" + model + new string(' ', length - 11 - model.Length) + "]}";
            string atTheLimit = Padded(ModelCatalog.MaxAnswerBytes);
            string over = Padded(ModelCatalog.MaxAnswerBytes + 1);
            Assert.Equal(ModelCatalog.MaxAnswerBytes, Encoding.UTF8.GetByteCount(atTheLimit));
            Assert.Equal(4 * 1024 * 1024, ModelCatalog.MaxAnswerBytes);

            AiModelList read = await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env), Recorder.Answering(atTheLimit), CancellationToken.None);
            AiModelList refused = await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env, compatibleKey: Key), Recorder.Answering(over), CancellationToken.None);

            Assert.Null(read.Problem);
            Assert.Equal(new AiModelInfo("m", 8192, 0), Assert.Single(read.Models));
            AssertProblem(refused, TooLong, "llm.example.com");
        }

        [Fact]
        public async Task An_answer_with_no_declared_length_is_read_no_further_than_4_MB()
        {
            using var env = new AiTestEnv();
            var body = new EndlessSpaces();
            var handler = new Recorder((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));

            AiModelList list = await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env, compatibleKey: Key), handler, CancellationToken.None);

            AssertProblem(list, TooLong, "llm.example.com");
            Assert.True(body.Given > 0, "the body was not read at all");
            Assert.True(body.Given <= ModelCatalog.MaxAnswerBytes + 1024 * 1024, "read " + N(body.Given) + " bytes of an endless answer");
        }

        [Fact]
        public async Task An_answer_that_declares_50_MB_is_not_read()
        {
            using var env = new AiTestEnv();
            var body = new EndlessSpaces();
            var handler = new Recorder((_, _) =>
            {
                var content = new StreamContent(body);
                content.Headers.ContentLength = 50L * 1024 * 1024;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });

            AiModelList list = await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env, compatibleKey: Key), handler, CancellationToken.None);

            AssertProblem(list, TooLong, "llm.example.com");
            Assert.Equal(0, body.Given);
        }

        // ----- Failures of a compatible server --------------------------------------------------

        [Theory]
        [InlineData(401, KeyRejected)]
        [InlineData(403, "The AI service refused the request (403). Check that this key may use this model.")]
        [InlineData(404, "The AI service said: HTTP 404.")]
        [InlineData(400, "The AI service said: HTTP 400.")]
        [InlineData(500, "The AI service said: HTTP 500.")]
        [InlineData(502, "The AI service said: HTTP 502.")]
        [InlineData(429, Busy)]
        [InlineData(503, Busy)]
        [InlineData(204, NotAList)]
        public async Task A_status_is_said_in_a_sentence_without_the_servers_own_words(int status, string sentence)
        {
            using var env = new AiTestEnv();
            // The server quotes the key and adds text of its own: neither may be shown.
            string body = "{\"error\":{\"message\":\"Incorrect API key provided: " + Key + ". <b>Visit</b> https://elsewhere.example.net/keys\",\"type\":\"invalid_request_error\"}}";
            var handler = Recorder.Answering(status == 204 ? "" : body, (HttpStatusCode)status);

            AiModelList list = await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env, compatibleKey: Key), handler, CancellationToken.None);

            Assert.Single(handler.Calls);       // asked once: a list request is not retried
            AssertProblem(list, sentence, "llm.example.com");
            Assert.DoesNotContain("elsewhere", list.Problem!, StringComparison.Ordinal);
            Assert.DoesNotContain("Incorrect", list.Problem!, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_connection_failure_is_said_in_a_sentence()
        {
            using var env = new AiTestEnv();
            var handler = Recorder.Failing(() => new HttpRequestException(
                "No connection could be made because the target machine actively refused it. (llm.example.com:443) " + Key));

            AiModelList list = await ModelCatalog.ListAsync(Compatible("https://llm.example.com/secret-path/v1?token=abc"), Secrets(env, compatibleKey: Key), handler, CancellationToken.None);

            AssertProblem(list, Unreachable, "llm.example.com");
        }

        [Fact]
        public async Task A_connection_that_breaks_while_the_answer_is_read_is_said_in_a_sentence()
        {
            using var env = new AiTestEnv();
            var handler = new Recorder((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) }));

            AiModelList list = await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env, compatibleKey: Key), handler, CancellationToken.None);

            AssertProblem(list, Unreachable, "llm.example.com");
        }

        private sealed class BrokenStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new IOException("The connection was reset. " + Key);
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        [Fact]
        public async Task A_timeout_of_the_http_client_is_said_in_a_sentence()
        {
            using var env = new AiTestEnv();
            // What HttpClient throws when its own timeout passes.
            var handler = Recorder.Failing(() => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.",
                new TimeoutException("A task was canceled. " + Key)));

            AiModelList list = await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env, compatibleKey: Key), handler, CancellationToken.None);

            AssertProblem(list, TimedOut, "llm.example.com");
            Assert.Equal(TimeSpan.FromSeconds(15), ModelCatalog.ListTimeout);
        }

        [Fact]
        public async Task A_server_that_never_answers_is_given_up_on_at_the_deadline()
        {
            using var env = new AiTestEnv();
            bool cancelled = false;
            var handler = new Recorder(async (_, ct) =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { cancelled = true; throw; }
                return Recorder.Reply(Data(Model("m")));
            });
            var watch = Stopwatch.StartNew();

            AiModelList list = await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env, compatibleKey: Key), handler,
                warn: null, timeout: TimeSpan.FromMilliseconds(300), CancellationToken.None);

            AssertProblem(list, TimedOut, "llm.example.com");
            Assert.True(cancelled, "the request was not cancelled at the deadline");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "took " + watch.Elapsed);
        }

        [Fact]
        public async Task A_body_that_never_ends_arriving_is_given_up_on_at_the_deadline()
        {
            using var env = new AiTestEnv();
            var handler = new Recorder((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) }));
            var watch = Stopwatch.StartNew();

            AiModelList list = await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env, compatibleKey: Key), handler,
                warn: null, timeout: TimeSpan.FromMilliseconds(300), CancellationToken.None);

            AssertProblem(list, TimedOut, "llm.example.com");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "took " + watch.Elapsed);
        }

        /// <summary>Headers arrived, the body never does: a read waits until it is cancelled.</summary>
        private sealed class StalledStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        [Fact]
        public async Task A_caller_that_cancels_gets_a_sentence_and_no_exception()
        {
            using var env = new AiTestEnv();
            using var cancel = new CancellationTokenSource();
            var handler = new Recorder(async (_, ct) =>
            {
                cancel.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Recorder.Reply(Data(Model("m")));
            });

            AiModelList list = await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env, compatibleKey: Key), handler, cancel.Token);

            AssertProblem(list, Cancelled, "llm.example.com");
        }

        [Fact]
        public async Task A_token_that_is_already_cancelled_sends_nothing()
        {
            using var env = new AiTestEnv();
            var compatible = Recorder.Answering(Data(Model("m")));
            var claude = Recorder.Answering(ClaudePage(false, "claude-a", "claude-a", ClaudeModel("claude-a")));

            AiModelList first = await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env, compatibleKey: Key), compatible, new CancellationToken(canceled: true));
            AiModelList second = await ModelCatalog.ListAsync(new AppConfig(), Secrets(env, claudeKey: Key), claude, new CancellationToken(canceled: true));

            Assert.Empty(compatible.Calls);
            Assert.Empty(claude.Calls);
            AssertProblem(first, Cancelled, "llm.example.com");
            AssertProblem(second, Cancelled, "api.anthropic.com");
        }

        [Fact]
        public async Task A_failure_nobody_expected_is_a_sentence_without_its_message()
        {
            using var env = new AiTestEnv();
            var handler = Recorder.Failing(() => new InvalidOperationException("Something odd at https://llm.example.com/secret-path?token=abc with " + Key));

            AiModelList list = await ModelCatalog.ListAsync(Compatible("https://llm.example.com/secret-path/v1?token=abc"), Secrets(env, compatibleKey: Key), handler, CancellationToken.None);

            AssertProblem(list, Failed, "llm.example.com");
        }

        [Fact]
        public async Task Every_problem_is_a_sentence_that_ends_in_a_full_stop()
        {
            using var env = new AiTestEnv();
            var problems = new List<string?>();
            foreach (int status in new[] { 301, 400, 401, 403, 404, 418, 429, 500, 503, 529 })
            {
                problems.Add((await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env), Recorder.Answering("{}", (HttpStatusCode)status), CancellationToken.None)).Problem);
            }
            problems.Add((await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env), Recorder.Answering("<html>"), CancellationToken.None)).Problem);
            problems.Add((await ModelCatalog.ListAsync(Compatible("ftp://llm.example.com"), Secrets(env), Recorder.Answering("[]"), CancellationToken.None)).Problem);
            problems.Add((await ModelCatalog.ListAsync(new AppConfig(), Secrets(env), Recorder.Answering("[]"), CancellationToken.None)).Problem);
            problems.Add((await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env), Recorder.Failing(() => new HttpRequestException("x")), CancellationToken.None)).Problem);
            problems.Add((await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env), Recorder.Failing(() => new NotSupportedException("x")), CancellationToken.None)).Problem);

            // The settings page puts "You can still type a model name." after it.
            Assert.All(problems, problem =>
            {
                Assert.False(string.IsNullOrWhiteSpace(problem));
                Assert.EndsWith(".", problem, StringComparison.Ordinal);
                Assert.DoesNotContain("\n", problem!, StringComparison.Ordinal);
            });
        }

        // ----- What is told to the log -------------------------------------------------------

        [Fact]
        public async Task A_failure_is_told_by_its_type_and_the_host_only()
        {
            using var env = new AiTestEnv();
            var lines = new List<string>();
            AppConfig config = Compatible("https://user:pass@llm.example.com:8443/secret-path/v1?token=abc", "some-model");

            await ModelCatalog.ListAsync(config, Secrets(env, compatibleKey: Key),
                Recorder.Failing(() => new HttpRequestException("refused (llm.example.com:8443) " + Key)), lines.Add, CancellationToken.None);
            await ModelCatalog.ListAsync(config, Secrets(env, compatibleKey: Key),
                Recorder.Answering("{\"error\":\"" + Key + "\"}", HttpStatusCode.NotFound), lines.Add, CancellationToken.None);
            await ModelCatalog.ListAsync(config, Secrets(env, compatibleKey: Key),
                Recorder.Answering("<html>" + Key + "</html>"), lines.Add, CancellationToken.None);

            Assert.Equal(new[]
            {
                "Listing the models of llm.example.com failed (HttpRequestException)",
                "Listing the models of llm.example.com failed (HTTP 404)",
                "Listing the models of llm.example.com failed (not a list)",
            }, lines);
        }

        [Fact]
        public async Task A_listing_that_works_or_was_never_sent_tells_the_log_nothing()
        {
            using var env = new AiTestEnv();
            var lines = new List<string>();
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();

            await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env), Recorder.Answering(Data(Model("m"))), lines.Add, CancellationToken.None);
            await ModelCatalog.ListAsync(Compatible("ftp://llm.example.com/v1"), Secrets(env), Recorder.Answering("[]"), lines.Add, CancellationToken.None);
            await ModelCatalog.ListAsync(new AppConfig(), Secrets(env), Recorder.Answering("[]"), lines.Add, CancellationToken.None);
            await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env), Recorder.Answering("[]"), lines.Add, cancel.Token);

            Assert.Empty(lines);
        }

        [Fact]
        public async Task A_log_callback_that_throws_does_not_break_the_listing()
        {
            using var env = new AiTestEnv();

            AiModelList list = await ModelCatalog.ListAsync(Compatible("https://llm.example.com/v1"), Secrets(env),
                Recorder.Answering("", HttpStatusCode.NotFound), _ => throw new InvalidOperationException("the log is gone"), CancellationToken.None);

            AssertProblem(list, "The AI service said: HTTP 404.", "llm.example.com");
        }

        // ----- Claude ------------------------------------------------------------------------

        [Fact]
        public async Task Claude_is_listed_through_the_models_api_over_two_pages()
        {
            using var env = new AiTestEnv();
            var handler = new Recorder((request, _) => Task.FromResult(Recorder.Reply(
                request.RequestUri!.Query.Contains("after_id", StringComparison.Ordinal)
                    ? ClaudePage(false, "claude-gamma", "claude-gamma", ClaudeModel("claude-gamma", 200_000, 8192))
                    : ClaudePage(true, "claude-alpha", "claude-beta", ClaudeModel("claude-alpha", 1_000_000, 128_000), ClaudeModel("claude-beta", 200_000, 64_000)))));

            AiModelList list = await ModelCatalog.ListAsync(new AppConfig(), Secrets(env, claudeKey: Key, compatibleKey: OtherKey), handler, CancellationToken.None);

            Assert.Null(list.Problem);
            Assert.Equal("api.anthropic.com", list.Host);
            Assert.Equal(new[]
            {
                new AiModelInfo("claude-alpha", 1_000_000, 128_000),
                new AiModelInfo("claude-beta", 200_000, 64_000),
                new AiModelInfo("claude-gamma", 200_000, 8192),
            }, list.Models);

            Assert.Equal(2, handler.Calls.Count);
            Assert.All(handler.Calls, call =>
            {
                Assert.Equal("GET", call.Method);
                Assert.Equal("https", call.Address.Scheme);
                Assert.Equal("api.anthropic.com", call.Address.Host);
                Assert.Equal("/v1/models", call.Address.AbsolutePath);
                Assert.Equal(Key, call.Header("x-api-key"));
                Assert.False(call.Headers.ContainsKey("Authorization"));
                Assert.NotEqual("", call.Header("anthropic-version"));
                Assert.DoesNotContain(OtherKey, string.Concat(call.Headers.Values), StringComparison.Ordinal);
            });
            Assert.Equal("?limit=100", handler.Calls[0].Address.Query);
            Assert.Contains("after_id=claude-beta", handler.Calls[1].Address.Query, StringComparison.Ordinal);
            Assert.Contains("limit=100", handler.Calls[1].Address.Query, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Claude_without_a_key_sends_nothing()
        {
            using var env = new AiTestEnv();
            var handler = Recorder.Answering(ClaudePage(false, "claude-a", "claude-a", ClaudeModel("claude-a")));

            // The compatible server's key is not Claude's.
            AiModelList list = await ModelCatalog.ListAsync(new AppConfig(), Secrets(env, compatibleKey: Key), handler, CancellationToken.None);

            Assert.Empty(handler.Calls);
            AssertProblem(list, NoKey, "api.anthropic.com");
        }

        [Fact]
        public async Task A_claude_model_without_limits_has_them_unknown()
        {
            using var env = new AiTestEnv();
            var handler = Recorder.Answering(ClaudePage(false, "claude-old", "claude-new",
                ClaudeModel("claude-old", window: null, output: null),
                ClaudeModel("claude-half", window: 200_000, output: null),
                ClaudeModel("claude-new", window: 200_000, output: 64_000)));

            AiModelList list = await ModelCatalog.ListAsync(new AppConfig(), Secrets(env, claudeKey: Key), handler, CancellationToken.None);

            Assert.Null(list.Problem);
            Assert.Equal(new[]
            {
                new AiModelInfo("claude-old", 0, 0),
                new AiModelInfo("claude-half", 200_000, 0),
                new AiModelInfo("claude-new", 200_000, 64_000),
            }, list.Models);
        }

        [Fact]
        public async Task Claude_numbers_and_ids_pass_the_same_checks()
        {
            using var env = new AiTestEnv();
            var handler = Recorder.Answering(ClaudePage(false, "a", "z",
                ClaudeModel("claude-small", window: 512, output: 100),
                ClaudeModel("claude-huge", window: 9_000_000_000, output: 9_000_000_000),
                ClaudeModel("claude-negative", window: -5, output: -5),
                ClaudeModel("claude-over", window: 8192, output: 64_000),
                ClaudeModel("  claude" + J(0x202E) + "-rtl" + J(0x0007) + "  "),
                ClaudeModel("claude-over", window: 100_000, output: 1000),
                ClaudeModel(""),
                ClaudeModel(new string('c', 5000))));

            AiModelList list = await ModelCatalog.ListAsync(new AppConfig(), Secrets(env, claudeKey: Key), handler, CancellationToken.None);

            Assert.Null(list.Problem);
            Assert.Equal(new[]
            {
                new AiModelInfo("claude-small", 0, 0),
                new AiModelInfo("claude-huge", 2_000_000, 2_000_000),
                new AiModelInfo("claude-negative", 0, 0),
                new AiModelInfo("claude-over", 8192, 8192),
                new AiModelInfo("claude-rtl", 200_000, 64_000),
                new AiModelInfo(new string('c', 200), 200_000, 64_000),
            }, list.Models);
        }

        [Fact]
        public async Task An_odd_claude_model_is_left_out_and_the_others_are_kept()
        {
            using var env = new AiTestEnv();
            string page = "{\"data\":[" +
                          "{\"type\":\"model\",\"display_name\":\"No id\",\"created_at\":\"2026-02-19T00:00:00Z\"}," +
                          "{\"type\":\"model\",\"id\":7,\"display_name\":\"A number\",\"created_at\":\"2026-02-19T00:00:00Z\"}," +
                          "{\"type\":\"model\",\"id\":null,\"display_name\":\"Null\",\"created_at\":\"2026-02-19T00:00:00Z\"}," +
                          "{\"id\":\"claude-bare\"}," +
                          "{\"type\":\"model\",\"id\":\"claude-text\",\"display_name\":\"x\",\"created_at\":\"never\",\"max_input_tokens\":\"many\",\"max_tokens\":1.5}," +
                          ClaudeModel("claude-good", 200_000, 64_000) +
                          "],\"has_more\":false,\"first_id\":\"a\",\"last_id\":\"z\"}";

            AiModelList list = await ModelCatalog.ListAsync(new AppConfig(), Secrets(env, claudeKey: Key), Recorder.Answering(page), CancellationToken.None);

            Assert.Null(list.Problem);
            Assert.Equal(new[]
            {
                new AiModelInfo("claude-bare", 0, 0),
                new AiModelInfo("claude-text", 0, 0),
                new AiModelInfo("claude-good", 200_000, 64_000),
            }, list.Models);
        }

        [Fact]
        public async Task Claude_pages_are_followed_until_500_models_are_kept()
        {
            using var env = new AiTestEnv();
            int page = 0;
            var handler = new Recorder((_, _) =>
            {
                int start = 100 * page++;
                string[] models = Enumerable.Range(start, 100).Select(i => ClaudeModel("claude-" + N(i))).ToArray();
                return Task.FromResult(Recorder.Reply(ClaudePage(true, "claude-" + N(start), "claude-" + N(start + 99), models)));
            });

            AiModelList list = await ModelCatalog.ListAsync(new AppConfig(), Secrets(env, claudeKey: Key), handler, CancellationToken.None);

            Assert.Null(list.Problem);
            Assert.Equal(500, list.Models.Count);
            Assert.Equal("claude-0", list.Models[0].Id);
            Assert.Equal("claude-499", list.Models[499].Id);
            Assert.Equal(5, handler.Calls.Count);
        }

        [Fact]
        public async Task A_claude_list_that_always_has_more_is_not_followed_for_ever()
        {
            using var env = new AiTestEnv();
            // The same page again and again: nothing new is learned and "has_more" stays true.
            var handler = Recorder.Answering(ClaudePage(true, "claude-a", "claude-b", ClaudeModel("claude-a"), ClaudeModel("claude-b")));

            AiModelList list = await ModelCatalog.ListAsync(new AppConfig(), Secrets(env, claudeKey: Key), handler, CancellationToken.None);

            Assert.Null(list.Problem);
            Assert.Equal(new[] { "claude-a", "claude-b" }, list.Models.Select(m => m.Id));
            Assert.Equal(2, handler.Calls.Count);       // a page that adds nothing new ends it
        }

        [Theory]
        [InlineData("")]                                                        // says nothing about more pages
        [InlineData(",\"has_more\":false")]
        [InlineData(",\"has_more\":\"yes\"")]
        [InlineData(",\"has_more\":true")]                                      // more, but no id to continue after
        [InlineData(",\"has_more\":true,\"first_id\":null,\"last_id\":null}")]
        public async Task A_claude_page_that_does_not_say_how_to_go_on_is_the_last_one(string rest)
        {
            using var env = new AiTestEnv();
            string body = "{\"data\":[" + ClaudeModel("claude-a") + "]" + rest + (rest.EndsWith('}') ? "" : "}");
            var handler = Recorder.Answering(body);

            AiModelList list = await ModelCatalog.ListAsync(new AppConfig(), Secrets(env, claudeKey: Key), handler, CancellationToken.None);

            Assert.Null(list.Problem);
            Assert.Equal(new AiModelInfo("claude-a", 200_000, 64_000), Assert.Single(list.Models));
            Assert.Single(handler.Calls);
        }

        [Fact]
        public async Task Claude_pages_of_one_model_each_stop_after_ten_pages()
        {
            using var env = new AiTestEnv();
            int page = 0;
            var handler = new Recorder((_, _) =>
            {
                string id = "claude-" + N(page++);
                return Task.FromResult(Recorder.Reply(ClaudePage(true, id, id, ClaudeModel(id))));
            });

            AiModelList list = await ModelCatalog.ListAsync(new AppConfig(), Secrets(env, claudeKey: Key), handler, CancellationToken.None);

            Assert.Null(list.Problem);
            Assert.Equal(10, handler.Calls.Count);
            Assert.Equal(Enumerable.Range(0, 10).Select(i => "claude-" + N(i)), list.Models.Select(m => m.Id));
        }

        [Fact]
        public async Task Claude_ignores_gateway_variables_from_the_environment()
        {
            string[] names = { "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_CUSTOM_HEADERS" };
            string?[] before = names.Select(Environment.GetEnvironmentVariable).ToArray();
            try
            {
                Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "https://proxy.invalid");
                Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", "proxy-token");
                Environment.SetEnvironmentVariable("ANTHROPIC_CUSTOM_HEADERS", "x-api-key: " + OtherKey + "\nanthropic-version: 1999-01-01");
                using var env = new AiTestEnv();
                var handler = Recorder.Answering(ClaudePage(false, "claude-a", "claude-a", ClaudeModel("claude-a")));

                AiModelList list = await ModelCatalog.ListAsync(new AppConfig(), Secrets(env, claudeKey: Key), handler, CancellationToken.None);

                Recorder.Call call = Assert.Single(handler.Calls);
                Assert.Equal("api.anthropic.com", call.Address.Host);
                Assert.Equal(Key, call.Header("x-api-key"));
                Assert.False(call.Headers.ContainsKey("Authorization"));
                Assert.NotEqual("1999-01-01", call.Header("anthropic-version"));
                Assert.DoesNotContain("proxy-token", string.Concat(call.Headers.Values), StringComparison.Ordinal);
                Assert.DoesNotContain(OtherKey, string.Concat(call.Headers.Values), StringComparison.Ordinal);
                Assert.Null(list.Problem);
            }
            finally
            {
                for (int i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], before[i]);
            }
        }

        [Theory]
        [InlineData(401, KeyRejected, 1)]
        [InlineData(403, "The AI service refused the request (403). Check that this key may use this model.", 1)]
        [InlineData(404, "The AI service said: HTTP 404.", 1)]
        [InlineData(429, Busy, 3)]          // retried twice by the SDK, as a question is
        [InlineData(529, Busy, 3)]
        public async Task A_claude_status_is_said_in_a_sentence_without_the_servers_own_words(int status, string sentence, int requests)
        {
            using var env = new AiTestEnv();
            var handler = Recorder.Answering(
                "{\"type\":\"error\",\"error\":{\"type\":\"some_error\",\"message\":\"Server words about " + Key + "\"}}", (HttpStatusCode)status);

            AiModelList list = await ModelCatalog.ListAsync(new AppConfig(), Secrets(env, claudeKey: Key), handler, CancellationToken.None);

            Assert.Equal(requests, handler.Calls.Count);
            AssertProblem(list, sentence, "api.anthropic.com");
            Assert.DoesNotContain("Server words", list.Problem!, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("")]
        [InlineData("null")]
        [InlineData("[]")]
        [InlineData("{}")]
        [InlineData("{\"data\":\"none\",\"has_more\":false}")]
        [InlineData("<html><body>Blocked by the proxy</body></html>")]
        [InlineData("{\"data\":[")]
        // The SDK reads a page whole: an entry that is not an object fails the page, not only itself.
        [InlineData("{\"data\":[7,null,\"text\"],\"has_more\":false,\"first_id\":null,\"last_id\":null}")]
        public async Task A_claude_answer_that_is_not_a_list_is_said_in_a_sentence(string body)
        {
            using var env = new AiTestEnv();
            var lines = new List<string>();
            var handler = Recorder.Answering(body);

            AiModelList list = await ModelCatalog.ListAsync(new AppConfig(), Secrets(env, claudeKey: Key), handler, lines.Add, CancellationToken.None);

            AssertProblem(list, NotAList, "api.anthropic.com");
            Assert.Single(handler.Calls);
            Assert.Equal("Listing the models of api.anthropic.com failed (not a list)", Assert.Single(lines));
        }

        [Fact]
        public async Task A_claude_answer_over_4_MB_never_reaches_the_sdk()
        {
            using var env = new AiTestEnv();
            var body = new EndlessSpaces();
            var handler = new Recorder((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));

            AiModelList list = await ModelCatalog.ListAsync(new AppConfig(), Secrets(env, claudeKey: Key), handler, CancellationToken.None);

            AssertProblem(list, TooLong, "api.anthropic.com");
            Assert.Single(handler.Calls);
            Assert.True(body.Given <= ModelCatalog.MaxAnswerBytes + 1024 * 1024, "read " + N(body.Given) + " bytes of an endless answer");
        }

        [Fact]
        public async Task A_claude_timeout_is_said_in_a_sentence()
        {
            using var env = new AiTestEnv();
            var handler = new Recorder(async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Recorder.Reply("{}");
            });
            var watch = Stopwatch.StartNew();

            AiModelList list = await ModelCatalog.ListAsync(new AppConfig(), Secrets(env, claudeKey: Key), handler,
                warn: null, timeout: TimeSpan.FromMilliseconds(300), CancellationToken.None);

            AssertProblem(list, TimedOut, "api.anthropic.com");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "took " + watch.Elapsed);
        }

        [Fact]
        public async Task The_deadline_is_for_the_whole_listing_and_not_for_each_page()
        {
            using var env = new AiTestEnv();
            int page = 0;
            // Every page arrives well inside the deadline, and there is always another one.
            var handler = new Recorder(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
                string id = "claude-" + N(page++);
                return Recorder.Reply(ClaudePage(true, id, id, ClaudeModel(id)));
            });

            AiModelList list = await ModelCatalog.ListAsync(new AppConfig(), Secrets(env, claudeKey: Key), handler,
                warn: null, timeout: TimeSpan.FromMilliseconds(700), CancellationToken.None);

            AssertProblem(list, TimedOut, "api.anthropic.com");
            Assert.True(handler.Calls.Count < 10, "all ten pages were asked for");     // they would have taken two seconds
        }

        [Fact]
        public async Task A_claude_connection_failure_is_said_in_a_sentence()
        {
            using var env = new AiTestEnv();
            var lines = new List<string>();
            var handler = Recorder.Failing(() => new HttpRequestException("No such host is known. (api.anthropic.com:443) " + Key));

            AiModelList list = await ModelCatalog.ListAsync(new AppConfig(), Secrets(env, claudeKey: Key), handler, lines.Add, CancellationToken.None);

            AssertProblem(list, Unreachable, "api.anthropic.com");
            Assert.Equal(3, handler.Calls.Count);       // retried twice by the SDK, as a question is
            Assert.Equal("Listing the models of api.anthropic.com failed (AnthropicIOException)", Assert.Single(lines));
        }

        // ----- What learned limits belong to ---------------------------------------------------

        [Fact]
        public void The_key_of_claude_names_the_provider_its_address_and_the_model()
        {
            Assert.Equal("Claude|https://api.anthropic.com|claude-haiku-4-5", ModelCatalog.KeyOf(new AppConfig()));
            Assert.Equal("Claude|https://api.anthropic.com|claude-sonnet-5-5", ModelCatalog.KeyOf(new AppConfig { AiClaudeModel = "claude-sonnet-5-5" }));
        }

        [Fact]
        public void The_key_of_a_compatible_server_names_the_scheme_the_host_the_port_and_the_model()
        {
            Assert.Equal("OpenAiCompatible|http://localhost:11434|llama3.2", ModelCatalog.KeyOf(Compatible("http://localhost:11434/v1", "llama3.2")));
            Assert.Equal("OpenAiCompatible|https://llm.example.com|org/Some-Model", ModelCatalog.KeyOf(Compatible("https://llm.example.com/v1", "org/Some-Model")));
            Assert.Equal("OpenAiCompatible|https://llm.example.com:8443|m", ModelCatalog.KeyOf(Compatible("https://llm.example.com:8443/v1", "m")));
            Assert.Equal("OpenAiCompatible|http://localhost:11434|", ModelCatalog.KeyOf(Compatible("http://localhost:11434/v1")));
        }

        [Fact]
        public void The_key_differs_when_the_provider_the_host_the_port_the_scheme_or_the_model_differs()
        {
            string key = ModelCatalog.KeyOf(Compatible("https://llm.example.com:8443/v1", "some-model"));

            Assert.NotEqual(key, ModelCatalog.KeyOf(Compatible("https://other.example.com:8443/v1", "some-model")));
            Assert.NotEqual(key, ModelCatalog.KeyOf(Compatible("https://llm.example.com:8444/v1", "some-model")));
            Assert.NotEqual(key, ModelCatalog.KeyOf(Compatible("https://llm.example.com/v1", "some-model")));
            Assert.NotEqual(key, ModelCatalog.KeyOf(Compatible("http://llm.example.com:8443/v1", "some-model")));
            Assert.NotEqual(key, ModelCatalog.KeyOf(Compatible("https://llm.example.com:8443/v1", "other-model")));
            Assert.NotEqual(key, ModelCatalog.KeyOf(Compatible("https://llm.example.com:8443/v1", "Some-Model")));     // the model exactly as configured
            Assert.NotEqual(key, ModelCatalog.KeyOf(Compatible("https://llm.example.com:8443/v1", "")));

            // The other provider, with the same model name and the compatible settings still in place.
            var claude = new AppConfig { AiCompatibleBaseUrl = "https://llm.example.com:8443/v1", AiCompatibleModel = "some-model", AiClaudeModel = "some-model" };
            Assert.NotEqual(key, ModelCatalog.KeyOf(claude));
            // A compatible server at Claude's own address is still another provider.
            Assert.NotEqual(ModelCatalog.KeyOf(new AppConfig { AiClaudeModel = "m" }), ModelCatalog.KeyOf(Compatible("https://api.anthropic.com/v1", "m")));
        }

        [Theory]
        [InlineData("https://llm.example.com/v1")]
        [InlineData("https://llm.example.com/v1/")]
        [InlineData("https://llm.example.com/V1")]
        [InlineData("https://llm.example.com/another/path")]
        [InlineData("https://llm.example.com")]
        [InlineData("https://LLM.Example.COM/v1")]
        [InlineData("HTTPS://llm.example.com/v1")]
        [InlineData("https://llm.example.com:443/v1")]
        [InlineData("  https://llm.example.com/v1  ")]
        [InlineData("https://llm.example.com/v1?token=abc")]
        [InlineData("https://user:password@llm.example.com/v1#part")]
        public void The_key_is_the_same_for_another_path_or_letter_case_of_the_same_address(string baseUrl)
        {
            Assert.Equal("OpenAiCompatible|https://llm.example.com|some-model", ModelCatalog.KeyOf(Compatible(baseUrl, "some-model")));
        }

        [Fact]
        public void A_default_port_is_left_out_of_the_key_for_http_too()
        {
            Assert.Equal(ModelCatalog.KeyOf(Compatible("http://llm.example.com/v1", "m")), ModelCatalog.KeyOf(Compatible("http://llm.example.com:80/v1", "m")));
            Assert.Equal("OpenAiCompatible|http://llm.example.com:443|m", ModelCatalog.KeyOf(Compatible("http://llm.example.com:443/v1", "m")));
            Assert.Equal("OpenAiCompatible|https://llm.example.com:80|m", ModelCatalog.KeyOf(Compatible("https://llm.example.com:80/v1", "m")));
            Assert.Equal("OpenAiCompatible|http://[::1]:11434|m", ModelCatalog.KeyOf(Compatible("http://[::1]:11434/v1", "m")));
        }

        [Fact]
        public void The_key_holds_nothing_secret_and_is_never_empty()
        {
            using var env = new AiTestEnv();
            Secrets(env, claudeKey: Key, compatibleKey: OtherKey);

            string key = ModelCatalog.KeyOf(Compatible("https://user:password@llm.example.com/secret-path/v1?api-key=abc123", "m"));

            Assert.Equal("OpenAiCompatible|https://llm.example.com|m", key);
            Assert.DoesNotContain("password", key, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-path", key, StringComparison.Ordinal);
            Assert.DoesNotContain("abc123", key, StringComparison.Ordinal);

            // An address nothing can be asked at still gives a key, and it is not the "never learned" empty one.
            Assert.Equal("OpenAiCompatible||m", ModelCatalog.KeyOf(Compatible("ftp://llm.example.com/v1", "m")));
            Assert.NotEqual("", ModelCatalog.KeyOf(Compatible("not an address")));
            Assert.NotEqual(new AppConfig().AiModelLimitsOf, ModelCatalog.KeyOf(new AppConfig()));
        }

        [Fact]
        public void A_key_of_an_ordinary_setup_fits_the_setting_that_keeps_it()
        {
            // The longest host name there is, a port, and the longest id the catalog gives.
            string host = string.Join(".", Enumerable.Repeat(new string('a', 61), 4));
            AppConfig config = Compatible("https://" + host + ":65535/v1", new string('m', ModelCatalog.MaxIdChars));

            string key = ModelCatalog.KeyOf(config);
            config.AiModelLimitsOf = key;

            Assert.True(key.Length <= 600, "the key is " + N(key.Length) + " characters");
            Assert.Equal(key, config.AiModelLimitsOf);
        }
    }
}
