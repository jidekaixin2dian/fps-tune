using System.Windows;
using System.Windows.Media;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class ConsoleView
{
    private readonly GamePreparationService _preparation;
    private bool _preparing;

    private async void StartPreparation_Click(object sender, RoutedEventArgs e)
    {
        if (_preparing || _refreshing || _locating) return;
        if (!SettingsService.Current.Preparation.Configured) await ConfigurePreparationAsync();
        else await RunPreparationAsync();
    }
    private async void ConfigurePreparation_Click(object sender, RoutedEventArgs e) => await ConfigurePreparationAsync();

    private async Task ConfigurePreparationAsync()
    {
        if (_preparing || GamePreparationService.IsRunning) return;
        var dialog = new GamePreparationWindow { Owner = Window.GetWindow(this) };
        DialogService.PrepareWindowForTest?.Invoke(dialog);
        if (dialog.ShowDialog() == true)
        {
            PreparationStatus.Visibility = Visibility.Visible; PreparationStatus.Text = Str.T("Str.PrepareConfigured");
            if (dialog.RunNow) await RunPreparationAsync();
        }
        else if (dialog.ReviewBasics) PrepareDelta_Click(this, new RoutedEventArgs());
    }

    private async Task RunPreparationAsync()
    {
        var options = SettingsService.Current.Preparation;
        if (_preparing) return;
        if (_preparation.IsBusy) { DialogService.Warning(Str.T("Str.PrepareFailed"), Str.T("Str.MemoryBusy")); return; }
        if (_preparation.NeedsAdministrator(options))
        {
            if (DialogService.Confirm(Str.T("Str.PrepareAdminTitle"), Str.T("Str.PrepareAdminBody"), confirmText: Str.T("Str.RestartAsAdminShort")))
                AdminHelper.RestartAsAdministrator();
            return;
        }
        _preparing = true;
        GameSwitcher.IsEnabled = LocateDeltaButton.IsEnabled = RefreshButton.IsEnabled = false;
        UpdatePreparation();
        PreparationStatus.Visibility = Visibility.Visible; PreparationStatus.Text = Str.T("Str.PrepareRunning");
        PreparationStatus.Foreground = (Brush)FindResource("TextSecondaryBrush");
        try
        {
            var results = await _preparation.RunAsync(options);
            var ok = results.All(r => r.Succeeded);
            var details = string.Join("\n\n", results.Select(r => Str.T("Str.PrepareStepResult", r.Name,
                Str.T(r.Succeeded ? "Str.PrepareStepDone" : "Str.PrepareStepAttention"), r.Detail)));
            PreparationStatus.Text = Str.T(ok ? "Str.PrepareDone" : "Str.PreparePartial"); PreparationStatus.ToolTip = details;
            PreparationStatus.Foreground = (Brush)FindResource(ok ? "OkBrush" : "WarningBrush");
            if (!ok) DialogService.Warning(Str.T("Str.PreparePartial"), details + "\n\n" + Str.T("Str.PrepareRecovery"));
        }
        catch (Exception ex)
        {
            PreparationStatus.Text = Str.T("Str.PrepareFailed"); PreparationStatus.ToolTip = PrivacyScrub.Sanitize(ex.Message);
            PreparationStatus.Foreground = (Brush)FindResource("WarningBrush");
            DialogService.Warning(Str.T("Str.PrepareFailed"), ex.Message);
        }
        finally
        {
            _preparing = false;
            GameSwitcher.IsEnabled = LocateDeltaButton.IsEnabled = RefreshButton.IsEnabled = true;
            UpdatePreparation();
        }
    }
}
