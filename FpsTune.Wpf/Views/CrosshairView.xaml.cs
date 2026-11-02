using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

/// <summary>外置准星独立页：覆盖窗只跟随前台显示器，不读写游戏进程。</summary>
public partial class CrosshairView : UserControl
{
    // InitializeComponent 期间（元素属性赋值时）事件即会触发，此时后续命名字段尚未 Connect；
    // 只允许 OnInitialized 全部完成后响应真实交互，避免半初始化状态触达 catch 回滚分支。
    private bool _ready;
    private bool _suppress;

    public CrosshairView() { InitializeComponent(); }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        var settings = SettingsService.Current;
        CrosshairStyleBox.SelectedIndex = Array.IndexOf(
            new[] { "cross", "dot", "crossdot", "circle" },
            CrosshairConfig.NormalizeStyle(settings.CrosshairStyle)) is >= 0 and var idx and not int.MaxValue ? idx : 0;
        CrosshairCheck.IsChecked = settings.CrosshairEnabled;
        CrosshairAdaptiveCheck.IsChecked = settings.CrosshairAdaptiveHide;
        CrosshairColorBox.Text = CrosshairConfig.NormalizeColor(settings.CrosshairColor);
        CrosshairSizeSlider.Value = Math.Clamp(settings.CrosshairSize, 4, 48);
        CrosshairGapSlider.Value = Math.Clamp(settings.CrosshairGap, 0, 30);
        CrosshairThicknessSlider.Value = Math.Clamp(settings.CrosshairThickness, 1, 10);
        CrosshairOpacitySlider.Value = Math.Clamp(settings.CrosshairOpacity, 10, 100);
        _ready = true;
        UpdatePreview();
    }

    /// <summary>颜色输入：能解析成 #RRGGBB 就立刻生效，不用等失去焦点。</summary>
    private void Crosshair_ColorChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _suppress) return;
        var text = (CrosshairColorBox.Text ?? "").Trim();
        if (text.Length == 6 && !text.StartsWith('#')) text = "#" + text;
        if (text.Length != 7 || !text.StartsWith('#') || !text.Skip(1).All(Uri.IsHexDigit)) return;
        Crosshair_Changed(sender, e);
    }

    /// <summary>设置页预览：与覆盖窗同一份图形，颜色/样式/透明度改完立刻能看到。</summary>
    private void UpdatePreview()
    {
        if (CrosshairPreviewHost is null) return;
        CrosshairPreviewHost.Content = CrosshairShape.Build(CrosshairConfig.FromSettings());
    }

    private void Crosshair_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _suppress) return;
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
            UpdatePreview();
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
            _suppress = true;
            CrosshairCheck.IsChecked = backup.Enabled;
            CrosshairAdaptiveCheck.IsChecked = backup.AdaptiveHide;
            _suppress = false;
        }
    }
}
