using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Ai.Tools
{
    /// <summary>
    /// Removes what identifies the person or the PC from text before it leaves MicaStats, whether
    /// for an AI provider or for an MCP client.
    ///
    /// <para>
    /// Process names and paths stay: they are what the answers are about. The user's own profile
    /// folder becomes <c>%USERPROFILE%</c>, any other profile folder <c>X:\Users\&lt;user&gt;</c>, the
    /// computer and user names <c>[computer]</c> and <c>[user]</c>, and IP and MAC addresses
    /// <c>[ip]</c> and <c>[mac]</c>. The rules run in that order, so a profile path is replaced whole
    /// before its user name could be picked out of it. Every rule leaves its own output alone, so
    /// redacting twice changes nothing more.
    /// </para>
    ///
    /// <para>
    /// When in doubt it hides more rather than less: a spaced name followed by a slash later in the
    /// sentence may take a few extra words with it. Over-redaction costs a little context; a leak
    /// cannot be taken back.
    /// </para>
    /// </summary>
    public sealed class Redactor
    {
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        // X:\Users\<name>. The name runs to the next separator, so "Jane Doe" is caught whole, or,
        // with no separator after it, to the next white space.
        private static readonly Regex OtherProfile = new(
            @"(?<!\w)([A-Za-z]):([\\/]+)(Users)([\\/]+)(?:[^\\/:*?""<>|\r\n]+(?=[\\/])|[^\\/:*?""<>|\s]+)",
            Options | RegexOptions.Compiled);

        // Four octets of 0-255, not part of a longer dotted number.
        private static readonly Regex IPv4 = new(
            @"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?!\d|\.\d)",
            Options | RegexOptions.Compiled);

        // Eight groups, or a "::" compression. A clock time such as 10:00:00 has neither, and a
        // C# name such as global::System fails the word-character guards.
        private static readonly Regex IPv6 = new(
            @"(?<![\w:])(?:(?:[0-9A-F]{1,4}:){7}[0-9A-F]{1,4}|(?:[0-9A-F]{1,4}(?::[0-9A-F]{1,4}){0,6})?::(?:[0-9A-F]{1,4}(?::[0-9A-F]{1,4}){0,6})?)(?![\w:])",
            Options | RegexOptions.Compiled);

        // Six pairs joined by one kind of separator, dash or colon.
        private static readonly Regex Mac = new(
            @"(?<![\w:-])[0-9A-F]{2}([:-])[0-9A-F]{2}(?:\1[0-9A-F]{2}){4}(?![\w:-])",
            Options | RegexOptions.Compiled);

        private readonly Regex? _ownProfile;
        private readonly Regex? _machine;
        private readonly Regex? _user;

        /// <param name="userProfile">The profile folder that becomes <c>%USERPROFILE%</c>; blank skips the rule.</param>
        /// <param name="userName">Replaced as a whole word when 3 or more characters long.</param>
        /// <param name="machineName">Replaced as a whole word when 3 or more characters long.</param>
        public Redactor(string userProfile, string userName, string machineName)
        {
            _ownProfile = ProfilePattern(userProfile);
            _machine = WordPattern(machineName);
            _user = WordPattern(userName);
        }

        /// <summary>A redactor for the Windows account MicaStats runs under.</summary>
        public static Redactor ForCurrentUser() =>
            new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName);

        /// <summary>The text with every identifying part replaced by its token.</summary>
        public string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";

            if (_ownProfile != null) text = _ownProfile.Replace(text, "%USERPROFILE%");
            text = OtherProfile.Replace(text, "${1}:${2}${3}${4}<user>");
            if (_machine != null) text = _machine.Replace(text, "[computer]");
            if (_user != null) text = _user.Replace(text, "[user]");
            text = IPv4.Replace(text, "[ip]");
            text = IPv6.Replace(text, "[ip]");
            return Mac.Replace(text, "[mac]");
        }

        /// <summary>
        /// Redacts every string value in the tree in place and returns <paramref name="node"/>.
        /// Property names are left alone: they are the tool's own vocabulary. A bare string value
        /// cannot be changed in place, so it comes back as a new node.
        /// </summary>
        public JsonNode? RedactJson(JsonNode? node)
        {
            switch (node)
            {
                case null:
                    return null;

                case JsonObject obj:
                    foreach (var pair in obj.ToList())
                    {
                        if (pair.Value is JsonValue value)
                        {
                            JsonNode redacted = RedactValue(value);
                            if (!ReferenceEquals(redacted, value)) obj[pair.Key] = redacted;
                        }
                        else
                        {
                            RedactJson(pair.Value);
                        }
                    }
                    return obj;

                case JsonArray array:
                    for (int i = 0; i < array.Count; i++)
                    {
                        if (array[i] is JsonValue value)
                        {
                            JsonNode redacted = RedactValue(value);
                            if (!ReferenceEquals(redacted, value)) array[i] = redacted;
                        }
                        else
                        {
                            RedactJson(array[i]);
                        }
                    }
                    return array;

                case JsonValue single:
                    return RedactValue(single);

                default:
                    return node;
            }
        }

        /// <summary>The value itself when it is not a string or needs no change, else a new string value.</summary>
        private JsonNode RedactValue(JsonValue value)
        {
            if (value.GetValueKind() != JsonValueKind.String) return value;

            string text = value.TryGetValue(out string? s) && s != null
                ? s
                : JsonSerializer.Deserialize<string>(value.ToJsonString()) ?? "";
            string redacted = Redact(text);
            return redacted == text ? value : JsonValue.Create(redacted)!;
        }

        /// <summary>
        /// The profile folder with either separator, in any case, and only as a whole folder:
        /// <c>C:\Users\Manoi2</c> is somebody else.
        /// </summary>
        private static Regex? ProfilePattern(string? profile)
        {
            if (string.IsNullOrWhiteSpace(profile)) return null;

            string[] parts = profile.Trim().Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;   // a bare drive is not a profile

            string body = string.Join(@"[\\/]+", parts.Select(Regex.Escape));
            // A space or dot only ends the profile when what follows does not run on into a
            // path separator: "Manoi Smith\x" and "Manoi.old\x" are longer folder names.
            return new Regex(@"(?<!\w)" + body + @"(?![^\\/\s""'<>|,;:.)\]}])(?![ .][^\s""'<>|\\/]*[\\/])", Options);
        }

        /// <summary>A whole-word pattern, or null for a name too short to replace safely.</summary>
        private static Regex? WordPattern(string? word)
        {
            if (string.IsNullOrWhiteSpace(word) || word.Trim().Length < 3) return null;
            // Never inside an existing token such as [user], <user> or %USERPROFILE%.
            return new Regex(@"(?<![\w\[<%])" + Regex.Escape(word.Trim()) + @"(?![\w\]>%])", Options);
        }
    }
}
