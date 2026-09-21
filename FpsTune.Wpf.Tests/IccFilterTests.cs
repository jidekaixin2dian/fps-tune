using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// ICC 生成器纯函数测试：断言 ICC 头字段、长度、tag 结构合法；并用真实 mscms
/// IsColorProfileValid 做只读冒烟验证（不写任何系统状态）。
/// </summary>
public class IccProfileGeneratorTests
{
    public static IEnumerable<object[]> AllPresets()
        => Enum.GetValues<IccFilterPreset>().Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(AllPresets))]
    public void Header_is_well_formed(IccFilterPreset preset)
    {
        var bytes = IccProfileGenerator.Build(preset);

        // 0..3 尺寸字段与实际长度一致
        Assert.Equal((uint)bytes.Length, ReadBe32(bytes, 0));
        Assert.True(bytes.Length > 128);
        // 8..11 版本 2.1
        Assert.Equal(0x02100000u, ReadBe32(bytes, 8));
        // 12 设备类 mntr；16 色彩空间 RGB ；20 PCS XYZ
        Assert.Equal("mntr", Ascii(bytes, 12, 4));
        Assert.Equal("RGB ", Ascii(bytes, 16, 4));
        Assert.Equal("XYZ ", Ascii(bytes, 20, 4));
        // 36 签名 acsp
        Assert.Equal("acsp", Ascii(bytes, 36, 4));
        // 68..79 PCS 白点 D50
        Assert.Equal(0x0000F6D6u, ReadBe32(bytes, 68));
        Assert.Equal(0x00010000u, ReadBe32(bytes, 72));
        Assert.Equal(0x0000D32Du, ReadBe32(bytes, 76));
    }

    [Theory]
    [MemberData(nameof(AllPresets))]
    public void Tag_table_is_well_formed(IccFilterPreset preset)
    {
        var bytes = IccProfileGenerator.Build(preset);

        int tagCount = (int)ReadBe32(bytes, 128);
        Assert.True(tagCount >= 9, "至少包含 desc/wtpt/rXYZ/gXYZ/bXYZ/rTRC/gTRC/bTRC/cprt");

        var seen = new HashSet<string>();
        for (int i = 0; i < tagCount; i++)
        {
            int off = 132 + i * 12;
            string sig = Ascii(bytes, off, 4);
            int dataOffset = (int)ReadBe32(bytes, off + 4);
            int dataSize = (int)ReadBe32(bytes, off + 8);
            Assert.True(seen.Add(sig), $"tag {sig} 重复");
            Assert.True(dataOffset >= 128 + 4 + tagCount * 12, $"tag {sig} 越过头部");
            Assert.True(dataOffset + dataSize <= bytes.Length, $"tag {sig} 越出文件");
        }
        Assert.Contains("rTRC", seen);
        Assert.Contains("gTRC", seen);
        Assert.Contains("bTRC", seen);
        Assert.Contains("rXYZ", seen);
    }

    [Theory]
    [MemberData(nameof(AllPresets))]
    public void Is_deterministic(IccFilterPreset preset)
    {
        Assert.Equal(IccProfileGenerator.Build(preset), IccProfileGenerator.Build(preset));
    }

    /// <summary>真机冒烟：mscms IsColorProfileValid 只读校验（不写系统状态）。</summary>
    [Theory]
    [MemberData(nameof(AllPresets))]
    public void System_validates_generated_profile(IccFilterPreset preset)
    {
        var api = new MscmsIccApi();
        Assert.True(api.IsProfileBytesValid(IccProfileGenerator.Build(preset)),
            $"{preset} 生成的 profile 未通过 mscms IsColorProfileValid");
    }

    private static uint ReadBe32(byte[] b, int off)
        => (uint)((b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3]);

    private static string Ascii(byte[] b, int off, int len) => System.Text.Encoding.ASCII.GetString(b, off, len);
}

/// <summary>
/// ICC 滤镜服务逻辑测试：全部走内存态假实现，不触碰真实系统。
/// 架构语义：备份始终代表介入前原点；外部漂移如实拒绝；还原 = 按备份精确写回并验证。
/// </summary>
[Collection("BackupService serial")]
public class IccFilterServiceTests : IDisposable
{
    private readonly FakeIccApi _api = new();
    private readonly string _backupDir;

    public IccFilterServiceTests()
    {
        _backupDir = Path.Combine(Path.GetTempPath(), "fpstune-icc-tests-" + Guid.NewGuid().ToString("N"));
        IccFilterService.ApiOverride = () => _api;
        IccFilterService.BackupDirOverride = _backupDir;
        Directory.CreateDirectory(_backupDir);
    }

    public void Dispose()
    {
        IccFilterService.ApiOverride = null;
        IccFilterService.BackupDirOverride = null;
        if (Directory.Exists(_backupDir))
            Directory.Delete(_backupDir, recursive: true);
        // FakeIccApi 的模拟色彩目录同样是临时产物，漏删会让每次 dotnet test 都留下几十个目录。
        if (Directory.Exists(_api.ColorDirectory))
            Directory.Delete(_api.ColorDirectory, recursive: true);
    }

    [Fact]
    public void Apply_installs_preset_and_backs_up_original_state()
    {
        var original = _api.InstallDisplayProfile("sRGB.icm");
        _api.AssociationList.AddRange(new[] { "sRGB.icm" });

        IccFilterService.Apply(IccFilterPreset.Vivid);

        // 预设被安装进色彩目录并成为列表末位（默认）
        Assert.Contains("FpsTune-Vivid.icc", _api.InstalledProfiles);
        Assert.Equal("FpsTune-Vivid.icc", LastValid());

        var state = IccFilterService.GetState();
        Assert.True(state.Supported);
        Assert.Equal("FpsTune-Vivid.icc", state.CurrentProfileName);
        Assert.True(state.Restorable);
    }

    [Fact]
    public void Second_apply_keeps_first_backup_of_original()
    {
        _api.InstallDisplayProfile("sRGB.icm");
        _api.AssociationList.AddRange(new[] { "sRGB.icm" });

        IccFilterService.Apply(IccFilterPreset.Vivid);
        IccFilterService.Apply(IccFilterPreset.ShadowBoost);

        Assert.Equal("FpsTune-ShadowBoost.icc", LastValid());

        // 还原必须回到"从未覆盖过"的原点（sRGB），而不是上一次的 Vivid
        Assert.True(IccFilterService.Restore());
        Assert.Equal(["sRGB.icm"], _api.AssociationList);
        Assert.Equal("sRGB.icm", LastValid());
    }

    [Fact]
    public void External_drift_is_refused_honestly()
    {
        _api.InstallDisplayProfile("sRGB.icm");
        _api.AssociationList.AddRange(new[] { "sRGB.icm" });

        IccFilterService.Apply(IccFilterPreset.Vivid);

        // 用户在工具之外改了关联（例如 colorcpl 设了别的默认）
        var other = _api.InstallDisplayProfile("其他.icc");
        _api.AssociationList.Add(other);

        var ex = Assert.Throws<InvalidOperationException>(() => IccFilterService.Apply(IccFilterPreset.Dehaze));
        Assert.Contains("还原", ex.Message);
        // 系统状态未被本工具继续改动
        Assert.Equal(other, LastValid());
    }

    [Fact]
    public void Restore_recovers_exact_original_list_and_deletes_backup()
    {
        _api.InstallDisplayProfile("sRGB.icm");
        _api.InstallDisplayProfile("校色.icm");
        // 原始列表带一个失效条目（文件不存在的残留），必须原样保留
        _api.AssociationList.AddRange(new[] { "sRGB.icm", "已删除.icm", "校色.icm" });

        IccFilterService.Apply(IccFilterPreset.Vivid);
        Assert.Equal("FpsTune-Vivid.icc", LastValid());

        Assert.True(IccFilterService.Restore());

        Assert.Equal(new[] { "sRGB.icm", "已删除.icm", "校色.icm" }, _api.AssociationList);
        Assert.Equal("校色.icm", LastValid());
        Assert.False(IccFilterService.GetState().Restorable);
    }

    [Fact]
    public void Restore_without_backup_is_honest_noop()
    {
        _api.InstallDisplayProfile("sRGB.icm");
        _api.AssociationList.AddRange(new[] { "sRGB.icm" });

        Assert.False(IccFilterService.Restore());
        Assert.Equal(["sRGB.icm"], _api.AssociationList);
    }

    [Fact]
    public void No_current_profile_refuses_to_switch()
    {
        // 列表为空 = 没有当前生效 profile，无法备份还原原点
        var ex = Assert.Throws<InvalidOperationException>(() => IccFilterService.Apply(IccFilterPreset.Vivid));
        Assert.Contains("拒绝", ex.Message);
        Assert.Null(IccFilterService.GetState().CurrentProfileName);
    }

    [Fact]
    public void Non_display_class_profile_is_not_treated_as_valid_default()
    {
        // 列表末位是打印机类 profile（非 mntr/RGB），生效默认应跳过它取上一个
        var display = _api.InstallDisplayProfile("sRGB.icm");
        var printer = _api.WriteProfileFile("RSWOP.icm", deviceClass: "prtr", colorSpace: "CMYK");
        _api.AssociationList.AddRange(new[] { display, printer });

        var state = IccFilterService.GetState();
        Assert.Equal(display, state.CurrentProfileName);
    }

