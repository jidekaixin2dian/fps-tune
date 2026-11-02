using System.IO;
using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class FilterView : UserControl
{
    // 目标显示器 → 滤镜（统一列表）→ 画面与色彩（含实机截图对比）→ 掉帧修复
    private bool _busy;
    private string? _vibStatus;
    private string? _iccStatus;
    private string? _iccBackupNotice;
    private string? _displayIdentity;
    private bool _displaySyncing;
    private bool _vibSyncing;
    private int _refreshGen;
    private bool _fullscreenSyncing;

    /// <summary>全屏方案的方案下拉条目（文案来自 ScreenColorSettings.TitleFor，与 ICC 预设共用同一套标题）。</summary>
    private sealed record FullscreenChoice(IccFilterPreset Preset, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>生效方式条目：gamma = 游戏内生效，icc = 仅色彩管理应用。</summary>
    private sealed record FullscreenChannelChoice(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record DisplayChoice(string Identity, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed class IccChoice(IccFilterPreset? preset, string titleKey, string descriptionKey, bool selected = false)
    {
        public IccFilterPreset? Preset { get; } = preset;
        public string TitleKey { get; } = titleKey;
        public string DescriptionKey { get; } = descriptionKey;
        public bool IsSelected { get; set; } = selected;
    }

    public FilterView()
    {
        InitializeComponent();
        // Preserve the page namescope and handlers while hosting all color controls together.
        ((Panel)VibranceContent.Parent).Children.Remove(VibranceContent);
        ((Panel)IccAdvanced.Parent).Children.Remove(IccAdvanced);
        ((Panel)SectionAutomation.Parent).Children.Remove(SectionAutomation);
        ((Panel)FilterCenter.Parent).Children.Remove(FilterCenter);
        ((Panel)PerAppCenter.Parent).Children.Remove(PerAppCenter);
        ColorPanel.VibranceContent = VibranceContent;
        ColorPanel.IccContent = IccAdvanced;
        ColorPanel.AutomationContent = SectionAutomation;
        ColorPanel.FilterContent = FilterCenter;
        ColorPanel.PerAppContent = PerAppCenter;
        ColorPanel.Unified = true;
        ColorPanel.SetMode(false);
        ColorPanel.ShowPreviewForProcess(AppState.GamePath);
        PerAppCenter.PreviewRequested += process => ColorPanel.ShowPreviewForProcess(process);
        // 滤镜选择已统一到上面的滤镜列表，色彩面板不再重复一套筛选入口。
        ColorPanel.SetFilterPickerVisible(false);
        ColorPanel.BusyChanged += busy => { _busy = busy; ++_refreshGen; SetDisplayInputEnabled(!busy); if (!busy) Refresh(); };
        FilterCenter.ChoiceChanged += OnFilterChoiceChanged;
        var choices = Enum.GetValues<IccFilterPreset>().Select(preset =>
        {
            var keys = IccProfileGenerator.ResourceKeysFor(preset);
            return new IccChoice(preset, keys.Title, keys.Description, preset == IccFilterPreset.Vivid);
        }).ToList();
        choices.Add(new(null, "Str.IccStandard", "Str.IccStandardDesc"));
        IccPresetList.ItemsSource = choices;
        FullscreenPresetCombo.ItemsSource = Enum.GetValues<IccFilterPreset>()
            .Select(preset => new FullscreenChoice(preset, ScreenColorSettings.TitleFor(preset))).ToList();
        FullscreenChannelCombo.ItemsSource = new List<FullscreenChannelChoice>
        {
            new(FullscreenPresetWatcher.ChannelGamma, Str.T("Str.FullscreenChannelGamma")),
            new(FullscreenPresetWatcher.ChannelIcc, Str.T("Str.FullscreenChannelIcc")),
        };
        LoadFullscreenSettings();
        Loaded += (_, _) => { Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaysChanged; LoadFullscreenSettings(); Refresh(); };
        Unloaded += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
    }

    /// <summary>滤镜列表选中后，把这条滤镜填进色彩面板：生成式给枚举，.icc 先物化成临时文件。</summary>
    private async void OnFilterChoiceChanged(FilterChoice choice)
    {
        try
        {
            if (choice.Generated is { } preset)
            {
                ColorPanel.SelectGeneratedPreset(preset);
                SuggestVibrance(choice.Vibrance ?? IccProfileGenerator.VibranceFor(preset));
                return;
            }
            if (await FilterCenter.MaterializeAsync(choice) is { } path)
            {
                ColorPanel.SelectIccFile(path);
                SuggestVibrance(choice.Vibrance);
            }
        }
        catch (Exception ex) { FilterCenter.ReportStatus(PrivacyScrub.Sanitize(ex.Message)); }
    }

    /// <summary>
    /// 预设自带数字振动档（1U 同款数据）：只把滑块与百分比预填成草稿，不写系统，
    /// 仍要点「应用」；该屏没有 DVC 通道时什么都不动。
    /// </summary>
    private void SuggestVibrance(IccFilterPreset preset) => SuggestVibrance(IccProfileGenerator.VibranceFor(preset));

    /// <summary>
    /// 预设自带数字振动档（1U 同款数据）：只把滑块与百分比预填成草稿，不写系统，
    /// 仍要点「应用」；该屏没有 DVC 通道时什么都不动。
    /// </summary>
    private void SuggestVibrance(int? suggestion)
    {
        if (VibSupportedPanel is null || VibSupportedPanel.Visibility != Visibility.Visible) return;
        if (suggestion is not { } percent) return;
        var value = Math.Clamp(percent, 0, 100);
        _vibSyncing = true;
        VibSlider.Value = value;
        VibPercentText.Text = value + "%";
        _vibSyncing = false;
        ColorPanel.SetVibrance(value, VibRestoreButton?.IsEnabled == true, draft: true);
        _vibStatus = Str.T("Str.FilterVibranceSuggested", value);
        VibStateText.Text = _vibStatus;
    }

    private void OpenDeltaFix_Click(object sender, RoutedEventArgs e) => OpenDeltaFix();
    internal void OpenDeltaFix()
    {
        var dialog = new DeltaFrameFixWindow { Owner = Window.GetWindow(this) };
        DialogService.PrepareWindowForTest?.Invoke(dialog);
        dialog.ShowDialog();
    }

    /// <summary>命令面板跳「画面与色彩」时回到当前屏幕方案顶部。</summary>
    internal void RevealColor()
    {
        ModeScreen.IsChecked = true;
        ColorPanel.SetMode(false);
        ColorPanel.FocusWorkspace();
    }

    /// <summary>整页只有两种应用范围；滤镜、参数和自定义 ICC 始终在「当前屏幕」下。</summary>
    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (ColorPanel is null || ModeScreen is null) return;
        var perApp = ModePerApp.IsChecked == true;
        ColorPanel.SetMode(perApp);
        if (perApp) PerAppCenter.ActivateSelection();
        else ColorPanel.ShowPreviewForProcess(AppState.GamePath);
    }

    // ---------- 目标显示器 ----------

    private void DisplaysChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(Refresh));
    private void DisplayRefresh_Click(object sender, RoutedEventArgs e) => Refresh();
    private void DisplayTarget_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_displaySyncing || _busy || DisplayTargetCombo.SelectedItem is not DisplayChoice choice) return;
        _displayIdentity = choice.Identity;
        ColorPanel.PauseForTargetChange();
        _vibStatus = _iccStatus = null;
        _refreshGen++;
        Refresh();
    }

    /// <summary>一次状态刷新的后台读取结果：色彩面板与数字振动都只读当前目标显示器。</summary>
    private sealed record ColorSnapshot(VibranceState? Vib, string? VibError, IccFilterService.IccState? Icc, string? IccError);

    private static ColorSnapshot ComputeSnapshot(string displayIdentity)
    {
        var vib = default(VibranceState);
        var vibError = default(string);
        try { vib = DigitalVibranceService.GetState(displayIdentity); }
        catch (Exception ex) { vibError = ex.Message; }

        var icc = default(IccFilterService.IccState);
        var iccError = default(string);
        try { icc = IccFilterService.GetState(displayIdentity); }
        catch (Exception ex) { iccError = ex.Message; }

        return new ColorSnapshot(vib, vibError, icc, iccError);
    }

    private void Refresh()
    {
        if (_busy) return;
        var gen = ++_refreshGen;
        _ = RefreshStatesAsync(gen);
    }

    private async Task RefreshStatesAsync(int gen)
    {
        ColorPanel.IsEnabled = false;
        DisplayTargetCombo.IsEnabled = DisplayRefreshButton.IsEnabled = false;
        VibApplyButton.IsEnabled = VibRestoreButton.IsEnabled = false;
        IccApplyButton.IsEnabled = IccApplyFileButton.IsEnabled = IccRestoreButton.IsEnabled = false;
        IReadOnlyList<IccDisplayInfo> displays;
        string? displayError = null;
        try { displays = await Task.Run(DisplayTopology.GetDisplays); }
        catch (Exception ex) { displays = []; displayError = ex.Message; }
        if (_busy || gen != _refreshGen) return;
        _displayIdentity ??= displays.FirstOrDefault(d => d.IsPrimary)?.Identity;
        _displaySyncing = true;
        var choices = displays.Select(d => new DisplayChoice(DisplayTopology.Identity(d),
            $"{(string.IsNullOrWhiteSpace(d.FriendlyName) ? d.GdiDeviceName : d.FriendlyName)} ({d.GdiDeviceName.Replace(@"\\.\", "")})"
            + (d.IsPrimary ? " · " + Str.T("Str.DisplayPrimary") : ""))).ToList();
        DisplayTargetCombo.ItemsSource = choices;
        DisplayTargetCombo.SelectedItem = choices.FirstOrDefault(c => c.Identity == _displayIdentity);
        _displaySyncing = false;
        SetDisplayInputEnabled(true);
        var selected = displays.FirstOrDefault(d => DisplayTopology.Identity(d) == _displayIdentity);
        DisplayTargetState.Text = displayError ?? Str.T(selected is null ? "Str.DisplayDisconnected" : !selected.Independent
            ? "Str.DisplayMirrored" : "Str.DisplayTargetHint");
        var identity = _displayIdentity ?? "unavailable-display"; // Never silently fall back when the selection is missing.
        var snap = await Task.Run(() => ComputeSnapshot(identity));
        if (_busy || gen != _refreshGen)
            return; // 期间发生了更新的刷新，旧结果作废
        ApplyVibState(snap);
        ApplyIccState(snap);
        UpdateFullscreenStatus();
        await ColorPanel.RefreshFor(identity);
    }

    // ---------- 全屏自动切换（与独立程序滤镜互斥，全屏方案优先） ----------

    private void LoadFullscreenSettings()
    {
        _fullscreenSyncing = true;
        try
        {
            var settings = SettingsService.Current;
            FullscreenCheck.IsChecked = settings.FullscreenPresetEnabled;
            FullscreenGamesOnlyCheck.IsChecked = settings.FullscreenPresetOnlyForGames;
            var channel = FullscreenPresetWatcher.NormalizeChannel(settings.FullscreenPresetChannel);
            FullscreenChannelCombo.SelectedItem = FullscreenChannelCombo.Items.OfType<FullscreenChannelChoice>()
                .FirstOrDefault(choice => choice.Key == channel);
            // 还没选过方案时展示第一项，但只有用户改动或开启开关才写入设置。
            var preset = FullscreenPresetWatcher.ParsePreset(settings.FullscreenPresetName) ?? IccFilterPreset.Vivid;
            FullscreenPresetCombo.SelectedItem = FullscreenPresetCombo.Items.OfType<FullscreenChoice>()
                .FirstOrDefault(choice => choice.Preset == preset);
        }
        finally
        {
            _fullscreenSyncing = false;
        }
        UpdateFullscreenStatus();
    }

    private void UpdateFullscreenStatus()
    {
        var status = App.FullscreenPreset?.Status;
        FullscreenStatus.Text = status is { Enabled: true, ActivePreset: { } preset }
            ? Str.T("Str.FullscreenStatusActive", preset, status.DisplayName ?? "—")
            : Str.T("Str.FullscreenStatusIdle");
    }

    /// <summary>设置落盘并让两个守护立刻重估；失败时回滚到改动前的值并刷新界面。</summary>
    private void PersistFullscreen(Action rollback)
    {
        try
        {
            SettingsService.Save(SettingsService.Current);
            App.FullscreenPreset?.ApplySettings();
            // 互斥关系：全屏方案开关一变，独立程序滤镜守护也得立刻重新判断要不要暂停。
            App.FilterWatcher?.ApplySettings();
            UpdateFullscreenStatus();
        }
        catch (Exception ex)
        {
            rollback();
            _fullscreenSyncing = true;
            try { LoadFullscreenSettingsCore(); }
            finally { _fullscreenSyncing = false; }
            FullscreenStatus.Text = PrivacyScrub.Sanitize(ex.Message);
        }
    }

    private void LoadFullscreenSettingsCore()
    {
        var settings = SettingsService.Current;
        FullscreenCheck.IsChecked = settings.FullscreenPresetEnabled;
        FullscreenGamesOnlyCheck.IsChecked = settings.FullscreenPresetOnlyForGames;
        var channel = FullscreenPresetWatcher.NormalizeChannel(settings.FullscreenPresetChannel);
        FullscreenChannelCombo.SelectedItem = FullscreenChannelCombo.Items.OfType<FullscreenChannelChoice>()
            .FirstOrDefault(choice => choice.Key == channel);
        var preset = FullscreenPresetWatcher.ParsePreset(settings.FullscreenPresetName) ?? IccFilterPreset.Vivid;
        FullscreenPresetCombo.SelectedItem = FullscreenPresetCombo.Items.OfType<FullscreenChoice>()
            .FirstOrDefault(choice => choice.Preset == preset);
        UpdateFullscreenStatus();
    }

    private void Fullscreen_Changed(object sender, RoutedEventArgs e)
    {
        if (_fullscreenSyncing || FullscreenCheck.IsChecked is null) return;
        var settings = SettingsService.Current;
        var old = settings.FullscreenPresetEnabled;
        var enabled = FullscreenCheck.IsChecked == true;
        settings.FullscreenPresetEnabled = enabled;
        // 开启时给一个确定方案，避免守护因为没选方案而空转。
        if (enabled && FullscreenPresetWatcher.ParsePreset(settings.FullscreenPresetName) is null
            && FullscreenPresetCombo.SelectedItem is FullscreenChoice fallback)
            settings.FullscreenPresetName = fallback.Preset.ToString();
        PersistFullscreen(() => settings.FullscreenPresetEnabled = old);
    }

    private void FullscreenPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_fullscreenSyncing || FullscreenPresetCombo.SelectedItem is not FullscreenChoice choice) return;
        var settings = SettingsService.Current;
        var old = settings.FullscreenPresetName;
        settings.FullscreenPresetName = choice.Preset.ToString();
        PersistFullscreen(() => settings.FullscreenPresetName = old);
    }

    private void FullscreenChannel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_fullscreenSyncing || FullscreenChannelCombo.SelectedItem is not FullscreenChannelChoice choice) return;
        var settings = SettingsService.Current;
        var old = settings.FullscreenPresetChannel;
        settings.FullscreenPresetChannel = choice.Key;
        PersistFullscreen(() => settings.FullscreenPresetChannel = old);
    }

    private void FullscreenGamesOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (_fullscreenSyncing || FullscreenGamesOnlyCheck.IsChecked is null) return;
        var settings = SettingsService.Current;
        var old = settings.FullscreenPresetOnlyForGames;
        settings.FullscreenPresetOnlyForGames = FullscreenGamesOnlyCheck.IsChecked == true;
        PersistFullscreen(() => settings.FullscreenPresetOnlyForGames = old);
    }

    // ---------- 数字振动（显示级，全桌面） ----------

    private void ApplyVibState(ColorSnapshot s)
    {
        if (s.VibError is not null)
        {
            ColorPanel.SetVibrance(null);
            VibSupportedPanel.Visibility = Visibility.Collapsed;
            VibUnsupportedText.Visibility = Visibility.Visible;
            VibUnsupportedText.Text = Str.T("Str.VibStateReadFailed", s.VibError);
            return;
        }

        var state = s.Vib!;
        if (!state.Supported)
        {
            VibSupportedPanel.Visibility = Visibility.Collapsed;
            ColorPanel.SetVibrance(null);
            VibUnsupportedText.Visibility = Visibility.Visible;
            VibUnsupportedText.Text = state.UnsupportedReason ?? Str.T("Str.VibranceUnavailable");
            return;
        }

        VibSupportedPanel.Visibility = Visibility.Visible;
        VibUnsupportedText.Visibility = Visibility.Collapsed;

        var percent = state.Max > state.Min
            ? (int)Math.Round((state.Current - state.Min) * 100.0 / (state.Max - state.Min))
            : 0;
        _vibSyncing = true;
        VibSlider.Value = percent;
        VibPercentText.Text = percent + "%";
        _vibSyncing = false;

        VibApplyButton.IsEnabled = true;
        ColorPanel.SetVibrance(percent, state.Restorable);
        VibRestoreButton.IsEnabled = state.Restorable;
        VibStateText.Text = _vibStatus ?? (state.Restorable
            ? Str.T("Str.VibStateCurrentRestorable", percent, PercentOf(state.Default, state))
            : Str.T("Str.VibStateCurrent", percent, PercentOf(state.Default, state)));
        if (_vibStatus is null && state.BackupNotice is not null) VibStateText.Text += "\n" + state.BackupNotice;
    }

    private static int PercentOf(int level, VibranceState state)
        => state.Max > state.Min
            ? (int)Math.Round((level - state.Min) * 100.0 / (state.Max - state.Min))
            : 0;

    private void VibSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_vibSyncing) return;
        VibPercentText.Text = (int)VibSlider.Value + "%";
        ColorPanel?.SetVibrance((int)VibSlider.Value, VibRestoreButton?.IsEnabled == true, draft: true);
    }

    /// <summary>
    /// 「推荐」快捷置值：社区竞技取向区间 55–60 的中点（57.5 向下取整）。
    /// 只移动滑块与百分比显示，**不直接写入** —— 仍须点「应用」走确认对话框才生效。
    /// </summary>
    private const int RecommendedVibrancePercent = 57;

    private void VibRecommended_Click(object sender, RoutedEventArgs e)
    {
        _vibSyncing = true;
        VibSlider.Value = RecommendedVibrancePercent;
        VibPercentText.Text = RecommendedVibrancePercent + "%";
        ColorPanel.SetVibrance(RecommendedVibrancePercent, VibRestoreButton.IsEnabled, draft: true);
        _vibSyncing = false;
    }

    private async void VibApply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var percent = (int)VibSlider.Value;
        var identity = _displayIdentity ?? "unavailable-display";
        var confirmed = DialogService.Confirm(
            Str.T("Str.ApplyVibrance"),
            Str.T("Str.DisplayVibConfirm", DisplayTargetCombo.Text, percent),
            confirmText: Str.T("Str.Apply"));
        if (!confirmed) return;

        _busy = true;
        SetDisplayInputEnabled(false);
        _vibStatus = null;
        VibApplyButton.IsEnabled = false;
        VibRestoreButton.IsEnabled = false;
        VibStateText.Text = Str.T("Str.Writing");
        try
        {
            var rebased = await Task.Run(() => DigitalVibranceService.SetPercent(percent, identity, startNewBaselineOnDrift: true));
            _vibStatus = Str.T("Str.DisplayVibApplied", percent) + (rebased ? " " + Str.T("Str.VibranceNewBaselineSaved") : "");
        }
        catch (Exception ex)
        {
            _vibStatus = Str.T("Str.DisplayApplyFailed", ex.Message);
            DialogService.Warning(Str.T("Str.OperationFailed"), _vibStatus);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private async void VibRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var identity = _displayIdentity ?? "unavailable-display";
        _busy = true;
        SetDisplayInputEnabled(false);
        _vibStatus = null;
        VibApplyButton.IsEnabled = false;
        VibRestoreButton.IsEnabled = false;
        VibStateText.Text = Str.T("Str.Restoring2");
        try
        {
            var restored = await Task.Run(() => DigitalVibranceService.Restore(identity));
            _vibStatus = restored ? Str.T("Str.VibranceRestored") : Str.T("Str.NothingToRestore2");
        }
        catch (Exception ex)
        {
            _vibStatus = Str.T("Str.DisplayRestoreFailed", ex.Message);
            DialogService.Warning(Str.T("Str.OperationFailed"), _vibStatus);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    // ---------- ICC 高级（独立于 DLSS 的可用性） ----------

    private void ApplyIccState(ColorSnapshot s)
    {
        if (s.IccError is not null)
        {
            IccSupportedPanel.Visibility = Visibility.Collapsed;
            IccUnsupportedText.Visibility = Visibility.Visible;
            IccUnsupportedText.Text = Str.T("Str.IccStateReadFailed", s.IccError);
            return;
        }

        var state = s.Icc!;
        _iccBackupNotice = state.BackupNotice;
        if (!state.Supported)
        {
            IccSupportedPanel.Visibility = Visibility.Collapsed;
            IccUnsupportedText.Visibility = Visibility.Visible;
            IccUnsupportedText.Text = state.UnsupportedReason ?? Str.T("Str.IccUnavailable");
            return;
        }

        IccSupportedPanel.Visibility = Visibility.Visible;
        IccUnsupportedText.Visibility = Visibility.Collapsed;
        IccCurrentText.Text = Str.T("Str.DisplayCurrentIccAssociation") + (state.CurrentProfileName ?? Str.T("Str.DisplayNoCustomIcc"));

        SetIccPresetCardsEnabled(true);
        IccApplyButton.IsEnabled = IccApplyFileButton.IsEnabled = state.CanApply;
        IccRestoreButton.IsEnabled = state.Restorable;

        IccStateText.Text = _iccStatus ?? (state.Restorable ? Str.T("Str.IccOriginalSaved") : "");
        if (_iccStatus is null && state.BackupNotice is not null) IccStateText.Text = state.BackupNotice;
    }

    private void SetIccPresetCardsEnabled(bool enabled) => IccPresetList.IsEnabled = enabled;

    private IccFilterPreset? SelectedIccPreset()
        => IccPresetList.Items.OfType<IccChoice>().SingleOrDefault(choice => choice.IsSelected)?.Preset;

    private async void IccApply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var preset = SelectedIccPreset();
        if (preset is null)
        {
            // 标准 = 还原原始
            await IccRestoreCore();
            return;
        }
        var identity = _displayIdentity ?? "unavailable-display";
        await IccApplyCore(() => IccFilterService.Apply(preset.Value, identity, startNewBaselineOnDrift: true));
    }

    /// <summary>用户自选 .icc / .icm 文件。与预设共用确认与应用流程。</summary>
    private void IccApplyFromFile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Str.T("Str.IccApplyFromFile"),
            Filter = Str.T("Str.IccFileFilter")
        };
        if (dialog.ShowDialog() != true) return;
        ColorPanel.SelectIccFile(dialog.FileName);
    }

    /// <summary>预设与用户文件共用的应用流程：确认 → 写入 → 状态反馈（同一套全局生效警示）。</summary>
    private async Task IccApplyCore(Func<string> apply)
    {
        var confirmed = DialogService.Confirm(
            Str.T("Str.ApplyIcc"),
            Str.T("Str.DisplayIccConfirm", DisplayTargetCombo.Text) + (_iccBackupNotice is null ? "" : "\n\n" + _iccBackupNotice),
            confirmText: Str.T("Str.Apply"));
        if (!confirmed) return;

        _busy = true;
        SetDisplayInputEnabled(false);
        _iccStatus = null;
        IccApplyButton.IsEnabled = false;
        IccApplyFileButton.IsEnabled = false;
        IccRestoreButton.IsEnabled = false;
        IccStateText.Text = Str.T("Str.ApplyingPreset");
        try
        {
            var name = await Task.Run(apply);
            _iccStatus = Str.T("Str.IccApplied", Path.GetFileName(name));
        }
        catch (Exception ex)
        {
            _iccStatus = Str.T("Str.IccApplyFailed", ex.Message);
            DialogService.Warning(Str.T("Str.OperationFailed"), _iccStatus);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private async void IccRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await IccRestoreCore();
    }

    private async Task IccRestoreCore()
    {
        var identity = _displayIdentity ?? "unavailable-display";
        _busy = true;
        SetDisplayInputEnabled(false);
        _iccStatus = null;
        IccApplyButton.IsEnabled = false;
        IccRestoreButton.IsEnabled = false;
        IccStateText.Text = Str.T("Str.Restoring2");
        try
        {
            var restored = await Task.Run(() => IccFilterService.Restore(identity));
            _iccStatus = restored ? Str.T("Str.IccRestored") : Str.T("Str.NothingToRestoreFilter");
        }
        catch (Exception ex)
        {
            _iccStatus = Str.T("Str.DisplayRestoreFailed", ex.Message);
            DialogService.Warning(Str.T("Str.OperationFailed"), _iccStatus);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private void SetDisplayInputEnabled(bool enabled)
    {
        if (!enabled) ColorPanel.IsEnabled = false;
        DisplayTargetCombo.IsEnabled = DisplayRefreshButton.IsEnabled = enabled;
        VibSlider.IsEnabled = VibRecommendedButton.IsEnabled = enabled;
        IccPresetList.IsEnabled = enabled;
    }
}
