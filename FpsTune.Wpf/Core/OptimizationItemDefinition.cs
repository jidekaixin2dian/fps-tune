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
    string Group);

/// <summary>
/// 优化项目录。实际数据来自 catalog/catalog.json（见 OptimizationCatalog），
/// 此类型仅作为既有调用点的兼容门面。
/// </summary>
public static class ItemCatalog
{
    public static IReadOnlyList<OptimizationItemDefinition> All => OptimizationCatalog.Items;
}
