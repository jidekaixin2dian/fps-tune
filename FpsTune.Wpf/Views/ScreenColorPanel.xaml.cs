using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Globalization;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class ScreenColorPanel : UserControl
{
    private string? _identity;
    private bool _syncing, _dirty, _busy;
    private int _generation;
    private int _previewGeneration;
    private readonly Dictionary<IccFilterPreset, Task<string>> _profileTasks = [];
    private bool _screenSupported, _iccSupported, _iccRestorable, _restorable, _vibranceRestorable;
    private int? _vibrance;
    private bool _vibranceDirty;
    private string? _iccFile;
    private string? _lastOutcome;
    internal bool Unified { get; set; }
    /// <summary>面板的 ICC 选择被系统状态刷新清空时通知宿主，让滤镜列表同步取消选中。</summary>
    internal event Action? IccSelectionCleared;
    public object VibranceContent { get => VibranceHost.Content; set => VibranceHost.Content = value; }
    public object IccContent { get => IccHost.Content; set => IccHost.Content = value; }
    public object FilterContent { get => FilterHost.Content; set => FilterHost.Content = value; }
    public object PerAppContent { get => PerAppHost.Content; set => PerAppHost.Content = value; }
    public object AutomationContent { get => AutomationHost.Content; set => AutomationHost.Content = value; }

    /// <summary>当前屏幕方案 / 独立游戏方案共用左侧实拍图，只切换右侧控制区。</summary>
    internal void SetMode(bool perApp)
    {
        CurrentModePanel.Visibility = perApp ? Visibility.Collapsed : Visibility.Visible;
        ActionBar.Visibility = perApp ? Visibility.Collapsed : Visibility.Visible;
        PerAppCard.Visibility = perApp ? Visibility.Visible : Visibility.Collapsed;
        PreviewModeHint.Text = Str.T(perApp ? "Str.FilterPerAppPreviewHint" : "Str.ColorPreviewStickyHint");
        ControlsScroll.ScrollToTop();
    }

    internal void ShowPreviewForProcess(string? processName)
        => ColorPreview.SetGameSuggestion(processName);

    internal void FocusWorkspace() => ControlsScroll.ScrollToTop();
    private bool IccMode => ColorModeCombo?.SelectedIndex == 1;
    private IccFilterPreset? Preset => (IccPresetCombo.SelectedItem as Choice)?.Preset;
    private readonly List<Slider> _sliders = [];
    /// <summary>暗部平衡在滑杆列表里的下标（ParameterGrid 里被提到独立卡片，顺序不能变）。</summary>
    private const int DarkBalanceIndex = 7;
    public event Action<bool>? BusyChanged;
    private sealed record Choice(string Label, IccFilterPreset? Preset) { public override string ToString() => Label; }
    public ScreenColorPanel()
    {
        InitializeComponent();
        var names = new[] { Str.T("Str.ColorBrightness"), Str.T("Str.ColorContrast"), "Gamma", Str.T("Str.ColorShadows"),
            Str.T("Str.ColorRed"), Str.T("Str.ColorGreen"), Str.T("Str.ColorBlue"), Str.T("Str.ColorDarkBalance"), Str.T("Str.ColorHighlight") };
        var ranges = new (double Min, double Max, double Value)[] { (-20,20,0), (80,120,100), (.7,1.5,1), (0,25,0),
            (80,110,100), (80,110,100), (80,110,100), (0,100,ScreenColorService.NeutralDarkBalance), (-25,25,0) };
        for (var i = 0; i < names.Length; i++)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 16, 16) };
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center }; text.SetResourceReference(StyleProperty, "MutedTextStyle");
            var range = ranges[i]; var label = names[i];
            var slider = new Slider { Minimum = range.Min, Maximum = range.Max, Value = range.Value,
                TickFrequency = i == 2 ? .01 : 1, IsSnapToTickEnabled = true, Margin = new Thickness(0,8,0,0) };
            System.Windows.Automation.AutomationProperties.SetName(slider, label);
            text.Text = label;
            var entry = new TextBox { Text = range.Value.ToString("0.##", CultureInfo.InvariantCulture), Width = 58, Padding = new Thickness(5,3,5,3), HorizontalContentAlignment = HorizontalAlignment.Right };
            System.Windows.Automation.AutomationProperties.SetName(entry, label + " " + Str.T("Str.ColorExactValue"));
            entry.TextChanged += (_, _) =>
            {
                if (entry.IsKeyboardFocusWithin && double.TryParse(entry.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    && double.IsFinite(value) && value >= slider.Minimum && value <= slider.Maximum) slider.Value = value;
            };
            entry.LostKeyboardFocus += (_, _) =>
            {
                if (double.TryParse(entry.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
                    slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
                entry.Text = slider.Value.ToString("0.##", CultureInfo.InvariantCulture);
            };
            slider.ValueChanged += (_, _) =>
            {
                if (!entry.IsKeyboardFocusWithin) entry.Text = slider.Value.ToString("0.##", CultureInfo.InvariantCulture);
                if (!_syncing) { MarkDraft(); ColorPresetCombo.SelectedIndex = 0; UpdatePreview(); }
            };
            var header = new DockPanel(); DockPanel.SetDock(entry, Dock.Right); header.Children.Add(entry); header.Children.Add(text);
            row.Children.Add(header); row.Children.Add(slider);
            // 暗部平衡单独占一张卡片（滤镜列表正下方），卡片标题已写名字，行内标签就不重复了。
            if (i == DarkBalanceIndex)
            {
                text.Visibility = Visibility.Collapsed;
                row.Margin = new Thickness(0);
                slider.Style = (Style)FindResource("TuneSliderStyle");
                System.Windows.Automation.AutomationProperties.SetAutomationId(slider, "ColorDarkBalanceSlider");
                slider.ValueChanged += (_, _) => UpdateDarkBalanceGamma(slider.Value);
                DarkBalanceRowHost.Children.Add(row);
            }
            else ParameterGrid.Children.Add(row);
            _sliders.Add(slider);
        }
        UpdateDarkBalanceGamma(_sliders[DarkBalanceIndex].Value);
        ColorPresetCombo.ItemsSource = new[] { new Choice(Str.T("Str.ColorCustom"), null) }
            .Concat(Enum.GetValues<IccFilterPreset>().Select(preset => new Choice(ScreenColorSettings.TitleFor(preset), preset)))
            .ToArray();
        IccPresetCombo.ItemsSource = ((Choice[])ColorPresetCombo.ItemsSource).Select((choice, index) => index == 0 ? new Choice(Str.T("Str.ColorIccFile"), null) : choice).ToArray();
        _syncing = true;
        ColorPresetCombo.SelectedIndex = 0; IccPresetCombo.SelectedIndex = 1;
        _syncing = false;
        ColorApplyButton.IsEnabled = ColorRestoreButton.IsEnabled = false;
        UpdatePreview();
    }
    private async void UpdatePreview()
    {
        if (_sliders.Count != 9) return;
        ColorPreview.SetSettings(Values());
        var generation = ++_previewGeneration;
        try
        {
            // 只要选了 ICC（不管当前是哪种写入模式），预览都有可切的「色彩管理」视图；
            // 「游戏内效果」视图本身不使用这份 profile。
            string? profile = _iccFile;
            if (profile is null && (IccMode || Preset is not null))
            {
                if (Preset is { } preset)
                {
                    if (!_profileTasks.TryGetValue(preset, out var pending))
                        _profileTasks[preset] = pending = Task.Run(() => ScreenColorPreview.CacheIccProfile(IccProfileGenerator.Build(preset)));
                    try { profile = await pending; }
                    catch { _profileTasks.Remove(preset); throw; }
                }
            }
            if (generation == _previewGeneration) await ColorPreview.SetIccProfileAsync(profile);
        }
        catch (Exception ex) { if (generation == _previewGeneration) ColorStatus.Text = ex.Message; }
    }
    internal void SetVibrance(int? value, bool restorable = false, bool draft = false)
    { _vibrance = value; _vibranceRestorable = restorable; _vibranceDirty = draft; if (draft) MarkDraft(); ColorPreview.SetVibrance(value ?? 50); Availability(); }
    internal void SelectIccFile(string path)
    {
        _syncing = true;
        try { _iccFile = path; ColorModeCombo.SelectedIndex = 1; IccPresetCombo.SelectedIndex = 0; }
        finally { _syncing = false; }
        // 状态行不显示临时文件名（滤镜列表物化出来的缓存路径），文件名在 ICC 高级里单独展示。
        _dirty = true; MarkDraft(); Availability(); UpdatePreview();
    }

    /// <summary>把滤镜列表里选中的内置生成式预设填进本面板（预览与应用都走这一份状态）。</summary>
    internal void SelectGeneratedPreset(IccFilterPreset preset)
    {
        _syncing = true;
        try { _iccFile = null; ColorModeCombo.SelectedIndex = 1; IccPresetCombo.SelectedIndex = IndexOfPreset(preset); }
        finally { _syncing = false; }
        _dirty = true; MarkDraft(); Availability(); UpdatePreview();
    }

    /// <summary>按预设枚举找下拉项（枚举值与列表下标不再相等，不能再用 (int)preset + 1）。</summary>
    private int IndexOfPreset(IccFilterPreset preset)
    {
        if (IccPresetCombo?.ItemsSource is not IEnumerable<Choice> items) return 0;
        var index = 0;
        foreach (var item in items)
        {
            if (item.Preset == preset) return index;
            index++;
        }
        return 0;
    }

    /// <summary>滤镜列表取代了这里的「选择滤镜」行，宿主页可收起它避免重复入口。</summary>
    internal void SetFilterPickerVisible(bool visible)
        => FilterPickerBox.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>由外部（滤镜列表的「应用滤镜」）触发的统一应用：色彩 + ICC + 振动一起走确认流程。</summary>

    /// <summary>由外部触发的还原：屏幕色彩、ICC、振动一并回滚。</summary>
    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ColorPresetCombo is null) return;
        if (IccMode && Preset is null && _iccFile is null) { IccPresetCombo.SelectedIndex = 1; return; }
        MarkDraft(); Availability(); UpdatePreview();
    }
    private void Availability()
    {
        if (ColorApplyButton is null) return;
        ColorApplyButton.IsEnabled = !_busy && (IccMode ? _iccSupported && (Preset is not null || _iccFile is not null) : _screenSupported || _iccRestorable || _vibranceDirty && _vibrance is not null);
        ParameterGrid.IsEnabled = !_busy && _screenSupported;
        ColorResetButton.IsEnabled = !_busy && _screenSupported;
        ColorPresetCombo.IsEnabled = !_busy && _screenSupported;
        IccPresetCombo.IsEnabled = !_busy && _iccSupported;
        FilterChips.IsEnabled = !_busy && _iccSupported;
        IccEnabledCheck.IsEnabled = !_busy && _iccSupported;
        IccEnabledCheck.IsChecked = IccMode;
        FilterChips.Opacity = IccMode ? 1 : .65;
        FilterDescription.Text = Preset is { } selected ? Str.T(IccProfileGenerator.ResourceKeysFor(selected).Description) : Str.T("Str.ColorIccFile");
        IccFileText.Text = _iccFile is null ? "" : Path.GetFileName(_iccFile);
        IccFileText.Visibility = IccMode && _iccFile is not null ? Visibility.Visible : Visibility.Collapsed;
        ColorRestoreButton.IsEnabled = !_busy && (_restorable || _vibranceRestorable);
    }
    private ScreenColorSettings Values() => new(_sliders[0].Value, _sliders[1].Value, _sliders[2].Value, _sliders[3].Value,
        _sliders[4].Value, _sliders[5].Value, _sliders[6].Value, _sliders[8].Value, _sliders[7].Value);
    /// <summary>暗部平衡的实时等效 γ：与「编辑 ICC」里那块同一套换算，读数随滑块走。</summary>
    private void UpdateDarkBalanceGamma(double value)
    {
        if (DarkBalanceGammaText is null) return;
        DarkBalanceGammaText.Text = Str.T("Str.FilterEditorDarkBalanceGammaValue",
            ScreenColorService.DarkBalanceGamma(value).ToString("0.00", CultureInfo.InvariantCulture));
    }

    private void ShowSettings(ScreenColorSettings s)
    {
        _syncing = true;
        try
        {
            var values = new[] { s.Brightness, s.Contrast, s.Gamma, s.Shadows, s.Red, s.Green, s.Blue, s.DarkBalance, s.Highlight };
            for (var i = 0; i < values.Length; i++) _sliders[i].Value = values[i];
        }
        finally { _syncing = false; UpdatePreview(); }
    }
    public async Task RefreshFor(string identity)
    {
        if (_busy) return;
        var gen = ++_generation;
        if (_identity != identity)
        {
            _identity = identity; _syncing = true;
            try { _iccFile = null; _lastOutcome = null; ColorModeCombo.SelectedIndex = 0; IccPresetCombo.SelectedIndex = 1; ColorPresetCombo.SelectedIndex = 0; }
            finally { _syncing = false; _dirty = false; _vibranceDirty = false; }
        }
        ColorApplyButton.IsEnabled = ColorRestoreButton.IsEnabled = false;
        var states = await Task.Run(() => (Screen: ScreenColorService.GetState(identity), Icc: IccFilterService.GetState(identity)));
        var s = states.Screen;
        if (gen != _generation || _identity != identity || _busy) return;
        ColorStatus.Text = _lastOutcome ?? s.Detail;
        if (!_dirty)
        {
            _syncing = true;
            try
            {
                _iccFile = states.Icc.Restorable ? states.Icc.CurrentProfilePath : null;
                ColorModeCombo.SelectedIndex = _iccFile is null ? 0 : 1;
                if (_iccFile is not null)
                {
                    var name = Path.GetFileName(_iccFile);
                    var known = Enum.GetValues<IccFilterPreset>().Cast<IccFilterPreset?>().FirstOrDefault(p =>
                        string.Equals(name, IccProfileGenerator.FileNameFor(p!.Value), StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, $"FpsTune-{p}.icc", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, $"FpsTune-{p}-v2.icc", StringComparison.OrdinalIgnoreCase));
                    if (known is { } selected)
                    {
                        _iccFile = null; IccPresetCombo.SelectedIndex = IndexOfPreset(selected);
                        if (!string.Equals(name, IccProfileGenerator.FileNameFor(selected), StringComparison.OrdinalIgnoreCase))
                        { _dirty = true; ColorStatus.Text = Str.T("Str.ColorLegacyFilterUpdate"); }
                    }
                    else IccPresetCombo.SelectedIndex = 0;
                }
            }
            finally { _syncing = false; }
            ShowSettings(s.Settings);
        }
        _screenSupported = s.Supported; _iccSupported = states.Icc.CanApply; _iccRestorable = states.Icc.Restorable;
        _restorable = s.Restorable || states.Icc.Restorable;
        Availability();
        // 系统上已经没有我们的 ICC（例如刚还原原始）：列表里那条选中态也不算数了。
        if (_iccFile is null && Preset is null) IccSelectionCleared?.Invoke();
        IsEnabled = true;
    }
    public void PauseForTargetChange() { ++_generation; _identity = null; _dirty = false; IsEnabled = false; }
    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        MarkDraft(); Availability();
        if (ColorPresetCombo.SelectedItem is Choice { Preset: { } preset }) ShowSettings(ScreenColorSettings.ForPreset(preset));
        else UpdatePreview();
    }
    private void IccPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ColorPreview is null) return;
        _iccFile = null; MarkDraft();
        if (Preset is not null) ColorModeCombo.SelectedIndex = 1;
        Availability(); UpdatePreview();
    }
    private void FilterChips_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_busy && _iccSupported && Preset is not null) ColorModeCombo.SelectedIndex = 1;
    }
    private void MarkDraft()
    {
        _dirty = true; _lastOutcome = null;
        ColorStatus.Text = Str.T("Str.ColorPreviewPending");
    }
    private void IccToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncing && ColorModeCombo is not null) ColorModeCombo.SelectedIndex = IccEnabledCheck.IsChecked == true ? 1 : 0;
    }
    private void Workspace_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 常规窗口固定双栏：左侧实拍图不动，右侧唯一控制栏滚动。
        // 极窄窗口才上下堆叠，避免把图片压到无法判断颜色。
        // 右栏宽度按比例给（32%），只夹在 360–460：窗口和全屏下都把宽度让给实拍图。
        var wide = e.NewSize.Width >= 980;
        ControlsColumn.Width = wide ? new GridLength(Math.Clamp(e.NewSize.Width * 0.32, 360, 460)) : new GridLength(0);
        Grid.SetColumn(ControlsPane, wide ? 1 : 0); Grid.SetRow(ControlsPane, wide ? 0 : 1);
        PreviewPane.Margin = new Thickness(0, 0, wide ? 18 : 0, wide ? 0 : 16);
    }
    private void Reset_Click(object sender, RoutedEventArgs e) { ShowSettings(new()); ColorPresetCombo.SelectedIndex = 0; MarkDraft(); }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _identity is null) return;
        var settings = Values(); var identity = _identity;
        var restoreIcc = !IccMode && _iccRestorable;
        if (!DialogService.Confirm(Str.T("Str.ColorApplyTitle"), Str.T("Str.ColorWorkspaceConfirm", IccMode ? Str.T("Str.ColorIccMode") : Str.T(restoreIcc ? "Str.ColorIccOffMode" : "Str.ColorScreenMode")), confirmText: Str.T("Str.ColorApply"))) return;
        var iccMode = IccMode; var preset = Preset; var file = _iccFile; var vibrance = _vibranceDirty ? _vibrance : null;
        var applyScreen = _screenSupported;
        await Run(() => {
            if (Unified) return ColorWorkspaceService.Apply(identity, settings, iccMode, preset, file, vibrance, applyScreen, restoreIcc);
            var rebased = ScreenColorService.Apply(settings, identity, startNewBaselineOnDrift: true);
            return Str.T("Str.ColorApplied") + (rebased ? " " + Str.T("Str.ColorNewBaselineSaved") : "");
        });
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _identity is null) return;
        var identity = _identity;
        await Run(() => Unified ? ColorWorkspaceService.Restore(identity)
            : ScreenColorService.Restore(identity) ? Str.T("Str.ColorRestored") : Str.T("Str.ColorNoBackup"));
    }
    private async Task Run(Func<string> action)
    {
        _busy = true; ++_generation; IsEnabled = false; BusyChanged?.Invoke(true);
        string result;
        try { result = await Task.Run(action); _dirty = false; }
        catch (Exception ex) { result = ex.Message; DialogService.Warning(Str.T("Str.ColorIncomplete"), result); }
        finally { _busy = false; IsEnabled = true; }
        _lastOutcome = result; BusyChanged?.Invoke(false);
        if (_identity is not null) await RefreshFor(_identity);
        ColorStatus.Text = result;
    }
}
