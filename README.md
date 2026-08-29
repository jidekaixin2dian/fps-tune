# FPS 帧律 · fps-tune

面向 Windows 玩家的**系统层**帧率优化工具，核心采用 C# WPF + .NET 8，
同时保留 PowerShell 脚本作为 CLI / AI Agent 入口。

它只调整 Windows 系统设置（注册表 / 电源计划 / 服务 / 启动配置），
不针对特定游戏——因此不仅适用于《三角洲行动》，也适用于
《CS2》《无畏契约》《APEX》《PUBG》《使命召唤》《守望先锋》等绝大多数 FPS 游戏。

能力闭环：**检测 → 解释 → 确认 → 执行 → 还原**。

- **系统层，可还原**：只改 Windows 设置，每次写入前自动备份原值（含"原本不存在"状态），支持一键还原。
- **不碰游戏**：不修改游戏目录文件、不注入进程、不与反作弊交互、不关引导虚拟化、不做显卡伪装。
- **全游戏自动定位**：内置主流 FPS 的进程与卸载表检测；也可用 `-GamePath` 手动指定任意游戏 EXE。
- **单一数据源**：29 个优化项与 3 个预设统一定义在 `catalog/catalog.json`，
  C# GUI 与 PowerShell 引擎运行时加载同一份文件，任一侧漂移会启动即报错。
- **版本单源化**：程序集版本 / 安装器版本 / CLI 自报版本全部来自 `Directory.Build.props` 的 `<Version>`。
- **MIT 开源**：代码完全原创（Clean-room），无遥测。

![build](https://github.com/jiaxindeyang-a11y/fps-tune/actions/workflows/build.yml/badge.svg)
![release](https://img.shields.io/github/v/release/jiaxindeyang-a11y/fps-tune)
![license](https://img.shields.io/github/license/jiaxindeyang-a11y/fps-tune)

> 项目背景：市面上的同类工具采用专有 EULA，禁止修改与再分发。
> 本项目以公开的功能清单为参考，代码与文档完全自写，宽松许可证开源。

## 快速开始（AI Agent）

```text
先执行 git clone https://github.com/jiaxindeyang-a11y/fps-tune.git，
然后读取克隆目录里的 SKILL.md，严格按其中的流程帮我优化帧率。
```

SKILL.md 是唯一的操作流程入口，不要凭空发挥。

## 快速开始（命令行）

```powershell
# 1. 检测（只读，安全）
powershell -NoProfile -ExecutionPolicy Bypass -File fps-tune.ps1 -Detect -Json

# 2. 应用（先向用户说明并征得同意，再加 -Force）
powershell -NoProfile -ExecutionPolicy Bypass -File fps-tune.ps1 -Apply -Preset balanced -Force -Json

# 3. 还原
powershell -NoProfile -ExecutionPolicy Bypass -File fps-tune.ps1 -Restore -Json
```

## 快速开始（GUI）

- 安装包：从 [Releases](https://github.com/jiaxindeyang-a11y/fps-tune/releases) 下载 `FpsTune-Setup-*.exe`
- 便携版：解压 `FpsTune-Portable.zip` 后运行 `FpsTune.exe`

本地构建：

```powershell
.\build-wpf.ps1 -Mode Build
.\FpsTune.Wpf\bin\Release\net8.0-windows\FpsTune.exe
et8.0-windows\FpsTune.exe
```

GUI 已完成：检测、优化、A/B 实验、朋友测试、备份/日志、设置、
深色/亮色/跟随系统主题、自定义无边框窗口与标题栏、现代化弹窗、首次启动引导。

## 架构

```
catalog/catalog.json          优化项与预设的唯一数据源
fps-tune.ps1                  PowerShell 引擎（CLI / Agent）
tuning-experiment.ps1         A/B 实验框架（模拟态可安全跑 CI）
FpsTune.Wpf/
  Core/                       OptimizationCatalog / NativeOptimizationEngine /
                              BackupService / DetectionService 等
  Services/                   主题 / 设置 / 脚本定位 / 进程封装 / 迁移
  Views/                      WPF 视图
FpsTune.Wpf.Tests/            单元测试（含 catalog 一致性守卫）
installer/                    Inno Setup 打包
legacy/                       已退役的旧 PowerShell GUI，仅存档
```

新增或修改一个优化项的流程：编辑 catalog/catalog.json 中对应条目后，
两台引擎分别实现 apply/revert 与 C# case 分支——一致性测试会强制两边同步，
漏掉任何一侧 CI 直接失败。

## 测试

```powershell
dotnet test FpsTune.Wpf.Tests/FpsTune.Wpf.Tests.csproj
# 以及无副作用的 PS 冒烟（CI 已集成）
powershell -File fps-tune.ps1 -Detect -Json
```

## 许可

MIT，详见 LICENSE。
