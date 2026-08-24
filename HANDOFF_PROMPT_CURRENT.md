# 新对话交接提示词（请完整复制）

你是一个资深软件工程师，负责继续开发 delta-force-tune「三角洲帧律」。

## 项目背景

- 产品：面向《三角洲行动》的 Windows 系统层帧率优化工具。
- 目标平台：Windows 10/11。
- 当前技术栈：
  - WPF + .NET 8
  - 封面/UI 已初步现代化
  - 核心功能已基本迁移到 C#（应用/检测/备份/还原不再回退 PowerShell）
  - 保留 PowerShell 引擎仅用于 A/B 实验、朋友测试和兼容排查
- 最终目标：成为可公开发布、安装包化、可自动更新、功能完整的桌面软件。

## 本地路径

- 主仓库：`C:\Users\Aether\Documents\dsh\work\delta-skill\`
- WPF 项目：`C:\Users\Aether\Documents\dsh\work\delta-skill\DeltaForceTune.Wpf\`
- 安装包脚本：`installer\setup.iss`
- 发布脚本：`publish-release.ps1`、`build-installer.ps1`
- 同步目录：`C:\Users\Aether\.dsh\wsl-output\`

## 当前版本

- 1.0.0（上线前仍以 1.0.0 为准）

## 当前已完成

- WPF 界面完整框架：首页、检测、优化、A/B 实验、朋友测试、备份/日志、设置
- 左侧导航 + 右侧内容区
- 深色/亮色/跟随系统主题，带淡入淡出动画
- 首页：产品图标、名称、功能模块、联系方式、版本号、硬件摘要
- 微信二维码弹窗，QQ/抖音个人主页链接
- 设置页：默认主题、关于、合规说明、管理员状态、检查更新，不展示个人联系方式编辑
- 线性可拖动滚动条
- 功能卡片 hover 动效，页面滑动动画
- 自定义 AppDialogWindow 替代系统 MessageBox（还原确认/提示/更新确认等）
- 检测/优化列表显示副作用（无副作用自动隐藏）
- C# 游戏路径自动查找（进程/卸载注册表/常见目录）
- C# 核心：
  - 22 项优化项定义
  - NativeOptimizationEngine：全部 22 项 C# 原生应用，覆盖注册表/电源/服务/休眠/BCD/GPU/Layers/DirectX
  - BackupService：支持注册表、服务启动类型、电源计划、休眠、BCD、动态 GPU 键等完整备份/还原
  - DetectionService：22 项检测均走 C#（含电源隐藏项、服务、休眠、BCD、GPU 状态）
  - HardwareInfoService（硬件信息：CPU/GPU/内存/内存频率/PCIe/笔记本/管理员）
  - OptimizationEngine 统一入口
- 检测/优化应用/备份/还原/备份列表已全部走 C# 引擎
- 还原不再回退 PowerShell
- 自动更新：GitHub Releases 检查
- 安装包：Inno Setup 脚本（单文件 EXE）
- 发布脚本：publish-release.ps1 增加绿色版 Portable zip；build-installer.ps1 增加产物检查
- 发布指南：RELEASE.md

## 尚未完成 / 必须继续

1. 待用户实测/验收 C# 引擎：
   - 在真实 Windows 上跑一次 Detect / Apply / Restore 往返
   - 重点验证 power-tuning、sysmain-off、wsearch-off、hibernate-off、gpu-pstate-lock 的还原
2. 安装包实际构建验证：
   - 需要安装 Inno Setup 6
   - 将 `dist\single-file\DeltaForceTune.exe` 打入安装包
3. 自动更新端到端验证：
   - 创建 GitHub Release
   - 上传安装包/单文件 EXE/Portable zip
   - 确认 UpdateService 能读取最新版本
4. UI 进一步现代化：
   - 自定义弹窗、副作用展示已加入；后续视觉微调可继续交给 vision 版本
   - 继续优化视觉，但不得破坏功能
5. 用户视角完整自检：
   - 所有页面无大面积留白
   - 深色/亮色下无不可读内容
   - 滚动条可用
   - 设置页不暴露开发者联系方式编辑
6. 最终提交 GitHub：
   - 所有任务完成且成熟后才 `git push origin main`
   - 同时发布 GitHub Release

## 用户红线 / 要求

- 不要修改核心引擎的合规边界：
  - 不修改游戏文件
  - 不注入进程
  - 不与反作弊交互
  - 不关闭引导虚拟化
  - 不做显卡伪装
- 不要停止太频繁；一个大版本尽量做完再让用户验收。
- 用户不想在设置页看到开发者社交账号编辑。
- 微信没有公开主页链接，使用二维码弹窗。
- QQ：`https://qm.qq.com/q/jwbacnxrmU`
- 抖音：`https://v.douyin.com/sf-DA6eLcjQ/`
- 微信 QR：`Assets\wechat-qr.jpg`
- 联系方式仅接受合作/反馈。
- 公开发布后再考虑“打赏开发者”。

## 常用命令

```powershell
# 构建
cd C:\Users\Aether\Documents\dsh\work\delta-skill
.\build-wpf.ps1 -Mode Build

# 运行
.\DeltaForceTune.Wpf\bin\Release\net8.0-windows\DeltaForceTune.exe

# 发布
.\publish-release.ps1

# 安装包
.\build-installer.ps1
```

## Git 状态

- 项目已有大量本地提交。
- 远程尚未推送最新大版本。
- 最终成熟后再统一提交 GitHub。
