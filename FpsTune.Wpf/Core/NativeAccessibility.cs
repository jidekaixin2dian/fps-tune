using System.Globalization;
using System.IO;
using FpsTune.Wpf.Services;
using Microsoft.Win32;

namespace FpsTune.Wpf.Core;

/// <summary>
/// FilterKeys（筛选键）防误触：误触热键位在 HKCU\Control Panel\Accessibility\Keyboard Response 的 Flags 里。
/// 新版 Windows（本机 26300 实测）SPI_GET/SETFILTERKEYS 结构体查询已不可靠（返回 FALSE，GetLastError=87/0），
/// 因此与 sticky-keys-off 一致走注册表：写前由备份契约记录原值，写后读回验证；重新登录后完全生效。
/// 只清热键位（FKF_HOTKEYACTIVE|FKF_CONFIRMHOTKEY），其他位（筛选键开关、节奏参数）原样保留。
/// </summary>
internal static class NativeAccessibility
{
    internal const int FkfFilterKeysOn = 0x1;
    internal const int FkfAvailable = 0x2;
    internal const int FkfHotkeyActive = 0x4;
    internal const int FkfConfirmHotkey = 0x8;

    private const string KeyPath = @"Control Panel\Accessibility\Keyboard Response";
    private const string ValueName = "Flags";

    /// <summary>读 Flags；值不存在或不是合法整数返回 null（调用方按不可用处理）。</summary>
    internal static int? ReadFlags()
    {
        var raw = RegistryHelper.ReadValue(RegistryHive.CurrentUser, KeyPath, ValueName)?.ToString();
        return int.TryParse(raw, out var flags) ? flags : null;
    }

    /// <summary>写 Flags 并读回验证；读回不一致抛出（调用方按单项失败处理，备份已保留）。</summary>
    internal static void WriteFlags(int flags)
    {
        RegistryHelper.SetValue(RegistryHive.CurrentUser, KeyPath, ValueName,
            flags.ToString(CultureInfo.InvariantCulture), RegistryValueKind.String);
        if (ReadFlags() != flags)
            throw new IOException(Str.T("Str.FilterKeysReadbackMismatch"));
    }
}
