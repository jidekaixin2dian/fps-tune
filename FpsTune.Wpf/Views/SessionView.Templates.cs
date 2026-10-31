using System.Windows;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class SessionView
{
    internal void LoadGameConditions(string path, SessionConditions conditions)
    {
        if (Service.IsRunning || Service.IsStopping || !string.Equals(path, AppState.GamePath, StringComparison.OrdinalIgnoreCase)) return;
        var captured = SessionConditionRules.CaptureGame(path, conditions);
        if (!SessionConditionRules.IsValid(captured)) return;
        FillConditions(captured); PrepareDeltaSession();
        _preparationHint = Str.T("Str.TuningConditionsLoaded"); UpdateRunState();
    }
    private void UpdateTemplateState()
    {
        var available = !Service.IsRunning && !Service.IsStopping && !string.IsNullOrWhiteSpace(AppState.GamePath);
        SaveTemplateButton.IsEnabled = LoadTemplateButton.IsEnabled = DeleteTemplateButton.IsEnabled = available;
        TemplateStatus.Text = available ? "" : Str.T(Service.IsRunning || Service.IsStopping
            ? "Str.SessionTemplateBusy" : "Str.SessionTemplateSelectGame");
    }

    private void SaveTemplate_Click(object sender, RoutedEventArgs e)
        => TemplateAction(path =>
        {
            if (!TryReadConditions(out var conditions)) throw new ArgumentException(Str.T("Str.ConditionInvalid"));
            SessionTemplateStore.Save(path, conditions);
            return Str.T("Str.SessionTemplateSaved", GamePathService.LabelFor(path));
        });

    private void LoadTemplate_Click(object sender, RoutedEventArgs e)
        => TemplateAction(path =>
        {
            var template = SessionTemplateStore.Load(path);
            if (template is null) return Str.T("Str.SessionTemplateMissing");
            FillConditions(template);
            return Str.T("Str.SessionTemplateLoaded", GamePathService.LabelFor(path));
        });

    private void DeleteTemplate_Click(object sender, RoutedEventArgs e)
        => TemplateAction(path =>
        {
            SessionTemplateStore.Delete(path);
            return Str.T("Str.SessionTemplateDeleted");
        });

    private void TemplateAction(Func<string, string> action)
    {
        if (Service.IsRunning || Service.IsStopping || string.IsNullOrWhiteSpace(AppState.GamePath)) return;
        try { TemplateStatus.Text = action(AppState.GamePath); }
        catch (Exception ex) { TemplateStatus.Text = Str.T("Str.SessionTemplateFailed", PrivacyScrub.Sanitize(ex.Message)); }
    }
}
