using System.Globalization;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Core;

/// <summary>一次取值写入的结果。AC/DC 分开记账：一边失败不能把另一边说成没写。</summary>
internal sealed record PowerWriteResult(
    bool AcRequested,
    bool DcRequested,
    bool AcPersisted,
    bool DcPersisted,
    string? AcError,
    string? DcError,
    bool Applied)
{
    internal bool Ok => AcError is null && DcError is null;
    internal bool AnyPersisted => AcPersisted || DcPersisted;
    internal bool Partial => !Ok && AnyPersisted;
}

/// <summary>「恢复本次改动」的结果；失败的项留在 journal 里，下次还能再试。</summary>
internal sealed record PowerRestoreResult(int Restored, int Failed, int Skipped, IReadOnlyList<string> Errors, bool Applied)
{
    internal bool Ok => Failed == 0 && Skipped == 0;
}

internal sealed record PowerRevealResult(int Revealed, int Denied, int Missing, int Failed, IReadOnlyList<string> Errors)
{
    internal int Total => Revealed + Denied + Missing + Failed;
}

/// <summary>
/// 电源设置写入：只写**当前活动方案**，写后读回复验；显隐（PowerSettings 的 Attributes）
/// 也在这里，因为两者都属于「改系统状态」的同一层。
///
/// 三个刻意的取舍：
/// 1. 非活动方案直接拒绝——参考实现同样只改活动方案，悄悄改别的方案会让用户莫名其妙丢配置。
/// 2. DC 写失败不回滚已成功的 AC：如实报「部分成功」，回滚反而会让用户以为没改过。
/// 3. 显隐先比对内存里的现有状态，已经在目标状态就不碰 HKLM（每次写系统级键都要提权）。
/// </summary>
internal static class PowerSettingWriter
{
    internal static Func<Guid?>? ActiveSchemeOverride { get; set; }

    internal static Guid? ActiveScheme()
    {
        if (ActiveSchemeOverride is not null) return ActiveSchemeOverride();
        var text = NativeSystem.GetActivePowerSchemeGuid();
        return Guid.TryParse(text, out var scheme) ? scheme : null;
    }

    /// <summary>写入一项设置的两套取值（null 表示这一套不动）。</summary>
    internal static PowerWriteResult Apply(Guid scheme, PowerSettingEntry entry, uint? ac, uint? dc)
    {
        RequireActiveScheme(scheme);
        if (!ac.HasValue && !dc.HasValue) throw new InvalidOperationException("No value was requested.");
        var api = PowerSettingCatalog.Api;

        var beforeAc = Read(api, scheme, entry, true);
        var beforeDc = Read(api, scheme, entry, false);
        PowerDetailsJournal.Record(scheme, entry, beforeAc, beforeDc);

        var acPersisted = false;
        var dcPersisted = false;
        string? acError = null;
        string? dcError = null;

        if (ac.HasValue)
        {
            var error = api.WriteValue(scheme, entry.Subgroup, entry.Setting, true, ac.Value);
            if (error != 0) acError = DescribeFailure("AC", error);
            else if (Read(api, scheme, entry, true) != ac.Value) acError = Str.T("Str.PowerDetailsNotPersisted", DisplayName(entry));
            else acPersisted = true;
        }

        if (dc.HasValue)
        {
            var error = api.WriteValue(scheme, entry.Subgroup, entry.Setting, false, dc.Value);
            if (error != 0) dcError = DescribeFailure("DC", error);
            else if (Read(api, scheme, entry, false) != dc.Value) dcError = Str.T("Str.PowerDetailsNotPersisted", DisplayName(entry));
            else dcPersisted = true;
        }

        var applied = true;
        if (acPersisted || dcPersisted)
            applied = api.SetActiveScheme(scheme) == 0;

        return new PowerWriteResult(ac.HasValue, dc.HasValue, acPersisted, dcPersisted, acError, dcError, applied);
    }

    /// <summary>把 journal 里的快照写回去。其他方案上的快照跳过（本机只允许改活动方案）。</summary>
    internal static PowerRestoreResult Restore(IReadOnlyList<PowerDetailsJournalEntry> entries)
    {
        if (entries.Count == 0) return new PowerRestoreResult(0, 0, 0, [], true);

        var active = ActiveScheme();
        var api = PowerSettingCatalog.Api;
        var restored = 0;
        var failed = 0;
        var skipped = 0;
        var errors = new List<string>();
        var applied = true;

        foreach (var entry in entries)
        {
            if (!Guid.TryParse(entry.Scheme, out var scheme) || !Guid.TryParse(entry.Subgroup, out var subgroup)
                || !Guid.TryParse(entry.Setting, out var setting))
            {
                skipped++;
                errors.Add(entry.Name);
                continue;
            }

            if (active != scheme)
            {
                skipped++;
                errors.Add(entry.Name);
                continue;
            }

            var ok = true;
            if (entry.Ac is uint ac)
            {
                var error = api.WriteValue(scheme, subgroup, setting, true, ac);
                if (error != 0 || ReadRaw(api, scheme, subgroup, setting, true) != ac)
                {
                    ok = false;
                    errors.Add(entry.Name);
                }
            }

            if (entry.Dc is uint dc)
            {
                var error = api.WriteValue(scheme, subgroup, setting, false, dc);
                if (error != 0 || ReadRaw(api, scheme, subgroup, setting, false) != dc)
                {
                    ok = false;
                    errors.Add(entry.Name);
                }
            }

            if (!ok)
            {
                failed++;
                continue;
            }

            restored++;
            PowerDetailsJournal.Remove(scheme, subgroup, setting);
        }

        if (restored > 0) applied = api.SetActiveScheme(active!.Value) == 0;

        return new PowerRestoreResult(restored, failed, skipped, errors, applied && restored > 0);
    }

