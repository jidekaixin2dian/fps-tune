# P3-10 · catalog 优化项扩展调研（2026-09-29）

> 调研动机：P0/P1/P2/P3 全部收口后，交接提示给出的默认方向是「优化项扩展调研」（P2-6 模式）。
> 本文 = 候选池 + 权威依据 + 红线预审 + 落地成本，**结论供用户拍板后再实现**（产品内容新增 = 用户决策，同 P3-9 先例）。
> 调研方法：Microsoft Learn / 官方硬件文档 / 社区权威实现（GitHub 高星工具）交叉核实，见各条目引文。

## 候选池总览

catalog 现有 33 项（键鼠 4 / 图形显示 6 / 网络 2 / 电源 3 / 系统与调度 18）。本轮筛查出的**未覆盖且过红线预审**的系统层候选项 4 个：

| # | 候选项 | 判定 | 一句话理由 |
|---|---|---|---|
| C1 | 鼠标缓冲区扩容（`Mouclass\Parameters\MouseDataQueueSize` 20→50） | **推荐** | 与已发布的 keyboard-latency 完全同构；Microsoft Learn 记载该参数 |
| C2 | 全局定时器分辨率请求（`GlobalTimerResolutionRequests=1`） | **推荐（带版本条件）** | Win11+/Server 21H2+ 定时器请求默认只作用于调用进程；此键恢复系统级语义。零风险、可还原 |
| C3 | PCIe 链路状态电源管理 = 关闭（ASPM None） | **推荐** | 微软官方隐藏电源设置（GUID 已核实）；与 power-tuning 同机制（powercfg） |
| C4 | 网卡节能关闭（`PnPCapabilities=0x18`，逐网卡枚举） | **推荐（带实现注意）** | 联机游戏空闲掉线/抖动的经典根因；需驱动更新会重置的副作用披露 |

评估过但**不进 catalog** 的（防止以后重查）：

- **Spectre/Meltdown 缓解措施关闭**：收益真实（部分老 CPU 明显）但属安全性整体下调，且收益高度依赖 CPU 代际——**不做**（与红线「可信透明」冲突：解释不清普遍收益）。
- **VBS / 内核隔离（内存完整性）关闭**：部分平台有实测帧率收益，但这是显著的安全特性降级，**默认不做**；若用户点名可作为独立项评估（须显式安全权衡文案）。
- **HPET / useplatformclock**：现代 Windows 默认已最优，强行 bcdedit 反而劣化；**不做**。
- **Defender 游戏目录排除**：安全面下调 + 每游戏路径级；**不做**。
- **LargeSystemCache / 页面文件固定大小**：社区玄学，客户端 Windows 默认已正确，可能劣化；**不做**。
- **MSI 模式强制**：设备相关、错配可能启动异常；**不做**。
- **图像锐化 / 三重缓冲**：P2-6 已有结论，不重查。

## 逐项依据

### C1 鼠标缓冲区扩容

- `HKLM\SYSTEM\CurrentControlSet\Services\Mouclass\Parameters\MouseDataQueueSize`：Microsoft Learn《Configuration of Keyboard and Mouse Class Drivers》记载——"Specifies the number of mouse events buffered by the mouse driver"。
- 与 `keyboard-latency`（已发布，同文档 Kbdclass 侧）完全同构：DWORD 20→50，重启生效，可还原（备份已支持原值不存在→还原时删除）。
- 红线：可还原 ✓；零侵入 ✓；解释清晰（高轮询率鼠标高频移动事件下降低缓冲溢出概率）✓。
- 落地成本：**低**——引擎 `RegistrySetIfDifferent` 一行 + 备份 case + 检测映射 + catalog 条目。

### C2 全局定时器分辨率请求

- `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel\GlobalTimerResolutionRequests=1`（DWORD，通常不存在，需新建）。
- 适用：**Windows 11+ / Windows Server 21H2+**（Win10 本来就是系统级，设了无意义）。Win11 24H2 起定时器请求进一步收窄为「前台进程作用域」，此键仍将其恢复为系统级。
- 引擎已有 `WindowsVersionHelper`，不满足版本时自动跳过（套 power-tuning 的跳过模式，不报错）。
- 红线：可还原 ✓（还原=删除该值，机制已存在）；副作用=空闲功耗微升（定时器不再回落）。
- 收益定位：与 mmcss-games 同档——**收益微弱但零风险**，文案如实写。
- 落地成本：**低**——同 C1，多一个版本条件判断。

### C3 PCIe 链路状态电源管理 = 关闭

- 微软官方《Link state power management》（learn.microsoft.com/windows-hardware/customize/power-settings/pci-express-settings-link-state-power-management）：
  - 子组 GUID `501a4d13-42af-4429-9fd1-a8218c268e20`（SUB_PCIEXPRESS），隐藏设置；
  - ASPM 设置 GUID `ee12f906-d277-404b-b6da-e5fa1a576df5`；值 0=None / 1=Moderate / 2=Maximum。
- ASPM 在链路空闲时切入 L0s/L1 低功耗态，退出延迟可能造成 GPU/NVMe 等设备的微卡顿；设为 None = 关闭 ASPM。
- 机制与 power-tuning 相同（powercfg -setacvalueindex / -setdcvalueindex + 隐藏设置解除），备份记录原始 AC/DC 值（BackupService 已有 power-tuning 三项的先例字段）。
- 红线：可还原 ✓；副作用=整机功耗略升（笔记本用户需知晓，文案明示）。
- 落地成本：**中**——套 power-tuning 的 power 分支模式。

### C4 网卡节能关闭

- `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\<NN>\PnPCapabilities=0x18`（24）= 取消勾选「允许计算机关闭此设备以节约电源」。
- 该路径按适配器实例枚举（0001、0002…），**需逐实例处理**：跳过虚拟网卡（Characteristics 含 NCF_VIRTUAL）、值已为 0x18 的跳过、原值不存在时记录 Existed=false。
- 已核实的副作用（Microsoft Q&A 实例）：**网卡驱动更新可能重置该值**——检测页会如实重新报"未达标"，文案披露。
- 红线：可还原 ✓（逐实例备份原值）；零侵入 ✓；联机 FPS 通用（空闲节能导致的首次包延迟/瞬时抖动）✓。
- 落地成本：**中**——多实例枚举 + 过滤逻辑 + 逐实例备份记录，四个候选里最贵但仍是熟悉模式的组合。

## 预设归属建议（若落地）

- `balanced`：C1（同 keyboard-latency 的排除口径——默认不勾）、C3、C4 建议入 balanced；C2 建议默认不勾（收益微弱，留给手动）。
- `safe-only`：四项均不进（safe-only 只有 5 个零风险图形/系统项）。

## 结论

四项均过红线预审、依据可查、成本可控。**是否落地、落哪几项，等用户拍板**；批准后按上表成本实现 + 全量测试 + bump 0.1.18 候选。
