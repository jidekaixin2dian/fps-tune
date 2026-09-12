using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 已通过内置版本校验的脚本：执行路径 + 父进程实际校验过的内容哈希 + 执行期租约。
/// 释放租约前，任何进程都无法改写/替换/删除该文件；哈希必须交给真正执行它的
/// 子进程再校验一次——父进程"校验"与子进程"按路径打开"之间存在时间差，
/// 只有让执行者自己校验，才能保证被执行的字节就是被校验过的字节。
/// </summary>
public sealed record VerifiedScript(string Path, string Sha256, FileStream Lease) : IDisposable
{
    public void Dispose() => Lease.Dispose();
}

public static class ScriptLocator
{
    private static string? _scriptDir;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, byte[]> EmbeddedCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>是否处于提权会话：提权时只执行与内置版本逐字节一致的副本。</summary>
    private static bool IsElevated => AdminHelper.IsAdministrator();

    private static string ScriptDir => _scriptDir ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FpsTune", "scripts");

    public static string Resolve(string fileName) => Resolve(fileName, AppContext.BaseDirectory, ScriptDir);

    /// <summary>
    /// 执行脚本前解析并"锁定"脚本：返回的租约在释放前会拒绝任何写入/替换/删除，
    /// 因此校验通过的内容不可能在执行途中被同账户的其他进程换掉。
    ///
    /// 提权会话只执行与内置资源逐字节一致的副本：应用目录里被改写的同名脚本直接拒绝执行，
    /// 提取目录里的旧副本会被重新释放覆盖。非提权会话保留"应用目录优先"的绿色版/开发语义。
    /// </summary>
    public static VerifiedScript OpenVerified(string fileName)
        => OpenVerified(fileName, AppContext.BaseDirectory, ScriptDir, IsElevated);

    internal static VerifiedScript OpenVerified(
        string fileName, string applicationDirectory, string scriptDirectory, bool elevated)
    {
        var embedded = ReadEmbedded(fileName);
        var expectedHash = Convert.ToHexString(SHA256.HashData(embedded));

        var external = Path.Combine(applicationDirectory, fileName);
        if (File.Exists(external))
        {
            if (!elevated)
            {
                // 绿色版/开发输出允许自行改脚本；仍用租约锁住，并按"实际内容"登记哈希，
                // 让子进程至少能发现校验之后发生的替换。
                var lease = OpenLease(external);
                var actualHash = HashOf(lease);
                if (actualHash is null)
                {
                    lease.Dispose();
                    throw new InvalidOperationException("无法读取脚本内容，已拒绝执行：" + external);
                }

                return new VerifiedScript(external, actualHash, lease);
            }

            var verifiedExternal = TryOpenVerifiedLease(external, expectedHash, out var externalProblem);
            if (verifiedExternal is not null)
                return new VerifiedScript(external, expectedHash, verifiedExternal);

            throw new InvalidOperationException(
                "脚本未通过内置版本校验（" + externalProblem + "），已拒绝在管理员会话中执行：" + external +
                "；请删除或还原该文件，或重新构建/重新安装后再试。");
        }

        // 没有外部文件（单文件发行版）：从嵌入资源释放，并保证拿到的一定是内置内容。
        var target = Path.Combine(scriptDirectory, fileName);
        var verified = TryOpenVerifiedLease(target, expectedHash, out _);
        if (verified is not null)
            return new VerifiedScript(target, expectedHash, verified);

        WriteEmbeddedAtomic(target, embedded);

        var released = TryOpenVerifiedLease(target, expectedHash, out var releasedProblem);
        if (released is not null)
            return new VerifiedScript(target, expectedHash, released);

        throw new InvalidOperationException(
            "脚本副本释放后仍未通过校验（" + releasedProblem + "），已拒绝执行：" + target);
    }

    /// <summary>只解析路径，不做提权校验、不返回租约（旧调用点与兼容测试使用）。</summary>
    internal static string Resolve(string fileName, string applicationDirectory, string scriptDirectory)
    {
        var embedded = ReadEmbedded(fileName);
        var expectedHash = Convert.ToHexString(SHA256.HashData(embedded));

        var external = Path.Combine(applicationDirectory, fileName);
        if (File.Exists(external))
            return external;

        // 当前工作目录不属于应用安装目录，不能用于查找可执行脚本。
        var target = Path.Combine(scriptDirectory, fileName);
        if (TryOpenVerifiedLease(target, expectedHash, out _) is { } verified)
        {
            verified.Dispose();
            return target;
        }

        WriteEmbeddedAtomic(target, embedded);
        return target;
    }

    /// <summary>
    /// 只读 + FileShare.Read：其他进程仍可读（PowerShell 要读），但写/替换/删除一律被拒绝。
    /// </summary>
    private static FileStream OpenLease(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法锁定脚本文件（可能被其他程序占用）：" + path, ex);
        }
    }

    /// <summary>校验与"锁定"共用同一个句柄，避免校验通过后被掉包。</summary>
    private static FileStream? TryOpenVerifiedLease(string path, string expectedHash, out string? problem)
    {
        problem = null;
        try
        {
            if (!File.Exists(path))
            {
                problem = "文件不存在";
                return null;
            }

            var lease = OpenLease(path);
            if (MatchesHash(lease, expectedHash))
                return lease;

            lease.Dispose();
            problem = "内容与内置版本不一致";
            return null;
        }
        catch (Exception ex)
        {
            problem = "无法读取脚本文件：" + ex.Message;
            return null;
        }
    }

    /// <summary>读取租约对应内容并返回大写十六进制 SHA256；失败返回 null。</summary>
    private static string? HashOf(FileStream stream)
    {
        try
        {
            stream.Position = 0;
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return null;
        }
    }

    private static bool MatchesHash(FileStream stream, string expectedHash)
        => HashOf(stream) is { } actual &&
           string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase);

    private static void WriteEmbeddedAtomic(string target, byte[] embedded)
    {
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // 独占临时名 + 原子替换：并发调用不会互相踩，也不会执行写了一半的脚本。
        var tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(embedded, 0, embedded.Length);
            File.Move(tmp, target, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch
            {
                // 写入异常优先；残留临时文件位于脚本目录内。
            }
        }
    }

    private static byte[] ReadEmbedded(string fileName)
    {
        // A/B 实验编排已迁入进程内 ExperimentRunner，脚本信任机器只剩朋友测试一处消费方
        if (fileName is not "friend-test.ps1")
            throw new ArgumentException("未知的应用脚本", nameof(fileName));

        lock (Gate)
        {
            if (EmbeddedCache.TryGetValue(fileName, out var cached))
                return cached;

            var resourceName = "FpsTune.Wpf.Scripts." + fileName;
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException($"未找到嵌入脚本资源：{resourceName}");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            EmbeddedCache[fileName] = bytes;
            return bytes;
        }
    }
}
