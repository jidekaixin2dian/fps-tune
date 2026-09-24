using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Core;

/// <summary>
/// 优化项分类的**显示标签**。
///
/// ⚠️ catalog 里的 <c>group</c> 值（键鼠 / 图形显示 / 网络 / 电源 / 系统与调度）是**稳定键**：
/// <c>OptimizeView</c> 的筛选 chip 用 XAML <c>Tag</c> 与 <c>OptimizationItemViewModel.Group</c>
/// 直接比较，条目分组也按它进行。**不要翻译 catalog 里的 group 值**，否则会静默破坏分类筛选。
/// 本类只提供「键 → 当前语言标签」的显示层映射。
/// </summary>
public static class CatalogGroups
{
    /// <summary>catalog 中出现过的全部分组键，顺序与界面筛选 chip 一致。</summary>
    public static readonly IReadOnlyList<string> Keys = new[]
    {
        "键鼠", "图形显示", "网络", "电源", "系统与调度",
    };

    private static readonly Dictionary<string, string> EnLabels = new(StringComparer.Ordinal)
    {
        ["键鼠"] = "Keyboard & mouse",
        ["图形显示"] = "Graphics",
        ["网络"] = "Network",
        ["电源"] = "Power",
        ["系统与调度"] = "System & scheduling",
    };

    /// <summary>
    /// 按当前界面语言取分组显示标签；未知键**原样返回**（不吞掉意外输入，便于发现 catalog 被改坏）。
    /// 与优化项文案同一套回退规则：非英文界面一律显示原文。
    /// </summary>
    public static string Display(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return key ?? "";

        if (LangService.Current == LangService.EnUs && EnLabels.TryGetValue(key, out var en))
            return en;

        return key;
    }
}
