namespace FpsTune.Wpf.Core;

internal static class WindowsVersionHelper
{
    private const int Windows11Build = 22000;

    internal static bool? IsWindows11Build(string? currentBuild, string? currentBuildNumber)
    {
        var hasBuild = false;
        foreach (var raw in new[] { currentBuild, currentBuildNumber })
        {
            if (!int.TryParse(raw, out var build))
                continue;

            hasBuild = true;
            if (build >= Windows11Build)
                return true;
        }

        return hasBuild ? false : null;
    }

    /// <summary>
    /// 本机 build 是否 ≥ 指定下限（读注册表 CurrentBuild）。读不到按不支持处理，
    /// 调用方（catalog 可用性门控）会置灰对应优化项而不是报错。
    /// </summary>
    internal static bool IsBuildAtLeast(int minimum)
    {
        try
        {
            using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            foreach (var raw in new[] { key?.GetValue("CurrentBuild")?.ToString(), key?.GetValue("CurrentBuildNumber")?.ToString() })
                if (int.TryParse(raw, out var build))
                    return build >= minimum;
        }
        catch { /* 读不到按不支持处理 */ }
        return false;
    }
}
