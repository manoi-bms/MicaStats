using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kil0bitSystemMonitor.Services.Conference;

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
        UsageText.Text = $"AI allowance: {usage.Used}/{usage.Limit} today · automatic analysis can use up to 360 requests/hour. Change the limit in Settings → AI.";
        AiQuotaText.Text = $"{usage.Used} / {usage.Limit} today";
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
        if (sources.Count == 0) return "Unsupported: no cited meeting or note evidence.";
        var references = _intelligence.References;
        var segments = _session.Segments;
        return "Sources: " + string.Join(", ", sources.Select(id =>
        {
            var note = references.FirstOrDefault(reference => reference.Id == id);
            if (note != null) return note.Title;
            var segment = segments.FirstOrDefault(segment => segment.Id == id);
            return segment == null ? id : $"{(segment.Source == MeetingSource.Microphone ? "Microphone" : "Conference audio")} {segment.Start:hh\\:mm\\:ss}";
        }));
    }

    private void Question_Changed(object sender, SelectionChangedEventArgs e) => ShowSelectedAnswer();

    private void ShowSelectedAnswer()
    {
        if (AnswerText == null) return;
        var question = QuestionChoice.SelectedItem as MeetingQuestion;
        AnswerText.Text = question == null ? "" : $"{question.Answer}\n\n{Sources(question.Sources)}"
            + (string.IsNullOrWhiteSpace(question.MissingInformation) ? "" : $"\nMissing information: {question.MissingInformation}");
        bool hasAnswer = !string.IsNullOrWhiteSpace(question?.Answer);
        CopyButton.IsEnabled = hasAnswer;
        UseForSpeechButton.IsEnabled = hasAnswer;
        AnswerEmpty.Visibility = question == null ? Visibility.Visible : Visibility.Collapsed;
        AnswerActionStatus.Text = hasAnswer ? "Copy includes sources; Prepare speech opens the Speech tab." : "Ask a question below to prepare an answer.";
    }

    private async void Ask_Click(object sender, RoutedEventArgs e)
    {
        if (_askingQuestion || _closing || _closed) return;
        string question = QuestionText.Text.Trim();
        if (!_session.IsActive) { _questionNotice = "Start listening before asking a private question."; RefreshQuestionState(); return; }
        if (question.Length == 0) { _questionNotice = "Type a question about this meeting first."; RefreshQuestionState(); QuestionText.Focus(); return; }
        _questionNotice = null;
        _askingQuestion = true;
        RefreshQuestionState();
        try { await _intelligence.AskAsync(question, _lifetime.Token); }
        catch (MeetingException error) { _questionNotice = error.Message; }
        catch (OperationCanceledException) { }
        catch { _questionNotice = "Could not prepare an answer. Check Settings → AI and try again."; }
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
        AskButton.Content = _askingQuestion ? "Preparing…" : "✨ Ask privately";
        QuestionText.IsReadOnly = _askingQuestion;
        QuestionStatus.Text = _askingQuestion ? "Preparing a private answer…"
            : !_session.IsActive ? "Start listening to ask a private question. Received results remain available for copying and saving."
            : _questionNotice ?? "Type your question and press Enter. Answers use the transcript and selected notes.";
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
        try { _copyText(AnswerText.Text); AnswerActionStatus.Text = "✓ Answer and supporting context copied."; }
        catch { AnswerActionStatus.Text = "Could not copy. Select the answer text and press Ctrl+C to try again."; }
    }
}
