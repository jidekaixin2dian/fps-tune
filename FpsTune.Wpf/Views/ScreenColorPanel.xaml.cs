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
    /// <summary>_iccFile 对应的预设名：内置资产原名（如 1.1·柔和.icc）或用户文件名，写规则时用它而不是临时路径。</summary>
    private string? _iccFilePreset;
    private string? _lastOutcome;
    internal bool Unified { get; set; }
    /// <summary>面板的 ICC 选择被系统状态刷新清空时通知宿主，让滤镜列表同步取消选中。</summary>
    internal event Action? IccSelectionCleared;
    /// <summary>应用目标切到某个程序／方案载入时，把这档数字振动交给宿主页去同步滑块。</summary>
    internal event Action<int?>? VibranceSuggested;
    public object VibranceContent { get => VibranceHost.Content; set => VibranceHost.Content = value; }
    public object IccContent { get => IccHost.Content; set => IccHost.Content = value; }
    public object FilterContent { get => FilterHost.Content; set => FilterHost.Content = value; }
    public object PerAppContent { get => PerAppHost.Content; set => PerAppHost.Content = value; }
    public object AutomationContent { get => AutomationHost.Content; set => AutomationHost.Content = value; }

    /// <summary>当前屏幕方案 / 独立程序滤镜共用左侧实拍图，只切换右侧控制区。</summary>
    internal void SetMode(bool perApp)
    {
        _perAppMode = perApp;
        CurrentModePanel.Visibility = perApp ? Visibility.Collapsed : Visibility.Visible;
        ActionBar.Visibility = perApp ? Visibility.Collapsed : Visibility.Visible;
        PerAppCard.Visibility = perApp ? Visibility.Visible : Visibility.Collapsed;
        // 独立程序滤镜页是「管程序」的页面：1U 在这一页不放截图对比与滤镜列表，我们也不让大图把规则挤到屏幕外。
        PreviewPane.Visibility = perApp ? Visibility.Collapsed : Visibility.Visible;
        PresetPane.Visibility = perApp ? Visibility.Collapsed : Visibility.Visible;
        ApplyColumns(ActualWidth, perApp);
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

    /// <summary>应用栏的「应用到」：桌面＝现在这套屏幕设置，全部游戏＝全自动模板，其余是某条规则的 exe。</summary>
    internal enum ApplyTargetKind { Desktop, AutoTemplate, Rule }

    private sealed record ApplyTargetChoice(ApplyTargetKind Kind, string? RuleKey, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record SchemeChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private string _applyTarget = "desktop";
    private bool _targetSyncing;
    private bool _perAppMode;

    /// <summary>当前应用目标（desktop / auto / rule:&lt;key&gt;），给专项用例核对。</summary>
    internal string ApplyTargetKey => _applyTarget;
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
                // 游戏加加那套：数值在上方居中、滑杆吃满、暗/亮在两端，没有多余的标签和读数。
                text.Visibility = Visibility.Collapsed;
                entry.Visibility = Visibility.Collapsed;
                row.Margin = new Thickness(0);
                slider.Style = (Style)FindResource("TuneSliderStyle");
                System.Windows.Automation.AutomationProperties.SetAutomationId(slider, "ColorDarkBalanceSlider");
                slider.ValueChanged += (_, _) => { DarkBalanceValue.Text = slider.Value.ToString("0", CultureInfo.InvariantCulture); };
                DarkBalanceRowHost.Children.Add(row);
            }
            else ParameterGrid.Children.Add(row);
            _sliders.Add(slider);
        }
        DarkBalanceValue.Text = _sliders[DarkBalanceIndex].Value.ToString("0", CultureInfo.InvariantCulture);
        DarkBalanceRowHost.IsEnabled = false;
        DarkBalanceResetButton.IsEnabled = false;
        ColorPresetCombo.ItemsSource = new[] { new Choice(Str.T("Str.ColorCustom"), null) }
            .Concat(Enum.GetValues<IccFilterPreset>().Select(preset => new Choice(ScreenColorSettings.TitleFor(preset), preset)))
            .ToArray();
        IccPresetCombo.ItemsSource = ((Choice[])ColorPresetCombo.ItemsSource).Select((choice, index) => index == 0 ? new Choice(Str.T("Str.ColorIccFile"), null) : choice).ToArray();
        _syncing = true;
        ColorPresetCombo.SelectedIndex = 0; IccPresetCombo.SelectedIndex = 1;
        _syncing = false;
        ColorApplyButton.IsEnabled = ColorRestoreButton.IsEnabled = false;
        RefreshApplyTargets();
        RefreshSchemes();
        UpdatePreview();
    }
    private async void UpdatePreview()
    {
        if (_sliders.Count != 9) return;
        ColorPreview.SetSettings(Values());
        // 游戏内效果通道：把选中的滤镜曲线一起算进预览，才和写入屏幕的效果一致。
        ColorPreview.SetFilterCurves(CurrentFilterCurves());
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
        try { _iccFile = path; _iccFilePreset = Path.GetFileName(path); ColorModeCombo.SelectedIndex = 1; IccPresetCombo.SelectedIndex = 0; }
        finally { _syncing = false; }
        // 状态行不显示临时文件名（滤镜列表物化出来的缓存路径），文件名在 ICC 高级里单独展示。
        _dirty = true; MarkDraft(); Availability(); UpdatePreview();
    }

    /// <summary>把滤镜列表里选中的内置生成式预设填进本面板（预览与应用都走这一份状态）。</summary>
    internal void SelectGeneratedPreset(IccFilterPreset preset)
    {
        _syncing = true;
        try { _iccFile = null; _iccFilePreset = null; ColorModeCombo.SelectedIndex = 1; IccPresetCombo.SelectedIndex = IndexOfPreset(preset); }
        finally { _syncing = false; }
        // 选中滤镜＝把这套预设的画面参数填进面板（草稿，仍需点「应用」）。
        // 滤镜列表走的是这条路（_syncing 期间不会触发 IccPreset_Changed），必须在这里补一次。
        ShowSettings(ScreenColorSettings.ForPreset(preset));
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
        DarkBalanceToggle.IsEnabled = !_busy && _screenSupported;
        DarkBalanceResetButton.IsEnabled = !_busy && _screenSupported && DarkBalanceToggle.IsChecked == true;
        DarkBalanceRowHost.IsEnabled = !_busy && _screenSupported && DarkBalanceToggle.IsChecked == true;
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
        ApplyTargetCombo.IsEnabled = !_busy;
        SaveSchemeButton.IsEnabled = !_busy;
        var hasSchemes = SchemeCombo is { Items.Count: > 0 };
        if (SchemeCombo is not null) SchemeCombo.IsEnabled = !_busy && hasSchemes;
        LoadSchemeButton.IsEnabled = !_busy && hasSchemes;
        DeleteSchemeButton.IsEnabled = !_busy && hasSchemes && SchemeCombo!.SelectedItem is not null;
    }
    private ScreenColorSettings Values() => new(_sliders[0].Value, _sliders[1].Value, _sliders[2].Value, _sliders[3].Value,
        _sliders[4].Value, _sliders[5].Value, _sliders[6].Value, _sliders[8].Value, _sliders[7].Value,
        DarkBalanceToggle.IsChecked == true);
    /// <summary>当前滤镜要折进屏幕曲线的校准表（vcgt）。</summary>
    private IccCurves? CurrentFilterCurves()
    {
        if (!IccMode) return null;
        try
        {
            // 只有**外部导入的 .icc** 才折：内置预设的 vcgt 与 ForPreset 的画面参数是同一条曲线，
            // 两边都折就等于套两遍（画面过重）。预设的观感由 settings=ForPreset 承担，
            // ICC 文件照样关联给读色彩管理的程序，vcgt 也会被系统当成校准表使用。
            if (_iccFile is not null && File.Exists(_iccFile)) return IccCurveReader.TryRead(File.ReadAllBytes(_iccFile));
            return null;
        }
        catch { return null; }
    }

    /// <summary>开关：关着时这条曲线不参与，滑杆就地禁用（值仍记着）。</summary>
    private void DarkBalance_Toggled(object sender, RoutedEventArgs e)
    {
        if (_sliders.Count <= DarkBalanceIndex || DarkBalanceRowHost is null) return;
        DarkBalanceRowHost.IsEnabled = DarkBalanceToggle.IsChecked == true;
        DarkBalanceResetButton.IsEnabled = DarkBalanceToggle.IsChecked == true;
        if (_syncing) return;
        MarkDraft(); ColorPresetCombo.SelectedIndex = 0; Availability(); UpdatePreview();
    }

    /// <summary>还原初始值：游戏加加默认 50。</summary>
    private void DarkBalance_Reset_Click(object sender, RoutedEventArgs e)
    {
        _sliders[DarkBalanceIndex].Value = ScreenColorService.DefaultDarkBalance;
        DarkBalanceValue.Text = ScreenColorService.DefaultDarkBalance.ToString("0", CultureInfo.InvariantCulture);
        MarkDraft(); ColorPresetCombo.SelectedIndex = 0; UpdatePreview();
    }
    private void ShowSettings(ScreenColorSettings s)
    {
        _syncing = true;
        try
        {
            var values = new[] { s.Brightness, s.Contrast, s.Gamma, s.Shadows, s.Red, s.Green, s.Blue, s.DarkBalance, s.Highlight };
            for (var i = 0; i < values.Length; i++) _sliders[i].Value = values[i];
            DarkBalanceToggle.IsChecked = s.DarkBalanceEnabled;
            DarkBalanceValue.Text = _sliders[DarkBalanceIndex].Value.ToString("0", CultureInfo.InvariantCulture);
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
            try { _iccFile = null; _iccFilePreset = null; _lastOutcome = null; ColorModeCombo.SelectedIndex = 0; IccPresetCombo.SelectedIndex = 1; ColorPresetCombo.SelectedIndex = 0; }
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
                _iccFilePreset = _iccFile is null ? null : Path.GetFileName(_iccFile);
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
                        _iccFile = null; _iccFilePreset = null; IccPresetCombo.SelectedIndex = IndexOfPreset(selected);
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
        RefreshApplyTargets();
        RefreshSchemes();
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
        _iccFile = null; _iccFilePreset = null; MarkDraft();
        if (Preset is not null) ColorModeCombo.SelectedIndex = 1;
        // 选中滤镜＝把这套预设的画面参数填进面板（草稿，仍需点「应用」）。
        // 以前这里什么都不填：滤镜的观感全指望折进 ramp 的 ICC 曲线，结果曲线抵消成恒等，
        // 用户看到的就是「换滤镜没反应、预览都一样」。
        if (Preset is { } preset) ShowSettings(ScreenColorSettings.ForPreset(preset));
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
    private void Workspace_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyColumns(e.NewSize.Width, _perAppMode);

    /// <summary>
    /// 1U 版式：常规窗口左列预设（340–420）、右列「上预览 + 下实时调色 + 底部应用栏」；
    /// 窄窗口（< 980）收起预设列；独立程序滤镜页不用左列，整页给规则。
    /// </summary>
    private void ApplyColumns(double width, bool perApp)
    {
        var wide = !perApp && width >= 980;
        PresetColumn.Width = wide ? new GridLength(Math.Clamp(width * 0.26, 340, 420)) : new GridLength(0);
        Grid.SetColumn(PresetPane, 0);
        Grid.SetColumnSpan(PresetPane, wide ? 1 : 2);
        PresetPane.Margin = new Thickness(0, 0, wide ? 16 : 0, wide ? 0 : 12);
        PresetPane.Height = double.NaN;
    }
    private void Reset_Click(object sender, RoutedEventArgs e) { ShowSettings(new()); ColorPresetCombo.SelectedIndex = 0; MarkDraft(); }

    // ---------- 应用栏：应用到哪儿 / 滤镜方案 ----------

    /// <summary>重建「应用到」下拉：桌面、全部游戏（全自动模板）、以及每条规则进去时的目标程序。</summary>
    internal void RefreshApplyTargets()
    {
        if (ApplyTargetCombo is null) return;
        _targetSyncing = true;
        try
        {
            var items = new List<ApplyTargetChoice>
            {
                new(ApplyTargetKind.Desktop, null, Str.T("Str.ColorApplyTargetDesktop")),
                new(ApplyTargetKind.AutoTemplate, null, Str.T("Str.ColorApplyTargetAuto")),
            };
            items.AddRange(IccAppRuleStore.Rules.Select(rule =>
                new ApplyTargetChoice(ApplyTargetKind.Rule, rule.Key, Str.T("Str.ColorApplyTargetRule", ExeOf(rule)))));
            ApplyTargetCombo.ItemsSource = items;
            var current = items.FirstOrDefault(item => TargetKey(item).Equals(_applyTarget, StringComparison.Ordinal)) ?? items[0];
            ApplyTargetCombo.SelectedItem = current;
            _applyTarget = TargetKey(current);
        }
        finally { _targetSyncing = false; }
    }

    /// <summary>独立程序滤镜页的「编辑调色」：把应用目标切到这条规则，并把它的滤镜与参数填进面板。</summary>
    internal void SetApplyTarget(string ruleKey)
    {
        _applyTarget = "rule:" + ruleKey;
        RefreshApplyTargets();
        if (IccAppRuleStore.Rules.FirstOrDefault(r => r.Key == ruleKey) is { } rule) ShowRuleDraft(rule);
    }

    private static string TargetKey(ApplyTargetChoice choice) => choice.Kind switch
    {
        ApplyTargetKind.AutoTemplate => "auto",
        ApplyTargetKind.Rule => "rule:" + choice.RuleKey,
        _ => "desktop",
    };

    private static string ExeOf(FilterAppRule rule)
        => rule.ExePath.Length > 0 ? Path.GetFileName(rule.ExePath) : rule.ProcessName;

    private void ApplyTarget_DropDownOpened(object sender, EventArgs e) => RefreshApplyTargets();

    private void ApplyTarget_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_targetSyncing || ApplyTargetCombo.SelectedItem is not ApplyTargetChoice choice) return;
        _applyTarget = TargetKey(choice);
        if (choice is { Kind: ApplyTargetKind.Rule, RuleKey: { } key }
            && IccAppRuleStore.Rules.FirstOrDefault(r => r.Key == key) is { } rule)
        {
            ShowRuleDraft(rule);
            ColorStatus.Text = Str.T("Str.ColorApplyTargetRuleHint", ExeOf(rule));
        }
        else if (choice.Kind == ApplyTargetKind.AutoTemplate)
            ColorStatus.Text = Str.T("Str.ColorApplyTargetAutoHint");
        else ColorStatus.Text = "";
    }

    /// <summary>把某条规则的内容填进面板（切换应用目标 / 编辑调色）：全是草稿，仍要点「应用整套」。</summary>
    private void ShowRuleDraft(FilterAppRule rule)
    {
        IccFilterPreset? preset = null;
        _syncing = true;
        try
        {
            if (rule.PresetFileName.Length == 0)
            {
                _iccFile = null; ColorModeCombo.SelectedIndex = 0; IccPresetCombo.SelectedIndex = 0;
            }
            else if (TryParseGenerated(rule.PresetFileName, out var parsed))
            {
                _iccFile = null; preset = parsed; ColorModeCombo.SelectedIndex = 1; IccPresetCombo.SelectedIndex = IndexOfPreset(parsed);
            }
            else
            {
                // 规则里的滤镜可能是内置资产（如 1.1·柔和.icc）或用户目录里的 .icc：两种都要能在面板里显示出来。
                _iccFile = UserPresetPath(rule.PresetFileName) ?? MaterializePreset(rule.PresetFileName);
                _iccFilePreset = rule.PresetFileName;
                ColorModeCombo.SelectedIndex = 1; IccPresetCombo.SelectedIndex = 0;
            }
        }
        finally { _syncing = false; }
        if (rule.Screen is { IsValid: true } screen) ShowSettings(screen.ToSettings());
        else if (preset is { } selected) ShowSettings(ScreenColorSettings.ForPreset(selected));
        _vibranceDirty = rule.Vibrance is not null;
        _vibrance = rule.Vibrance;
        VibranceSuggested?.Invoke(rule.Vibrance);
        _dirty = true; MarkDraft(); Availability(); UpdatePreview();
    }

    private static bool TryParseGenerated(string presetFileName, out IccFilterPreset preset)
    {
        preset = default;
        return presetFileName.StartsWith(GameFilterLibrary.GeneratedPrefix, StringComparison.Ordinal)
               && Enum.TryParse(presetFileName[GameFilterLibrary.GeneratedPrefix.Length..], ignoreCase: false, out preset);
    }

    private static string? UserPresetPath(string fileName)
    {
        if (fileName.Length == 0) return null;
        var path = Path.Combine(GameFilterLibrary.UserDir, Path.GetFileName(fileName));
        return File.Exists(path) ? path : null;
    }

    /// <summary>内置资产 / 用户目录里的预设物化成临时 .icc，面板的预览与应用都按文件走。</summary>
    private static string? MaterializePreset(string fileName)
    {
        if (fileName.Length == 0) return null;
        try { return ScreenColorPreview.CacheIccProfile(GameFilterLibrary.ReadPreset(fileName)); }
        catch { return null; }
    }

    /// <summary>
    /// 当前滤镜写成规则能存的名字：生成式用 generated:枚举；内置预设/用户 .icc 直接记原名；
    /// 只有真正的外部文件才拷进滤镜目录后记文件名。临时物化的路径绝不能写进规则（重启就没了）。
    /// </summary>
    private string SelectedPresetName()
    {
        if (Preset is { } preset) return GameFilterLibrary.GeneratedPrefix + preset.ToString();
        if (_iccFilePreset is { Length: > 0 } known
            && (GameFilterLibrary.Presets.Any(p => p.FileName.Equals(known, StringComparison.OrdinalIgnoreCase))
                || UserPresetPath(known) is not null)) return known;
        if (_iccFile is { Length: > 0 } file && File.Exists(file))
        {
            try { return GameFilterLibrary.SaveUserPreset(Path.GetFileName(file), File.ReadAllBytes(file)); }
            catch { return ""; }
        }
        return "";
    }

    private int? DraftVibrance()
        => _vibranceDirty ? _vibrance : Preset is { } preset ? IccProfileGenerator.VibranceFor(preset) : null;

    private bool DraftIncludeScreen(ScreenColorSettings settings)
        => Preset is not null || settings != new ScreenColorSettings();

    private void ApplyToTarget(ApplyTargetChoice choice)
    {
        var presetName = SelectedPresetName();
        var settings = Values();
        var vibrance = DraftVibrance();
        var snapshot = AppRuleScreen.FromSettings(settings);
        var includeScreen = DraftIncludeScreen(settings);
        if (presetName.Length == 0 && vibrance is null && !includeScreen)
        { ColorStatus.Text = Str.T("Str.FilterNeedPresetFirst"); return; }
        try
        {
            if (choice.Kind == ApplyTargetKind.AutoTemplate) { SaveAutoTemplate(presetName, vibrance, includeScreen, snapshot); return; }
            var rule = IccAppRuleStore.Rules.FirstOrDefault(r => r.Key == choice.RuleKey);
            if (rule is null) { RefreshApplyTargets(); return; }
            // 「自动」规则跟着全自动模板走（徽章不许说谎）：改它就等于改模板。
            if (rule.Auto) { SaveAutoTemplate(presetName, vibrance, includeScreen, snapshot); return; }
            IccAppRuleStore.Save(IccAppRuleStore.Rules.Select(r => r.Key == rule.Key
                ? r with { PresetFileName = presetName, Vibrance = vibrance, IncludeScreen = includeScreen, Screen = snapshot }
                : r).ToList());
            if (presetName.Length > 0) FilterPreferenceStore.NoteUsed(presetName);
            ColorStatus.Text = Str.T("Str.FilterRuleApplied", rule.ProcessName);
            RefreshApplyTargets();
            App.FilterWatcher?.ApplySettings();
        }
        catch (Exception ex) { ColorStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }

    /// <summary>把当前这套写成全自动模板，并让已收编的规则立刻跟着变。</summary>
    private void SaveAutoTemplate(string presetName, int? vibrance, bool includeScreen, AppRuleScreen snapshot)
    {
        var template = FilterSchemeStore.SaveAutoTemplate(presetName, vibrance, includeScreen, snapshot);
        var updated = PerAppGameCollector.ApplyTemplate(template);
        ColorStatus.Text = Str.T("Str.FilterAutoTemplateSaved", updated);
        RefreshApplyTargets();
        App.FilterWatcher?.ApplySettings();
    }

    /// <summary>重建「方案」下拉：存下来的滤镜方案（预设 + 振动 + 画面参数）。</summary>
    internal void RefreshSchemes()
    {
        if (SchemeCombo is null) return;
        var schemes = FilterSchemeStore.Schemes;
        var currentId = (SchemeCombo.SelectedItem as SchemeChoice)?.Id;
        SchemeCombo.ItemsSource = schemes.Select(s => new SchemeChoice(s.Id, s.Name)).ToList();
        SchemeCombo.SelectedItem = SchemeCombo.Items.OfType<SchemeChoice>().FirstOrDefault(c => c.Id == currentId)
            ?? (SchemeCombo.Items.Count > 0 ? SchemeCombo.Items[0] : null);
        var has = schemes.Count > 0;
        SchemeCombo.IsEnabled = !_busy && has;
        LoadSchemeButton.IsEnabled = !_busy && has;
        DeleteSchemeButton.IsEnabled = !_busy && has && SchemeCombo.SelectedItem is not null;
    }

    private string SchemeDraftName()
    {
        if (Preset is { } preset) return ScreenColorSettings.TitleFor(preset);
        if (_iccFilePreset is { Length: > 0 } known) return Path.GetFileNameWithoutExtension(known);
        if (_iccFile is { Length: > 0 } file) return Path.GetFileNameWithoutExtension(file);
        return Str.T("Str.FilterSchemeCustomName");
    }

    private void SaveScheme_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var presetName = SelectedPresetName();
        var settings = Values();
        if (presetName.Length == 0 && !DraftIncludeScreen(settings))
        { ColorStatus.Text = Str.T("Str.FilterNeedPresetFirst"); return; }
        try
        {
            var scheme = FilterSchemeStore.Save(SchemeDraftName(), presetName, DraftVibrance(),
                DraftIncludeScreen(settings), AppRuleScreen.FromSettings(settings));
            RefreshSchemes();
            SchemeCombo.SelectedItem = SchemeCombo.Items.OfType<SchemeChoice>().FirstOrDefault(c => c.Id == scheme.Id);
            ColorStatus.Text = Str.T("Str.FilterSchemeSaved", scheme.Name);
        }
        catch (Exception ex) { ColorStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }

    private void LoadScheme_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || SchemeCombo.SelectedItem is not SchemeChoice choice) return;
        if (FilterSchemeStore.Find(choice.Id) is not { } scheme) { RefreshSchemes(); return; }
        IccFilterPreset? preset = null;
        _syncing = true;
        try
        {
            if (scheme.PresetFileName.Length == 0)
            {
                _iccFile = null; ColorModeCombo.SelectedIndex = 0; IccPresetCombo.SelectedIndex = 0;
            }
            else if (TryParseGenerated(scheme.PresetFileName, out var parsed))
            {
                _iccFile = null; preset = parsed; ColorModeCombo.SelectedIndex = 1; IccPresetCombo.SelectedIndex = IndexOfPreset(parsed);
            }
            else
            {
                _iccFile = UserPresetPath(scheme.PresetFileName) ?? MaterializePreset(scheme.PresetFileName);
                _iccFilePreset = scheme.PresetFileName;
                ColorModeCombo.SelectedIndex = 1; IccPresetCombo.SelectedIndex = 0;
            }
        }
        finally { _syncing = false; }
        if (scheme.Screen is { IsValid: true } screen) ShowSettings(screen.ToSettings());
        else if (preset is { } selected) ShowSettings(ScreenColorSettings.ForPreset(selected));
        _vibranceDirty = scheme.Vibrance is not null;
        _vibrance = scheme.Vibrance;
        VibranceSuggested?.Invoke(scheme.Vibrance);
        _dirty = true; MarkDraft(); Availability(); UpdatePreview();
        ColorStatus.Text = Str.T("Str.FilterSchemeLoaded", scheme.Name);
    }

    private void DeleteScheme_Click(object sender, RoutedEventArgs e)
    {
        if (SchemeCombo.SelectedItem is not SchemeChoice choice) return;
        if (!DialogService.Confirm(Str.T("Str.FilterSchemeDeleteTitle"), Str.T("Str.FilterSchemeDeleteConfirm", choice.Label), danger: true)) return;
        FilterSchemeStore.Remove(choice.Id);
        RefreshSchemes();
        ColorStatus.Text = Str.T("Str.FilterSchemeDeleted", choice.Label);
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        // 应用到某个程序 / 全部游戏时写的是规则与模板，跟当前屏幕无关，不需要显示器身份。
        if (ApplyTargetCombo.SelectedItem is ApplyTargetChoice { Kind: not ApplyTargetKind.Desktop } target)
        {
            ApplyToTarget(target);
            return;
        }
        if (_identity is null) return;
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
