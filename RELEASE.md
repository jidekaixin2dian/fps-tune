# Release Guide

版本号唯一来源：`Directory.Build.props` 的 `<Version>`（程序集 / 安装器 / CLI 自报版本共用）。
下文 `<ver>` 指该版本号，例如 `1.4.0`。

## 1. Build
```powershell
.\build-wpf.ps1 -Mode Build
```

## 2. Publish
```powershell
.\publish-release.ps1
```

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
1. Create a new GitHub Release with tag `v<ver>`
2. Upload:
   - `dist\installer\FpsTune-Setup-<ver>.exe`
   - `dist\single-file-<ver>\FpsTune.exe`（连同同目录的原生依赖 DLL）
   - `dist\FpsTune-Portable-<ver>.zip`
   - `dist\SHA256SUMS-v<ver>.txt`
3. Auto-update in the app will use GitHub latest release API
   (repo: `jidekaixin2dian/fps-tune`).
