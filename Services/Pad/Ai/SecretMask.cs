using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        private const string PlaintextBasePrefix = "LOCAL_SECRET_";

        private readonly List<string> _ids;          // _ids[n - 1] is credential n
        private readonly List<int> _uses;            // how often credential n appears in the original
        private readonly List<string> _plaintext;
        private readonly List<int> _plaintextUses;
        private readonly string _prefix;
        private readonly string _plaintextPrefix;

        private readonly Regex _placeholder;
        private readonly Regex _plaintextPlaceholder;

        private SecretMask(string text, List<string> ids, List<int> uses, string prefix,
                           List<string>? plaintext = null, List<int>? plaintextUses = null,
                           string? plaintextPrefix = null)
        {
            Text = text;
            // The token is [[<prefix><n>]]; the prefix is absent from the original text, so a placeholder the user typed is never counted.
            _placeholder = new Regex(@"\[\[" + Regex.Escape(prefix) + @"([0-9]{1,4})\]\]", RegexOptions.CultureInvariant);
            _ids = ids;
            _uses = uses;
            _plaintext = plaintext ?? new List<string>();
            _plaintextUses = plaintextUses ?? new List<int>();
            _prefix = prefix;
            _plaintextPrefix = plaintextPrefix ?? PlaintextBasePrefix;
            _plaintextPlaceholder = new Regex(@"\[\[" + Regex.Escape(_plaintextPrefix) + @"([0-9]{1,4})\]\]", RegexOptions.CultureInvariant);
        }

        /// <summary>The text with placeholders, safe to send.</summary>
        public string Text { get; }

        /// <summary>How many different credentials the original held.</summary>
        public int Count => _ids.Count;

        /// <summary>How many distinct locally detected plaintext values were removed.</summary>
        public int PlaintextCount => _plaintext.Count;

        public static SecretMask Of(string text)
            => Of(text, Array.Empty<string>());

        /// <summary>
        /// Removes stored credential pills and exact locally detected plaintext values. Plaintext
        /// values remain only in this local object so tool and model text never contains them.
        /// </summary>
        public static SecretMask Of(string text, IReadOnlyList<string> plaintextValues)
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

            string plaintextPrefix = PlaintextBasePrefix;
            while (text.Contains("[[" + plaintextPrefix, StringComparison.Ordinal)) plaintextPrefix += "X_";

            var plaintext = new List<string>();
            if (plaintextValues != null)
                foreach (string? value in plaintextValues)
                    if (!string.IsNullOrEmpty(value) && !plaintext.Contains(value, StringComparer.Ordinal))
                        plaintext.Add(value);

            var plaintextUses = new List<int>(plaintext.Count);
            for (int i = 0; i < plaintext.Count; i++) plaintextUses.Add(0);
            string safe = masked.ToString();
            if (plaintext.Count > 0)
            {
                var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < plaintext.Count; i++) indexes[plaintext[i]] = i;
                string alternatives = string.Join("|", plaintext.OrderByDescending(value => value.Length).Select(Regex.Escape));
                MatchCollection storedTokens = new Regex(@"\[\[" + Regex.Escape(prefix) + @"[0-9]{1,4}\]\]", RegexOptions.CultureInvariant).Matches(safe);
                safe = Regex.Replace(safe, alternatives, match =>
                {
                    foreach (Match storedToken in storedTokens)
                        if (match.Index >= storedToken.Index && match.Index + match.Length <= storedToken.Index + storedToken.Length)
                            return match.Value;
                    int index = indexes[match.Value];
                    plaintextUses[index]++;
                    return Token(plaintextPrefix, index + 1);
                }, RegexOptions.CultureInvariant);
            }

            return new SecretMask(safe, ids, uses, prefix, plaintext, plaintextUses, plaintextPrefix);
        }

        /// <summary>
        /// Redacts a task label or instruction using this source request's stable placeholder
        /// numbers. Values not present in the source use a generic placeholder which cannot be
        /// mistaken for one of the source credentials during result restoration.
        /// </summary>
        internal string RedactFragment(string text, IReadOnlyList<string>? additionalPlaintext = null)
        {
            text ??= "";
            var stored = new StringBuilder(text.Length);
            int at = 0;
            foreach (SecretReference reference in SecretTokens.Find(text))
            {
                stored.Append(text, at, reference.Offset - at);
                int index = _ids.IndexOf(reference.Id);
                stored.Append(index >= 0 ? Token(_prefix, index + 1) : "[[HIDDEN_CREDENTIAL]]");
                at = reference.Offset + reference.Length;
            }
            stored.Append(text, at, text.Length - at);

            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < _plaintext.Count; i++) replacements[_plaintext[i]] = Token(_plaintextPrefix, i + 1);
            if (additionalPlaintext != null)
                foreach (string? value in additionalPlaintext)
                    if (!string.IsNullOrEmpty(value) && !replacements.ContainsKey(value))
                        replacements[value] = "[[HIDDEN_CREDENTIAL]]";
            if (replacements.Count == 0) return stored.ToString();

            string safe = stored.ToString();
            string alternatives = string.Join("|", replacements.Keys.OrderByDescending(value => value.Length).Select(Regex.Escape));
            MatchCollection protectedTokens = new Regex(
                @"\[\[(?:" + Regex.Escape(_prefix) + @"[0-9]{1,4}|HIDDEN_CREDENTIAL)\]\]",
                RegexOptions.CultureInvariant).Matches(safe);
            return Regex.Replace(safe, alternatives, match =>
            {
                foreach (Match token in protectedTokens)
                    if (match.Index >= token.Index && match.Index + match.Length <= token.Index + token.Length)
                        return match.Value;
                return replacements[match.Value];
            }, RegexOptions.CultureInvariant);
        }

        /// <summary>The model's reply with each known placeholder turned back into its pill. An invented one stays as text.</summary>
        public string Unmask(string result) => Unmask(result, restorePlaintext: true);

        /// <summary>
        /// Restores stored credential pills. Locally detected plaintext is restored only for an
        /// explicit rewrite path; read-only answers can keep the safe placeholders.
        /// </summary>
        public string Unmask(string result, bool restorePlaintext)
        {
            string stored = _placeholder.Replace(result ?? "", match =>
                int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= _ids.Count
                    ? SecretTokens.Format(_ids[n - 1])
                    : match.Value);
            if (!restorePlaintext) return stored;
            return _plaintextPlaceholder.Replace(stored, match =>
                int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= _plaintext.Count
                    ? _plaintext[n - 1]
                    : match.Value);
        }

        /// <summary><see cref="Lost"/> unless the reply holds each placeholder exactly as often as the original did.</summary>
        public string? Problem(string result)
        {
            if (_ids.Count == 0 && _plaintext.Count == 0) return null;
            var seen = new int[_ids.Count];
            foreach (Match match in _placeholder.Matches(result ?? ""))
                if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= _ids.Count)
                    seen[n - 1]++;
            for (int i = 0; i < seen.Length; i++)
                if (seen[i] != _uses[i]) return Lost;

            var plaintextSeen = new int[_plaintext.Count];
            foreach (Match match in _plaintextPlaceholder.Matches(result ?? ""))
                if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= _plaintext.Count)
                    plaintextSeen[n - 1]++;
            for (int i = 0; i < plaintextSeen.Length; i++)
                if (plaintextSeen[i] != _plaintextUses[i]) return Lost;
            return null;
        }

        private static string Token(string prefix, int n) => "[[" + prefix + n.ToString(CultureInfo.InvariantCulture) + "]]";
    }
}
