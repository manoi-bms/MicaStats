using Kil0bitSystemMonitor.Services.Pad.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class AiSessionTests
    {
        private static readonly AiSourceFacts Ok = new(true, false, true);

        private static AiSession Done(PadAiAction action, string source, string reply, bool fromSelection = true, string? instruction = null)
        {
            var s = new AiSession(action, source, fromSelection, instruction);
            s.Start();
            s.Append(reply);
            s.Complete(false);
            return s;
        }

        [Fact]
        public void A_clean_rewrite_from_a_selection_can_replace()
        {
            var v = Done(PadAiAction.Improve, "hello", "\nHello.\n").View(Ok);
            Assert.True(v.ShowReplace);
            Assert.True(v.CanReplace);
            Assert.True(v.CanInsert);
            Assert.True(v.CanCopy);
            Assert.True(v.CanRetry);
            Assert.True(v.CanShowChanges);
            Assert.Equal("", v.Status);
            Assert.Equal("Hello.", v.Result);
            Assert.Equal("hello", v.Original);
            Assert.Equal("Improve writing", v.Title);
            Assert.False(v.Markdown);
            Assert.False(v.Running);
        }

        [Fact]
        public void Reason_source_not_shown()
        {
            var v = Done(PadAiAction.Improve, "a", "b").View(new AiSourceFacts(false, true, false));
            Assert.False(v.CanReplace);
            Assert.False(v.CanInsert);
            Assert.False(v.CanRetry);
            Assert.Equal("Show the note this came from to apply it", v.Status);
        }

        [Fact]
        public void Try_again_is_offered_only_while_the_source_note_is_shown()
        {
            var elsewhere = new AiSourceFacts(false, false, true);

            var summary = Done(PadAiAction.Summarize, "text", "- point", false);
            Assert.True(summary.View(Ok).CanRetry);
            Assert.False(summary.View(elsewhere).CanRetry);   // another note's text must not be sent in its place
            Assert.True(summary.View(elsewhere).CanCopy);

            var failed = new AiSession(PadAiAction.Improve, "a", true);
            failed.Start(); failed.Fail("No key");
            Assert.True(failed.View(Ok).CanRetry);
            Assert.False(failed.View(elsewhere).CanRetry);

            // A read-only or changed source can still be tried again: only another note cannot.
            Assert.True(summary.View(new AiSourceFacts(true, true, false)).CanRetry);
        }

        [Fact]
        public void Reason_read_only_comes_before_changed()
        {
            var v = Done(PadAiAction.Improve, "a", "b").View(new AiSourceFacts(true, true, false));
            Assert.False(v.CanReplace);
            Assert.False(v.CanInsert);
            Assert.Equal("This note is read-only", v.Status);
        }

        [Fact]
        public void Reason_changed()
        {
            var v = Done(PadAiAction.Improve, "a", "b").View(new AiSourceFacts(true, false, false));
            Assert.False(v.CanReplace);
            Assert.True(v.CanInsert);
            Assert.Equal("The text changed since the request; use Insert below or Copy", v.Status);
        }

        [Fact]
        public void Reason_changed_comes_before_credential()
        {
            string src = "a {{secret:K7Q2M9XD}}";
            var v = Done(PadAiAction.Improve, src, "no placeholder").View(new AiSourceFacts(true, false, false));
            Assert.Equal("The text changed since the request; use Insert below or Copy", v.Status);
        }

        [Fact]
        public void Reason_credential_lost()
        {
            var v = Done(PadAiAction.Improve, "a {{secret:K7Q2M9XD}}", "a nothing").View(Ok);
            Assert.False(v.CanReplace);
            Assert.Equal(SecretMask.Lost, v.Status);
        }

        [Fact]
        public void A_stopped_rewrite_cannot_replace_but_can_insert()
        {
            var s = new AiSession(PadAiAction.Improve, "a", true);
            s.Start(); s.Append("partial"); s.Complete(true);
            var v = s.View(Ok);
            Assert.Equal("Stopped", v.Status);
            Assert.False(v.CanReplace);
            Assert.True(v.CanInsert);
            Assert.True(v.CanCopy);
        }

        [Fact]
        public void A_cut_short_rewrite_cannot_replace()
        {
            var s = new AiSession(PadAiAction.Improve, "a", true);
            s.Start(); s.Append("partial"); s.MarkCutShort(); s.Complete(false);
            var v = s.View(Ok);
            Assert.Equal("Cut short at the length limit", v.Status);
            Assert.False(v.CanReplace);
            Assert.True(v.CanInsert);
        }

        [Fact]
        public void A_failed_run_shows_the_message_and_offers_copy_only_if_text_arrived()
        {
            var s = new AiSession(PadAiAction.Improve, "a", true);
            s.Start(); s.Fail("No key");
            var v = s.View(Ok);
            Assert.Equal("No key", v.Status);
            Assert.False(v.CanReplace);
            Assert.False(v.CanInsert);
            Assert.False(v.CanCopy);
            Assert.False(v.CanShowChanges);
            Assert.True(v.CanRetry);

            var t = new AiSession(PadAiAction.Improve, "a", true);
            t.Start(); t.Append("some"); t.Fail("Broke");
            var w = t.View(Ok);
            Assert.True(w.CanCopy);
            Assert.False(w.CanInsert);
            Assert.False(w.CanReplace);
            Assert.Equal("Broke", w.Status);
        }

        [Fact]
        public void Summarize_has_no_replace_and_renders_markdown()
        {
            var v = Done(PadAiAction.Summarize, "text", "- point", true).View(Ok);
            Assert.False(v.ShowReplace);
            Assert.False(v.CanReplace);
            Assert.True(v.Markdown);
            Assert.False(v.CanShowChanges);
            Assert.True(v.CanInsert);
            Assert.Equal("", v.Status);
        }

        [Fact]
        public void Ask_from_a_selection_shows_replace()
        {
            var v = Done(PadAiAction.Ask, "text", "answer", true, "reword").View(Ok);
            Assert.True(v.ShowReplace);
            Assert.True(v.CanReplace);
            Assert.False(v.CanShowChanges);
        }

        [Fact]
        public void Ask_on_a_whole_note_has_no_replace()
        {
            var v = Done(PadAiAction.Ask, "text", "answer", false, "reword").View(Ok);
            Assert.False(v.ShowReplace);
            Assert.False(v.CanReplace);
            Assert.Equal("", v.Status);
        }

        [Fact]
        public void Ask_without_an_instruction_awaits_one()
        {
            var s = new AiSession(PadAiAction.Ask, "text", true);
            Assert.True(s.AwaitingInstruction);
            var v = s.View(Ok);
            Assert.True(v.AskForInstruction);
            Assert.False(v.CanRetry);
            Assert.False(v.Running);
            Assert.True(new AiSession(PadAiAction.Ask, "t", true, "  ").AwaitingInstruction);
            Assert.False(new AiSession(PadAiAction.Ask, "t", true, "do it").AwaitingInstruction);
            Assert.False(new AiSession(PadAiAction.Improve, "t", true).AwaitingInstruction);
        }

        [Fact]
        public void A_typed_instruction_is_cleaned_of_credentials()
        {
            var s = new AiSession(PadAiAction.Ask, "text", true, "use {{secret:K7Q2M9XD}} here");
            Assert.Equal("use [credential] here", s.Instruction);
            Assert.DoesNotContain("{{secret:", s.UserMessage);
        }

        [Fact]
        public void A_too_long_rewrite_is_refused()
        {
            var s = new AiSession(PadAiAction.Improve, new string('x', 8001), true);
            Assert.Equal(PadAiAction.Improve.TooLong(8001), s.Refusal);
            var v = s.View(Ok);
            Assert.False(v.CanRetry);
            Assert.Equal("", v.Result);
            Assert.Equal(s.Refusal, v.Status);
            Assert.False(v.CanCopy);
        }

        [Fact]
        public void SourceLine_wording()
        {
            Assert.Equal("Selection, 412 characters", new AiSession(PadAiAction.Summarize, new string('x', 412), true).View(Ok).SourceLine);
            Assert.Equal("Whole note, 3,120 characters", new AiSession(PadAiAction.Summarize, new string('x', 3120), false).View(Ok).SourceLine);
            Assert.Equal("Selection, 1 character", new AiSession(PadAiAction.Summarize, "x", true).View(Ok).SourceLine);
        }

        [Fact]
        public void A_credential_comes_back_as_a_pill()
        {
            string src = "pw {{secret:K7Q2M9XD}}";
            var s = new AiSession(PadAiAction.Improve, src, true);
            Assert.Contains("[[CREDENTIAL_1]]", s.UserMessage);
            Assert.DoesNotContain("{{secret:", s.UserMessage);
            s.Start(); s.Append("Password [[CREDENTIAL_"); s.Append("1]]\n"); s.Complete(false);
            Assert.Equal("Password {{secret:K7Q2M9XD}}", s.ResultForNote);
            var v = s.View(Ok);
            Assert.Equal("Password {{secret:K7Q2M9XD}}", v.Result);
            Assert.True(v.CanReplace);
        }

        [Fact]
        public void The_user_message_uses_the_mask_text()
        {
            var s = new AiSession(PadAiAction.Shorten, "a {{secret:K7Q2M9XD}}", true);
            Assert.Equal(PadAiPrompts.ForAction(PadAiAction.Shorten.Instruction, SecretMask.Of("a {{secret:K7Q2M9XD}}").Text), s.UserMessage);
        }

        [Fact]
        public void After_MarkApplied_replace_is_off_and_the_status_says_what_happened()
        {
            var s = Done(PadAiAction.Improve, "a", "b");
            s.MarkApplied("Replaced the selection");
            var v = s.View(Ok);
            Assert.False(v.CanReplace);
            Assert.Equal("Replaced the selection", v.Status);
        }

        [Fact]
        public void ClearApplied_offers_replace_again_and_drops_the_applied_status()
        {
            var s = Done(PadAiAction.Improve, "a", "b");
            s.MarkApplied("Replaced the selection");

            s.ClearApplied();   // the Replace was undone

            var v = s.View(Ok);
            Assert.True(v.CanReplace);
            Assert.Equal("", v.Status);

            s.ClearApplied();   // nothing applied: nothing to clear
            Assert.True(s.View(Ok).CanReplace);
        }

        [Fact]
        public void Start_twice_throws()
        {
            var s = new AiSession(PadAiAction.Improve, "a", true);
            s.Start();
            Assert.Throws<System.InvalidOperationException>(() => s.Start());
        }

        [Fact]
        public void Start_after_Complete_throws()
        {
            var s = Done(PadAiAction.Improve, "a", "b");
            var ex = Assert.Throws<System.InvalidOperationException>(() => s.Start());
            Assert.Equal("An AI session runs once", ex.Message);
        }

        [Fact]
        public void Start_on_a_refused_session_throws()
        {
            var s = new AiSession(PadAiAction.Improve, new string('x', 8001), true);
            Assert.Throws<System.InvalidOperationException>(() => s.Start());
        }

        [Fact]
        public void Start_while_awaiting_an_instruction_throws()
        {
            var s = new AiSession(PadAiAction.Ask, "a", true);
            Assert.Throws<System.InvalidOperationException>(() => s.Start());
        }

        [Fact]
        public void Append_after_Complete_is_ignored()
        {
            var s = Done(PadAiAction.Improve, "a", "b");
            s.Append(" more");
            Assert.Equal("b", s.View(Ok).Result);
        }

        [Fact]
        public void Fail_and_MarkCutShort_after_Complete_are_ignored()
        {
            var s = Done(PadAiAction.Improve, "a", "b");
            s.Fail("late");
            s.MarkCutShort();
            var v = s.View(Ok);
            Assert.Equal("", v.Status);
            Assert.True(v.CanReplace);
        }

        [Fact]
        public void A_literal_placeholder_in_the_text_and_a_real_credential_are_told_apart()
        {
            string src = "see [[CREDENTIAL_1]] and {{secret:K7Q2M9XD}}";
            var s = new AiSession(PadAiAction.Improve, src, true);
            Assert.Contains("[[CREDENTIAL_1]]", s.UserMessage);
            Assert.Contains("[[CREDENTIAL_X_1]]", s.UserMessage);
            s.Start(); s.Append("look [[CREDENTIAL_1]] and [[CREDENTIAL_X_1]]"); s.Complete(false);
            Assert.Equal("look [[CREDENTIAL_1]] and {{secret:K7Q2M9XD}}", s.ResultForNote);
            Assert.True(s.View(Ok).CanReplace);
        }

        [Fact]
        public void A_typed_instruction_is_trimmed_in_the_message()
        {
            var s = new AiSession(PadAiAction.Ask, "t", true, "   do it  ");
            Assert.Equal("do it", s.Instruction);
            Assert.StartsWith("Task: do it\n", s.UserMessage);
        }

        [Fact]
        public void While_running_nothing_is_offered_but_copy()
        {
            var s = new AiSession(PadAiAction.Improve, "a", true);
            s.Start(); s.Append("par");
            var v = s.View(Ok);
            Assert.True(v.Running);
            Assert.True(s.Running);
            Assert.False(s.Finished);
            Assert.False(v.CanReplace);
            Assert.False(v.CanInsert);
            Assert.False(v.CanRetry);
            Assert.True(v.CanCopy);
            Assert.Equal("", v.Status);
        }
    }
}
