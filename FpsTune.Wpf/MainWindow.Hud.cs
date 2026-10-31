using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf;

public partial class MainWindow
{
    internal PerformanceHudService HudController { get; set; } = App.Hud;
    private bool _loadingHudAppearance;
    private void HudEntry_Click(object sender, RoutedEventArgs e)
    {
        if (HudOptionsPanel.Visibility == Visibility.Visible) { HudOptionsPanel.Visibility = Visibility.Collapsed; return; }
        _loadingHudAppearance = true;
        var appearance = HudController.Appearance;
        HudStripOption.IsChecked = appearance.Layout == "strip"; HudCardOption.IsChecked = appearance.Layout == "card";
        HudCompactOption.IsChecked = appearance.Layout == "compact";
        HudTransparencySlider.Value = appearance.Transparency;
        HudLeftOption.IsChecked = appearance.Position == "top-left"; HudCenterOption.IsChecked = appearance.Position == "top-center";
        HudRightOption.IsChecked = appearance.Position == "top-right";
        _loadingHudAppearance = false;
        HudOptionsStatus.Text = ""; UpdateHudPreview(); HudController.Restore();
        HudOptionsPanel.Visibility = Visibility.Visible;
    }
    private void HudOptionsDismiss_Click(object sender, RoutedEventArgs e) => HudOptionsPanel.Visibility = Visibility.Collapsed;
    private void HudRestore_Click(object sender, RoutedEventArgs e) => HudController.Restore();
    private void HudClose_Click(object sender, RoutedEventArgs e) => HudController.Close();
    private void HudAppearance_Changed(object sender, RoutedEventArgs e) => UpdateHudPreview();
    private void HudTransparency_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateHudPreview();
    private void HudTransparencyPreset_Click(object sender, RoutedEventArgs e)
        => HudTransparencySlider.Value = double.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
    private void UpdateHudPreview()
    {
        if (_loadingHudAppearance || HudTransparencyLabel is null || HudCenterOption is null || HudOptionsStatus is null) return;
        HudTransparencyLabel.Text = Str.T("Str.HudTransparency", HudTransparencySlider.Value.ToString("F0"));
        HudController.PreviewAppearance(new(HudCompactOption.IsChecked == true ? "compact" : HudCardOption.IsChecked == true ? "card" : "strip", HudTransparencySlider.Value,
            HudLeftOption.IsChecked == true ? "top-left" : HudRightOption.IsChecked == true ? "top-right" : "top-center"));
        HudOptionsStatus.Text = "";
    }
    private void HudGamePreset_Click(object sender, RoutedEventArgs e)
    {
        HudCompactOption.IsChecked = true; HudTransparencySlider.Value = 100; HudRightOption.IsChecked = true;
        HudController.Show();
        HudOptionsStatus.Text = HudController.Window?.SetClickThrough(true) == true ? Str.T("Str.HudGameActive") : Str.T("Str.HudGameUnavailable");
    }
    private void HudAppearanceSave_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Current; var previous = settings.HudAppearance;
        settings.HudAppearance = HudController.Appearance;
        try { SettingsService.Save(settings); HudOptionsStatus.Text = Str.T("Str.HudAppearanceSaved"); }
        catch (Exception ex) { settings.HudAppearance = previous; HudOptionsStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
}
