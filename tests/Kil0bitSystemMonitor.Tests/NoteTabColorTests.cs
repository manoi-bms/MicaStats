using System;
using System.IO;
using System.Linq;
using Kil0bitSystemMonitor.Services.Pad;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public sealed class NoteTabColorTests
    {
        [Fact]
        public void Automatic_choices_begin_with_well_separated_colors()
        {
            int first = NoteTabColors.ChooseHue(Array.Empty<int>());
            int second = NoteTabColors.ChooseHue(new[] { first });
            int third = NoteTabColors.ChooseHue(new[] { first, second });
            int fourth = NoteTabColors.ChooseHue(new[] { first, second, third });

            Assert.Equal(new[] { 210, 30, 120, 300 }, new[] { first, second, third, fourth });
            Assert.Equal(NoteTabColors.Choices.Count, NoteTabColors.Choices.Select(choice => choice.Hue).Distinct().Count());
            Assert.All(NoteTabColors.Choices, choice => Assert.InRange(choice.Hue, 0, 359));
        }

        [Fact]
        public void Automatic_colors_remain_distinct_beyond_the_picker_palette()
        {
            int[] assigned = new int[24];
            for (int index = 0; index < assigned.Length; index++)
                assigned[index] = NoteTabColors.ChooseHue(assigned.Take(index));

            Assert.Equal(assigned.Length, assigned.Distinct().Count());
            Assert.DoesNotContain(assigned[NoteTabColors.Choices.Count], assigned.Take(NoteTabColors.Choices.Count));
        }

        [Fact]
        public void Stable_fallback_hue_depends_only_on_note_id()
        {
            int first = NoteTabColors.HueForId("0123456789abcdef0123456789abcdef");

            Assert.Equal(first, NoteTabColors.HueForId("0123456789abcdef0123456789abcdef"));
            Assert.NotEqual(first, NoteTabColors.HueForId("1123456789abcdef0123456789abcdef"));
            Assert.InRange(first, 0, 359);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Tab_tints_are_opaque_and_accents_and_titles_remain_readable(bool dark)
        {
            PadPalette palette = dark ? PadPalette.Dark : PadPalette.Light;
            for (int hue = 0; hue < 360; hue++)
            {
                PadColor inactive = NoteTabColors.Background(hue, palette, active: false, hover: false);
                PadColor hover = NoteTabColors.Background(hue, palette, active: false, hover: true);
                PadColor active = NoteTabColors.Background(hue, palette, active: true, hover: false);
                PadColor accent = NoteTabColors.Accent(hue, palette);

                Assert.All(new[] { inactive, hover, active }, background => Assert.Equal(255, background.A));
                Assert.All(new[] { inactive, hover, active, palette.Popup }, background =>
                    Assert.True(PadColor.Contrast(accent, background) >= 3.0));
                Assert.True(PadColor.Contrast(palette.TabTitle, inactive) >= 4.5);
                Assert.True(PadColor.Contrast(palette.TabTitle, hover) >= 4.5);
                Assert.True(PadColor.Contrast(palette.TabActiveTitle, active) >= 4.5);
            }
        }

        [Fact]
        public void New_notes_get_distinct_colors_and_create_current_text()
        {
            using var env = new PadTestEnv();

            OpenNote first = env.Workspace.NewNote();
            OpenNote second = env.Workspace.NewNote();
            OpenNote third = env.Workspace.NewNote();
            Assert.True(env.Workspace.SetTabColorHue(first, 345));
            env.Workspace.FlushPending();
            env.Flush();

            Assert.Equal(new[] { 345, 30, 120 }, new[] { first.TabColorHue, second.TabColorHue, third.TabColorHue });
            Assert.True(File.Exists(env.Store.CurrentPath(first.Id)));
            Assert.Equal(string.Empty, env.DiskText(first));
            Assert.Equal(345, env.Store.LoadMeta(first.Id)!.TabColorHue);
        }

        [Fact]
        public void Restore_assigns_and_persists_a_color_without_rewriting_text()
        {
            using var env = new PadTestEnv();
            NoteMeta legacy = NoteStore.NewMeta(env.Clock.UtcNow, 1, null);
            const string text = "legacy note";
            env.Store.SaveNote(legacy, text, env.Store.NextVersion());
            env.Store.SaveSession(new SessionState
            {
                OpenNoteIds = new() { legacy.Id },
                ActiveNoteId = legacy.Id,
            });
            byte[] before = File.ReadAllBytes(env.Store.CurrentPath(legacy.Id));

            PadWorkspace workspace = env.NewWorkspace();
            OpenNote restored = Assert.Single(workspace.Restore());
            Assert.Equal(210, restored.TabColorHue);
            Assert.True(workspace.HasPendingChanges(restored));
            env.Clock.Advance(1);
            workspace.Tick();
            env.Flush();

            Assert.Equal(before, File.ReadAllBytes(env.Store.CurrentPath(legacy.Id)));
            Assert.Equal(210, env.Store.LoadMeta(legacy.Id)!.TabColorHue);
        }

        [Fact]
        public void Restore_assigns_legacy_tabs_around_colors_already_saved_later_in_the_session()
        {
            using var env = new PadTestEnv();
            NoteMeta legacy = NoteStore.NewMeta(env.Clock.UtcNow, 1, null);
            NoteMeta saved = NoteStore.NewMeta(env.Clock.UtcNow.AddMinutes(1), 2, null);
            saved.TabColorHue = 210;
            env.Store.SaveNote(legacy, "legacy", env.Store.NextVersion());
            env.Store.SaveNote(saved, "saved", env.Store.NextVersion());
            env.Store.SaveSession(new SessionState
            {
                OpenNoteIds = new() { legacy.Id, saved.Id },
                ActiveNoteId = saved.Id,
            });

            PadWorkspace workspace = env.NewWorkspace();
            OpenNote[] restored = workspace.Restore().ToArray();

            Assert.Equal(30, restored.Single(note => note.Id == legacy.Id).TabColorHue);
            Assert.Equal(210, restored.Single(note => note.Id == saved.Id).TabColorHue);
        }

        [Fact]
        public void Changing_color_is_metadata_only_and_survives_restart()
        {
            using var env = new PadTestEnv();
            OpenNote note = env.Workspace.NewNote();
            PadTestEnv.Type(env.Workspace, note, "project plan");
            Assert.True(env.Workspace.FlushAll(TimeSpan.FromSeconds(5)));
            byte[] textBefore = File.ReadAllBytes(env.Store.CurrentPath(note.Id));
            DateTime lastEdit = note.LastEditUtc;
            bool snapshotDirty = note.ChangedSinceSnapshot;
            bool fileDirty = note.HasUnsavedEdits;
            int textNotifications = 0;
            env.Workspace.NoteTextChanged += _ => textNotifications++;

            Assert.True(env.Workspace.SetTabColorHue(note, 345));
            env.Clock.Advance(1);
            env.Workspace.Tick();
            env.Flush();

            Assert.Equal(textBefore, File.ReadAllBytes(env.Store.CurrentPath(note.Id)));
            Assert.Equal(lastEdit, note.LastEditUtc);
            Assert.Equal(snapshotDirty, note.ChangedSinceSnapshot);
            Assert.Equal(fileDirty, note.HasUnsavedEdits);
            Assert.Equal(0, textNotifications);
            PadWorkspace restarted = env.NewWorkspace();
            Assert.Equal(345, Assert.Single(restarted.Restore()).TabColorHue);
        }

        [Fact]
        public void Color_change_does_not_downgrade_a_pending_text_save()
        {
            using var env = new PadTestEnv();
            OpenNote note = env.Workspace.NewNote();
            Assert.True(env.Workspace.FlushAll(TimeSpan.FromSeconds(5)));

            PadTestEnv.Type(env.Workspace, note, "latest text");
            Assert.True(env.Workspace.SetTabColorHue(note, 175));
            env.Clock.Advance(1);
            env.Workspace.Tick();
            env.Flush();

            Assert.Equal("latest text", env.DiskText(note));
            NoteMeta saved = Assert.IsType<NoteMeta>(env.Store.LoadMeta(note.Id));
            Assert.Equal(175, saved.TabColorHue);
        }

        [Fact]
        public void Invalid_or_foreign_color_changes_are_ignored()
        {
            using var firstEnv = new PadTestEnv();
            using var secondEnv = new PadTestEnv();
            OpenNote note = firstEnv.Workspace.NewNote();
            OpenNote foreign = secondEnv.Workspace.NewNote();
            int original = note.TabColorHue;

            Assert.False(firstEnv.Workspace.SetTabColorHue(note, -1));
            Assert.False(firstEnv.Workspace.SetTabColorHue(note, 360));
            Assert.False(firstEnv.Workspace.SetTabColorHue(foreign, 45));
            Assert.False(firstEnv.Workspace.SetTabColorHue(null!, 45));
            Assert.Equal(original, note.TabColorHue);
            Assert.False(firstEnv.Workspace.HasPendingChanges(note));
        }

        [Fact]
        public void Color_survives_clone_rename_move_close_and_reopen()
        {
            using var env = new PadTestEnv();
            OpenNote first = env.Workspace.NewNote();
            OpenNote second = env.Workspace.NewNote();
            PadTestEnv.Type(env.Workspace, first, "content");
            env.Workspace.SetTabColorHue(first, 255);
            env.Workspace.Rename(first, "Reference");
            env.Workspace.MoveTab(first, 1);

            Assert.Equal(255, first.Meta.Clone().TabColorHue);
            Assert.Equal(255, first.TabColorHue);
            Assert.Equal(255, env.Workspace.Open[1].TabColorHue);

            Assert.True(env.Workspace.FlushAll(TimeSpan.FromSeconds(5)));
            env.Workspace.Close(first);
            OpenNote reopened = Assert.IsType<OpenNote>(env.Workspace.Reopen(first.Id));
            Assert.Equal(255, reopened.TabColorHue);
            Assert.Equal(second.Id, env.Workspace.Open[0].Id);
        }

        [Fact]
        public void File_note_color_never_changes_the_source_file()
        {
            using var env = new PadTestEnv();
            string source = env.FileOf("source.md");
            File.WriteAllText(source, "source text");
            OpenNote note = Assert.IsType<OpenNote>(env.Workspace.OpenFile(source).Note);
            env.Flush();
            byte[] before = File.ReadAllBytes(source);

            Assert.True(env.Workspace.SetTabColorHue(note, 45));
            env.Clock.Advance(1);
            env.Workspace.Tick();
            env.Flush();

            Assert.Equal(before, File.ReadAllBytes(source));
            Assert.False(note.HasUnsavedEdits);
            Assert.Equal(45, env.Store.LoadMeta(note.Id)!.TabColorHue);
        }
    }
}
