using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Core;

public sealed record OptimizationItemDefinition(
    string Id,
    string Name,
    string Description,
    string SideEffect,
    bool Admin,
    bool Default,
    bool Reboot,
    string Kind,
    string Group,
    string? NameEn = null,
    string? DescriptionEn = null,
    string? SideEffectEn = null)
{
    /// <summary>按当前界面语言取显示名（英文缺失回退中文，绝不返回空串）。</summary>
    public string DisplayName => Pick(Name, NameEn);

    /// <summary>按当前界面语言取显示说明。</summary>
    public string DisplayDescription => Pick(Description, DescriptionEn);

    /// <summary>按当前界面语言取副作用说明。</summary>
    public string DisplaySideEffect => Pick(SideEffect, SideEffectEn);

    /// <summary>
    /// 语言回退：只有界面语言为英文**且**英文文案非空时才用英文，否则一律用中文。
    /// 宁可露出中文，也不要显示空白条目。
    ///
    /// 注意：CLI 分支在 <c>LangService.Load()</c> 之前就从 <c>App.OnStartup</c> 返回，
    /// 所以 CLI 进程里 <c>LangService.Current</c> 恒为 zh-CN —— detect JSON 的输出
    /// 与用户的语言设置无关，机器协议保持稳定。改动此处前先读
    /// <c>docs/dev/PLAN-P2-1-catalog-i18n.md</c>。
    /// </summary>
    private static string Pick(string zh, string? en)
        => LangService.Current == LangService.EnUs && !string.IsNullOrWhiteSpace(en) ? en! : zh;
}

/// <summary>
/// 优化项目录。实际数据来自 catalog/catalog.json（见 OptimizationCatalog），
/// 此类型仅作为既有调用点的兼容门面。
/// </summary>
public static class ItemCatalog
{
    public static IReadOnlyList<OptimizationItemDefinition> All => OptimizationCatalog.Items;
}
