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
#define MyAppVersion "1.5"
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
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
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
; 使用者的 WebView2 profile（登入 session、cookie）跟 %LocalAppData% 下的
; ZhCnToTw 資料夾放在一起（見 MainWindow.xaml.cs），但那個資料夾不是
; {app} 安裝目錄本身，解除安裝不會自動清掉——刻意不在這裡強制刪除：
; 使用者可能只是重灌想保留登入狀態，跟 Mac 版 Uninstaller.swift 詢問
; 使用者要不要順便清掉資料的邏輯是同一個考量，只是 Windows 這邊還沒有
; 對應的詢問 UI，先保守留著不動，需要的話之後再補。
