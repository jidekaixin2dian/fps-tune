using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace FpsTune.Wpf.Core;

/// <summary>枚举型电源设置的一个候选值（值 + 系统给出的显示名）。</summary>
internal sealed record PowerPossibleValue(uint Value, string Name)
{
    /// <summary>下拉框显示文本；public 才能被 XAML 的 DisplayMemberPath 绑定。</summary>
    public string Display => string.IsNullOrWhiteSpace(Name)
        ? Value.ToString(CultureInfo.InvariantCulture)
        : Name;
}

/// <summary>
/// 一项电源设置的全部只读信息。逐项读取失败只落在这一条上（<see cref="ReadFailed"/>），
/// 不会让整张表作废——参考实现同款容错，本机 178 项里有大量第三方/OEM 项本来就古怪。
/// </summary>
internal sealed class PowerSettingEntry
{
    internal Guid Subgroup { get; init; }
    internal Guid Setting { get; init; }
    internal string SubgroupName { get; init; } = "";
    internal string Name { get; init; } = "";
    internal string Description { get; init; } = "";
    internal string Units { get; init; } = "";
    internal uint Minimum { get; init; }
    internal uint Maximum { get; init; }
    internal uint Increment { get; init; }
    internal IReadOnlyList<PowerPossibleValue> PossibleValues { get; init; } = [];
    internal uint? Ac { get; init; }
    internal uint? Dc { get; init; }
    internal uint? AcDefault { get; init; }
    internal uint? DcDefault { get; init; }
    internal bool HiddenByItem { get; init; }
    internal bool HiddenByGroup { get; init; }
    internal bool NameReadFailed { get; init; }
    internal bool ValuesReadFailed { get; init; }
    internal string? Error { get; init; }
    internal bool IsCommon { get; init; }

    /// <summary>取值范围读到了（系统给了 min/max 且 max &gt; min）。</summary>
    internal bool RangeKnown { get; init; }

    /// <summary>取值域已知：要么是候选值列表，要么是 min/max。读不到时界面不瞎报「可填 0–0」。</summary>
    internal bool DomainKnown => PossibleValues.Count > 0 || RangeKnown;

    /// <summary>名称或两套取值都读不到时算「这一项没读到」。</summary>
    internal bool ReadFailed => NameReadFailed || ValuesReadFailed;

    internal bool Hidden => HiddenByItem || HiddenByGroup;


    internal bool Changed =>
        (Ac.HasValue && AcDefault.HasValue && Ac.Value != AcDefault.Value) ||
        (Dc.HasValue && DcDefault.HasValue && Dc.Value != DcDefault.Value);
}

/// <summary>一次整表读取的结果；枚举本身失败时 <see cref="Failed"/> 为真，界面据此提示重试。</summary>
internal sealed record PowerSettingScan(IReadOnlyList<PowerSettingEntry> Entries, bool Failed, string? Error);

internal enum PowerValueIssue
{
    None,
    NotANumber,
    OutOfRange,
    StepMismatch,
    NotAnOption
}

/// <summary>取值校验结果；界面按 <see cref="Issue"/> 选文案，不在这里拼中文。</summary>
internal sealed record PowerValueCheck(PowerValueIssue Issue, uint Value, uint? Nearest)
{
    internal bool Ok => Issue == PowerValueIssue.None;
}

/// <summary>注册表里的可见性/单位信息，读不到就是 null，不抛异常。</summary>
internal sealed record PowerSettingRegistryInfo(int? ItemAttributes, int? GroupAttributes, string? Units);

internal enum PowerVisibilityStatus
{
    /// <summary>本来就在目标状态，没有写注册表。</summary>
    Unchanged,
    Updated,
    /// <summary>系统里没有这一项对应的注册表键。</summary>
    Missing,
    Denied,
    Failed
}

internal sealed record PowerVisibilityWrite(PowerVisibilityStatus Status, string? Error)
{
    internal bool Ok => Status is PowerVisibilityStatus.Unchanged or PowerVisibilityStatus.Updated;
}

/// <summary>
/// powrprof 的可注入包装层：单测替换 <see cref="PowerSettingCatalog.ApiOverride"/>，
/// 生产走 <see cref="NativePowerSettingsApi"/>。签名与真实导出一一对应（用只读探针在本机核对过）。
/// </summary>
internal interface IPowerSettingsApi
{
    uint Enumerate(Guid scheme, Guid? subgroup, uint accessor, uint index, out Guid identity);
    uint ReadFriendlyName(Guid scheme, Guid? subgroup, Guid? setting, out string text);
    uint ReadDescription(Guid scheme, Guid subgroup, Guid setting, out string text);
    uint ReadValueMin(Guid scheme, Guid subgroup, Guid setting, out uint value);
    uint ReadValueMax(Guid scheme, Guid subgroup, Guid setting, out uint value);
    uint ReadValueIncrement(Guid scheme, Guid subgroup, Guid setting, out uint value);
    uint ReadPossibleValue(Guid scheme, Guid subgroup, Guid setting, uint index, out uint value);
    uint ReadPossibleFriendlyName(Guid scheme, Guid subgroup, Guid setting, uint index, out string text);
    uint ReadValue(Guid scheme, Guid subgroup, Guid setting, bool ac, out uint value);
    uint ReadDefault(Guid scheme, Guid subgroup, Guid setting, bool ac, out uint value);
    uint WriteValue(Guid scheme, Guid subgroup, Guid setting, bool ac, uint value);
    uint SetActiveScheme(Guid scheme);
}

internal sealed class NativePowerSettingsApi : IPowerSettingsApi
{
    private const int TextCapacity = 4096;

    internal static NativePowerSettingsApi Instance { get; } = new();

    public uint Enumerate(Guid scheme, Guid? subgroup, uint accessor, uint index, out Guid identity)
    {
        identity = Guid.Empty;
        var pointer = subgroup.HasValue ? Marshal.AllocHGlobal(16) : IntPtr.Zero;
        try
        {
            if (subgroup.HasValue) Marshal.StructureToPtr(subgroup.Value, pointer, false);
            var buffer = new byte[16];
            uint size = 16;
            var error = PowerEnumerate(IntPtr.Zero, ref scheme, pointer, accessor, index, buffer, ref size);
            if (error != 0) return error;
            if (size != 16) return 87;
            identity = new Guid(buffer);
            return 0;
        }
        finally
        {
            if (pointer != IntPtr.Zero) Marshal.FreeHGlobal(pointer);
        }
    }

    public uint ReadFriendlyName(Guid scheme, Guid? subgroup, Guid? setting, out string text)
        => ReadText(scheme, subgroup, setting, description: false, out text);

    public uint ReadDescription(Guid scheme, Guid subgroup, Guid setting, out string text)
        => ReadText(scheme, subgroup, setting, description: true, out text);

    // 取值域三个函数只有 4 个参数（root / 分组 / 设置 / out 值）：它们查的是设置自己的
    // ValueMin/ValueMax/ValueIncrement，与方案无关。多传一个 scheme 会整体错位并返回
    // ERROR_FILE_NOT_FOUND（本机实测）。
    public uint ReadValueMin(Guid scheme, Guid subgroup, Guid setting, out uint value)
        => PowerReadValueMin(IntPtr.Zero, ref subgroup, ref setting, out value);

    public uint ReadValueMax(Guid scheme, Guid subgroup, Guid setting, out uint value)
        => PowerReadValueMax(IntPtr.Zero, ref subgroup, ref setting, out value);

    public uint ReadValueIncrement(Guid scheme, Guid subgroup, Guid setting, out uint value)
        => PowerReadValueIncrement(IntPtr.Zero, ref subgroup, ref setting, out value);

