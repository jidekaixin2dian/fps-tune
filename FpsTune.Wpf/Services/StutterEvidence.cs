using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using FpsTune.Wpf.Core;
using Microsoft.Win32;

namespace FpsTune.Wpf.Services;

/// <summary>一次卡顿现场取证快照。GPU 读数、后台占用与 HAGS/MPO 状态都是快照时刻的实况。</summary>
public sealed record StutterEvidenceSnapshot(
    string GameName,
    DateTimeOffset At,
    IReadOnlyList<double> StutterFramesMs,
    double MedianMs,
    double Fps,
    double P1Low,
    string? GpuName,
    double? GpuUtilizationPercent,
    double? GpuMemoryPercent,
    double? GpuTemperatureC,
    double? GpuPowerW,
    IReadOnlyList<string> BackgroundTop,
    string? HagsState,
    string? MpoState);

internal sealed record StutterEvidenceHistory(IReadOnlyList<StutterEvidenceSnapshot> Records, int Skipped, bool Unavailable);

/// <summary>
/// 卡顿现场取证（0.5.0 M4）：把卡顿点附近的帧时间、当时的显卡读数、后台占用 Top3 与
/// HAGS/MPO 状态写进用户数据目录，供回看。只读采集，不触碰系统设置。
/// </summary>
public static class StutterEvidence
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>取证 JSON 的输出目录。</summary>
    public static string EvidenceRoot => Path.Combine(UserDataPaths.Root, "stutter");

    /// <summary>测试注入点：后台 Top（默认走 BackgroundResourceCheck 的 3 秒窗口）。</summary>
    internal static Func<IReadOnlyList<string>>? BackgroundTopOverride;
    /// <summary>测试注入点：显卡读数（默认 GpuTelemetryService.Read）。</summary>
    internal static Func<GpuTelemetrySnapshot>? GpuReadOverride;
    /// <summary>测试注入点：输出目录。</summary>
    internal static string? EvidenceRootOverride;

    /// <summary>拍下现场：采集并落盘，返回 JSON 路径；失败返回 null（调用方给文案）。</summary>
    public static async Task<string?> CaptureAsync(StutterEvidenceContext context)
    {
        try
        {
            var gpu = GpuReadOverride?.Invoke() ?? GpuTelemetryService.Read();
            var background = BackgroundTopOverride?.Invoke() ?? await CollectBackgroundAsync();
            var reading = gpu.Reading;
            var snapshot = new StutterEvidenceSnapshot(
                context.GameName, DateTimeOffset.Now,
                context.StutterFramesMs, context.MedianMs, context.Fps, context.P1Low,
                reading?.Name,
                reading?.UtilizationPercent,
                reading?.MemoryPercent,
                reading?.TemperatureC,
                reading?.PowerW,
                background,
                ReadHags(), ReadMpo());
            var directory = EvidenceRootOverride ?? EvidenceRoot;
            Directory.CreateDirectory(directory);
            var stamp = snapshot.At.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var path = Path.Combine(directory, $"stutter-{stamp}-{Guid.NewGuid():N}.json");
            AtomicFile.WriteAllTextDurable(path, JsonSerializer.Serialize(snapshot, JsonOpts), new UTF8Encoding(false));
            return path;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("stutter-evidence", ex);
            return null;
        }
    }

    internal const int HistoryLimit = 30;
    private const int MaxSnapshotBytes = 64 * 1024;

    internal static StutterEvidenceHistory ReadHistory(CancellationToken cancellation = default)
    {
        var directory = EvidenceRootOverride ?? EvidenceRoot;
        try
        {
            var root = new DirectoryInfo(directory);
            var attributes = File.GetAttributes(directory);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                return new([], 0, true);

            var recent = new PriorityQueue<StutterEvidenceSnapshot, DateTimeOffset>();
            var skipped = 0;
            var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            foreach (var file in root.EnumerateFiles("stutter-*.json", options))
            {
                cancellation.ThrowIfCancellationRequested();
                if (ReadSnapshot(file.FullName) is { } record)
                {
                    recent.Enqueue(record, record.At);
                    if (recent.Count > HistoryLimit) recent.Dequeue();
                }
                else
                    skipped++;
            }
            return new(recent.UnorderedItems.OrderByDescending(item => item.Priority)
                .Select(item => item.Element).ToArray(), skipped, false);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { return new([], 0, false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new([], 0, true);
        }
    }

    private static StutterEvidenceSnapshot? ReadSnapshot(string path)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > MaxSnapshotBytes)
                return null;
            var snapshot = JsonSerializer.Deserialize<StutterEvidenceSnapshot>(input, JsonOpts);
            return snapshot is { StutterFramesMs: not null, BackgroundTop: not null }
                && snapshot.At != default ? snapshot : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<string>> CollectBackgroundAsync()
    {
        try
        {
            var result = await BackgroundResourceCheck.RunAsync(CancellationToken.None).ConfigureAwait(false);
            return result.TopCpu.Take(3)
                .Select(s => $"{s.Name} {s.CpuPercent.ToString("0", CultureInfo.InvariantCulture)}%")
                .ToList();
        }
        catch
        {
            return []; // 后台占用拿不到就留空，不阻塞取证。
        }
    }

    /// <summary>HAGS：HKLM GraphicsDrivers\HwSchMode；1=关、2=开、读不到为 null。</summary>
    internal static string? ReadHags() => ReadState(
        @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode",
        value => value switch { 2 => "on", 1 => "off", _ => null });

    /// <summary>MPO：HKLM DWM\OverlayTestMode；5=已关闭覆盖、读不到/其他为 null（默认行为）。</summary>
    internal static string? ReadMpo() => ReadState(
        @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode",
        value => value == 5 ? "off" : null);

    private static string? ReadState(string keyPath, string valueName, Func<uint, string?> describe)
    {
        try
        {
            var value = RegistryHelper.ReadValue(RegistryHive.LocalMachine, keyPath, valueName);
            if (value is uint numeric) return describe(numeric);
            if (value is int signed && signed >= 0) return describe((uint)signed);
            return null;
        }
        catch { return null; }
    }
}

/// <summary>取证上下文：HUD 在点击「拍下现场」时从帧时间线取的当前窗口数据。</summary>
public sealed record StutterEvidenceContext(
    string GameName,
    IReadOnlyList<double> StutterFramesMs,
    double MedianMs,
    double Fps,
    double P1Low);
