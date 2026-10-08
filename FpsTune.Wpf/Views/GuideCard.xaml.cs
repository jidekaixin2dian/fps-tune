using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views.Controls;

namespace FpsTune.Wpf.Views;

public partial class GuideCard : UserControl
{
    private readonly Action<string> _navigate;
    private int _step;
    private static readonly string[] Targets = ["home", "detect", "opt", "session", "ab", "backup"];
    private static readonly string[] Titles = ["Str.GuideStep1", "Str.GuideStep2", "Str.GuideStep3", "Str.GuideStep4", "Str.GuideStep5", "Str.GuideStep6"];
    private static readonly string[] Bodies = ["Str.GuideBody1", "Str.GuideBody2", "Str.GuideBody3", "Str.GuideBody4", "Str.GuideBody5", "Str.GuideBody6"];

    public NotificationCard Notification => Card;
    public GuideCard(Action<string> navigate)
    {
        InitializeComponent();
        _navigate = navigate;
        ShowSection(false);
    }

    public void ShowSection(bool feedback)
    {
        TutorialBody.Visibility = feedback ? Visibility.Collapsed : Visibility.Visible;
        FeedbackBody.Visibility = feedback ? Visibility.Visible : Visibility.Collapsed;
        Card.SetHeader(Str.T(feedback ? "Str.Feedback" : "Str.Guide"),
            Str.T(feedback ? "Str.CommunityTitle" : "Str.GuideIntro"));
        RefreshStep();
        Card.SetExpanded(true);
    }

    private void RefreshStep()
    {
        StepTitle.Text = Str.T(Titles[_step]);
        StepBody.Text = Str.T(Bodies[_step]);
        StepProgress.Text = $"{_step + 1:00} / {Targets.Length:00}";
        PreviousButton.IsEnabled = _step > 0;
        NextButton.Content = Str.T(_step == Targets.Length - 1 ? "Str.FinishGuide" : "Str.NextStep");
    }
    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 0) _step--;
        RefreshStep();
    }
    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == Targets.Length - 1) { Card.SetExpanded(false); return; }
        _step++;
        RefreshStep();
    }
    private void GoTo_Click(object sender, RoutedEventArgs e)
    {
        _navigate(Targets[_step]);
        Card.SetExpanded(false);
    }
    private void CopyGroup_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText("659528489"); CopyStatus.Text = Str.T("Str.GroupCopied"); }
        catch (Exception ex) { CopyStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
    private void OpenIssue_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/jidekaixin2dian/fps-tune/issues/new") { UseShellExecute = true }); }
        catch { CopyStatus.Text = Str.T("Str.CannotOpenLink"); }
    }
}
