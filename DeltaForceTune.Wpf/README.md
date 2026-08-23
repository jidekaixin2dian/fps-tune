# 三角洲帧律 · DeltaForceTune WPF

最终公开发布版 GUI。

## 当前结构

- 左侧导航 + 右侧内容区
- 检测 / 优化 / A/B 实验 / 朋友测试 / 备份日志
- 暗色 / 亮色 / 跟随系统
- 调用现有的 `delta-optimizer.ps1` / `tuning-experiment.ps1` / `friend-test.ps1`
- PowerShell 版本继续保留，作为命令行和备用入口

## 构建

需要安装 .NET 8 SDK：

```powershell
dotnet build -c Release
```

## 发布单文件 EXE

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

## 绿色文件夹

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

发布后把 `delta-optimizer.ps1`、`tuning-experiment.ps1`、`friend-test.ps1` 和生成的 EXE 放在同一目录即可。
