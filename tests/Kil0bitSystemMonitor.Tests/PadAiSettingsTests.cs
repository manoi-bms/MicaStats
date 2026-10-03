using System;
using System.IO;
using System.Linq;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>
    /// Settings → MicaPad → AI and its docs (MicaPad AI spec 1). The settings window reads and writes
    /// %APPDATA% through ConfigService when built, so, like the other settings tests, these pin its
    /// markup and wiring as text; the privacy line itself is the pure PadAiPrivacy.
    /// </summary>
    public class PadAiSettingsTests
    {
        private static string Read(string name) => File.ReadAllText(Path.Combine(PadWindowTests.RepoRoot(), name));

        [Fact]
        public void The_setting_is_off_for_a_new_config()
        {
            Assert.False(new AppConfig().PadAiEnabled);
        }

        [Fact]
        public void The_privacy_line_follows_the_provider()
        {
            Assert.Equal(
                "Text you run an AI action on, and passages found for a question, go to Anthropic (api.anthropic.com). Stored credentials are never sent.",
                PadAiPrivacy.Describe(AiProviders.Claude, ""));
            Assert.Equal("Everything stays on this PC (localhost).",
                PadAiPrivacy.Describe(AiProviders.OpenAiCompatible, "http://localhost:11434/v1"));
        }

        [Fact]
        public void The_card_the_privacy_line_and_the_button_are_in_the_pad_section()
        {
            string xaml = Read("SettingsWindow.xaml");
            int pad = xaml.IndexOf("x:Name=\"PadSection\"", StringComparison.Ordinal);
            int ai = xaml.IndexOf("x:Name=\"AiSection\"", StringComparison.Ordinal);
            int search = xaml.IndexOf("x:Name=\"PadSearchPanel\"", StringComparison.Ordinal);
            Assert.True(pad >= 0 && search > pad && ai > search);
            string section = xaml.Substring(search, ai - search);

            Assert.Contains("Text=\"AI\" FontSize=\"18\" FontWeight=\"SemiBold\"", section);
            Assert.Contains("Text=\"Use AI in MicaPad\"", section);
            Assert.Contains("Text=\"Improve, translate, summarize or explain selected text, and get answers from your notes in Search notes. Nothing is sent until you run an action.\"", section);
            Assert.Contains("x:Name=\"PadAiToggle\" Toggled=\"OnPadToggled\"", section);
            Assert.Contains("x:Name=\"PadAiPrivacyText\"", section);
            Assert.Contains("x:Name=\"PadAiProviderButton\"", section);
            Assert.Contains("Content=\"AI provider settings…\"", section);
            Assert.Contains("Click=\"OnPadAiProviderSettings\"", section);
        }

        [Fact]
        public void The_toggle_is_loaded_saved_and_the_privacy_line_refreshed_each_time_the_section_shows()
        {
            string code = Read("SettingsWindow.xaml.cs");

            Assert.Contains("PadAiToggle.IsOn = cfg.PadAiEnabled;", code);
            Assert.Contains("PadAiPrivacyText.Text = Kil0bitSystemMonitor.Services.Pad.Ai.PadAiPrivacy.Describe(cfg.AiProvider, cfg.AiCompatibleBaseUrl);", code);
            Assert.Contains("cfg.PadAiEnabled = PadAiToggle.IsOn;", code);
            Assert.Contains("SelectSection(\"AI\");", code);
            // LoadPadSettings runs on every visit to the MicaPad section, which is what refreshes the line.
            Assert.Contains("case \"MicaPad\": PadSection.Visibility = Visibility.Visible; LoadPadSettings(); break;", code);
        }

        // ---- docs ----

        [Fact]
        public void The_guide_has_the_ai_section_with_the_decided_behaviour()
        {
            string guide = Read("GUIDE.md");
            int start = guide.IndexOf("### AI in MicaPad", StringComparison.Ordinal);
            Assert.True(start > 0);
            string section = guide.Substring(start);
            section = section.Substring(0, section.IndexOf("\n### ", 5, StringComparison.Ordinal));

            Assert.Contains("Ctrl+Shift+A", section);
            Assert.Contains("Ctrl+Enter", section);
            Assert.Contains("Use AI in MicaPad", section);
            Assert.Contains("Replace selection", section);
            Assert.Contains("Try again", section);
            Assert.Contains("Answering from", section);
            Assert.Contains("Answered from", section);
            Assert.Contains("never clickable", section);
            Assert.Contains("never sent", section);
            Assert.Contains("daily limit", section);
            Assert.Contains("Turn on Settings → MicaPad → AI to get answers", section);
        }

        /// <summary>The guide's AI section, with each bullet's wrapped lines joined into one.</summary>
        private static string GuideAiSection()
        {
            string guide = Read("GUIDE.md").Replace("\r\n", "\n", StringComparison.Ordinal);
            int start = guide.IndexOf("### AI in MicaPad", StringComparison.Ordinal);
            string section = guide.Substring(start);
            section = section.Substring(0, section.IndexOf("\n### ", 5, StringComparison.Ordinal));
            return section.Replace("\n  ", " ", StringComparison.Ordinal);
        }

        [Fact]
        public void The_guide_says_what_a_question_sends_and_denies_a_title_only_for_an_action()
        {
            string sent = Assert.Single(GuideAiSection().Split('\n'), l => l.StartsWith("* **What is sent**", StringComparison.Ordinal));

            // An action on text: the text and nothing about the note.
            Assert.Contains("For an action on text", sent, StringComparison.Ordinal);
            Assert.Contains("no title, no other note, no file path", sent, StringComparison.Ordinal);
            // A question: up to 8 passages from any note, each under its note's title, heading and line numbers.
            int question = sent.IndexOf("For a question", StringComparison.Ordinal);
            Assert.True(question > sent.IndexOf("no file path", StringComparison.Ordinal), "the denial comes before the question, and covers the action only");
            string forQuestion = sent.Substring(question);
            Assert.Contains("up to 8 passages", forQuestion, StringComparison.Ordinal);
            Assert.Contains("title", forQuestion, StringComparison.Ordinal);
            Assert.Contains("heading", forQuestion, StringComparison.Ordinal);
            Assert.Contains("line numbers", forQuestion, StringComparison.Ordinal);
            Assert.Contains("file name", forQuestion, StringComparison.Ordinal);
            Assert.DoesNotContain("no title", forQuestion, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_guide_does_not_promise_Insert_below_where_it_is_off()
        {
            string section = GuideAiSection();

            Assert.DoesNotContain("**Insert below** and **Copy** still work", section, StringComparison.Ordinal);
            string replace = Assert.Single(section.Split('\n'), l => l.StartsWith("* **Replace selection** is offered only", StringComparison.Ordinal));
            Assert.Contains("failed", replace, StringComparison.Ordinal);          // a failed reply: Copy only
            Assert.Contains("another tab", replace, StringComparison.Ordinal);     // and so on another tab
            Assert.Contains("read-only", replace, StringComparison.Ordinal);
        }

        [Fact]
        public void The_readme_says_Ctrl_Shift_A_opens_Ask_AI_and_what_a_question_sends_in_both_languages()
        {
            string readme = Read("README.md");
            int thai = readme.IndexOf("## เกี่ยวกับ MicaStats", StringComparison.Ordinal);
            string english = readme.Substring(0, thai);
            string thaiPart = readme.Substring(thai);

            // What's New: the shortcut opens Ask AI…; it does not run Improve, Fix and the rest.
            string news = Assert.Single(english.Split('\n'), l => l.StartsWith("* **AI in MicaPad.**", StringComparison.Ordinal));
            Assert.DoesNotContain("menu or **Ctrl+Shift+A**", news, StringComparison.Ordinal);
            Assert.Contains("**Ask AI…** (**Ctrl+Shift+A**)", news, StringComparison.Ordinal);
            string thaiNews = Assert.Single(thaiPart.Split('\n'), l => l.StartsWith("* **AI ใน MicaPad**", StringComparison.Ordinal));
            Assert.DoesNotContain("**AI** หรือ **Ctrl+Shift+A**", thaiNews, StringComparison.Ordinal);
            Assert.Contains("**Ask AI…** (**Ctrl+Shift+A**)", thaiNews, StringComparison.Ordinal);

            // Private: the denial is the action's; a question sends each passage with its note's title, heading and lines.
            Assert.DoesNotContain("or a question and its passages, is sent", english, StringComparison.Ordinal);
            Assert.Contains("each with its note's title, heading and line numbers", english, StringComparison.Ordinal);
            Assert.DoesNotContain("หรือคำถามกับข้อความที่เกี่ยวข้อง โดยห่อไว้", thaiPart, StringComparison.Ordinal);
            Assert.Contains("ชื่อโน้ต หัวข้อ และเลขบรรทัด", thaiPart, StringComparison.Ordinal);
        }

        [Fact]
        public void The_readme_announces_ai_and_lists_the_shortcuts_in_both_languages()
        {
            string readme = Read("README.md");
            int thai = readme.IndexOf("## เกี่ยวกับ MicaStats", StringComparison.Ordinal);
            Assert.True(thai > 0);
            string english = readme.Substring(0, thai);
            string thaiPart = readme.Substring(thai);

            Assert.True(english.IndexOf("**Since v1.14.0** — coming in the next release:", StringComparison.Ordinal)
                        < english.IndexOf("**v1.14.0** —", StringComparison.Ordinal));
            Assert.Contains("\n#### AI\n", english);
            Assert.True(thaiPart.IndexOf("**หลัง v1.14.0** (จะมาในรีลีสถัดไป):", StringComparison.Ordinal)
                        < thaiPart.IndexOf("**v1.14.0** —", StringComparison.Ordinal));

            foreach (string part in new[] { english, thaiPart })
            {
                string[] rows = part.Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal)).ToArray();
                Assert.Contains(rows, r => r.Contains("**Ctrl+Shift+A**", StringComparison.Ordinal));
                Assert.Contains(rows, r => r.Contains("**Ctrl+Enter**", StringComparison.Ordinal));
            }

            // The Wiki.js bullet stays one line (CodeCopyTests).
            Assert.Single(readme.Split('\n'), l => l.StartsWith("* **Markdown the way Wiki.js shows it**", StringComparison.Ordinal));
        }
    }
}
