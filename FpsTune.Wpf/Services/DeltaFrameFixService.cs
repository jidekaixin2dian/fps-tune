using System.IO;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>三角洲掉帧修复 · 扫描结果。</summary>
public sealed record DeltaFrameScan(
    bool GameFound,
    string? GameDir,
    long PsoCacheBytes,
    long SavedBytes,
    long DxCacheBytes,
    string? PsoCacheDir,
    string? SavedDir,
    string? Error);

/// <summary>三角洲掉帧修复 · 单步结果。</summary>
public sealed record DeltaFixStep(string Name, bool Succeeded, string Detail);

/// <summary>
/// 三角洲掉帧修复（对标 1U DeltaFrameFix，行为以其为基准、实现为本项目原版）：
/// 9.4 修复 = 把游戏目录 Saved\PSOCache 整目录改名备份（不动画质与按键设置）；
/// 7.31 一键修复 = Saved 改名备份 + 清 NVIDIA DXCache；再附运行库检测与 -dx12/-dx11 指引。
/// 全部操作改名备份、可逆；绝不删除或改动游戏本体文件。
/// </summary>
public static class DeltaFrameFixService
{
    public const string GameExecutable = DeltaPreparation.GameExecutable;

    /// <summary>测试注入 DXCache 路径；null 走真实 %LOCALAPPDATA%。</summary>
    internal static string? DxCacheDirOverride { get; set; }

    /// <summary>定位三角洲并读取相关目录体积。</summary>
    public static DeltaFrameScan Scan(string? gameExe = null)
    {
        gameExe ??= StateStore.FindGameByProcessName(Path.GetFileNameWithoutExtension(GameExecutable))?.ExePath
            ?? AppState.GamePath;
        try
        {
            if (string.IsNullOrWhiteSpace(gameExe) || !File.Exists(gameExe))
                return new(false, null, 0, 0, 0, null, null, Str.T("Str.DeltaFixNeedGame"));
            var dir = Path.GetDirectoryName(gameExe)!;
            var saved = Path.Combine(dir, "Saved");
            var pso = Path.Combine(saved, "PSOCache");
            var psoBytes = DirSize(pso);
            var savedBytes = DirSize(saved) - psoBytes;
            var dx = DxCacheDir();
            return new(true, dir, psoBytes, Math.Max(0, savedBytes), DirSize(dx), pso, saved, null);
        }
        catch (Exception ex)
        {
            return new(false, null, 0, 0, 0, null, null, PrivacyScrub.Sanitize(ex.Message));
        }
    }

    /// <summary>9.4 修复：PSOCache 改名备份（画着色器缓存，画质按键原样保留）。</summary>
    public static DeltaFixStep FixPsoCache(string? gameExe)
    {
        var scan = Scan(gameExe);
        if (!scan.GameFound || scan.PsoCacheDir is null)
            return new(Str.T("Str.DeltaFixPsoName"), false, Str.T("Str.DeltaFixNeedGame"));
        return RenameDir(scan.PsoCacheDir);
    }

    /// <summary>7.31 一键修复：Saved 改名备份（画质按键回默认，可逆）+ 清 NVIDIA DXCache。</summary>
    public static IReadOnlyList<DeltaFixStep> FixLegacy(string? gameExe)
    {
        var steps = new List<DeltaFixStep>();
        var scan = Scan(gameExe);
        if (scan.GameFound && scan.SavedDir is not null)
            steps.Add(RenameDir(scan.SavedDir));
        try
        {
            var dx = DxCacheDir();
            if (Directory.Exists(dx))
            {
                var backup = dx + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                Directory.Move(dx, backup);
                steps.Add(new(Str.T("Str.DeltaFixDxName"), true, Str.T("Str.DeltaFixRenamed", backup)));
            }
            else steps.Add(new(Str.T("Str.DeltaFixDxName"), true, Str.T("Str.DeltaFixNothingToClean")));
        }
        catch (Exception ex)
        {
            steps.Add(new(Str.T("Str.DeltaFixDxName"), false, PrivacyScrub.Sanitize(ex.Message)));
        }
        return steps;
    }

    private static DeltaFixStep RenameDir(string dir)
    {
        var name = Path.GetFileName(dir);
        if (!Directory.Exists(dir))
            return new(name, true, Str.T("Str.DeltaFixNothingToClean"));
        var backup = dir + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Directory.Move(dir, backup);
        return new(name, true, Str.T("Str.DeltaFixRenamed", backup));
    }

    private static long DirSize(string? dir)
    {
        if (dir is null || !Directory.Exists(dir)) return 0;
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }

    /// <summary>NVIDIA DXCache（三角洲着色器缓存大头）。</summary>
    private static string? DxCacheDir()
    {
        if (DxCacheDirOverride is not null) return DxCacheDirOverride;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "NVIDIA", "DXCache");
    }
}
