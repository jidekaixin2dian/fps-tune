# 参与贡献

感谢你想动这个项目。这里是最短路径。

## 环境

- Windows 10 / 11 x64
- .NET 10 SDK（`winget install Microsoft.DotNet.SDK.10`）

```powershell
git clone https://github.com/jidekaixin2dian/fps-tune.git
cd fps-tune
dotnet build FpsTune.Wpf/FpsTune.Wpf.csproj -c Release
dotnet test FpsTune.Wpf.Tests/FpsTune.Wpf.Tests.csproj -c Release
```

跑起来：`.\FpsTune.Wpf\bin\Release\net10.0-windows\FpsTune.exe`

## 开发时唯一要小心的地方

这个程序会真的改系统设置。开发与验证请遵守：

- 验证检测/界面用只读路径（`-Detect`、GUI 的「检测」页），它们不写任何东西。
- 需要验证 apply/revert 时，优先用 `-Experiment -Simulate` 或在虚拟机 / 测试账户里做。
- 不要为了测试方便去掉写前备份。备份与精确还原是这个项目存在的理由。

## 改动规则

产品红线（不可协商，详见 `docs/ROADMAP.md`）：

1. 所有系统改动写前备份原值（含"原本不存在"状态），可逐项/一键还原，已达标项跳过不写。
2. 不修改游戏文件、不注入进程、不读游戏内存、不与反作弊交互、不伪装硬件、不关引导虚拟化。
3. 每项优化的价值主张必须可被 A/B 实验验证；解释不清收益的项不进 catalog。
4. 优化项是系统层的，不绑定特定游戏；游戏相关的只做路径级适配。
5. 所有改动留 JSON 审计记录；默认无遥测。

新增或修改一个优化项：

1. 编辑 `catalog/catalog.json`，补 `id` / `name` / `description` / `sideEffect` / `admin` / `reboot` / `kind` / `group`。
2. 在 `FpsTune.Wpf/Core/NativeOptimizationEngine.cs` 加 apply/revert 分支。
3. 在 `FpsTune.Wpf/Core/DetectionService.cs` 加状态读取分支。
4. 在 `FpsTune.Wpf/Core/BackupService.Capture.cs` 声明要备份哪些键/值。
5. 跑 `dotnet test`——catalog 一致性守卫会强制上面几步同步，漏一步就红。

## 提 PR 之前

- 一个 PR 只做一件事。界面重排和引擎修复分开。
- 描述里写清"改了什么 / 为什么 / 怎么验的"，贴命令或截图。
- 涉及界面：说明深色、浅色、低配模式下分别看过。
- 涉及系统设置：说明在什么硬件/系统版本上验证过，以及还原是否精确回到原值。
- 不要顺手加依赖。加依赖需要在 PR 里单独说明理由。

## 好的起点

`good first issue` 标签下的任务通常不需要读完整引擎。当前最缺的一块是界面文案的
国际化（现在全部硬编码中文），如果你想做，先开一个 issue 说明思路，避免白干。
