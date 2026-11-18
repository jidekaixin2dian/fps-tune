using Microsoft.Win32;

namespace FpsTune.Wpf.Core;

/// <summary>由单个注册表 DWORD 表示的优化项。检测、备份和写入共用此目标。</summary>
internal sealed record SimpleRegistryTweak(RegistryHive Hive, string Path, string Name, int Value, string AppliedMessage)
{
    internal static SimpleRegistryTweak? For(string id) => id switch
    {
        "hags" => new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, "已开启 HAGS"),
        "mpo-off" => new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5, "已禁用 MPO"),
        "prio-separation" => new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x28, "已提升前台进程调度权重"),
        "wer-off" => new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 1, "已关闭错误报告"),
        "transparency-off" => new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, "已关闭透明特效"),
        "net-throttling-off" => new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", -1, "已解除网络限流"),
        "sys-responsiveness" => new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 10, "已降低后台响应保留"),
        "paging-exec" => new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "DisablePagingExecutive", 1, "已开启内核常驻内存"),
        _ => null
    };
}
