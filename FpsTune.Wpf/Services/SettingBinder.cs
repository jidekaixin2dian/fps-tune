namespace FpsTune.Wpf.Services;

/// <summary>一次设置保存的结果；<see cref="Detail"/> 是已脱敏的失败原因，成功时为 null。</summary>
internal readonly record struct SettingSaveResult(bool Ok, string? Detail)
{
    internal static readonly SettingSaveResult Saved = new(true, null);
}

/// <summary>
/// 设置页「改内存 → 保存 → 失败回滚」的唯一入口。
/// <para>
/// 设置页里每个开关都长一个样：把控件的新值写进 <see cref="SettingsService.Current"/>、
/// 调 <see cref="SettingsService.Save"/>、失败时把内存和控件一起翻回旧值并说明原因。
/// 这段样板原来在每个处理器里抄一遍（正文 20 行、失败分支 8 行），抄漏一步就会出现
/// 「界面显示已开启、磁盘上还是关」的状态。这里只留三件事：执行改动、捕获异常、还原。
/// </para>
/// <para>
/// 不碰控件、不弹窗、不写日志：界面侧的回滚与提示由调用方决定（见 SettingsView 的 Save* helpers），
/// 这样这个类可以脱离 WPF 直接单测。
/// </para>
/// </summary>
internal static class SettingBinder
{
    /// <summary>执行一次可能失败的改动；抛异常时调用 <paramref name="undo"/> 还原并返回失败。</summary>
    internal static SettingSaveResult Attempt(Action change, Action? undo = null)
    {
        try
        {
            change();
            return SettingSaveResult.Saved;
        }
        catch (Exception ex)
        {
            if (undo is not null)
            {
                // 回滚本身失败也不能把原始失败盖掉：原始失败才是要说给用户听的那一个。
                try { undo(); } catch (Exception) { /* 回滚失败只丢内存态，不阻断提示 */ }
            }
            return new SettingSaveResult(false, PrivacyScrub.Sanitize(ex.Message));
        }
    }

    /// <summary>改内存 → 保存到 settings.json；失败回滚内存。<paramref name="undo"/> 负责把字段写回旧值。</summary>
    internal static SettingSaveResult Persist(Action change, Action undo)
        => Attempt(() =>
        {
            change();
            SettingsService.Save(SettingsService.Current);
        }, undo);

    /// <summary>单字段布尔开关：读旧值 → 写新值 → 保存；失败把旧值写回。</summary>
    internal static SettingSaveResult PersistBool(Func<AppSettings, bool> read, Action<AppSettings, bool> write, bool wanted)
    {
        var settings = SettingsService.Current;
        var previous = read(settings);
        return Persist(() => write(settings, wanted), () => write(settings, previous));
    }

    /// <summary>单字段字符串/枚举开关（主题、关闭去向这类）：同上，回滚到旧值。</summary>
    internal static SettingSaveResult PersistValue<T>(Func<AppSettings, T> read, Action<AppSettings, T> write, T wanted)
    {
        var settings = SettingsService.Current;
        var previous = read(settings);
        return Persist(() => write(settings, wanted), () => write(settings, previous));
    }
}
