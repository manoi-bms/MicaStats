using System;
using System.Collections.Concurrent;
using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Kil0bitSystemMonitor.Services;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// The highlighting definition of a language: AvalonEdit's built-in ones by name, MicaPad's own
    /// (INI, YAML, Batch, Log, TypeScript, Shell, Pascal, Go, Dockerfile, Rust, Ruby, Kotlin and the
    /// fence-only Markdown) from embedded .xshd files. Loaded once and shared; never modified,
    /// <see cref="ThemedHighlightingColorizer"/> repaints them at draw time.
    /// </summary>
    internal static class PadHighlighting
    {
        private static readonly ConcurrentDictionary<string, IHighlightingDefinition?> Cache = new();

        /// <summary>
        /// The definition for <paramref name="language"/>, or null when it has none (Plain text,
        /// Markdown) or it could not be loaded — then the note shows as plain text and the failure
        /// is logged once.
        /// </summary>
        public static IHighlightingDefinition? For(PadLanguage language)
        {
            if (language.Definition == null) return null;
            return Cache.GetOrAdd(language.Id, _ => Load(language));
        }

        private static IHighlightingDefinition? Load(PadLanguage language)
        {
            try
            {
                if (!language.OwnDefinition)
                    return HighlightingManager.Instance.GetDefinition(language.Definition!)
                           ?? throw new InvalidOperationException("AvalonEdit has no definition named " + language.Definition);

                string name = "MicaPad.Highlighting." + language.Definition + ".xshd";
                using Stream stream = typeof(PadHighlighting).Assembly.GetManifestResourceStream(name)
                                      ?? throw new InvalidOperationException("Missing resource " + name);
                using var reader = XmlReader.Create(stream);
                return HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Warn("pad", "The " + language.Name + " colors could not be loaded, so it shows as plain text: " + ex.Message);
                return null;
            }
        }
    }
}
