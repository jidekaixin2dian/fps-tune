using System.Text.Json.Nodes;

namespace FpsTune.Wpf.Services;

public static class AppState
{
    public static JsonNode? DetectJson { get; set; }
    public static List<OptimizationItem> Items { get; set; } = new();
    public static string? GamePath { get; set; }
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
    bool IsDefault);
