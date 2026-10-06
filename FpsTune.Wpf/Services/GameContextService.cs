using System.IO;

namespace FpsTune.Wpf.Services;

/// <summary>A/B 页/概览页游戏切换器的条目（Label 直接展示）。</summary>
public sealed record GameSwitcherItem(string Label, string? ProfileId, string? ExePath)
{
    public override string ToString() => Label;
}

/// <summary>
/// 0.2.0 M2：游戏上下文服务——切换的唯一入口。概览页切换器、定位流程、
/// AutoProfile 启动边沿都走这里；切换完成后广播 GameSwitched（UI 线程），
/// 各页面自行重建。忙检：检测进行中拒绝切换，防止把结果算到别的游戏头上。
/// </summary>
public static class GameContextService
{
    /// <summary>切换完成后触发（保证 UI 线程调用）。</summary>
    public static event Action? GameSwitched;

    /// <summary>由 MainWindow 注入：检测页是否正在检测（忙检数据源）。</summary>
    public static Func<bool>? IsBusyProvider { get; set; }

    /// <summary>最近一次切换是否需要补检测（快照缺失且调用方要求补测）；
    /// 由 MainWindow 消费后清除。</summary>
    public static bool NeedsDetection { get; private set; }

    public static bool IsBusy => IsBusyProvider?.Invoke() == true;

    public static void ClearNeedsDetection() => NeedsDetection = false;

    /// <summary>切换到目标游戏（空路径清除当前选择）。返回 false = 找不到档案 / 检测进行中被拒；
    /// 已是当前游戏时返回 true 且不做任何事。</summary>
    public static bool SwitchTo(string? exePath, bool detectIfMissing = false)
    {
        if (IsBusy)
            return false;
        var profile = string.IsNullOrWhiteSpace(exePath) ? null : StateStore.FindGameByPath(exePath);
        if (!string.IsNullOrWhiteSpace(exePath) && profile is null)
            return false;
        if (string.Equals(AppState.GamePath, profile?.ExePath, StringComparison.OrdinalIgnoreCase))
            return true;

        StateStore.SaveGamePath(profile?.ExePath);
        AppState.GamePath = profile?.ExePath;
        var root = profile is null ? null : StateStore.LoadDetectForGame(profile.Id);
        AppState.DetectJson = root;
        AppState.Items = root is null ? new List<OptimizationItem>() : DetectionData.ParseItems(root);
        NeedsDetection = root is null && detectIfMissing;
        GameSwitched?.Invoke();
        return true;
    }

    /// <summary>切换器条目：游戏档案（按名排序）+ 末尾固定的「添加游戏」占位。</summary>
    public static IReadOnlyList<GameSwitcherItem> BuildSwitcherItems()
    {
        var items = StateStore.LoadGames()
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GameSwitcherItem(g.Name, g.Id, g.ExePath))
            .ToList();
        items.Add(new GameSwitcherItem(Str.T("Str.GameSwitcherAdd"), null, null));
        return items;
    }

    /// <summary>当前选中项在切换器里的 ExePath 匹配（无匹配返回 null）。</summary>
    public static GameSwitcherItem? SelectedItem(IReadOnlyList<GameSwitcherItem> items)
        => items.FirstOrDefault(v => v.ExePath is not null
            && string.Equals(v.ExePath, AppState.GamePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>文件选择器添加游戏（校验 .exe；取消返回 null）。</summary>
    public static StateStore.GameProfile? PickAndAddGame(System.Windows.Window? owner)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = Str.T("Str.PickGameExeTitle"),
            Filter = Str.T("Str.PickGameExeFilter"),
            CheckFileExists = true,
        };
        if (picker.ShowDialog(owner) != true)
            return null;
        var path = picker.FileName;
        if (!File.Exists(path) || !string.Equals(System.IO.Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            FpsTune.Wpf.Services.DialogService.Warning(Str.T("Str.PickGameExeTitle"), Str.T("Str.MustPickGameExe"));
            return null;
        }
        return StateStore.AddGame(path);
    }
}
