# Release Guide

版本号唯一来源：`Directory.Build.props` 的 `<Version>`（程序集 / 安装器 / CLI 自报版本共用）。
下文 `<ver>` 指该版本号；当前发布版本为 `1.5.0`。

发布必须从最终提交开始。`publish-release.ps1` 读取干净工作树的
`git rev-parse HEAD`，把完整 40 位 `finalSha` 嵌入程序集
`InformationalVersion`，并用本机 `FileVersionInfo.ProductVersion` 验证单文件、绿色目录和
便携包内的 EXE 同时包含版本号与完整 SHA。提交前构建不能作为正式发布资产。

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

Output:
- `dist\single-file-<ver>\FpsTune.exe`（压缩单文件，约 66 MB）
- `dist\folder-<ver>\...`（绿色文件夹）
- `dist\FpsTune-Portable-<ver>.zip`
- `dist\SHA256SUMS-v<ver>.txt`

脚本结束会打印各产物体积。

## 3. Installer
Requires Inno Setup 6:
```powershell
.\build-installer.ps1
```

Output:
- `dist\installer\FpsTune-Setup-<ver>.exe`

## 4. GitHub Release
1. 先运行 `build-installer.ps1`；它会用当前单文件和便携包重写清单并加入安装器 SHA256。
2. 只有所有本机验证通过后，创建与 `finalSha` 精确对应的 GitHub Release，tag 为 `v<ver>`。
3. Upload:
   - `dist\installer\FpsTune-Setup-<ver>.exe`
   - `dist\single-file-<ver>\FpsTune.exe`（真正单文件，不上传同目录依赖）
   - `dist\FpsTune-Portable-<ver>.zip`
   - `dist\SHA256SUMS-v<ver>.txt`
4. 发布前核对四个资产均存在、非空、版本正确，并按清单复核 SHA256；远端 tag/资产也须再次核对。
5. Auto-update in the app will use GitHub latest release API
   (repo: `jidekaixin2dian/fps-tune`).
