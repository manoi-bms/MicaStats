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
        private static readonly HighlightingColor FunctionColor = CreateColor();

        private readonly IHighlighter _inner;
        private readonly Action<Exception> _onFailure;
        private bool _failed;

        /// <param name="onFailure">Told the first time the pass throws; that line keeps its other colors.</param>
        public FunctionCallHighlighter(IHighlighter inner, Action<Exception> onFailure)
        {
            _inner = inner;
            _onFailure = onFailure;
        }

        private static HighlightingColor CreateColor()
        {
            var color = new HighlightingColor { Name = "Function", Underline = false };
            color.Freeze();
            return color;
        }

        /// <summary>
        /// Adds a Function section for each name the line's sections leave uncovered, keeping the
        /// sections in offset order. A throw leaves the line as it was and is passed to
        /// <paramref name="onFailure"/>: it costs only this line its function colors.
        /// </summary>
        internal static void AddTo(HighlightedLine line, Action<Exception> onFailure)
        {
            try
            {
                var documentLine = line.DocumentLine;
                if (documentLine.Length > MarkdownLineTokenizer.MaxInlineLength) return;
                var calls = FunctionCalls.Find(line.Document.GetText(documentLine), MarkdownLineTokenizer.MaxInlineLength);
                if (calls.Count == 0) return;

                // Sections are sorted by offset: the ones starting before a name's end come first,
                // and if none of them reaches past the name's start, nothing covers the name.
                var sections = line.Sections;
                var merged = new List<HighlightedSection>(sections.Count + calls.Count);
                int next = 0;
                int reach = int.MinValue;
                bool added = false;
                foreach (var (start, length) in calls)
                {
                    int from = documentLine.Offset + start;
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
                if (!added) return;
                for (; next < sections.Count; next++) merged.Add(sections[next]);

                sections.Clear();
                foreach (var section in merged) sections.Add(section);
            }
            catch (Exception ex)
            {
                onFailure(ex);
            }
        }

        public HighlightedLine HighlightLine(int lineNumber)
        {
            var line = _inner.HighlightLine(lineNumber);
            AddTo(line, Failed);
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
