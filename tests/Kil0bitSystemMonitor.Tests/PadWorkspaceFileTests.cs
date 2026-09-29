using System;
using System.IO;
using System.Linq;
using System.Text;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    /// <summary>Real files: shadowed while edited, written only on Ctrl+S, and watched for outside changes.</summary>
    public class PadWorkspaceFileTests : IDisposable
    {
        private const string Sawasdee = "สวัสดี";
        private readonly PadTestEnv _env = new();

        private PadWorkspace Ws => _env.Workspace;

        public void Dispose() => _env.Dispose();

        private string WriteFile(string name, byte[] bytes)
        {
            string path = _env.FileOf(name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static string ChangeFile(string path, string text)
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
            // Move the write time on, so the change is visible whatever the timer resolution.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            return path;
        }

        [Fact]
        public void An_opened_file_is_untouched_until_ctrl_s()
        {
            byte[] original = Encoding.UTF8.GetBytes("a\r\nb");
            string path = WriteFile("notes.txt", original);

            var result = Ws.OpenFile(path);
            Assert.Equal(OpenFileStatus.Opened, result.Status);
            var note = result.Note!;
            Assert.Equal("a\r\nb", note.TextProvider());
            Assert.Equal("notes.txt", note.Title);

            PadTestEnv.Type(Ws, note, "a\r\nb\r\nc");
            _env.Clock.Advance(1);
            Ws.Tick();
            _env.Flush();

            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.True(note.HasUnsavedEdits);
            Assert.Equal("a\r\nb\r\nc", _env.DiskText(note));

            Assert.Equal(SaveToFileStatus.Saved, Ws.SaveToSource(note).Status);
            Assert.Equal(Encoding.UTF8.GetBytes("a\r\nb\r\nc"), File.ReadAllBytes(path));
            Assert.False(note.HasUnsavedEdits);
        }

        [Fact]
        public void Opening_a_file_that_is_already_open_switches_to_its_tab()
        {
            string path = WriteFile("a.txt", Encoding.UTF8.GetBytes("x"));
            var first = Ws.OpenFile(path).Note;
            Ws.NewNote();

            var again = Ws.OpenFile(path.ToUpperInvariant());

            Assert.Equal(OpenFileStatus.AlreadyOpen, again.Status);
            Assert.Same(first, again.Note);
            Assert.Same(first, Ws.Active);
        }

        [Fact]
        public void Reopening_a_closed_file_restores_its_unsaved_edits()
        {
            string path = WriteFile("draft.txt", Encoding.UTF8.GetBytes("v1"));
            var note = Ws.OpenFile(path).Note!;
            PadTestEnv.Type(Ws, note, "v1 plus edits");
            Ws.Close(note);

            var again = Ws.OpenFile(path);

            Assert.Equal(OpenFileStatus.Opened, again.Status);
            Assert.Equal(note.Id, again.Note!.Id);
            Assert.Equal("v1 plus edits", again.Note.TextProvider());
            Assert.True(again.Note.HasUnsavedEdits);
        }

        [Fact]
        public void Saving_keeps_the_original_ansi_encoding()
        {
            byte[] cp874 = { 0xCA, 0xC7, 0xD1, 0xCA, 0xB4, 0xD5 };
            string path = WriteFile("thai.txt", cp874);
            var note = Ws.OpenFile(path).Note!;
            Assert.Equal(PadEncoding.Ansi, note.Meta.Encoding);

            PadTestEnv.Type(Ws, note, Sawasdee + Sawasdee);

            Assert.Equal(SaveToFileStatus.Saved, Ws.SaveToSource(note).Status);
            Assert.Equal(cp874.Concat(cp874).ToArray(), File.ReadAllBytes(path));
        }

        [Fact]
        public void A_character_the_encoding_cannot_hold_is_reported_before_anything_is_written()
        {
            string path = WriteFile("latin.txt", Encoding.UTF8.GetBytes("plain"));
            var note = Ws.OpenFile(path).Note!;
            Ws.SetEncoding(note, PadEncoding.Ansi, 1252);
            PadTestEnv.Type(Ws, note, "plain " + Sawasdee);

            Assert.Equal(SaveToFileStatus.Lossy, Ws.SaveToSource(note).Status);
            Assert.Equal(Encoding.UTF8.GetBytes("plain"), File.ReadAllBytes(path));

            Ws.SetEncoding(note, PadEncoding.Utf8, 0);
            Assert.Equal(SaveToFileStatus.Saved, Ws.SaveToSource(note).Status);
            Assert.Equal(Encoding.UTF8.GetBytes("plain " + Sawasdee), File.ReadAllBytes(path));
        }

        [Fact]
        public void A_scratch_note_needs_save_as_and_then_becomes_file_backed()
        {
            var note = Ws.NewNote();
            PadTestEnv.Type(Ws, note, "Todo\nmilk");
            Assert.Equal(SaveToFileStatus.NeedsSaveAs, Ws.SaveToSource(note).Status);

            string path = _env.FileOf("todo.txt");
            Assert.Equal(SaveToFileStatus.Saved, Ws.SaveAs(note, path).Status);

            Assert.Equal(Encoding.UTF8.GetBytes("Todo\nmilk"), File.ReadAllBytes(path));
            Assert.True(note.Meta.IsFileBacked);
            Assert.Equal("todo.txt", note.Title);
            Assert.Equal(LineEnding.Lf, note.Meta.LineEnding);
        }

        [Fact]
        public void Save_as_onto_a_file_open_in_another_tab_is_refused()
        {
            string path = WriteFile("taken.txt", Encoding.UTF8.GetBytes("x"));
            Ws.OpenFile(path);
            var scratch = Ws.NewNote();
            PadTestEnv.Type(Ws, scratch, "other");

            var result = Ws.SaveAs(scratch, path);

            Assert.Equal(SaveToFileStatus.Failed, result.Status);
            Assert.False(scratch.Meta.IsFileBacked);
            Assert.Equal(Encoding.UTF8.GetBytes("x"), File.ReadAllBytes(path));
        }

        [Fact]
        public void A_change_on_disk_without_edits_reloads_silently()
        {
            string path = WriteFile("live.txt", Encoding.UTF8.GetBytes("old"));
            var note = Ws.OpenFile(path).Note!;
            ChangeFile(path, "new content");

            Assert.Equal(DiskChangeAction.ReloadSilently, Ws.CheckDisk(note));
            Assert.Equal("new content", Ws.ReloadFromDisk(note, out _, out bool lossy));
            Assert.False(lossy);
            Assert.False(note.HasUnsavedEdits);
            Assert.Equal(DiskChangeAction.None, Ws.CheckDisk(note));
        }

        [Fact]
        public void A_change_on_disk_with_edits_asks_and_reload_keeps_the_edits_in_history()
        {
            string path = WriteFile("both.txt", Encoding.UTF8.GetBytes("base"));
            var note = Ws.OpenFile(path).Note!;
            PadTestEnv.Type(Ws, note, "my edits");
            ChangeFile(path, "their edits");

            Assert.Equal(DiskChangeAction.AskReloadOrKeep, Ws.CheckDisk(note));
            Assert.Equal("their edits", Ws.ReloadFromDisk(note, out _, out bool lossy));
            Assert.False(lossy);
            _env.Flush();

            Assert.Contains(_env.Store.ListSnapshots(note.Id), s => _env.Store.ReadSnapshot(s) == "my edits");
        }

        [Fact]
        public void Keep_mine_silences_the_question_until_the_next_outside_change()
        {
            string path = WriteFile("mine.txt", Encoding.UTF8.GetBytes("base"));
            var note = Ws.OpenFile(path).Note!;
            PadTestEnv.Type(Ws, note, "my edits");
            ChangeFile(path, "their edits");

            Ws.KeepMine(note);
            Assert.Equal(DiskChangeAction.None, Ws.CheckDisk(note));

            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("a third, longer version"));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(2));
            Assert.Equal(DiskChangeAction.AskReloadOrKeep, Ws.CheckDisk(note));
        }

        [Fact]
        public void A_deleted_file_can_be_kept_as_a_note()
        {
            string path = WriteFile("gone.txt", Encoding.UTF8.GetBytes("text"));
            var note = Ws.OpenFile(path).Note!;
            File.Delete(path);

            Assert.Equal(DiskChangeAction.AskSaveAsOrKeepAsNote, Ws.CheckDisk(note));
            Ws.DetachFromFile(note);
            _env.Flush();

            Assert.False(note.Meta.IsFileBacked);
            Assert.Equal("gone.txt", note.Title);
            Assert.Equal("text", _env.DiskText(note));
        }

        [Fact]
        public void Restoring_a_clean_file_note_reads_the_file_fresh()
        {
            string path = WriteFile("fresh.txt", Encoding.UTF8.GetBytes("before"));
            Ws.OpenFile(path);
            Assert.True(Ws.FlushAll(TimeSpan.FromSeconds(5)));
            ChangeFile(path, "after restart");

            var restored = _env.NewWorkspace();
            var note = Assert.Single(restored.Restore());

            Assert.Equal("after restart", note.TextProvider());
            Assert.Equal(DiskChangeAction.None, restored.CheckDisk(note));
        }

        [Fact]
        public void Converting_line_endings_takes_a_snapshot_first()
        {
            string path = WriteFile("unix.txt", Encoding.UTF8.GetBytes("a\nb\n"));
            var note = Ws.OpenFile(path).Note!;
            Assert.Equal(LineEnding.Lf, note.Meta.LineEnding);

            string converted = Ws.ConvertLineEndings(note, LineEnding.CrLf);
            _env.Flush();

            Assert.Equal("a\r\nb\r\n", converted);
            Assert.Equal(LineEnding.CrLf, note.Meta.LineEnding);
            Assert.Single(_env.Store.ListSnapshots(note.Id));
        }

        [Fact]
        public void A_file_with_undecodable_bytes_opens_and_is_reported_lossy()
        {
            string path = WriteFile("broken.txt", new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', 0xFF, (byte)'b' });

            var result = Ws.OpenFile(path);

            Assert.Equal(OpenFileStatus.Opened, result.Status);
            Assert.True(result.Lossy);
            Assert.Equal("a\uFFFDb", result.Note!.TextProvider());
            Assert.False(Ws.OpenFile(WriteFile("fine.txt", Encoding.UTF8.GetBytes("ok"))).Lossy);
        }

        [Fact]
        public void Reopening_a_closed_file_that_became_binary_is_refused()
        {
            string path = WriteFile("was-text.txt", Encoding.UTF8.GetBytes("text"));
            var note = Ws.OpenFile(path).Note!;
            Ws.Close(note);
            WriteFile("was-text.txt", new byte[] { 0x4D, 0x5A, 0x00, 0x01 });

            var again = Ws.OpenFile(path);

            Assert.Equal(OpenFileStatus.Binary, again.Status);
            Assert.Null(again.Note);
        }

        [Fact]
        public void A_failed_save_as_leaves_the_note_exactly_as_it_was()
        {
            var note = Ws.NewNote();
            PadTestEnv.Type(Ws, note, "keep me");
            string missingFolder = _env.FileOf(System.IO.Path.Combine("no-such-folder", "x.txt"));

            var result = Ws.SaveAs(note, missingFolder);
            _env.Flush();

            Assert.Equal(SaveToFileStatus.Failed, result.Status);
            Assert.False(note.Meta.IsFileBacked);
            Assert.Null(_env.Store.LoadMeta(note.Id)!.SourcePath);
            Assert.Equal("keep me", _env.DiskText(note));
        }

        [Fact]
        public void Ctrl_s_after_an_outside_change_is_refused_and_the_file_keeps_the_outside_text()
        {
            string path = WriteFile("shared.txt", Encoding.UTF8.GetBytes("base"));
            var note = Ws.OpenFile(path).Note!;
            PadTestEnv.Type(Ws, note, "mine");
            ChangeFile(path, "theirs");

            var result = Ws.SaveToSource(note);
            _env.Flush();

            Assert.Equal(SaveToFileStatus.ChangedOnDisk, result.Status);
            Assert.Equal(Encoding.UTF8.GetBytes("theirs"), File.ReadAllBytes(path));
            Assert.True(note.HasUnsavedEdits);
            Assert.Empty(_env.Store.ListSnapshots(note.Id));
        }

        [Fact]
        public void A_clean_note_is_not_saved_over_an_outside_change_either()
        {
            string path = WriteFile("log.txt", Encoding.UTF8.GetBytes("line 1"));
            var note = Ws.OpenFile(path).Note!;
            ChangeFile(path, "line 1\r\nline 2");

            Assert.Equal(SaveToFileStatus.ChangedOnDisk, Ws.SaveToSource(note).Status);
            Assert.Equal(Encoding.UTF8.GetBytes("line 1\r\nline 2"), File.ReadAllBytes(path));
        }

        [Fact]
        public void Overwrite_writes_the_note_and_keeps_the_outside_version_in_history()
        {
            string path = WriteFile("shared.txt", Encoding.UTF8.GetBytes("base"));
            var note = Ws.OpenFile(path).Note!;
            PadTestEnv.Type(Ws, note, "mine");
            ChangeFile(path, "theirs");

            var result = Ws.SaveToSource(note, overwriteExternalChanges: true);
            _env.Flush();

            Assert.Equal(SaveToFileStatus.Saved, result.Status);
            Assert.Equal(Encoding.UTF8.GetBytes("mine"), File.ReadAllBytes(path));
            Assert.False(note.HasUnsavedEdits);
            var versions = _env.Store.ListSnapshots(note.Id);
            Assert.Equal(new[] { "mine", "theirs" }, versions.Select(v => _env.Store.ReadSnapshot(v)));
            Assert.Equal(SaveToFileStatus.Saved, Ws.SaveToSource(note).Status);   // the file is ours again
        }

        [Fact]
        public void Save_as_onto_an_existing_file_is_not_asked_about_outside_changes()
        {
            string path = WriteFile("original.txt", Encoding.UTF8.GetBytes("base"));
            var note = Ws.OpenFile(path).Note!;
            PadTestEnv.Type(Ws, note, "mine");
            ChangeFile(path, "theirs");                                   // Ctrl+S would now ask
            string other = WriteFile("other.txt", Encoding.UTF8.GetBytes("something else"));

            Assert.Equal(SaveToFileStatus.Saved, Ws.SaveAs(note, other).Status);
            Assert.Equal(Encoding.UTF8.GetBytes("mine"), File.ReadAllBytes(other));
            Assert.Equal(Encoding.UTF8.GetBytes("theirs"), File.ReadAllBytes(path));

            string fresh = _env.FileOf("fresh-copy.txt");
            PadTestEnv.Type(Ws, note, "mine, again");
            Assert.Equal(SaveToFileStatus.Saved, Ws.SaveAs(note, fresh).Status);
            Assert.Equal(Encoding.UTF8.GetBytes("mine, again"), File.ReadAllBytes(fresh));
        }

        [Fact]
        public void A_code_page_this_pc_does_not_have_fails_the_save_without_throwing()
        {
            string path = WriteFile("odd.txt", Encoding.UTF8.GetBytes("plain"));
            var note = Ws.OpenFile(path).Note!;
            Ws.SetEncoding(note, PadEncoding.Ansi, 99999);

            var result = Ws.SaveToSource(note);

            Assert.Equal(SaveToFileStatus.Failed, result.Status);
            Assert.Contains("99999", result.Error);
            Assert.Equal(Encoding.UTF8.GetBytes("plain"), File.ReadAllBytes(path));
        }

        [Theory]
        [InlineData("missing.txt", OpenFileStatus.NotFound)]
        [InlineData("binary.bin", OpenFileStatus.Binary)]
        public void Missing_and_binary_files_are_refused(string name, OpenFileStatus expected)
        {
            if (name == "binary.bin") WriteFile(name, new byte[] { 0x4D, 0x5A, 0x00, 0x01 });

            var result = Ws.OpenFile(_env.FileOf(name));

            Assert.Equal(expected, result.Status);
            Assert.Null(result.Note);
            Assert.Empty(Ws.Open);
        }
    }
}
