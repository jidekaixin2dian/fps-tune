# Release Guide

## 1. Build
```powershell
.\build-wpf.ps1 -Mode Build
```

## 2. Publish
```powershell
.\publish-release.ps1
```

Output:
- `dist\single-file\DeltaForceTune.exe`
- `dist\folder\...`

## 3. Installer
Requires Inno Setup 6:
```powershell
.\build-installer.ps1
```

Output:
- `dist\installer\DeltaForceTune-Setup-1.0.0.exe`

## 4. GitHub Release
1. Create a new GitHub Release with tag `v1.0.0`
2. Upload:
   - `dist\installer\DeltaForceTune-Setup-1.0.0.exe`
   - `dist\single-file\DeltaForceTune.exe`
   - `dist\folder\DeltaForceTune.zip`
3. Auto-update in the app will use GitHub latest release API.
