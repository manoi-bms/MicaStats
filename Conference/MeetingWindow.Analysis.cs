using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kil0bitSystemMonitor.Services.Conference;
using Kil0bitSystemMonitor.Services.Localization;

namespace Kil0bitSystemMonitor.Conference;

public partial class MeetingWindow
{
    private MeetingAnalysis? _displayedAnalysis;
    private bool _askingQuestion;
    private string? _questionNotice;

    private void AiLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AiLanguageChoice?.SelectedValue is string language)
            _config.MeetingAiResponseLanguage = language;
    }

    private void RefreshAnalysis()
    {
        AnalysisStatus.Text = _intelligence.Status;
        if (_intelligence.Analysis?.ContextNotice is { } notice) AnalysisStatus.Text += " " + notice;
        var usage = _usage();
        UsageText.Text = UiText.Format("AI allowance: {0}/{1} today · automatic analysis can use up to 360 requests/hour. Change the limit in Settings → AI.", usage.Used, usage.Limit);
        AiQuotaText.Text = UiText.Format("{0} / {1} today", usage.Used, usage.Limit);
        RefreshQuestionState();
        var analysis = _intelligence.Analysis;
        SummaryEmpty.Visibility = analysis == null ? Visibility.Visible : Visibility.Collapsed;
        if (ReferenceEquals(analysis, _displayedAnalysis)) return;
        ClearDerivedSpeech();
        string? selectedQuestion = (QuestionChoice.SelectedItem as MeetingQuestion)?.Question;
        _displayedAnalysis = analysis;
        var text = new StringBuilder();
        if (analysis != null)
        {
            text.AppendLine(analysis.Summary).AppendLine();
            foreach (var point in analysis.Points)
                text.AppendLine($"• {point.Text}\n  {Sources(point.Sources)}\n");
        }
        SummaryText.Text = text.ToString();
        QuestionChoice.ItemsSource = analysis?.Questions;
        QuestionChoice.SelectedItem = analysis?.Questions.FirstOrDefault(question => question.Question == selectedQuestion)
            ?? analysis?.Questions.FirstOrDefault();
        ShowSelectedAnswer();
    }

    private string Sources(System.Collections.Generic.IReadOnlyList<string> sources)
    {
        if (sources.Count == 0) return UiText.Tr("Unsupported: no cited meeting or note evidence.");
        var references = _intelligence.References;
        var segments = _session.Segments;
        return UiText.Format("Sources: {0}", string.Join(", ", sources.Select(id =>
        {
            var note = references.FirstOrDefault(reference => reference.Id == id);
            if (note != null) return note.Title;
            var segment = segments.FirstOrDefault(segment => segment.Id == id);
            return segment == null ? id : $"{UiText.Tr(segment.Source == MeetingSource.Microphone ? "Microphone" : "Conference audio")} {segment.Start:hh\\:mm\\:ss}";
        })));
    }

    internal bool NavigateToCitedSegment(string sourceId)
    {
        if (!TranscriptText.SelectSegment(sourceId)) return false;
        MeetingTabs.SelectedIndex = 0;
        return true;
    }

    private void Question_Changed(object sender, SelectionChangedEventArgs e) => ShowSelectedAnswer();

    private void ShowSelectedAnswer()
    {
        if (AnswerText == null) return;
        var question = QuestionChoice.SelectedItem as MeetingQuestion;
        AnswerText.Text = question == null ? "" : $"{question.Answer}\n\n{Sources(question.Sources)}"
            + (string.IsNullOrWhiteSpace(question.MissingInformation) ? "" : "\n" + UiText.Format("Missing information: {0}", question.MissingInformation));
        bool hasAnswer = !string.IsNullOrWhiteSpace(question?.Answer);
        CopyButton.IsEnabled = hasAnswer;
        UseForSpeechButton.IsEnabled = hasAnswer;
        AnswerEmpty.Visibility = question == null ? Visibility.Visible : Visibility.Collapsed;
        AnswerActionStatus.Text = UiText.Tr(hasAnswer ? "Copy includes sources; Prepare speech opens the Speech tab." : "Ask a question below to prepare an answer.");
    }

    private async void Ask_Click(object sender, RoutedEventArgs e)
    {
        if (_askingQuestion || _closing || _closed) return;
        string question = QuestionText.Text.Trim();
        if (!_session.IsActive) { _questionNotice = UiText.Tr("Start listening before asking a private question."); RefreshQuestionState(); return; }
        if (question.Length == 0) { _questionNotice = UiText.Tr("Type a question about this meeting first."); RefreshQuestionState(); QuestionText.Focus(); return; }
        _questionNotice = null;
        _askingQuestion = true;
        RefreshQuestionState();
        try { await _intelligence.AskAsync(question, _lifetime.Token); }
        catch (MeetingException error) { _questionNotice = error.Message; }
        catch (OperationCanceledException) { }
        catch { _questionNotice = UiText.Tr("Could not prepare an answer. Check Settings → AI and try again."); }
        finally
        {
            _askingQuestion = false;
            if (!_closed && !_closing)
            {
                _questionNotice ??= _intelligence.Status;
                RefreshAnalysis();
            }
        }
    }

    private void RefreshQuestionState()
    {
        AskButton.IsEnabled = _session.IsActive && !_askingQuestion && !_closing;
        AskButton.Content = UiText.Tr(_askingQuestion ? "Preparing…" : "✨ Ask privately");
        QuestionText.IsReadOnly = _askingQuestion;
        QuestionStatus.Text = _askingQuestion ? UiText.Tr("Preparing a private answer…")
            : !_session.IsActive ? UiText.Tr("Start listening to ask a private question. Received results remain available for copying and saving.")
            : _questionNotice ?? UiText.Tr("Type your question and press Enter. Answers use the transcript and selected notes.");
    }

    private void Question_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        Ask_Click(sender, e);
    }

    private void OpenAiSettings_Click(object sender, RoutedEventArgs e) => App.ShowSettingsSection("AI");

    private void CopyAnswer_Click(object sender, RoutedEventArgs e)
    {
        if (QuestionChoice.SelectedItem is not MeetingQuestion question || string.IsNullOrWhiteSpace(question.Answer)) return;
        try { _copyText(AnswerText.Text); AnswerActionStatus.Text = UiText.Tr("✓ Answer and supporting context copied."); }
        catch { AnswerActionStatus.Text = UiText.Tr("Could not copy. Select the answer text and press Ctrl+C to try again."); }
    }
}
