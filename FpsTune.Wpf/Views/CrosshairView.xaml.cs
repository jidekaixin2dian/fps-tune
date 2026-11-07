using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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

    // 保存失败每次进页面只提示一次：滑杆每拖一格都会触发，连弹会烦。
    private bool _saveFailureReported;

    // 运行状态每 250 ms 读一帧覆盖窗的判定结果；离开页面就停，别让缓存页在后台空转。
    private DispatcherTimer? _statusTimer;

    // 按键捕获：点一下按钮，再按下要用的键。第一拍只记基线（此刻按着的不算），之后取第一个新按下的键。
    private DispatcherTimer? _captureTimer;
    private string? _captureTarget;
    private bool _captureArmed;
    private readonly HashSet<string> _captureHeldAtStart = new(StringComparer.Ordinal);

    public CrosshairView()
    {
        InitializeComponent();
        Loaded += (_, _) => StartStatusTimer();
        Unloaded += (_, _) => { StopStatusTimer(); CancelCapture(); };
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        var settings = SettingsService.Current;
        var config = CrosshairConfig.FromSettings();
        CrosshairStyleBox.SelectedIndex = Array.IndexOf(
            new[] { "cross", "dot", "crossdot", "circle" },
            CrosshairConfig.NormalizeStyle(settings.CrosshairStyle)) is >= 0 and var idx and not int.MaxValue ? idx : 0;
        CrosshairCheck.IsChecked = settings.CrosshairEnabled;
        CrosshairColorBox.Text = CrosshairConfig.NormalizeColor(settings.CrosshairColor);
        CrosshairSizeSlider.Value = Math.Clamp(settings.CrosshairSize, 4, 48);
        CrosshairGapSlider.Value = Math.Clamp(settings.CrosshairGap, 0, 30);
        CrosshairThicknessSlider.Value = Math.Clamp(settings.CrosshairThickness, 1, 10);
        CrosshairOpacitySlider.Value = Math.Clamp(settings.CrosshairOpacity, 10, 100);
        // 显示方式/开镜方式/开镜隐藏/按键/延迟：都按归一后的配置回填，坏值在界面上看到的也是实际生效值。
        _suppress = true;
        try
        {
            CrosshairModeAlways.IsChecked = config.DisplayModeKind == CrosshairDisplayMode.Always;
            CrosshairModeFire.IsChecked = config.DisplayModeKind == CrosshairDisplayMode.Fire;
            CrosshairModeAds.IsChecked = config.DisplayModeKind == CrosshairDisplayMode.Ads;
            CrosshairAdsHold.IsChecked = config.AdsModeKind == CrosshairAdsMode.Hold;
            CrosshairAdsToggle.IsChecked = config.AdsModeKind == CrosshairAdsMode.Toggle;
            CrosshairHideOnAdsCheck.IsChecked = config.EffectiveHideOnAds;
            CrosshairShowDelaySlider.Value = config.ShowDelayMs;
            CrosshairHideDelaySlider.Value = config.HideDelayMs;
        }
        finally { _suppress = false; }
        BuildSwatches();
        SyncPalette(CrosshairColorBox.Text);
        UpdateTriggerLabels();
        UpdateModeDescriptions();
        UpdateDelayReadouts();
        _ready = true;
        UpdatePreview();
        // 运行状态先算一次：页面还没进可视树（离屏渲染、缓存页）时也能显示当前状态，不留空行。
        UpdateStatus();
        if (IsLoaded) StartStatusTimer();
    }

    // ── 运行状态 ──────────────────────────────────────────────────────────────

    private void StartStatusTimer()
    {
        if (_statusTimer is not null) { _statusTimer.Start(); UpdateStatus(); return; }
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        UpdateStatus();
    }

    private void StopStatusTimer() => _statusTimer?.Stop();

    /// <summary>
    /// 运行状态行：读覆盖窗最近一帧的判定（按键推定），不读取游戏。
    /// 总开关关着或覆盖窗没在跑时，这里如实说「现在不会显示」。
    /// </summary>
    private void UpdateStatus()
    {
        if (CrosshairFireStateText is null || CrosshairAdsStateText is null || CrosshairStatusHintText is null) return;
        if (!SettingsService.Current.CrosshairEnabled || App.CrosshairOverlay is not { } overlay)
        {
            CrosshairFireStateText.Text = Str.T("Str.CrosshairStatusNotPressed");
            CrosshairAdsStateText.Text = Str.T("Str.CrosshairStatusAdsOff");
            CrosshairStatusHintText.Text = Str.T("Str.CrosshairStatusMasterOff");
            return;
        }
        var frame = overlay.LastFrame;
        CrosshairFireStateText.Text = Str.T(frame.FireStatusKey);
        CrosshairAdsStateText.Text = Str.T(frame.AdsStatusKey);
        CrosshairStatusHintText.Text = Str.T(frame.Phase == CrosshairPhase.WaitingSync
            ? "Str.CrosshairSyncKeyDesc"
            : "Str.CrosshairStatusReadOnly");
    }

    // ── 显示方式 / 开镜方式 / 延迟 ────────────────────────────────────────────

    private string SelectedDisplayMode
        => CrosshairModeFire.IsChecked == true ? "fire" : CrosshairModeAds.IsChecked == true ? "ads" : "always";

    private string SelectedAdsMode => CrosshairAdsToggle.IsChecked == true ? "toggle" : "hold";

    private void DisplayMode_Changed(object sender, RoutedEventArgs e)
    {
        UpdateModeDescriptions();
        if (!_ready || _suppress) return;
        Crosshair_Changed(sender, e);
    }

    private void AdsMode_Changed(object sender, RoutedEventArgs e)
    {
        UpdateModeDescriptions();
        if (!_ready || _suppress) return;
        Crosshair_Changed(sender, e);
    }

    /// <summary>显示方式/开镜方式下面的那行说明，跟着当前选项走。</summary>
    private void UpdateModeDescriptions()
    {
        if (CrosshairModeDescText is null || CrosshairAdsModeDescText is null) return;
        var mode = SelectedDisplayMode;
        CrosshairModeDescText.Text = mode switch
        {
            "fire" => Str.T("Str.CrosshairModeFireDesc"),
            "ads" => Str.T("Str.CrosshairModeAdsDesc"),
            _ => Str.T("Str.CrosshairModeAlwaysDesc"),
        };
        CrosshairAdsModeDescText.Text = CrosshairAdsToggle?.IsChecked == true
            ? Str.T("Str.CrosshairAdsToggleDesc")
            : Str.T("Str.CrosshairAdsHoldDesc");
        // 据枪模式的显示条件本来就是「在开镜」，再叠一层「开镜时隐藏」只会互相打架，这里置灰。
        if (CrosshairHideOnAdsCheck is not null) CrosshairHideOnAdsCheck.IsEnabled = mode != "ads";
    }

    private void Delay_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // InitializeComponent 期间滑杆按 XAML 默认值逐个触发，读数控件可能还没建好。
        if (CrosshairShowDelayValue is null || CrosshairHideDelayValue is null) return;
        UpdateDelayReadouts();
        if (!_ready || _suppress) return;
        Crosshair_Changed(sender, e);
    }

    /// <summary>延迟读数：0 显示「立即」，其余直接给毫秒数（滑杆本身就是按毫秒走的）。</summary>
    private void UpdateDelayReadouts()
    {
        if (CrosshairShowDelayValue is null || CrosshairHideDelayValue is null) return;
        CrosshairShowDelayValue.Text = DelayText(CrosshairShowDelaySlider.Value);
        CrosshairHideDelayValue.Text = DelayText(CrosshairHideDelaySlider.Value);
    }

    private static string DelayText(double value)
        => value <= 0 ? Str.T("Str.CrosshairDelayImmediate") : $"{Math.Round(value):0} ms";

    // ── 按键捕获 ──────────────────────────────────────────────────────────────

    private void CaptureFireTrigger_Click(object sender, RoutedEventArgs e) => BeginCapture("fire");

    private void CaptureAdsTrigger_Click(object sender, RoutedEventArgs e) => BeginCapture("ads");

    private void CaptureSyncTrigger_Click(object sender, RoutedEventArgs e) => BeginCapture("sync");

    /// <summary>
    /// 开始捕获：按钮变成「正在等待按键…」，随后每 40 ms 看一次按键状态。
    /// 不装钩子、不抢焦点——只是轮询，和覆盖窗读键用的同一条路径。
    /// </summary>
    private void BeginCapture(string target)
    {
        if (!_ready) return;
        CancelCapture();
        _captureTarget = target;
        _captureArmed = false;
        _captureHeldAtStart.Clear();
        CaptureButton(target).Content = Str.T("Str.CrosshairTriggerCapturing");
        _captureTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        _captureTimer.Tick += (_, _) => CaptureTick();
        _captureTimer.Start();
    }

    private void CancelCapture()
    {
        _captureTimer?.Stop();
        _captureTimer = null;
        _captureTarget = null;
        _captureArmed = false;
        _captureHeldAtStart.Clear();
        UpdateTriggerLabels();
    }

    private void CaptureTick()
    {
        if (_captureTarget is null) { CancelCapture(); return; }
        if (!_captureArmed)
        {
            // 第一拍只记基线：点按钮那一下的左键还按着，按住 Esc 也不该被当成选择。
            foreach (var name in CrosshairTriggers.BindableNames)
                if (CrosshairTriggers.IsHeld(name)) _captureHeldAtStart.Add(name);
            _captureArmed = true;
            return;
        }
        if (!_captureHeldAtStart.Contains("Escape") && CrosshairTriggers.IsHeld("Escape"))
        {
            CancelCapture();
            return;
        }
        foreach (var name in CrosshairTriggers.BindableNames)
        {
            if (!CrosshairTriggers.IsHeld(name))
            {
                _captureHeldAtStart.Remove(name);
                continue;
            }
            if (_captureHeldAtStart.Contains(name)) continue;
            AcceptCapture(name);
            return;
        }
    }

    private void AcceptCapture(string triggerName)
    {
        var target = _captureTarget;
        CancelCapture();
        var settings = SettingsService.Current;
        var backup = CrosshairConfig.FromSettings();
        try
        {
            switch (target)
            {
                case "fire": settings.CrosshairFireTrigger = triggerName; break;
                case "ads": settings.CrosshairAdsTrigger = triggerName; break;
                case "sync": settings.CrosshairSyncTrigger = triggerName; break;
                default: return;
            }
            SettingsService.Save(settings);
            App.ApplyCrosshairSettings();
            UpdateTriggerLabels();
        }
        catch (Exception ex)
        {
            RestoreSettings(settings, backup);
            ReportSaveFailure(ex);
        }
    }

    private Button CaptureButton(string target) => target switch
    {
        "ads" => CrosshairAdsTriggerButton,
        "sync" => CrosshairSyncTriggerButton,
        _ => CrosshairFireTriggerButton,
    };

    /// <summary>三个按键按钮显示当前绑定的键名；同步键没设就是「未设置」。</summary>
    private void UpdateTriggerLabels()
    {
        if (CrosshairFireTriggerButton is null) return;
        var settings = SettingsService.Current;
        CrosshairFireTriggerButton.Content = TriggerText(settings.CrosshairFireTrigger, Str.T("Str.CrosshairTriggerCapture"));
        CrosshairAdsTriggerButton.Content = TriggerText(settings.CrosshairAdsTrigger, Str.T("Str.CrosshairTriggerCapture"));
        CrosshairSyncTriggerButton.Content = TriggerText(settings.CrosshairSyncTrigger, Str.T("Str.CrosshairSyncKeyUnset"));
    }

    private static string TriggerText(string? stored, string whenUnset)
        => CrosshairTriggers.DisplayName(stored) is { Length: > 0 } name ? name : whenUnset;

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
            var hideOnAds = CrosshairHideOnAdsCheck.IsChecked == true;
            // 旧字段照写：一直显示模式读的就是 AdaptiveHide，老用户升级后行为不变。
            settings.CrosshairAdaptiveHide = hideOnAds;
            settings.CrosshairHideOnAds = hideOnAds;
            settings.CrosshairDisplayMode = SelectedDisplayMode;
            settings.CrosshairAdsMode = SelectedAdsMode;
            settings.CrosshairShowDelayMs = (int)Math.Round(CrosshairShowDelaySlider.Value);
            settings.CrosshairHideDelayMs = (int)Math.Round(CrosshairHideDelaySlider.Value);
            SettingsService.Save(settings);
            App.ApplyCrosshairSettings();
            UpdatePreview();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            RestoreSettings(settings, backup);
            _suppress = true;
            try
            {
                CrosshairCheck.IsChecked = backup.Enabled;
                CrosshairHideOnAdsCheck.IsChecked = backup.EffectiveHideOnAds;
                CrosshairColorBox.Text = backup.Color;
                CrosshairModeAlways.IsChecked = backup.DisplayModeKind == CrosshairDisplayMode.Always;
                CrosshairModeFire.IsChecked = backup.DisplayModeKind == CrosshairDisplayMode.Fire;
                CrosshairModeAds.IsChecked = backup.DisplayModeKind == CrosshairDisplayMode.Ads;
                CrosshairAdsHold.IsChecked = backup.AdsModeKind == CrosshairAdsMode.Hold;
                CrosshairAdsToggle.IsChecked = backup.AdsModeKind == CrosshairAdsMode.Toggle;
                CrosshairShowDelaySlider.Value = backup.ShowDelayMs;
                CrosshairHideDelaySlider.Value = backup.HideDelayMs;
            }
            finally { _suppress = false; }
            UpdateModeDescriptions();
            UpdateDelayReadouts();
            UpdateTriggerLabels();
            ReportSaveFailure(ex);
        }
    }

    /// <summary>写盘/应用失败时把设置对象整份回滚（含本轮新增字段），界面随后按备份回填。</summary>
    private static void RestoreSettings(AppSettings settings, CrosshairConfig backup)
    {
        settings.CrosshairEnabled = backup.Enabled;
        settings.CrosshairStyle = backup.Style;
        settings.CrosshairColor = backup.Color;
        settings.CrosshairSize = backup.Size;
        settings.CrosshairGap = backup.Gap;
        settings.CrosshairThickness = backup.Thickness;
        settings.CrosshairOpacity = backup.Opacity;
        settings.CrosshairAdaptiveHide = backup.AdaptiveHide;
        settings.CrosshairHideOnAds = backup.HideOnAds;
        settings.CrosshairDisplayMode = backup.DisplayMode;
        settings.CrosshairAdsMode = backup.AdsMode;
        settings.CrosshairFireTrigger = backup.FireTrigger;
        settings.CrosshairAdsTrigger = backup.AdsTrigger;
        settings.CrosshairSyncTrigger = backup.SyncTrigger;
        settings.CrosshairShowDelayMs = backup.ShowDelayMs;
        settings.CrosshairHideDelayMs = backup.HideDelayMs;
    }

    // 以前这里只把数值弹回去，用户只看到滑杆自己跳回原样，不知道为什么。
    private void ReportSaveFailure(Exception ex)
    {
        if (_saveFailureReported) return;
        _saveFailureReported = true;
        DialogService.Warning(Str.T("Str.CrosshairSaveFailed"), ex.Message);
    }
}
