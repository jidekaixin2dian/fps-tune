using Microsoft.Win32;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 读取 Windows 自己给这块显示器关联的 ICC 配置文件，以及它到底有没有「校准表」。
///
/// 为什么需要这个：Windows 设置里那个「颜色配置文件」磁贴只做**关联**——告诉读色彩管理的程序
/// 「这块屏是哪个配置」。屏幕本身会不会变，取决于该配置文件里有没有 <c>vcgt</c> 校准表，
/// 以及系统里的「使用 Windows 显示器校准」是否开启。选 <c>sRGB IEC61966-2.1</c> 这类色彩空间
/// 配置文件是不会有任何变化的（它没有校准表）；选 1U 那种带 vcgt 的预设才会真的改画面。
/// </summary>
internal static class SystemColorProfile
{
    internal sealed record Info(
        string? ProfileName,
        string? Path,
        bool HasCalibration,
        bool CalibrationLoadingEnabled,
        IccCurves? Curves)
    {
        internal bool CanApply => HasCalibration && Curves is not null;
    }

    private const string DisplayClass = "{4d36e96e-e325-11ce-bfc1-08002be10318}";
    private const string AssociationPath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM\ProfileAssociations\Display\" + DisplayClass;
    private const string CalibrationPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM\Calibration";

    internal static Info Read(string? colorDirectory = null)
    {
        var loading = ReadCalibrationLoading();
        try
        {
            var directory = colorDirectory ?? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "spool", "drivers", "color");
            foreach (var name in EnumerateAssociatedProfiles())
            {
                var path = System.IO.Path.Combine(directory, name);
                if (!System.IO.File.Exists(path)) continue;
                IccCurves? curves = null;
                try { curves = IccCurveReader.TryRead(System.IO.File.ReadAllBytes(path)); }
                catch { /* 读不出来就当没有校准表 */ }
                return new Info(name, path, curves is { EncodedRamp: true }, loading, curves);
            }
        }
        catch { /* 注册表读不到就返回空信息 */ }
        return new Info(null, null, false, loading, null);
    }

    /// <summary>当前生效的配置文件排在前面（快照优先，其次完整关联列表）。</summary>
    private static IEnumerable<string> EnumerateAssociatedProfiles()
    {
        using var root = Registry.CurrentUser.OpenSubKey(AssociationPath);
        if (root is null) yield break;
        var names = new List<string>();
        foreach (var keyName in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(keyName);
            if (key is null) continue;
            foreach (var valueName in new[] { "ICMProfileSnapshot", "ICMProfile" })
            {
                if (key.GetValue(valueName) is not string[] entries) continue;
                foreach (var entry in entries)
                    if (!string.IsNullOrWhiteSpace(entry) && !names.Contains(entry, StringComparer.OrdinalIgnoreCase))
                        names.Add(entry.Trim());
            }
            if (names.Count > 0) break; // 单显示器先取第一块；多显示器请按设备实例号细分（见 ICMPROFILE 注释）
        }
        foreach (var name in names.Where(n => n.EndsWith(".icc", StringComparison.OrdinalIgnoreCase)
                     || n.EndsWith(".icm", StringComparison.OrdinalIgnoreCase)))
            yield return name;
    }

    private static bool ReadCalibrationLoading()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CalibrationPath);
            return key?.GetValue("CalibrationManagementEnabled") is int v && v != 0;
        }
        catch { return false; }
    }
}
