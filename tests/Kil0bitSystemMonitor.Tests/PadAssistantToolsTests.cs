using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Localization;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Ai;
using Microsoft.Extensions.AI;
using Xunit;

using CheckBox = System.Windows.Controls.CheckBox;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Kil0bitSystemMonitor.Tests;

[Collection("UiTextGlobal")]
public sealed class PadAssistantToolsTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 8, 9, 0, 0, TimeSpan.FromHours(7));
    private static readonly DateTimeOffset Finished = Started.AddHours(2);
    private static readonly TimeSpan BodyLimit = TimeSpan.FromSeconds(60);

    private sealed class Harness
    {
        public Harness(PadTestEnv env)
        {
            Env = env;
            Usage = new UsageMeter(env.FileOf("assistant-tool-usage.json"), () => Started.LocalDateTime);
            Window = new MicaPadWindow(env.Workspace, new AppConfig { PadAiEnabled = true, PadMarkdown = true });
            Window.AiEnabled = () => true;
            Window.AiRunnerFactory = () => new PadAiRunner(
                () => new AiClientResult(Model, null, false), Usage, () => 100);
            Window.AiLog = _ => { };
            Window.Now = () => Now;
        }

        public PadTestEnv Env { get; }
        public MicaPadWindow Window { get; }
        public ScriptedChatClient Model { get; } = new();
        public UsageMeter Usage { get; }
        public DateTimeOffset Now { get; set; } = Started;
        public OpenNote Note => Env.Workspace.Active!;

        public void Write(string source)
        {
            Window.Editor.Document.Text = source;
            Window.Editor.Select(0, 0);
            PadLanguageWindowTests.Pump();
        }

        public Task Ask(string instruction = "Help with these items") =>
            Window.RunAiAsync(PadAiAction.Ask, instruction);
    }

    private static Task OnUi(Func<Harness, Task> test) => UiThread.RunAsync(async () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var env = new PadTestEnv(post: action => dispatcher.BeginInvoke(action));
        var h = new Harness(env);
        try
        {
            h.Window.LoadSession();
            await test(h);
        }
        finally
        {
            h.Window.CloseForExit();
        }
    }, BodyLimit);

    private static TaskDateRecord OnlyTask(Harness h) => Assert.Single(h.Note.Meta.TaskDates);

    private static string TaskId(Harness h) => OnlyTask(h).Id;

    private static IReadOnlyList<string> ProposalIds(Harness h) =>
        Assert.IsType<PadAiToolSet>(h.Window.AiToolsNow).Proposals.Select(proposal => proposal.Id).ToArray();

    private static void CompleteTaskCall(Harness h, bool completed)
    {
        h.Model.Call("list_tasks")
            .Call("propose_task_state", new Dictionary<string, object?>
            {
                ["task_id"] = TaskId(h),
                ["completed"] = completed,
            })
            .Reply(completed ? "I prepared the completion for review." : "I prepared reopening for review.");
    }

    [Fact]
    public Task Completing_a_reviewed_task_changes_nothing_before_approval_then_records_its_finish_separately() => OnUi(async h =>
    {
        h.Write("- [ ] Send report");
        TaskDateRecord before = OnlyTask(h);
        CompleteTaskCall(h, completed: true);

        await h.Ask("Mark the report task complete");

        Assert.Equal("- [ ] Send report", h.Window.Editor.Text);
        Assert.Null(OnlyTask(h).Finished);
        Assert.Equal(Visibility.Visible, h.Window.AiPanel.ToolReviewPanel.Visibility);
        Assert.True(h.Window.AiPanel.ToolApplyButton.IsEnabled);

        h.Now = Finished;
        h.Window.ApplyAiToolChanges(ProposalIds(h));

        Assert.Equal("- [x] Send report", h.Window.Editor.Text);
        TaskDateRecord after = OnlyTask(h);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Created, after.Created);
        Assert.Equal(Finished, after.Finished);
        Assert.DoesNotContain("created:", h.Window.Editor.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("finished:", h.Window.Editor.Text, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task Reopening_a_reviewed_task_keeps_its_created_date_and_clears_its_finished_date() => OnUi(async h =>
    {
        h.Write("- [x] Send report");
        TaskDateRecord before = OnlyTask(h);
        Assert.NotNull(before.Finished);
        CompleteTaskCall(h, completed: false);

        await h.Ask("Reopen the report task");
        h.Now = Finished;
        h.Window.ApplyAiToolChanges(ProposalIds(h));

        Assert.Equal("- [ ] Send report", h.Window.Editor.Text);
        TaskDateRecord after = OnlyTask(h);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Created, after.Created);
        Assert.Null(after.Finished);
    });

    [Fact]
    public Task Editing_any_part_of_the_note_after_review_rejects_the_whole_proposal_batch() => OnUi(async h =>
    {
        h.Write("Heading\n- [ ] Send report");
        CompleteTaskCall(h, completed: true);
        await h.Ask("Complete the report task");
        IReadOnlyList<string> proposals = ProposalIds(h);

        h.Window.Editor.Document.Insert(0, "Changed heading\n");
        string changed = h.Window.Editor.Text;
        PadLanguageWindowTests.Pump();

        h.Window.ApplyAiToolChanges(proposals);

        Assert.Equal(changed, h.Window.Editor.Text);
        Assert.Contains("- [ ] Send report", h.Window.Editor.Text, StringComparison.Ordinal);
        Assert.Null(OnlyTask(h).Finished);
        Assert.False(h.Window.AiPanel.ToolApplyButton.IsEnabled);
    });

    [Fact]
    public Task Switching_notes_after_review_disables_apply_and_changes_neither_note() => OnUi(async h =>
    {
        h.Write("- [ ] Send report");
        OpenNote original = h.Note;
        CompleteTaskCall(h, completed: true);
        await h.Ask("Complete the report task");
        IReadOnlyList<string> proposals = ProposalIds(h);

        h.Window.NewTab();
        OpenNote other = h.Note;
        h.Window.Editor.Document.Text = "Other note";
        PadLanguageWindowTests.Pump();

        Assert.Same(other, h.Env.Workspace.ActiveIn(h.Window.WindowId));
        Assert.Equal("Other note", h.Window.Editor.Text);
        Assert.False(h.Window.AiPanel.ToolApplyButton.IsEnabled);
        h.Window.ApplyAiToolChanges(proposals);

        Assert.Equal("- [ ] Send report", original.TextProvider());
        Assert.Equal("Other note", other.TextProvider());
        Assert.Null(Assert.Single(original.Meta.TaskDates).Finished);
    });

    [Fact]
    public Task Opening_a_read_only_preview_after_review_disables_apply_and_rejects_the_change() => OnUi(async h =>
    {
        h.Write("- [ ] Send report");
        CompleteTaskCall(h, completed: true);
        await h.Ask("Complete the report task");
        IReadOnlyList<string> proposals = ProposalIds(h);

        h.Window.PreviewPanel.Visibility = Visibility.Visible;
        PadLanguageWindowTests.Pump();

        Assert.False(h.Window.AiPanel.ToolApplyButton.IsEnabled);
        h.Window.ApplyAiToolChanges(proposals);
        Assert.Equal("- [ ] Send report", h.Window.Editor.Text);
        Assert.Null(OnlyTask(h).Finished);
    });

    [Fact]
    public Task A_document_failure_before_a_task_edit_preserves_the_source_dates_and_review() => OnUi(async h =>
    {
        h.Write("- [ ] Send report");
        TaskDateRecord before = OnlyTask(h);
        CompleteTaskCall(h, completed: true);
        await h.Ask("Complete the report task");
        TextDocument document = h.Window.Editor.Document;
        bool fail = true;
        EventHandler<DocumentChangeEventArgs> throwOnce = (_, _) =>
        {
            if (fail)
            {
                fail = false;
                throw new InvalidOperationException("Injected before-edit failure");
            }
        };
        document.Changing += throwOnce;

        try
        {
            h.Window.ApplyAiToolChanges(ProposalIds(h));
        }
        finally
        {
            document.Changing -= throwOnce;
        }

        Assert.Equal("- [ ] Send report", document.Text);
        Assert.Equal(before, OnlyTask(h));
        Assert.True(h.Window.AiPanel.ToolApplyButton.IsEnabled);
    });

    [Fact]
    public Task A_document_failure_after_a_task_edit_rolls_back_the_source_dates_and_review() => OnUi(async h =>
    {
        h.Write("- [ ] Send report");
        TaskDateRecord before = OnlyTask(h);
        CompleteTaskCall(h, completed: true);
        await h.Ask("Complete the report task");
        TextDocument document = h.Window.Editor.Document;
        bool fail = true;
        EventHandler<DocumentChangeEventArgs> throwOnce = (_, _) =>
        {
            if (fail)
            {
                fail = false;
                throw new InvalidOperationException("Injected after-edit failure");
            }
        };
        document.Changed += throwOnce;

        try
        {
            h.Window.ApplyAiToolChanges(ProposalIds(h));
        }
        finally
        {
            document.Changed -= throwOnce;
        }

        Assert.Equal("- [ ] Send report", document.Text);
        Assert.Equal(before, OnlyTask(h));
        Assert.True(h.Window.AiPanel.ToolApplyButton.IsEnabled);
    });

    [Fact]
    public Task An_update_finished_failure_restores_task_dates_and_keeps_the_review_retryable() => OnUi(async h =>
    {
        h.Write("- [ ] Send report");
        TaskDateRecord before = OnlyTask(h);
        CompleteTaskCall(h, completed: true);
        await h.Ask("Complete the report task");
        IReadOnlyList<string> proposals = ProposalIds(h);
        TextDocument document = h.Window.Editor.Document;
        bool fail = true;
        EventHandler throwOnce = (_, _) =>
        {
            if (fail)
            {
                fail = false;
                throw new InvalidOperationException("Injected update-finished failure");
            }
        };
        document.UpdateFinished += throwOnce;

        try
        {
            h.Window.ApplyAiToolChanges(proposals);
        }
        finally
        {
            document.UpdateFinished -= throwOnce;
        }

        Assert.Equal("- [ ] Send report", document.Text);
        Assert.Equal(before, OnlyTask(h));
        Assert.True(h.Window.AiPanel.ToolApplyButton.IsEnabled);

        h.Now = Finished;
        h.Window.ApplyAiToolChanges(proposals);
        Assert.Equal("- [x] Send report", document.Text);
        Assert.Equal(Finished, OnlyTask(h).Finished);
    });

    [Fact]
    public Task Cancelling_a_finished_review_rejects_its_proposals() => OnUi(async h =>
    {
        h.Write("- [ ] Send report");
        CompleteTaskCall(h, completed: true);
        await h.Ask("Complete the report task");
        IReadOnlyList<string> proposals = ProposalIds(h);

        h.Window.AiPanel.StopButton.RaiseEvent(
            new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        h.Window.ApplyAiToolChanges(proposals);

        Assert.Equal("- [ ] Send report", h.Window.Editor.Text);
        Assert.Null(OnlyTask(h).Finished);
        Assert.False(h.Window.AiPanel.ToolApplyButton.IsEnabled);
    });

    [Fact]
    public Task Unchecking_every_review_item_disables_apply_and_applies_nothing() => OnUi(async h =>
    {
        h.Write("- [ ] Send report");
        CompleteTaskCall(h, completed: true);
        await h.Ask("Complete the report task");
        h.Window.AiPanel.Measure(new Size(420, 260));
        h.Window.AiPanel.Arrange(new Rect(0, 0, 420, 260));
        h.Window.AiPanel.UpdateLayout();
        CheckBox check = Assert.Single(Descendants<CheckBox>(h.Window.AiPanel.ToolReviewList));

        check.IsChecked = false;
        PadLanguageWindowTests.Pump();
        Assert.False(h.Window.AiPanel.ToolApplyButton.IsEnabled);
        h.Window.AiPanel.ToolApplyButton.RaiseEvent(
            new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        Assert.Equal("- [ ] Send report", h.Window.Editor.Text);
        Assert.Null(OnlyTask(h).Finished);
    });

    [Fact]
    public Task Credential_candidates_and_matching_task_text_never_enter_provider_messages_or_tool_payloads() => OnUi(async h =>
    {
        const string secret = "violet-gorilla-8472";
        h.Write("password: " + secret + "\n- [ ] Rotate " + secret);
        h.Model.Call("scan_credentials")
            .Call("list_tasks")
            .Call("propose_credential_storage", new Dictionary<string, object?>
            {
                ["candidate_ids"] = new[] { "credential-1" },
            })
            .Reply("I found one candidate for your review.");

        await h.Ask("Store every labelled password safely");

        Assert.Equal("password: " + secret + "\n- [ ] Rotate " + secret, h.Window.Editor.Text);
        Assert.False(h.Env.Vault.Exists);
        Assert.NotEmpty(h.Model.Requests);
        foreach (ScriptedChatClient.Request request in h.Model.Requests)
        {
            foreach (string outbound in OutboundText(request))
                Assert.DoesNotContain(secret, outbound, StringComparison.Ordinal);
        }
    });

    [Fact]
    public Task An_existing_vault_stores_the_approved_batch_then_masks_every_copy_and_clears_undo() => OnUi(async h =>
    {
        const string secret = "violet-gorilla-8472";
        h.Env.Vault.Load();
        h.Env.Vault.Create("246810");
        h.Write("password: " + secret + "\nbackup password: " + secret);
        h.Env.Workspace.SnapshotNow(h.Note, SnapshotReason.Pause);
        h.Model.Call("scan_credentials")
            .Call("propose_credential_storage", new Dictionary<string, object?>
            {
                ["candidate_ids"] = new[] { "credential-1" },
            })
            .Reply("Ready for review.");

        await h.Ask("Store the password");
        h.Window.ApplyAiToolChanges(ProposalIds(h));

        CredentialInfo credential = Assert.Single(h.Env.Vault.Credentials);
        string reference = SecretTokens.Format(credential.Id);
        Assert.Equal("password: " + reference + "\nbackup password: " + reference, h.Window.Editor.Text);
        Assert.False(h.Window.Editor.CanUndo);
        Assert.True(h.Env.Workspace.FlushAll(TimeSpan.FromSeconds(5)));
        h.Env.Flush();
        Assert.Equal(0, h.Window.RetryPendingScrubs());
        Assert.Equal(0, h.Window.PendingScrubCount);
        Assert.True(File.ReadAllBytes(h.Env.Vault.FilePath).AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) < 0);
        Assert.All(h.Env.Store.ListSnapshots(h.Note.Id), snapshot =>
            Assert.DoesNotContain(secret, h.Env.Store.ReadSnapshot(snapshot) ?? "", StringComparison.Ordinal));
    });

    [Fact]
    public Task A_mixed_task_and_credential_batch_preserves_both_task_identities_and_updates_only_the_selected_finish() => OnUi(async h =>
    {
        const string secret = "violet-gorilla-8472";
        h.Env.Vault.Load();
        h.Env.Vault.Create("246810");
        h.Write("- [ ] Send report\n- [ ] Rotate password: " + secret);
        TaskDateRecord[] before = h.Note.Meta.TaskDates.ToArray();
        Assert.Equal(2, before.Length);
        h.Model.Call("list_tasks")
            .Call("scan_credentials")
            .Call("propose_task_state", new Dictionary<string, object?>
            {
                ["task_id"] = before[0].Id,
                ["completed"] = true,
            })
            .Call("propose_credential_storage", new Dictionary<string, object?>
            {
                ["candidate_ids"] = new[] { "credential-1" },
            })
            .Reply("Ready for review.");

        await h.Ask("Complete the report and protect the password");
        h.Now = Finished;
        h.Window.ApplyAiToolChanges(ProposalIds(h));

        CredentialInfo credential = Assert.Single(h.Env.Vault.Credentials);
        Assert.Equal("- [x] Send report\n- [ ] Rotate password: " + SecretTokens.Format(credential.Id),
            h.Window.Editor.Text);
        TaskDateRecord[] after = h.Note.Meta.TaskDates.ToArray();
        TaskDateRecord report = Assert.Single(after, task => task.Id == before[0].Id);
        TaskDateRecord rotate = Assert.Single(after, task => task.Id == before[1].Id);
        Assert.Equal(before[0].Created, report.Created);
        Assert.Equal(Finished, report.Finished);
        Assert.Equal(before[1].Created, rotate.Created);
        Assert.Null(rotate.Finished);
    });

    [Fact]
    public Task A_throwing_vault_change_listener_does_not_interrupt_or_duplicate_an_approved_credential_batch() => OnUi(async h =>
    {
        const string secret = "violet-gorilla-8472";
        h.Env.Vault.Load();
        h.Env.Vault.Create("246810");
        h.Env.Vault.Changed += (_, _) => throw new InvalidOperationException("Injected permanent vault listener failure");
        h.Write("password: " + secret + "\nbackup password: " + secret);
        h.Env.Workspace.SnapshotNow(h.Note, SnapshotReason.Pause);
        h.Model.Call("scan_credentials")
            .Call("propose_credential_storage", new Dictionary<string, object?>
            {
                ["candidate_ids"] = new[] { "credential-1" },
            })
            .Reply("Ready for review.");

        await h.Ask("Store the password");
        IReadOnlyList<string> proposals = ProposalIds(h);
        h.Window.ApplyAiToolChanges(proposals);

        CredentialInfo stored = Assert.Single(h.Env.Vault.Credentials);
        string reference = SecretTokens.Format(stored.Id);
        Assert.Equal("password: " + reference + "\nbackup password: " + reference, h.Window.Editor.Text);
        Assert.False(h.Window.Editor.CanUndo);
        Assert.Equal(Visibility.Collapsed, h.Window.AiPanel.Visibility);
        Assert.Null(h.Window.AiToolsNow);

        h.Window.ApplyAiToolChanges(proposals);
        Assert.Equal(stored, Assert.Single(h.Env.Vault.Credentials));
        Assert.Equal("password: " + reference + "\nbackup password: " + reference, h.Window.Editor.Text);

        Assert.True(h.Env.Workspace.FlushAll(TimeSpan.FromSeconds(5)));
        h.Env.Flush();
        Assert.Equal(0, h.Window.RetryPendingScrubs());
        Assert.Equal(0, h.Window.PendingScrubCount);
        Assert.All(h.Env.Store.ListSnapshots(h.Note.Id), snapshot =>
            Assert.DoesNotContain(secret, h.Env.Store.ReadSnapshot(snapshot) ?? "", StringComparison.Ordinal));
    });

    [Fact]
    public Task The_first_credential_batch_creates_nothing_until_approval_then_uses_the_existing_PIN_card() => OnUi(async h =>
    {
        const string secret = "violet-gorilla-8472";
        h.Write("password: " + secret);
        h.Model.Call("scan_credentials")
            .Call("propose_credential_storage", new Dictionary<string, object?>
            {
                ["candidate_ids"] = new[] { "credential-1" },
            })
            .Reply("Ready for review.");

        await h.Ask("Store the password");

        Assert.False(h.Env.Vault.Exists);
        Assert.Equal("password: " + secret, h.Window.Editor.Text);
        Assert.False(h.Window.VaultCard.IsOpen);

        h.Window.ApplyAiToolChanges(ProposalIds(h));

        Assert.False(h.Env.Vault.Exists);
        Assert.Equal("password: " + secret, h.Window.Editor.Text);
        Assert.True(h.Window.VaultCard.IsOpen);
        Assert.Equal("CreatePin", h.Window.VaultCard.Mode);

        h.Window.VaultCard.PinBox.Password = "246810";
        h.Window.VaultCard.PinBox2.Password = "246810";
        h.Window.VaultCard.PrimaryButton.RaiseEvent(
            new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        Assert.True(h.Env.Vault.Exists);
        CredentialInfo stored = Assert.Single(h.Env.Vault.Credentials);
        Assert.Equal("password: " + SecretTokens.Format(stored.Id), h.Window.Editor.Text);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task A_document_failure_before_or_after_a_credential_edit_rolls_back_the_vault_and_allows_one_clean_retry(bool afterEdit) => OnUi(async h =>
    {
        const string secret = "violet-gorilla-8472";
        h.Env.Vault.Load();
        h.Env.Vault.Create("246810");
        string existingId = h.Env.Vault.Add("existing-secret-value", "Existing", null);
        CredentialInfo[] credentialsBefore = h.Env.Vault.Credentials.ToArray();
        string source = "password: " + secret;
        h.Write(source);
        h.Model.Call("scan_credentials")
            .Call("propose_credential_storage", new Dictionary<string, object?>
            {
                ["candidate_ids"] = new[] { "credential-1" },
            })
            .Reply("Ready for review.");
        await h.Ask("Store the password");
        IReadOnlyList<string> proposals = ProposalIds(h);
        TextDocument document = h.Window.Editor.Document;
        bool fail = true;
        EventHandler<DocumentChangeEventArgs> throwOnce = (_, _) =>
        {
            if (fail)
            {
                fail = false;
                throw new InvalidOperationException(afterEdit
                    ? "Injected credential after-edit failure"
                    : "Injected credential before-edit failure");
            }
        };
        if (afterEdit) document.Changed += throwOnce;
        else document.Changing += throwOnce;

        try
        {
            h.Window.ApplyAiToolChanges(proposals);
        }
        finally
        {
            if (afterEdit) document.Changed -= throwOnce;
            else document.Changing -= throwOnce;
        }

        Assert.Equal(source, document.Text);
        Assert.Equal<CredentialInfo>(credentialsBefore, h.Env.Vault.Credentials);
        using (var saved = new CredentialVault(
            h.Env.Vault.FilePath, () => Started.UtcDateTime, h.Env.Vault.Rounds, _ => { }))
        {
            Assert.Equal(VaultLoadStatus.Ready, saved.Load());
            Assert.Equal<CredentialInfo>(credentialsBefore, saved.Credentials);
            Assert.Equal(UnlockOutcome.Unlocked, saved.Unlock("246810").Outcome);
            Assert.Equal("existing-secret-value", saved.Reveal(existingId));
        }
        Assert.True(h.Window.AiPanel.ToolApplyButton.IsEnabled);

        h.Window.ApplyAiToolChanges(proposals);

        CredentialInfo stored = Assert.Single(h.Env.Vault.Credentials, item => item.Id != existingId);
        Assert.Equal("password: " + SecretTokens.Format(stored.Id), document.Text);
        Assert.Equal(credentialsBefore.Length + 1, h.Env.Vault.Credentials.Count);
        Assert.False(h.Window.Editor.CanUndo);
        Assert.True(File.ReadAllBytes(h.Env.Vault.FilePath).AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) < 0);
    });

    [Fact]
    public Task An_unknown_candidate_in_the_exact_basic_auth_review_request_is_rejected_before_vault_save() => OnUi(async h =>
    {
        const string source = "password=https://user:secret@example.com/";
        h.Env.Vault.Load();
        h.Env.Vault.Create("246810");
        byte[] vaultBefore = File.ReadAllBytes(h.Env.Vault.FilePath);
        h.Write(source);
        h.Model.Call("scan_credentials")
            .Call("propose_credential_storage", new Dictionary<string, object?>
            {
                ["candidate_ids"] = new[] { "credential-1", "credential-2" },
            })
            .Reply("Ready for review.");

        await h.Ask("Store every password candidate");

        Assert.Empty(Assert.IsType<PadAiToolSet>(h.Window.AiToolsNow).Proposals);
        Assert.False(h.Window.AiPanel.ToolApplyButton.IsEnabled);
        Assert.Equal(source, h.Window.Editor.Text);
        Assert.Empty(h.Env.Vault.Credentials);
        Assert.Equal(vaultBefore, File.ReadAllBytes(h.Env.Vault.FilePath));
    });

    [Fact]
    public Task Overlapping_assignment_and_basic_auth_candidates_are_rejected_before_the_vault_is_saved() => OnUi(async h =>
    {
        const string source = "password=https://user:secret-8472@example.com/";
        h.Env.Vault.Load();
        h.Env.Vault.Create("246810");
        byte[] vaultBefore = File.ReadAllBytes(h.Env.Vault.FilePath);
        h.Write(source);
        h.Model.Call("scan_credentials")
            .Call("propose_credential_storage", new Dictionary<string, object?>
            {
                ["candidate_ids"] = new[] { "credential-1", "credential-2" },
            })
            .Reply("Ready for review.");

        await h.Ask("Store every password candidate");
        IReadOnlyList<string> proposals = ProposalIds(h);
        Assert.Equal(2, proposals.Count);
        h.Window.ApplyAiToolChanges(proposals);

        Assert.Equal(source, h.Window.Editor.Text);
        Assert.Empty(h.Env.Vault.Credentials);
        Assert.Equal(vaultBefore, File.ReadAllBytes(h.Env.Vault.FilePath));
        Assert.True(h.Window.AiPanel.ToolApplyButton.IsEnabled);
    });

    [Fact]
    public Task A_file_backed_note_keeps_its_plaintext_on_disk_until_the_user_saves() => OnUi(async h =>
    {
        const string secret = "violet-gorilla-8472";
        h.Env.Vault.Load();
        h.Env.Vault.Create("246810");
        string path = h.Env.FileOf("account.md");
        File.WriteAllText(path, "password: " + secret);
        h.Window.OpenPath(path);
        h.Model.Call("scan_credentials")
            .Call("propose_credential_storage", new Dictionary<string, object?>
            {
                ["candidate_ids"] = new[] { "credential-1" },
            })
            .Reply("Ready for review.");

        await h.Ask("Store the password");
        h.Window.ApplyAiToolChanges(ProposalIds(h));

        CredentialInfo stored = Assert.Single(h.Env.Vault.Credentials);
        Assert.Equal("password: " + SecretTokens.Format(stored.Id), h.Window.Editor.Text);
        Assert.Equal("password: " + secret, File.ReadAllText(path));
        Assert.True(h.Note.HasUnsavedEdits);
        Assert.Contains("save the file", h.Window.StatusMessage.Text, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task A_failed_vault_save_leaves_the_note_task_dates_and_credentials_unchanged() => OnUi(async h =>
    {
        const string secret = "violet-gorilla-8472";
        h.Env.Vault.Load();
        h.Env.Vault.Create("246810");
        h.Write("password: " + secret + "\n- [ ] Send report");
        TaskDateRecord before = OnlyTask(h);
        h.Model.Call("scan_credentials")
            .Call("list_tasks")
            .Call("propose_credential_storage", new Dictionary<string, object?>
            {
                ["candidate_ids"] = new[] { "credential-1" },
            })
            .Call("propose_task_state", new Dictionary<string, object?>
            {
                ["task_id"] = before.Id,
                ["completed"] = true,
            })
            .Reply("Ready for review.");
        await h.Ask("Store the password and complete the report task");
        IReadOnlyList<string> proposals = ProposalIds(h);
        string source = h.Window.Editor.Text;

        using (new FileStream(h.Env.Vault.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            h.Window.ApplyAiToolChanges(proposals);

        Assert.Equal(source, h.Window.Editor.Text);
        Assert.Empty(h.Env.Vault.Credentials);
        TaskDateRecord after = OnlyTask(h);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Created, after.Created);
        Assert.Equal(before.Finished, after.Finished);
    });

    [Theory]
    [InlineData("en", false)]
    [InlineData("en", true)]
    [InlineData("th", false)]
    [InlineData("th", true)]
    public void Review_rows_keep_selection_icons_and_readable_actions_in_a_narrow_themed_pane(string language, bool dark) => UiThread.Run(() =>
    {
        string previous = UiText.Language;
        try
        {
            UiText.SetLanguage(language);
            var pane = new AiPane { Visibility = Visibility.Visible };
            pane.ApplyTheme(dark);
            var rows = new[]
            {
                new AiToolReviewRow("task-state:t1:completed",
                    UiText.Format("Complete: {0}", "Send report"),
                    UiText.Format("Line {0} · tracked dates update automatically", 3), "\uE930"),
                new AiToolReviewRow("store-credential:credential-1",
                    UiText.Format("Store credential: {0}", "password"),
                    UiText.Format("Line {0} · value hidden · replaces every exact copy in this note", 7), "\uE72E"),
            };

            pane.Show(new AiPaneView(
                Title: "Ask AI",
                SourceLine: UiText.Format("Whole note, {0} characters", 42),
                AskForInstruction: false,
                Result: "Ready for review.",
                Markdown: true,
                Running: false,
                Status: "",
                ShowReplace: false,
                CanReplace: false,
                CanInsert: false,
                CanCopy: true,
                CanRetry: true,
                CanShowChanges: false,
                Original: "",
                ShowInsert: false,
                Info: UiText.Format("Finished in {0} s", 1)));
            pane.ShowToolReview(rows, canApply: true, "Nothing has changed yet.");
            pane.Measure(new Size(420, 260));
            pane.Arrange(new Rect(0, 0, 420, 260));
            pane.UpdateLayout();

            Assert.Equal(Visibility.Visible, pane.ToolReviewPanel.Visibility);
            Assert.Equal<AiToolReviewRow>(rows, pane.ToolReviewList.Items.Cast<AiToolReviewRow>());
            Assert.All(rows, row =>
            {
                Assert.True(row.Selected);
                Assert.False(string.IsNullOrWhiteSpace(row.Glyph));
                Assert.False(string.IsNullOrWhiteSpace(row.Title));
                Assert.False(string.IsNullOrWhiteSpace(row.Detail));
            });
            Assert.Equal(UiText.Tr("Apply selected"), pane.ToolApplyButton.Content);
            Assert.True(pane.ToolApplyButton.IsEnabled);
            Assert.Equal("\uE930", ButtonIcon.GetGlyph(pane.ToolApplyButton));
            Assert.NotNull(pane.ToolApplyButton.ContentTemplate);
            Assert.InRange(pane.ToolReviewPanel.ActualWidth, 1, 420);
            Assert.Equal(Visibility.Collapsed, pane.ReplaceButton.Visibility);
            Assert.Equal(Visibility.Collapsed, pane.InsertButton.Visibility);
            Assert.NotEmpty(Descendants<CheckBox>(pane.ToolReviewList));
            Assert.All(Descendants<CheckBox>(pane.ToolReviewList), check => Assert.True(check.IsChecked == true));
            Assert.NotNull(pane.ToolReviewPanel.Background);
            Assert.NotNull(pane.ToolReviewPanel.BorderBrush);

            if (language == "th")
            {
                string folder = Path.Combine(PadWindowTests.RepoRoot(), "artifacts", "micapad-assistant-tools");
                Directory.CreateDirectory(folder);
                pane.Measure(new Size(420, 600));
                pane.Arrange(new Rect(0, 0, 420, 600));
                pane.UpdateLayout();
                Point applyAt = pane.ToolApplyButton.TranslatePoint(new Point(), pane);
                Assert.InRange(applyAt.Y, 1, 599);
                var bitmap = new RenderTargetBitmap(420, 600, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(pane);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using FileStream output = File.Create(Path.Combine(folder,
                    $"review-thai-{(dark ? "dark" : "light")}-420x600.png"));
                encoder.Save(output);
            }
        }
        finally
        {
            UiText.SetLanguage(previous);
        }
    });

    private static IEnumerable<string> OutboundText(ScriptedChatClient.Request request)
    {
        foreach (ChatMessage message in request.Messages)
        {
            yield return message.Text;
            foreach (AIContent content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text:
                        yield return text.Text;
                        break;
                    case FunctionCallContent call:
                        yield return JsonSerializer.Serialize(call.Arguments);
                        break;
                    case FunctionResultContent result:
                        yield return result.Result?.ToString() ?? "";
                        break;
                }
            }
        }
    }

    private static IReadOnlyList<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        void Visit(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) found.Add(match);
                Visit(child);
            }
        }
        Visit(root);
        return found;
    }
}