    public uint ReadValue(Guid scheme, Guid subgroup, Guid setting, bool ac, out uint value)
        => ac
            ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out value)
            : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out value);

    public uint ReadDefault(Guid scheme, Guid subgroup, Guid setting, bool ac, out uint value)
        => ac
            ? PowerReadACDefaultIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out value)
            : PowerReadDCDefaultIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out value);

    public uint ReadPossibleValue(Guid scheme, Guid subgroup, Guid setting, uint index, out uint value)
    {
        value = 0;
        uint type = 0;
        uint size = 0;
        var error = PowerReadPossibleValue(IntPtr.Zero, ref subgroup, ref setting, out type, index, IntPtr.Zero, ref size);
        if (error != 0) return error;
        // 候选值本身是 REG_DWORD（4 字节）；更长的类型这里按不支持处理。
        if (size is not (1 or 2 or 4)) return 87;
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            var actual = size;
            error = PowerReadPossibleValue(IntPtr.Zero, ref subgroup, ref setting, out type, index, buffer, ref actual);
            if (error != 0) return error;
            value = size switch
            {
                1 => (uint)Marshal.ReadByte(buffer),
                2 => (uint)Marshal.ReadInt16(buffer),
                _ => (uint)Marshal.ReadInt32(buffer)
            };
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public uint ReadPossibleFriendlyName(Guid scheme, Guid subgroup, Guid setting, uint index, out string text)
    {
        text = "";
        var buffer = Marshal.AllocHGlobal(TextCapacity);
        try
        {
            uint size = TextCapacity;
            var error = PowerReadPossibleFriendlyName(IntPtr.Zero, ref subgroup, ref setting, index, buffer, ref size);
            if (error != 0) return error;
            text = ReadString(buffer, size);
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public uint WriteValue(Guid scheme, Guid subgroup, Guid setting, bool ac, uint value)
        => ac
            ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value)
            : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value);

    public uint SetActiveScheme(Guid scheme) => PowerSetActiveScheme(IntPtr.Zero, ref scheme);

    private static uint ReadText(Guid scheme, Guid? subgroup, Guid? setting, bool description, out string text)
    {
        text = "";
        var groupPointer = subgroup.HasValue ? Marshal.AllocHGlobal(16) : IntPtr.Zero;
        var settingPointer = setting.HasValue ? Marshal.AllocHGlobal(16) : IntPtr.Zero;
        var buffer = Marshal.AllocHGlobal(TextCapacity);
        try
        {
            if (subgroup.HasValue) Marshal.StructureToPtr(subgroup.Value, groupPointer, false);
            if (setting.HasValue) Marshal.StructureToPtr(setting.Value, settingPointer, false);
            uint size = TextCapacity;
            var error = description
                ? PowerReadDescription(IntPtr.Zero, ref scheme, groupPointer, settingPointer, buffer, ref size)
                : PowerReadFriendlyName(IntPtr.Zero, ref scheme, groupPointer, settingPointer, buffer, ref size);
            if (error != 0) return error;
            text = ReadString(buffer, size);
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            if (groupPointer != IntPtr.Zero) Marshal.FreeHGlobal(groupPointer);
            if (settingPointer != IntPtr.Zero) Marshal.FreeHGlobal(settingPointer);
        }
    }

    private static string ReadString(IntPtr buffer, uint size)
    {
        var characters = (int)(Math.Min(size, TextCapacity) / 2);
        return characters <= 0 ? "" : (Marshal.PtrToStringUni(buffer, characters) ?? "").TrimEnd('\0');
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerEnumerate(IntPtr root, ref Guid scheme, IntPtr subgroup, uint accessor, uint index, byte[] buffer, ref uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, IntPtr buffer, ref uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadDescription(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, IntPtr buffer, ref uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadValueMin(IntPtr root, ref Guid subgroup, ref Guid setting, out uint value);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadValueMax(IntPtr root, ref Guid subgroup, ref Guid setting, out uint value);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadValueIncrement(IntPtr root, ref Guid subgroup, ref Guid setting, out uint value);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadACDefaultIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadDCDefaultIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadPossibleValue(IntPtr root, ref Guid subgroup, ref Guid setting, out uint type, uint index, IntPtr buffer, ref uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerReadPossibleFriendlyName(IntPtr root, ref Guid subgroup, ref Guid setting, uint index, IntPtr buffer, ref uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
}

/// <summary>
/// 当前活动电源方案的全部电源设置（含系统隐藏项）。
///
/// 为什么不用 <c>powercfg -query</c> 文本解析：输出随系统语言变化，隐藏项的 Attributes
/// 也不在文本里；powrprof 能直接给出 GUID、取值域、默认值和候选值。
///
/// 三处刻意的取舍：
/// 1. 数值型设置也能被 <c>PowerReadPossibleValue</c>「成功」读出候选列表（本机实测是退化数据），
///    所以先用 min/max 判定是不是数值型，只有 min/max 取不到时才枚举候选值。
/// 2. 逐项读取失败只落在该条（<see cref="PowerSettingEntry.ReadFailed"/>），不炸整张表。
/// 3. 隐藏位是**两级**的：设置项自己的 Attributes 与所在分组的 Attributes 都要看。
/// </summary>
internal static class PowerSettingCatalog
{
    internal const uint AccessSubgroup = 17;
    internal const uint AccessSetting = 18;

    /// <summary>系统设置（旧版）分组的 GUID：方案枚举有时不返回它，参考实现的指纹同样会补上。</summary>
    internal static readonly Guid LegacySubgroup = Guid.Parse("fea3413e-7e05-4911-9a71-700331f1c294");

    /// <summary>「常用设置」优先展示的项，按 GUID 认（与分组无关，OEM 改了分组名也认得出）。</summary>
    private static readonly HashSet<Guid> CommonSettings =
    [
        Guid.Parse("893dee8e-2bef-41e0-89c6-b55d0929964c"), // 最小处理器状态
        Guid.Parse("bc5038f7-23e0-4960-96da-33abaf5935ec"), // 最大处理器状态
        Guid.Parse("94d3a615-a899-4ac5-ae2b-e4d8f634367f"), // 系统散热方式
        Guid.Parse("be337238-0d82-4146-a960-4f3749d470c7"), // 处理器性能提升模式
        Guid.Parse("465e1f50-b610-473a-ab58-00d1077dc418"), // 处理器性能提升方式
        Guid.Parse("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"), // 能效偏好
        Guid.Parse("0cc5b647-c1df-4637-891a-dec35c318583"), // 核心停放最小核心数
        Guid.Parse("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"), // 关闭显示器
        Guid.Parse("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"), // 睡眠
        Guid.Parse("9d7815a6-7ee4-497e-8888-515a05f02364"), // 休眠
        Guid.Parse("6738e2c4-e8a5-4a42-b16a-e040e769756e"), // 关闭硬盘
        Guid.Parse("48e6b7a6-50f5-4782-a5d4-53bb8f07e226"), // USB 选择性暂停
        Guid.Parse("ee12f906-d277-404b-b6da-e5fa1a576df5"), // PCIe 链接状态电源管理
        Guid.Parse("12bbebe6-58d6-4636-95bb-3217ef867c1a")  // 无线适配器省电模式
    ];

    /// <summary>单测注入点；必须与 <see cref="RegistryReadOverride"/> / <see cref="RegistryWriteOverride"/> 一起复位。</summary>
    internal static IPowerSettingsApi? ApiOverride { get; set; }
    internal static Func<Guid, Guid?, PowerSettingRegistryInfo>? RegistryReadOverride { get; set; }
    internal static Func<Guid, Guid?, bool, PowerVisibilityWrite>? RegistryWriteOverride { get; set; }

    internal static IPowerSettingsApi Api => ApiOverride ?? NativePowerSettingsApi.Instance;

    internal static bool IsCommonSetting(Guid setting) => CommonSettings.Contains(setting);

    /// <summary>读取当前活动方案的全部设置。返回空表且 <c>Failed</c> 为真表示整表都没读到。</summary>
    internal static PowerSettingScan Load(Guid scheme)
    {
        var api = Api;
        List<Guid> subgroups;
        try
        {
            subgroups = EnumerateIdentities(api, scheme, null, AccessSubgroup);
        }
        catch (Exception ex)
        {
            return new PowerSettingScan([], true, ex.Message);
        }

        var groups = new List<Guid>(subgroups);
        if (!groups.Contains(LegacySubgroup)) groups.Add(LegacySubgroup);

        var entries = new List<PowerSettingEntry>();
        foreach (var subgroup in groups)
        {
            var groupName = "";
            try
            {
                if (api.ReadFriendlyName(scheme, subgroup, null, out var text) != 0) groupName = "";
                else groupName = text;
            }
            catch
            {
                groupName = "";
            }

            var groupInfo = ReadRegistry(subgroup, null);
            List<Guid> settings;
            try
            {
                settings = EnumerateIdentities(api, scheme, subgroup, AccessSetting);
            }
            catch
            {
                // 单个分组枚举失败只跳过这个分组，其他组照常列出。
                continue;
            }

            foreach (var setting in settings)
                entries.Add(ReadEntry(api, scheme, subgroup, groupName, groupInfo, setting));
        }

        entries.Sort(Compare);
        return new PowerSettingScan(entries, false, null);
    }

    /// <summary>只重读一条（写完/切方案后刷新，避免整表重扫）。</summary>
    internal static PowerSettingEntry ReRead(PowerSettingEntry previous, Guid scheme)
        => ReadEntry(Api, scheme, previous.Subgroup, previous.SubgroupName, ReadRegistry(previous.Subgroup, null), previous.Setting);

    internal static PowerValueCheck Check(PowerSettingEntry entry, string text)
    {
        if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return new PowerValueCheck(PowerValueIssue.NotANumber, 0, null);
        return Check(entry, parsed);
    }

    internal static PowerValueCheck Check(PowerSettingEntry entry, long value)
    {
        if (value < 0) return new PowerValueCheck(PowerValueIssue.OutOfRange, 0, entry.Minimum);

        if (entry.PossibleValues.Count > 0)
        {
            foreach (var possible in entry.PossibleValues)
                if (possible.Value == value) return new PowerValueCheck(PowerValueIssue.None, (uint)value, null);
            return new PowerValueCheck(PowerValueIssue.NotAnOption, 0, null);
        }

        if (!entry.DomainKnown)
        {
            // 取值域没读到：只做 uint 边界校验，越界值由系统自己拒绝。
            return value <= uint.MaxValue
                ? new PowerValueCheck(PowerValueIssue.None, (uint)value, null)
                : new PowerValueCheck(PowerValueIssue.OutOfRange, 0, null);
        }

        if (value < entry.Minimum || value > entry.Maximum)
            return new PowerValueCheck(PowerValueIssue.OutOfRange, 0, Nearest(entry, value));

        if (entry.Increment > 1)
        {
            var offset = value - entry.Minimum;
            if (offset % entry.Increment != 0)
                return new PowerValueCheck(PowerValueIssue.StepMismatch, 0, Nearest(entry, value));
        }

        return new PowerValueCheck(PowerValueIssue.None, (uint)value, null);
    }

    private static uint Nearest(PowerSettingEntry entry, long value)
    {
        if (entry.Increment <= 1) return (uint)Math.Clamp(value, entry.Minimum, entry.Maximum);
        var steps = (uint)Math.Round((value - entry.Minimum) / (double)entry.Increment, MidpointRounding.AwayFromZero);
        var candidate = entry.Minimum + steps * entry.Increment;
        return (uint)Math.Clamp(candidate, entry.Minimum, entry.Maximum);
    }

    private static PowerSettingEntry ReadEntry(
        IPowerSettingsApi api, Guid scheme, Guid subgroup, string groupName,
        PowerSettingRegistryInfo groupInfo, Guid setting)
    {
        var info = ReadRegistry(subgroup, setting);

        var name = "";
        var nameFailed = false;
        try
        {
            if (api.ReadFriendlyName(scheme, subgroup, setting, out var text) != 0) nameFailed = true;
            else name = text;
        }
        catch
        {
            nameFailed = true;
        }

        var description = "";
        try
        {
            if (api.ReadDescription(scheme, subgroup, setting, out var text) != 0) description = "";
            else description = text;
        }
        catch
        {
            description = "";
        }

        string? error = null;
        var ac = TryRead((out uint value) => api.ReadValue(scheme, subgroup, setting, true, out value), ref error);
        var dc = TryRead((out uint value) => api.ReadValue(scheme, subgroup, setting, false, out value), ref error);
        var acDefault = TryRead((out uint value) => api.ReadDefault(scheme, subgroup, setting, true, out value), ref error);
        var dcDefault = TryRead((out uint value) => api.ReadDefault(scheme, subgroup, setting, false, out value), ref error);

        var minimum = TryRead((out uint value) => api.ReadValueMin(scheme, subgroup, setting, out value), ref error);
        var maximum = TryRead((out uint value) => api.ReadValueMax(scheme, subgroup, setting, out value), ref error);
        var increment = TryRead((out uint value) => api.ReadValueIncrement(scheme, subgroup, setting, out value), ref error);
        var numeric = minimum.HasValue && maximum.HasValue && maximum.Value > minimum.Value;

        var possible = numeric
            ? (IReadOnlyList<PowerPossibleValue>)[]
            : ReadPossibleValues(api, scheme, subgroup, setting, name);

        return new PowerSettingEntry
        {
            Subgroup = subgroup,
            Setting = setting,
            SubgroupName = groupName,
            Name = name,
            Description = description,
            Units = info.Units ?? "",
            Minimum = minimum ?? 0,
            Maximum = maximum ?? 0,
            Increment = increment ?? 0,
            PossibleValues = possible,
            Ac = ac,
            Dc = dc,
            AcDefault = acDefault,
            DcDefault = dcDefault,
            HiddenByItem = IsHidden(info.ItemAttributes),
            HiddenByGroup = IsHidden(groupInfo.GroupAttributes),
            NameReadFailed = nameFailed,
            ValuesReadFailed = ac is null && dc is null,
            Error = error,
            IsCommon = IsCommonSetting(setting),
            RangeKnown = numeric
        };
    }

    private static IReadOnlyList<PowerPossibleValue> ReadPossibleValues(
        IPowerSettingsApi api, Guid scheme, Guid subgroup, Guid setting, string settingName)
    {
        var values = new List<PowerPossibleValue>();
        for (uint index = 0; index < 64; index++)
        {
            uint value;
            try
            {
                if (api.ReadPossibleValue(scheme, subgroup, setting, index, out value) != 0) break;
            }
            catch
            {
                break;
            }

            var name = "";
            try
            {
                if (api.ReadPossibleFriendlyName(scheme, subgroup, setting, index, out var text) != 0) name = "";
                else name = text;
            }
            catch
            {
                name = "";
            }

            values.Add(new PowerPossibleValue(value, name));
        }

        // 退化读数：系统对某些项会把设置名当成每个候选值的名字，这种列表没有意义。
        if (values.Count > 1 && values.All(value => string.IsNullOrWhiteSpace(value.Name) || value.Name == settingName))
            return [];

        values.Sort((a, b) => a.Value.CompareTo(b.Value));
        return values;
    }

    private static List<Guid> EnumerateIdentities(IPowerSettingsApi api, Guid scheme, Guid? subgroup, uint accessor)
    {
        var result = new List<Guid>();
        for (uint index = 0; index < 4096; index++)
        {
            var error = api.Enumerate(scheme, subgroup, accessor, index, out var identity);
            if (error == 259) break;
            if (error != 0) throw new Win32Exception((int)error);
            result.Add(identity);
        }
        return result;
    }

    private static int Compare(PowerSettingEntry a, PowerSettingEntry b)
    {
        if (a.IsCommon != b.IsCommon) return a.IsCommon ? -1 : 1;
        var compare = string.Compare(a.SubgroupName, b.SubgroupName, StringComparison.CurrentCulture);
        if (compare != 0) return compare;
        compare = string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
        return compare != 0 ? compare : a.Setting.CompareTo(b.Setting);
    }

    private delegate uint UintProbe(out uint value);

    private static uint? TryRead(UintProbe probe, ref string? error)
    {
        uint value = 0;
        try
        {
            var code = probe(out value);
            if (code == 0) return value;
            error ??= "Win32 error " + code.ToString(CultureInfo.InvariantCulture) + ".";
            return null;
        }
        catch (Exception ex)
        {
            error ??= ex.Message;
            return null;
        }
    }

    /// <summary>Attributes 的 0x1 是隐藏位，0x2 是显式显示位（显式显示优先）。</summary>
    internal static bool IsHidden(int? attributes)
        => attributes is int value && (value & 1) != 0 && (value & 2) == 0;

    internal static PowerSettingRegistryInfo ReadRegistry(Guid subgroup, Guid? setting)
        => RegistryReadOverride?.Invoke(subgroup, setting) ?? ReadRegistryNative(subgroup, setting);

    internal static PowerVisibilityWrite WriteRegistry(Guid subgroup, Guid? setting, bool visible)
        => RegistryWriteOverride?.Invoke(subgroup, setting, visible) ?? WriteRegistryNative(subgroup, setting, visible);

    private const string PowerSettingsKey = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\";

    private static PowerSettingRegistryInfo ReadRegistryNative(Guid subgroup, Guid? setting)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            int? group = null;
            int? item = null;
            string? units = null;
            using (var groupKey = baseKey.OpenSubKey(PowerSettingsKey + RegistryName(subgroup)))
            {
                group = ReadDword(groupKey?.GetValue("Attributes"));
            }

            if (setting.HasValue)
            {
                using var settingKey = baseKey.OpenSubKey(PowerSettingsKey + RegistryName(subgroup) + "\\" + RegistryName(setting.Value));
                item = ReadDword(settingKey?.GetValue("Attributes"));
                // 单位存在 ValueUnits 里，内容多为间接字符串 @powrprof.dll,-81,percent，需要解一次。
                units = ResolveUnits(settingKey, settingKey?.GetValue("ValueUnits") as string);
            }

            return new PowerSettingRegistryInfo(item, group, units);
        }
        catch
        {
            // 读注册表失败按「不知道」处理：既不误标隐藏，也不阻断列表。
            return new PowerSettingRegistryInfo(null, null, null);
        }
    }

    private static PowerVisibilityWrite WriteRegistryNative(Guid subgroup, Guid? setting, bool visible)
    {
        var path = PowerSettingsKey + RegistryName(subgroup);
        if (setting.HasValue) path += "\\" + RegistryName(setting.Value);
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path, writable: true);
            if (key is null) return new PowerVisibilityWrite(PowerVisibilityStatus.Missing, null);

            var current = ReadDword(key.GetValue("Attributes")) ?? 0;
            var next = visible ? (current & ~1) | 2 : current | 1;
            if (next == current) return new PowerVisibilityWrite(PowerVisibilityStatus.Unchanged, null);

            key.SetValue("Attributes", next, RegistryValueKind.DWord);
            var persisted = ReadDword(key.GetValue("Attributes"));
            return persisted == next
                ? new PowerVisibilityWrite(PowerVisibilityStatus.Updated, null)
                : new PowerVisibilityWrite(PowerVisibilityStatus.Failed, "The Attributes value did not persist.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new PowerVisibilityWrite(PowerVisibilityStatus.Denied, ex.Message);
        }
        catch (System.Security.SecurityException ex)
        {
            return new PowerVisibilityWrite(PowerVisibilityStatus.Denied, ex.Message);
        }
        catch (Exception ex)
        {
            return new PowerVisibilityWrite(PowerVisibilityStatus.Failed, ex.Message);
        }
    }

    // 系统写的是不带花括号的名称（本机实测：0012ee47-... 而不是 {0012EE47-...}），用 "D" 格式。
    private static string RegistryName(Guid value) => value.ToString("D");

    private static string? ResolveUnits(RegistryKey? key, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!raw.StartsWith("@", StringComparison.Ordinal)) return raw;
        if (key is null) return null;
        try
        {
            var buffer = new StringBuilder(256);
            uint required = 0;
            var result = RegLoadMUIString(key.Handle.DangerousGetHandle(), "ValueUnits", buffer,
                (uint)buffer.Capacity * 2, ref required, 0, null);
            var text = buffer.ToString();
            return result == 0 && text.Length > 0 ? text : null;
        }
        catch
        {
            // 解析不了就当作没有单位，不显示半截英文兜底串。
            return null;
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegLoadMUIString(IntPtr key, string valueName, StringBuilder buffer, uint bufferSizeBytes, ref uint required, uint flags, string? directory);

    private static int? ReadDword(object? value) => value switch
    {
        int number => number,
        byte[] bytes when bytes.Length >= 4 => BitConverter.ToInt32(bytes, 0),
        _ => null
    };
}
