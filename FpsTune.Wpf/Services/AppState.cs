using System.Text.Json.Nodes;

namespace FpsTune.Wpf.Services;

public static class AppState
{
    public static JsonNode? DetectJson { get; set; }
    public static List<OptimizationItem> Items { get; set; } = new();
    public static string? GamePath { get; set; }

    /// <summary>0.2.0 M1：当前优化页/概览页勾选的优化项集合（A/B 页自定义实测的数据源）。</summary>
    public static HashSet<string> SelectedIds { get; } = new(StringComparer.Ordinal);
}

public sealed record OptimizationItem(
    string Id,
    string Name,
    string Description,
    string SideEffect,
    bool RequiresAdmin,
    bool RequiresReboot,
    bool Optimized,
    string Current,
    bool IsDefault,
    string Group);
