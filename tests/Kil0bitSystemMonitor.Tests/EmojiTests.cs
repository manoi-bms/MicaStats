using System.Linq;
using System.Windows;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Emoji codes (spec 5, R5).</summary>
    public class EmojiTests
    {
        [Fact]
        public void The_table_holds_every_gemoji_name()
        {
            Assert.Equal(1913, Emoji.Count);
            Assert.Equal("\uD83D\uDE04", Emoji.GlyphOf("smile"));
            Assert.Equal("\uD83D\uDC4D", Emoji.GlyphOf("+1"));
            Assert.Equal("\uD83D\uDE80", Emoji.GlyphOf("rocket"));
            Assert.Null(Emoji.GlyphOf("not-an-emoji"));
        }

        [Fact]
        public void Codes_are_found_with_their_colons()
        {
            var found = Emoji.Find(":smile: and :tada:");

            Assert.Equal(new[] { (0, 7), (12, 6) }, found.Select(f => (f.Start, f.Length)));
            Assert.Equal("\uD83C\uDF89", found[1].Glyph);
            Assert.Single(Emoji.Find("a :qq: :smile:"));
            Assert.Equal(7, Emoji.Find("a :qq: :smile:")[0].Start);
        }

        [Theory]
        [InlineData("12:30:45")]
        [InlineData("::")]
        [InlineData(":SMILE:")]
        [InlineData("`:smile:`")]
        [InlineData(":smile")]
        [InlineData("http://example.com:8080/")]
        [InlineData("user:id:42")]
        [InlineData("00:1a:ab:cd:ef:01")]
        [InlineData("a[1:-1:2]")]
        [InlineData("ratio 1:100:2")]
        [InlineData("en:us:utf8")]
        public void Emoji_need_a_known_name_between_colons(string line) => Assert.Empty(Emoji.Find(line));

        [Theory]
        [InlineData("Done :rocket:", 5)]
        [InlineData("(:smile:)", 1)]
        [InlineData(":smile:,", 0)]
        public void A_code_next_to_punctuation_or_a_space_is_an_emoji(string line, int start) =>
            Assert.Equal(start, Assert.Single(Emoji.Find(line)).Start);

        [Fact]
        public void Two_codes_back_to_back_are_both_emoji() =>
            Assert.Equal(new[] { (0, 4), (4, 7) }, Emoji.Find(":+1::smile:").Select(f => (f.Start, f.Length)));

        [Fact]
        public void The_generator_shows_a_glyph_for_the_whole_code_outside_fences() => UiThread.Run(() =>
        {
            var cache = new MarkdownDocumentCache();
            var view = new TextView { Document = new TextDocument(":rocket: go\n```\n:rocket:\n```") };
            view.ElementGenerators.Add(new EmojiGenerator(cache));
            view.Measure(new Size(600, 400));
            view.Arrange(new Rect(0, 0, 600, 400));
            view.EnsureVisualLines();

            var first = view.GetVisualLine(1)!.Elements.First();
            Assert.IsType<FormattedTextElement>(first);
            Assert.Equal(8, first.DocumentLength);
            Assert.DoesNotContain(view.GetVisualLine(3)!.Elements, e => e is FormattedTextElement);
        });

        [Fact]
        public void Markdown_tabs_get_emoji_and_others_do_not() => UiThread.Run(() =>
        {
            var editor = new ICSharpCode.AvalonEdit.TextEditor { Document = new TextDocument(":smile:") };
            var language = new EditorLanguage(editor, () => PadPalette.Dark, folds: false);

            language.Apply(PadLanguages.Markdown);
            Assert.Single(editor.TextArea.TextView.ElementGenerators.OfType<EmojiGenerator>());

            language.Apply(PadLanguages.ById("json")!);
            Assert.Empty(editor.TextArea.TextView.ElementGenerators.OfType<EmojiGenerator>());
        });
    }
}
