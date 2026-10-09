; fps-tune Inno Setup installer
#define MyAppName "FPS 帧律"
#ifndef MyAppVersion
  #error 请通过 build-installer.ps1 构建，由其注入 /DMyAppVersion（版本唯一来源：Directory.Build.props）
#endif
#define MyAppPublisher "FPS 帧律"
#define MyAppExeName "FpsTune.exe"

[Setup]
AppId={{8D6E7F3A-4C5B-4D1E-9A2B-7C0F6E1D8B4A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
LicenseFile=..\LICENSE
DefaultDirName={autopf}\FpsTune
UsePreviousAppDir=yes
DisableDirPage=no
AlwaysShowDirOnReadyPage=yes
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist\installer
OutputBaseFilename=FpsTune-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardResizable=yes
WizardSizePercent=115
DisableWelcomePage=no
SetupIconFile=..\FpsTune.Wpf\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardImageFile=..\dist\installer-art\wizard.bmp
WizardSmallImageFile=..\dist\installer-art\logo.bmp
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
; Pack the verified executable and complete legal texts; no external driver DLLs.
Source: "..\dist\single-file-{#MyAppVersion}\*"; Excludes: "*.pdb,Directory.Build.props"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Messages]
WelcomeLabel1=安装 FPS 帧律
WelcomeLabel2=系统调校与性能测量，从检测到实测。%n%n接下来选择安装位置和快捷方式，然后安装 FPS 帧律。
