using System.IO;
using System.Text.Json;

namespace FpsTune.Wpf.Core;

/// <summary>
/// 优化项与预设的唯一数据源：catalog/catalog.json。
/// 检测/应用/还原引擎与 CLI 均消费同一份文件；
/// 引用 catalog 中不存在的 id 会在启动时立即失败。
/// </summary>
public static class OptimizationCatalog
{
    private static readonly object Gate = new();
    private static CatalogData? _cache;

    private sealed class CatalogData
    {
        public List<OptimizationItemDefinition> Items { get; init; } = new();
        public List<string> Order { get; init; } = new();
        public JsonElement Presets { get; init; }
    }

    public static IReadOnlyList<OptimizationItemDefinition> Items
    {
        get { return Ensure().Items; }
    }

    public static IReadOnlyList<string> ItemOrder
    {
        get { return Ensure().Order; }
    }

    /// <summary>catalog 中定义的预设名（供 CLI 校验与帮助信息使用）。</summary>
    public static IReadOnlyList<string> PresetNames
    {
        get
        {
            var data = Ensure();
            var names = new List<string>();
            if (data.Presets.ValueKind == JsonValueKind.Object)
                names.AddRange(data.Presets.EnumerateObject().Select(p => p.Name));
            return names;
        }
    }

    /// <summary>
    /// 预设 -> 优化项 id 列表。"full" 返回全部；其余按 catalog.presets 的
    /// include/exclude 解析；未知预设回退到 balanced（保持既有 GUI 约定），
    /// 但回退只允许一次——balanced 本身缺失或格式非法时明确报错，
    /// 绝不无限递归（否则外部 catalog.json 被改坏会让进程 StackOverflow 崩溃）。
    /// </summary>
    public static IReadOnlyList<string> ResolvePreset(string name)
    {
        return ResolveFrom(Ensure(), name);
    }

    /// <summary>测试入口：直接解析给定的 catalog JSON，绕过单例缓存。</summary>
    internal static IReadOnlyList<string> ResolvePresetFromJson(string json, string name)
    {
        return ResolveFrom(Parse(json), name);
    }

    private static IReadOnlyList<string> ResolveFrom(CatalogData data, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "full")
            return data.Order.ToList();

        // 兼容旧约定：未知预设回退 balanced，但只回退一次。
        return ResolveNamed(data, name)
               ?? ResolveNamed(data, "balanced")
               ?? throw new InvalidOperationException(
                   "catalog.json 预设无法解析: " + name +
                   "（回退预设 balanced 也缺失或格式非法：需为对象且含 include 或 exclude 数组）");
    }

    /// <summary>解析具名预设；不存在或格式非法（无 include/exclude）返回 null。</summary>
    private static IReadOnlyList<string>? ResolveNamed(CatalogData data, string name)
    {
        if (data.Presets.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var p in data.Presets.EnumerateObject())
        {
            if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (p.Value.ValueKind != JsonValueKind.Object)
                return null;

            var obj = p.Value;
            if (obj.TryGetProperty("include", out var inc) && inc.ValueKind == JsonValueKind.Array)
                return inc.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();

            if (obj.TryGetProperty("exclude", out var exc) && exc.ValueKind == JsonValueKind.Array)
            {
                var exclude = exc.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet(StringComparer.Ordinal);
                return data.Order.Where(id => !exclude.Contains(id)).ToList();
            }

            return null;
        }

        return null;
    }

    private static CatalogData Ensure()
    {
        if (_cache is not null)
            return _cache;
        lock (Gate)
        {
            if (_cache is not null)
                return _cache;

            _cache = Parse(LoadJson());
            return _cache;
        }
    }

    private static CatalogData Parse(string json)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var parsed = JsonSerializer.Deserialize<CatalogFile>(json, opts)
                     ?? throw new InvalidOperationException("catalog.json 解析结果为空");

        if (parsed.Items is null || parsed.Items.Count == 0)
            throw new InvalidOperationException("catalog.json 未包含任何优化项");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var it in parsed.Items)
        {
            if (string.IsNullOrWhiteSpace(it.Id))
                throw new InvalidOperationException("catalog.json 存在空 id");
            if (!seen.Add(it.Id))
                throw new InvalidOperationException($"catalog.json 中 id 重复: {it.Id}");
        }

        // 预设名是 GUI/CLI 的固定契约（未知预设回退 balanced 一次），
        // 这两个名字缺失说明 catalog 被改坏，启动即报错比静默回退更诚实。
        var presetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (parsed.Presets is { ValueKind: JsonValueKind.Object } presets)
            presetNames.UnionWith(presets.EnumerateObject().Select(p => p.Name));
        foreach (var required in new[] { "balanced", "safe-only" })
        {
            if (!presetNames.Contains(required))
                throw new InvalidOperationException($"catalog.json 缺少必需预设: {required}");
        }

        return new CatalogData
        {
            Items = parsed.Items,
            Order = parsed.Items.Select(x => x.Id).ToList(),
            Presets = parsed.Presets ?? default,
        };
    }

    private static string LoadJson()
    {
        // 1) 外部文件（绿色目录 / 开发输出），便于不重编译即可调整文案
        var external = Path.Combine(AppContext.BaseDirectory, "catalog.json");
        if (File.Exists(external))
            return File.ReadAllText(external);

        // 2) 嵌入资源（单文件发布）
        var asm = typeof(OptimizationCatalog).Assembly;
        using var stream = asm.GetManifestResourceStream("FpsTune.Wpf.Catalog.catalog.json");
        if (stream is null)
            throw new FileNotFoundException(
                "未找到 catalog.json：既无外部文件也无嵌入资源 " +
                "(FpsTune.Wpf.Catalog.catalog.json)");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class CatalogFile
    {
        public List<OptimizationItemDefinition>? Items { get; set; }
        public JsonElement? Presets { get; set; }
    }
}
