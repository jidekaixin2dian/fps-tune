using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 安装信息自检/修复的契约。
///
/// 这组用例全部走注入（目录、注册表读、注册表写、快捷方式存在性/创建），
/// 不写真实注册表、不碰真实开始菜单：测试机上一旦真写，跑一次测试就会往系统里
/// 塞一条「FPS 帧律」卸载项，比不测更糟。
/// </summary>
public sealed class InstallIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fpstune-install-" + Guid.NewGuid().ToString("N"));
    private readonly string _installDir;
    private readonly Dictionary<string, string?> _registry = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Path, string Target)> _shortcuts = new(StringComparer.Ordinal);
    private readonly List<string> _created = new();

    public InstallIntegrityTests()
    {
        _installDir = Path.Combine(_root, "FpsTune");
        Directory.CreateDirectory(_installDir);
        InstallIntegrity.DirectoryOverride = _installDir;

        InstallIntegrity.UninstallValueOverride = name => _registry.TryGetValue(name, out var v) ? v : null;
        InstallIntegrity.UninstallWriteOverride = (_, values) =>
        {
            foreach (var (name, value) in values) _registry[name] = value.ToString();
        };
        InstallIntegrity.ShortcutExistsOverride = path => _shortcuts.ContainsKey(path);
        InstallIntegrity.ShortcutCreateOverride = (path, target) =>
        {
            _created.Add(path);
            _shortcuts[path] = (path, target);
        };
    }

    public void Dispose()
    {
        InstallIntegrity.DirectoryOverride = null;
        InstallIntegrity.UninstallValueOverride = null;
        InstallIntegrity.UninstallWriteOverride = null;
        InstallIntegrity.ShortcutExistsOverride = null;
        InstallIntegrity.ShortcutCreateOverride = null;
        try { Directory.Delete(_root, true); } catch { }
    }

    private void MarkInstalled(params string[] extraFiles)
    {
        File.WriteAllText(Path.Combine(_installDir, "unins000.exe"), "stub");
        File.WriteAllText(Path.Combine(_installDir, "unins000.dat"), "stub");
        File.WriteAllText(Path.Combine(_installDir, "FpsTune.exe"), "stub");
        File.WriteAllText(Path.Combine(_installDir, "LICENSE"), "stub");
        File.WriteAllText(Path.Combine(_installDir, "THIRD-PARTY-NOTICES.txt"), "stub");
        foreach (var extra in extraFiles) File.WriteAllText(Path.Combine(_installDir, extra), "stub");
    }

    private void MarkRegistered()
    {
        _registry["UninstallString"] = "\"" + Path.Combine(_installDir, "unins000.exe") + "\"";
        _shortcuts[InstallIntegrity.StartMenuShortcut] = (InstallIntegrity.StartMenuShortcut, InstallIntegrity.ExecutablePath);
        _shortcuts[InstallIntegrity.DesktopShortcut] = (InstallIntegrity.DesktopShortcut, InstallIntegrity.ExecutablePath);
    }

    [Fact]
    public void Portable_run_is_not_an_install_and_needs_no_repair()
    {
        // 目录里没有 unins000.dat：开发目录、绿色版都算这一类，不该提示用户去「修复安装」。
        Assert.Null(InstallIntegrity.Inspect());
    }

    [Fact]
    public void Complete_install_reports_nothing_missing()
    {
        MarkInstalled();
        MarkRegistered();

        var state = InstallIntegrity.Inspect();

        Assert.NotNull(state);
        Assert.True(state!.IsComplete);
        Assert.Empty(state.Details);
    }

    [Fact]
    public void Missing_uninstall_entry_and_shortcuts_are_all_reported()
    {
        MarkInstalled();

        var state = InstallIntegrity.Inspect();

        Assert.NotNull(state);
        Assert.False(state!.IsComplete);
        Assert.Equal(
            new[] { InstallGap.UninstallEntry, InstallGap.StartMenuShortcut, InstallGap.DesktopShortcut },
            state.Gaps);
    }

    [Fact]
    public void Uninstall_entry_pointing_at_another_directory_counts_as_missing()
    {
        // 用户把程序复制到别处、或者旧版本的卸载项还留着：指向的不是当前目录就等于没登记。
        MarkInstalled();
        MarkRegistered();
        _registry["UninstallString"] = "\"" + Path.Combine(_root, "old", "unins000.exe") + "\"";

        var state = InstallIntegrity.Inspect();

        Assert.NotNull(state);
        Assert.Contains(InstallGap.UninstallEntry, state!.Gaps);
    }

    [Fact]
    public void Repair_restores_uninstall_entry_and_shortcuts_then_reports_complete()
    {
        MarkInstalled();
        var before = InstallIntegrity.Inspect()!;

        var result = InstallIntegrity.Repair(before);

        Assert.True(result.Ok);
        Assert.Contains("DisplayName", _registry.Keys);
        Assert.Equal(InstallIntegrity.AppName, _registry["DisplayName"]);
        Assert.True(_shortcuts.ContainsKey(InstallIntegrity.StartMenuShortcut));
        Assert.True(_shortcuts.ContainsKey(InstallIntegrity.DesktopShortcut));
        // 卸载命令必须指向安装目录自己的 unins000.exe，否则「设置 → 卸载」会跑去别处。
        Assert.Contains(Path.Combine(_installDir, "unins000.exe"), _registry["UninstallString"]);
        Assert.True(InstallIntegrity.Inspect()!.IsComplete);
    }

    [Fact]
    public void Start_menu_shortcut_falls_back_to_the_user_start_menu_without_admin_rights()
    {
        // 公共开始菜单（ProgramData）需要管理员，标准权限的修复写不进去：
        // 回退到用户级开始菜单，开始菜单搜索照样能找到，不再整体报修复失败。
        MarkInstalled();
        InstallIntegrity.ShortcutCreateOverride = (path, target) =>
        {
            if (path == InstallIntegrity.StartMenuShortcut)
                throw new UnauthorizedAccessException("模拟无管理员权限");
            _created.Add(path);
            _shortcuts[path] = (path, target);
        };
        var before = InstallIntegrity.Inspect()!;

        var result = InstallIntegrity.Repair(before);

        Assert.True(result.Ok);
        Assert.True(_shortcuts.ContainsKey(InstallIntegrity.UserStartMenuShortcut));
        Assert.True(_shortcuts.ContainsKey(InstallIntegrity.DesktopShortcut));
        Assert.False(_shortcuts.ContainsKey(InstallIntegrity.StartMenuShortcut));
        Assert.True(InstallIntegrity.Inspect()!.IsComplete);
    }

    [Fact]
    public void Repair_does_not_touch_shortcuts_that_already_exist()
    {
        MarkInstalled();
        MarkRegistered();
        _registry.Remove("UninstallString");
        var before = InstallIntegrity.Inspect()!;

        var result = InstallIntegrity.Repair(before);

        Assert.True(result.Ok);
        Assert.Empty(_created);
        Assert.Equal(2, _shortcuts.Count);
    }

    [Fact]
    public void Stray_files_from_a_development_build_are_counted_but_never_deleted()
    {
        // 真机踩过的那次：安装器记下的是单文件三件套，后来被 dotnet build 产物覆盖。
        MarkInstalled("FpsTune.dll", "FpsTune.pdb", "FpsTune.deps.json");
        MarkRegistered();

        var state = InstallIntegrity.Inspect()!;
        var stray = state.Details.Single(d => d.Gap == InstallGap.StrayFiles);

        Assert.Equal(3, stray.StrayCount);
        Assert.True(InstallIntegrity.Repair(state).Ok);
        Assert.True(File.Exists(Path.Combine(_installDir, "FpsTune.pdb")));
        Assert.True(File.Exists(Path.Combine(_installDir, "FpsTune.dll")));
    }

    [Fact]
    public void Repair_reports_failure_instead_of_claiming_success_when_write_is_denied()
    {
        MarkInstalled();
        InstallIntegrity.UninstallWriteOverride = (_, _) => throw new UnauthorizedAccessException("denied");
        var before = InstallIntegrity.Inspect()!;

        var result = InstallIntegrity.Repair(before);

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
    }
}
