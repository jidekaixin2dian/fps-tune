using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace FpsTune.Wpf.Services;

/// <summary>安装信息里的一处缺口。</summary>
internal enum InstallGap
{
    /// <summary>控制面板/设置里看不到本程序（卸载项缺失或指向别处）。</summary>
    UninstallEntry,
    /// <summary>开始菜单找不到（快捷方式缺失或指向不存在的路径）。</summary>
    StartMenuShortcut,
    /// <summary>桌面上找不到（快捷方式缺失或指向不存在的路径）。</summary>
    DesktopShortcut,
    /// <summary>安装目录里混进了本次安装不认识的文件（卸载时不会清掉，也会让形态与安装日志不一致）。</summary>
    StrayFiles,
}

/// <summary>一处可修复项的现状。Detail 已脱敏，可直接进界面。</summary>
internal sealed record InstallGapState(InstallGap Gap, string Detail, int StrayCount = 0);

/// <summary>安装信息快照：与安装器写下的记录逐项对照。</summary>
internal sealed record InstallState(
    string InstallDirectory,
    string ExecutablePath,
    IReadOnlyList<InstallGap> Gaps,
    IReadOnlyList<InstallGapState> Details)
{
    internal bool IsComplete => Gaps.Count == 0;
}

/// <summary>修复动作的结果。</summary>
internal sealed record InstallRepairResult(bool Ok, string Summary, IReadOnlyList<string> Steps)
{
    internal static InstallRepairResult Done(IReadOnlyList<string> steps) => new(true, "", steps);
    internal static InstallRepairResult Failed(string summary, IReadOnlyList<string> steps) => new(false, summary, steps);
}

/// <summary>
/// 安装信息自检与修复。
///
/// 为什么需要它：安装器（Inno）只在自己安装时写一次卸载项与快捷方式，
/// 之后我们用开发构建直接覆盖 <c>D:/FpsTune</c>，目录里的文件清单就从「单文件」
/// 变成「多文件产物」。一旦卸载项或快捷方式被外部工具（第三方卸载器、清理软件、
/// 手工删注册表）清掉，程序本体还在、也能直接双击启动，但开始菜单搜索不到、
/// 「应用和功能」里没有它 —— 用户只会觉得「这东西没装过」。
///
/// 判定和修复都以安装器真实写下的记录为准：
/// <list type="bullet">
/// <item>安装目录 = <c>unins000.dat</c> 所在目录（回退到当前 exe 目录）；</item>
/// <item>卸载项 = <c>HKCU/HKLM\…\Uninstall\{AppId}_is1</c>（Inno 的规则：管理员装进 HKLM，否则 HKCU）；</item>
/// <item>快捷方式 = 安装器记录的 <c>ProgramData\…\Start Menu\Programs\FPS 帧律.lnk</c> 与
/// <c>C:\Users\Public\Desktop\FPS 帧律.lnk</c>，缺了才补，已有的一律不覆盖。</item>
/// </list>
/// 修复只写这一小撮安装元数据，不碰任何优化项、不碰用户设置。
/// </summary>
internal static class InstallIntegrity
{
    /// <summary>安装器 setup.iss 的 AppId（去掉花括号），卸载项键名 = <c>{AppId}_is1</c>。</summary>
    internal const string AppId = "8D6E7F3A-4C5B-4D1E-9A2B-7C0F6E1D8B4A";
    internal const string AppName = "FPS 帧律";
    internal const string ExeName = "FpsTune.exe";
    internal const string UninstallerName = "unins000.exe";
    internal const string InstallLogName = "unins000.dat";

    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Wow64UninstallKeyPath = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>测试注入：替换安装目录（默认取 exe 所在目录）。</summary>
    internal static string? DirectoryOverride { get; set; }

    /// <summary>测试注入：替换卸载项读取（默认读真实注册表）。</summary>
    internal static Func<string, string?>? UninstallValueOverride { get; set; }

