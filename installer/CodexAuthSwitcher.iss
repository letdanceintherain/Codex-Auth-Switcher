#define AppIdName "CodexAuthSwitcher"
#define AppDisplayName "CodexAuthSwitcher - Codex 认证切换器"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif
#define AppExeName "CodexAuthSwitcher.exe"

[Setup]
AppId={{8B72F019-50AE-4D6E-9969-9A6F3DA6E20A}}
AppName={#AppDisplayName}
AppVersion={#AppVersion}
AppVerName={#AppDisplayName} {#AppVersion}
DefaultDirName={localappdata}\Programs\{#AppIdName}
DefaultGroupName={#AppDisplayName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename={#AppIdName}-Setup-{#AppVersion}
UninstallDisplayIcon={app}\{#AppExeName}
AppComments=Switch Codex accounts and keep conversations connected / 切换 Codex 账号并自动衔接历史对话
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UsePreviousAppDir=no
CloseApplications=yes
SetupIconFile=..\src\CodexAuthSwitcher.App\Assets\AppIcon.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
SetupWindowTitle=Setup - %1 / 安装 - %1
WelcomeLabel1=Welcome to the [name] Setup Wizard / 欢迎使用 [name] 安装向导
WelcomeLabel2=This will install [name/ver] on your computer.%n%nIt is recommended that you close all other applications before continuing. / 即将把 [name/ver] 安装到你的电脑上。%n%n建议继续之前先关闭其他应用程序。
SelectDirLabel3=Setup will install [name] into the following folder. / 安装程序将把 [name] 安装到以下文件夹。
SelectTasksLabel2=Select the additional tasks you would like Setup to perform while installing [name], then click Next. / 请选择安装 [name] 时希望执行的附加任务，然后点击“下一步”。
FinishedHeadingLabel=Completing the [name] Setup Wizard / 正在完成 [name] 安装向导
FinishedLabelNoIcons=Setup has finished installing [name] on your computer. / 安装程序已完成 [name] 的安装。
FinishedLabel=Setup has finished installing [name] on your computer. The application may be launched by selecting the installed shortcuts. / 安装程序已完成 [name] 的安装。你可以通过已创建的快捷方式启动程序。
InstallingLabel=Please wait while Setup installs [name] on your computer. / 安装程序正在安装 [name]，请稍候。

[CustomMessages]
DesktopIcon=Create a &desktop icon / 创建桌面图标
AdditionalIcons=Additional icons / 附加图标
LaunchApp=Launch {#AppDisplayName} / 启动 {#AppDisplayName}

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppDisplayName}"; Filename: "{app}\{#AppExeName}"
Name: "{commondesktop}\{#AppDisplayName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent
