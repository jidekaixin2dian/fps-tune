# Release Guide

版本号唯一来源：`Directory.Build.props` 的 `<VersionPrefix>`（三段数字，程序集 / 安装器 /
CLI 自报版本共用）+ `<VersionSuffix>`（预发布标识，只进 `InformationalVersion` / `ProductVersion` 展示）。
下文 `<ver>` 指 `VersionPrefix`。当前已发布：`v0.1.1-beta`；正在发布：`v0.1.2-beta`（M1 DLSS/数字振动 + M2 ICC）。
> 历史注记：1.x 版本线（v1.6.2 及之前）已在 `170a8bd` 重置为 0.1 Beta 线，旧版本号只出现在历史与 `docs/` 存档里。

发布必须从最终提交开始。`publish-release.ps1` 读取干净工作树的
`git rev-parse HEAD`，把完整 40 位 `finalSha` 嵌入程序集
`InformationalVersion`，并用本机 `FileVersionInfo.ProductVersion` 验证单文件、绿色目录和
便携包内的 EXE 同时包含版本号与完整 SHA。提交前构建不能作为正式发布资产。

行尾约定：仓库统一存 LF（`.gitattributes` 强制所有文本文件 `eol=lf`）。
若 `git diff --stat` 出现大量"增删完全对称"的文件，说明行尾被工具翻转成了 CRLF，
**不要提交**——用 `git checkout -- <file>` 丢弃或让编辑器保存为 LF 后再继续，
否则会把整个仓库历史翻成 CRLF 并卡死发布闸门。

## 1. Build
```powershell
.\build-wpf.ps1 -Mode Build
```

## 2. Publish
```powershell
.\publish-release.ps1
```

脚本还会验证单文件目录只有 `FpsTune.exe`，从仅含该 EXE 的临时目录执行
`-Version` 与 `-Detect -Json`，并生成只包含单文件和便携包的 SHA256 清单。
**单文件仅作本机验证用，不作为 GitHub Release 资产**（与 README 下载表一致：
公开只发安装包 + 便携 zip + SHA256 清单）。

Output:
- `dist\single-file-<ver>\FpsTune.exe`（压缩单文件，约 66 MB；**验证用，不上传**）
- `dist\folder-<ver>\...`（绿色文件夹）
- `dist\FpsTune-Portable-<ver>.zip`
- `dist\SHA256SUMS-v<ver>.txt`

脚本结束会打印各产物体积。

## 3. Installer
Requires Inno Setup 6:
```powershell
.\build-installer.ps1 -CheckOnly
.\build-installer.ps1
```

编译器按显式 `-IsccPath` / `ISCC_PATH`，或 PATH、Program Files、
`$env:LOCALAPPDATA\Programs\Inno Setup 6` 查找。非默认安装可指定：
```powershell
.\build-installer.ps1 -CheckOnly -IsccPath 'D:\Tools\Inno Setup 6\ISCC.exe'
```
`-CheckOnly` 会实际启动编译器，不生成安装包。文件存在但启动被拒绝时，
应检查当前执行账户、沙箱和 Windows 权限，并通过工具的权限提升流程重试；
不能据此断言“未安装”，也不应直接重装。查找失败会列出检查路径。

Output:
- `dist\installer\FpsTune-Setup-<ver>.exe`

## 4. GitHub Release
1. 先运行 `build-installer.ps1`；它会用当前单文件和便携包重写清单并加入安装器 SHA256。
2. 只有所有本机验证通过后，创建与 `finalSha` 精确对应的 GitHub Release，tag 为 `v<ver>`。
3. Upload（**三个资产，与 README / README.en 下载表一致**）:
   - `dist\installer\FpsTune-Setup-<ver>.exe`
   - `dist\FpsTune-Portable-<ver>.zip`
   - `dist\SHA256SUMS-v<ver>.txt`
   不上传 `single-file-<ver>\FpsTune.exe`（验证用产物）。
4. 发布前核对三个资产均存在、非空、版本正确，并按清单复核 SHA256；远端 tag/资产也须再次核对。
5. Auto-update in the app will use GitHub latest release API
   (repo: `jidekaixin2dian/fps-tune`).