    /// <summary>测试注入：替换注册表写入。</summary>
    internal static Action<string, IReadOnlyDictionary<string, object>>? UninstallWriteOverride { get; set; }

    /// <summary>测试注入：替换快捷方式存在性判断。</summary>
    internal static Func<string, bool>? ShortcutExistsOverride { get; set; }

    /// <summary>测试注入：替换快捷方式创建。</summary>
    internal static Action<string, string>? ShortcutCreateOverride { get; set; }

    internal static string InstallDirectory =>
        DirectoryOverride ?? Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory;

    internal static string ExecutablePath => Path.Combine(InstallDirectory, ExeName);

    internal static string StartMenuShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", AppName + ".lnk");

    /// <summary>用户级开始菜单（无需管理员）。公共开始菜单写不进去时的回退位置。</summary>
    internal static string UserStartMenuShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", AppName + ".lnk");

    /// <summary>开始菜单快捷方式的候选位置，优先全体用户（与安装器一致），回退当前用户。</summary>
    internal static string[] StartMenuShortcutPaths => [StartMenuShortcut, UserStartMenuShortcut];

    internal static string DesktopShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), AppName + ".lnk");

    /// <summary>用户桌面（无需管理员）。公共桌面写不进去时的回退位置。</summary>
    internal static string UserDesktopShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    /// <summary>桌面快捷方式的候选位置，优先全体用户（与安装器一致），回退当前用户。</summary>
    internal static string[] DesktopShortcutPaths => [DesktopShortcut, UserDesktopShortcut];

    internal static string UninstallKeyName => "{" + AppId + "}_is1";

    /// <summary>
    /// 读出当前安装信息。目录里没有 <c>unins000.dat</c>（开发目录、绿色版）时返回 <c>null</c>：
    /// 那种情况下没有「安装」可言，不该提示用户去修。
    /// </summary>
    internal static InstallState? Inspect()
    {
        var dir = InstallDirectory;
        if (!File.Exists(Path.Combine(dir, InstallLogName))) return null;

        var details = new List<InstallGapState>();

        var uninstallString = ReadUninstallValue("UninstallString");
        if (string.IsNullOrWhiteSpace(uninstallString)
            || !uninstallString.Contains(UninstallerName, StringComparison.OrdinalIgnoreCase)
            || !SameDirectory(uninstallString, dir))
        {
            details.Add(new InstallGapState(
                InstallGap.UninstallEntry,
                string.IsNullOrWhiteSpace(uninstallString) ? "" : Scrub(uninstallString)));
        }

        if (!StartMenuShortcutPaths.Any(ShortcutPointsHere))
            details.Add(new InstallGapState(InstallGap.StartMenuShortcut, Scrub(UserStartMenuShortcut)));

        if (!DesktopShortcutPaths.Any(ShortcutPointsHere))
            details.Add(new InstallGapState(InstallGap.DesktopShortcut, Scrub(UserDesktopShortcut)));

        var strays = CountStrayFiles(dir);
        if (strays > 0) details.Add(new InstallGapState(InstallGap.StrayFiles, "", strays));

        return new InstallState(dir, ExecutablePath, details.Select(d => d.Gap).ToArray(), details);
    }

    /// <summary>
    /// 修复缺口：补卸载项（先 HKLM，无权限退到 HKCU）、补缺失的快捷方式。
    /// 已有的东西不覆盖；<c>StrayFiles</c> 不在修复范围内（要删文件得用户自己决定）。
    /// </summary>
    internal static InstallRepairResult Repair(InstallState state)
    {
        var steps = new List<string>();
        var failures = new List<string>();

        if (state.Gaps.Contains(InstallGap.UninstallEntry))
        {
            try
            {
                WriteUninstallEntry();
                steps.Add(Str.T("Str.InstallRepairStepUninstall"));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
            {
                failures.Add(Str.T("Str.InstallRepairFailUninstall", ex.Message));
            }
        }

        if (state.Gaps.Contains(InstallGap.StartMenuShortcut))
        {
            // 公共开始菜单需要管理员；写不进去就落到用户级开始菜单——开始菜单搜索照样能找到。
            var lastFailure = "";
            var created = false;
            foreach (var path in StartMenuShortcutPaths)
            {
                try
                {
                    CreateShortcut(path, state.ExecutablePath);
                    steps.Add(path == StartMenuShortcut
                        ? Str.T("Str.InstallRepairStepShortcut", Str.T("Str.InstallLocationStartMenu"))
                        : Str.T("Str.InstallRepairUserStartMenu"));
                    created = true;
                    break;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or COMException)
                {
                    lastFailure = ex.Message;
                }
            }
            if (!created)
                failures.Add(Str.T("Str.InstallRepairFailShortcut", Str.T("Str.InstallLocationStartMenu"), lastFailure));
        }

        if (state.Gaps.Contains(InstallGap.DesktopShortcut))
        {
            // 公共桌面同样要管理员；写不进去就落到用户桌面，桌面上照样看得见。
            var lastFailure = "";
            var created = false;
            foreach (var path in DesktopShortcutPaths)
            {
                try
                {
                    CreateShortcut(path, state.ExecutablePath);
                    steps.Add(path == DesktopShortcut
                        ? Str.T("Str.InstallRepairStepShortcut", Str.T("Str.InstallLocationDesktop"))
                        : Str.T("Str.InstallRepairUserDesktop"));
                    created = true;
                    break;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or COMException)
                {
                    lastFailure = ex.Message;
                }
            }
            if (!created)
                failures.Add(Str.T("Str.InstallRepairFailShortcut", Str.T("Str.InstallLocationDesktop"), lastFailure));
        }

        if (state.Gaps.Contains(InstallGap.StrayFiles))
            steps.Add(Str.T("Str.InstallRepairStepStray", StrayCount(state)));

        if (failures.Count > 0)
            return InstallRepairResult.Failed(string.Join("\n", failures), steps);

        return steps.Count == 0
            ? InstallRepairResult.Done(new[] { Str.T("Str.InstallRepairNothing") })
            : InstallRepairResult.Done(steps);
    }

    private static int StrayCount(InstallState state)
        => state.Details.FirstOrDefault(d => d.Gap == InstallGap.StrayFiles)?.StrayCount ?? 0;

    /// <summary>
    /// 安装目录里不属于安装记录的文件数。安装记录只有三样东西（exe、LICENSE、第三方声明），
    /// 多出来的都是「开发构建直接覆盖进来」的产物：卸载时不会删，形态也和安装日志对不上。
    /// 用户自己的数据不在安装目录，所以这里只数不删。
    /// </summary>
    private static int CountStrayFiles(string dir)
    {
        try
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ExeName, "LICENSE", "THIRD-PARTY-NOTICES.txt", UninstallerName, InstallLogName,
            };
            return Directory.EnumerateFileSystemEntries(dir)
                .Count(p => !known.Contains(Path.GetFileName(p)));
        }
        catch
        {
            return 0;
        }
    }

    private static bool ShortcutPointsHere(string path)
    {
        if (ShortcutExistsOverride is not null) return ShortcutExistsOverride(path);
        if (!File.Exists(path)) return false;
        var target = ShortcutTarget.Read(path);
        return target is null || SameDirectory(target, InstallDirectory);
    }

    private static bool SameDirectory(string path, string dir)
    {
        try
        {
            var trimmed = path.Trim().Trim('"');
            var candidate = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrEmpty(candidate)) return false;
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadUninstallValue(string name)
    {
        if (UninstallValueOverride is not null) return UninstallValueOverride(name);
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.CurrentUser, RegistryView.Registry64),
                 })
        {
            foreach (var sub in new[] { UninstallKeyPath, Wow64UninstallKeyPath })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(sub + "\\" + UninstallKeyName);
                    if (key?.GetValue(name) is string value && value.Length > 0) return value;
                }
                catch
                {
                    // 读不到就继续找下一个位置；找不到等于「卸载项缺失」。
                }
            }
        }
        return null;
    }

    private static void WriteUninstallEntry()
    {
        var values = UninstallValues();
        if (UninstallWriteOverride is not null)
        {
            UninstallWriteOverride(UninstallKeyName, values);
            return;
        }
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.CreateSubKey(UninstallKeyPath + "\\" + UninstallKeyName, writable: true);
                if (key is null) continue;
                foreach (var (name, value) in values) key.SetValue(name, value);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                // 普通权限写不了 HKLM：退回 HKCU，设置里一样能看到与卸载。
            }
            catch (System.Security.SecurityException)
            {
                // 同上。
            }
        }
        throw new UnauthorizedAccessException(Str.T("Str.InstallRepairNoPermission"));
    }

    /// <summary>卸载项的值与 Inno 自己写下的那套保持一致，避免「设置」里显示成外来条目。</summary>
    private static Dictionary<string, object> UninstallValues()
    {
        var dir = InstallDirectory;
        var exe = ExecutablePath;
        var uninstaller = Path.Combine(dir, UninstallerName);
        var values = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["DisplayName"] = AppName,
            ["DisplayVersion"] = UpdateService.DisplayVersion,
            ["DisplayIcon"] = exe,
            ["InstallLocation"] = Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar,
            ["UninstallString"] = "\"" + uninstaller + "\"",
            ["QuietUninstallString"] = "\"" + uninstaller + "\" /SILENT",
            ["Publisher"] = AppName,
            ["NoModify"] = 1,
            ["NoRepair"] = 1,
            ["InstallDate"] = DateTime.Now.ToString("yyyyMMdd"),
            ["EstimatedSize"] = EstimateSizeKb(dir),
            ["Inno Setup: App Path"] = dir,
            ["Inno Setup: Icon Group"] = AppName,
            ["Inno Setup: Setup Version"] = "6.4.0",
            ["Inno Setup: Language"] = "chinesesimplified",
            ["Inno Setup: User"] = Environment.UserName,
        };
        return values;
    }

    private static int EstimateSizeKb(string dir)
    {
        try
        {
            return (int)(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024);
        }
        catch
        {
            return 0;
        }
    }

    private static void CreateShortcut(string path, string target)
    {
        if (ShortcutCreateOverride is not null)
        {
            ShortcutCreateOverride(path, target);
            return;
        }
        ShortcutTarget.Create(path, target, AppName);
    }

    private static string Scrub(string text) => PrivacyScrub.Sanitize(text);
}

/// <summary>读写 .lnk（IShellLinkW + IPersistFile），不引第三方包、不启动 PowerShell。</summary>
internal static class ShortcutTarget
{
    private static readonly Guid ShellLink = new("00021401-0000-0000-C000-000000000046");

    internal static string? Read(string path)
    {
        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ShellLink);
            if (type is null) return null;
            instance = Activator.CreateInstance(type);
            if (instance is not IPersistFile persist) return null;
            persist.Load(path, 0);
            if (instance is not IShellLinkW link) return null;
            var buffer = new char[1024];
            link.GetPath(buffer, buffer.Length, IntPtr.Zero, 0);
            var target = new string(buffer).TrimEnd('\0');
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
        }
    }

    internal static void Create(string path, string target, string description)
    {
        var type = Type.GetTypeFromCLSID(ShellLink)
                   ?? throw new InvalidOperationException(Str.T("Str.InstallRepairNoShellLink"));
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(type)
                       ?? throw new InvalidOperationException(Str.T("Str.InstallRepairNoShortcutObject"));
            var link = (IShellLinkW)instance;
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            ((IPersistFile)instance).Save(path, true);
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
        }
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] char[] pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] char[] pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] char[] pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] char[] pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] char[] pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
