---
name: fps-tune
description: Windows 系统设置调校与 FPS 对照测量。使用 FPS 帧律检测硬件、游戏路径和当前设置，解释优化项与副作用，在用户同意的范围内应用、实测或还原。适用于用户请求本工具调校三角洲行动、CS2、VALORANT 等 PC 游戏的场景。
---

# FPS 帧律 · 使用者操作流程

本文件面向通过 `FpsTune.exe` 帮用户调校的 AI 助手。开发项目使用本地开发规则。
使用确定性的 C# 引擎执行检测、应用与还原，并向用户汇报实际结果。

## 前置条件

- Windows 10/11 x64，满足 .NET 10 运行条件。安装包自带运行时；便携包需要 .NET 10 Windows Desktop Runtime。
- 从 [官方 Releases](https://github.com/jidekaixin2dian/fps-tune/releases/latest) 取得程序并核对校验值。当前源码候选为 0.2.4-beta，公开版为 0.2.3-beta；使用前读取 `-Version`。
- `<root>` 是程序目录，本地构建通常位于 `FpsTune.Wpf\bin\Release\net10.0-windows`。
- 带参数进入无头模式，结果输出到 stdout，退出码 0/1。CLI Apply / Restore 执行即生效。
- 项目清单、副作用、权限和预设以引擎 `-Detect -Json` 及 `catalog/catalog.json` 为准，不根据旧文章猜测。

## 检测与审阅

```powershell
& "<root>\FpsTune.exe" -Version
& "<root>\FpsTune.exe" -Detect -Json
& "<root>\FpsTune.exe" -Detect -Game "C:\Games\Game.exe" -Json
```

检测返回 `hardware`、`gamePath`、`items` 与 `checks`。游戏查找使用卸载记录和有界目录扫描；没有识别时使用用户提供的 EXE。
先说明硬件、当前状态、拟改项目及副作用，再按用户已有授权执行。未授权的系统修改需要明确同意；同一范围已获同意后不重复确认。

- 需要管理员时说明并使用用户认可的提权终端，不扩大到整个使用过程。
- `fso-off` / `gpu-pref` / `game-priority` 依赖游戏路径，缺失时跳过。
- `wsearch-off` 影响搜索索引；`hibernate-off` 关闭休眠与快速启动，删除的休眠内容不能恢复。
- 接电 CPU 项、关闭节能项可能增加温度、功耗和风扇噪声；默认未选项不替用户自动勾选。
- 体检中的驱动、内存频率等建议是诊断，不代为安装驱动、改 BIOS 或关闭安全功能。

## 应用与汇报

```powershell
& "<root>\FpsTune.exe" -Apply -Preset balanced -Game "C:\Games\Game.exe" -Json
& "<root>\FpsTune.exe" -Apply -Items game-mode,gpu-pref -Game "C:\Games\Game.exe" -Json
```

`balanced` 为 16 项，`safe-only` 为 4 项无需管理员的设置，`full` 包含全部 38 项；full 包含副作用不同的手动项目，不作为默认推荐。
说明实际选择后执行。结果包含 `operationId`、`backupFile`、逐项 `ok/changed/skipped/stateUncertain` 及重启要求。
写入前按项保存原值和目标身份；不能读取原值时停止该项。失败或状态不确定必须保留记录并如实报告，不把局部成功当成全部完成。

## 对照测量

```powershell
& "<root>\FpsTune.exe" -Experiment -Baseline -Json
& "<root>\FpsTune.exe" -Experiment -Test -Group group-1 -Json
& "<root>\FpsTune.exe" -Experiment -Report -Json
```

固定设备、驱动、游戏、画质、分辨率、地图和路线；真实采样需要已安装且签名/发布者通过检查的官方 PresentMon。
基线和候选至少 3 次有效样本、CV ≤ 0.05。P99 恶化超过 3% 时拒绝保留，其他收益阈值以判定记录为准。规则判定不等于统计显著性或未来场景收益。
需重启项目不加入即时 A/B。发生异常时检查该操作回执，报告回滚失败，不启动另一轮覆盖现场。
演练时每条命令都加 `-Simulate`；模拟状态与历史独立，不当作真实 FPS 收益。

## 恢复

```powershell
& "<root>\FpsTune.exe" -ListRestore -Json
& "<root>\FpsTune.exe" -Restore -Json
& "<root>\FpsTune.exe" -Restore -Items hags,dvr-off -Json
```

恢复本工具记录的原值，原先不存在的值删除；已消费的系统备份保留为 `.restored`。
系统、DRS、ICC、数字振动由恢复协调器分别处理，独立报告失败。
外部修改、设备/驱动变化、旧备份目标不足或异机记录会阻止无法确认的恢复。不要删除未决标记或用猜测的默认值覆盖；先核对具体目标和记录。
导入备份用于保存记录，不自动授权将其他机器或重装前设置写入当前系统。

## 显示与维护功能

GUI 提供 NVIDIA DRS / DLSS、主屏数字振动与 ICC。部分游戏不使用 ICC；驱动设置的效果依游戏支持情况而异。
GTX 1050 Ti 名称实验只改 Windows DeviceDesc，不改变 DXGI 硬件标识或硬件能力，不保证游戏采用该名称。
缓存维护先预览再确认删除，删除不可还原，重建可能暂时卡顿。禁止未经授权清理真实缓存。
本地壁纸仅是软件背景，不修改 Windows 壁纸。

## 交付原则

1. 不绕过引擎直接改注册表、电源、驱动，不修改游戏文件、不注入游戏进程。
2. 按用户授权范围执行，解释实际影响；不把第三方工具或帖子当成收益证明。
3. 以退出码、逐项结果和回执验证完成，保留失败与不确定记录。
4. 不承诺固定帧数、全部可逆或所有反作弊兼容。
5. 手动诊断导出可能包含设备与操作信息，用户检查后再分享，不自动发送。
6. 默认启动访问 GitHub 检查更新，可关闭；下载与安装均由用户操作。遵守项目 MIT 与第三方许可，合法修改和再分发不需要额外许可。
