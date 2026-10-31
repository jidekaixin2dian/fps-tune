using System.Runtime.InteropServices;

namespace FpsTune.Wpf.Core;

/// <summary>
/// FilterKeys（筛选键）读写：按住右 Shift 8 秒误触弹窗的根源是 FKF_HOTKEYACTIVE 热键位。
/// 用 SystemParametersInfo 读写 FILTERKEYS——同一调用既更新运行时状态又持久化到
/// HKCU\Control Panel\Accessibility\Keyboard Response，立即生效，无需重新登录。
/// 只清热键位，不改用户自用的筛选节奏（等待/延迟/重复参数原样保留）。
/// </summary>
internal static class NativeAccessibility
{
    internal const int FkfFilterKeysOn = 0x1;
    internal const int FkfAvailable = 0x2;
    internal const int FkfHotkeyActive = 0x4;
    internal const int FkfConfirmHotkey = 0x8;

    private const int SpiGetFilterKeys = 0x003A;
    private const int SpiSetFilterKeys = 0x003B;
    private const int SpifUpdateIniFile = 0x01;
    private const int SpifSendChange = 0x02;

    internal static Func<FilterKeysState>? ReadOverride { get; set; }
    internal static Func<FilterKeysState, uint>? WriteOverride { get; set; }

    public sealed record FilterKeysState(
        int Flags, int WaitPeriod, int DelayRate, int RepeatDelay, int RepeatRate, int BounceMSec)
    {
        /// <summary>按住右 Shift 触发 FilterKeys 询问的热键是否开启。</summary>
        public bool HotkeyActive => (Flags & FkfHotkeyActive) != 0;

        /// <summary>只关闭误触路径（热键位与确认弹窗位），其余位保持用户原值。</summary>
        public FilterKeysState WithoutHotkey() => this with
        {
            Flags = Flags & ~(FkfHotkeyActive | FkfConfirmHotkey)
        };
    }

    /// <summary>当前 FilterKeys 状态；读取失败抛出（调用方决定如何呈现）。</summary>
    internal static FilterKeysState Read()
    {
        if (ReadOverride is not null) return ReadOverride();
        var keys = new FILTERKEYS { cbSize = (uint)Marshal.SizeOf<FILTERKEYS>() };
        var ok = SystemParametersInfo(SpiGetFilterKeys, (uint)Marshal.SizeOf<FILTERKEYS>(), ref keys, 0);
        if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new((int)keys.dwFlags, (int)keys.iWaitPeriod, (int)keys.iDelayRate,
            (int)keys.iRepeatDelay, (int)keys.iRepeatRate, (int)keys.iBounceMSec);
    }

    /// <summary>写入并广播（立即生效 + 持久化）；返回 Win32 错误码（0 成功）。</summary>
    internal static uint Write(FilterKeysState state)
    {
        if (WriteOverride is not null) return WriteOverride(state);
        var keys = new FILTERKEYS
        {
            cbSize = (uint)Marshal.SizeOf<FILTERKEYS>(),
            dwFlags = (uint)state.Flags,
            iWaitPeriod = (uint)state.WaitPeriod,
            iDelayRate = (uint)state.DelayRate,
            iRepeatDelay = (uint)state.RepeatDelay,
            iRepeatRate = (uint)state.RepeatRate,
            iBounceMSec = (uint)state.BounceMSec
        };
        var ok = SystemParametersInfo(SpiSetFilterKeys, (uint)Marshal.SizeOf<FILTERKEYS>(), ref keys,
            SpifUpdateIniFile | SpifSendChange);
        return ok ? 0 : (uint)Marshal.GetLastWin32Error();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILTERKEYS
    {
        public uint cbSize;
        public uint dwFlags;
        public uint iWaitPeriod;
        public uint iDelayRate;
        public uint iRepeatDelay;
        public uint iRepeatRate;
        public uint iBounceMSec;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint size, ref FILTERKEYS filterKeys, uint profile);
}
