namespace DeltaForceTune.Wpf.Core;

public sealed record OptimizationItemDefinition(
    string Id,
    string Name,
    string Description,
    string SideEffect,
    bool Admin,
    bool Reboot,
    string Kind);

public static class ItemCatalog
{
    public static IReadOnlyList<OptimizationItemDefinition> All { get; } = new List<OptimizationItemDefinition>
    {
        new("power-ultimate", "电源计划 → 卓越性能", "切换到卓越性能电源计划，让 CPU 更积极跑满频率。", "功耗与发热略升；笔记本续航变短。", true, false, "power"),
        new("power-tuning", "电源计划隐藏项调优", "调整影响性能的隐藏电源参数。", "功耗略升；不支持的 CPU 自动跳过。", true, true, "power"),
        new("hags", "开启硬件加速 GPU 计划", "HwSchMode=2，降低输入延迟与掉帧。", "个别老驱动可能蓝屏，可还原。", true, true, "registry"),
        new("game-mode", "开启 Windows 游戏模式", "游戏运行时优先分配 CPU/GPU 资源。", "", false, false, "registry"),
        new("dvr-off", "关闭 Xbox 后台录制", "减少后台编码负载。", "Win+G 录制不可用。", false, false, "registry"),
        new("prio-separation", "前台进程调度权重提升", "提升前台游戏进程调度优先级。", "个别后台软件响应变慢。", true, false, "registry"),
        new("wer-off", "关闭 Windows 错误报告", "减少崩溃时的磁盘/CPU 开销。", "崩溃时无系统提示。", true, false, "registry"),
        new("transparency-off", "关闭窗口透明特效", "减少桌面 GPU 开销。", "桌面观感变朴素。", false, false, "registry"),
        new("fso-off", "禁用游戏全屏优化", "避免部分游戏全屏优化带来的兼容问题。", "需要游戏路径。", false, false, "layers"),
        new("gpu-pref", "游戏强制使用高性能 GPU", "指定游戏使用独显。", "需要游戏路径。", false, false, "registry"),
        new("mpo-off", "禁用 MPO 多平面叠加", "减少部分 N 卡掉帧/闪屏。", "可能影响桌面合成特效。", true, true, "registry"),
        new("net-throttling-off", "解除多媒体网络限流", "降低网络限流对在线游戏的影响。", "", true, false, "registry"),
        new("sys-responsiveness", "系统后台响应保留设为最低", "给前台游戏更多 CPU 资源。", "", true, false, "registry"),
        new("mmcss-games", "MMCSS 游戏任务档位拉满", "提升游戏线程调度优先级。", "", true, false, "registry"),
        new("sysmain-off", "禁用 SysMain 服务", "减少后台预取和磁盘活动。", "可能影响系统响应缓存。", true, true, "service"),
        new("wsearch-off", "禁用 Windows Search 索引", "减少后台索引负载。", "Windows 搜索功能受限。", true, true, "service"),
        new("hibernate-off", "关闭休眠与快速启动", "减少休眠文件占用。", "不能使用休眠功能。", true, true, "hibernate"),
        new("game-priority", "游戏进程 CPU 优先级提到高", "提高游戏进程 CPU 优先级。", "需要游戏路径。", true, false, "registry"),
        new("paging-exec", "内核代码常驻内存", "减少内核内存分页。", "内存占用略增。", true, true, "registry"),
        new("mem-compress-off", "关闭内存压缩", "减少内存压缩带来的 CPU 负载。", "内存占用可能增加。", true, true, "registry"),
        new("gpu-pstate-lock", "禁止显卡动态降频", "锁定显卡性能状态，减少掉帧。", "显卡功耗和温度可能上升。", true, true, "registry"),
        new("dyntick-off", "禁用动态计时器", "降低系统计时器抖动。", "可能增加系统功耗。", true, true, "bcdedit"),
    };
}
