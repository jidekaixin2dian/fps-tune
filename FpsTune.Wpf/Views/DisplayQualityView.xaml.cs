using System.IO;
using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

/// <summary>
/// 显示与画质页：驱动层的按游戏设置（当前为 DLSS SR 预设覆盖）。
/// 非 N 卡环境如实显示不支持；无游戏定位时提示先到检测页定位。
/// </summary>
public partial class DisplayQualityView : UserControl
{
    private bool _busy;
    private string? _dlssStatus;
    private string? _vibStatus;
    private string? _iccStatus;

    public DisplayQualityView()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        if (_busy)
            return;

        RefreshDlss();
        RefreshVibrance();
        RefreshIcc();
    }

    private void RefreshDlss()
    {
        if (!DisplayQualityService.FeatureEnabled)
        {
            SupportedPanel.Visibility = Visibility.Collapsed;
            UnsupportedText.Visibility = Visibility.Visible;
            UnsupportedText.Text = "DLSS 模型覆盖功能已停用（FPS_ENABLE_DLSS=0）。";
            return;
        }

        if (!DisplayQualityService.IsNvidiaSupported)
        {
            SupportedPanel.Visibility = Visibility.Collapsed;
            UnsupportedText.Visibility = Visibility.Visible;
            UnsupportedText.Text = "未检测到 NVIDIA 显卡驱动，DLSS 覆盖不可用。";
            return;
        }

        SupportedPanel.Visibility = Visibility.Visible;
        UnsupportedText.Visibility = Visibility.Collapsed;

        var path = AppState.GamePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            GameStateText.Text = "尚未定位游戏主程序。请先到「检测」页定位游戏，再回到本页设置。";
            ApplyButton.IsEnabled = false;
            RestoreButton.IsEnabled = false;
            SetPresetCardsEnabled(false);
            StateText.Text = "";
            return;
        }

        var label = GamePathService.LabelFor(path);
        var exeName = Path.GetFileName(path);
        GameStateText.Text = $"当前游戏：{label}（{exeName}）";

        try
        {
            var state = DisplayQualityService.GetDlssState(exeName);
            SyncPresetCardSelection(state);
            StateText.Text = _dlssStatus ?? (state.Covered
                ? $"当前覆盖：{PresetLabel(state.PresetValue ?? 0)}"
                    + (state.Restorable ? "（本工具写入，可还原）" : "（其他工具/驱动既有配置，应用时将自动备份原值）")
                : "当前未覆盖（跟随游戏内设置）。");
            ApplyButton.IsEnabled = true;
            RestoreButton.IsEnabled = state.Covered || state.Restorable;
            SetPresetCardsEnabled(true);
        }
        catch (Exception ex)
        {
            StateText.Text = "读取覆盖状态失败：" + ex.Message;
            ApplyButton.IsEnabled = false;
            RestoreButton.IsEnabled = false;
        }
    }

    private void SyncPresetCardSelection(DisplayQualityService.DlssState state)
    {
        var target = state.Covered && state.PresetValue is { } value
            ? value switch
            {
                (uint)DlssPreset.Latest => PresetLatest,
                (uint)DlssPreset.PresetM => PresetM,
                (uint)DlssPreset.PresetK => PresetK,
                (uint)DlssPreset.PresetJ => PresetJ,
                (uint)DlssPreset.PresetE => PresetE,
                _ => null
            }
            : PresetFollowGame;
        if (target is not null)
            target.IsChecked = true;
    }

    private void Preset_Checked(object sender, RoutedEventArgs e)
    {
        // 选中卡片即启用"应用覆盖"；不自动写驱动，统一由按钮执行
        _dlssStatus = null;
        if (SupportedPanel.Visibility == Visibility.Visible && !_busy)
            ApplyButton.IsEnabled = true;
    }

    private void SetPresetCardsEnabled(bool enabled)
    {
        foreach (var card in new RadioButton[] { PresetFollowGame, PresetLatest, PresetM, PresetK, PresetJ, PresetE })
            card.IsEnabled = enabled;
    }

    private static string PresetLabel(uint value) => value switch
    {
        (uint)DlssPreset.Latest => "最新预设",
        (uint)DlssPreset.PresetM => "M 预设（新一代模型·高端）",
        (uint)DlssPreset.PresetK => "K 预设（新一代模型）",
        (uint)DlssPreset.PresetJ => "J 预设（新一代模型）",
        (uint)DlssPreset.PresetE => "E 预设（旧一代 CNN 模型）",
        _ => $"预设 0x{value:X}"
    };

    private DlssPreset? SelectedPreset()
    {
        if (PresetFollowGame.IsChecked == true) return DlssPreset.FollowGame;
        if (PresetLatest.IsChecked == true) return DlssPreset.Latest;
        if (PresetM.IsChecked == true) return DlssPreset.PresetM;
        if (PresetK.IsChecked == true) return DlssPreset.PresetK;
        if (PresetJ.IsChecked == true) return DlssPreset.PresetJ;
        if (PresetE.IsChecked == true) return DlssPreset.PresetE;
        return null;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var path = AppState.GamePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        var preset = SelectedPreset();
        if (preset is null)
        {
            DialogService.Warning("显示与画质", "请先选择一个预设。");
            return;
        }

        _busy = true;
        _dlssStatus = null;
        ApplyButton.IsEnabled = false;
        RestoreButton.IsEnabled = false;
        StateText.Text = "正在写入驱动设置…";
        try
        {
            var exeName = Path.GetFileName(path);
            await Task.Run(() => DisplayQualityService.ApplyDlssPreset(exeName, preset.Value));
            _dlssStatus = preset.Value == DlssPreset.FollowGame
                ? "已移除覆盖：DLSS 预设回到游戏内/驱动默认。进游戏生效。"
                : $"已覆盖 DLSS 预设为 {PresetLabel((uint)preset.Value)}。进游戏生效；不满意可点「还原默认」。";
        }
        catch (NvdrsException ex) when (ex.Status == -175)
        {
            _dlssStatus = "应用失败：写入 NVIDIA 配置需要管理员权限。系统设置未变。";
            if (DialogService.Confirm(
                    "需要管理员权限",
                    "写入 NVIDIA 驱动配置需要管理员权限，当前程序不是以管理员身份运行的。\n\n" +
                    "要以管理员身份重启并重试吗？",
                    confirmText: "以管理员重启"))
                AdminHelper.RestartAsAdministrator();
        }
        catch (Exception ex)
        {
            _dlssStatus = "应用失败：" + ex.Message + "。系统设置未变或已如实还原，可重试。";
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var path = AppState.GamePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        _busy = true;
        _dlssStatus = null;
        ApplyButton.IsEnabled = false;
        RestoreButton.IsEnabled = false;
        StateText.Text = "正在还原…";
        try
        {
            var exeName = Path.GetFileName(path);
            var removed = await Task.Run(() => DisplayQualityService.RemoveDlssOverride(exeName));
            _dlssStatus = removed
                ? "已还原：覆盖前的原值已恢复（或自建配置文件已删除）。进游戏生效。"
                : "没有找到本工具的覆盖或还原备份，无需还原。";
        }
        catch (NvdrsException ex) when (ex.Status == -175)
        {
            _dlssStatus = "还原失败：写入 NVIDIA 配置需要管理员权限。";
            if (DialogService.Confirm(
                    "需要管理员权限",
                    "还原 NVIDIA 驱动配置需要管理员权限。\n\n要以管理员身份重启并重试吗？",
                    confirmText: "以管理员重启"))
                AdminHelper.RestartAsAdministrator();
        }
        catch (Exception ex)
        {
            _dlssStatus = "还原失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    // ---------- 数字振动（显示级，全桌面） ----------

    private bool _vibSyncing;

    private void RefreshVibrance()
    {
        try
        {
            var state = DigitalVibranceService.GetState();
            if (!state.Supported)
            {
                VibSupportedPanel.Visibility = Visibility.Collapsed;
                VibUnsupportedText.Visibility = Visibility.Visible;
                VibUnsupportedText.Text = state.UnsupportedReason ?? "数字振动在当前环境不可用。";
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
            VibRestoreButton.IsEnabled = state.Restorable;
            VibStateText.Text = _vibStatus ?? (state.Restorable
                ? $"当前 {percent}%（已记录原始档位，可还原；驱动默认约 {PercentOf(state.Default, state)}%）"
                : $"当前 {percent}%（驱动默认约 {PercentOf(state.Default, state)}%）");
        }
        catch (Exception ex)
        {
            VibSupportedPanel.Visibility = Visibility.Collapsed;
            VibUnsupportedText.Visibility = Visibility.Visible;
            VibUnsupportedText.Text = "读取数字振动状态失败：" + ex.Message;
        }
    }

    private static int PercentOf(int level, VibranceState state)
        => state.Max > state.Min
            ? (int)Math.Round((level - state.Min) * 100.0 / (state.Max - state.Min))
            : 0;

    private void VibSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_vibSyncing)
            return;
        VibPercentText.Text = (int)VibSlider.Value + "%";
    }

    private async void VibApply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var percent = (int)VibSlider.Value;
        var confirmed = DialogService.Confirm(
            "应用数字振动",
            $"将把整块屏幕的色彩鲜艳度调到 {percent}%。\n\n" +
            "· 这是显示全局设置：桌面、网页、游戏观感都会变化\n" +
            "· 不是只在游戏内生效\n" +
            "· 应用前会记住当前档位，可随时「还原原始」\n\n确定应用？",
            confirmText: "应用");
        if (!confirmed)
            return;

        _busy = true;
        _vibStatus = null;
        VibApplyButton.IsEnabled = false;
        VibRestoreButton.IsEnabled = false;
        VibStateText.Text = "正在写入…";
        try
        {
            await Task.Run(() => DigitalVibranceService.SetPercent(percent));
            _vibStatus = $"已应用：{percent}%。整屏即时生效；不满意点「还原原始」。";
        }
        catch (Exception ex)
        {
            _vibStatus = "应用失败：" + ex.Message;
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
        _busy = true;
        _vibStatus = null;
        VibApplyButton.IsEnabled = false;
        VibRestoreButton.IsEnabled = false;
        VibStateText.Text = "正在还原…";
        try
        {
            var restored = await Task.Run(() => DigitalVibranceService.Restore());
            _vibStatus = restored
                ? "已还原：鲜艳度回到调整前的档位。"
                : "没有找到需要还原的备份，无需还原。";
        }
        catch (Exception ex)
        {
            _vibStatus = "还原失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    // ---------- ICC 滤镜（第二张卡片，独立于 DLSS 的可用性） ----------

    private void RefreshIcc()
    {
        try
        {
            var state = IccFilterService.GetState();
            if (!state.Supported)
            {
                IccSupportedPanel.Visibility = Visibility.Collapsed;
                IccUnsupportedText.Visibility = Visibility.Visible;
                IccUnsupportedText.Text = state.UnsupportedReason ?? "ICC 滤镜在当前环境不可用。";
                return;
            }

            IccSupportedPanel.Visibility = Visibility.Visible;
            IccUnsupportedText.Visibility = Visibility.Collapsed;
            IccCurrentText.Text = "当前生效：" + (state.CurrentProfileName ?? "<无>（未读取到可用的显示配置文件）");

            SetIccPresetCardsEnabled(true);
            IccApplyButton.IsEnabled = state.CurrentProfileName is not null;
            IccRestoreButton.IsEnabled = state.Restorable;

            IccStateText.Text = _iccStatus ?? (state.Restorable
                ? "已记录你的原始色彩配置，可一键还原。"
                : "");
        }
        catch (Exception ex)
        {
            IccSupportedPanel.Visibility = Visibility.Collapsed;
            IccUnsupportedText.Visibility = Visibility.Visible;
            IccUnsupportedText.Text = "读取 ICC 状态失败：" + ex.Message;
        }
    }

    private void SetIccPresetCardsEnabled(bool enabled)
    {
        foreach (var card in new RadioButton[] { IccPresetVivid, IccPresetShadowBoost, IccPresetDehaze, IccPresetStandard })
            card.IsEnabled = enabled;
    }

    private IccFilterPreset? SelectedIccPreset()
    {
        if (IccPresetVivid.IsChecked == true) return IccFilterPreset.Vivid;
        if (IccPresetShadowBoost.IsChecked == true) return IccFilterPreset.ShadowBoost;
        if (IccPresetDehaze.IsChecked == true) return IccFilterPreset.Dehaze;
        return null; // 「标准」卡片 = 还原语义，走 Restore
    }

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

        var confirmed = DialogService.Confirm(
            "应用 ICC 滤镜",
            "切换是系统全局的：整个桌面（含网页、视频、游戏）的观感都会变化。\n\n" +
            "· 第一版只作用主显示器，多显示器暂不支持\n" +
            "· 程序生成的 ICC 是简单曲线变换，效果弱于专业校色\n" +
            "· 应用前会自动记录原始配置，随时可点「还原原始」恢复\n\n确定应用？",
            confirmText: "应用");
        if (!confirmed)
            return;

        _busy = true;
        _iccStatus = null;
        IccApplyButton.IsEnabled = false;
        IccRestoreButton.IsEnabled = false;
        IccStateText.Text = "正在应用预设…";
        try
        {
            var name = await Task.Run(() => IccFilterService.Apply(preset.Value));
            _iccStatus = $"已生效：{name}。系统全局切换即时可见；不满意点「还原原始」。";
        }
        catch (Exception ex)
        {
            _iccStatus = "应用失败：" + ex.Message;
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
        _busy = true;
        _iccStatus = null;
        IccApplyButton.IsEnabled = false;
        IccRestoreButton.IsEnabled = false;
        IccStateText.Text = "正在还原…";
        try
        {
            var restored = await Task.Run(() => IccFilterService.Restore());
            _iccStatus = restored
                ? "已还原：显示器的色彩配置已恢复为应用滤镜前的原样。"
                : "没有找到需要还原的备份（尚未应用过滤镜），无需还原。";
        }
        catch (Exception ex)
        {
            _iccStatus = "还原失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }
}
