using System;
using System.Collections.Generic;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Function names in their own color (ruling R4). <see cref="AddTo"/> gives each name
    /// <see cref="FunctionCalls"/> finds in a highlighted line the Function color, only where no
    /// section of the language's colors (a string, a comment, a keyword...) already is. As an
    /// <see cref="IHighlighter"/> it wraps a whole file's highlighter and does that to every line it
    /// hands out; fences call <see cref="AddTo"/> themselves. Only languages flagged
    /// <see cref="PadLanguage.FunctionCalls"/> get either.
    /// </summary>
    internal sealed class FunctionCallHighlighter : IHighlighter
    {
        /// <summary>
        /// The one color every added section shares. <c>Underline = false</c> draws nothing, but
        /// without some style AvalonEdit's colorizer would skip the color as empty and never paint it.
        /// </summary>
        private static readonly HighlightingColor FunctionColor = CreateColor("Function");

        /// <summary>What a keyword the definition colored as a call is repainted with.</summary>
        private static readonly HighlightingColor KeywordColor = CreateColor("Keywords");

        private readonly IHighlighter _inner;
        private readonly Action<Exception> _onFailure;
        private readonly CallSyntax _syntax;
        private bool _failed;

        /// <param name="onFailure">Told the first time the pass throws; that line keeps its other colors.</param>
        /// <param name="syntax">The language's call syntax (<see cref="CallSyntax.For"/>); null is <see cref="CallSyntax.Plain"/>.</param>
        public FunctionCallHighlighter(IHighlighter inner, Action<Exception> onFailure, CallSyntax? syntax = null)
        {
            _inner = inner;
            _onFailure = onFailure;
            _syntax = syntax ?? CallSyntax.Plain;
        }

        private static HighlightingColor CreateColor(string name)
        {
            var color = new HighlightingColor { Name = name, Underline = false };
            color.Freeze();
            return color;
        }

        /// <summary>
        /// Adds a Function section for each name the line's sections leave uncovered, keeping the
        /// sections in offset order. A section the definition colored as a call whose text is a
        /// <see cref="FunctionCalls.IsKeyword">keyword</see> (PHP's <c>if (</c>, C++'s <c>decltype(</c>,
        /// C#'s <c>when (</c>) gets the keyword color instead, in place; one that a declaration word of
        /// the language names (<paramref name="syntax"/>: Python's <c>class Foo(</c>, C#'s
        /// <c>record Point(</c>) is dropped, a type being no call. A throw leaves the line as it
        /// was and is passed to <paramref name="onFailure"/>: it costs only this line its function colors.
        /// </summary>
        /// <param name="syntax">The language's call syntax (<see cref="CallSyntax.For"/>); null is <see cref="CallSyntax.Plain"/>.</param>
        internal static void AddTo(HighlightedLine line, Action<Exception> onFailure, CallSyntax? syntax = null)
        {
            try
            {
                syntax ??= CallSyntax.Plain;
                var documentLine = line.DocumentLine;
                if (documentLine.Length > MarkdownLineTokenizer.MaxInlineLength) return;
                string text = line.Document.GetText(documentLine);
                var sections = line.Sections;

                List<HighlightedSection>? keywords = null;
                List<HighlightedSection>? declared = null;
                foreach (var section in sections)
                {
                    int at = section.Offset - documentLine.Offset;
                    if (at < 0 || at + section.Length > text.Length) continue;
                    bool keyword = FunctionCalls.IsKeyword(text.AsSpan(at, section.Length)) && !FunctionCalls.AfterMemberAccess(text, at);
                    if (!keyword && !syntax.DeclaresTypeAt(text, at)) continue;
                    if (SyntaxColors.Categorize(section.Color?.Name) != SyntaxCategory.Function) continue;
                    if (keyword) (keywords ??= new List<HighlightedSection>()).Add(section);
                    else (declared ??= new List<HighlightedSection>()).Add(section);
                }
                var merged = WithCalls(sections, FunctionCalls.Find(text, MarkdownLineTokenizer.MaxInlineLength, syntax), documentLine.Offset);

                // Nothing has changed the line so far; nothing below throws.
                if (keywords != null)
                    foreach (var section in keywords) section.Color = KeywordColor;
                if (merged == null && declared == null) return;
                var kept = merged ?? new List<HighlightedSection>(sections);
                sections.Clear();
                foreach (var section in kept)
                    if (declared == null || !declared.Contains(section)) sections.Add(section);
            }
            catch (Exception ex)
            {
                onFailure(ex);
            }
        }

        /// <summary>
        /// <paramref name="sections"/> with a Function section for each call no section covers, in
        /// offset order; null when no call is added.
        /// </summary>
        private static List<HighlightedSection>? WithCalls(IList<HighlightedSection> sections, IReadOnlyList<(int Start, int Length)> calls, int lineOffset)
        {
            if (calls.Count == 0) return null;

            // Sections are sorted by offset: the ones starting before a name's end come first,
            // and if none of them reaches past the name's start, nothing covers the name.
            var merged = new List<HighlightedSection>(sections.Count + calls.Count);
            int next = 0;
            int reach = int.MinValue;
            bool added = false;
            foreach (var (start, length) in calls)
            {
                int from = lineOffset + start;
                int to = from + length;
                for (; next < sections.Count && sections[next].Offset < to; next++)
                {
                    reach = Math.Max(reach, sections[next].Offset + sections[next].Length);
                    merged.Add(sections[next]);
                }
                if (reach > from) continue;
                merged.Add(new HighlightedSection { Offset = from, Length = length, Color = FunctionColor });
                added = true;
            }
            if (!added) return null;
            for (; next < sections.Count; next++) merged.Add(sections[next]);
            return merged;
        }

        public HighlightedLine HighlightLine(int lineNumber)
        {
            var line = _inner.HighlightLine(lineNumber);
            AddTo(line, Failed, _syntax);
            return line;
        }

        private void Failed(Exception ex)
        {
            if (_failed) return;
            _failed = true;
            _onFailure(ex);
        }

        public IDocument Document => _inner.Document;

        public HighlightingColor DefaultTextColor => _inner.DefaultTextColor;

        public event HighlightingStateChangedEventHandler HighlightingStateChanged
        {
            add => _inner.HighlightingStateChanged += value;
            remove => _inner.HighlightingStateChanged -= value;
        }

        public IEnumerable<HighlightingColor> GetColorStack(int lineNumber) => _inner.GetColorStack(lineNumber);

        public void UpdateHighlightingState(int lineNumber) => _inner.UpdateHighlightingState(lineNumber);

        public void BeginHighlighting() => _inner.BeginHighlighting();

        public void EndHighlighting() => _inner.EndHighlighting();

        public HighlightingColor GetNamedColor(string name) => _inner.GetNamedColor(name);

        public void Dispose() => _inner.Dispose();
    }
}
