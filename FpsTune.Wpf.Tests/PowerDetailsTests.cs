using System.ComponentModel;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 电源细项（WP1）的契约测试：枚举映射、两级隐藏位、取值域校验、写后读回、
/// journal 往返与损坏留档、恢复只碰改过的项、显隐逐项计数，另加一次离屏渲染 + 真实动作。
/// </summary>
[Collection("BackupService serial")]
public sealed class PowerDetailsTests : IDisposable
{
    private const string SchemeText = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private static readonly Guid Scheme = Guid.Parse(SchemeText);
    private static readonly Guid OtherScheme = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid ProcessorGroup = Guid.Parse("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid VideoGroup = Guid.Parse("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid MaxState = Guid.Parse("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid BoostMode = Guid.Parse("be337238-0d82-4146-a960-4f3749d470c7");
    private static readonly Guid DisplayOff = Guid.Parse("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    private static readonly Guid UnknownSetting = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fpstune-power-" + Guid.NewGuid().ToString("N"));
    private readonly FakeApi _api = new();
    private Func<Guid, Guid?, bool, PowerVisibilityWrite> _registryWriter =
        (_, _, _) => new PowerVisibilityWrite(PowerVisibilityStatus.Updated, null);

    public PowerDetailsTests()
    {
        Directory.CreateDirectory(_directory);
        PowerDetailsJournal.PathOverride = Path.Combine(_directory, "power-details.json");
        PowerSettingCatalog.ApiOverride = _api;
        PowerSettingCatalog.RegistryReadOverride = (group, setting) =>
            Registry.TryGetValue((group, setting), out var info) ? info : new PowerSettingRegistryInfo(null, null, null);
        PowerSettingCatalog.RegistryWriteOverride = (group, setting, visible) =>
        {
            RegistryWrites.Add((group, setting, visible));
            return _registryWriter(group, setting, visible);
        };
        PowerSettingWriter.ActiveSchemeOverride = () => Scheme;
    }

    internal readonly Dictionary<(Guid Group, Guid? Setting), PowerSettingRegistryInfo> Registry = new();
    internal readonly List<(Guid Group, Guid? Setting, bool Visible)> RegistryWrites = new();

    public void Dispose()
    {
        PowerSettingCatalog.ApiOverride = null;
        PowerSettingCatalog.RegistryReadOverride = null;
        PowerSettingCatalog.RegistryWriteOverride = null;
        PowerSettingWriter.ActiveSchemeOverride = null;
        PowerDetailsJournal.PathOverride = null;
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch (IOException) { }
    }

    // ---------------------------------------------------------------- 枚举映射

    [Fact]
    public void Catalog_maps_groups_values_domain_and_common_flag()
    {
        Seed();
        var api = _api;
        api.Group(ProcessorGroup, "处理器电源管理");
        api.Setting(ProcessorGroup, MaxState, "最大处理器状态", setting =>
        {
            setting.Minimum = 5;
            setting.Maximum = 100;
            setting.Increment = 5;
            setting.Ac = 100;
            setting.Dc = 50;
            setting.AcDefault = 100;
            setting.DcDefault = 100;
            setting.Description = "限制处理器最高性能状态";
        });
        api.Group(VideoGroup, "显示");
        api.Setting(VideoGroup, UnknownSetting, "关闭显示器", setting =>
        {
            setting.Possible = [new(0, "从不"), new(60, "1 分钟"), new(300, "5 分钟")];
            setting.Ac = 60;
            setting.Dc = 300;
            setting.AcDefault = 0;
            setting.DcDefault = 0;
        });

        var scan = PowerSettingCatalog.Load(Scheme);

        Assert.False(scan.Failed);
        Assert.Equal(2, scan.Entries.Count);
        var max = scan.Entries.Single(entry => entry.Setting == MaxState);
        Assert.True(max.IsCommon);
        Assert.Equal("最大处理器状态", max.Name);
        Assert.Equal("处理器电源管理", max.SubgroupName);
        Assert.Equal("限制处理器最高性能状态", max.Description);
        Assert.Equal((5u, 100u, 5u), (max.Minimum, max.Maximum, max.Increment));
        Assert.Empty(max.PossibleValues);
        Assert.True(max.DomainKnown);
        Assert.Equal((100u, 50u, 100u, 100u), (max.Ac!.Value, max.Dc!.Value, max.AcDefault!.Value, max.DcDefault!.Value));
        Assert.True(max.Changed);
        Assert.False(max.ReadFailed);

        var display = scan.Entries.Single(entry => entry.Setting == UnknownSetting);
        Assert.False(display.IsCommon);
        Assert.Equal(3, display.PossibleValues.Count);
        Assert.Equal([0u, 60u, 300u], display.PossibleValues.Select(value => value.Value));
        Assert.Equal("1 分钟", display.PossibleValues[1].Display);
        Assert.True(display.DomainKnown);
        Assert.True(display.Changed);
        Assert.Equal("1 分钟", PowerDetailsWindow.ValueText(display, display.Ac));
        Assert.Equal("5 分钟", PowerDetailsWindow.ValueText(display, display.Dc));

        // 常用设置排在前面（分组标题里也是先「常用设置」再「其他设置」）。
        Assert.Equal(MaxState, scan.Entries[0].Setting);
    }

    [Fact]
    public void Hidden_flags_cover_item_and_group_attributes()
    {
        Seed();
        _api.Group(ProcessorGroup, "处理器电源管理");
        _api.Setting(ProcessorGroup, MaxState, "最大处理器状态", _ => { });
        _api.Setting(ProcessorGroup, BoostMode, "处理器性能提升模式", _ => { });
        _api.Group(VideoGroup, "显示");
        _api.Setting(VideoGroup, DisplayOff, "关闭显示器", _ => { });

        Registry[(ProcessorGroup, MaxState)] = new PowerSettingRegistryInfo(1, null, null);          // 项级隐藏
        Registry[(ProcessorGroup, BoostMode)] = new PowerSettingRegistryInfo(3, null, null);         // 隐藏位 + 显式显示位
        Registry[(VideoGroup, null)] = new PowerSettingRegistryInfo(null, 1, null);            // 组级隐藏

        var scan = PowerSettingCatalog.Load(Scheme);

        var max = scan.Entries.Single(entry => entry.Setting == MaxState);
        Assert.True(max.HiddenByItem);
        Assert.False(max.HiddenByGroup);
        Assert.True(max.Hidden);

        var boost = scan.Entries.Single(entry => entry.Setting == BoostMode);
        Assert.False(boost.HiddenByItem);
        Assert.False(boost.Hidden);

        var displayOff = scan.Entries.Single(entry => entry.Setting == DisplayOff);
        Assert.True(displayOff.HiddenByGroup);
        Assert.True(displayOff.Hidden);
        Assert.Equal(Str.T("Str.PowerDetailsGroupHiddenBadge"),
            new PowerSettingRow(displayOff).HiddenBadgeText);
        Assert.Equal(Str.T("Str.PowerDetailsHiddenBadge"), new PowerSettingRow(max).HiddenBadgeText);
    }

    [Fact]
    public void Unreadable_name_keeps_the_rest_of_the_table()
    {
        Seed();
        _api.Group(ProcessorGroup, "处理器电源管理");
        _api.Setting(ProcessorGroup, MaxState, "最大处理器状态", setting => setting.Ac = 100);
        _api.Setting(ProcessorGroup, BoostMode, "处理器性能提升模式", setting => setting.Ac = 2);
        _api.ThrowingNames.Add((ProcessorGroup, BoostMode));

        var scan = PowerSettingCatalog.Load(Scheme);

        Assert.False(scan.Failed);
        Assert.Equal(2, scan.Entries.Count);
        var broken = scan.Entries.Single(entry => entry.Setting == BoostMode);
        Assert.True(broken.NameReadFailed);
        Assert.True(broken.ReadFailed);
        Assert.Equal(2u, broken.Ac!.Value);                                  // 取值仍然读到了
        Assert.Equal(Str.T("Str.PowerDetailsUnreadable"), PowerDetailsWindow.DisplayName(broken));
        Assert.Equal(Visibility.Visible, new PowerSettingRow(broken).UnreadableBadgeVisibility);

        var healthy = scan.Entries.Single(entry => entry.Setting == MaxState);
        Assert.False(healthy.ReadFailed);
        Assert.Equal("最大处理器状态", PowerDetailsWindow.DisplayName(healthy));
    }

    [Fact]
    public void Unreadable_values_mark_only_that_setting()
    {
        Seed();
        _api.Group(ProcessorGroup, "处理器电源管理");
        _api.Setting(ProcessorGroup, MaxState, "最大处理器状态", setting => setting.Ac = 100);
        _api.Setting(ProcessorGroup, BoostMode, "处理器性能提升模式", setting => setting.FailValues = true);

        var scan = PowerSettingCatalog.Load(Scheme);

        var broken = scan.Entries.Single(entry => entry.Setting == BoostMode);
        Assert.True(broken.ValuesReadFailed);
        Assert.True(broken.ReadFailed);
        Assert.Null(broken.Ac);
        Assert.Null(broken.Dc);
        Assert.Equal("处理器性能提升模式", PowerDetailsWindow.DisplayName(broken));
        Assert.Equal(Visibility.Visible, new PowerSettingRow(broken).UnreadableBadgeVisibility);
        Assert.False(scan.Entries.Single(entry => entry.Setting == MaxState).ReadFailed);
    }

    // ---------------------------------------------------------------- 校验

    [Fact]
    public void Validation_rejects_step_range_and_option_mismatch()
    {
        Seed();
        _api.Group(ProcessorGroup, "处理器电源管理");
        _api.Setting(ProcessorGroup, MaxState, "最大处理器状态", setting =>
        {
            setting.Minimum = 5;
            setting.Maximum = 100;
            setting.Increment = 5;
        });
        _api.Group(VideoGroup, "显示");
        _api.Setting(VideoGroup, DisplayOff, "关闭显示器", setting =>
            setting.Possible = [new(0, "从不"), new(60, "1 分钟")]);

        var scan = PowerSettingCatalog.Load(Scheme);
        var numeric = scan.Entries.Single(entry => entry.Setting == MaxState);
        var enumerated = scan.Entries.Single(entry => entry.Setting == DisplayOff);

        Assert.Equal(PowerValueIssue.None, PowerSettingCatalog.Check(numeric, "15").Issue);
        Assert.Equal(15u, PowerSettingCatalog.Check(numeric, "15").Value);
        Assert.Equal(PowerValueIssue.StepMismatch, PowerSettingCatalog.Check(numeric, "17").Issue);
        Assert.Equal(15u, PowerSettingCatalog.Check(numeric, "17").Nearest!.Value);
        Assert.Equal(PowerValueIssue.OutOfRange, PowerSettingCatalog.Check(numeric, "105").Issue);
        Assert.Equal(PowerValueIssue.OutOfRange, PowerSettingCatalog.Check(numeric, "0").Issue);
        Assert.Equal(PowerValueIssue.NotANumber, PowerSettingCatalog.Check(numeric, "abc").Issue);

        Assert.Equal(PowerValueIssue.None, PowerSettingCatalog.Check(enumerated, "60").Issue);
        Assert.Equal(PowerValueIssue.NotAnOption, PowerSettingCatalog.Check(enumerated, "30").Issue);
        Assert.Equal(Str.T("Str.PowerDetailsInvalidNumber"), PowerDetailsWindow.IssueText(numeric, PowerSettingCatalog.Check(numeric, "x")));
        Assert.Equal(Str.T("Str.PowerDetailsRange"), PowerDetailsWindow.DomainText(numeric));
        Assert.Equal(Str.T("Str.PowerDetailsPossible"), PowerDetailsWindow.DomainText(enumerated));
    }

    // ---------------------------------------------------------------- 写入

    [Fact]
    public void Write_reports_readback_mismatch()
    {
        Seed();
        _api.Group(ProcessorGroup, "处理器电源管理");
        _api.Setting(ProcessorGroup, MaxState, "最大处理器状态", setting => { setting.Ac = 100; setting.Dc = 100; });
        var entry = PowerSettingCatalog.Load(Scheme).Entries.Single();
        _api.ReadBackMismatch.Add((ProcessorGroup, MaxState));

        var result = PowerSettingWriter.Apply(Scheme, entry, 20, null);

        Assert.False(result.Ok);
        Assert.False(result.AcPersisted);
        Assert.False(result.Partial);
        Assert.Equal(Str.T("Str.PowerDetailsNotPersisted", "最大处理器状态"), result.AcError);
        Assert.Contains(("ac", 20u), _api.Writes);
    }

    [Fact]
    public void Write_reports_partial_success_and_keeps_ac()
    {
        Seed();
        _api.Group(ProcessorGroup, "处理器电源管理");
        _api.Setting(ProcessorGroup, MaxState, "最大处理器状态", setting => { setting.Ac = 100; setting.Dc = 100; });
        var entry = PowerSettingCatalog.Load(Scheme).Entries.Single();
        _api.WriteFailures.Add(((ProcessorGroup, MaxState), false));   // DC 写失败

        var result = PowerSettingWriter.Apply(Scheme, entry, 20, 40);

        Assert.False(result.Ok);
        Assert.True(result.AcPersisted);
        Assert.False(result.DcPersisted);
        Assert.True(result.Partial);
        Assert.Null(result.AcError);
        Assert.NotNull(result.DcError);
        Assert.Equal(20u, _api.Settings[(ProcessorGroup, MaxState)].Ac);      // AC 没有被回滚
        Assert.Equal(100u, _api.Settings[(ProcessorGroup, MaxState)].Dc);     // DC 保持原值
        Assert.Equal(Str.T("Str.PowerDetailsAppliedPartial"), PartialText(result));
    }

    private static string PartialText(PowerWriteResult result)
        => result.Partial && result.AcPersisted && !result.DcPersisted ? Str.T("Str.PowerDetailsAppliedPartial") : "";

    [Fact]
    public void Write_refuses_non_active_scheme()
    {
        Seed();
        _api.Group(ProcessorGroup, "处理器电源管理");
        _api.Setting(ProcessorGroup, MaxState, "最大处理器状态", _ => { });
        var entry = PowerSettingCatalog.Load(Scheme).Entries.Single();

        PowerSettingWriter.ActiveSchemeOverride = () => OtherScheme;
        Assert.Throws<InvalidOperationException>(() => PowerSettingWriter.Apply(Scheme, entry, 20, 20));
        Assert.Empty(_api.Writes);

        PowerSettingWriter.ActiveSchemeOverride = () => null;
        Assert.Throws<InvalidOperationException>(() => PowerSettingWriter.Apply(Scheme, entry, 20, 20));
        Assert.Empty(_api.Writes);
    }

    [Fact]
    public void Write_never_touches_a_side_without_a_readable_original()
    {
        Seed();
        _api.Group(ProcessorGroup, "Processor");
        _api.Setting(ProcessorGroup, MaxState, "Maximum", setting => { setting.Ac = 100; setting.Dc = 90; });
        var entry = PowerSettingCatalog.Load(Scheme).Entries.Single();
        _api.Settings[(ProcessorGroup, MaxState)].FailValues = true;

        var result = PowerSettingWriter.Apply(Scheme, entry, 20, 30);

        Assert.False(result.Ok);
        Assert.NotNull(result.AcError);
        Assert.NotNull(result.DcError);
        Assert.Empty(_api.Writes);
        Assert.Empty(PowerDetailsJournal.Load());
    }

    [Fact]
    public void Restore_preserves_untouched_side_and_retries_activation_after_failure()
    {
        Seed();
        _api.Group(ProcessorGroup, "Processor");
        _api.Setting(ProcessorGroup, MaxState, "Maximum", setting => { setting.Ac = 100; setting.Dc = 90; });
        var entry = PowerSettingCatalog.Load(Scheme).Entries.Single();
        Assert.True(PowerSettingWriter.Apply(Scheme, entry, 20, 90).Ok);
        Assert.Null(Assert.Single(PowerDetailsJournal.Load()).Dc);
        _api.Settings[(ProcessorGroup, MaxState)].Dc = 70;
        _api.ActiveSetError = 5;

        var failed = PowerSettingWriter.Restore();

        Assert.False(failed.Applied);
        Assert.Equal(1, failed.Failed);
        Assert.Single(PowerDetailsJournal.Load());
        Assert.Equal(70u, _api.Settings[(ProcessorGroup, MaxState)].Dc);

        _api.ActiveSetError = 0;
        Assert.Equal(1, PowerSettingWriter.Restore().Restored);
        Assert.Empty(PowerDetailsJournal.Load());
        Assert.Equal(100u, _api.Settings[(ProcessorGroup, MaxState)].Ac);
        Assert.Equal(70u, _api.Settings[(ProcessorGroup, MaxState)].Dc);
    }

    // ---------------------------------------------------------------- journal

    [Fact]
    public void Journal_keeps_first_snapshot_and_archives_corrupt_file()
    {
        Seed();
        _api.Group(ProcessorGroup, "处理器电源管理");
        _api.Setting(ProcessorGroup, MaxState, "最大处理器状态", setting => { setting.Ac = 100; setting.Dc = 90; });
        var entry = PowerSettingCatalog.Load(Scheme).Entries.Single();

        PowerSettingWriter.Apply(Scheme, entry, 20, 20);
        var snapshot = PowerDetailsJournal.Load();
        var recorded = Assert.Single(snapshot);
        Assert.Equal(SchemeText, recorded.Scheme);
        Assert.Equal(ProcessorGroup.ToString("D"), recorded.Subgroup);
        Assert.Equal(MaxState.ToString("D"), recorded.Setting);
        Assert.Equal(100u, recorded.Ac!.Value);
        Assert.Equal(90u, recorded.Dc!.Value);
        Assert.Equal("最大处理器状态", recorded.Name);

        // 再改一次：快照仍是第一次改动前的值，否则「恢复」只能退回上一次改动。
        PowerSettingWriter.Apply(Scheme, entry, 35, 35);
        Assert.Equal(100u, Assert.Single(PowerDetailsJournal.Load()).Ac!.Value);

        File.WriteAllText(PowerDetailsJournal.JournalPath, "{ this is not json", new UTF8Encoding(false));
        Assert.Empty(PowerDetailsJournal.Load());
        Assert.True(File.Exists(PowerDetailsJournal.JournalPath + ".corrupt"));
    }

    [Fact]
    public void Restore_only_writes_journaled_items_of_the_active_scheme()
    {
        Seed();
        _api.Group(ProcessorGroup, "处理器电源管理");
        _api.Setting(ProcessorGroup, MaxState, "最大处理器状态", setting => { setting.Ac = 100; setting.Dc = 90; });
        _api.Group(VideoGroup, "显示");
        _api.Setting(VideoGroup, DisplayOff, "关闭显示器", setting => { setting.Ac = 60; setting.Dc = 60; });

        var entries = PowerSettingCatalog.Load(Scheme).Entries;
        var max = entries.Single(entry => entry.Setting == MaxState);
        var displayOff = entries.Single(entry => entry.Setting == DisplayOff);
        PowerSettingWriter.Apply(Scheme, max, 20, 20);
        PowerSettingWriter.Apply(Scheme, displayOff, 120, 120);
        Assert.Equal(2, PowerDetailsJournal.Load().Count);

        // 混一条别的方案上的快照进去：只允许恢复活动方案，别的方案跳过。
        var document = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(PowerDetailsJournal.JournalPath));
        var list = new List<object>();
        foreach (var element in document.GetProperty("Entries").EnumerateArray())
            list.Add(element.Clone());
        list.Add(new { Scheme = OtherScheme.ToString("D"), Subgroup = ProcessorGroup.ToString("D"), Setting = UnknownSetting.ToString("D"), Ac = (uint?)11, Dc = (uint?)11, Name = "别的方案", At = DateTimeOffset.Now });
        File.WriteAllText(PowerDetailsJournal.JournalPath,
            JsonSerializer.Serialize(new { Version = 1, Entries = list }), new UTF8Encoding(false));

        _api.Writes.Clear();
        var result = PowerSettingWriter.Restore();

        Assert.Equal(2, result.Restored);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Failed);
        Assert.Equal(4, _api.Writes.Count);                                       // 活动方案两项 x (AC+DC)，别的方案一项不写
        Assert.Equal(100u, _api.Settings[(ProcessorGroup, MaxState)].Ac);
        Assert.Equal(60u, _api.Settings[(VideoGroup, DisplayOff)].Ac);
        var remaining = Assert.Single(PowerDetailsJournal.Load());
        Assert.Equal(OtherScheme.ToString("D"), remaining.Scheme);
    }

