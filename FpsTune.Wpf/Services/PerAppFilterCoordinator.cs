using System.IO;

namespace FpsTune.Wpf.Services;

/// <summary>状态机输出的动作，系统写入不在这个纯逻辑类里发生。</summary>
public enum PerAppFilterAction { None, Apply, Restore }

public sealed record PerAppFilterDecision(PerAppFilterAction Action, FilterAppRule? Rule = null);

/// <summary>独立游戏方案的显式方案定义：ICC 通道、预设调色 gamma、数字振动可独立组合。</summary>
public sealed record PerAppFilterPlan(string Identity, FilterAppRule Rule)
{
    internal bool HasIcc { get; init; } = true;
    internal bool HasScreen => Rule.IncludeScreen;
    internal int? Vibrance => Rule.Vibrance;

    internal static PerAppFilterPlan Create(string identity, FilterAppRule rule)
    {
        if (string.IsNullOrWhiteSpace(identity)) throw new InvalidOperationException(Str.T("Str.DisplayDisconnected"));
        if (!rule.IncludeScreen && rule.Vibrance is null && string.IsNullOrWhiteSpace(rule.PresetFileName))
            throw new InvalidOperationException(Str.T("Str.FilterRuleNeedPreset"));
        return new(identity, rule);
    }

    internal (IccFilterPreset? Preset, string? File) ResolveIcc()
    {
        if (!HasIcc || string.IsNullOrWhiteSpace(Rule.PresetFileName)) return (null, null);
        if (Rule.PresetFileName.StartsWith(GameFilterLibrary.GeneratedPrefix, StringComparison.Ordinal))
        {
            var key = Rule.PresetFileName[GameFilterLibrary.GeneratedPrefix.Length..];
            if (!Enum.TryParse<IccFilterPreset>(key, ignoreCase: false, out var preset))
                throw new InvalidOperationException(Str.T("Str.FilterPresetMissing", Rule.PresetFileName));
            return (preset, null);
        }
        // 复用 ScreenColorPreview 的有界缓存，不再每 500ms 产生一个临时 ICC。
        return (null, ScreenColorPreview.CacheIccProfile(GameFilterLibrary.ReadPreset(Rule.PresetFileName)));
    }
}

/// <summary>每次前台采样产生目标或 null；纯 C#，可用假时钟测抖动与规则切换。</summary>
public sealed class PerAppFilterStateMachine
{
    public int EnterSamples { get; init; } = 2;
    public int ExitSamples { get; init; } = 2;
    private string? _candidate;
    private int _count;
    private FilterAppRule? _current;
    private string? _identity;

    public PerAppFilterDecision Update(FilterAppRule? rule, string? identity)
    {
        var target = rule is null || identity is null ? null : rule.Key + "|" + identity;
        if (target != _candidate) { _candidate = target; _count = 0; }
        if (target is not null && _current is not null && _current.Key == rule!.Key && _identity == identity)
        {
            _count = 0;
            return new(PerAppFilterAction.None, _current);
        }
        if (target is null && _current is null) return new(PerAppFilterAction.None);
        _count++;
        var threshold = _current is null ? EnterSamples : ExitSamples;
        if (_count < Math.Max(1, threshold)) return new(PerAppFilterAction.None, _current);
        _count = 0;
        if (target is null)
        {
            var restore = new PerAppFilterDecision(PerAppFilterAction.Restore, _current);
            _current = null; _identity = null;
            return restore;
        }
        _current = rule; _identity = identity;
        return new(PerAppFilterAction.Apply, rule);
    }

    public void Reset()
    {
        _candidate = null; _count = 0; _current = null; _identity = null;
    }
}

/// <summary>
/// 自动方案在首次写入前落恢复凭证，再委托 ColorWorkspaceService。
/// 正常手动滤镜没有被本守护接管时，不会被恢复；凭证只在自动方案已进入时存在。
/// </summary>
internal static class PerAppFilterExecution
{
    internal static Func<string>? PendingPathOverride { get; set; }
    private sealed record Pending(int SchemaVersion, string Identity, string ProcessName, string Preset, DateTime CreatedAt);
    private static string PendingPath => PendingPathOverride?.Invoke()
        ?? Path.Combine(UserDataPaths.Root, "game-filters", "perapp-pending.json");

    public static string Apply(string identity, FilterAppRule rule)
    {
        var plan = PerAppFilterPlan.Create(identity, rule);
        var (preset, file) = plan.ResolveIcc();
        var settings = preset is { } p ? ScreenColorSettings.ForPreset(p) : new ScreenColorSettings();
        // 凭证先持久化，标明接管了这块屏；实际备份仍由底层三条既有服务负责。
        WritePending(new Pending(1, identity, rule.ProcessName, rule.PresetFileName, DateTime.Now));
        try
        {
            // 自动方案绝不接管用户已有基线；外部漂移时让既有服务拒绝写入并保留凭证。
            return ColorWorkspaceService.Apply(identity, settings, preset is not null || file is not null,
                preset, file, rule.Vibrance, applyScreen: rule.IncludeScreen, startNewBaselineOnDrift: false);
        }
        catch
        {
            // 部分通道成功时保留凭证；下次 Poll 重试/退出时继续走底层精确还原。
            throw;
        }
    }

    public static string Restore(string identity)
        => ColorWorkspaceService.Restore(identity);

    public static void MarkRestored()
    {
        try { if (File.Exists(PendingPath)) File.Delete(PendingPath); }
        catch (IOException) { /* 凭证保留比误报已还原更可靠；下次启动继续检查。 */ }
    }

    /// <summary>启动时先处理上次未正常退出的自动方案，再开启前台跟随。</summary>
    public static bool RestoreOnStartup()
    {
        Pending? pending;
        try
        {
            if (!File.Exists(PendingPath) || new FileInfo(PendingPath).Length > 64 * 1024) return false;
            pending = System.Text.Json.JsonSerializer.Deserialize<Pending>(File.ReadAllText(PendingPath));
            if (pending is null || pending.SchemaVersion != 1 || string.IsNullOrWhiteSpace(pending.Identity)) return false;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return false;
        }
        Restore(pending.Identity);
        MarkRestored();
        return true;
    }

    private static void WritePending(Pending pending)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PendingPath)!);
        AtomicFile.WriteAllTextDurable(PendingPath,
            System.Text.Json.JsonSerializer.Serialize(pending), new System.Text.UTF8Encoding(false));
    }
}
