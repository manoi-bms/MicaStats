using System;
using System.Text.RegularExpressions;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Underlines http, https and mailto addresses (spec 4.1). Only <see cref="SafeLinks"/> patterns
    /// match, and a match that is not an allowed link produces no element at all. Ctrl+Click raises
    /// RequestNavigate, which the window handles itself.
    /// </summary>
    internal sealed class SafeLinkGenerator : LinkElementGenerator
    {
        public SafeLinkGenerator()
            : base(new Regex(SafeLinks.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            RequireControlModifierForClick = true;
        }

        protected override Uri GetUriFromMatch(Match match) => SafeLinks.TryCreate(match.Value)!;
    }
}
