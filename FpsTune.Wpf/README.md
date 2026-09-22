# FPS 帧律 · FpsTune WPF

最终公开发布版 GUI。优化引擎全部为 C# 实现（Core/ 目录），
`FpsTune.exe` 带命令行参数时进入无头 CLI 模式（`-Detect` / `-Apply` / `-Restore` / `-ListRestore` / `-Version`）。

## 当前结构

- 左侧导航 + 右侧内容区
- 检测 / 优化 / A/B 实验 / 朋友测试 / 备份日志
- 暗色 / 亮色 / 跟随系统
- 外部脚本仅剩一枚：`tools/friend-test.ps1`（朋友测试记录），作为嵌入资源随包分发；
  A/B 实验编排已迁入进程内 `ExperimentRunner`（CLI 动词 `-Experiment`）

## 构建

需要安装 .NET 10 SDK：

```powershell
dotnet build -c Release
```

## 发布单文件 EXE

（构建产物；**公开 Release 不提供单文件**，只发安装包 + 便携 zip + SHA256 清单，见根 `RELEASE.md` §4 与 README 下载表。）

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true /p:DebugType=none
```

## 绿色文件夹

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

推荐直接使用仓库根目录的 `publish-release.ps1`：一键产出单文件 EXE、便携 zip、SHA256 清单并打印各产物体积。
