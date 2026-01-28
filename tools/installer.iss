[Setup]
AppName=调试资料汇总平台
AppVersion=1.0.3
AppPublisher=马文水
VersionInfoCompany=马文水
VersionInfoProductName=调试资料汇总平台
VersionInfoProductVersion=1.0.3
VersionInfoVersion=1.0.3.0
DefaultDirName={localappdata}\Programs\DebugSummaryPlatform
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename=DebugSummaryPlatform_Setup_win-x64

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\调试资料汇总平台"; Filename: "{app}\FieldKb.Client.Wpf.exe"
Name: "{autodesktop}\调试资料汇总平台"; Filename: "{app}\FieldKb.Client.Wpf.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked
