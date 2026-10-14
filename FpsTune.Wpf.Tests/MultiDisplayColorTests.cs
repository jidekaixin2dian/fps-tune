using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class MultiDisplayColorTests : IDisposable
{
    internal readonly string Root = Path.Combine(Path.GetTempPath(), "fpstune-displays-" + Guid.NewGuid().ToString("N"));
    internal List<IccDisplayInfo> Displays = [Screen("A", "DISPLAY1", 0, "0001", true), Screen("B", "DISPLAY2", 1, "0002", false)];
    internal readonly Dictionary<string, int> Levels = new() { ["A"] = 30, ["B"] = 60 };
    internal readonly Dictionary<string, List<string>> Profiles = new() { ["A"] = ["a.icm"], ["B"] = ["b.icm"] };
    internal Action? BeforeVibWrite;
    internal Action? BeforeIccWrite;
    internal bool IgnoreVibWrite;
    internal readonly List<string> Writes = [];
    public MultiDisplayColorTests()
    {
        Directory.CreateDirectory(Root);
        DisplayTopology.Override = () => DisplayTopology.MarkIndependent(Displays);
        DigitalVibranceService.BackupDirOverride = Path.Combine(Root, "vib");
        IccFilterService.BackupDirOverride = Path.Combine(Root, "icc");
        DigitalVibranceService.ApiOverride = () => new Dvc(this);
        IccFilterService.ApiOverride = () => new Icc(this);
        Directory.CreateDirectory(Path.Combine(Root, "colors"));
        foreach (var profile in new[] { "a.icm", "b.icm", "external.icm" })
            File.WriteAllBytes(Path.Combine(Root, "colors", profile), IccProfileGenerator.Build(IccFilterPreset.Vivid));
    }
    internal static IccDisplayInfo Screen(string id, string gdi, uint source, string key, bool primary)
        => new(@"\\.\" + gdi, 123, 0, source, key, true, id, "Screen " + id, primary);
    public void Dispose()
    {
        DisplayTopology.Override = null;
        DigitalVibranceService.ApiOverride = null;
        IccFilterService.ApiOverride = null;
        DigitalVibranceService.BackupDirOverride = null;
        IccFilterService.BackupDirOverride = null;
        Directory.Delete(Root, true);
    }
    [Fact]
    public void Per_screen_originals_and_readback_remain_independent_when_primary_and_connection_names_change()
    {
        DigitalVibranceService.SetPercent(75, "A");
        IccFilterService.Apply(IccFilterPreset.Warm, "A");
        DigitalVibranceService.SetPercent(85, "B");
        IccFilterService.Apply(IccFilterPreset.Dehaze, "B");
        Assert.Equal(2, DigitalVibranceService.BackupTargets().Count);
        Assert.Equal(2, IccFilterService.BackupTargets().Count);
        Displays = [Screen("B", "DISPLAY1", 0, "0002", true), Screen("A", "DISPLAY2", 1, "0001", false)];
        Assert.True(DigitalVibranceService.Restore("A"));
        Assert.True(IccFilterService.Restore("A"));
        Assert.Equal(30, Levels["A"]);
        Assert.Equal(["a.icm"], Profiles["A"]);
        Assert.Equal(85, Levels["B"]);
        Assert.Equal("FpsTune-Dehaze.icc", Profiles["B"].Last());
        Assert.Single(DigitalVibranceService.BackupTargets());
        Assert.Single(IccFilterService.BackupTargets());
        Assert.True(DigitalVibranceService.Restore("B"));
        Assert.True(IccFilterService.Restore("B"));
        Assert.Equal(60, Levels["B"]);
        Assert.Equal(["b.icm"], Profiles["B"]);
    }
    [Fact]
    public void Unplugged_or_replaced_screen_never_falls_back_to_primary_or_consumes_the_old_backup()
    {
        DigitalVibranceService.SetPercent(75, "A");
        IccFilterService.Apply(IccFilterPreset.Warm, "A");
        Displays[0] = Screen("Replacement", "DISPLAY1", 0, "0001", true);
        Assert.Throws<InvalidOperationException>(() => DigitalVibranceService.Restore("A"));
        Assert.Throws<InvalidOperationException>(() => IccFilterService.Restore("A"));
        Assert.Throws<InvalidOperationException>(() => DigitalVibranceService.SetPercent(90, "A"));
        Assert.Throws<InvalidOperationException>(() => IccFilterService.Apply(IccFilterPreset.Vivid, "A"));
        Assert.Equal(2, Writes.Count);
        Assert.Single(DigitalVibranceService.BackupTargets());
        Assert.Single(IccFilterService.BackupTargets());
        Assert.Equal(60, Levels["B"]);
    }
    [Fact]
    public void Hotplug_between_read_and_write_aborts_each_native_connection_without_writing_another_screen()
    {
        BeforeVibWrite = () => Displays[0] = Displays[0] with { SourceId = 9 };
        Assert.Throws<InvalidOperationException>(() => DigitalVibranceService.SetPercent(80, "A"));
        Assert.Equal(30, Levels["A"]);
        BeforeVibWrite = null;
        BeforeIccWrite = () => Displays[0] = Displays[0] with { GdiDeviceName = @"\\.\DISPLAY9" };
        Assert.Throws<InvalidOperationException>(() => IccFilterService.Apply(IccFilterPreset.Warm, "A"));
        Assert.Equal(["a.icm"], Profiles["A"]);
        Assert.Empty(Writes);
    }
    [Fact]
    public void Mirrored_and_duplicate_identities_are_refused_before_any_write()
    {
        Displays[1] = Displays[1] with { SourceId = 0 };
        Assert.Throws<InvalidOperationException>(() => IccFilterService.Apply(IccFilterPreset.Warm, "B"));
        Assert.Throws<InvalidOperationException>(() => DigitalVibranceService.SetPercent(80, "B"));
        Displays[1] = Displays[1] with { Identity = "A", SourceId = 1 };
        Assert.Throws<InvalidOperationException>(() => DisplayTopology.Require("A"));
        Assert.Empty(Writes);
    }
    [Fact]
    public void Unconfirmed_vibrance_save_or_restore_keeps_receipt_instead_of_claiming_success()
    {
        IgnoreVibWrite = true;
        Assert.Throws<InvalidOperationException>(() => DigitalVibranceService.SetPercent(80, "A"));
        Assert.Single(DigitalVibranceService.BackupTargets());
        IgnoreVibWrite = false;
        DigitalVibranceService.SetPercent(80, "A");
        IgnoreVibWrite = true;
        Assert.Throws<InvalidOperationException>(() => DigitalVibranceService.Restore("A"));
        Assert.Equal(80, Levels["A"]);
        Assert.Single(DigitalVibranceService.BackupTargets());
    }
    [Fact]
    public void Legacy_vibrance_receipt_for_another_screen_is_not_deleted_or_used_as_its_original()
    {
        Directory.CreateDirectory(DigitalVibranceService.BackupDirOverride!);
        var file = Path.Combine(DigitalVibranceService.BackupDirOverride!, "vibrance.json");
        var original = JsonSerializer.Serialize(new { Level = 30, Display = "A", Expected = 75, Min = 0, Max = 100 });
        File.WriteAllText(file, original);
        Levels["A"] = 75;
        DigitalVibranceService.SetPercent(80, "B");
        Assert.Equal(original, File.ReadAllText(file));
        DigitalVibranceService.SetPercent(85, "A");
        Assert.False(File.Exists(file));
        Assert.True(DigitalVibranceService.Restore("A"));
        Assert.Equal(30, Levels["A"]);
        Assert.Equal(80, Levels["B"]);
    }
    [Fact]
    public void Legacy_icc_receipt_without_monitor_identity_is_preserved_and_not_assigned_to_a_reused_driver_key()
    {
        Directory.CreateDirectory(IccFilterService.BackupDirOverride!);
        var path = Path.Combine(IccFilterService.BackupDirOverride!, "backup.json");
        var before = JsonSerializer.Serialize(new { AssociationSubKey = "0001", DefaultProfile = "a.icm", ListBefore = new[] { "a.icm" }, CreatedAt = "old" });
        File.WriteAllText(path, before);
        Assert.Throws<InvalidOperationException>(() => IccFilterService.Restore("A"));
        Assert.Throws<InvalidOperationException>(() => IccFilterService.Apply(IccFilterPreset.Warm, "A"));
        Assert.Equal(before, File.ReadAllText(path));
        IccFilterService.Apply(IccFilterPreset.Warm, "B");
        Assert.Equal(["a.icm"], Profiles["A"]);
    }
    [Fact]
    public void Restore_all_restores_connected_screen_even_when_other_screen_is_offline_or_corrupt()
    {
        var oldRoot = UserDataPaths.RootOverride;
        UserDataPaths.RootOverride = Root;
        try
        {
            foreach (var id in new[] { "A", "B" }) { DigitalVibranceService.SetPercent(80, id); IccFilterService.Apply(IccFilterPreset.Warm, id); }
            Displays.RemoveAt(0);
            var result = RecoveryCoordinator.RestoreAll();
            Assert.Equal(2, result.Failures.Count);
            Assert.Equal(2, result.Restored.Count);
            Assert.Equal(60, Levels["B"]);
            Assert.Equal(["b.icm"], Profiles["B"]);
            Assert.Single(IccFilterService.BackupTargets());
            Assert.Single(DigitalVibranceService.BackupTargets());
            Displays.Add(Screen("A", "DISPLAY3", 3, "0001", false));
            IccFilterService.Apply(IccFilterPreset.Warm, "B");
            File.WriteAllText(DisplayBackupFiles.PathFor(IccFilterService.BackupDirOverride!, "backup", "A"), "{corrupt");
            result = RecoveryCoordinator.RestoreAll();
            Assert.Single(result.Failures);
            Assert.Equal(["b.icm"], Profiles["B"]);
            Assert.Equal(30, Levels["A"]);
        }
        finally { UserDataPaths.RootOverride = oldRoot; }
    }
    [Fact]
    public void Native_dvc_binds_by_Gdi_name_even_when_Nvidia_enumeration_order_is_reversed()
    {
        var api = new NvDvcApi();
        SetDelegate(api, "_enumDisplay", nameof(EnumDisplays));
        SetDelegate(api, "_getDisplayName", nameof(DisplayName));
        SetDelegate(api, "_setDvcLevel", nameof(SetNative));
        _writtenHandle = IntPtr.Zero;
        Assert.True(api.TrySelectDisplay("A"));
        api.SetLevel(70);
        Assert.Equal(new IntPtr(200), _writtenHandle);
        Displays.RemoveAt(0);
        Assert.Throws<InvalidOperationException>(() => api.SetLevel(80));
        Assert.Equal(new IntPtr(200), _writtenHandle);
    }
    [Fact]
    public void Existing_preset_in_original_association_list_is_kept_as_part_of_the_exact_restore_baseline()
    {
        File.Copy(Path.Combine(Root, "colors", "a.icm"), Path.Combine(Root, "colors", "FpsTune-Warm.icc"));
        Profiles["A"].Add("FpsTune-Warm.icc");
        IccFilterService.Apply(IccFilterPreset.Vivid, "A");
        Assert.True(IccFilterService.Restore("A"));
        Assert.Equal(new[] { "a.icm", "FpsTune-Warm.icc" }, Profiles["A"]);
    }
    [Fact]
    public void Ambiguous_Gdi_paths_or_driver_handles_never_choose_the_first_matching_monitor()
    {
        var api = new NvDvcApi();
        SetDelegate(api, "_enumDisplay", nameof(EnumDisplays));
        SetDelegate(api, "_getDisplayName", nameof(DuplicateDisplayName));
        Assert.False(api.TrySelectDisplay("A"));
        Displays[1] = Displays[1] with { GdiDeviceName = Displays[0].GdiDeviceName };
        Assert.Throws<InvalidOperationException>(() => DisplayTopology.Require("A"));
    }
    [Fact]
    public void Ccd_path_stride_matches_Windows_native_layout()
    {
        Assert.Equal(20, Marshal.SizeOf<DisplayTopology.Source>());
        Assert.Equal(48, Marshal.SizeOf<DisplayTopology.Target>());
        Assert.Equal(72, Marshal.SizeOf<DisplayTopology.PathInfo>());
        Assert.Equal(20, Marshal.OffsetOf<DisplayTopology.PathInfo>("Target").ToInt32());
    }
    private static IntPtr _writtenHandle;
    private static int EnumDisplays(uint index, ref IntPtr handle) { handle = index == 0 ? new(100) : new(200); return index < 2 ? 0 : -7; }
    private static int DisplayName(IntPtr handle, StringBuilder name) { name.Append(handle == new IntPtr(100) ? @"\\.\DISPLAY2" : @"\\.\DISPLAY1"); return 0; }
    private static int DuplicateDisplayName(IntPtr handle, StringBuilder name) { name.Append(@"\\.\DISPLAY1"); return 0; }
    private static int SetNative(IntPtr handle, uint output, int level) { _writtenHandle = handle; return 0; }
    private static void SetDelegate(object api, string field, string method)
    {
        var member = typeof(NvDvcApi).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!;
        member.SetValue(api, Delegate.CreateDelegate(member.FieldType, typeof(MultiDisplayColorTests).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!));
    }
    private sealed class Dvc(MultiDisplayColorTests owner) : INvibranceApi
    {
        private IccDisplayInfo? _display;
        public bool TryInitialize() => true;
        public string? LastError => null;
        public bool TrySelectDisplay(string? id) { try { _display = DisplayTopology.Require(id); return true; } catch { return false; } }
        public bool TryGetInfo(out int current, out int min, out int max, out int defaultValue)
        { DisplayTopology.ValidateConnection(_display!); current = owner.Levels[_display!.Identity!]; min = 0; max = 100; defaultValue = 50; return true; }
        public string? DisplayIdentity() => _display?.Identity;
        public string DisplayName() => _display!.FriendlyName;
        public void SetLevel(int level)
        { owner.BeforeVibWrite?.Invoke(); DisplayTopology.ValidateConnection(_display!); owner.Writes.Add("vib:" + _display!.Identity); if (!owner.IgnoreVibWrite) owner.Levels[_display.Identity!] = level; }
        public void Dispose() { }
    }
    private sealed class Icc(MultiDisplayColorTests owner) : IIccSystemApi
    {
        public IReadOnlyList<IccDisplayInfo> GetDisplays() => DisplayTopology.GetDisplays();
        public bool TryGetPrimaryDisplay(out IccDisplayInfo display) { display = GetDisplays().First(d => d.IsPrimary); return true; }
        public void ValidateConnection(IccDisplayInfo display) => DisplayTopology.ValidateConnection(display);
        public string? TryGetColorDirectory() => Path.Combine(owner.Root, "colors");
        public bool InstallColorProfile(string source) { File.Copy(source, Path.Combine(TryGetColorDirectory()!, Path.GetFileName(source)), true); return true; }
        public bool SetDisplayDefaultAssociation(IccDisplayInfo display, string name)
        { owner.BeforeIccWrite?.Invoke(); ValidateConnection(display); owner.Writes.Add("icc:" + display.Identity); owner.Profiles[display.Identity!].Add(name); return true; }
        public string[] ReadAssociationList(IccDisplayInfo display) => owner.Profiles[display.Identity!].ToArray();
        public void WriteAssociationList(IccDisplayInfo display, string[] list) { ValidateConnection(display); owner.Profiles[display.Identity!] = list.ToList(); }
        public bool IsProfileBytesValid(byte[] profile) => true;
    }
}
