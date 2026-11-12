using System.IO;
using System.Text.RegularExpressions;

namespace FpsTune.Wpf.Core;

/// <summary>自启项对当前用户生效（可改），还是对全部用户生效（需要管理员）。</summary>
internal enum StartupScope
{
    CurrentUser,
    AllUsers
}

/// <summary>自启项来自哪个位置。</summary>
internal enum StartupOrigin
{
    /// <summary>注册表 Run 键：每次登录都启动。</summary>
    Run,

    /// <summary>注册表 RunOnce 键：只启动一次，然后自己被删掉。</summary>
    RunOnce,

    /// <summary>启动文件夹（开始菜单→启动）。</summary>
    Folder
}

/// <summary>一条自启项的当前状态。命令原文照录，只额外给出可执行文件路径与用途判断。</summary>
internal sealed record StartupEntry(
    string Id,
    string Name,
    string Command,
    StartupScope Scope,
    StartupOrigin Origin,
    bool Enabled,
    string? Executable)
{
    /// <summary>只有当前用户的 Run/RunOnce 项由我们直接改；其它位置只读展示。</summary>
    internal bool Toggleable => Origin is StartupOrigin.Run or StartupOrigin.RunOnce && Scope == StartupScope.CurrentUser;

    /// <summary>命令指向的程序能不能找到；解析不出路径时为 null（未知，不当成缺失）。</summary>
    internal bool? ExecutableExists => Executable is { Length: > 0 } path && System.IO.Path.IsPathFullyQualified(path)
        ? File.Exists(path)
        : null;

    /// <summary>命令指向临时目录：这类自启项通常来自安装包解压残留，长期运行并不可靠。</summary>
    internal bool PointsAtTemporaryFolder => Executable is { Length: > 0 } path &&
        path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase);
}

/// <summary>自启项的登记位置。改注册表只走这里给出的路径，避免各页面各写一份。</summary>
internal static class StartupLocations
{
    internal const string CurrentUserRun = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string CurrentUserRunOnce = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    internal const string AllUsersRun = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    internal const string AllUsersRunOnce = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";

    /// <summary>我们自己放「已停用自启项」的键：值名 = 原位置 + 原名，值数据 = 原命令。</summary>
    internal const string DisabledStore = @"Software\FpsTune\DisabledStartup";

    internal static string RegistryPath(StartupScope scope, StartupOrigin origin) => (scope, origin) switch
    {
        (StartupScope.CurrentUser, StartupOrigin.Run) => CurrentUserRun,
        (StartupScope.CurrentUser, StartupOrigin.RunOnce) => CurrentUserRunOnce,
        (StartupScope.AllUsers, StartupOrigin.Run) => AllUsersRun,
        (StartupScope.AllUsers, StartupOrigin.RunOnce) => AllUsersRunOnce,
        _ => throw new ArgumentOutOfRangeException(nameof(origin))
    };

    /// <summary>自启项 id：位置 + 作用域 + 名字，能唯一定位一条（同名不同位置不会被混为一谈）。</summary>
    internal static string BuildId(StartupScope scope, StartupOrigin origin, string name)
        => $"{origin.ToString().ToLowerInvariant()}:{(scope == StartupScope.CurrentUser ? "hkcu" : "hklm")}:{name}";

    /// <summary>停用后存在我们键里的值名（不要和别的东西重名）。</summary>
    internal static string BuildStoreValueName(StartupScope scope, StartupOrigin origin, string name)
        => $"{(origin == StartupOrigin.Run ? "run" : "runonce")}:{(scope == StartupScope.CurrentUser ? "hkcu" : "hklm")}:{name}";

    internal static StartupScope ScopeOf(string id) => id.Contains(":hklm:", StringComparison.OrdinalIgnoreCase)
        ? StartupScope.AllUsers
        : StartupScope.CurrentUser;

    internal static StartupOrigin OriginOf(string id) => id.StartsWith("runonce:", StringComparison.OrdinalIgnoreCase)
        ? StartupOrigin.RunOnce
        : StartupOrigin.Run;

    /// <summary>从 id 取回原始名字（可能含冒号，所以只切掉前两段）。</summary>
    internal static string NameOf(string id)
    {
        var first = id.IndexOf(':');
        if (first < 0) return id;
        var second = id.IndexOf(':', first + 1);
        return second < 0 ? "" : id[(second + 1)..];
    }
}

/// <summary>
/// 从自启命令里取出实际要运行的程序路径。
/// 命令是用户机器上的第三方字符串：可能是带引号的路径 + 参数、可能带环境变量、
/// 也可能是 explorer/netsh 这类系统程序；解析不出就返回 null，不猜。
/// </summary>
internal static class StartupCommandParser
{
    private static readonly Regex Quoted = new("^\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly Regex Bare = new(@"^([^\s]+)", RegexOptions.CultureInvariant);

    internal static string? ExtractExecutable(string? command)
    {
        var text = (command ?? "").Trim();
        if (text.Length == 0) return null;

        string? candidate;
        var quoted = Quoted.Match(text);
        if (quoted.Success)
        {
            candidate = quoted.Groups[1].Value;
        }
        else
        {
            var bare = Bare.Match(text);
            candidate = bare.Success ? bare.Groups[1].Value : null;

            // 未加引号且路径含空格（C:\Program Files\...）：按扩展名回退到第一个可执行文件。
            if (candidate is not null && !HasExecutableExtension(candidate))
            {
                var index = IndexOfExecutableExtension(text);
                if (index > 0) candidate = text[..index];
            }
        }

        if (string.IsNullOrWhiteSpace(candidate)) return null;
        candidate = candidate.Trim().Trim('"');
        if (candidate.Length == 0) return null;
        try { candidate = Environment.ExpandEnvironmentVariables(candidate); }
        catch (ArgumentException) { /* 还原原始串，交给上层显示。 */ }
        return candidate;
    }

    private static bool HasExecutableExtension(string path) => ExtensionIndex(path) > 0;

    private static int ExtensionIndex(string text)
    {
        foreach (var extension in new[] { ".exe", ".com", ".bat", ".cmd", ".lnk", ".ps1", ".msc" })
        {
            var index = text.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (index > 0) return index + extension.Length;
        }
        return -1;
    }

    private static int IndexOfExecutableExtension(string text)
    {
        var best = -1;
        foreach (var extension in new[] { ".exe", ".com", ".bat", ".cmd", ".lnk" })
        {
            var index = text.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var end = index + extension.Length;
            // 后面紧跟空格或结尾才算数，"x.exe.bak" 这类不能被当成程序。
            if (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != ',') continue;
            if (best < 0 || end < best) best = end;
        }
        return best;
    }
}