    /// <summary>单项显隐。已经在目标状态就不写注册表。</summary>
    internal static PowerVisibilityWrite SetVisibility(PowerSettingEntry entry, bool visible)
    {
        if (visible == !entry.Hidden) return new PowerVisibilityWrite(PowerVisibilityStatus.Unchanged, null);

        if (!visible)
        {
            if (entry.HiddenByItem) return new PowerVisibilityWrite(PowerVisibilityStatus.Unchanged, null);
            return PowerSettingCatalog.WriteRegistry(entry.Subgroup, entry.Setting, false);
        }

        if (entry.HiddenByGroup)
        {
            var group = PowerSettingCatalog.WriteRegistry(entry.Subgroup, null, true);
            if (!group.Ok) return group;
        }

        return entry.HiddenByItem
            ? PowerSettingCatalog.WriteRegistry(entry.Subgroup, entry.Setting, true)
            : new PowerVisibilityWrite(PowerVisibilityStatus.Updated, null);
    }

    /// <summary>
    /// 「让隐藏项全部显示」：只处理当前隐藏的项，逐项记账；分组级隐藏先解分组，
    /// 分组解锁失败时同组各项不再逐个尝试（它们一定也失败）。
    /// </summary>
    internal static PowerRevealResult RevealAllHidden(IReadOnlyList<PowerSettingEntry> entries)
    {
        var hidden = entries.Where(entry => entry.Hidden).ToList();
        if (hidden.Count == 0) return new PowerRevealResult(0, 0, 0, 0, []);

        var revealed = 0;
        var denied = 0;
        var missing = 0;
        var failed = 0;
        var errors = new List<string>();
        var groups = new Dictionary<Guid, PowerVisibilityWrite>();

        foreach (var entry in hidden)
        {
            PowerVisibilityWrite? groupResult = null;
            if (entry.HiddenByGroup)
            {
                if (!groups.TryGetValue(entry.Subgroup, out var cached))
                {
                    cached = PowerSettingCatalog.WriteRegistry(entry.Subgroup, null, true);
                    groups[entry.Subgroup] = cached;
                }
                groupResult = cached;
            }

            if (groupResult is { Ok: false } blocked)
            {
                Count(blocked.Status, entry, ref denied, ref missing, ref failed, errors);
                continue;
            }

            var result = entry.HiddenByItem
                ? PowerSettingCatalog.WriteRegistry(entry.Subgroup, entry.Setting, true)
                : new PowerVisibilityWrite(PowerVisibilityStatus.Updated, null);

            if (result.Ok) revealed++;
            else Count(result.Status, entry, ref denied, ref missing, ref failed, errors);
        }

        return new PowerRevealResult(revealed, denied, missing, failed, errors);
    }

    private static void Count(
        PowerVisibilityStatus status, PowerSettingEntry entry,
        ref int denied, ref int missing, ref int failed, List<string> errors)
    {
        switch (status)
        {
            case PowerVisibilityStatus.Denied:
                denied++;
                break;
            case PowerVisibilityStatus.Missing:
                missing++;
                break;
            default:
                failed++;
                break;
        }
        errors.Add(DisplayName(entry));
    }

    private static void RequireActiveScheme(Guid scheme)
    {
        var active = ActiveScheme();
        if (active is null) throw new InvalidOperationException("The active power scheme could not be read.");
        if (active.Value != scheme) throw new InvalidOperationException("Only the active power scheme can be written.");
    }

    private static uint? Read(IPowerSettingsApi api, Guid scheme, PowerSettingEntry entry, bool ac)
        => ReadRaw(api, scheme, entry.Subgroup, entry.Setting, ac);

    private static uint? ReadRaw(IPowerSettingsApi api, Guid scheme, Guid subgroup, Guid setting, bool ac)
    {
        try
        {
            return api.ReadValue(scheme, subgroup, setting, ac, out var value) == 0 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string DisplayName(PowerSettingEntry entry)
        => string.IsNullOrWhiteSpace(entry.Name) ? entry.Setting.ToString("D") : entry.Name;

    private static string DescribeFailure(string side, uint error)
        => side + " write failed (" + error.ToString(CultureInfo.InvariantCulture) + ").";
}
