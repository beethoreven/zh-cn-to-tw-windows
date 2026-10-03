# 中文

## zh-cn-to-tw-windows — Windows 桌面殼

「劇本殺繁化助手」的 Windows 桌面殼，角色對應 `zh-cn-to-tw-mac`：用原生殼包 WebView 內嵌 `zh-cn-to-tw-web` 的前端，並管理本機的 `zh-cn-to-tw-ocr-service` 子行程，本機直接跑，不透過瀏覽器。這份文件分成兩個獨立的部分，請依需求閱讀:

- **[專案報告](#專案報告)**：為什麼選這套技術、遇到什麼問題、怎麼解的
- **[架設 SOP](#架設-sop)**：怎麼在本機把這支殼跑起來、怎麼打包成安裝檔、怎麼出新版

這兩部分刻意分開，不要交叉閱讀；報告是背景知識，SOP 是操作手冊。

---

## 專案報告

### 這是什麼

WPF（.NET 8）+ WebView2 的桌面殼，用固定的 `file://` 路徑載入 `zh-cn-to-tw-web` 的 `index.html`，理由跟 Mac 版完全一樣：本機 HTTP server 的 port 每次啟動不固定，會讓 `localStorage`（登入 session）的 origin 跟著變，等於每次開 App 都要重新登入；`file://` 的路徑固定，origin 穩定，登入狀態才留得住。

已經上線：用 Inno Setup 打包成單一安裝檔 `ZhCnToTw-Setup-<版本>.exe`，掛在這支 repo 的 GitHub Releases（tag 是 `v<版本>`），下載頁 `https://beethoreven.github.io/zh-cn-to-tw-web/` 直接連到安裝檔本身。功能跟 Mac 11+ 版對等：桌面版 Google 登入、Stage 1（本機 OCR + LLM 潤飾）、Stage 2（LLM 校對）、系統睡眠保護、下載結果。

支援範圍是 **Windows 10（1607 以上）／Windows 11，64 位元**，單一 build 涵蓋全部，不像 Mac 版拆成兩包。不支援 Windows 7，也沒有 32 位元或 ARM64 版——見「支援範圍為什麼是 Win10 1607 以上、64 位元」。

### 系統架構

這支 repo 是 `zh-cn-to-tw` meta-repo 底下的 submodule，跟 `zh-cn-to-tw-web`、`zh-cn-to-tw-ocr-service`、`zh-cn-to-tw-backend` 互為 sibling：

```
zh-cn-to-tw/                       (meta-repo，本身不部署)
├── build_app_exe.bat              (打包入口：清 bin/obj 後呼叫下面的 build_installer.bat)
├── zh-cn-to-tw-backend/           (Flask API，部署在 Render)
├── zh-cn-to-tw-web/               (前端，本殼用 file:// 內嵌它)
├── zh-cn-to-tw-mac/               (macOS 桌面殼)
├── zh-cn-to-tw-ocr-service/       (本機 OCR，PyInstaller 打包後內嵌進本殼)
└── zh-cn-to-tw-windows/           (這支 repo)
    ├── src/ZhCnToTw/
    └── packaging/
```

安裝後的目錄長這樣，殼在執行期就是照這個結構找東西：

```
%LocalAppData%\Programs\ZhCnToTw\
├── ZhCnToTw.exe                   (殼本身，self-contained，不需要另外裝 .NET)
├── web\                           (zh-cn-to-tw-web 的整份內容)
└── ocr-service\
    └── zh-cn-to-tw-ocr-service.exe
```

本機開發時（`dotnet run`，還沒跑過打包腳本），執行檔旁邊沒有 `web\`，殼會從執行檔位置往上找，直到找到 sibling 的 `zh-cn-to-tw-web/index.html`——這個退路假設這支 repo 是在 `zh-cn-to-tw` meta-repo 底下、跟 `zh-cn-to-tw-web` 同層被 clone 下來的。OCR 服務沒有對應的自動退路，開發時要用環境變數指定（見架設 SOP Part C）。

### 為什麼選 WebView2，不是別的方案

Windows 沒有直接對應 macOS WKWebView 的原生框架。WebView2 是微軟官方方案，跟 WKWebView 概念最接近（都是「殼掌控生命週期、內容跑在 Chromium/WebKit 的內嵌引擎」），而且多數 Windows 10/11 機器已經內建或透過 Windows Update 裝好 Runtime，不像 Electron 那樣得整包 Chromium+Node.js 一起發（動輒 100MB+），也不像 Tauri 那樣需要額外的 Rust 技術棧。

目前**沒有**做 WebView2 Runtime 的偵測或自動安裝：機器上沒有 Runtime 時，`InitializeWebViewAsync` 的 `try/catch` 會攔下初始化失敗，跳出「無法啟動內嵌瀏覽器元件（WebView2）」的訊息框後關閉 App。這層 `try/catch` 是刻意加的——它掛在 `Loaded` 的 async void lambda 底下，沒攔的話會變成未處理例外，使用者只會看到視窗憑空消失。

### 支援範圍為什麼是 Win10 1607 以上、64 位元

- **.NET 8 本身的下限就是 Windows 10 1607**，Windows 7/8.1 不在 .NET 8 的支援清單裡，這支殼選了 .NET 8 就不可能在 Win7 上跑。最初規劃時曾經把「理想上 Win7 也要能跑」列為目標（連帶規劃過把 WebView2 Evergreen Standalone Installer 內嵌進安裝包、沒偵測到 Runtime 就靜默安裝），最後沒有做，下載頁上寫的支援範圍就是 Win10 1607 以上。
- **只出 x64**：`build_installer.bat` 用 `-r win-x64` 發布，`installer.iss` 用 `ArchitecturesAllowed=x64` 擋掉其他架構；內嵌的 `zh-cn-to-tw-ocr-service` 也是在 x64 的 Python 上用 PyInstaller 打包的。
- **OCR 引擎不要求 AVX2**：開發初期在一台真實的使用者機器（Intel Pentium Gold G5400）上實測 `import paddle` 直接讓整個 process 崩潰，原因是 paddlepaddle 的 pip wheel 假設 CPU 有 AVX2。`zh-cn-to-tw-ocr-service` 因此從 paddleocr 換成 `rapidocr_onnxruntime`，這是 Windows 版能在舊款 CPU 上跑的前提——完整經過見 `zh-cn-to-tw-ocr-service` 的 README。

### zh-cn-to-tw-web 是為 WKWebView 寫的，這支殼怎麼跟它溝通

`zh-cn-to-tw-web` 的 `script.js`（Mac/Windows 共用的同一份前端）裡，桌面版登入、本機 OCR 控制、系統睡眠保護這幾個功能，直接呼叫 `window.webkit.messageHandlers.<name>.postMessage(...)`——這是 Safari/WebKit 專有的橋接 API，Chromium 為底的 WebView2 沒有這個全域物件，原樣執行會直接拋 `ReferenceError`。

刻意不修改 `zh-cn-to-tw-web`（那是 Mac/Windows 共用的前端，改了兩邊都要重新驗證，風險比較高）。改成在這支殼裡用 `CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync` 注入一段 shim，在頁面載入前就把 `window.webkit.messageHandlers` 這個物件補上，內部把同樣的呼叫轉送到 WebView2 原生的 `window.chrome.webview.postMessage`；殼這邊再用 `CoreWebView2.WebMessageReceived` 依訊息裡的 `channel` 欄位分派到對應的 C# 處理函式（`MainWindow.xaml.cs` 的 `OnWebMessageReceived`）。網頁那邊完全不用知道自己現在是跑在哪一種殼上面。

這個設計的實際效果：前端新增的功能只要不需要新的橋接 channel，Windows 殼一行都不用改。1.3 的「進行LLM潤飾」開關、1.7 的「切割左右頁格式」開關都是這樣——後者的參數是前端直接用 HTTP 送給本機 OCR 服務的，殼不經手。

### 本機 OCR 服務：用到才開、用完就關

`OcrServiceManager.cs` 對應 Mac 版 `OCRServiceManager.swift`，行為刻意保持一致：

- **Port 不寫死**：不設 `OCR_SERVICE_PORT`，讓 ocr-service 自己跟作業系統要一個空 port，殼從子行程 stdout 讀 `OCR_SERVICE_PORT=<port>` 那一行拿到實際值，再用 `ExecuteScriptAsync` 寫進頁面的 `window.__OCR_PORT__`。這個專案本機測試階段吃過很多次「port 被舊 process 卡住」的虧。
- **Token**：殼每次啟動產生一個 32 bytes 的隨機 token，用 `OCR_SERVICE_TOKEN` 環境變數傳給子行程，同時放進網頁網址的 `ocrToken` 參數。早期版本網址這邊曾經誤用另外產生的隨機值，跟傳給子行程的對不上，本機服務的 `X-OCR-Token` 驗證全部被拒絕。
- **生命週期**：服務平常是關著的。網頁按下「上傳並開始繁化」時透過 `ocrService` channel 送 `start`，OCR 階段結束（不論成敗）送 `stop`，App 關閉時也會停。刻意不做定時自動重啟，那會讓「用完就關」失去意義。
- **啟動逾時 + 重試**：20 秒內沒拿到 port 就強制砍掉重來，最多自動重試 3 次。`EnsureRunning` 每次被呼叫都會把重試次數歸零——它唯一的呼叫者是使用者按「上傳並開始繁化」，每次都是新的嘗試；原本不歸零，連續失敗 3 次之後，之後每次重按都只剩一次機會、不會再自動重試（Mac 版有同一個問題，1.7 一起修掉）。
- **Callback 一律做 identity 比對**：`OutputDataReceived`/`Exited` 是非同步 callback，可能在新 process 已經起來之後才姍姍來遲。每個 callback 都用 `ReferenceEquals(_process, process)` 確認自己還是「目前這一個」才生效，否則會把新 process 已經設好的狀態蓋掉。

### 孤兒行程：為什麼用 Job Object，不沿用 ocr-service 自己的看門狗

`zh-cn-to-tw-ocr-service` 的 `app.py` 有一個孤兒看門狗（每 5 秒輪詢 `os.getppid()` 有沒有變），Mac 版靠它在殼死掉後自我了結。這套邏輯在 Windows 上基本失效：POSIX 系統 parent 死掉後子行程會被重新掛到別的 parent，`getppid()` 讀到的值真的會變；Windows 不會重新掛接，那個值是建立當下就固定住的，parent 死了也不會變。

`ProcessJobObject.cs` 改用 Windows 原生的 Job Object（`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`）：殼持有 job handle，子行程啟動後立刻綁進這個 job，殼的 process 一結束——不管是正常關閉、被工作管理員砍掉還是崩潰——Windows 核心會直接連坐砍掉子行程。這是作業系統核心層級的保證，沒有輪詢延遲的空窗。建立 job 失敗時靜默放棄，不影響正常的啟動/停止流程：這是額外保險，不是核心功能。

### Google 桌面登入：系統瀏覽器 + loopback HTTP

WKWebView/WebView2 這類內嵌瀏覽器都不是「真正的瀏覽器」，Google 會限制或降級內嵌瀏覽器裡的 OAuth 流程。這支殼跟 Mac 版（`GoogleDesktopSignIn.swift`）走同一套協定，也是 Google 官方對原生桌面 App 建議的做法（RFC 8252：OAuth 2.0 for Native Apps）：

1. 本機起一個 loopback TCP 監聽，固定在 `127.0.0.1:53682`。
2. 用系統瀏覽器開 Google 登入頁，`redirect_uri` 指到這個本機監聽的 `/callback`。
3. 使用者在真正的瀏覽器分頁完成登入，Google 用 `response_mode=form_post` 把 `id_token` POST 回這個本機監聽。
4. 解析出 token，塞回網頁既有的 `handleCredentialResponse()`，後續驗證/儲存/UI 更新完全沿用網頁原本的邏輯。

`client_id` 直接沿用 Mac 版那一組（`GoogleDesktopSignIn.cs` 裡的常數）——Google Cloud Console 的「已授權的重新導向 URI」是精確字串比對，Mac 版已經登記過 `http://127.0.0.1:53682/callback`，Windows 用同一個 port/path 不需要另外申請。

刻意不用 `HttpListener`（.NET 內建的 HTTP.SYS 封裝）：在部分 Windows 環境下，非管理員身分監聽 HTTP prefix 需要先用 `netsh` 保留 URL ACL，不該要求一般使用者為了登入先跑一次系統管理員指令。改用最原始的 `TcpListener`，手動解析最小可用的 HTTP 請求/回應——跟 Mac 版用 `NWListener` 直接處理原始 TCP 是同一個理由。

**踩到的坑：登入偶爾卡在「沒有取得 id_token」**。原本 loopback 監聽只接受第一條連線就關掉；如果有額外的連線先搶到這唯一一次 accept（例如瀏覽器的連線預熱），真正帶 `id_token` 的 callback 反而會在監聽已關閉的狀態下被拒絕連線。改成迴圈接受連線，直到解析出 `id_token` 或整體逾時（300 秒）為止。已用真實 Google 帳號在本機重現並確認修復有效。除錯 log 寫在 `%LocalAppData%\ZhCnToTw\oauth_debug.log`，`id_token` 是遮罩後才寫入磁碟的，只留長度方便比對。

### 踩到的坑：Windows 版被誤判成「過舊的 Mac 版」要求強制更新

實測撞過：登入後畫面直接跳出「目前 App 版本過舊（最低需求版本 1.1），請先更新才能繼續使用」，但 Windows 版當時根本還沒有版本 1.1 這個概念。

原因：後端 `GET /api/version_check` 用 `(os, os_version)` 這組複合鍵查強制更新門檻。`zh-cn-to-tw-web` 的 `script.js` 呼叫這支 API 時，`os` 參數是寫死的 `"macos"`（Mac/Windows 共用前端的既有限制）；殼這邊原本把 `osTier` 這個查詢參數也沿用 Mac 版的值，於是 `(os=macos, os_version=<Mac 的 tier>)` 直接命中 Mac 版真正在用的政策資料，Windows 端當時回報的佔位版本號 `0.1` 被判定過舊。

修法：把 `osTier` 改成資料庫裡不存在的值 `"windows"`。後端 `app.py` 的 `version_check` 本來就設計成「查無這個 os 的門檻資料時一律不擋」（`get_policy` 回 `None` 就直接回 `force_update: false`），`(os=macos, os_version=windows)` 查無資料，問題解決。`script.js` 裡以 `osTier` 分流的地方（`"10.15"` 走 Vision OCR 與 `legacyDownload`）用的都是精確字串比對，`"windows"` 不會命中，Windows 一律走 ocr-service 與標準下載路徑。

這個修法的代價見下面「版本號」一節：Windows 目前完全不受強制更新政策約束。

### 版本號：跟 Mac 版同步，但沒有共用的追蹤機制

Windows 版的版本號是手動維護的，寫死在三個地方，出新版時要一起改：

| 位置 | 影響什麼 |
|---|---|
| `src/ZhCnToTw/ZhCnToTw.csproj` 的 `Version`/`FileVersion`/`AssemblyVersion` | 檔案總管「內容」分頁看到的版本資訊 |
| `packaging/installer.iss` 的 `MyAppVersion` | 安裝檔檔名（`ZhCnToTw-Setup-<版本>.exe`）與「程式和功能」清單顯示的版本 |
| `src/ZhCnToTw/MainWindow.xaml.cs` 的 `BuildDesktopUrl` 裡的 `appMajor`/`appMinor` | 網頁向後端 `version_check` 回報的版本 |

版本號刻意跟 Mac 版對齊（同一個版本號代表同一份前端功能），Mac 版出新版、Windows 版跟上對等功能時一起手動更新。Windows 版不一定每個版號都有對應的發布：1.6 是共用前端的修正（冷啟動時畫面沒鎖住），當時只有 Mac 版出了 1.6，Windows 從 1.5 直接到 1.7，1.7 的安裝檔才包進那個修正。

meta-repo 的 `update_version` skill 只處理 Mac 版的 `Info.plist` 跟後端 `app_versions` 表，不會動到上面這三處。後端 `app_versions` 表目前也沒有 `(os=macos, os_version=windows)` 的資料列，所以**強制更新政策對 Windows 版不生效**——要讓它生效，需要一併處理 `script.js` 裡寫死的 `os=macos`，以及在 `app_versions` 補上 Windows 的資料列。

### 打包：Inno Setup 安裝檔

`packaging/build_installer.bat` 做四件事，對應 Mac 版的 build_app + build_dmg：

1. `dotnet publish`（self-contained、win-x64、Release）到 `packaging\staging\app\`——self-contained 所以使用者機器不需要另外裝 .NET。
2. 把 `zh-cn-to-tw-web` 整份複製進 `staging\app\web\`。
3. 用 `zh-cn-to-tw-ocr-service` 自己的 venv 跑 PyInstaller 重新打包，複製進 `staging\app\ocr-service\`。
4. 呼叫 Inno Setup 的 `ISCC.exe`，把 `staging\app\` 整個封裝成 `packaging\dist\ZhCnToTw-Setup-<版本>.exe`。

每一步都是真的重新 build，不沿用舊產物（對應 `known-issue-check` 第 21 條：「打包給人用」的腳本不能把過期的產物包成一個看起來全新的檔名）。meta-repo 根目錄的 `build_app_exe.bat` 是入口，先清掉 `bin/`、`obj/` 再呼叫這支腳本。實測 1.7 的安裝檔約 137MB，`staging\app\` 展開約 442MB。

幾個刻意的決定與踩過的坑：

- **安裝不需要 UAC 提權**：`PrivilegesRequired=lowest` + 安裝到 `{localappdata}\Programs\ZhCnToTw`，一般使用者帳號就能裝完。這跟把 WebView2 使用者資料夾放在 `%LocalAppData%\ZhCnToTw\WebView2` 是同一個理由的延伸——整條路徑都不碰需要管理員權限才能寫入的目錄。
- **顯示名稱用中文、檔名用 ASCII**：使用者看得到的名稱（視窗標題、開始功能表、桌面捷徑、解除安裝清單）是「繁化助手」，對齊 Mac 版；執行檔 `ZhCnToTw.exe` 與安裝檔檔名維持 ASCII。Mac 版發布 DMG 時實測撞過 `gh` CLI 會把上傳檔名開頭的 CJK 位元組吃掉（見 `zh-cn-to-tw-mac` 的 README），Release 資產維持 ASCII 直接避開。
- **PyInstaller 偶發 `FileNotFoundError`**：PyInstaller 寫出 exe 後立刻想重新開啟它設定時間戳記，被防毒軟體即時掃描鎖住檔案。這個錯誤不在 PyInstaller 自己 `_retry_operation` 的白名單裡，一次沒中就直接放棄；同一個指令手動重跑幾次會成功，確認是暫時性問題。腳本因此自己重試整個 PyInstaller 呼叫，最多 3 次。
- **批次檔不用多行括號區塊**：在這個環境下，`cmd.exe` 對 `if`/`for` 的多行 `( ... )` 主體解析不可靠——即使該分支的條件不成立，整支腳本也會無聲中斷，沒有任何錯誤訊息。全部改用單行 `if X goto label` 之後穩定下來。路徑也先用 `%%~f` 正規化，帶 `..\..` 的未正規化路徑同樣讓 `if exist` 無聲失效過。
- **批次檔刻意只用 ASCII**：`cmd.exe` 對非 ASCII 原始碼編碼（BOM、codepage）的處理不一致，寫這支腳本時直接撞過，所以兩支 `.bat` 都沒有中文註解。

### 下載存到桌面

Stage 1/2 的「下載結果」走 `fetch` + blob + `<a download>` 這條標準路徑，WebView2 原生就認得。`DownloadStarting` 事件把存檔目錄從系統「下載」資料夾換成桌面，檔名用 `UniqueDestination` 加 ` (1)`、` (2)` 後綴避免覆蓋使用者前一次下載的同名檔案。`legacyDownload` channel 也有實作（同樣寫到桌面），但 `script.js` 只有在 `osTier === "10.15"` 才會走那條路，Windows 上實際不會被觸發，只是備援。

### 檔案結構

```
src/ZhCnToTw/
├── ZhCnToTw.csproj             # 專案檔；版本號（三處之一）
├── App.xaml(.cs)               # 進入點
├── MainWindow.xaml(.cs)        # 主視窗、WebView2 初始化、webkit-bridge shim
│                               # 注入與分派、桌面版網址參數（含 appMajor/
│                               # appMinor）、睡眠保護、下載改存桌面、重新整理鍵
├── GoogleDesktopSignIn.cs      # 桌面版 Google 登入（loopback + 系統瀏覽器）
├── OcrServiceManager.cs        # 本機 OCR 子行程生命週期、port/token、逾時重試
└── ProcessJobObject.cs         # Job Object：殼結束時連坐砍掉 OCR 子行程
packaging/
├── build_installer.bat         # 打包腳本（publish → web → ocr-service → Inno Setup）
├── installer.iss               # Inno Setup 腳本；版本號（三處之一）
└── AppIcon.ico
```

`packaging/staging/`、`packaging/dist/` 是打包產物，不進版控。

### 已知限制

- **強制更新政策對 Windows 不生效**，版本號也沒有自動化的追蹤機制，見「版本號」一節。
- **沒有 WebView2 Runtime 偵測/自動安裝**：機器上沒有 Runtime 時只會跳錯誤訊息然後關閉，使用者要自己去裝。
- **不支援 Windows 7／8.1、32 位元、ARM64**。
- **解除安裝不會清使用者資料**：`%LocalAppData%\ZhCnToTw\`（WebView2 profile、登入 session、`oauth_debug.log`）不在安裝目錄底下，解除安裝後會留著。這是刻意保守的——使用者可能只是重灌想保留登入狀態；Mac 版有詢問要不要一併清掉的 UI，Windows 這邊還沒有。
- **WebView2 render process 掛掉時沒有完整復原 UI**：Mac 版對 WKWebView 背景 process 被系統砍掉有專門處理（整個換成原生提示畫面）。這裡只監聽 `CoreWebView2.ProcessFailed` 印 log，還沒實測過是否需要 Mac 版那種複雜度的復原 UI。
- **安裝檔沒有程式碼簽章**。

---

# 架設 SOP / Setup Guide

## Part A. 開發環境需求

### 1. 只是要在本機把殼跑起來

1. **.NET SDK 8.0（LTS）**——用 `dotnet --version` 確認；沒有的話用 `winget install --id Microsoft.DotNet.SDK.8 --source winget` 裝。
2. **Microsoft Edge WebView2 Runtime**——Windows 10/11 通常已經內建，沒有的話殼啟動時會跳錯誤訊息。
3. 這支 repo 要跟 `zh-cn-to-tw-web` 放在同一個 `zh-cn-to-tw` meta-repo 底下（互為 sibling submodule）。單獨 clone 這支 repo、旁邊沒有 `zh-cn-to-tw-web` 的話，畫面會顯示「找不到前端網頁」。

### 2. 要打包成安裝檔，另外還需要

1. **Inno Setup 6**——`winget install --id JRSoftware.InnoSetup`。腳本會依序在 `%LOCALAPPDATA%\Programs\Inno Setup 6\`、`%ProgramFiles(x86)%\Inno Setup 6\`、`%ProgramFiles%\Inno Setup 6\` 找 `ISCC.exe`。
2. **`zh-cn-to-tw-ocr-service` 的 venv**——打包腳本會直接用 `zh-cn-to-tw-ocr-service\venv\Scripts\python.exe` 跑 PyInstaller，找不到就中止。venv（虛擬環境）是一個獨立的 Python 套件安裝目錄，避免跟系統的 Python 互相干擾。第一次要先建好：

```powershell
cd ..\zh-cn-to-tw-ocr-service
python -m venv venv
venv\Scripts\Activate.ps1
pip install -r requirements.txt -r requirements-build.txt
```

## Part B. 本機執行

```bash
cd src/ZhCnToTw
dotnet run
```

第一次執行 WebView2 會在 `%LocalAppData%\ZhCnToTw\WebView2` 建立自己的使用者資料夾（存 cookie、localStorage、登入 session），跟執行檔本身分開放。

這樣跑起來，登入與 Stage 2 可以直接用。**Stage 1 需要本機 OCR 服務**，而 `dotnet run` 的輸出目錄旁邊沒有 `ocr-service\`，要先設下面其中一個環境變數再執行，否則按「上傳並開始繁化」會顯示找不到 ocr-service 執行檔的錯誤。最快的做法是直接用 ocr-service 的 venv（需要先照 Part A 步驟 2 建好）：

```powershell
$env:OCR_SERVICE_DEV_PYTHON_DIR = "I:\Codebase\zh-cn-to-tw\zh-cn-to-tw-ocr-service"
dotnet run
```

## Part C. 環境變數總覽

全部都是開發階段用的，正式安裝的版本不需要設任何一個。

| 變數 | 必填 | 說明 |
|---|---|---|
| `WEB_BASE_URL_OVERRIDE` | 否 | 指到本機另外跑的網頁伺服器（例如 `python -m http.server`），測試還沒進 sibling 目錄的前端改動。設定後殼會直接載入這個網址，不會走 `file://`。 |
| `WEB_API_BASE_OVERRIDE` | 否 | 指到本機另外跑的 backend（例如 `http://127.0.0.1:5001`），測試還沒部署上去的後端改動。省略時預設打正式的 `https://zh-cn-to-tw-backend.onrender.com`。 |
| `OCR_SERVICE_DEV_PATH` | 否 | 指到本機已經用 PyInstaller 打包好的 `zh-cn-to-tw-ocr-service.exe`。只有在執行檔旁邊沒有 `ocr-service\` 時才會被用到。 |
| `OCR_SERVICE_DEV_PYTHON_DIR` | 否 | 指到 `zh-cn-to-tw-ocr-service` repo 根目錄，殼會直接用它的 `venv\Scripts\python.exe app.py` 啟動服務，不需要每次改動都重新跑 PyInstaller。優先順序排在 `OCR_SERVICE_DEV_PATH` 之後。 |

## Part D. 打包成安裝檔

在 meta-repo 根目錄執行：

```bat
build_app_exe.bat
```

它會清掉 `bin/`、`obj/`、`packaging\staging\`、`packaging\dist\`，然後依序跑 `dotnet publish`、複製前端、PyInstaller 重新打包 OCR 服務、Inno Setup 封裝。整個流程約數分鐘，產物在 `zh-cn-to-tw-windows\packaging\dist\ZhCnToTw-Setup-<版本>.exe`。

如果第 3 步連續三次失敗並提示防毒軟體，把 `zh-cn-to-tw-ocr-service` 資料夾加進防毒軟體即時掃描的排除清單後重跑。

## Part E. 出新版

1. 把版本號改成新的，三個地方要一致（見專案報告「版本號」一節）：`src/ZhCnToTw/ZhCnToTw.csproj`、`packaging/installer.iss`、`src/ZhCnToTw/MainWindow.xaml.cs` 的 `appMajor`/`appMinor`。
2. 照 Part D 打包，實際安裝起來測過登入、Stage 1、Stage 2。
3. commit、push，然後建立 Release 並上傳安裝檔（tag 是 `v<版本>`，檔名維持 ASCII）：

```bash
gh release create v1.7 packaging/dist/ZhCnToTw-Setup-1.7.exe --title "繁化助手 1.7（Windows）"
```

4. 到 `zh-cn-to-tw-web` 的 `update-page` 分支，在 `index.html` 的 Windows 區塊最上面新增一個 `.version-block`，連結格式是 `https://github.com/beethoreven/zh-cn-to-tw-windows/releases/download/v<版本>/ZhCnToTw-Setup-<版本>.exe`。新增、不要改寫既有的區塊——舊版要留著供人回退。
5. 回到 meta-repo，更新這支 submodule 的 pointer 並 commit。

---

# English

## zh-cn-to-tw-windows — Windows Desktop Shell

The Windows desktop shell for the traditional-Chinese script-murder-game localization assistant, playing the same role as `zh-cn-to-tw-mac`: a native shell wrapping a WebView that embeds the `zh-cn-to-tw-web` frontend and manages the local `zh-cn-to-tw-ocr-service` subprocess, run locally without a browser. This document has two independent parts, read whichever you need:

- **[Project Report](#project-report)**: why this stack was chosen, what went wrong, how it was fixed
- **[Setup Guide](#setup-guide)**: how to run this shell locally, package it into an installer, and ship a new version

The two parts are intentionally separate; the report is background knowledge, the guide is an operations manual.

---

## Project Report

### What This Is

A WPF (.NET 8) + WebView2 desktop shell that loads `zh-cn-to-tw-web`'s `index.html` via a fixed `file://` path, for exactly the same reason as the Mac build: a local HTTP server's port changes on every launch, which changes `localStorage`'s (login session) origin every time — meaning the user would have to log in again on every app start. A fixed `file://` path keeps the origin stable, so the login session survives restarts.

It is live: packaged with Inno Setup into a single installer, `ZhCnToTw-Setup-<version>.exe`, attached to this repo's GitHub Releases (tagged `v<version>`), and the download page at `https://beethoreven.github.io/zh-cn-to-tw-web/` links straight to the installer file. It is at feature parity with the Mac 11+ build: desktop Google sign-in, Stage 1 (local OCR + LLM refinement), Stage 2 (LLM proofreading), the system-sleep guard, and result downloads.

Supported range is **Windows 10 (1607 or later) / Windows 11, 64-bit**, covered by a single build rather than split into two packages like the Mac side. Windows 7 is not supported, and there is no 32-bit or ARM64 build — see "Why the supported range is Windows 10 1607+, 64-bit".

### System Architecture

This repo is a submodule under the `zh-cn-to-tw` meta-repo, sibling to `zh-cn-to-tw-web`, `zh-cn-to-tw-ocr-service`, and `zh-cn-to-tw-backend`:

```
zh-cn-to-tw/                       (meta-repo, not deployed itself)
├── build_app_exe.bat              (packaging entry point: cleans bin/obj, then calls build_installer.bat below)
├── zh-cn-to-tw-backend/           (Flask API, deployed on Render)
├── zh-cn-to-tw-web/               (frontend, embedded via file:// here)
├── zh-cn-to-tw-mac/               (macOS desktop shell)
├── zh-cn-to-tw-ocr-service/       (local OCR, PyInstaller-packaged and embedded in this shell)
└── zh-cn-to-tw-windows/           (this repo)
    ├── src/ZhCnToTw/
    └── packaging/
```

The installed layout looks like this, and it is exactly the structure the shell searches at runtime:

```
%LocalAppData%\Programs\ZhCnToTw\
├── ZhCnToTw.exe                   (the shell itself, self-contained, no separate .NET install needed)
├── web\                           (the entire contents of zh-cn-to-tw-web)
└── ocr-service\
    └── zh-cn-to-tw-ocr-service.exe
```

During local development (`dotnet run`, before any packaging script has run) there is no `web\` next to the executable, so the shell walks up from the executable's location until it finds a sibling `zh-cn-to-tw-web/index.html` — this fallback assumes the repo is checked out under the `zh-cn-to-tw` meta-repo, at the same level as `zh-cn-to-tw-web`. The OCR service has no equivalent automatic fallback; in development it must be pointed to with an environment variable (see Setup Guide, Part C).

### Why WebView2, and not something else

Windows has no framework that directly corresponds to macOS's WKWebView. WebView2 is Microsoft's official answer — conceptually closest to WKWebView (a shell that owns the lifecycle, content runs inside an embedded Chromium/WebKit engine) — and most Windows 10/11 machines already have the Runtime preinstalled or delivered via Windows Update, unlike Electron (which ships an entire Chromium+Node.js bundle, often 100MB+) or Tauri (which pulls in a separate Rust toolchain).

There is currently **no** WebView2 Runtime detection or automatic installation: on a machine without the Runtime, the `try/catch` in `InitializeWebViewAsync` catches the initialization failure, shows a "cannot start the embedded browser component (WebView2)" message box, and closes the app. That `try/catch` is deliberate — the code runs under an async void lambda on `Loaded`, so without it the failure would become an unhandled exception and the user would just see the window vanish.

### Why the supported range is Windows 10 1607+, 64-bit

- **.NET 8's own floor is Windows 10 1607**; Windows 7/8.1 are not on .NET 8's supported list, so choosing .NET 8 rules out running on Windows 7. The original plan listed "ideally Windows 7 should work too" as a goal (along with embedding the WebView2 Evergreen Standalone Installer in the installer and silently installing it when no Runtime was detected); that was not done, and the supported range stated on the download page is Windows 10 1607 or later.
- **x64 only**: `build_installer.bat` publishes with `-r win-x64`, and `installer.iss` blocks other architectures with `ArchitecturesAllowed=x64`; the embedded `zh-cn-to-tw-ocr-service` is likewise PyInstaller-packaged on an x64 Python.
- **The OCR engine does not require AVX2**: early in development, `import paddle` crashed the entire process on a real user-grade machine (an Intel Pentium Gold G5400), because paddlepaddle's pip wheel assumes the CPU has AVX2. `zh-cn-to-tw-ocr-service` therefore switched from paddleocr to `rapidocr_onnxruntime`, which is the precondition for the Windows build running on older CPUs — the full story is in `zh-cn-to-tw-ocr-service`'s README.

### zh-cn-to-tw-web was written for WKWebView — how does this shell talk to it

`zh-cn-to-tw-web`'s `script.js` (the same frontend shared by both the Mac and Windows builds) calls `window.webkit.messageHandlers.<name>.postMessage(...)` directly for desktop sign-in, local OCR control, and the system-sleep guard — this is a Safari/WebKit-only bridge API that Chromium-based WebView2 does not have; calling it as-is throws a `ReferenceError`.

`zh-cn-to-tw-web` was deliberately left unmodified (it's the frontend shared by both platforms; changing it means re-validating both). Instead, this shell injects a shim via `CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync` that defines `window.webkit.messageHandlers` before the page's own scripts run, forwarding the same calls to WebView2's native `window.chrome.webview.postMessage`; the shell then dispatches on `CoreWebView2.WebMessageReceived` based on a `channel` field to the matching C# handler (`OnWebMessageReceived` in `MainWindow.xaml.cs`). The web page never needs to know which shell it's running inside.

The practical effect of this design: as long as a new frontend feature needs no new bridge channel, the Windows shell needs no changes at all. The "LLM refinement" toggle in 1.3 and the "split left/right pages" toggle in 1.7 both worked this way — the latter's parameter is sent by the frontend straight to the local OCR service over HTTP, never passing through the shell.

### Local OCR service: started on demand, stopped when done

`OcrServiceManager.cs` is the counterpart of the Mac build's `OCRServiceManager.swift`, deliberately kept behaviorally identical:

- **No hardcoded port**: `OCR_SERVICE_PORT` is left unset so ocr-service asks the OS for a free port itself; the shell reads the actual value from the `OCR_SERVICE_PORT=<port>` line on the subprocess's stdout, then writes it into the page's `window.__OCR_PORT__` via `ExecuteScriptAsync`. This project was bitten many times during local testing by "port held by a stale process".
- **Token**: on every launch the shell generates a 32-byte random token, passes it to the subprocess via the `OCR_SERVICE_TOKEN` environment variable, and also puts it in the page URL's `ocrToken` parameter. An early version mistakenly used a separately generated random value on the URL side, which didn't match what the subprocess received, so the local service rejected every `X-OCR-Token` check.
- **Lifecycle**: the service is normally off. When the page's "upload and start" button is pressed it sends `start` over the `ocrService` channel, sends `stop` once the OCR phase ends (success or failure), and the service is also stopped when the app closes. There is deliberately no periodic auto-restart; that would defeat "stop when done".
- **Startup timeout + retry**: if no port arrives within 20 seconds the process is force-killed and restarted, up to 3 automatic attempts. `EnsureRunning` resets the attempt counter on every call — its only caller is the user pressing "upload and start", and each press is a fresh attempt; previously it was not reset, so after 3 consecutive failures every later press got only a single attempt with no automatic retry (the Mac build had the same bug, fixed together in 1.7).
- **Every callback does an identity check**: `OutputDataReceived`/`Exited` are asynchronous callbacks that may arrive after a new process is already up. Each callback confirms via `ReferenceEquals(_process, process)` that it still belongs to "the current one" before taking effect; otherwise it would overwrite state the new process had already set correctly.

### Orphan processes: why a Job Object instead of ocr-service's own watchdog

`zh-cn-to-tw-ocr-service`'s `app.py` has an orphan watchdog (polling every 5 seconds whether `os.getppid()` changed), which the Mac build relies on to self-terminate once the shell dies. That logic is effectively broken on Windows: on POSIX systems a child gets reparented when its parent dies, so `getppid()` genuinely changes; Windows does not reparent — the value is fixed at creation time and stays the same after the parent dies.

`ProcessJobObject.cs` uses the native Windows Job Object (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) instead: the shell holds the job handle, the subprocess is assigned to the job immediately after starting, and the moment the shell's process ends — clean exit, killed from Task Manager, or a crash — the Windows kernel kills the subprocess along with it. This is an OS-kernel-level guarantee with no polling-delay window. If creating the job fails it is silently abandoned without affecting the normal start/stop flow: this is an extra safety net, not core functionality.

### Google desktop sign-in: system browser + loopback HTTP

Embedded WebViews (WKWebView or WebView2 alike) aren't "real" browsers, and Google restricts or degrades OAuth flows run inside them. This shell follows the same protocol as the Mac build (`GoogleDesktopSignIn.swift`) — also Google's own recommendation for native desktop apps (RFC 8252, OAuth 2.0 for Native Apps):

1. Start a loopback TCP listener locally, fixed at `127.0.0.1:53682`.
2. Open Google's sign-in page in the system browser, with `redirect_uri` pointing at this local listener's `/callback`.
3. The user completes sign-in in a real browser tab; Google POSTs the `id_token` back to the local listener via `response_mode=form_post`.
4. The token is parsed out and handed to the page's existing `handleCredentialResponse()` — all subsequent validation, storage, and UI updates reuse the page's existing logic unchanged.

The `client_id` is the same one the Mac build uses (a constant in `GoogleDesktopSignIn.cs`) — Google Cloud Console's "Authorized redirect URIs" is an exact string match, and the Mac build already has `http://127.0.0.1:53682/callback` registered, so Windows reuses the same port/path without needing a separate registration.

`HttpListener` (.NET's HTTP.SYS wrapper) was deliberately avoided: on some Windows setups, listening on an HTTP prefix as a non-administrator requires a `netsh` URL ACL reservation beforehand, and users shouldn't have to run an admin command just to sign in. A raw `TcpListener` is used instead, manually parsing the minimal HTTP request/response needed — the same reasoning as the Mac build's direct use of `NWListener` at the raw TCP level.

**Bug hit: sign-in occasionally stuck on "no id_token received"**. The loopback listener originally accepted only the first connection and then closed; if an extra connection grabbed that single accept first (for example the browser's connection pre-warming), the real callback carrying the `id_token` was refused because the listener was already closed. It now loops accepting connections until an `id_token` is parsed or the overall timeout (300 seconds) elapses. Reproduced locally with a real Google account and confirmed fixed. A debug log is written to `%LocalAppData%\ZhCnToTw\oauth_debug.log`; the `id_token` is redacted before being written to disk, keeping only its length for comparison.

### Bug hit during testing: the Windows build was misidentified as an outdated Mac build

Observed in testing: right after signing in, the app popped a dialog saying "This app version is too old (minimum required version 1.1), please update before continuing" — but the Windows build had no concept of version 1.1 at the time.

Root cause: the backend's `GET /api/version_check` looks up the forced-update threshold by the composite key `(os, os_version)`. `zh-cn-to-tw-web`'s `script.js` hardcodes `os="macos"` when calling this endpoint (a pre-existing limitation of the frontend shared across both platforms). The shell originally reused the Mac build's value for the `osTier` query parameter too, so `(os=macos, os_version=<the Mac tier>)` hit exactly the policy row the real Mac build uses, and the placeholder version `0.1` the Windows build was reporting at the time got flagged as outdated.

Fix: change `osTier` to `"windows"`, a value with no row in the database. The backend's `version_check` was already designed so that "no threshold data for this os means never block" (`get_policy` returning `None` short-circuits to `force_update: false`), so `(os=macos, os_version=windows)` finds no row and the problem goes away. The places in `script.js` that branch on `osTier` (`"10.15"` taking the Vision OCR path and `legacyDownload`) all use exact string comparison, so `"windows"` never matches and Windows always takes the ocr-service and standard-download paths.

The cost of this fix is covered under "Version number" below: the Windows build is currently not subject to the forced-update policy at all.

### Version number: kept in sync with the Mac build, but with no shared tracking mechanism

The Windows build's version number is maintained by hand and hardcoded in three places that must be changed together for each release:

| Location | What it affects |
|---|---|
| `Version`/`FileVersion`/`AssemblyVersion` in `src/ZhCnToTw/ZhCnToTw.csproj` | The version shown in File Explorer's "Properties" tab |
| `MyAppVersion` in `packaging/installer.iss` | The installer filename (`ZhCnToTw-Setup-<version>.exe`) and the version shown in "Programs and Features" |
| `appMajor`/`appMinor` in `BuildDesktopUrl` in `src/ZhCnToTw/MainWindow.xaml.cs` | The version the page reports to the backend's `version_check` |

The version number is deliberately aligned with the Mac build (the same number means the same frontend feature set) and is bumped by hand when the Mac build ships a new version and the Windows build catches up on the equivalent features. Not every version number has a Windows release: 1.6 was a fix in the shared frontend (the page not being locked on cold start), only the Mac build shipped a 1.6 at the time, and Windows went straight from 1.5 to 1.7 — the 1.7 installer is the first to include that fix.

The meta-repo's `update_version` skill only handles the Mac build's `Info.plist` and the backend's `app_versions` table; it does not touch the three places above. The backend's `app_versions` table also currently has no row for `(os=macos, os_version=windows)`, so **the forced-update policy has no effect on the Windows build** — making it take effect requires also dealing with the hardcoded `os=macos` in `script.js` and adding Windows rows to `app_versions`.

### Packaging: the Inno Setup installer

`packaging/build_installer.bat` does four things, the counterpart of the Mac side's build_app + build_dmg:

1. `dotnet publish` (self-contained, win-x64, Release) into `packaging\staging\app\` — self-contained so the user's machine needs no separate .NET install.
2. Copies all of `zh-cn-to-tw-web` into `staging\app\web\`.
3. Re-packages with PyInstaller using `zh-cn-to-tw-ocr-service`'s own venv, and copies the result into `staging\app\ocr-service\`.
4. Calls Inno Setup's `ISCC.exe` to wrap all of `staging\app\` into `packaging\dist\ZhCnToTw-Setup-<version>.exe`.

Every step genuinely rebuilds and never reuses stale output (per `known-issue-check` item 21: a "package for distribution" script must not wrap stale output under a fresh-looking filename). `build_app_exe.bat` at the meta-repo root is the entry point; it cleans `bin/` and `obj/` first, then calls this script. Measured for 1.7: the installer is about 137MB, and `staging\app\` unpacked is about 442MB.

A few deliberate decisions and pitfalls:

- **Installation needs no UAC elevation**: `PrivilegesRequired=lowest` plus installing to `{localappdata}\Programs\ZhCnToTw`, so an ordinary user account can complete the install. This extends the same reasoning as putting the WebView2 user data folder at `%LocalAppData%\ZhCnToTw\WebView2` — nothing on the whole path touches a directory that needs administrator rights to write.
- **Chinese display name, ASCII filenames**: the user-visible name (window title, Start Menu, desktop shortcut, uninstall list) is 「繁化助手」, matching the Mac build; the executable `ZhCnToTw.exe` and the installer filename stay ASCII. The Mac build hit a `gh` CLI bug when publishing its DMG, where leading CJK bytes of an uploaded filename get eaten (see `zh-cn-to-tw-mac`'s README); keeping Release assets ASCII avoids it outright.
- **Intermittent PyInstaller `FileNotFoundError`**: right after writing the exe, PyInstaller tries to re-open it to set the timestamp, and real-time antivirus scanning has the file locked. This error isn't on PyInstaller's own `_retry_operation` allowlist, so it gives up after one miss; rerunning the exact same command by hand a few times succeeds, confirming it is transient. The script therefore retries the entire PyInstaller invocation itself, up to 3 times.
- **No multi-line parenthesized blocks in the batch files**: in this environment `cmd.exe` parses multi-line `( ... )` bodies of `if`/`for` unreliably — the whole script would silently abort with no error message even when that branch's condition was false. Switching everything to single-line `if X goto label` made it stable. Paths are also normalized up front with `%%~f`; an un-normalized path containing `..\..` likewise made `if exist` fail silently.
- **Batch files are deliberately ASCII-only**: `cmd.exe` handles non-ASCII source encoding (BOM, codepage) inconsistently, hit directly while writing this script, so neither `.bat` has Chinese comments.

### Downloads go to the Desktop

The Stage 1/2 "download result" buttons use the standard `fetch` + blob + `<a download>` path, which WebView2 handles natively. The `DownloadStarting` event redirects the save directory from the system "Downloads" folder to the Desktop, with `UniqueDestination` adding ` (1)`, ` (2)` suffixes so a previous download of the same name isn't overwritten. The `legacyDownload` channel is implemented too (also writing to the Desktop), but `script.js` only takes that path when `osTier === "10.15"`, so it is never actually triggered on Windows and exists only as a fallback.

### File Structure

```
src/ZhCnToTw/
├── ZhCnToTw.csproj             # Project file; version number (one of three places)
├── App.xaml(.cs)               # Entry point
├── MainWindow.xaml(.cs)        # Main window, WebView2 init, webkit-bridge shim
│                               # injection/dispatch, desktop URL parameters (incl.
│                               # appMajor/appMinor), sleep guard, downloads to
│                               # Desktop, reload button
├── GoogleDesktopSignIn.cs      # Desktop Google sign-in (loopback + system browser)
├── OcrServiceManager.cs        # Local OCR subprocess lifecycle, port/token, timeout retry
└── ProcessJobObject.cs         # Job Object: kills the OCR subprocess when the shell exits
packaging/
├── build_installer.bat         # Packaging script (publish → web → ocr-service → Inno Setup)
├── installer.iss               # Inno Setup script; version number (one of three places)
└── AppIcon.ico
```

`packaging/staging/` and `packaging/dist/` are build output and are not version-controlled.

### Known Limitations

- **The forced-update policy has no effect on Windows**, and the version number has no automated tracking; see "Version number".
- **No WebView2 Runtime detection or automatic install**: on a machine without the Runtime the app only shows an error message and closes; the user has to install it themselves.
- **No support for Windows 7/8.1, 32-bit, or ARM64.**
- **Uninstalling does not remove user data**: `%LocalAppData%\ZhCnToTw\` (WebView2 profile, login session, `oauth_debug.log`) is outside the install directory and is left behind after uninstalling. This is deliberately conservative — the user may just be reinstalling and want to keep their login; the Mac build has UI asking whether to clear it as well, which Windows doesn't have yet.
- **No full recovery UI for a crashed WebView2 render process**: the Mac build has dedicated handling for WKWebView's background process being killed by the system (swapping in a native placeholder screen). Here only `CoreWebView2.ProcessFailed` is logged, and it hasn't been tested in practice whether the Mac build's level of recovery-UI complexity is needed.
- **The installer is not code-signed.**

---

# Setup Guide

## Part A. Development Environment Requirements

### 1. Just running the shell locally

1. **.NET SDK 8.0 (LTS)** — check with `dotnet --version`; if missing, install with `winget install --id Microsoft.DotNet.SDK.8 --source winget`.
2. **Microsoft Edge WebView2 Runtime** — usually preinstalled on Windows 10/11; without it the shell shows an error message at startup.
3. This repo must sit alongside `zh-cn-to-tw-web` under the same `zh-cn-to-tw` meta-repo (sibling submodules). If this repo is cloned standalone without `zh-cn-to-tw-web` next to it, the app will show "Frontend page not found".

### 2. Additionally needed for packaging an installer

1. **Inno Setup 6** — `winget install --id JRSoftware.InnoSetup`. The script looks for `ISCC.exe` in `%LOCALAPPDATA%\Programs\Inno Setup 6\`, `%ProgramFiles(x86)%\Inno Setup 6\`, and `%ProgramFiles%\Inno Setup 6\`, in that order.
2. **`zh-cn-to-tw-ocr-service`'s venv** — the packaging script runs PyInstaller directly with `zh-cn-to-tw-ocr-service\venv\Scripts\python.exe` and aborts if it isn't found. A venv (virtual environment) is a self-contained Python package directory that keeps things from interfering with the system Python. Create it once first:

```powershell
cd ..\zh-cn-to-tw-ocr-service
python -m venv venv
venv\Scripts\Activate.ps1
pip install -r requirements.txt -r requirements-build.txt
```

## Part B. Running Locally

```bash
cd src/ZhCnToTw
dotnet run
```

On first run, WebView2 creates its own user data folder at `%LocalAppData%\ZhCnToTw\WebView2` (cookies, localStorage, login session), kept separate from the executable itself.

Run this way, sign-in and Stage 2 work right away. **Stage 1 needs the local OCR service**, and `dotnet run`'s output directory has no `ocr-service\` next to it, so set one of the environment variables below before running; otherwise pressing "upload and start" shows an error that the ocr-service executable cannot be found. The quickest option is to use ocr-service's venv directly (create it first per Part A, step 2):

```powershell
$env:OCR_SERVICE_DEV_PYTHON_DIR = "I:\Codebase\zh-cn-to-tw\zh-cn-to-tw-ocr-service"
dotnet run
```

## Part C. Environment Variables

All of these are for development only; an installed release build needs none of them.

| Variable | Required | Description |
|---|---|---|
| `WEB_BASE_URL_OVERRIDE` | No | Points to a local web server (e.g. `python -m http.server`), for testing frontend changes not yet in the sibling directory. When set, the shell navigates to this URL directly instead of `file://`. |
| `WEB_API_BASE_OVERRIDE` | No | Points to a local backend (e.g. `http://127.0.0.1:5001`), for testing backend changes not yet deployed. Defaults to the production `https://zh-cn-to-tw-backend.onrender.com` when unset. |
| `OCR_SERVICE_DEV_PATH` | No | Points to a locally PyInstaller-packaged `zh-cn-to-tw-ocr-service.exe`. Only used when there is no `ocr-service\` next to the executable. |
| `OCR_SERVICE_DEV_PYTHON_DIR` | No | Points to the `zh-cn-to-tw-ocr-service` repo root; the shell starts the service directly with its `venv\Scripts\python.exe app.py`, so PyInstaller doesn't have to be rerun for every change. Lower priority than `OCR_SERVICE_DEV_PATH`. |

## Part D. Packaging an Installer

Run from the meta-repo root:

```bat
build_app_exe.bat
```

It cleans `bin/`, `obj/`, `packaging\staging\`, and `packaging\dist\`, then runs `dotnet publish`, copies the frontend, re-packages the OCR service with PyInstaller, and wraps everything with Inno Setup. The whole process takes a few minutes; the output is `zh-cn-to-tw-windows\packaging\dist\ZhCnToTw-Setup-<version>.exe`.

If step 3 fails three times in a row with the antivirus hint, add the `zh-cn-to-tw-ocr-service` folder to your antivirus's real-time scanning exclusions and rerun.

## Part E. Shipping a New Version

1. Change the version number to the new one in all three places (see "Version number" in the Project Report): `src/ZhCnToTw/ZhCnToTw.csproj`, `packaging/installer.iss`, and `appMajor`/`appMinor` in `src/ZhCnToTw/MainWindow.xaml.cs`.
2. Package per Part D, actually install it, and test sign-in, Stage 1, and Stage 2.
3. Commit, push, then create the Release and upload the installer (tag is `v<version>`, filename stays ASCII):

```bash
gh release create v1.7 packaging/dist/ZhCnToTw-Setup-1.7.exe --title "繁化助手 1.7（Windows）"
```

4. On `zh-cn-to-tw-web`'s `update-page` branch, add a new `.version-block` at the top of the Windows section in `index.html`, with a link of the form `https://github.com/beethoreven/zh-cn-to-tw-windows/releases/download/v<version>/ZhCnToTw-Setup-<version>.exe`. Add a block; don't rewrite existing ones — older versions stay available for rollback.
5. Back in the meta-repo, update this submodule's pointer and commit.
