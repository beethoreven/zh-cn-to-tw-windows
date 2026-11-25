; Inno Setup 安裝腳本，對應 Mac 版的 DMG 打包。
;
; PrivilegesRequired=lowest + DefaultDirName 用 {localappdata}：刻意不裝進
; Program Files，安裝不需要 UAC 提權，一般使用者帳號就能裝完、裝好。這個
; 決定也跟 MainWindow.xaml.cs 把 WebView2 使用者資料夾放在 %LocalAppData%
; 是同一個理由的延伸——整個安裝路徑都不碰需要管理員權限才能寫入的目錄。
;
; Source 只有一行 "staging\app\*"：build_installer.bat 已經先把
; dotnet publish 的自足發布結果、zh-cn-to-tw-web 前端、PyInstaller 打包好
; 的 ocr-service 三份東西集中放進 staging\app\ 底下，這裡單純把整個資料夾
; 原封不動裝進去，跟 MainWindow.xaml.cs 的 ResolveWebSource()/
; OcrServiceManager.ResolveExecutable() 找 web\/ocr-service\ 子資料夾的
; 邏輯一一對應。

; MyAppName（顯示名稱，Start Menu/桌面捷徑/解除安裝清單看到的字）跟
; OutputBaseFilename（安裝檔實際檔名）刻意分開處理：前者用中文
; 「繁化助手」跟 Mac 版 Info.plist 的 CFBundleDisplayName 對齊，後者
; 保持 ASCII「ZhCnToTw」——Mac 版發布 DMG 到 GitHub Release 時實測撞過
; `gh` CLI 會把上傳檔名開頭的 CJK 位元組吃掉（見 zh-cn-to-tw-mac
; README「已知的坑」），Release 資產檔名維持 ASCII 直接避開這個問題，
; 不需要每次發布再手動重新命名一次。
#define MyAppName "繁化助手"
#define MyAppVersion "2.3"
#define MyAppExeName "ZhCnToTw.exe"

[Setup]
AppId={{25D707A1-4FA6-430D-BE9D-0BC19672C4B5}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=阿舍老師
DefaultDirName={localappdata}\Programs\ZhCnToTw
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir=dist
OutputBaseFilename=ZhCnToTw-Setup-{#MyAppVersion}
SetupIconFile=AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
Source: "staging\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 使用者的 WebView2 profile（登入 session、cookie）與登入除錯 log 放在
; %LocalAppData%\ZhCnToTw（見 MainWindow.xaml.cs、GoogleDesktopSignIn.cs），
; 那個資料夾不是 {app} 安裝目錄本身，這裡的清單不會刪到它。要不要刪改在
; 下面的 [Code] 問使用者：只是重灌的人想保留登入狀態，把電腦交給別人或
; 不再使用的人則要清掉，不然下一個用這台電腦的人打開就是原本的帳號。
; 跟 Mac 版 Uninstaller.swift 詢問要不要順便清掉資料是同一個考量。

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  DataDir := ExpandConstant('{localappdata}\ZhCnToTw');
  if not DirExists(DataDir) then
    Exit;
  { 安靜模式（/SILENT、/VERYSILENT）沒有人能回答，保守留著 }
  if UninstallSilent then
    Exit;
  if MsgBox('要一併刪除這台電腦上的登入狀態與設定嗎？' + #13#10#13#10 +
            '只是要重新安裝的話選「否」，下次打開不用重新登入。' + #13#10 +
            '要把電腦交給別人或不再使用的話選「是」。',
            mbConfirmation, MB_YESNO) = IDYES then
    DelTree(DataDir, True, True, True);
end;
