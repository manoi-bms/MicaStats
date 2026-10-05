using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Kil0bitSystemMonitor.Services.Conference;

namespace Kil0bitSystemMonitor.Conference;

public partial class MeetingWindow
{
    private void RefreshNotes()
    {
        string? availableId = (AvailableNotes.SelectedItem as MeetingNoteChoice)?.Id;
        string? selectedId = (SelectedNotes.SelectedItem as MeetingNoteChoice)?.Id;
        AvailableNotes.ItemsSource = _references.Available;
        AvailableNotes.SelectedItem = _references.Available.FirstOrDefault(x => x.Id == availableId);
        SelectedNotes.ItemsSource = _references.Selected;
        SelectedNotes.SelectedItem = _references.Selected.FirstOrDefault(x => x.Id == selectedId);
        NotesStatus.Text = _references.Status;
        NotesCountText.Text = $"{_references.Selected.Count} selected";
    }

    private async void RefreshNotes_Click(object sender, RoutedEventArgs e)
    {
        try { await _references.RefreshAvailableAsync(_lifetime.Token); }
        catch (OperationCanceledException) { }
        catch { NotesStatus.Text = "Could not list notes. Open MicaPad and try again."; }
    }

    private async void AddReference_Click(object sender, RoutedEventArgs e)
    {
        if (AvailableNotes.SelectedItem is not MeetingNoteChoice choice) return;
        try { await _references.SelectAsync(choice.Id, _lifetime.Token); }
        catch (OperationCanceledException) { }
        catch { NotesStatus.Text = "Could not read that note."; }
    }

    private void RemoveReference_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNotes.SelectedItem is MeetingNoteChoice choice) _references.Remove(choice.Id);
    }

    private void ReferenceContextInvalidated()
    {
        // Derived speech is UI-owned. Calls from a credential event clear services immediately below.
        if (Dispatcher.CheckAccess()) ClearDerivedSpeech();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(ClearDerivedSpeech);
    }

    internal void CredentialsStored()
    {
        if (_closing || _closed) return;
        _ = _speech.StopAsync();
        _ = ReloadAfterCredentialAsync();
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() =>
        {
            SpeechText.Clear();
            _speechSources = Array.Empty<string>();
            RefreshAnalysis();
        }));
    }

    private async Task ReloadAfterCredentialAsync()
    {
        try { await _references.InvalidateCredentialsAsync(_lifetime.Token); }
        catch (OperationCanceledException) { }
        catch { /* The selection coordinator keeps invalidated context cleared on a failed reload. */ }
    }
}
