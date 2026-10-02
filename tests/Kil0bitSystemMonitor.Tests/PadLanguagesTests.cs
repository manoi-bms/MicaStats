using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Which language a tab uses: the extension table, Auto for notes, the Markdown switch, an explicit choice and the size limit.</summary>
    public class PadLanguagesTests
    {
        [Theory]
        [InlineData(@"C:\n\a.md", "markdown")]
        [InlineData(@"C:\n\a.markdown", "markdown")]
        [InlineData(@"C:\n\a.txt", "markdown")]
        [InlineData(@"C:\n\a.json", "json")]
        [InlineData(@"C:\n\A.JSON", "json")]
        [InlineData(@"C:\n\a.jsonc", "json")]
        [InlineData(@"C:\n\App.xaml", "xml")]
        [InlineData(@"C:\n\a.csproj", "xml")]
        [InlineData(@"C:\n\a.svg", "xml")]
        [InlineData(@"C:\n\a.htm", "html")]
        [InlineData(@"C:\n\a.cs", "csharp")]
        [InlineData(@"C:\n\a.ts", "typescript")]
        [InlineData(@"C:\n\a.tsx", "typescript")]
        [InlineData(@"C:\n\a.mts", "typescript")]
        [InlineData(@"C:\n\deploy.sh", "shell")]
        [InlineData(@"C:\n\.bashrc", "shell")]
        [InlineData(@"C:\n\Unit1.pas", "pascal")]
        [InlineData(@"C:\n\Project1.dpr", "pascal")]
        [InlineData(@"C:\n\main.go", "go")]
        [InlineData(@"C:\n\lib.rs", "rust")]
        [InlineData(@"C:\n\app.rb", "ruby")]
        [InlineData(@"C:\n\Gemfile", "ruby")]
        [InlineData(@"C:\n\Rakefile", "ruby")]
        [InlineData(@"C:\n\rakefile", "ruby")]
        [InlineData(@"C:\n\Main.kt", "kotlin")]
        [InlineData(@"C:\n\build.gradle.kts", "kotlin")]
        [InlineData(@"C:\n\Dockerfile", "dockerfile")]
        [InlineData(@"C:\n\dockerfile", "dockerfile")]
        [InlineData(@"C:\n\Dockerfile.dev", "dockerfile")]
        [InlineData(@"C:\n\app.dockerfile", "dockerfile")]
        [InlineData(@"C:\n\Containerfile", "dockerfile")]
        [InlineData(@"C:\n\a.jsx", "javascript")]
        [InlineData(@"C:\n\a.css", "css")]
        [InlineData(@"C:\n\a.psm1", "powershell")]
        [InlineData(@"C:\n\a.py", "python")]
        [InlineData(@"C:\n\a.sql", "sql")]
        [InlineData(@"C:\n\a.hpp", "cpp")]
        [InlineData(@"C:\n\a.java", "java")]
        [InlineData(@"C:\n\a.php", "php")]
        [InlineData(@"C:\n\a.bas", "vb")]
        [InlineData(@"C:\n\a.patch", "diff")]
        [InlineData(@"C:\n\a.cfg", "ini")]
        [InlineData(@"C:\n\.editorconfig", "ini")]
        [InlineData(@"C:\n\a.yml", "yaml")]
        [InlineData(@"C:\n\a.cmd", "batch")]
        [InlineData(@"C:\n\micastats.log", "log")]
        [InlineData(@"C:\n\a.csv", "plain")]
        [InlineData(@"C:\n\README", "plain")]
        public void Auto_picks_by_file_type(string path, string expected)
        {
            Assert.Equal(expected, PadLanguages.Resolve(null, path, markdownOn: true, textLength: 10).Language.Id);
        }

        [Fact]
        public void Notes_are_markdown_unless_markdown_is_off()
        {
            Assert.Same(PadLanguages.Markdown, PadLanguages.Resolve(null, null, true, 10).Language);
            Assert.Same(PadLanguages.Plain, PadLanguages.Resolve(null, null, false, 10).Language);
        }

        [Fact]
        public void With_markdown_off_md_and_txt_are_plain_but_code_keeps_its_colors()
        {
            Assert.Same(PadLanguages.Plain, PadLanguages.Resolve(null, @"C:\n\a.md", false, 10).Language);
            Assert.Same(PadLanguages.Plain, PadLanguages.Resolve(null, @"C:\n\a.txt", false, 10).Language);
            Assert.Equal("json", PadLanguages.Resolve(null, @"C:\n\a.json", false, 10).Language.Id);
        }

        [Fact]
        public void An_explicit_choice_wins_over_auto_and_the_switch()
        {
            Assert.Same(PadLanguages.Plain, PadLanguages.Resolve("plain", @"C:\n\a.json", true, 10).Language);
            Assert.Same(PadLanguages.Markdown, PadLanguages.Resolve("markdown", null, false, 10).Language);
            Assert.Equal("json", PadLanguages.Resolve("JSON", null, true, 10).Language.Id);
        }

        [Fact]
        public void An_unknown_choice_falls_back_to_auto()
        {
            Assert.Equal("json", PadLanguages.Resolve("klingon", @"C:\n\a.json", true, 10).Language.Id);
        }

        [Fact]
        public void Text_over_the_limit_is_shown_plain_and_says_so()
        {
            var atLimit = PadLanguages.Resolve(null, @"C:\n\a.json", true, PadLanguages.MaxFormattedChars);
            Assert.False(atLimit.TooLarge);
            Assert.Equal("JSON", atLimit.DisplayName);

            var over = PadLanguages.Resolve(null, @"C:\n\a.json", true, PadLanguages.MaxFormattedChars + 1);
            Assert.True(over.TooLarge);
            Assert.Equal("Plain text (large)", over.DisplayName);
            Assert.Same(PadLanguages.Plain, over.Effective);
            Assert.Equal("json", over.Language.Id);
        }

        [Fact]
        public void Languages_are_listed_once_with_names_and_fold_kinds()
        {
            var all = PadLanguages.All;
            Assert.Equal(28, all.Count);
            Assert.Equal(all.Count, all.Select(l => l.Id).Distinct().Count());
            Assert.All(all, l => Assert.False(string.IsNullOrWhiteSpace(l.Name)));
            Assert.Same(PadLanguages.Plain, all[0]);
            Assert.Same(PadLanguages.Markdown, all[1]);
            Assert.Equal(PadFoldKind.Braces, PadLanguages.ById("json")!.Fold);
            Assert.Equal(PadFoldKind.Xml, PadLanguages.ById("html")!.Fold);
            Assert.Equal(PadFoldKind.Headings, PadLanguages.Markdown.Fold);
            Assert.Equal(PadFoldKind.None, PadLanguages.ById("python")!.Fold);
            foreach (string id in new[] { "typescript", "kotlin", "go", "rust" }) Assert.Equal(PadFoldKind.Braces, PadLanguages.ById(id)!.Fold);
            foreach (string id in new[] { "shell", "ruby", "pascal", "dockerfile" }) Assert.Equal(PadFoldKind.None, PadLanguages.ById(id)!.Fold);
            Assert.Equal("Pascal/Delphi", PadLanguages.ById("pascal")!.Name);
            Assert.All(new[] { "typescript", "shell", "pascal", "go", "dockerfile", "rust", "ruby", "kotlin" }, id => Assert.True(PadLanguages.ById(id)!.OwnDefinition));
            Assert.Null(PadLanguages.ById("markdown-fence"));
            Assert.NotNull(PadLanguages.ForFence("markdown-fence"));
            Assert.NotNull(PadLanguages.ForFence("Markdown-Fence"));
            Assert.Same(PadLanguages.ById("json"), PadLanguages.ForFence("json"));
            Assert.Null(PadLanguages.ForFence("klingon"));
            Assert.Null(PadLanguages.ForFence(null));
            Assert.Equal(new[]
            {
                "plain", "markdown", "json", "xml", "html", "csharp", "javascript", "typescript", "css", "powershell", "shell", "python", "sql",
                "cpp", "java", "kotlin", "go", "rust", "php", "ruby", "pascal", "vb", "diff", "dockerfile", "ini", "yaml", "batch", "log",
            }, all.Select(l => l.Id));
            Assert.True(PadLanguages.ById("log")!.OwnDefinition);
            Assert.Null(PadLanguages.Markdown.Definition);
        }

        [Fact]
        public void A_chosen_language_is_saved_with_the_note()
        {
            using var env = new PadTestEnv();
            var note = env.Workspace.NewNote();

            env.Workspace.SetLanguage(note, "json");
            env.Flush();
            Assert.Equal("json", env.Store.LoadMeta(note.Id)!.Language);

            env.Workspace.SetLanguage(note, "klingon");   // unknown: back to Auto
            env.Flush();
            Assert.Null(note.Meta.Language);
            Assert.Null(env.Store.LoadMeta(note.Id)!.Language);
        }
    }
}