    [Fact]
    public void Unsupported_environment_reports_honestly()
    {
        _api.SimulateNoDisplay = true;
        var state = IccFilterService.GetState();
        Assert.False(state.Supported);
        Assert.NotNull(state.UnsupportedReason);
        Assert.Throws<InvalidOperationException>(() => IccFilterService.Apply(IccFilterPreset.Vivid));
    }

    [Fact]
    public void System_wide_association_device_is_reported_unsupported()
    {
        _api.UsePerUser = false;
        _api.InstallDisplayProfile("sRGB.icm");
        _api.AssociationList.AddRange(new[] { "sRGB.icm" });

        var state = IccFilterService.GetState();
        Assert.False(state.Supported);
        Assert.Throws<InvalidOperationException>(() => IccFilterService.Apply(IccFilterPreset.Vivid));
    }

    private string? LastValid()
    {
        for (int i = _api.AssociationList.Count - 1; i >= 0; i--)
        {
            var n = _api.AssociationList[i];
            if (string.IsNullOrWhiteSpace(n)) continue;
            var full = Path.Combine(_api.ColorDirectory, n);
            if (IccProfileFile.IsDisplayProfile(full))
                return n;
        }
        return null;
    }

    /// <summary>内存态 ICC 系统层：色彩目录用临时目录，SetDisplayDefault 模拟真实行为（追加到列表末尾）。</summary>
    private sealed class FakeIccApi : IIccSystemApi
    {
        public string ColorDirectory { get; } = Path.Combine(Path.GetTempPath(), "fpstune-icc-color-" + Guid.NewGuid().ToString("N"));
        public List<string> AssociationList { get; } = new();
        public HashSet<string> InstalledProfiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool SimulateNoDisplay { get; set; }
        public bool UsePerUser { get; set; } = true;

        public FakeIccApi()
        {
            Directory.CreateDirectory(ColorDirectory);
        }

        public bool TryGetPrimaryDisplay(out IccDisplayInfo display)
        {
            if (SimulateNoDisplay)
            {
                display = new IccDisplayInfo("", 0, 0, 0, "", false);
                return false;
            }
            display = new IccDisplayInfo("\\\\.\\DISPLAY1", 0xF015, 0, 0, "0004", UsePerUser);
            return true;
        }

        public string? TryGetColorDirectory() => ColorDirectory;

        public bool InstallColorProfile(string sourcePath)
        {
            if (!File.Exists(sourcePath))
                return false;
            var target = Path.Combine(ColorDirectory, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, target, overwrite: true);
            InstalledProfiles.Add(Path.GetFileName(sourcePath));
            return true;
        }

        public bool SetDisplayDefaultAssociation(IccDisplayInfo display, string profileName)
        {
            if (!UsePerUser)
                return false;
            if (!InstalledProfiles.Contains(profileName))
                return false;
            AssociationList.Add(profileName);
            return true;
        }

        public string[] ReadAssociationList(IccDisplayInfo display) => AssociationList.ToArray();

        public void WriteAssociationList(IccDisplayInfo display, string[] list)
        {
            AssociationList.Clear();
            AssociationList.AddRange(list);
        }

        public bool IsProfileBytesValid(byte[] profile) => true;

        /// <summary>写一个真实生成的 ICC 文件充当色彩目录里的既有 profile。</summary>
        public string InstallDisplayProfile(string name)
        {
            var bytes = IccProfileGenerator.Build(IccProfilePresetStub(name));
            var path = Path.Combine(ColorDirectory, name);
            File.WriteAllBytes(path, bytes);
            InstalledProfiles.Add(name);
            return name;
        }

        public string WriteProfileFile(string name, string deviceClass, string colorSpace)
        {
            var bytes = IccProfileGenerator.Build(IccProfilePresetStub(name));
            // 改写设备类/色彩空间四字符段
            System.Text.Encoding.ASCII.GetBytes(deviceClass).CopyTo(bytes, 12);
            System.Text.Encoding.ASCII.GetBytes(colorSpace).CopyTo(bytes, 16);
            var path = Path.Combine(ColorDirectory, name);
            File.WriteAllBytes(path, bytes);
            InstalledProfiles.Add(name);
            return name;
        }

        private static IccProfileGenerator.PresetParams IccProfilePresetStub(string name)
            => new(name, "test " + name);
    }
}