    [Fact]
    public void Restore_with_empty_journal_writes_nothing()
    {
        Seed();
        Assert.False(File.Exists(PowerDetailsJournal.JournalPath));

        var result = PowerSettingWriter.Restore();

        Assert.Equal(0, result.Restored);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.Skipped);
        Assert.Empty(_api.Writes);
        Assert.Equal(0, _api.ActiveSetCalls);
        Assert.False(File.Exists(PowerDetailsJournal.JournalPath));
    }

    [Fact]
    public void Shared_recovery_lists_and_restores_power_details()
    {
        var root = UserDataPaths.RootOverride;
        UserDataPaths.RootOverride = _directory;
        try
        {
            Seed();
            _api.Group(ProcessorGroup, "Processor");
            _api.Setting(ProcessorGroup, MaxState, "Maximum", setting => { setting.Ac = 100; setting.Dc = 90; });
            var entry = PowerSettingCatalog.Load(Scheme).Entries.Single();
            Assert.True(PowerSettingWriter.Apply(Scheme, entry, 20, null).Ok);

            Assert.Contains(RecoveryCoordinator.Inventory(), scope => scope.Module == "power-details");
            var result = RecoveryCoordinator.RestoreAll();

            Assert.Empty(result.Failures);
            Assert.Contains(result.Restored, item => item.File == Str.T("Str.PowerDetailsTitle"));
            Assert.Equal(100u, _api.Settings[(ProcessorGroup, MaxState)].Ac);
            Assert.Empty(PowerDetailsJournal.Load());
        }
        finally { UserDataPaths.RootOverride = root; }
    }

    [Fact]
    public void Restore_preserves_a_value_changed_outside_the_app()
    {
        Seed();
        _api.Group(ProcessorGroup, "Processor");
        _api.Setting(ProcessorGroup, MaxState, "Maximum", setting => { setting.Ac = 100; setting.Dc = 90; });
        var entry = PowerSettingCatalog.Load(Scheme).Entries.Single();
        Assert.True(PowerSettingWriter.Apply(Scheme, entry, 70, null).Ok);
        _api.Settings[(ProcessorGroup, MaxState)].Ac = 80;
        _api.Writes.Clear();

        Assert.Equal(1, PowerSettingWriter.Restore().Failed);
        Assert.Equal(80u, _api.Settings[(ProcessorGroup, MaxState)].Ac);
        Assert.Single(PowerDetailsJournal.Load());
        Assert.Empty(_api.Writes);
    }

    // ---------------------------------------------------------------- 显隐

    [Fact]
    public void Reveal_all_skips_when_nothing_hidden_and_counts_failures()
    {
        Seed();
        _api.Group(ProcessorGroup, "处理器电源管理");
        _api.Setting(ProcessorGroup, MaxState, "最大处理器状态", _ => { });
        _api.Setting(ProcessorGroup, BoostMode, "处理器性能提升模式", _ => { });
        _api.Group(VideoGroup, "显示");
        _api.Setting(VideoGroup, DisplayOff, "关闭显示器", _ => { });
        var entries = PowerSettingCatalog.Load(Scheme).Entries;

        // 没有任何隐藏项：一个注册表写都不该发生。
        var none = PowerSettingWriter.RevealAllHidden(entries);
        Assert.Equal(0, none.Total);
        Assert.Empty(RegistryWrites);

        Registry[(ProcessorGroup, MaxState)] = new PowerSettingRegistryInfo(1, null, null);
        Registry[(ProcessorGroup, BoostMode)] = new PowerSettingRegistryInfo(1, null, null);
        Registry[(VideoGroup, null)] = new PowerSettingRegistryInfo(null, 1, null);
        var hidden = PowerSettingCatalog.Load(Scheme).Entries;
        Assert.Equal(3, hidden.Count(entry => entry.Hidden));

        _registryWriter = (group, setting, _) =>
            (group, setting) switch
            {
                (_, var target) when group == ProcessorGroup && target == BoostMode => new PowerVisibilityWrite(PowerVisibilityStatus.Denied, "denied"),
                (var owner, null) when owner == VideoGroup => new PowerVisibilityWrite(PowerVisibilityStatus.Missing, null),
                _ => new PowerVisibilityWrite(PowerVisibilityStatus.Updated, null)
            };

        var result = PowerSettingWriter.RevealAllHidden(hidden);

        Assert.Equal(1, result.Revealed);   // MaxState 成功
        Assert.Equal(1, result.Denied);     // BoostMode 没权限
        Assert.Equal(1, result.Missing);    // 显示组在注册表里没有对应项
        Assert.Equal(3, result.Total);
        Assert.DoesNotContain(RegistryWrites, write => write.Setting == MaxState && !write.Visible);
    }

    [Fact]
    public void Visibility_write_is_skipped_when_already_in_target_state()
    {
        Seed();
        _api.Group(ProcessorGroup, "处理器电源管理");
        _api.Setting(ProcessorGroup, MaxState, "最大处理器状态", _ => { });
        var entry = PowerSettingCatalog.Load(Scheme).Entries.Single();

        var result = PowerSettingWriter.SetVisibility(entry, visible: true);

        Assert.Equal(PowerVisibilityStatus.Unchanged, result.Status);
        Assert.Empty(RegistryWrites);
    }

    // ---------------------------------------------------------------- 离屏渲染 + 真实动作

    [Fact]
    public void Panel_renders_and_runs_actions_without_rescanning_on_filter()
    {
        const string testName = nameof(Panel_renders_and_runs_actions_without_rescanning_on_filter);
        if (Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") != "1")
        {
            DlssFeedbackTests.RunIsolated(testName, typeof(PowerDetailsTests));
            return;
        }

        Exception? error = null;
        var thread = new Thread(() =>
        {
            Application? app = null;
            PowerDetailsWindow? window = null;
            try
            {
                var root = Path.Combine(Path.GetTempPath(), "fpstune-power-ui-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                UserDataPaths.RootOverride = root;
                PowerDetailsJournal.PathOverride = Path.Combine(root, "power-details.json");
                SeedUiFixture();

                // 低配模式关掉主题过渡动画，离屏渲染才能拿到稳定的画笔颜色。
                UiPerformance.LowSpec = true;
                app = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown,
                    Resources = DlssFeedbackTests.LoadResources()
                };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                LangService.Apply(LangService.ZhCn);

                window = new PowerDetailsWindow();
                DlssFeedbackTests.PrepareBackgroundWindow(window);
                window.Show();
                var list = (ListBox)window.FindName("SettingList");
                DlssFeedbackTests.PumpUntil(() => list.Items.Count > 0);

                Assert.Equal(900, window.MinWidth);
                Assert.Equal(600, window.MinHeight);
                Assert.Equal(Str.T("Str.PowerDetailsCount", list.Items.Count), ((TextBlock)window.FindName("CountText")).Text);
                Assert.Equal(2, ((ICollectionView)list.ItemsSource).Groups.Count);   // 常用设置 / 其他设置
                Assert.Contains(Str.T("Str.PowerDetailsPlanLabel", "平衡"), ((TextBlock)window.FindName("PlanLabel")).Text, StringComparison.Ordinal);

                var scans = _api.GroupEnumerationCalls;
                // 选中一行 → 右侧详情出现（IsSynchronizedWithCurrentItem=False，不会自己选中）。
                var selectionChanges = 0;
                list.SelectionChanged += (_, _) => selectionChanges++;
                list.SelectedIndex = 0;
                window.UpdateLayout();
                Assert.True(selectionChanges > 0, "选中行没有触发 SelectionChanged（items=" + list.Items.Count + "）");
                Assert.NotNull(list.SelectedItem);
                Assert.IsType<PowerSettingRow>(list.SelectedItem);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("DetailsPanel")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("SelectPrompt")).Visibility);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("DetailsPanel")).Visibility);
                Assert.Empty(((TextBlock)window.FindName("StatusText")).Text);

                // 读不到名称的那一条仍然列出并显示「没有读到」；搜索/筛选只过滤内存表，不重新枚举。
                ((TextBox)window.FindName("SearchBox")).Text = "OEM";
                window.UpdateLayout();
                Assert.Single(list.Items);
                Assert.Equal(Str.T("Str.PowerDetailsUnreadable"), ((PowerSettingRow)list.Items[0]).DisplayName);
                Assert.Equal(scans, _api.GroupEnumerationCalls);
                DlssFeedbackTests.PumpUntil(() => VisibleText(window).Any(text => text.Text == Str.T("Str.PowerDetailsUnreadable")));

                ((RadioButton)window.FindName("FilterHidden")).IsChecked = true;
                window.UpdateLayout();
                Assert.True(list.Items.Count >= 1);
                Assert.Equal(scans, _api.GroupEnumerationCalls);

                ((RadioButton)window.FindName("FilterAll")).IsChecked = true;
                ((TextBox)window.FindName("SearchBox")).Text = "";
                window.UpdateLayout();

                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.SetMode(theme);
                    list.SelectedItem = RowOf(list, MaxState);
                    window.UpdateLayout();
                    Render((FrameworkElement)window.Content, "power-details-" + theme);
                    AssertReadable(window);

                    // 最小窗口 900x600 也要能看：标题、搜索、筛选、批量动作与详情都不能互相压掉。
                    window.Width = 900;
                    window.Height = 600;
                    window.UpdateLayout();
                    Render((FrameworkElement)window.Content, "power-details-min-" + theme);
                    AssertReadable(window);
                    window.Width = 1040;
                    window.Height = 720;
                    window.UpdateLayout();
                }

                ThemeManager.SetMode(ThemeManager.CurrentMode);

                // 写入 AC + DC（勾「两边写同一个值」）：按 GUID 锁定「最大处理器状态」那一行。
                list.SelectedItem = RowOf(list, MaxState);
                window.UpdateLayout();
                ((TextBox)window.FindName("AcBox")).Text = "20";
                ((CheckBox)window.FindName("SameBothCheck")).IsChecked = true;
                ((Button)window.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => ((TextBlock)window.FindName("StatusText")).Text.Length > 0);
                Assert.Contains(("ac", 20u), _api.Writes);
                Assert.Contains(("dc", 20u), _api.Writes);
                Assert.Equal(20u, _api.Settings[(ProcessorGroup, MaxState)].Ac);
                Assert.Single(PowerDetailsJournal.Load());

                // 让隐藏项全部显示：逐项计数（本夹具 3 项项级隐藏），取值不动。
                var revealsBefore = RegistryWrites.Count;
                ((Button)window.FindName("RevealButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => ((TextBlock)window.FindName("StatusText")).Text
                    == Str.T("Str.PowerDetailsRevealDone", 3));
                Assert.True(RegistryWrites.Count > revealsBefore);
                Assert.Contains(RegistryWrites, write => write.Group == ProcessorGroup && write.Setting == MaxState && write.Visible);
                Assert.Contains(RegistryWrites, write => write.Group == VideoGroup && write.Setting == UnknownSetting && write.Visible);
                Assert.DoesNotContain(RegistryWrites, write => !write.Visible);

                // 恢复本次改动：journal 里的项回到改动前，状态行如实报告条数。
                ((Button)window.FindName("RestoreButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => ((TextBlock)window.FindName("StatusText")).Text
                    == Str.T("Str.PowerDetailsRestoreDone", 1));
                Assert.Equal(100u, _api.Settings[(ProcessorGroup, MaxState)].Ac);
                Assert.Empty(PowerDetailsJournal.Load());

                // 再点一次：没有记录时只提示，不写任何值。
                _api.Writes.Clear();
                ((Button)window.FindName("RestoreButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DlssFeedbackTests.PumpUntil(() => ((TextBlock)window.FindName("StatusText")).Text
                    == Str.T("Str.PowerDetailsRestoreNone"));
                Assert.Empty(_api.Writes);

                // 入口：「电源」分组旁的按钮存在且文案走资源键。
                var optimize = new OptimizeView();
                var entryButton = (Button)optimize.FindName("PowerDetailsButton");
                Assert.NotNull(entryButton);
                Assert.Equal(Str.T("Str.PowerDetailsOpen"), entryButton.Content);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                window?.Close();
                app?.Shutdown();
                UiPerformance.LowSpec = false;
                UserDataPaths.RootOverride = null;
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "Power details UI check did not finish.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    // ---------------------------------------------------------------- 假 P/Invoke 层

    private void Seed() => _api.Reset();

    private sealed class FakeSetting
    {
        internal string Name = "";
        internal string Description = "";
        internal uint Minimum;
        internal uint Maximum;
        internal uint Increment;
        internal List<PowerPossibleValue>? Possible;
        internal uint Ac;
        internal uint Dc;
        internal uint AcDefault;
        internal uint DcDefault;
        internal bool FailValues;
    }

    private sealed class FakeApi : IPowerSettingsApi
    {
        internal readonly List<Guid> Subgroups = [];
        internal readonly Dictionary<Guid, List<Guid>> SettingsByGroup = [];
        internal readonly Dictionary<Guid, string> GroupNames = [];
        internal readonly Dictionary<(Guid Group, Guid Setting), FakeSetting> Settings = [];
        internal readonly HashSet<(Guid, Guid)> ThrowingNames = [];
        internal readonly HashSet<((Guid, Guid) Key, bool Ac)> WriteFailures = [];
        internal readonly HashSet<(Guid, Guid)> ReadBackMismatch = [];
        internal readonly List<(string Side, uint Value)> Writes = [];
        internal string SchemeName = "平衡";
        internal int ActiveSetCalls;
        internal uint ActiveSetError;
        internal int GroupEnumerationCalls;

        internal void Reset()
        {
            Subgroups.Clear(); SettingsByGroup.Clear(); GroupNames.Clear(); Settings.Clear();
            ThrowingNames.Clear(); WriteFailures.Clear(); ReadBackMismatch.Clear();
            Writes.Clear(); ActiveSetCalls = 0; GroupEnumerationCalls = 0;
        }

        internal void Group(Guid group, string name)
        {
            Subgroups.Add(group);
            SettingsByGroup[group] = [];
            GroupNames[group] = name;
        }

        internal void Setting(Guid group, Guid setting, string name, Action<FakeSetting> configure)
        {
            var value = new FakeSetting { Name = name };
            configure(value);
            Settings[(group, setting)] = value;
            SettingsByGroup[group].Add(setting);
        }

        public uint Enumerate(Guid scheme, Guid? subgroup, uint accessor, uint index, out Guid identity)
        {
            identity = Guid.Empty;
            if (subgroup is null && index == 0) GroupEnumerationCalls++;
            var list = subgroup is null ? Subgroups : SettingsByGroup.GetValueOrDefault(subgroup.Value) ?? [];
            if (index >= list.Count) return 259;
            identity = list[(int)index];
            return 0;
        }

        public uint ReadFriendlyName(Guid scheme, Guid? subgroup, Guid? setting, out string text)
        {
            text = "";
            if (subgroup is null)
            {
                text = SchemeName;
                return 0;
            }
            if (setting is null)
            {
                text = GroupNames.GetValueOrDefault(subgroup.Value) ?? "";
                return 0;
            }
            if (ThrowingNames.Contains((subgroup.Value, setting.Value)))
                throw new InvalidOperationException("PowerReadFriendlyName failed.");
            if (!Settings.TryGetValue((subgroup.Value, setting.Value), out var value)) return 87;
            text = value.Name;
            return 0;
        }

        public uint ReadDescription(Guid scheme, Guid subgroup, Guid setting, out string text)
        {
            text = Settings.TryGetValue((subgroup, setting), out var value) ? value.Description : "";
            return 0;
        }

        public uint ReadValueMin(Guid scheme, Guid subgroup, Guid setting, out uint value)
            => Range(subgroup, setting, RangeKind.Min, out value);

        public uint ReadValueMax(Guid scheme, Guid subgroup, Guid setting, out uint value)
            => Range(subgroup, setting, RangeKind.Max, out value);

        public uint ReadValueIncrement(Guid scheme, Guid subgroup, Guid setting, out uint value)
            => Range(subgroup, setting, RangeKind.Increment, out value);

        private uint Range(Guid subgroup, Guid setting, RangeKind kind, out uint value)
        {
            value = 0;
            if (!Settings.TryGetValue((subgroup, setting), out var shared) || shared.Possible is not null) return 87;
            value = kind switch
            {
                RangeKind.Min => shared.Minimum,
                RangeKind.Max => shared.Maximum,
                _ => shared.Increment
            };
            return 0;
        }

        public uint ReadPossibleValue(Guid scheme, Guid subgroup, Guid setting, uint index, out uint value)
        {
            value = 0;
            if (!Settings.TryGetValue((subgroup, setting), out var shared) || shared.Possible is null) return 259;
            if (index >= shared.Possible.Count) return 259;
            value = shared.Possible[(int)index].Value;
            return 0;
        }

        public uint ReadPossibleFriendlyName(Guid scheme, Guid subgroup, Guid setting, uint index, out string text)
        {
            text = "";
            if (!Settings.TryGetValue((subgroup, setting), out var shared) || shared.Possible is null) return 259;
            if (index >= shared.Possible.Count) return 259;
            text = shared.Possible[(int)index].Name;
            return 0;
        }

        public uint ReadValue(Guid scheme, Guid subgroup, Guid setting, bool ac, out uint value)
        {
            value = 0;
            if (!Settings.TryGetValue((subgroup, setting), out var shared) || shared.FailValues) return 5;
            value = ac ? shared.Ac : shared.Dc;
            return 0;
        }

        public uint ReadDefault(Guid scheme, Guid subgroup, Guid setting, bool ac, out uint value)
        {
            value = 0;
            if (!Settings.TryGetValue((subgroup, setting), out var shared)) return 5;
            value = ac ? shared.AcDefault : shared.DcDefault;
            return 0;
        }

        public uint WriteValue(Guid scheme, Guid subgroup, Guid setting, bool ac, uint value)
        {
            Writes.Add((ac ? "ac" : "dc", value));
            if (WriteFailures.Contains(((subgroup, setting), ac))) return 5;
            if (ReadBackMismatch.Contains((subgroup, setting))) return 0;   // 写成功但不落盘
            if (!Settings.TryGetValue((subgroup, setting), out var shared)) return 87;
            if (ac) shared.Ac = value; else shared.Dc = value;
            return 0;
        }

        public uint SetActiveScheme(Guid scheme)
        {
            ActiveSetCalls++;
            return ActiveSetError;
        }
    }

    private enum RangeKind
    {
        Min,
        Max,
        Increment
    }

    // ---------------------------------------------------------------- UI 夹具 / 渲染

    private void SeedUiFixture()
    {
        Seed();
        _api.Group(ProcessorGroup, "处理器电源管理");
        _api.Setting(ProcessorGroup, MaxState, "最大处理器状态", setting =>
        {
            setting.Minimum = 5; setting.Maximum = 100; setting.Increment = 5;
            setting.Ac = 100; setting.Dc = 100; setting.AcDefault = 100; setting.DcDefault = 100;
            setting.Description = "限制处理器最高性能状态，越低越省电。";
        });
        _api.Setting(ProcessorGroup, BoostMode, "处理器性能提升模式", setting =>
        {
            setting.Possible = [new(0, "已禁用"), new(1, "已启用"), new(2, "激进")];
            setting.Ac = 2; setting.Dc = 0; setting.AcDefault = 0; setting.DcDefault = 0;
            setting.Description = "决定处理器在负载突增时的加速策略。";
        });
        _api.Group(VideoGroup, "显示");
        _api.Setting(VideoGroup, DisplayOff, "关闭显示器", setting =>
        {
            setting.Minimum = 1; setting.Maximum = 120; setting.Increment = 1;
            setting.Ac = 10; setting.Dc = 5; setting.AcDefault = 10; setting.DcDefault = 5;
            setting.Description = "无操作多久后关闭显示器。";
        });
        _api.Setting(VideoGroup, UnknownSetting, "OEM 亮度曲线", setting =>
        {
            setting.Minimum = 0; setting.Maximum = 100; setting.Increment = 1;
            setting.Ac = 50; setting.Dc = 50; setting.AcDefault = 50; setting.DcDefault = 50;
            setting.Description = "OEM 自定义亮度曲线，名称读不到时用这段说明仍可搜索。";
        });
        _api.ThrowingNames.Add((VideoGroup, UnknownSetting));

        Registry[(ProcessorGroup, MaxState)] = new PowerSettingRegistryInfo(1, null, null);
        Registry[(ProcessorGroup, BoostMode)] = new PowerSettingRegistryInfo(1, null, null);
        Registry[(VideoGroup, UnknownSetting)] = new PowerSettingRegistryInfo(1, null, null);
    }

    private static PowerSettingRow RowOf(ListBox list, Guid setting)
        => ((IEnumerable<PowerSettingRow>)((ICollectionView)list.ItemsSource).SourceCollection)
            .Single(row => row.Entry.Setting == setting);

    internal static string Output()
    {
        var path = Environment.GetEnvironmentVariable("FPSTUNE_POWER_SHOTS");
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "ref-align-297", "shots-power");
        Directory.CreateDirectory(path);
        return path;
    }

    private static IEnumerable<TextBlock> VisibleText(DependencyObject root)
        => Descendants(root).OfType<TextBlock>().Where(text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text));

    private static void AssertReadable(DependencyObject root)
    {
        var small = VisibleText(root).Where(text => text.FontSize < 13)
            .Select(text => text.Text + "(" + text.FontSize.ToString(CultureInfo.InvariantCulture) + ")").Distinct().ToArray();
        Assert.True(small.Length == 0, "电源细项面板出现小于 13 号的可见文字：" + string.Join(" | ", small.Take(6)));
    }

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void Render(FrameworkElement element, string name)
    {
        var expected = (Color)ColorConverter.ConvertFromString(name.EndsWith("-light", StringComparison.Ordinal) ? "#F7F9FC" : "#0C1015")!;
        DlssFeedbackTests.PumpUntil(() => ((SolidColorBrush)Application.Current.FindResource("AppBackdropBrush")).Color == expected);
        var frame = new DispatcherFrame();
        element.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var backdrop = (SolidColorBrush)Application.Current.FindResource("AppBackdropBrush");
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            drawing.DrawRectangle(backdrop, null, bounds);
            drawing.DrawRectangle(new VisualBrush(element), null, bounds);
        }
        bitmap.Render(visual);
        var path = Path.Combine(Output(), name + ".png");
        using (var stream = File.Create(path))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
        }

        // 证据自校验：读回 PNG 的角落像素，确认深色图真的是深色，而不是主题没切过去。
        var decoder = new PngBitmapDecoder(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var pixels = new byte[4];
        decoder.Frames[0].CopyPixels(new Int32Rect(4, 4, 1, 1), pixels, 4, 0);
        Assert.True(pixels[2] == expected.R && pixels[1] == expected.G && pixels[0] == expected.B,
            name + " 背景色不对：期望 " + expected + "，实际 B=" + pixels[0] + " G=" + pixels[1] + " R=" + pixels[2]
            + "；画笔=" + backdrop.Color + " mode=" + ThemeManager.CurrentMode + " 元素=" + element.GetType().Name
            + " 元素背景=" + (element is Border border ? border.Background?.ToString() : "?"));
    }
}
