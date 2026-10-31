using System.Windows;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;

namespace FpsTune.Wpf;

public partial class MainWindow
{
    internal void OpenGameTuning(string path)
    {
        if (GameTuning.Identify(path) is null || GameContextService.IsBusy || ExperimentRunner.IsRunning || App.SessionService.IsRunning || App.SessionService.IsStopping) return;
        var dialog = new GameTuningWindow(path) { Owner = this };
        DialogService.PrepareWindowForTest?.Invoke(dialog);
        if (dialog.ShowDialog() != true || !string.Equals(path, AppState.GamePath, StringComparison.OrdinalIgnoreCase)) return;
        if (dialog.Recording is { } conditions)
        {
            NavigateTo("session"); ((SessionView)GetPage("session")).LoadGameConditions(path, conditions);
        }
        else if (dialog.OpenDisplay)
        {
            NavigateTo("display"); SelectTaggedTab(GetPage("display"), "dlss");
        }
    }
}
