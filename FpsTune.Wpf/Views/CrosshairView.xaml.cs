using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
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
    // 调色盘回填滑块时不能再反过来写设置（否则每次刷新都算一次用户改动）。
    private bool _paletteSync;

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
        BuildSwatches();
        SyncPalette(CrosshairColorBox.Text);
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
        SyncPalette(text);
        Crosshair_Changed(sender, e);
    }

    /// <summary>设置页预览：与覆盖窗同一份图形，颜色/样式/透明度改完立刻能看到。</summary>
    private void UpdatePreview()
    {
        if (CrosshairPreviewHost is null) return;
        CrosshairPreviewHost.Content = CrosshairShape.Build(CrosshairConfig.FromSettings());
    }

    /// <summary>离开颜色框时把内容归一成 #RRGGBB 写回，避免框里留着不被接受的输入。</summary>
    private void Crosshair_ColorCommitted(object sender, RoutedEventArgs e)
    {
        if (!_ready || _suppress) return;
        var normalized = CrosshairConfig.NormalizeColor(CrosshairColorBox.Text);
        if (!string.Equals(CrosshairColorBox.Text, normalized, StringComparison.Ordinal))
        {
            _suppress = true;
            CrosshairColorBox.Text = normalized;
            _suppress = false;
        }
        SyncPalette(normalized);
        Crosshair_Changed(sender, e);
    }

    // ── 调色盘 ────────────────────────────────────────────────────────────────
    // 三个入口（预设色块 / HSV 滑杆 / 十六进制框）都汇到 ApplyColor，只有它写设置，
    // 另一侧只做回填，避免「改滑块又触发写设置」的循环。

    private void BuildSwatches()
    {
        if (CrosshairSwatchPanel is null) return;
        foreach (var hex in CrosshairColorMath.Presets)
        {
            var swatch = new Button
            {
                Tag = hex,
                Background = new SolidColorBrush(ParseColor(hex)),
                Style = (Style)FindResource("CrosshairSwatchStyle"),
                ToolTip = hex
            };
            AutomationProperties.SetName(swatch, hex);
            swatch.Click += Swatch_Click;
            CrosshairSwatchPanel.Children.Add(swatch);
        }
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex }) return;
        ApplyColor(hex);
    }

    private void Crosshair_HsvChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // InitializeComponent 期间滑杆按 XAML 里的默认值逐个触发 ValueChanged，后面的滑杆/读数还没建好。
        if (CrosshairHueValue is null || CrosshairSaturationSlider is null || CrosshairBrightnessSlider is null
            || CrosshairSaturationValue is null || CrosshairBrightnessValue is null) return;
        var hex = CrosshairColorMath.FromHsv(CrosshairHueSlider.Value, CrosshairSaturationSlider.Value,
            CrosshairBrightnessSlider.Value);
        UpdatePaletteReadouts(hex);
        if (!_ready || _paletteSync || _suppress) return;
        // 滑杆只是「跟着框里的颜色取整回填」时，不要把预设色/手输的精确值改写成取整后的颜色。
        if (SlidersMirrorCurrentColor()) return;
        ApplyColor(hex);
    }

    /// <summary>三个滑杆是否正好等于十六进制框里颜色取整后的 HSV（回填态，不该反写颜色）。</summary>
    private bool SlidersMirrorCurrentColor()
    {
        if (!CrosshairColorMath.TryParse(CrosshairColorBox.Text, out var rgb)) return false;
        var (hue, saturation, value) = CrosshairColorMath.ToHsv(rgb);
        return Math.Abs(Math.Round(hue) - CrosshairHueSlider.Value) < 0.001
            && Math.Abs(Math.Round(saturation) - CrosshairSaturationSlider.Value) < 0.001
            && Math.Abs(Math.Round(value) - CrosshairBrightnessSlider.Value) < 0.001;
    }

    /// <summary>唯一写入口：归一颜色 → 回填十六进制框 → 同步调色盘 → 保存设置并刷新预览。</summary>
    private void ApplyColor(string color)
    {
        var normalized = CrosshairConfig.NormalizeColor(color);
        if (!string.Equals(CrosshairColorBox.Text, normalized, StringComparison.Ordinal))
        {
            _suppress = true;
            CrosshairColorBox.Text = normalized;
            _suppress = false;
        }
        SyncPalette(normalized);
        Crosshair_Changed(this, new RoutedEventArgs());
    }

    /// <summary>按当前颜色回填 HSV 滑杆与读数（色块与选中态由 UpdatePaletteReadouts 统一刷）。</summary>
    private void SyncPalette(string? text)
    {
        if (!CrosshairColorMath.TryParse(text, out var rgb)) return;
        var (hue, saturation, value) = CrosshairColorMath.ToHsv(rgb);
        _paletteSync = true;
        try
        {
            CrosshairHueSlider.Value = hue;
            CrosshairSaturationSlider.Value = saturation;
            CrosshairBrightnessSlider.Value = value;
        }
        finally { _paletteSync = false; }
        // 读数按滑杆现值（已按 1 取整）算，保证色块、读数与框里的十六进制永远同色。
        UpdatePaletteReadouts(CrosshairColorMath.FromHsv(CrosshairHueSlider.Value, CrosshairSaturationSlider.Value,
            CrosshairBrightnessSlider.Value));
    }

    /// <summary>读数、当前色块与预设选中态：读数取滑杆现值，选中的预设色以十六进制框为准。</summary>
    private void UpdatePaletteReadouts(string hex)
    {
        if (CrosshairHueValue is null || CrosshairColorSwatch is null) return;
        var current = CrosshairConfig.NormalizeColor(CrosshairColorBox?.Text ?? hex);
        CrosshairHueValue.Text = $"{CrosshairHueSlider.Value:0}°";
        CrosshairSaturationValue.Text = $"{CrosshairSaturationSlider.Value:0}%";
        CrosshairBrightnessValue.Text = $"{CrosshairBrightnessSlider.Value:0}%";
        CrosshairColorSwatch.Background = new SolidColorBrush(ParseColor(current));
        if (CrosshairSwatchPanel is null) return;
        foreach (var child in CrosshairSwatchPanel.Children.OfType<Button>())
        {
            var selected = string.Equals(child.Tag as string, current, StringComparison.OrdinalIgnoreCase);
            child.BorderThickness = new Thickness(selected ? 2 : 1);
            child.SetResourceReference(Control.BorderBrushProperty, selected ? "AccentBrush" : "BorderBrush");
        }
    }

    private static Color ParseColor(string hex)
        => CrosshairColorMath.TryParse(hex, out var rgb) ? Color.FromRgb(rgb.R, rgb.G, rgb.B) : Color.FromRgb(0x00, 0xFF, 0x66);

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
            CrosshairColorBox.Text = backup.Color;
            _suppress = false;
        }
    }
}
