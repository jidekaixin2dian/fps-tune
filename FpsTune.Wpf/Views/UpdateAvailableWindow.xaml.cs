using System.Windows;
using System.Windows.Input;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class UpdateAvailableWindow : Window
{
    private readonly UpdateInfo _info;
    private readonly Action<string> _openLink;
    public UpdateAvailableWindow(UpdateInfo info) : this(info, ProductLinks.Open) { }
    internal UpdateAvailableWindow(UpdateInfo info, Action<string> openLink)
    {
        _info = info; _openLink = openLink;
        InitializeComponent();
        VersionText.Text = $"{UpdateService.DisplayVersion} → {info.ReleaseTag ?? info.Version}";
        NotesText.Text = ReleaseNotesText.Format(info.Notes);
    }
    private void Download_Click(object sender, RoutedEventArgs e)
    {
        try { _openLink(ProductLinks.ReleasePage(_info)); }
        catch { StatusText.Text = Str.T("Str.CannotOpenLink"); return; }
        Close();
    }
    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Button) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
