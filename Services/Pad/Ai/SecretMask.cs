using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kil0bitSystemMonitor.Services.Pad.Ai
{
    /// <summary>
    /// Text for the model with every stored credential taken out (MicaPad AI spec 2): each
    /// <c>{{secret:ID}}</c> becomes <c>[[CREDENTIAL_n]]</c>, numbered from 1 in order of first
    /// appearance, the same credential keeping its number. The id and the secret value never
    /// leave the PC. <see cref="Unmask"/> puts the pills back into the model's reply.
    /// </summary>
    public sealed class SecretMask
    {
        /// <summary>Why a result may not replace the selection.</summary>
        public const string Lost = "The result lost or repeated a stored credential, so it cannot replace the selection";

        private const string BasePrefix = "CREDENTIAL_";

        private readonly List<string> _ids;          // _ids[n - 1] is credential n
        private readonly List<int> _uses;            // how often credential n appears in the original

        private readonly Regex _placeholder;

        private SecretMask(string text, List<string> ids, List<int> uses, string prefix)
        {
            Text = text;
            // The token is [[<prefix><n>]]; the prefix is absent from the original text, so a placeholder the user typed is never counted.
            _placeholder = new Regex(@"\[\[" + Regex.Escape(prefix) + @"([0-9]{1,4})\]\]", RegexOptions.CultureInvariant);
            _ids = ids;
            _uses = uses;
        }

        /// <summary>The text with placeholders, safe to send.</summary>
        public string Text { get; }

        /// <summary>How many different credentials the original held.</summary>
        public int Count => _ids.Count;

        public static SecretMask Of(string text)
        {
            text ??= "";
            string prefix = BasePrefix;
            while (text.Contains("[[" + prefix, StringComparison.Ordinal)) prefix += "X_";
            var ids = new List<string>();
            var uses = new List<int>();
            var masked = new StringBuilder(text.Length);
            int at = 0;
            foreach (SecretReference reference in SecretTokens.Find(text))
            {
                masked.Append(text, at, reference.Offset - at);
                int n = ids.IndexOf(reference.Id) + 1;
                if (n == 0)
                {
                    ids.Add(reference.Id);
                    uses.Add(0);
                    n = ids.Count;
                }
                uses[n - 1]++;
                masked.Append(Token(prefix, n));
                at = reference.Offset + reference.Length;
            }
            masked.Append(text, at, text.Length - at);
            return new SecretMask(masked.ToString(), ids, uses, prefix);
        }

        /// <summary>The model's reply with each known placeholder turned back into its pill. An invented one stays as text.</summary>
        public string Unmask(string result) =>
            _placeholder.Replace(result ?? "", match =>
                int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= _ids.Count
                    ? SecretTokens.Format(_ids[n - 1])
                    : match.Value);

        /// <summary><see cref="Lost"/> unless the reply holds each placeholder exactly as often as the original did.</summary>
        public string? Problem(string result)
        {
            if (_ids.Count == 0) return null;
            var seen = new int[_ids.Count];
            foreach (Match match in _placeholder.Matches(result ?? ""))
                if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= _ids.Count)
                    seen[n - 1]++;
            for (int i = 0; i < seen.Length; i++)
                if (seen[i] != _uses[i]) return Lost;
            return null;
        }

        private static string Token(string prefix, int n) => "[[" + prefix + n.ToString(CultureInfo.InvariantCulture) + "]]";
    }
}
