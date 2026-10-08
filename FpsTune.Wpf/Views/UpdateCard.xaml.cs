using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views.Controls;

namespace FpsTune.Wpf.Views;

public partial class UpdateCard : UserControl
{
    private readonly UpdateInfo _info;
    private readonly CancellationTokenSource _lifetime = new();
    public NotificationCard Notification => Card;
    public UpdateCard(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;
        Card.SetHeader(Str.T("Str.UpdateAvailable"), $"{UpdateService.DisplayVersion} → {info.ReleaseTag ?? info.Version}");
        Card.SetResourceReference(NotificationCard.NotificationTitleProperty, "Str.UpdateAvailable");
        NotesText.Text = info.Notes;
        StatusText.SetResourceReference(TextBlock.TextProperty, "Str.UpdateOptional");
    }
    public void Cancel() => _lifetime.Cancel();
    private void Later_Click(object sender, RoutedEventArgs e) => Card.SetExpanded(false);
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (App.SessionService.IsRunning || ExperimentRunner.IsRunning)
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
        catch (Exception ex) { StatusText.Text = Str.T("Str.UpdateFailed", PrivacyScrub.Sanitize(ex.Message)); }
        finally { InstallButton.IsEnabled = true; DownloadProgress.Visibility = Visibility.Collapsed; }
    }
}
