using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FpsTune.Wpf.Core;

/// <summary>
/// Windows 11 "电源模式"（现代电源滑块：最佳能效 / 平衡 / 最佳性能）的读写。
/// 接口是 powrprof.dll 导出的 PowerGetActualOverlayScheme / PowerSetActiveOverlayScheme
/// （未写入官方头文件，签名已在本机 26300 实测：单参数 GUID，返回 Win32 错误码）。
/// 平衡态表示为零 GUID，不对应任何方案键。
/// </summary>
internal static class NativePowerOverlay
{
    public static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");
    public static readonly Guid BestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    /// <summary>滑块"平衡"位置：系统里存零 GUID，而不是某个方案。</summary>
    public static readonly Guid Balanced = Guid.Empty;

    internal const string UserPowerSchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    internal const string AcOverlayValueName = "ActiveOverlayAcPowerScheme";
    internal const string DcOverlayValueName = "ActiveOverlayDcPowerScheme";

    internal static Func<Guid>? ReadOverride { get; set; }
    internal static Func<Guid, uint>? WriteOverride { get; set; }

    /// <summary>注册表里是否存在 overlay 记录；不存在的系统（无现代滑块）应隐藏该项。</summary>
    internal static Func<bool>? SupportedOverride { get; set; }

    /// <summary>当前生效的 overlay（接电态）。读不到或系统不支持时返回 null。</summary>
    internal static Guid? ReadActual()
    {
        if (ReadOverride is not null) return ReadOverride();
        var error = PowerGetActualOverlayScheme(out var guid);
        return error == 0 ? guid : null;
    }

    /// <summary>写入 overlay 并由调用方读回验证；返回 Win32 错误码（0 成功）。</summary>
    internal static uint Write(Guid overlay)
    {
        if (WriteOverride is not null) return WriteOverride(overlay);
        return PowerSetActiveOverlayScheme(overlay);
    }

    /// <summary>是否支持现代电源模式：以注册表持久化值为准（值存在即支持，含零 GUID 平衡态）。</summary>
    internal static bool IsSupported()
    {
        if (SupportedOverride is not null) return SupportedOverride();
        return RegistryHelper.ValueExists(RegistryHive.LocalMachine, UserPowerSchemesKey, AcOverlayValueName);
    }

    /// <summary>注册表持久化的接电 overlay；缺失或不是合法 GUID 返回 null。</summary>
    internal static Guid? ReadPersistedAc()
    {
        var text = RegistryHelper.ReadValue(RegistryHive.LocalMachine, UserPowerSchemesKey, AcOverlayValueName)?.ToString();
        return Guid.TryParse(text, out var guid) ? guid : null;
    }

    /// <summary>
    /// 写入后验证：API 读回优先，注册表持久化值兜底（用电池时 API 可能返回 DC 侧 overlay）。
    /// </summary>
    internal static bool VerifyApplied(Guid expected)
        => ReadActual() == expected || ReadPersistedAc() == expected;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerGetActualOverlayScheme(out Guid overlayGuid);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlayGuid);
}
