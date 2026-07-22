; ====================================================================
; TrayNexus Inno Setup 安装脚本
; 相对路径基于本脚本所在目录（installer/），用 .. 回到仓库根
; 图标统一：安装包 / 卸载程序 / 桌面快捷方式 / 开始菜单卸载项 均使用 tray_default.ico
; ====================================================================

[Setup]
AppName=TrayNexus
AppVersion=1.0722.1
AppVerName=TrayNexus 1.0722.1
AppPublisher=Aiyow
AppCopyright=Copyright © 2026 Aiyow
DefaultDirName={autopf}\TrayNexus
DefaultGroupName=TrayNexus
OutputDir=..\bin
OutputBaseFilename=Traynexus-Setup-1.0722.1
; 安装包图标（同时也是卸载程序 unins000.exe 的图标来源）
SetupIconFile=..\resources\tray_default.ico
; 控制面板「程序和功能」里显示的卸载图标，与安装包一致
UninstallDisplayIcon={app}\Traynexus.exe
Compression=lzma2
SolidCompression=yes
; 内存清理需要管理员权限
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
ChangesAssociations=no
CreateAppDir=yes

[Files]
Source: "..\bin\Traynexus.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; 开始菜单程序组
Name: "{autoprograms}\TrayNexus"; Filename: "{app}\Traynexus.exe"; IconFilename: "{app}\Traynexus.exe"
; 桌面快捷方式（图标来自应用 exe，与安装包同源）
Name: "{autodesktop}\TrayNexus"; Filename: "{app}\Traynexus.exe"; IconFilename: "{app}\Traynexus.exe"; Tasks: desktopicon
; 开始菜单卸载项（图标与安装包一致）
Name: "{group}\Uninstall TrayNexus"; Filename: "{uninstallexe}"; IconFilename: "{app}\Traynexus.exe"

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Run]
; 使用 shellexec 让 ShellExecuteEx 处理应用清单里的 requireAdministrator，避免 CreateProcess code 740
Filename: "{app}\Traynexus.exe"; Description: "Launch TrayNexus"; Flags: nowait postinstall skipifsilent shellexec runascurrentuser
