using System.IO;
using System.Security.Cryptography;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 内置工具的解压与完整性校验。嵌入资源在首次使用时释放到用户数据目录（带版本子目录），
/// SHA-256 与入仓记录（FpsTune.Wpf/Tools/PresentMon/PresentMon-Source.txt 与 licenses/manifest.json）一致才放行；
/// 已有产物哈希不符（磁盘损坏/被改写）时重解压自愈。资源缺失或写入失败抛异常，由调用方降级。
/// </summary>
internal static class BundledToolExtract
{
    internal const string PresentMonResourceName = "FpsTune.Tools.PresentMon-x64.exe";
    internal const string PresentMonVersion = "2.5.1";
    /// <summary>Intel 官方未修改二进制的 SHA-256（签名校验另由 TrustedCaptureTool 白名单覆盖）。</summary>
    internal const string PresentMonSha256 = "9bec3083069f58f911e6a512f4806db51a27bd096103087bc1d05ef54c80a191";

    /// <summary>测试注入：直接给出资源流（null 表示资源缺失），不读程序集。</summary>
    internal static Func<string, Stream?>? ResourceOverride;
    /// <summary>测试注入：解压目标目录。</summary>
    internal static string? InstallDirOverride;

    internal static string PresentMonInstallDir =>
        InstallDirOverride ?? Path.Combine(UserDataPaths.Root, "tools", "presentmon", PresentMonVersion);

    internal static string PresentMonPath => Path.Combine(PresentMonInstallDir, "PresentMon-x64.exe");

    /// <summary>返回解压后的内置 PresentMon 路径。落盘先写临时文件再原子替换，半写产物不会被视为可用。</summary>
    internal static string EnsurePresentMon()
    {
        var path = PresentMonPath;
        if (File.Exists(path) && Sha256Matches(path, PresentMonSha256))
            return path;
        Directory.CreateDirectory(PresentMonInstallDir);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var resource = OpenResource(PresentMonResourceName))
            {
                if (resource is null)
                    throw new InvalidOperationException("内置采样器资源缺失，无法解压。");
                using var target = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                resource.CopyTo(target);
                target.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* 移动成功后临时文件已不存在。 */ }
        }
        if (!Sha256Matches(path, PresentMonSha256))
            throw new IOException("内置采样器解压后校验失败，已拒绝使用。");
        return path;
    }

    private static Stream? OpenResource(string name)
    {
        if (ResourceOverride is { } injected) return injected(name);
        return typeof(BundledToolExtract).Assembly.GetManifestResourceStream(name);
    }

    /// <summary>文件 SHA-256 是否与期望一致；读不到（占用/权限）按不一致处理，走重解压路径。</summary>
    internal static bool Sha256Matches(string path, string expected)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return string.Equals(hash, expected, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
