using System.IO;
using System.Windows;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>一次自启项扫描的结果。</summary>
internal sealed record StartupAuditSnapshot(
    IReadOnlyList<StartupEntry> Entries,
    int Unavailable,
    DateTimeOffset ReadAt)
{
    internal int EnabledCount => Entries.Count(e => e.Enabled);

    internal int DisabledCount => Entries.Count(e => !e.Enabled);

    internal int MissingCount => Entries.Count(e => e.ExecutableExists == false);
}

/// <summary>一次服务扫描的结果。</summary>
internal sealed record ServiceAuditSnapshot(
    IReadOnlyList<ServiceAuditRow> Rows,
    int Unavailable,
    DateTimeOffset ReadAt);

/// <summary>改一条自启项的结果：Ok=false 时 Message 是能照做的下一步。</summary>
internal sealed record StartupToggleResult(bool Ok, string Message);

/// <summary>
/// 后台与自启审查。读取全部走只读 API（注册表枚举、启动文件夹、SCM 查询配置）；
/// 唯一的写入是「停用/启用当前用户的自启项」：把值从 Run 键搬到我们自己的键，
/// 搬之前先确认读得到，搬之后读回确认，失败就地还原。
/// </summary>
internal static class StartupAuditService
{
    /// <summary>测试注入：整份快照。</summary>
    internal static Func<StartupAuditSnapshot>? SnapshotOverride { get; set; }

    /// <summary>测试注入：服务状态。</summary>
    internal static Func<ServiceAuditSnapshot>? ServicesOverride { get; set; }

    private static readonly string[] SkippedFolderNames = ["desktop.ini"];

    internal static StartupAuditSnapshot ReadStartup()
    {
        if (SnapshotOverride is { } injected) return injected();

        var entries = new List<StartupEntry>();
        var unavailable = 0;
        foreach (var (scope, origin) in new[]
                 {
                     (StartupScope.CurrentUser, StartupOrigin.Run),
                     (StartupScope.CurrentUser, StartupOrigin.RunOnce),
                     (StartupScope.AllUsers, StartupOrigin.Run),
                     (StartupScope.AllUsers, StartupOrigin.RunOnce)
                 })
        {
            try { ReadRegistry(entries, scope, origin); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                unavailable++;
            }
        }

        ReadDisabledStore(entries);
        ReadStartupFolders(entries, ref unavailable);

        var ordered = entries
            .OrderByDescending(e => e.Enabled)
            .ThenBy(e => e.Scope)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new(ordered, unavailable, DateTimeOffset.Now);
    }

    internal static ServiceAuditSnapshot ReadServices()
    {
        if (ServicesOverride is { } injected) return injected();
        var rows = ServiceAuditCatalog.Evaluate(NativeServiceStatus.ReadStartType, NativeServiceStatus.Read);
        return new(rows, rows.Count(r => !r.Readable), DateTimeOffset.Now);
    }

