using System.Windows;
using System.Windows.Media;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class ConsoleView
{
    private readonly GamePreparationService _preparation;
    private bool _preparing;
    private CancellationTokenSource? _preparationCancellation;
    private void CancelPreparation_Click(object sender, RoutedEventArgs e)
    {
        _preparationCancellation?.Cancel(); CancelPreparationButton.IsEnabled = false;
        PreparationStatus.Text = Str.T("Str.PrepareStopping");
    }

    private async void StartPreparation_Click(object sender, RoutedEventArgs e)
    {
        if (_preparing || _refreshing || _locating) return;
        if (!TryGetPreparation(out var options)) return;
        if (!options.Configured) await ConfigurePreparationAsync();
        else await RunPreparationAsync();
    }
    private async void ConfigurePreparation_Click(object sender, RoutedEventArgs e) => await ConfigurePreparationAsync();

    internal async Task ConfigurePreparationAsync()
    {
        if (_preparing || GamePreparationService.IsRunning) return;
        var dialog = new GamePreparationWindow { Owner = Window.GetWindow(this) };
        DialogService.PrepareWindowForTest?.Invoke(dialog);
        if (dialog.ShowDialog() == true)
        {
            UpdatePreparation();
            PreparationStatus.Visibility = Visibility.Visible; PreparationStatus.Text = Str.T("Str.PrepareConfigured");
            if (dialog.RunNow) await RunPreparationAsync(dialog.SavedOptions);
        }
        else if (dialog.ReviewBasics) PrepareDelta_Click(this, new RoutedEventArgs());
    }

    private async Task RunPreparationAsync(GamePreparationOptions? savedOptions = null)
    {
        GamePreparationOptions options;
        if (savedOptions is not null) options = savedOptions;
        else if (!TryGetPreparation(out options)) return;
        if (_preparing) return;
        if (_preparation.IsBusy) { DialogService.Warning(Str.T("Str.PrepareFailed"), Str.T("Str.MemoryBusy")); return; }
        if (_preparation.NeedsAdministrator(options))
        {
            if (DialogService.Confirm(Str.T("Str.PrepareAdminTitle"), Str.T("Str.PrepareAdminBody"), confirmText: Str.T("Str.RestartAsAdminShort")))
                AdminHelper.RestartAsAdministrator();
            return;
        }
        _preparing = true;
        using var cancellation = new CancellationTokenSource(); _preparationCancellation = cancellation;
        CancelPreparationButton.Visibility = Visibility.Visible; CancelPreparationButton.IsEnabled = true;
        GameSwitcher.IsEnabled = LocateDeltaButton.IsEnabled = RefreshButton.IsEnabled = false;
        UpdatePreparation();
        PreparationStatus.Visibility = Visibility.Visible; PreparationStatus.Text = Str.T("Str.PrepareRunning");
        PreparationStatus.Foreground = (Brush)FindResource("TextSecondaryBrush");
        try
        {
            var progress = new Progress<MemoryCleanupProgress>(p =>
            { if (_preparing && !cancellation.IsCancellationRequested) PreparationStatus.Text = MemoryCleanupSummary.Stage(p); });
            var results = await _preparation.RunAsync(options, memoryProgress: progress, cancellation: cancellation.Token);
            var ok = results.All(r => r.Succeeded);
            var details = string.Join("\n\n", results.Select(r => Str.T("Str.PrepareStepResult", r.Name,
                Str.T(r.Succeeded ? "Str.PrepareStepDone" : "Str.PrepareStepAttention"), r.Detail)));
            PreparationStatus.Text = cancellation.IsCancellationRequested ? Str.T("Str.PrepareStopped") : Str.T(ok ? "Str.PrepareDone" : "Str.PreparePartial"); PreparationStatus.ToolTip = details;
            PreparationStatus.Foreground = (Brush)FindResource(ok ? "OkBrush" : "WarningBrush");
            if (!ok && !cancellation.IsCancellationRequested) DialogService.Warning(Str.T("Str.PreparePartial"), details + "\n\n" + Str.T("Str.PrepareRecovery"));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { PreparationStatus.Text = Str.T("Str.PrepareStopped"); }
        catch (Exception ex)
        {
            PreparationStatus.Text = Str.T("Str.PrepareFailed"); PreparationStatus.ToolTip = PrivacyScrub.Sanitize(ex.Message);
            PreparationStatus.Foreground = (Brush)FindResource("WarningBrush");
            DialogService.Warning(Str.T("Str.PrepareFailed"), ex.Message);
        }
        finally
        {
            _preparing = false;
            _preparationCancellation = null; CancelPreparationButton.Visibility = Visibility.Collapsed;
            GameSwitcher.IsEnabled = LocateDeltaButton.IsEnabled = RefreshButton.IsEnabled = true;
            UpdatePreparation();
        }
    }
    private bool TryGetPreparation(out GamePreparationOptions options)
    {
        try { options = PreparationPlanStore.Resolve(AppState.GamePath).Options; return true; }
        catch (Exception ex)
        {
            options = new(); PreparationStatus.Visibility = Visibility.Visible;
            PreparationStatus.Text = PrivacyScrub.Sanitize(ex.Message);
            return false;
        }
    }
}
