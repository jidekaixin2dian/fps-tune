---
name: fps-tune
description: 三角洲行动（Delta Force）Windows 系统层帧率优化。检测电脑硬件、游戏安装位置与当前系统设置，经用户逐项确认后批量应用可还原的 Windows 层优化（电源计划、HAGS、游戏模式、关闭后台录制、MMCSS、网络限流等），并为对应显卡厂商给出驱动内手动设置清单。所有改动写入前自动备份，支持一键还原。当用户提到"三角洲行动 卡顿 / 掉帧 / 帧数低 / 画面优化 / 帧率优化 / 优化设置"时使用。
---

# 三角洲行动 · 系统层帧率优化（通用 Agent 技能）

本技能与具体 AI 工具无关：任何能在用户 Windows 电脑上执行 PowerShell 的助手
（Claude Code、Codex、WorkBuddy、豆包等）按下面的流程操作即可。核心逻辑全部在
`fps-tune.ps1` 里，你只负责：**检测 → 向用户解释 → 征得确认 → 执行 → 汇报**。
所有系统改动都由确定性脚本完成并自动备份，你的角色是"理解意图、解释取舍、把关确认"，
而不是替脚本决定改什么。

## 前置条件

- Windows 10 / 11，自带 Windows PowerShell 5.1 即可，无需安装任何东西。
- 部分项（电源计划、HAGS、系统服务等）需要**管理员权限**的 PowerShell。
- 脚本位置：`<root>\fps-tune.ps1`（下文 `<root>` 指脚本所在目录）。
- 所有命令用 `-ExecutionPolicy Bypass`（下载的脚本带网络标记，默认策略会拒绝）。
- 不要在未安装脚本的机器上凭空执行；用户没有脚本时，把脚本交给用户/放到本机目录后再继续。

## 流程

### 第 1 步：检测（只读，安全）

```
powershell -NoProfile -ExecutionPolicy Bypass -File "<root>\fps-tune.ps1" -Detect -Json
```

返回 JSON：
- `hardware`：CPU / 内存 / 显卡（含厂商）/ 系统版本 / 是否笔记本 / 是否管理员
- `gamePath`：自动找到的游戏主程序（运行中进程 → 卸载注册表 → 常见盘符兜底；找不到为 null）
- `items`：每个优化项的 id、说明、副作用、是否需要管理员、当前是否已达标
- `checks`：只读体检结果（VC++ 运行库缺失 / 内存频率 / PCIe 链路），只报告不修改
- `presets`：三套预设（full / balanced / safe-only）对应的项目清单
- `gpuGuide`：按用户显卡厂商生成的驱动内手动设置清单

### 第 2 步：向用户汇报并确认

用平实的语言告诉用户：检测到什么硬件、游戏装在哪、哪些项已达标、哪些项建议优化、
每项干什么、有什么副作用。**必须先获得用户明确同意才能进入第 3 步**——这是改系统设置。

需要如实告知的注意点：

- 需要管理员的项，当前会话不是管理员时会失败并明确报错——此时请用户用管理员身份重开终端。
- `fso-off` / `gpu-pref` / `game-priority` 依赖找到游戏主程序；找不到时这三项自动跳过，
  可让用户提供游戏安装位置后用 `-GamePath "主程序完整路径"` 补上。
- `wsearch-off`（禁用搜索索引）会让系统搜索明显变慢；`hibernate-off`（关休眠）会顺带
  关掉快速启动，笔记本合盖只剩睡眠——这两项默认不勾选，勾选前务必说明。
- `power-tuning` 会改电源隐藏参数，个别主板/笔记本厂商策略下可能引起异常（极少见）；
  出现异常用还原命令恢复即可。
- 纯检测项（`checks`）查出问题时是"体检立功"而不是工具失败，转述时区分开：
  - VC++ 缺失：只报"哪个架构缺失"，给微软官方链接（aka.ms/vs/18/release/vc_redist.x64.exe
    与 .x86.exe）。x64 与 x86 是两套独立运行库，**版本不同步通常无害**，不要引导用户
    卸载重装其他年份的 VC++；缺失时覆盖安装即可，装完重启再检测。
  - 内存频率：当前运行频率与 BIOS 配置频率不一致时，提示进 BIOS 查 XMP/A-XMP/EXPO/DOCP
    （菜单名因主板品牌和 CPU 平台而异）；菜单不存在说明厂商未开放，不强求。
  - PCIe 链路：仅 NVIDIA 卡自动读取；当前 Gen 低于上限时提示检查插槽/延长线。
- **不要建议关闭引导虚拟化**：ACE 反作弊会检查虚拟化状态，关掉会导致游戏报错进不去。

### 第 3 步：应用（必须已获用户同意）

```
powershell -NoProfile -ExecutionPolicy Bypass -File "<root>\fps-tune.ps1" -Apply -Preset balanced -Force -Json
```

- 默认套用预设：`-Preset balanced`（副作用小）通常最合适；`full` 含全部
  22 项；`safe-only` 只改当前用户设置、通常不需要管理员。
- 也可以逐项：`-Apply -Items power-ultimate,hags,dvr-off -Force -Json`。
- 预设/清单念给用户听，让用户选，不要替用户决定勾哪些。
- **`-Force` 是"已获得用户同意"的开关**：没有 `-Force` 脚本会拒绝执行（exit 3）。
  这是防呆设计，不是免责声明——你仍然必须真的先征得用户同意。
- 结果里每项带 `ok` / `changed` / `skipped`，末尾有 `summary`（x 成功、y 失败、z 跳过）。
  `reboot` 数组列出需要重启才完全生效的项，汇报时按它提醒用户重启，别自己猜。
- 每次 Apply 会先把所有原值写入 `%LocalAppData%\FpsTune\backup\backup-<时间戳>.json`，
  结果里有 `backupFile` 路径；转述给用户，告诉他还原就靠这份备份。

### 第 4 步：显卡驱动内设置（手动，念给用户听）

驱动内的 3D 设置无法安全脚本化。把检测结果里的 `gpuGuide` 清单展示给用户，
指导其在 NVIDIA 控制面板 / AMD Adrenalin / Intel Arc 控制面板中手动设置（约 2 分钟）。
清单已按检测到的显卡厂商生成（先确认 `hardware.gpuVendor` 再给对应内容）。

### 自动寻找最佳配置（A/B 调优）

同一台设备、固定场景下，用数据决定哪些优化组合值得保留：

```
# 1) 先采集基线（3 次采样，需游戏在运行、场景固定）
powershell -NoProfile -ExecutionPolicy Bypass -File "<root>\tuning-experiment.ps1" -Baseline -Json

# 2) 依次测试候选组（每个组约 3 × 90 秒）
powershell -NoProfile -ExecutionPolicy Bypass -File "<root>\tuning-experiment.ps1" -Test -Group group-1 -Json
powershell -NoProfile -ExecutionPolicy Bypass -File "<root>\tuning-experiment.ps1" -Test -Group group-2 -Json
powershell -NoProfile -ExecutionPolicy Bypass -File "<root>\tuning-experiment.ps1" -Test -Group group-3 -Json

# 3) 查看实验结果与 CSV 导出
powershell -NoProfile -ExecutionPolicy Bypass -File "<root>\tuning-experiment.ps1" -Report -Json
```

规则版决策（非统计推断）：按平均帧率 / 1% low / P99 帧时间 / 卡顿次数对比基线，
有收益保留、无收益**自动还原**。样本不足、游戏退出、失去前台或基线不稳定时不形成结论。

Agent 的职责与红线：

- 向用户解释：采样期间必须**保持同一地图、画质、分辨率与路线**，不要切窗口、改设置、
  开其他占资源的程序；每轮采样脚本会阻塞约 90 秒，不要打断。
- 采样器依赖官方 PresentMon。本机没有时**引导用户自行安装**
  （`winget install Intel.PresentMon.Console` 或官网下载），不要代替用户安装。
- 不要替实验挑选候选组之外的项，**绝不要把显卡伪装等高风险/需重启项加入候选**。
- 基线不稳定（CV > 0.05）时让用户重采，不要强行继续。

### 还原（用户后悔时）

