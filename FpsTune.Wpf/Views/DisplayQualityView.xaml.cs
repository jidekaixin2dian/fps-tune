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

    public DisplayQualityView()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        if (_busy)
            return;

        if (!DisplayQualityService.FeatureEnabled)
        {
            SupportedPanel.Visibility = Visibility.Collapsed;
            UnsupportedText.Visibility = Visibility.Visible;
            UnsupportedText.Text = "DLSS 模型覆盖功能尚在稳定性验证中，本版本暂未启用。";
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
            if (state.Covered)
            {
                StateText.Text = $"当前覆盖：{PresetLabel(state.PresetValue ?? 0)}"
                    + (state.Restorable ? "（本工具写入，可还原）" : "（其他工具/驱动既有配置，应用时将自动备份原值）");
            }
            else
            {
                StateText.Text = "当前未覆盖（跟随游戏内设置）。";
            }
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
        ApplyButton.IsEnabled = false;
        RestoreButton.IsEnabled = false;
        StateText.Text = "正在写入驱动设置…";
        try
        {
            var exeName = Path.GetFileName(path);
            await Task.Run(() => DisplayQualityService.ApplyDlssPreset(exeName, preset.Value));
            StateText.Text = preset.Value == DlssPreset.FollowGame
                ? "已移除覆盖：DLSS 预设回到游戏内/驱动默认。进游戏生效。"
                : $"已覆盖 DLSS 预设为 {PresetLabel((uint)preset.Value)}。进游戏生效；不满意可点「还原默认」。";
        }
        catch (Exception ex)
        {
            StateText.Text = "应用失败：" + ex.Message + "。系统设置未变或已如实还原，可重试。";
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
        ApplyButton.IsEnabled = false;
        RestoreButton.IsEnabled = false;
        StateText.Text = "正在还原…";
        try
        {
            var exeName = Path.GetFileName(path);
            var removed = await Task.Run(() => DisplayQualityService.RemoveDlssOverride(exeName));
            StateText.Text = removed
                ? "已还原：覆盖前的原值已恢复（或自建配置文件已删除）。进游戏生效。"
                : "没有找到本工具的覆盖或还原备份，无需还原。";
        }
        catch (Exception ex)
        {
            StateText.Text = "还原失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }
}
