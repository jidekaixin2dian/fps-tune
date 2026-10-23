using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 进程身份校验：进程名之外再比对可执行文件完整路径。
/// 进程名可被任意程序改名冒充；AutoProfile 自动应用与 FPS 采样都以此确认目标确实是档案里的游戏本体。
/// </summary>
internal static class ProcessIdentity
{
    /// <summary>测试替换路径解析（真实实现走 QueryFullProcessImageName，仅 Windows）。</summary>
    internal static Func<Process, string?>? ImagePathOverride;

    /// <summary>返回进程可执行文件的完整路径；取不到（权限、已退出、受保护进程）返回 null。</summary>
    public static string? TryGetImagePath(Process process)
    {
        if (ImagePathOverride is not null)
            return ImagePathOverride(process);
        try
        {
            // PROCESS_QUERY_LIMITED_INFORMATION：最低权限即可查询路径，普通用户态进程都能取到。
            var handle = OpenProcess(0x1000u, false, process.Id);
            if (handle == IntPtr.Zero)
                return null;
            try
            {
                var buffer = new StringBuilder(32768);
                var length = buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref length) ? buffer.ToString() : null;
            }
            finally { CloseHandle(handle); }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>进程可执行文件路径与期望的完全限定路径一致（忽略大小写）；期望为空或取不到路径都算不匹配。</summary>
    public static bool MatchesPath(Process process, string? expectedExePath)
    {
        if (string.IsNullOrWhiteSpace(expectedExePath))
            return false;
        var path = TryGetImagePath(process);
        return path is not null
            && string.Equals(path, expectedExePath.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
