using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Microsoft.Win32;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 自启项审查：命令解析、位置识别、停用/启用往返。
/// 注册表全部走 <see cref="RegistryHelper"/> 的注入点，用例不碰真实系统。
/// </summary>
public class StartupAuditTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --startup", @"C:\Program Files\App\app.exe")]
    [InlineData("C:\\Games\\launcher.exe -silent", @"C:\Games\launcher.exe")]
    [InlineData(@"C:\Program Files\Foo\foo.exe", @"C:\Program Files\Foo\foo.exe")]
    [InlineData("\"C:\\Path With Space\\tool.exe\"", @"C:\Path With Space\tool.exe")]
    [InlineData("explorer.exe \"C:\\some dir\"", @"explorer.exe")]
    [InlineData("  ", null)]
    [InlineData("", null)]
    public void Extracts_the_executable_from_startup_commands(string command, string? expected)
        => Assert.Equal(expected, StartupCommandParser.ExtractExecutable(command));

    [Fact]
    public void Expands_environment_variables_in_startup_commands()
    {
        var parsed = StartupCommandParser.ExtractExecutable("%SystemRoot%\\System32\\rundll32.exe shell32.dll,Control_RunDLL");
        Assert.NotNull(parsed);
        Assert.EndsWith(@"\System32\rundll32.exe", parsed!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%", parsed!, StringComparison.Ordinal);
    }

    [Fact]
    public void Entry_ids_survive_a_round_trip_even_with_colons_in_the_name()
    {
        var id = StartupLocations.BuildId(StartupScope.AllUsers, StartupOrigin.RunOnce, "Vendor:Updater");
        Assert.Equal(StartupScope.AllUsers, StartupLocations.ScopeOf(id));
        Assert.Equal(StartupOrigin.RunOnce, StartupLocations.OriginOf(id));
        Assert.Equal("Vendor:Updater", StartupLocations.NameOf(id));
        Assert.NotEqual(id, StartupLocations.BuildId(StartupScope.CurrentUser, StartupOrigin.RunOnce, "Vendor:Updater"));
    }

    [Fact]
    public void Only_current_user_registry_entries_are_toggleable()
    {
        Assert.True(Entry(StartupScope.CurrentUser, StartupOrigin.Run).Toggleable);
        Assert.True(Entry(StartupScope.CurrentUser, StartupOrigin.RunOnce).Toggleable);
        Assert.False(Entry(StartupScope.AllUsers, StartupOrigin.Run).Toggleable);
        Assert.False(Entry(StartupScope.CurrentUser, StartupOrigin.Folder).Toggleable);
    }

    [Fact]
    public void Missing_program_and_temp_folder_are_reported_without_guessing()
    {
        var temp = new StartupEntry("run:hkcu:Tool", "Tool", @"C:\Users\me\AppData\Local\Temp\x\setup.exe",
            StartupScope.CurrentUser, StartupOrigin.Run, true, @"C:\Users\me\AppData\Local\Temp\x\setup.exe");
        Assert.False(temp.ExecutableExists);
        Assert.True(temp.PointsAtTemporaryFolder);

        // 命令里解不出路径：显示为未知，不报「程序不存在」。
        var unknown = new StartupEntry("run:hkcu:Odd", "Odd", "rundll32.exe shell32.dll,Control_RunDLL",
            StartupScope.CurrentUser, StartupOrigin.Run, true, null);
        Assert.Null(unknown.ExecutableExists);
        Assert.False(unknown.PointsAtTemporaryFolder);
    }

    [Fact]
    public void Disabling_moves_the_command_into_our_store_and_enabling_puts_it_back()
    {
        using var registry = new FakeRegistry();
        var command = "\"C:\\Steam\\steam.exe\" -silent";
        registry.Values[(RegistryHive.CurrentUser, StartupLocations.CurrentUserRun, "Steam")] = (command, RegistryValueKind.String);
        var entry = new StartupEntry(StartupLocations.BuildId(StartupScope.CurrentUser, StartupOrigin.Run, "Steam"),
            "Steam", command, StartupScope.CurrentUser, StartupOrigin.Run, Enabled: true, @"C:\Steam\steam.exe");
        var storeName = StartupLocations.BuildStoreValueName(StartupScope.CurrentUser, StartupOrigin.Run, "Steam");

        var disabled = StartupAuditService.SetEnabled(entry, enabled: false);
        Assert.True(disabled.Ok);
        Assert.False(registry.Values.ContainsKey((RegistryHive.CurrentUser, StartupLocations.CurrentUserRun, "Steam")));
        var stored = registry.Values[(RegistryHive.CurrentUser, StartupLocations.DisabledStore, storeName)];
        Assert.Equal(command, stored.Value);
        Assert.Equal(RegistryValueKind.String, stored.Kind);

        var enabled = StartupAuditService.SetEnabled(entry with { Enabled = false }, enabled: true);
        Assert.True(enabled.Ok);
        Assert.Equal(command, registry.Values[(RegistryHive.CurrentUser, StartupLocations.CurrentUserRun, "Steam")].Value);
        Assert.False(registry.Values.ContainsKey((RegistryHive.CurrentUser, StartupLocations.DisabledStore, storeName)));
    }

    [Fact]
    public void Disabling_something_that_is_already_gone_reports_a_next_step_instead_of_writing()
    {
        using var registry = new FakeRegistry();
        var entry = new StartupEntry(StartupLocations.BuildId(StartupScope.CurrentUser, StartupOrigin.Run, "Gone"),
            "Gone", "C:\\Gone\\gone.exe", StartupScope.CurrentUser, StartupOrigin.Run, true, @"C:\Gone\gone.exe");

        var result = StartupAuditService.SetEnabled(entry, enabled: false);

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Empty(registry.Values);
    }

    [Fact]
    public void Enabling_without_a_stored_command_fails_instead_of_inventing_one()
    {
        using var registry = new FakeRegistry();
        var entry = new StartupEntry(StartupLocations.BuildId(StartupScope.CurrentUser, StartupOrigin.Run, "Ghost"),
            "Ghost", "C:\\Ghost\\ghost.exe", StartupScope.CurrentUser, StartupOrigin.Run, false, @"C:\Ghost\ghost.exe");

        var result = StartupAuditService.SetEnabled(entry, enabled: true);

        Assert.False(result.Ok);
        Assert.Empty(registry.Values);
    }

    [Fact]
    public void Machine_scope_entries_are_refused_before_any_registry_access()
    {
        using var registry = new FakeRegistry();
        var result = StartupAuditService.SetEnabled(Entry(StartupScope.AllUsers, StartupOrigin.Run, "Vendor"), enabled: false);
        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Empty(registry.Values);
    }

    [Fact]
    public void Snapshot_counts_reflect_enabled_disabled_and_missing_items()
    {
        // 前两条指向真实存在的文件（本测试程序集），只有最后一条是故意指向不存在的路径。
        var existing = typeof(StartupAuditTests).Assembly.Location;
        var snapshot = new StartupAuditSnapshot(
        [
            Entry(StartupScope.CurrentUser, StartupOrigin.Run, "Steam") with { Command = existing, Executable = existing },
            Entry(StartupScope.CurrentUser, StartupOrigin.Run, "Off") with { Enabled = false, Command = existing, Executable = existing },
            new StartupEntry("run:hkcu:Gone", "Gone", @"C:\Gone\gone.exe", StartupScope.CurrentUser,
                StartupOrigin.Run, true, @"C:\Gone\gone.exe")
        ], 1, DateTimeOffset.Now);

        Assert.Equal(2, snapshot.EnabledCount);
        Assert.Equal(1, snapshot.DisabledCount);
        Assert.Equal(1, snapshot.MissingCount);
    }

    private static StartupEntry Entry(StartupScope scope, StartupOrigin origin, string name = "Item")
        => new(StartupLocations.BuildId(scope, origin, name), name, $@"C:\Apps\{name}.exe", scope, origin,
            Enabled: true, $@"C:\Apps\{name}.exe");

    /// <summary>只活在用例里的注册表替身：记录值、类型，支持读/写/删。</summary>
    private sealed class FakeRegistry : IDisposable
    {
        internal readonly Dictionary<(RegistryHive Hive, string Path, string Name), (object Value, RegistryValueKind Kind)> Values = [];

        internal FakeRegistry()
        {
            RegistryHelper.SnapshotOverride = (hive, path, name) =>
                Values.TryGetValue((hive, path, name), out var value)
                    ? new RegistryHelper.Snapshot(true, value.Value, value.Kind)
                    : new RegistryHelper.Snapshot(false, null, RegistryValueKind.None);
            RegistryHelper.SetOverride = (hive, path, name, value, kind) =>
                Values[(hive, path, name)] = (value, kind);
            RegistryHelper.DeleteOverride = (hive, path, name) => Values.Remove((hive, path, name));
        }

        public void Dispose()
        {
            RegistryHelper.SnapshotOverride = null;
            RegistryHelper.SetOverride = null;
            RegistryHelper.DeleteOverride = null;
        }
    }
}
