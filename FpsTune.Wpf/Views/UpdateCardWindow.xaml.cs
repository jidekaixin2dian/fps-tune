using System.Windows;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class UpdateCardWindow : Window
{
    private readonly UpdateInfo _info;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _collapsed;
    public UpdateCardWindow(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;
        VersionText.Text = $"{UpdateService.DisplayVersion} → {info.ReleaseTag ?? info.Version}";
        NotesText.Text = info.Notes;
        StatusText.Text = Str.T("Str.UpdateOptional");
        Closed += (_, _) => _lifetime.Cancel();
    }
    private void Fold_Click(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;
        Details.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        Height = _collapsed ? 112 : 360;
        FoldButton.Content = Str.T(_collapsed ? "Str.UpdateExpand" : "Str.UpdateCollapse");
    }
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (App.SessionService.IsRunning)
        {
            StatusText.Text = Str.T("Str.UpdateSessionBusy");
            return;
        }
        InstallButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        StatusText.Text = Str.T("Str.UpdateDownloading");
        try
        {
            await UpdateInstaller.InstallAsync(_info, value => Dispatcher.Invoke(() => DownloadProgress.Value = value), _lifetime.Token);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException) { StatusText.Text = Str.T("Str.UpdateCancelled"); }
        catch (Exception ex) { StatusText.Text = Str.T("Str.UpdateFailed", ex.Message); }
        finally { InstallButton.IsEnabled = true; DownloadProgress.Visibility = Visibility.Collapsed; }
    }
}