```
# 查看可精确还原的项
powershell -NoProfile -ExecutionPolicy Bypass -File "<root>\fps-tune.ps1" -ListRestoreItems -Json

# 还原全部
powershell -NoProfile -ExecutionPolicy Bypass -File "<root>\fps-tune.ps1" -Restore -Json

# 只还原某些项
powershell -NoProfile -ExecutionPolicy Bypass -File "<root>\fps-tune.ps1" -Restore -Items hags,dvr-off -Json
```

- 还原按备份记录逐项恢复原值（包括"原本不存在"的值会删除而不是写默认值）。
- 已消费的备份会重命名为 `.restored` 保留供审计，不会误删。
- 还原失败会如实报错并指出哪一项，不要假装成功。

## 优化项一览（id 供 -Items 使用）

| id | 作用 | 管理员 | 默认勾选 | 需重启 |
|---|---|---|---|---|
| power-ultimate | 切到卓越性能电源计划（无可用方案时自建一份） | 是 | 是 | 否 |
| power-tuning | 电源隐藏项调优：USB3 省电关、性能提升 Aggressive、空闲降频关 | 是 | 是 | 是 |
| hags | 开启硬件加速 GPU 计划（HwSchMode=2） | 是 | 是 | 是 |
| game-mode | 开启 Windows 游戏模式 | 否 | 是 | 否 |
| dvr-off | 关闭 Xbox 后台录制 | 否 | 是 | 否 |
| prio-separation | 前台调度权重 0x28 | 是 | 是 | 否 |
| wer-off | 关闭 Windows 错误报告 | 是 | 是 | 否 |
| transparency-off | 关闭窗口透明特效 | 否 | 是 | 否 |
| fso-off | 禁用游戏全屏优化（需游戏路径） | 否 | 是 | 否 |
| gpu-pref | 游戏强制高性能 GPU（需游戏路径，双显卡关键） | 否 | 是 | 否 |
| mpo-off | 禁用 MPO 多平面叠加（治闪烁） | 是 | 是 | 是 |
| net-throttling-off | 解除多媒体网络限流 | 是 | 是 | 否 |
| sys-responsiveness | 系统后台响应保留降到 10 | 是 | 是 | 否 |
| mmcss-games | MMCSS 游戏任务档位拉满 | 是 | 是 | 否 |
| game-priority | 游戏进程 CPU 高优先级（IFEO，需游戏路径） | 是 | 是 | 否 |
| paging-exec | 内核代码常驻内存 | 是 | 是 | 是 |
| sysmain-off | 禁用 SysMain 预取（默认不勾） | 是 | 否 | 是 |
| wsearch-off | 禁用 Windows Search 索引（默认不勾，搜索变慢） | 是 | 否 | 是 |
| hibernate-off | 关休眠+快速启动（默认不勾，台式机可勾） | 是 | 否 | 是 |
| mem-compress-off | 关闭内存压缩（默认不勾，仅供手动对比） | 是 | 否 | 是 |
| gpu-pstate-lock | 禁止显卡动态降频（默认不勾，待机功耗升） | 是 | 否 | 否 |
| dyntick-off | 禁用动态计时器（bcdedit，默认不勾） | 是 | 否 | 是 |

本工具**没有**以下高风险项，也永远不做：显卡型号伪装、修改游戏目录文件、
注入游戏进程、关闭引导虚拟化、任何与反作弊的交互。

## 红线（任何 agent 都必须遵守）

1. **agent 驱动 CLI 不代表用户同意任何声明。** 免责声明、副作用说明必须由用户
   本人阅读并同意；不得代替用户确认，也不得通过任何方式绕过确认。
2. 未经用户明确同意，不得执行 `-Apply` / `-Restore`。
3. 不要绕过脚本自己改注册表/电源设置——脚本的备份机制是唯一的还原保障。
4. 不修改游戏安装目录内的任何文件，不注入进程，不与反作弊交互。
5. 执行后如实汇报每项成功/失败/跳过，并告知备份文件位置与还原方法。
6. 不要代替用户下载或运行任何安装包；需要装 VC++ 运行库时只给官方链接让用户自己装。
7. 不承诺固定帧数提升；收益因机器而异，让用户实测对比。
