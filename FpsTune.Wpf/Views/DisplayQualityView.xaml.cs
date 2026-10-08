using System.IO;
using System.Text;
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
    private string? _drsStatus;
    private bool _drsSyncing;

    public DisplayQualityView()
    {
        InitializeComponent();
        Loaded += (_, _) => { Refresh(); _ = RefreshGpuIdentityAsync(); };
    }

    private int _refreshGen;

    private void Refresh()
    {
        if (_busy)
            return;

        var gen = ++_refreshGen;
        RefreshDriverAdvice();
        RefreshManualChecklist();
        // NVAPI / ICC / 注册表读取全部移到线程池：点击切页不再被同步 IO 卡住。
        // 快速切页会并发两轮只读快照，无害；gen 保证只有最新一轮的结果落到界面。
        _ = RefreshStatesAsync(gen);
    }

    /// <summary>一次状态刷新的后台读取结果：全部服务调用在 ComputeSnapshot 里完成，UI 更新统一在 ApplySnapshot。</summary>
    private sealed record StateSnapshot(
        bool FeatureEnabled,
        bool NvidiaSupported,
        bool HasGame,
        string? GameLabel,
        string? GameExe,
        DisplayQualityService.DlssState? Dlss, string? DlssError,
        VibranceState? Vib, string? VibError,
        IccFilterService.IccState? Icc, string? IccError,
        DisplayQualityService.DrsGameSettings? Drs, string? DrsError);

    private static StateSnapshot ComputeSnapshot()
    {
        var feature = DisplayQualityService.FeatureEnabled;
        var nvidia = feature && DisplayQualityService.IsNvidiaSupported;
        var path = AppState.GamePath;
        var hasGame = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        string? label = null, exe = null;

        var dlss = default(DisplayQualityService.DlssState);
        var dlssError = default(string);
        var drs = default(DisplayQualityService.DrsGameSettings);
        var drsError = default(string);
        if (nvidia && hasGame)
        {
            exe = Path.GetFileName(path);
            label = GamePathService.LabelFor(path!);
            try { dlss = DisplayQualityService.GetDlssState(exe!); }
            catch (Exception ex) { dlssError = ex.Message; }
            try { drs = DisplayQualityService.GetDrsGameSettings(exe!); }
            catch (Exception ex) { drsError = ex.Message; }
        }

        var vib = default(VibranceState);
        var vibError = default(string);
        try { vib = DigitalVibranceService.GetState(); }
        catch (Exception ex) { vibError = ex.Message; }

        var icc = default(IccFilterService.IccState);
        var iccError = default(string);
        try { icc = IccFilterService.GetState(); }
        catch (Exception ex) { iccError = ex.Message; }

        return new StateSnapshot(feature, nvidia, hasGame, label, exe,
            dlss, dlssError, vib, vibError, icc, iccError, drs, drsError);
    }

    private async Task RefreshStatesAsync(int gen)
    {
        var snap = await Task.Run(ComputeSnapshot);
        if (gen != _refreshGen)
            return; // 期间发生了更新的刷新，旧结果作废
        ApplySnapshot(snap);
    }

    private void ApplySnapshot(StateSnapshot s)
    {
        ApplyDlssState(s);
        ApplyVibState(s);
        ApplyIccState(s);
        ApplyDrsState(s);
    }

    // ---------- 页签状态徽标：不点进去也能看到各组当前状态 ----------

    /// <summary>设置页签徽标；text 为 null 时隐藏徽标（尚未读到状态）。</summary>
    private static void SetBadge(TextBlock status, Border badge, string? text)
    {
        if (text is null)
        {
            badge.Visibility = Visibility.Collapsed;
            return;
        }
        status.Text = text;
        badge.Visibility = Visibility.Visible;
    }

    private void SetGameChip(string? label, string? exe)
    {
        if (label is null)
        {
            GameChip.Visibility = Visibility.Collapsed;
            return;
        }
        GameChipText.Text = Str.T("Str.CurrentGame") + "：" + label;
        GameChip.ToolTip = exe;
        GameChip.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 驱动版本建议：只读信息。
    /// **本工具不下载、不安装任何驱动**（红线）——这里只给版本号与来源，引导用户自己去官网。
    /// </summary>
    private void RefreshDriverAdvice()
    {
        var gpu = HardwareInfoService.Get().Gpu;
        var advice = GpuDriverAdvisor.For(gpu);

        DriverAdviceSourceText.Text = Str.T("Str.DriverSource");
        SetBadge(TabStatusAdvice, TabBadgeAdvice,
            string.IsNullOrWhiteSpace(advice?.Series) ? "—" : advice.Series);

        if (advice is null)
        {
            DriverAdviceText.Text = Str.T("Str.DriverNoAdvice");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(Str.T("Str.DriverGpuLabel") + gpu);
        sb.AppendLine(Str.T("Str.DriverSeriesLabel") + advice.Series);
        sb.AppendLine(Str.T("Str.DriverStableLabel") +
                      (string.IsNullOrEmpty(advice.Stable) ? Str.T("Str.DriverNoStable") : advice.Stable));
        if (!string.IsNullOrWhiteSpace(advice.Alternatives))
            sb.AppendLine(Str.T("Str.DriverAltLabel") + advice.Alternatives);
        sb.Append(advice.Note);
        DriverAdviceText.Text = sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 驱动内手动设置清单（P2-8）：按显卡厂商切换卡片内容，纯只读指引。
    /// 认不出厂商就明说（Unknown 分支），不猜——与 <see cref="GpuDriverAdvisor"/> 同一口径。
    /// </summary>
    private void RefreshManualChecklist()
    {
        var gpu = HardwareInfoService.Get().Gpu;
        ManualGpuText.Text = Str.T("Str.ManualGpuLabel") + gpu;

        var vendor = GpuVendor.Of(gpu);
        ManualAmdPanel.Visibility = vendor == GpuVendorKind.Amd ? Visibility.Visible : Visibility.Collapsed;
        ManualIntelPanel.Visibility = vendor == GpuVendorKind.Intel ? Visibility.Visible : Visibility.Collapsed;
        ManualNvidiaText.Visibility = vendor == GpuVendorKind.Nvidia ? Visibility.Visible : Visibility.Collapsed;
        ManualUnknownText.Visibility = vendor == GpuVendorKind.Unknown ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 二级页签（用户反馈：卡片太多滚动太长）：一次只显示一组卡片。
    /// 页面实例被缓存，页签选择在会话内自然保持；XAML 已静态设置首签可见。
    /// </summary>
    private void SubTab_Checked(object sender, RoutedEventArgs e)
    {
        // InitializeComponent 期间首个页签的 Checked 会提前触发，此时后面的卡片还没解析出来
        if (CardDlss is null || CardAdvice is null)
            return;
        if (sender is not RadioButton { Tag: string key })
            return;

        CardDlss.Visibility = key == "dlss" ? Visibility.Visible : Visibility.Collapsed;
        CardVib.Visibility = key == "vibicc" ? Visibility.Visible : Visibility.Collapsed;
        CardDrs.Visibility = key == "drs" ? Visibility.Visible : Visibility.Collapsed;
        CardAdvice.Visibility = key == "advice" ? Visibility.Visible : Visibility.Collapsed;
        CardGpuIdentity.Visibility = key == "identity" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyDlssState(StateSnapshot s)
    {
        if (!s.FeatureEnabled)
        {
            SupportedPanel.Visibility = Visibility.Collapsed;
            UnsupportedText.Visibility = Visibility.Visible;
            UnsupportedText.Text = Str.T("Str.DlssDisabled");
            return;
        }

        if (!s.NvidiaSupported)
        {
            SupportedPanel.Visibility = Visibility.Collapsed;
            UnsupportedText.Visibility = Visibility.Visible;
            UnsupportedText.Text = Str.T("Str.NoNvidiaForDlss");
            return;
        }

        SupportedPanel.Visibility = Visibility.Visible;
        UnsupportedText.Visibility = Visibility.Collapsed;

        if (!s.HasGame)
        {
            SetGameChip(null, null);
            ApplyButton.IsEnabled = false;
            RestoreButton.IsEnabled = false;
            SetPresetCardsEnabled(false);
            StateText.Text = Str.T("Str.LocateGameFirstThenReturn");
            SetBadge(TabStatusDlss, TabBadgeDlss, "—");
            return;
        }

        SetGameChip(s.GameLabel, s.GameExe);

        if (s.DlssError is not null)
        {
            StateText.Text = "读取覆盖状态失败：" + s.DlssError;
            ApplyButton.IsEnabled = false;
            RestoreButton.IsEnabled = false;
            SetBadge(TabStatusDlss, TabBadgeDlss, "—");
            return;
        }

        var state = s.Dlss!;
        SyncPresetCardSelection(state);
        StateText.Text = _dlssStatus ?? (state.Covered
            ? $"当前覆盖：{PresetLabel(state.PresetValue ?? 0)}"
                + (state.Restorable ? "（本工具写入，可还原）" : "（其他工具/驱动既有配置，应用时将自动备份原值）")
            : Str.T("Str.NotOverriddenInGame"));
        ApplyButton.IsEnabled = true;
        RestoreButton.IsEnabled = state.Covered || state.Restorable;
        SetPresetCardsEnabled(true);
        SetBadge(TabStatusDlss, TabBadgeDlss,
            state.Covered ? DlssPresetBadge(state.PresetValue) : Str.T("Str.FollowGame"));
    }

    /// <summary>页签徽标用短预设名：字母 / 最新 / 原始十六进制。</summary>
    private static string DlssPresetBadge(uint? value) => value switch
    {
        (uint)DlssPreset.Latest => Str.T("Str.LatestPreset"),
        (uint)DlssPreset.PresetK => "K",
        (uint)DlssPreset.PresetM => "M",
        (uint)DlssPreset.PresetJ => "J",
        (uint)DlssPreset.PresetE => "E",
        { } v => $"0x{v:X}",
        null => "—",
    };

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
        // 选中卡片即启用Str.T("Str.ApplyOverride")；不自动写驱动，统一由按钮执行
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
        (uint)DlssPreset.Latest => Str.T("Str.LatestPreset"),
        (uint)DlssPreset.PresetM => "M 预设（新一代模型·高端）",
        (uint)DlssPreset.PresetK => "K 预设（新一代模型·推荐）",
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
            DialogService.Warning("显示与画质", Str.T("Str.SelectPresetFirst"));
            return;
        }

        _busy = true;
        _dlssStatus = null;
        ApplyButton.IsEnabled = false;
        RestoreButton.IsEnabled = false;
        StateText.Text = Str.T("Str.WritingDrs");
        try
        {
            var exeName = Path.GetFileName(path);
            await Task.Run(() => DisplayQualityService.ApplyDlssPreset(exeName, preset.Value));
            _dlssStatus = preset.Value == DlssPreset.FollowGame
                ? Str.T("Str.OverrideRemoved")
                : $"已覆盖 DLSS 预设为 {PresetLabel((uint)preset.Value)}。进游戏生效；不满意可点「还原默认」。";
        }
        catch (NvdrsException ex) when (ex.Status == -175)
        {
            _dlssStatus = Str.T("Str.ApplyFailedNeedAdminUnchanged");
            if (DialogService.Confirm(
                    Str.T("Str.NeedsAdmin"),
                    "写入 NVIDIA 驱动配置需要管理员权限，当前程序不是以管理员身份运行的。\n\n" +
                    Str.T("Str.ConfirmRestartAdminRetry"),
                    confirmText: Str.T("Str.RestartAsAdminShort")))
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
        StateText.Text = Str.T("Str.Restoring2");
        try
        {
            var exeName = Path.GetFileName(path);
            var removed = await Task.Run(() => DisplayQualityService.RemoveDlssOverride(exeName));
            _dlssStatus = removed
                ? Str.T("Str.OverrideRestored")
                : Str.T("Str.NothingToRestoreOverride");
        }
        catch (NvdrsException ex) when (ex.Status == -175)
        {
            _dlssStatus = Str.T("Str.RestoreFailedNeedAdmin");
            if (DialogService.Confirm(
                    Str.T("Str.NeedsAdmin"),
                    "还原 NVIDIA 驱动配置需要管理员权限。\n\n要以管理员身份重启并重试吗？",
                    confirmText: Str.T("Str.RestartAsAdminShort")))
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

    private void ApplyVibState(StateSnapshot s)
    {
        if (s.VibError is not null)
        {
            VibSupportedPanel.Visibility = Visibility.Collapsed;
            VibUnsupportedText.Visibility = Visibility.Visible;
            VibUnsupportedText.Text = "读取数字振动状态失败：" + s.VibError;
            SetBadge(TabStatusVib, TabBadgeVib, "—");
            return;
        }

        var state = s.Vib!;
        if (!state.Supported)
        {
            VibSupportedPanel.Visibility = Visibility.Collapsed;
            VibUnsupportedText.Visibility = Visibility.Visible;
            VibUnsupportedText.Text = state.UnsupportedReason ?? Str.T("Str.VibranceUnavailable");
            SetBadge(TabStatusVib, TabBadgeVib, "—");
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
        SetBadge(TabStatusVib, TabBadgeVib, $"{percent}%");
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
        _vibSyncing = false;
    }

    private async void VibApply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var percent = (int)VibSlider.Value;
        var confirmed = DialogService.Confirm(
            Str.T("Str.ApplyVibrance"),
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
        VibStateText.Text = Str.T("Str.Writing");
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
        VibStateText.Text = Str.T("Str.Restoring2");
        try
        {
            var restored = await Task.Run(() => DigitalVibranceService.Restore());
            _vibStatus = restored
                ? Str.T("Str.VibranceRestored")
                : Str.T("Str.NothingToRestore2");
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

    private void ApplyIccState(StateSnapshot s)
    {
        if (s.IccError is not null)
        {
            IccSupportedPanel.Visibility = Visibility.Collapsed;
            IccUnsupportedText.Visibility = Visibility.Visible;
            IccUnsupportedText.Text = "读取 ICC 状态失败：" + s.IccError;
            SetBadge(TabStatusIcc, TabBadgeIcc, "—");
            return;
        }

        var state = s.Icc!;
        if (!state.Supported)
        {
            IccSupportedPanel.Visibility = Visibility.Collapsed;
            IccUnsupportedText.Visibility = Visibility.Visible;
            IccUnsupportedText.Text = state.UnsupportedReason ?? Str.T("Str.IccUnavailable");
            SetBadge(TabStatusIcc, TabBadgeIcc, "—");
            return;
        }

        IccSupportedPanel.Visibility = Visibility.Visible;
        IccUnsupportedText.Visibility = Visibility.Collapsed;
        IccCurrentText.Text = Str.T("Str.CurrentlyActive") + (state.CurrentProfileName ?? "<无>（未读取到可用的显示配置文件）");

        SetIccPresetCardsEnabled(true);
        IccApplyButton.IsEnabled = state.CurrentProfileName is not null;
        IccRestoreButton.IsEnabled = state.Restorable;

        IccStateText.Text = _iccStatus ?? (state.Restorable
            ? Str.T("Str.IccOriginalSaved")
            : "");
        SetBadge(TabStatusIcc, TabBadgeIcc, ShortProfileName(state.CurrentProfileName));
    }

    /// <summary>页签徽标用短 profile 名：超长截断，避免页签被撑开。</summary>
    private static string ShortProfileName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "—";
        return name.Length <= 16 ? name : name[..13] + "…";
    }

    private void SetIccPresetCardsEnabled(bool enabled)
    {
        // 8 张预设卡全量启停（原只列 4 张，其余靠默认值兜底，语义不完整）
        foreach (var card in new RadioButton[]
        {
            IccPresetVivid, IccPresetShadowBoost, IccPresetDehaze, IccPresetNightGuard,
            IccPresetWarm, IccPresetCool, IccPresetSoft, IccPresetStandard
        })
            card.IsEnabled = enabled;
    }

    private IccFilterPreset? SelectedIccPreset()
    {
        if (IccPresetVivid.IsChecked == true) return IccFilterPreset.Vivid;
        if (IccPresetShadowBoost.IsChecked == true) return IccFilterPreset.ShadowBoost;
        if (IccPresetDehaze.IsChecked == true) return IccFilterPreset.Dehaze;
        if (IccPresetNightGuard.IsChecked == true) return IccFilterPreset.NightGuard;
        if (IccPresetWarm.IsChecked == true) return IccFilterPreset.Warm;
        if (IccPresetCool.IsChecked == true) return IccFilterPreset.Cool;
        if (IccPresetSoft.IsChecked == true) return IccFilterPreset.Soft;
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

        await IccApplyCore(() => IccFilterService.Apply(preset.Value));
    }

    /// <summary>P2-10：用户自选 .icc / .icm 文件。与预设共用确认与应用流程。</summary>
    private void IccApplyFromFile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Str.T("Str.IccApplyFromFile"),
            Filter = Str.T("Str.IccFileFilter")
        };
        if (dialog.ShowDialog() != true)
            return;
        var path = dialog.FileName;
        _ = IccApplyCore(() => IccFilterService.ApplyFromFile(path));
    }

    /// <summary>预设与用户文件共用的应用流程：确认 → 写入 → 状态反馈（同一套全局生效警示）。</summary>
    private async Task IccApplyCore(Func<string> apply)
    {
        var confirmed = DialogService.Confirm(
            Str.T("Str.ApplyIcc"),
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
        IccStateText.Text = Str.T("Str.Restoring2");
        try
        {
            var restored = await Task.Run(() => IccFilterService.Restore());
            _iccStatus = restored
                ? Str.T("Str.IccRestored")
                : Str.T("Str.NothingToRestoreFilter");
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

    // ---------- M3：驱动 3D（纹理/电源/透明度/预渲染） ----------

    private void ApplyDrsState(StateSnapshot s)
    {
        if (!s.FeatureEnabled)
        {
            DrsSupportedPanel.Visibility = Visibility.Collapsed;
            DrsUnsupportedText.Visibility = Visibility.Visible;
            DrsUnsupportedText.Text = Str.T("Str.DrsDisabled");
            SetBadge(TabStatusDrs, TabBadgeDrs, "—");
            return;
        }
        if (!s.NvidiaSupported)
        {
            DrsSupportedPanel.Visibility = Visibility.Collapsed;
            DrsUnsupportedText.Visibility = Visibility.Visible;
            DrsUnsupportedText.Text = Str.T("Str.NoNvidiaForDrs");
            SetBadge(TabStatusDrs, TabBadgeDrs, "—");
            return;
        }

        DrsSupportedPanel.Visibility = Visibility.Visible;
        DrsUnsupportedText.Visibility = Visibility.Collapsed;

        if (!s.HasGame)
        {
            DrsStateText.Text = Str.T("Str.LocateGameFirst");
            DrsApplyButton.IsEnabled = false;
            DrsRestoreButton.IsEnabled = false;
            SetBadge(TabStatusDrs, TabBadgeDrs, "—");
            return;
        }

        if (s.DrsError is not null)
        {
            DrsStateText.Text = "读取驱动 3D 设置失败：" + s.DrsError;
            DrsApplyButton.IsEnabled = false;
            DrsRestoreButton.IsEnabled = false;
            SetBadge(TabStatusDrs, TabBadgeDrs, "—");
            return;
        }

        var state = s.Drs!;
        _drsSyncing = true;
        SelectComboByTag(TexQualityCombo, state.TextureQuality is { } t ? ((uint)t).ToString() : "");
        SelectComboByTag(PowerModeCombo, state.PowerMode is { } p ? ((uint)p).ToString() : "");
        SelectComboByTag(TransparencyCombo, state.TransparencyAa is { } a ? ((int)a).ToString() : "");
        SelectComboByTag(PreRenderCombo, state.PreRenderLimit is { } pr ? pr.ToString() : "");
        SelectComboByTag(AnisoCombo, state.Aniso is null or AnisoLevel.AppControlled
            ? ""
            : ((uint)state.Aniso.Value).ToString());
        SelectComboByTag(VSyncCombo, state.VSync switch
        {
            VSyncMode.ForceOff => "off",
            VSyncMode.ForceOn => "on",
            _ => "",
        });
        SelectComboByTag(ShaderCacheCombo, state.ShaderCache switch
        {
            true => "1",
            false => "0",
            null => "",
        });
        _drsSyncing = false;

        DrsStateText.Text = _drsStatus ?? (state.Restorable
            ? Str.T("Str.OriginalSaved")
            : Str.T("Str.NotOverriddenDriver"));
        DrsApplyButton.IsEnabled = true;
        DrsRestoreButton.IsEnabled = state.Restorable;

        var customCount = 0;
        if (state.TextureQuality is not null) customCount++;
        if (state.PowerMode is not null) customCount++;
        if (state.TransparencyAa is not null) customCount++;
        if (state.PreRenderLimit is not null) customCount++;
        if (state.Aniso is { } and not AnisoLevel.AppControlled) customCount++;
        if (state.VSync is not VSyncMode.AppControlled) customCount++;
        if (state.ShaderCache is not null) customCount++;
        SetBadge(TabStatusDrs, TabBadgeDrs,
            customCount > 0 ? Str.T("Str.BadgeCustomCount", customCount) : Str.T("Str.BadgeDefault"));
    }

    private static void SelectComboByTag(System.Windows.Controls.ComboBox combo, string tag)
    {
        foreach (System.Windows.Controls.ComboBoxItem item in combo.Items)
        {
            if ((item.Tag as string) == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private static string? ComboTag(System.Windows.Controls.ComboBox combo)
        => (combo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string;

    private void Drs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_drsSyncing)
            _drsStatus = null;
    }

    private async void DrsPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var path = AppState.GamePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        // 桌面非笔电高端卡才推透明度 4x；默认 2x
        var desktopHighEnd = HardwareInfoService.IsDesktop && HardwareInfoService.IsHighEndNvidia;
        var traaLabel = desktopHighEnd ? Str.T("Str.Supersample4x") : Str.T("Str.Supersample2x");
        var recipe = DisplayQualityService.CompetitiveRecipe(desktopHighEnd);
        var confirmed = DialogService.Confirm(Str.T("Str.OneClickEsports"),
            Str.T("Str.RecipeConfirmIntro") + "\n\n" + string.Join("\n", recipe.Select(r => "· " + r.Label + ": " + r.ValueLabel))
            + "\n\n" + Str.T("Str.RecipeConfirmFooter"), confirmText: Str.T("Str.RecipeApply"));
        if (!confirmed)
            return;

        _busy = true;
        _drsStatus = null;
        DrsApplyButton.IsEnabled = false;
        DrsRestoreButton.IsEnabled = false;
        DrsStateText.Text = Str.T("Str.WritingEsports");
        try
        {
            var exeName = Path.GetFileName(path);
            await Task.Run(() => DisplayQualityService.ApplyCompetitivePreset(exeName, desktopHighEnd));
            _drsStatus = "已应用竞技推荐（高质量 + 最高性能 + " + traaLabel + " + 预渲染 1 帧）。";
        }
        catch (NvdrsException ex) when (ex.Status == -175)
        {
            _drsStatus = Str.T("Str.ApplyFailedNeedAdmin");
            if (DialogService.Confirm(Str.T("Str.NeedsAdmin"), Str.T("Str.ConfirmRestartAdminRetry"), confirmText: Str.T("Str.RestartAsAdminShort")))
                AdminHelper.RestartAsAdministrator();
        }
        catch (Exception ex)
        {
            _drsStatus = "应用失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private async void DrsApply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var path = AppState.GamePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        var confirmed = DialogService.Confirm(
            Str.T("Str.ApplyDrsFull"),
            "将按所选值写入当前游戏的 NVIDIA 驱动配置。\n\n" +
            "· 不修改游戏文件，可随时「还原默认」\n" +
            "· 与 DLSS 覆盖共用备份，还原会一并恢复\n" +
            "· 写入驱动需要管理员权限\n\n确定应用？",
            confirmText: "应用");
        if (!confirmed)
            return;

        _busy = true;
        _drsStatus = null;
        DrsApplyButton.IsEnabled = false;
        DrsRestoreButton.IsEnabled = false;
        DrsStateText.Text = Str.T("Str.Writing");
        try
        {
            var exeName = Path.GetFileName(path);
            await Task.Run(() =>
            {
                var tex = ComboTag(TexQualityCombo);
                if (!string.IsNullOrEmpty(tex))
                    DisplayQualityService.ApplyTextureQuality(exeName, (TextureFilterQuality)uint.Parse(tex));
                var power = ComboTag(PowerModeCombo);
                if (!string.IsNullOrEmpty(power))
                    DisplayQualityService.ApplyPowerMode(exeName, (PowerMode)uint.Parse(power));
                var traa = ComboTag(TransparencyCombo);
                if (!string.IsNullOrEmpty(traa))
                    DisplayQualityService.ApplyTransparencyAa(exeName, (TransparencyAa)int.Parse(traa));
                var pr = ComboTag(PreRenderCombo);
                if (!string.IsNullOrEmpty(pr))
                    DisplayQualityService.ApplyPreRenderLimit(exeName, uint.Parse(pr));
                else
                    DisplayQualityService.ApplyPreRenderLimit(exeName, null);
                var aniso = ComboTag(AnisoCombo);
                if (!string.IsNullOrEmpty(aniso))
                    DisplayQualityService.ApplyAnisoLevel(exeName, (AnisoLevel)uint.Parse(aniso));
                else
                    DisplayQualityService.ApplyAnisoLevel(exeName, AnisoLevel.AppControlled);
                var vsync = ComboTag(VSyncCombo);
                DisplayQualityService.ApplyVSyncMode(exeName, vsync switch
                {
                    "off" => VSyncMode.ForceOff,
                    "on" => VSyncMode.ForceOn,
                    _ => VSyncMode.AppControlled,
                });
                var sc = ComboTag(ShaderCacheCombo);
                DisplayQualityService.ApplyShaderDiskCache(exeName, sc switch
                {
                    "1" => true,
                    "0" => false,
                    _ => null,
                });
            });
            _drsStatus = Str.T("Str.DrsApplied");
        }
        catch (NvdrsException ex) when (ex.Status == -175)
        {
            _drsStatus = Str.T("Str.ApplyFailedNeedAdmin");
            if (DialogService.Confirm(Str.T("Str.NeedsAdmin"), Str.T("Str.ConfirmRestartAdminRetry"), confirmText: Str.T("Str.RestartAsAdminShort")))
                AdminHelper.RestartAsAdministrator();
        }
        catch (Exception ex)
        {
            _drsStatus = "应用失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private async void DrsRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        _drsStatus = null;
        DrsApplyButton.IsEnabled = false;
        DrsRestoreButton.IsEnabled = false;
        DrsStateText.Text = Str.T("Str.Restoring2");
        try
        {
            var exeName = Path.GetFileName(AppState.GamePath!);
            var restored = await Task.Run(() => DisplayQualityService.RemoveDlssOverride(exeName));
            _drsStatus = restored
                ? Str.T("Str.DrsRestored")
                : Str.T("Str.NothingToRestore");
        }
        catch (Exception ex)
        {
            _drsStatus = "还原失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }
    private async Task RefreshGpuIdentityAsync()
    {
        if (_busy) return;
        try
        {
            var devices = await Task.Run(GpuIdentityService.GetDevices);
            if (_busy) return;
            GpuIdentityDevices.ItemsSource = devices;
            GpuIdentityDevices.SelectedIndex = devices.Count > 0 ? 0 : -1;
            GpuIdentityApplyButton.IsEnabled = devices.Count > 0;
            GpuIdentityStatus.Text = devices.Count == 0 ? Str.T("Str.GpuIdentityUnavailable") : Str.T("Str.GpuIdentityReady");
        }
        catch (Exception ex) { GpuIdentityStatus.Text = ex.Message; GpuIdentityApplyButton.IsEnabled = false; }
    }

    private void GpuIdentityRefresh_Click(object sender, RoutedEventArgs e) => _ = RefreshGpuIdentityAsync();

    private bool CheckGpuIdentityPrerequisites()
    {
        if (_busy) return false;
        if (App.SessionService.IsRunning)
        { GpuIdentityStatus.Text = Str.T("Str.GpuIdentityCloseGame"); return false; }
        var path = AppState.GamePath;
        if (!string.IsNullOrEmpty(path))
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path));
            var running = processes.Length > 0;
            foreach (var process in processes) process.Dispose();
            if (running) { GpuIdentityStatus.Text = Str.T("Str.GpuIdentityCloseGame"); return false; }
        }
        if (!AdminHelper.IsAdministrator())
        {
            if (DialogService.Confirm(Str.T("Str.NeedsAdmin"), Str.T("Str.ConfirmRestartAdminRetry"), confirmText: Str.T("Str.RestartAsAdminShort")))
                AdminHelper.RestartAsAdministrator();
            return false;
        }
        return true;
    }

    private async void GpuIdentityApply_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckGpuIdentityPrerequisites() || GpuIdentityDevices.SelectedItem is not GpuIdentityTarget target) return;
        if (!DialogService.Confirm(Str.T("Str.GpuIdentityTitle"), Str.T("Str.GpuIdentityConfirm", target.DriverName))) return;
        _busy = true; GpuIdentityApplyButton.IsEnabled = false; GpuIdentityRestoreButton.IsEnabled = false;
        try { var result = await GpuIdentityService.ApplyAsync(target); GpuIdentityStatus.Text = result.Message; }
        catch (Exception ex) { GpuIdentityStatus.Text = ex.Message; }
        finally { _busy = false; GpuIdentityApplyButton.IsEnabled = true; GpuIdentityRestoreButton.IsEnabled = true; }
    }

    private async void GpuIdentityRestore_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckGpuIdentityPrerequisites()) return;
        _busy = true; GpuIdentityApplyButton.IsEnabled = false; GpuIdentityRestoreButton.IsEnabled = false;
        try
        {
            var result = await GpuIdentityService.RestoreAsync();
            GpuIdentityStatus.Text = result.Failures.Count > 0 ? string.Join("\n", result.Failures)
                : result.Restored.Count > 0 ? Str.T("Str.GpuIdentityRestored") : Str.T("Str.GpuIdentityNoBackup");
        }
        catch (Exception ex) { GpuIdentityStatus.Text = ex.Message; }
        finally { _busy = false; GpuIdentityApplyButton.IsEnabled = true; GpuIdentityRestoreButton.IsEnabled = true; }
    }

}