    private static void ReadRegistry(List<StartupEntry> entries, StartupScope scope, StartupOrigin origin)
    {
        var path = StartupLocations.RegistryPath(scope, origin);
        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
            scope == StartupScope.CurrentUser ? Microsoft.Win32.RegistryHive.CurrentUser : Microsoft.Win32.RegistryHive.LocalMachine,
            Microsoft.Win32.RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(path);
        if (key is null) return;

        foreach (var name in key.GetValueNames())
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var command = key.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
            entries.Add(new(
                StartupLocations.BuildId(scope, origin, name),
                name,
                command,
                scope,
                origin,
                Enabled: true,
                StartupCommandParser.ExtractExecutable(command)));
        }
    }

    /// <summary>停用的自启项仍然列出（否则用户没法再打开），位置标记为停用。</summary>
    private static void ReadDisabledStore(List<StartupEntry> entries)
    {
        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
            Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(StartupLocations.DisabledStore);
        if (key is null) return;

        foreach (var storeName in key.GetValueNames())
        {
            var parts = storeName.Split(':', 3);
            if (parts.Length < 3) continue;
            var scope = parts[1].Equals("hklm", StringComparison.OrdinalIgnoreCase) ? StartupScope.AllUsers : StartupScope.CurrentUser;
            var origin = parts[0].Equals("runonce", StringComparison.OrdinalIgnoreCase) ? StartupOrigin.RunOnce : StartupOrigin.Run;
            var name = parts[2];
            if (string.IsNullOrWhiteSpace(name)) continue;
            var command = key.GetValue(storeName, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
            entries.Add(new(
                StartupLocations.BuildId(scope, origin, name),
                name,
                command,
                scope,
                origin,
                Enabled: false,
                StartupCommandParser.ExtractExecutable(command)));
        }
    }

    private static void ReadStartupFolders(List<StartupEntry> entries, ref int unavailable)
    {
        var locations = new[]
        {
            (Scope: StartupScope.CurrentUser, Path: Environment.GetFolderPath(Environment.SpecialFolder.Startup)),
            (Scope: StartupScope.AllUsers, Path: Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup))
        };

        foreach (var (scope, folder) in locations)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var name = Path.GetFileName(file);
                    if (string.IsNullOrWhiteSpace(name) || SkippedFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                        continue;
                    entries.Add(new(
                        $"folder:{(scope == StartupScope.CurrentUser ? "user" : "common")}:{name}",
                        name,
                        file,
                        scope,
                        StartupOrigin.Folder,
                        Enabled: true,
                        file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unavailable++;
            }
        }
    }

    /// <summary>
    /// 停用或启用一条自启项。只处理当前用户的 Run/RunOnce：
    /// 其余位置要么需要管理员、要么是文件夹快捷方式，本页只展示原因，不代改。
    /// </summary>
    internal static StartupToggleResult SetEnabled(StartupEntry entry, bool enabled)
    {
        if (!entry.Toggleable)
            return new(false, Str.T("Str.StartupNotToggleable"));

        var target = enabled ? "startup-enable" : "startup-disable";
        using var trace = OperationDiagnostics.Begin("startup-audit", Str.T("Str.StartupAuditTarget", entry.Name), null);
        try
        {
            trace.Stage("precheck");
            using var mutation = SystemMutationGate.Acquire();
            trace.Stage("apply");
            var result = SetEnabledCore(entry, enabled);
            trace.Item(entry.Id, result.Ok ? "succeeded" : "failed", result.Ok ? null : result.Message);
            trace.Stage("finalize");
            trace.Settle(result.Ok ? "succeeded" : "failed");
            if (!result.Ok) ErrorLog.Write(target, new InvalidOperationException(result.Message));
            return result;
        }
        catch (Exception ex)
        {
            trace.Fail(ex);
            ErrorLog.Write(target, ex);
            return new(false, PrivacyScrub.Sanitize(ex.Message));
        }
    }

    private static StartupToggleResult SetEnabledCore(StartupEntry entry, bool enabled)
    {
        var livePath = StartupLocations.RegistryPath(entry.Scope, entry.Origin);
        var storeName = StartupLocations.BuildStoreValueName(entry.Scope, entry.Origin, entry.Name);

        if (!enabled)
        {
            var current = RegistryHelper.ReadSnapshot(Microsoft.Win32.RegistryHive.CurrentUser, livePath, entry.Name);
            if (!current.Existed) return new(false, Str.T("Str.StartupToggleMissing"));

            // 先落备份值，再从自启位置删：顺序反了会在两步之间丢掉命令原文。
            RegistryHelper.SetValue(Microsoft.Win32.RegistryHive.CurrentUser, StartupLocations.DisabledStore,
                storeName, current.Value ?? "", current.Kind);
            RegistryHelper.DeleteValue(Microsoft.Win32.RegistryHive.CurrentUser, livePath, entry.Name);

            if (RegistryHelper.ValueExists(Microsoft.Win32.RegistryHive.CurrentUser, livePath, entry.Name))
            {
                // 读回发现还在：把备份撤掉，避免留下「既没停用、又多一份备份」的半成品。
                RegistryHelper.DeleteValue(Microsoft.Win32.RegistryHive.CurrentUser, StartupLocations.DisabledStore, storeName);
                return new(false, Str.T("Str.StartupDisableFailed"));
            }

            return new(true, Str.T("Str.StartupDisabled", entry.Name));
        }

        var stored = RegistryHelper.ReadSnapshot(Microsoft.Win32.RegistryHive.CurrentUser, StartupLocations.DisabledStore, storeName);
        if (!stored.Existed) return new(false, Str.T("Str.StartupEnableMissing"));

        RegistryHelper.SetValue(Microsoft.Win32.RegistryHive.CurrentUser, livePath, entry.Name,
            stored.Value ?? "", stored.Kind);
        if (!RegistryHelper.ValueExists(Microsoft.Win32.RegistryHive.CurrentUser, livePath, entry.Name))
            return new(false, Str.T("Str.StartupEnableFailed"));

        RegistryHelper.DeleteValue(Microsoft.Win32.RegistryHive.CurrentUser, StartupLocations.DisabledStore, storeName);
        return new(true, Str.T("Str.StartupEnabled", entry.Name));
    }

    /// <summary>打开系统自带的启动项管理或服务管理；页面只负责把用户送到能做后续操作的地方。</summary>
    internal static void OpenSystemTool(string id)
    {
        try
        {
            MaintenanceService.Open(id);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("startup-audit-open", ex);
            throw;
        }
    }
}
