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

        [Fact]
        public void The_two_note_switches_are_cards_after_the_ai_toggle_in_order()
        {
            string xaml = Read("SettingsWindow.xaml");
            int toggle = xaml.IndexOf("x:Name=\"PadAiToggle\"", StringComparison.Ordinal);
            int ai = xaml.IndexOf("x:Name=\"AiSection\"", StringComparison.Ordinal);
            string after = xaml.Substring(toggle, ai - toggle);

            int provider = after.IndexOf("x:Name=\"PadAiProviderButton\"", StringComparison.Ordinal);
            int askTitle = after.IndexOf("Text=\"Let Ask MicaStats search your notes\"", StringComparison.Ordinal);
            int askHint = after.IndexOf("Text=\"Ask MicaStats can look up passages and read notes to answer a question.\"", StringComparison.Ordinal);
            int askToggle = after.IndexOf("x:Name=\"AiNotesInAskToggle\" Toggled=\"OnPadToggled\"", StringComparison.Ordinal);
            int askText = after.IndexOf("x:Name=\"AiNotesInAskText\"", StringComparison.Ordinal);
            int mcpTitle = after.IndexOf("Text=\"Let MCP clients search your notes\"", StringComparison.Ordinal);
            int mcpHint = after.IndexOf("Text=\"Programs such as Claude Code, connected through MCP, get two read-only tools: search_notes and get_note.\"", StringComparison.Ordinal);
            int mcpToggle = after.IndexOf("x:Name=\"AiNotesInMcpToggle\" Toggled=\"OnPadToggled\"", StringComparison.Ordinal);
            int mcpText = after.IndexOf("x:Name=\"AiNotesInMcpText\"", StringComparison.Ordinal);

            Assert.True(0 < askTitle && askTitle < askHint && askHint < askToggle && askToggle < askText, "ask card");
            Assert.True(askText < mcpTitle && mcpTitle < mcpHint && mcpHint < mcpToggle && mcpToggle < mcpText, "mcp card, after the ask card");
            Assert.True(provider > 0, "the provider button is still there");
        }

        [Fact]
        public void The_note_switches_are_loaded_with_their_texts_and_saved_with_the_others()
        {
            string code = Read("SettingsWindow.xaml.cs");
            int load = code.IndexOf("private void LoadPadSettings", StringComparison.Ordinal);
            int toggled = code.IndexOf("private void OnPadToggled", StringComparison.Ordinal);
            Assert.True(load > 0 && toggled > load);
            string loading = code.Substring(load, toggled - load);

            Assert.Contains("AiNotesInAskToggle.IsOn = cfg.AiNotesInAsk;", loading);
            Assert.Contains("AiNotesInMcpToggle.IsOn = cfg.AiNotesInMcp;", loading);
            Assert.Contains("AiNotesInAskText.Text = Kil0bitSystemMonitor.Services.Pad.Ai.PadAiPrivacy.NotesInAsk(cfg.AiProvider, cfg.AiCompatibleBaseUrl);", loading);
            Assert.Contains("AiNotesInMcpText.Text = Kil0bitSystemMonitor.Services.Pad.Ai.PadAiPrivacy.NotesInMcp;", loading);
            Assert.True(loading.IndexOf("_loadingPad = true;", StringComparison.Ordinal) < loading.IndexOf("AiNotesInAskToggle.IsOn", StringComparison.Ordinal));

            string saving = code.Substring(toggled, code.IndexOf("OnPadAiProviderSettings", toggled, StringComparison.Ordinal) - toggled);
            Assert.Contains("cfg.AiNotesInAsk = AiNotesInAskToggle.IsOn;", saving);
            Assert.Contains("cfg.AiNotesInMcp = AiNotesInMcpToggle.IsOn;", saving);
            Assert.Contains("_config.SaveConfig();", saving);
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
        public void The_guide_documents_the_note_tools_and_diagram_help()
        {
            string guide = Read("GUIDE.md").Replace("\r\n", "\n", StringComparison.Ordinal);
            foreach (string needle in new[]
            {
                "search_notes", "get_note", "Let Ask MicaStats search your notes", "Let MCP clients search your notes",
                "**Draw as diagram**", "**Fix with AI**", "### Notes in Ask MicaStats and MCP", "[credential]",
                "New conversation", "MicaStats is not running", "Insert below", "Replace selection",
            })
                Assert.Contains(needle, guide, StringComparison.Ordinal);

            string ai = GuideAiSection();
            Assert.Contains("**Draw as diagram**", ai, StringComparison.Ordinal);
            Assert.Contains("**Fix with AI**", ai, StringComparison.Ordinal);
            Assert.Contains("only that source", ai, StringComparison.Ordinal);
        }

        [Fact]
        public void The_readme_documents_the_note_tools_and_diagram_help_in_both_languages_and_keeps_its_lists_apart()
        {
            string readme = Read("README.md").Replace("\r\n", "\n", StringComparison.Ordinal);
            int thai = readme.IndexOf("## เกี่ยวกับ MicaStats", StringComparison.Ordinal);
            foreach (string part in new[] { readme.Substring(0, thai), readme.Substring(thai) })
            {
                foreach (string needle in new[] { "search_notes", "get_note", "Let Ask MicaStats search your notes", "Let MCP clients search your notes", "**Draw as diagram**", "**Fix with AI**" })
                    Assert.Contains(needle, part, StringComparison.Ordinal);
            }

            // The line after each What's New list is blank, so the next paragraph is not folded into a bullet.
            string[] lines = readme.Split('\n');
            foreach (string start in new[] { "**v1.15.0** — task tracking", "**v1.15.0** — ติดตามงาน" })
            {
                int i = Array.FindIndex(lines, l => l.StartsWith(start, StringComparison.Ordinal));
                Assert.True(i >= 0);
                Assert.Equal("", lines[i + 1]);
                int j = i + 2;
                while (lines[j].StartsWith("* ", StringComparison.Ordinal)) j++;
                Assert.True(j > i + 3, "both bullets are in the block");
                Assert.Equal("", lines[j]);
                Assert.StartsWith("**v1.14.0** —", lines[j + 1], StringComparison.Ordinal);
            }
            Assert.Single(readme.Split('\n'), l => l.StartsWith("* **Markdown the way Wiki.js shows it**", StringComparison.Ordinal));
        }

        [Fact]
        public void The_guide_describes_what_an_ai_answer_shows_and_the_pane_and_window_changes()
        {
            string guide = Read("GUIDE.md").Replace("\r\n", "\n", StringComparison.Ordinal);

            foreach (string needle in new[]
            {
                "**Try again**", "**Source**", "**Copy image**", "**Copy source**", "Drawing the diagram…", "This diagram could not be drawn",
                "splitter", "double-click", "Finished in", "Waiting for", "Writing…", "Thinking…", "Reading live status…",
                "of 100 today", "latest text", "remembers its size", "Draw diagrams", "at most 8 diagrams", "Drawing the diagram…", "llm.example.com",
            })
                Assert.Contains(needle, guide, StringComparison.Ordinal);
            Assert.DoesNotContain("api.openai.com", guide, StringComparison.Ordinal);
        }

        [Fact]
        public void The_readme_describes_what_an_ai_answer_shows_and_the_pane_changes_in_both_languages()
        {
            string readme = Read("README.md").Replace("\r\n", "\n", StringComparison.Ordinal);
            int thai = readme.IndexOf("## เกี่ยวกับ MicaStats", StringComparison.Ordinal);

            foreach (string part in new[] { readme.Substring(0, thai), readme.Substring(thai) })
            {
                foreach (string needle in new[] { "**Try again**", "**Source**", "**Copy image**", "Drawing the diagram…", "splitter", "Finished in", "**Draw diagrams**" })
                    Assert.Contains(needle, part, StringComparison.Ordinal);

                // The new items are in the v1.15.0 block, which ends where v1.14.0 starts.
                int since = part.IndexOf("**v1.15.0** —", StringComparison.Ordinal);
                Assert.True(since >= 0);
                int end = part.IndexOf("**v1.14.0** —", since, StringComparison.Ordinal);
                string block = part.Substring(since, end - since);
                Assert.Contains("splitter", block, StringComparison.Ordinal);
                Assert.Contains("Finished in", block, StringComparison.Ordinal);
                Assert.Contains("Mermaid", block, StringComparison.Ordinal);
            }
        }

        // ---- what the final review of the AI chat UI settled, in the guide and in both parts of the README ----

        /// <summary>A file with every run of white space as one space: its bullets wrap, and a sentence is looked for whole.</summary>
        private static string Flat(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

        /// <summary>A tag written bare is dropped by a Markdown renderer: <c>br</c> is always in backticks.</summary>
        private static void AssertNoBareBreakTag(string name, string text) =>
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(text, @"(?<!`)<br\s*/?>"), name + " writes a br tag outside backticks");

        [Fact]
        public void The_guide_says_what_the_final_review_of_the_ai_chat_ui_settled()
        {
            string guide = Read("GUIDE.md").Replace("\r\n", "\n", StringComparison.Ordinal);
            string flat = Flat(guide);

            foreach (string needle in new[]
            {
                // What an answer shows.
                "more than 12 columns or 100 rows stays plain text",
                "a ninth diagram is shown as code",
                "while its block is still being written",
                "under a numbered step",
                "`<br>` in a table cell is a line break",
                "need the Microsoft Edge WebView2 Runtime, as diagrams in notes do",
                "is never loaded: it is shown as \"!\" and a link",
                // The two Try again buttons.
                "runs the request again",
                "draws that diagram again",
                // The splitter.
                "the editor is held to 320",
                "the pane keeps 260 and the editor gets the rest",
                "has no splitter",
                // Ask AI on a note that is not Markdown.
                "starts with **Source** on",
                // A credential stored from a note.
                "ends an Ask MicaStats conversation that had read notes",
                "This conversation was cleared: text from a note it had read was stored as a credential.",
            })
                Assert.Contains(needle, flat, StringComparison.Ordinal);

            // The numbers stand with the splitter, in its own bullet.
            string splitter = Assert.Single(GuideAiSection().Split('\n'), l => l.StartsWith("* **The pane has a splitter**", StringComparison.Ordinal));
            foreach (string number in new[] { "260", "900", "320" }) Assert.Contains(number, splitter, StringComparison.Ordinal);

            AssertNoBareBreakTag("GUIDE.md", guide);
        }

        [Fact]
        public void The_readme_says_what_the_final_review_of_the_ai_chat_ui_settled_in_both_languages()
        {
            string readme = Read("README.md").Replace("\r\n", "\n", StringComparison.Ordinal);
            int thai = readme.IndexOf("## เกี่ยวกับ MicaStats", StringComparison.Ordinal);
            string english = readme.Substring(0, thai);
            string thaiPart = readme.Substring(thai);

            foreach (string part in new[] { english, thaiPart })
            {
                // Shown as it is on screen or as it is written, so the same in both languages.
                foreach (string needle in new[] { "Waiting for claude-sonnet-5-5…", "`<br>`", "WebView2 Runtime", "**Source**", "**Try again**" })
                    Assert.Contains(needle, part, StringComparison.Ordinal);

                // The splitter's own bullet carries its three numbers.
                Assert.Contains(part.Split('\n'), l => l.StartsWith("* ", StringComparison.Ordinal) && l.Contains("splitter", StringComparison.Ordinal)
                    && l.Contains("260", StringComparison.Ordinal) && l.Contains("900", StringComparison.Ordinal) && l.Contains("320", StringComparison.Ordinal));
                // The table limits stand together.
                Assert.Contains(part.Split('\n'), l => l.Contains("12", StringComparison.Ordinal) && l.Contains("100", StringComparison.Ordinal)
                    && l.Contains("`<br>`", StringComparison.Ordinal));
            }

            foreach (string needle in new[]
            {
                "more than 12 columns or 100 rows stays plain text",
                "a ninth diagram in one answer is shown as code",
                "while its block is still being written",
                "under a numbered step",
                "runs the request again",
                "draws it again",
                "the editor is held to 320",
                "the pane keeps 260 and the editor gets the rest",
                "has no splitter",
                "starts with **Source** on",
                "is never loaded: it is shown as a link",
                "ends an Ask MicaStats conversation that had read notes",
            })
                Assert.Contains(needle, english, StringComparison.Ordinal);

            foreach (string needle in new[]
            {
                "เกิน 12 คอลัมน์หรือ 100 แถวจะคงเป็นข้อความธรรมดา",
                "แผนภาพที่เก้าในคำตอบเดียวจะแสดงเป็นโค้ด",
                "ระหว่างที่บล็อกของมันยังเขียนไม่จบ",
                "ใต้ขั้นตอนที่มีเลขกำกับ",
                "ส่งคำขอใหม่อีกครั้ง",
                "วาดแผนภาพนั้นใหม่",
                "ตัวแก้ไขจะเหลืออย่างน้อย 320",
                "แผงจะกว้าง 260 ส่วนที่เหลือเป็นของตัวแก้ไข",
                "ไม่มีตัวแบ่งแผง",
                "จะเริ่มโดยเปิด **Source** ไว้",
                "ไม่ถูกโหลดเลย แต่แสดงเป็นลิงก์",
                "จบบทสนทนาใน Ask MicaStats ที่เคยอ่านโน้ต",
            })
                Assert.Contains(needle, thaiPart, StringComparison.Ordinal);

            // The Thai part says "(the model's name)" in Thai, in both places.
            Assert.DoesNotContain("the model's name", thaiPart, StringComparison.Ordinal);
            Assert.Equal(2, thaiPart.Split("\"Waiting for claude-sonnet-5-5…\" (ชื่อโมเดล)", StringSplitOptions.None).Length - 1);
            Assert.Equal(2, english.Split("\"Waiting for claude-sonnet-5-5…\" (the model's name)", StringSplitOptions.None).Length - 1);

            // No real service address, as in the guide.
            Assert.DoesNotContain("api.openai.com", readme, StringComparison.Ordinal);
            AssertNoBareBreakTag("README.md", readme);
        }

        [Fact]
        public void The_guide_and_the_readme_keep_a_blank_line_before_every_heading()
        {
            foreach (string file in new[] { "GUIDE.md", "README.md" })
            {
                string[] lines = Read(file).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                bool fenced = false;
                for (int i = 1; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith("```", StringComparison.Ordinal)) fenced = !fenced;
                    if (!fenced && lines[i].StartsWith("#", StringComparison.Ordinal))
                        Assert.True(lines[i - 1].Length == 0, file + " line " + (i + 1) + ": no blank line before the heading");
                }
            }
        }

        [Fact]
        public void The_readme_announces_ai_and_lists_the_shortcuts_in_both_languages()
        {
            string readme = Read("README.md");
            int thai = readme.IndexOf("## เกี่ยวกับ MicaStats", StringComparison.Ordinal);
            Assert.True(thai > 0);
            string english = readme.Substring(0, thai);
            string thaiPart = readme.Substring(thai);

            int englishRelease = english.IndexOf("**v1.15.0** —", StringComparison.Ordinal);
            Assert.True(englishRelease >= 0 && englishRelease < english.IndexOf("**v1.14.0** —", StringComparison.Ordinal));
            Assert.Contains("\n#### AI\n", english);
            int thaiRelease = thaiPart.IndexOf("**v1.15.0** —", StringComparison.Ordinal);
            Assert.True(thaiRelease >= 0 && thaiRelease < thaiPart.IndexOf("**v1.14.0** —", StringComparison.Ordinal));

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
