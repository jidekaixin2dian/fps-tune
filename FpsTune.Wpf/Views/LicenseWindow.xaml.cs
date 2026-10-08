using System.Windows;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class LicenseWindow : Window
{
    public LicenseWindow()
    {
        InitializeComponent();
        ProjectText.Text = LicenseService.ProjectLicense;
        ThirdPartyText.Text = LicenseService.ThirdPartyNotices;
    }
}
