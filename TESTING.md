# 朋友测试指南（简易数据也欢迎）

## 测试前必读

- 只做系统层优化，不碰游戏文件、不注入、不关虚拟化、不做显卡伪装。
- 所有改动写前备份，可还原：`FpsTune.exe -Restore -Json`
- 不要替朋友下载/运行安装包；PresentMon 等工具请他们自己装。
- 游戏内画质、分辨率、DLSS/FSR、场景、路线、时长在“优化前/后”必须保持一致。

## 记录数据

> 2026-09-25：原先的「朋友测试」页与 `tools\friend-test.ps1` 已整体移除
> （用户决定不再保留这个模块）。下面直接用**回传数据模板**手工记录即可，
> 不再需要跑任何脚本。

1. 这个流程只生成记录，**不执行任何优化或还原**。
2. 顺序不能反：先测“优化前”，再运行 `-Apply`，再测“优化后”；测完“优化后”再决定是否 `-Restore`。
3. 前后必须保持同一场景、画质、分辨率、DLSS/FSR、路线和时长；游戏保持前台，不要切窗口、不要开其他占资源的程序。
4. 没有 1% low 数据就留空；有“游戏加加”或游戏内 FPS 面板时，优先记录 1% low 或帧时间图。
5. 如果机器改过显卡型号，请在备注里写明真实型号。
6. 回传数据即可（截图或按下面模板填的表格都行）。

## 方式 A：完整 A/B 自动采样（有官方 PresentMon 时）

1. 自己安装官方 PresentMon：`winget install Intel.PresentMon.Console`
2. 进入固定场景（推荐靶场），运行：

   ```powershell
   FpsTune.exe -Experiment -Baseline -Json
   FpsTune.exe -Experiment -Test -Group group-1 -Json
   FpsTune.exe -Experiment -Test -Group group-2 -Json
   FpsTune.exe -Experiment -Test -Group group-3 -Json
   FpsTune.exe -Experiment -Report -Json
   ```

3. 把 `%LocalAppData%\FpsTune\experiment\experiment-summary.csv` 和
   `%LocalAppData%\FpsTune\experiment\state.json` 发回来即可。

## 方式 B：只有帧率 / 游戏加加数据（没有 PresentMon 也可以）

1. 固定场景、画质、分辨率、DLSS/FSR、路线和时长（建议 5 分钟以上）。
2. 先测“优化前”：用游戏加加 / 游戏内 FPS 面板记录平均帧率、1% low 或帧时间曲线，截图。
3. 运行优化（先让朋友确认副作用）：

   ```powershell
   FpsTune.exe -Apply -Preset balanced -Json
   ```

4. 再测“优化后”：同场景同设置再录一次，截图。
5. 如果要还原：

   ```powershell
   FpsTune.exe -Restore -Json
   ```

## 回传数据模板

| 项目 | 内容 |
|---|---|
| CPU / GPU / 内存 / 系统 | 例：i9-13900HX / RTX 5070 Ti Laptop GPU / 16GB / Win11 |
| 游戏路径 | 例：D:\Delta Force\Delta Force\DeltaForce\Binaries\Win64\DeltaForceClient-Win64-Shipping.exe |
| 场景与画质 | 例：靶场，2K 全高，DLSS 质量 |
| 优化前平均 FPS / 1% low |  |
| 优化后平均 FPS / 1% low |  |
| 数据截图 / CSV |  |
| 备注（卡顿体感、是否还原等） |  |

> 只有“平均 FPS”也有参考价值；有 1% low 或帧时间图更好。没有完整数据也欢迎，注明测试条件即可。
