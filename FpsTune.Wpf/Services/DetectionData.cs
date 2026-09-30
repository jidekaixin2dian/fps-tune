using System.Text.Json.Nodes;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 0.2.0 M2：检测结果 JSON → 应用状态的纯解析（从 DetectView.ApplyDetectData 抽出共用）。
/// 概览页切游戏时要"不经过检测页 UI"把逐游戏快照灌回 AppState，与检测页走同一套字段口径。
/// </summary>
public static class DetectionData
{
    public static List<OptimizationItem> ParseItems(JsonObject root)
    {
        var detectedItems = new List<OptimizationItem>();
        if (root["items"] is not JsonArray items)
            return detectedItems;

        foreach (var item in items)
        {
            var id = item?["id"]?.GetValue<string>() ?? "";
            var name = item?["name"]?.GetValue<string>() ?? "";
            var desc = item?["desc"]?.GetValue<string>()
                ?? item?["description"]?.GetValue<string>()
                ?? "";
            var sideEffect = item?["sideEffect"]?.GetValue<string>() ?? "";
            var admin = item?["requiresAdmin"]?.GetValue<bool>()
                ?? item?["needsAdmin"]?.GetValue<bool>()
                ?? item?["admin"]?.GetValue<bool>()
                ?? false;
            var reboot = item?["requiresReboot"]?.GetValue<bool>()
                ?? item?["needsReboot"]?.GetValue<bool>()
                ?? item?["reboot"]?.GetValue<bool>()
                ?? false;
            var optimized = item?["optimized"]?.GetValue<bool>() ?? false;
            var current = item?["current"]?.GetValue<string>() ?? "";
            var isDefault = item?["default"]?.GetValue<bool>() ?? false;
            var group = item?["group"]?.GetValue<string>() ?? "";
            detectedItems.Add(new OptimizationItem(id, name, desc, sideEffect, admin, reboot, optimized, current, isDefault, group));
        }
        return detectedItems;
    }
}
