using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class FilterView : UserControl
{
    private bool _crosshairLoaded;
    private bool _suppressCrosshair;
    public FilterView() { InitializeComponent(); }
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        var settings = SettingsService.Current;
        CrosshairStyleBox.SelectedIndex = Array.IndexOf(
            new[] { "cross", "dot", "crossdot", "circle" },
            CrosshairConfig.NormalizeStyle(settings.CrosshairStyle)) is >= 0 and var idx and not int.MaxValue ? idx : 0;
        _crosshairLoaded = true;
        CrosshairCheck.IsChecked = settings.CrosshairEnabled;
        CrosshairAdaptiveCheck.IsChecked = settings.CrosshairAdaptiveHide;
        _crosshairLoaded = false;
        CrosshairColorBox.Text = CrosshairConfig.NormalizeColor(settings.CrosshairColor);
        CrosshairSizeSlider.Value = Math.Clamp(settings.CrosshairSize, 4, 48);
        CrosshairGapSlider.Value = Math.Clamp(settings.CrosshairGap, 0, 30);
        CrosshairThicknessSlider.Value = Math.Clamp(settings.CrosshairThickness, 1, 10);
        CrosshairOpacitySlider.Value = Math.Clamp(settings.CrosshairOpacity, 10, 100);
    }
    private void Crosshair_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressCrosshair || _crosshairLoaded || CrosshairCheck.IsChecked is null) return;
        var settings = SettingsService.Current;
        var backup = CrosshairConfig.FromSettings();
        try
        {
            settings.CrosshairEnabled = CrosshairCheck.IsChecked == true;
            settings.CrosshairStyle = ((ComboBoxItem)CrosshairStyleBox.SelectedItem)?.Tag as string ?? "cross";
            settings.CrosshairColor = CrosshairConfig.NormalizeColor(CrosshairColorBox.Text);
            settings.CrosshairSize = CrosshairSizeSlider.Value;
            settings.CrosshairGap = CrosshairGapSlider.Value;
            settings.CrosshairThickness = CrosshairThicknessSlider.Value;
            settings.CrosshairOpacity = CrosshairOpacitySlider.Value;
            settings.CrosshairAdaptiveHide = CrosshairAdaptiveCheck.IsChecked == true;
            SettingsService.Save(settings);
            App.ApplyCrosshairSettings();
        }
        catch (Exception)
        {
            settings.CrosshairEnabled = backup.Enabled;
            settings.CrosshairStyle = backup.Style;
            settings.CrosshairColor = backup.Color;
            settings.CrosshairSize = backup.Size;
            settings.CrosshairGap = backup.Gap;
            settings.CrosshairThickness = backup.Thickness;
            settings.CrosshairOpacity = backup.Opacity;
            settings.CrosshairAdaptiveHide = backup.AdaptiveHide;
            _suppressCrosshair = true;
            CrosshairCheck.IsChecked = backup.Enabled;
            CrosshairAdaptiveCheck.IsChecked = backup.AdaptiveHide;
            _suppressCrosshair = false;
        }
    }
    private void OpenDeltaFix_Click(object sender, RoutedEventArgs e) => OpenDeltaFix();
    internal void OpenDeltaFix()
    {
        var dialog = new DeltaFrameFixWindow { Owner = Window.GetWindow(this) };
        DialogService.PrepareWindowForTest?.Invoke(dialog);
        dialog.ShowDialog();
    }
}
