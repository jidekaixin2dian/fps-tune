using System.Diagnostics;
using System.IO;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>手动与启动提醒共用的更新流程；只有用户点击安装才调用。</summary>
internal static class UpdateInstaller
{
    internal static async Task InstallAsync(UpdateInfo info, Action<double> progress, CancellationToken token)
    {
        if (info.InstallerUrl is null || info.ChecksumUrl is null)
            throw new InvalidOperationException(Str.T("Str.UpdateAssetsMissing"));
        var directory = Path.Combine(Path.GetTempPath(), "FpsTune-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var installerPath = Path.Combine(directory, UpdateService.InstallerAssetName(info.Version));
        var manifestPath = Path.Combine(directory, UpdateService.ChecksumAssetName(info.Version));
        var started = false;
        try
        {
            await UpdateService.DownloadAsync(info.InstallerUrl, installerPath, progress, token);
            await UpdateService.DownloadAsync(info.ChecksumUrl, manifestPath, null, token, 1024 * 1024);
            var manifest = await File.ReadAllTextAsync(manifestPath, token);
            if (!UpdateService.TryReadSha256(manifest, Path.GetFileName(installerPath), out var expected))
                throw new InvalidOperationException(Str.T("Str.ManifestMissingEntry"));
            // 保持只读句柄到启动完成，阻止校验与执行之间覆盖或替换文件。
            using var verifiedFile = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(verifiedFile));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(Str.T("Str.InstallerHashMismatch"));
            token.ThrowIfCancellationRequested();
            using var gate = SystemMutationGate.Acquire(TimeSpan.Zero);
            if (App.SessionService.IsRunning || ExperimentRunner.IsRunning)
                throw new InvalidOperationException(Str.T("Str.UpdateSessionBusy"));
            var installDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            using var process = Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                Arguments = $"/NORESTART /DIR=\"{installDirectory}\""
            });
            if (process is null) throw new InvalidOperationException(Str.T("Str.CannotStartInstaller"));
            started = true;
        }
        finally
        {
            // 安装器启动后仍需读取自身，保留本次目录；其余情况仅清理本次创建的文件。
            if (!started)
            {
                try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
