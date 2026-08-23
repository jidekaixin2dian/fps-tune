# 朋友测试指南（简易数据也欢迎）

## 测试前必读

- 只做系统层优化，不碰游戏文件、不注入、不关虚拟化、不做显卡伪装。
- 所有改动写前备份，可还原：`powershell -NoProfile -ExecutionPolicy Bypass -File delta-optimizer.ps1 -Restore -Json`
- 不要替朋友下载/运行安装包；PresentMon 等工具请他们自己装。
- 游戏内画质、分辨率、DLSS/FSR、场景、路线、时长在“优化前/后”必须保持一致。

## 方式 A：完整 A/B 自动采样（有官方 PresentMon 时）

1. 自己安装官方 PresentMon：`winget install Intel.PresentMon.Console`
2. 进入固定场景（推荐靶场），运行：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File tuning-experiment.ps1 -Baseline -Json
   powershell -NoProfile -ExecutionPolicy Bypass -File tuning-experiment.ps1 -Test -Group group-1 -Json
   powershell -NoProfile -ExecutionPolicy Bypass -File tuning-experiment.ps1 -Test -Group group-2 -Json
   powershell -NoProfile -ExecutionPolicy Bypass -File tuning-experiment.ps1 -Test -Group group-3 -Json
   powershell -NoProfile -ExecutionPolicy Bypass -File tuning-experiment.ps1 -Report -Json
   ```

3. 把 `%LocalAppData%\DeltaOptimizer\experiment\experiment-summary.csv` 和
   `%LocalAppData%\DeltaOptimizer\experiment\state.json` 发回来即可。

## 方式 B：只有帧率 / 游戏加加数据（没有 PresentMon 也可以）

1. 固定场景、画质、分辨率、DLSS/FSR、路线和时长（建议 5 分钟以上）。
2. 先测“优化前”：用游戏加加 / 游戏内 FPS 面板记录平均帧率、1% low 或帧时间曲线，截图。
3. 运行优化（先让朋友确认副作用）：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File delta-optimizer.ps1 -Apply -Preset balanced -Force -Json
   ```

4. 再测“优化后”：同场景同设置再录一次，截图。
5. 如果要还原：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File delta-optimizer.ps1 -Restore -Json
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
