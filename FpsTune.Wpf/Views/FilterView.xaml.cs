using System.IO;
using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class FilterView : UserControl
{
    // InitializeComponent 期间（元素属性赋值时）事件即会触发，此时后续命名字段尚未 Connect；
    // 只允许 OnInitialized 全部完成后响应真实交互，避免半初始化状态触达 catch 回滚分支。
    private bool _crosshairReady;
    private bool _suppressCrosshair;

    // 画面色彩与滤镜：目标显示器 → 画面与色彩（含实机截图对比）→ 滤镜预设库
    private bool _busy;
    private string? _vibStatus;
    private string? _iccStatus;
    private string? _iccBackupNotice;
    private string? _displayIdentity;
    private bool _displaySyncing;
    private bool _vibSyncing;
    private int _refreshGen;

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
        ColorPanel.VibranceContent = VibranceContent;
        ColorPanel.IccContent = IccAdvanced;
        ColorPanel.Unified = true;
        ColorPanel.BusyChanged += busy => { _busy = busy; ++_refreshGen; SetDisplayInputEnabled(!busy); if (!busy) Refresh(); };
        var choices = Enum.GetValues<IccFilterPreset>().Select(preset =>
        {
            var keys = IccProfileGenerator.ResourceKeysFor(preset);
            return new IccChoice(preset, keys.Title, keys.Description, preset == IccFilterPreset.Vivid);
        }).ToList();
        choices.Add(new(null, "Str.IccStandard", "Str.IccStandardDesc"));
        IccPresetList.ItemsSource = choices;
        Loaded += (_, _) => { Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaysChanged; Refresh(); };
        Unloaded += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
    }

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
        _crosshairReady = true;
    }
    private void Crosshair_Changed(object sender, RoutedEventArgs e)
    {
        if (!_crosshairReady || _suppressCrosshair) return;
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

    /// <summary>命令面板跳「画面与色彩」时直接把该卡滚到视口。</summary>
    internal void RevealColor() => CardVib.BringIntoView();

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
        await ColorPanel.RefreshFor(identity);
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
