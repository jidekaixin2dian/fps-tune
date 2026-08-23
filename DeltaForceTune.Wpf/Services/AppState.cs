using System.Text.Json.Nodes;

namespace DeltaForceTune.Wpf.Services;

public static class AppState
{
    public static JsonNode? DetectJson { get; set; }
    public static List<OptimizationItem> Items { get; set; } = new();
    public static string? GamePath { get; set; }
}

public sealed record OptimizationItem(
    string Id,
    string Description,
    bool RequiresAdmin = false,
    bool RequiresReboot = false);
