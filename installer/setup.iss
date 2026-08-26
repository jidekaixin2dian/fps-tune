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
DefaultDirName={autopf}\FpsTune
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist\installer
OutputBaseFilename=FpsTune-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
; WPF 自包含单文件发布会同时生成原生依赖与 PS 兼容脚本，必须一起打包。
Source: "..\dist\single-file\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent
