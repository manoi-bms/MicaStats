using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Kil0bitSystemMonitor.Services.Conference;

namespace Kil0bitSystemMonitor.Conference;

public partial class MeetingWindow
{
    private MeetingAnalysis? _displayedAnalysis;

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
        AskButton.IsEnabled = _session.IsActive;
        var analysis = _intelligence.Analysis;
        if (ReferenceEquals(analysis, _displayedAnalysis)) return;
        ClearDerivedSpeech();
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
        QuestionChoice.SelectedIndex = analysis?.Questions.Count > 0 ? 0 : -1;
        ShowSelectedAnswer();
    }

    private string Sources(System.Collections.Generic.IReadOnlyList<string> sources) => sources.Count == 0
        ? "Unsupported: no cited meeting or note evidence."
        : "Sources: " + string.Join(", ", sources.Select(id =>
        {
            var note = _intelligence.References.FirstOrDefault(reference => reference.Id == id);
            return note == null ? id : $"{note.Title} ({id})";
        }));

    private void Question_Changed(object sender, SelectionChangedEventArgs e) => ShowSelectedAnswer();

    private void ShowSelectedAnswer()
    {
        if (AnswerText == null) return;
        var question = QuestionChoice.SelectedItem as MeetingQuestion;
        AnswerText.Text = question == null ? "" : $"{question.Answer}\n\n{Sources(question.Sources)}"
            + (string.IsNullOrWhiteSpace(question.MissingInformation) ? "" : $"\nMissing information: {question.MissingInformation}");
        CopyButton.IsEnabled = question != null;
        UseForSpeechButton.IsEnabled = question != null;
    }

    private async void Ask_Click(object sender, RoutedEventArgs e)
    {
        string question = QuestionText.Text.Trim();
        if (question.Length == 0) return;
        try { await _intelligence.AskAsync(question); }
        catch { AnalysisStatus.Text = "Could not prepare an answer. Check Settings → AI and try again."; }
        RefreshAnalysis();
    }

    private void CopyAnswer_Click(object sender, RoutedEventArgs e)
    {
        if (QuestionChoice.SelectedItem is not MeetingQuestion question) return;
        try { System.Windows.Clipboard.SetText(question.Answer); }
        catch { AnalysisStatus.Text = "Could not copy the answer. Select its text and copy again."; }
    }
}
