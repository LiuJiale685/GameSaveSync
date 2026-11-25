; GameSaveSync 安装包脚本（Inno Setup 6）| By Jiale
; 用法：先执行 installer\build-installer.ps1（会自动发布并调用本脚本），
; 或手动：ISCC.exe installer\GameSaveSync.iss

#define AppName "GameSaveSync"
#define AppVersion "1.0.0"
#define AppPublisher "Jiale"
#define AppCopyright "Copyright (c) 2026 Jiale. All rights reserved."
#define AppExeName "GameSaveSync.exe"
; 发布产物目录（由 build-installer.ps1 生成，相对本脚本）
#define PublishDir "..\tmp\publish"
; 应用图标（与主程序 exe 图标同源，位于 Desktop 项目的 Assets 目录）
#define AppIconFile "..\src\GameSaveSync.Desktop\Assets\GameSaveSync.ico"

[Setup]
AppId={{8A3F6C21-5B4D-4E97-9C1A-7D2E4F8B0A63}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright={#AppCopyright}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}
VersionInfoCopyright={#AppCopyright}
; 按用户安装，无需管理员权限（数据写 %LOCALAPPDATA%）
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
OutputDir=..\dist
OutputBaseFilename={#AppName}-{#AppVersion}-win-x64-setup
; 安装包与卸载程序图标统一使用应用图标
SetupIconFile={#AppIconFile}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 安装前请用户关闭正在运行的实例
CloseApplications=yes
RestartApplications=no
MinVersion=10.0

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 多文件自包含发布：包含主程序及全部依赖 dll / 原生库
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 安装目录内的运行时残留（正常情况下程序数据在 %LOCALAPPDATA%，此处仅清理安装目录）
Type: filesandordirs; Name: "{app}"

[Registry]
; 开机自启项由程序内“设置 → 开机自动启动”写入（HKCU Run），
; 安装器本身不创建；卸载时清除残留值，避免开机启动已删除的程序。
; 该值不存在时卸载删除为空操作，不会报错。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: none; ValueName: "GameSaveSync"; Flags: uninsdeletevalue
