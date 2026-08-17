# 中文

## zh-cn-to-tw-windows — Windows 桌面殼

「劇本殺繁化助手」的 Windows 桌面殼，角色對應 `zh-cn-to-tw-mac`：用原生殼包 WebView 內嵌 `zh-cn-to-tw-web` 的前端，本機直接跑，不透過瀏覽器。這份文件分成兩個獨立的部分，請依需求閱讀:

- **[專案報告](#專案報告)**：為什麼選這套技術、遇到什麼問題、怎麼解的
- **[架設 SOP](#架設-sop)**：怎麼在本機把這支殼跑起來

這兩部分刻意分開，不要交叉閱讀；報告是背景知識，SOP 是操作手冊。

---

## 專案報告

### 這是什麼

WPF（.NET 8）+ WebView2 的桌面殼，用固定的 `file://` 路徑載入 `zh-cn-to-tw-web` 的 `index.html`，理由跟 Mac 版完全一樣：本機 HTTP server 的 port 每次啟動不固定，會讓 `localStorage`（登入 session）的 origin 跟著變，等於每次開 App 都要重新登入；`file://` 的路徑固定，origin 穩定，登入狀態才留得住。

目前處於 Windows 版整體規劃的 Stage 1（先求殼能開起來、核心功能能跑），還沒進到 Stage 2（套件/Windows 版本相容性）、Stage 3（打包成 `.exe`）。

### 系統架構

這支 repo 是 `zh-cn-to-tw` meta-repo 底下的 submodule，跟 `zh-cn-to-tw-web`、`zh-cn-to-tw-backend` 互為 sibling：

```
zh-cn-to-tw/                       (meta-repo，本身不部署)
├── zh-cn-to-tw-backend/           (Flask API，部署在 Render)
├── zh-cn-to-tw-web/               (前端，本殼用 file:// 內嵌它)
├── zh-cn-to-tw-mac/                (macOS 桌面殼)
├── zh-cn-to-tw-ocr-service/       (本機 OCR，尚未有 Windows 版)
└── zh-cn-to-tw-windows/            (這支 repo)
    └── src/ZhCnToTw/
```

本機開發時（`dotnet run`，還沒跑過打包腳本），殼會從執行檔位置往上找，直到找到 sibling 的 `zh-cn-to-tw-web/index.html`——這個目錄結構的假設就是靠上面這張圖建立的，換句話說這支 repo 目前假設是在 `zh-cn-to-tw` meta-repo底下、跟 `zh-cn-to-tw-web` 同層被 clone 下來的。

### 為什麼選 WebView2，不是別的方案

Windows 沒有直接對應 macOS WKWebView 的原生框架。WebView2 是微軟官方方案，跟 WKWebView 概念最接近（都是「殼掌控生命週期、內容跑在 Chromium/WebKit 的內嵌引擎」），而且多數 Windows 10/11 機器已經內建或透過 Windows Update 裝好 Runtime，不像 Electron 那樣得整包 Chromium+Node.js 一起發（動輒 100MB+），也不像 Tauri 那樣需要額外的 Rust 技術棧。

Win7 不會被 Windows Update 推送 WebView2 Runtime，需要額外部署——這部分（偵測 Runtime 是否存在、離線靜默安裝 Evergreen Standalone Installer）還沒實作，屬於 Stage 2/3 待辦，見「已知限制」。

### zh-cn-to-tw-web 是為 WKWebView 寫的，這支殼怎麼跟它溝通

`zh-cn-to-tw-web` 的 `script.js`（Mac/Windows 共用的同一份前端）裡，桌面版登入、本機 OCR 控制、系統睡眠保護這幾個功能，直接呼叫 `window.webkit.messageHandlers.<name>.postMessage(...)`——這是 Safari/WebKit 專有的橋接 API，Chromium 為底的 WebView2 沒有這個全域物件，原樣執行會直接拋 `ReferenceError`。

刻意不修改 `zh-cn-to-tw-web`（那是 Mac/Windows 共用的前端，改了兩邊都要重新驗證，風險比較高）。改成在這支殼裡用 `CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync` 注入一段 shim，在頁面載入前就把 `window.webkit.messageHandlers` 這個物件補上，內部把同樣的呼叫轉送到 WebView2 原生的 `window.chrome.webview.postMessage`；殼這邊再用 `CoreWebView2.WebMessageReceived` 依訊息裡的 `channel` 欄位分派到對應的 C# 處理函式（`MainWindow.xaml.cs` 的 `OnWebMessageReceived`）。網頁那邊完全不用知道自己現在是跑在哪一種殼上面。

### Google 桌面登入：系統瀏覽器 + loopback HTTP

WKWebView/WebView2 這類內嵌瀏覽器都不是「真正的瀏覽器」，Google 會限制或降級內嵌瀏覽器裡的 OAuth 流程。這支殼跟 Mac 版（`GoogleDesktopSignIn.swift`）走同一套協定，也是 Google 官方對原生桌面 App 建議的做法（RFC 8252：OAuth 2.0 for Native Apps）：

1. 本機起一個一次性的 loopback TCP 監聽，固定在 `127.0.0.1:53682`。
2. 用系統瀏覽器開 Google 登入頁，`redirect_uri` 指到這個本機監聽的 `/callback`。
3. 使用者在真正的瀏覽器分頁完成登入，Google 用 `response_mode=form_post` 把 `id_token` POST 回這個本機監聽。
4. 解析出 token，塞回網頁既有的 `handleCredentialResponse()`，後續驗證/儲存/UI 更新完全沿用網頁原本的邏輯。

`client_id` 直接沿用 Mac 版那一組（`GoogleDesktopSignIn.cs` 裡的常數）——Google Cloud Console 的「已授權的重新導向 URI」是精確字串比對，Mac 版已經登記過 `http://127.0.0.1:53682/callback`，Windows 用同一個 port/path 不需要另外申請。

刻意不用 `HttpListener`（.NET 內建的 HTTP.SYS 封裝）：在部分 Windows 環境下，非管理員身分監聽 HTTP prefix 需要先用 `netsh` 保留 URL ACL，不該要求一般使用者為了登入先跑一次系統管理員指令。改用最原始的 `TcpListener`，手動解析最小可用的 HTTP 請求/回應——跟 Mac 版用 `NWListener` 直接處理原始 TCP 是同一個理由。

### 踩到的坑：Windows 版被誤判成「過舊的 Mac 版」要求強制更新

實測撞過：登入後畫面直接跳出「目前 App 版本過舊（最低需求版本 1.1），請先更新才能繼續使用」，但 Windows 版根本還沒有版本 1.1 這個概念。

原因：後端 `GET /api/version_check` 用 `(os, os_version)` 這組複合鍵查強制更新門檻。`zh-cn-to-tw-web` 的 `script.js` 呼叫這支 API 時，`os` 參數是寫死的 `"macos"`（Mac/Windows 共用前端的既有限制）；殼這邊原本把 `osTier` 這個查詢參數也沿用 Mac 版的 `"13+"`，圖的是避免撞到 `script.js` 裡只認 `"12-"` 這個特定字串的邊界情況（`STAGE1_LOCKED`、`legacyDownload` 分支）。但 `(os=macos, os_version=13+)` 這組鍵剛好直接命中 Mac 版真正在用的政策資料（Mac 版當時最低要求版本 1.1），Windows 端這時回報的版本號是還沒接上真正版本追蹤機制前的佔位值 `0.1`，因此被判定過舊。

修法：把 `osTier` 改成資料庫裡不存在的值 `"windows"`。後端 `app.py` 的 `version_check` 本來就設計成「查無這個 os 的門檻資料時一律不擋」（`get_policy` 回 `None` 就直接回 `force_update: false`），`(os=macos, os_version=windows)` 查無資料，问题解決，且不影响 `script.js` 裡只認 `"12-"` 的那兩處邊界判斷（用任何非 `"12-"` 的字串都行）。

這個修法是暫時解——之後 Windows 版接上自己的版本追蹤機制（`appMajor`/`appMinor` 現在也是寫死的佔位值）時，要一併檢討 `script.js` 裡 `os` 參數寫死 `"macos"` 這件事本身，以及後端 `app_versions` 表要不要新增 `os = 'windows'` 的資料列。

### 已知限制

- **OCR 服務未移植**：`zh-cn-to-tw-ocr-service` 還沒有 Windows 版執行檔。`MainWindow.xaml.cs` 的 `HandleOcrService` 目前只接住 `ocrService` channel 的訊息、印 log，不會真的啟動任何服務——Stage 1 的 PDF 上傳因此會在網頁端等 30 秒後顯示「本機 OCR 服務啟動逾時」（這是 `script.js` 本身的逾時保護，不是殼的 bug）。**Stage 2「直接上傳繁體內容」不需要 OCR，已驗證可正常運作。**
- **版本追蹤是佔位值**：`appMajor`/`appMinor`/`osTier` 目前在 `MainWindow.xaml.cs` 的 `BuildDesktopUrl` 裡是寫死的，Windows 版還沒有自己的版本追蹤機制（對應 Mac 版 `Info.plist` 的 `CFBundleShortVersionString` + 後端 `app_versions` 表）。
- **Win7/CPU 架構相容性、`.exe` 打包（含 meta-repo 根目錄的 `build_app_exe.bat`）、WebView2 Runtime 自動偵測 + 離線靜默安裝，都還沒做**，屬於 Windows 版整體規劃的 Stage 2/3，見 `zh-cn-to-tw` 的 `CLAUDE.md`。
- **WebView2 render process 掛掉時沒有完整復原 UI**：Mac 版對 WKWebView 背景 process 被系統砍掉這件事有專門處理（`webContentProcessDied`，整個換成原生提示畫面）。WebView2 對這種情況原生就比較穩健，目前只監聽 `CoreWebView2.ProcessFailed` 印 log，還沒實測過是否需要 Mac 版那種複雜度的復原 UI。

### 檔案結構

```
src/ZhCnToTw/
├── ZhCnToTw.csproj
├── App.xaml(.cs)              # 進入點
├── MainWindow.xaml(.cs)        # 主視窗、WebView2 初始化、
│                                # webkit-bridge shim 注入與分派、
│                                # 重新整理鍵
└── GoogleDesktopSignIn.cs      # 桌面版 Google 登入（loopback + 系統瀏覽器）
```

---

# 架設 SOP / Setup Guide

## Part A. 開發環境需求

1. **.NET SDK 8.0（LTS）**——用 `dotnet --version` 確認；沒有的話用 `winget install --id Microsoft.DotNet.SDK.8 --source winget` 裝。
2. 這支 repo 要跟 `zh-cn-to-tw-web` 放在同一個 `zh-cn-to-tw` meta-repo 底下（互為 sibling submodule）——本機執行時殼會自動往上找到 `zh-cn-to-tw-web/index.html` 讀取（見「系統架構」）。單獨 clone 這支 repo、旁邊沒有 `zh-cn-to-tw-web` 的話，畫面會顯示「找不到前端網頁」。

## Part B. 本機執行

```bash
cd src/ZhCnToTw
dotnet run
```

第一次執行 WebView2 會在 `%LocalAppData%\ZhCnToTw\WebView2` 建立自己的使用者資料夾（存 cookie、localStorage、登入 session），跟執行檔本身分開放，避免之後打包安裝到 `Program Files` 時因為沒有寫入權限導致啟動失敗。

## Part C. 環境變數總覽

| 變數 | 必填 | 說明 |
|---|---|---|
| `WEB_BASE_URL_OVERRIDE` | 否 | 開發階段用，指到本機另外跑的網頁伺服器（例如 `python -m http.server`），測試還沒進 sibling 目錄的前端改動。設定後殼會直接載入這個網址，不會走 `file://`。 |
| `WEB_API_BASE_OVERRIDE` | 否 | 開發階段用，指到本機另外跑的 backend（例如 `http://127.0.0.1:5001`），測試還沒部署上去的後端改動。省略時預設打正式的 `https://zh-cn-to-tw-backend.onrender.com`。 |

---

# English

## zh-cn-to-tw-windows — Windows Desktop Shell

The Windows desktop shell for the traditional-Chinese script-murder-game localization assistant, playing the same role as `zh-cn-to-tw-mac`: a native shell wrapping a WebView that embeds the `zh-cn-to-tw-web` frontend, run locally without a browser. This document has two independent parts, read whichever you need:

- **[Project Report](#project-report)**: why this stack was chosen, what went wrong, how it was fixed
- **[Setup Guide](#setup-guide)**: how to get this shell running locally

The two parts are intentionally separate; the report is background knowledge, the guide is an operations manual.

---

## Project Report

### What This Is

A WPF (.NET 8) + WebView2 desktop shell that loads `zh-cn-to-tw-web`'s `index.html` via a fixed `file://` path, for exactly the same reason as the Mac build: a local HTTP server's port changes on every launch, which changes `localStorage`'s (login session) origin every time — meaning the user would have to log in again on every app start. A fixed `file://` path keeps the origin stable, so the login session survives restarts.

This is currently Stage 1 of the overall Windows rollout plan (get the shell running with core functionality working) — Stage 2 (package/Windows-version compatibility) and Stage 3 (packaging as an `.exe`) haven't started yet.

### System Architecture

This repo is a submodule under the `zh-cn-to-tw` meta-repo, sibling to `zh-cn-to-tw-web` and `zh-cn-to-tw-backend`:

```
zh-cn-to-tw/                       (meta-repo, not deployed itself)
├── zh-cn-to-tw-backend/           (Flask API, deployed on Render)
├── zh-cn-to-tw-web/               (frontend, embedded via file:// here)
├── zh-cn-to-tw-mac/                (macOS desktop shell)
├── zh-cn-to-tw-ocr-service/       (local OCR, no Windows build yet)
└── zh-cn-to-tw-windows/            (this repo)
    └── src/ZhCnToTw/
```

During local development (`dotnet run`, before any packaging script has run), the shell walks up from the executable's location until it finds a sibling `zh-cn-to-tw-web/index.html` — this assumes the repo is checked out under the `zh-cn-to-tw` meta-repo, at the same level as `zh-cn-to-tw-web`, as shown above.

### Why WebView2, and not something else

Windows has no framework that directly corresponds to macOS's WKWebView. WebView2 is Microsoft's official answer — conceptually closest to WKWebView (a shell that owns the lifecycle, content runs inside an embedded Chromium/WebKit engine) — and most Windows 10/11 machines already have the Runtime preinstalled or delivered via Windows Update, unlike Electron (which ships an entire Chromium+Node.js bundle, often 100MB+) or Tauri (which pulls in a separate Rust toolchain).

Windows 7 does not receive the WebView2 Runtime via Windows Update and needs separate handling — detecting whether the Runtime exists and silently installing the offline Evergreen Standalone Installer isn't implemented yet; see "Known Limitations".

### zh-cn-to-tw-web was written for WKWebView — how does this shell talk to it

`zh-cn-to-tw-web`'s `script.js` (the same frontend shared by both the Mac and Windows builds) calls `window.webkit.messageHandlers.<name>.postMessage(...)` directly for desktop sign-in, local OCR control, and the system-sleep guard — this is a Safari/WebKit-only bridge API that Chromium-based WebView2 does not have; calling it as-is throws a `ReferenceError`.

`zh-cn-to-tw-web` was deliberately left unmodified (it's the frontend shared by both platforms; changing it means re-validating both). Instead, this shell injects a shim via `CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync` that defines `window.webkit.messageHandlers` before the page's own scripts run, forwarding the same calls to WebView2's native `window.chrome.webview.postMessage`; the shell then dispatches on `CoreWebView2.WebMessageReceived` based on a `channel` field to the matching C# handler (`OnWebMessageReceived` in `MainWindow.xaml.cs`). The web page never needs to know which shell it's running inside.

### Google desktop sign-in: system browser + loopback HTTP

Embedded WebViews (WKWebView or WebView2 alike) aren't "real" browsers, and Google restricts or degrades OAuth flows run inside them. This shell follows the same protocol as the Mac build (`GoogleDesktopSignIn.swift`) — also Google's own recommendation for native desktop apps (RFC 8252, OAuth 2.0 for Native Apps):

1. Start a one-shot loopback TCP listener locally, fixed at `127.0.0.1:53682`.
2. Open Google's sign-in page in the system browser, with `redirect_uri` pointing at this local listener's `/callback`.
3. The user completes sign-in in a real browser tab; Google POSTs the `id_token` back to the local listener via `response_mode=form_post`.
4. The token is parsed out and handed to the page's existing `handleCredentialResponse()` — all subsequent validation, storage, and UI updates reuse the page's existing logic unchanged.

The `client_id` is the same one the Mac build uses (a constant in `GoogleDesktopSignIn.cs`) — Google Cloud Console's "Authorized redirect URIs" is an exact string match, and the Mac build already has `http://127.0.0.1:53682/callback` registered, so Windows reuses the same port/path without needing a separate registration.

`HttpListener` (.NET's HTTP.SYS wrapper) was deliberately avoided: on some Windows setups, listening on an HTTP prefix as a non-administrator requires a `netsh` URL ACL reservation beforehand, and users shouldn't have to run an admin command just to sign in. A raw `TcpListener` is used instead, manually parsing the minimal HTTP request/response needed — the same reasoning as the Mac build's direct use of `NWListener` at the raw TCP level.

### Bug hit during testing: the Windows build was misidentified as an outdated Mac build

Observed in testing: right after signing in, the app popped a dialog saying "This app version is too old (minimum required version 1.1), please update before continuing" — but the Windows build has no concept of version 1.1 at all.

Root cause: the backend's `GET /api/version_check` looks up the forced-update threshold by the composite key `(os, os_version)`. `zh-cn-to-tw-web`'s `script.js` hardcodes `os="macos"` when calling this endpoint (a pre-existing limitation of the frontend shared across both platforms). The shell originally reused the Mac build's `"13+"` string for the `osTier` query parameter too, to avoid `script.js`'s edge-case handling that only checks for the specific string `"12-"` (the `STAGE1_LOCKED` flag and the `legacyDownload` branch). But `(os=macos, os_version=13+)` happens to be exactly the key the real Mac build's policy row uses (Mac's minimum required version was 1.1 at the time), and the Windows build was reporting the placeholder version `0.1` (since it doesn't have real version tracking wired up yet) — so it got flagged as outdated.

Fix: change `osTier` to `"windows"`, a value with no row in the database. The backend's `version_check` was already designed so that "no threshold data for this os means never block" (`get_policy` returning `None` short-circuits to `force_update: false`), so `(os=macos, os_version=windows)` finds no row and the problem goes away — without breaking either of `script.js`'s two checks that only match the literal `"12-"` string.

This fix is a stopgap — once the Windows build gets real version tracking (`appMajor`/`appMinor` are also placeholders right now), `script.js`'s hardcoded `os="macos"` needs to be revisited too, along with whether the backend's `app_versions` table should gain rows for `os = 'windows'`.

### Known Limitations

- **OCR service not ported**: `zh-cn-to-tw-ocr-service` has no Windows executable yet. `HandleOcrService` in `MainWindow.xaml.cs` currently only logs messages on the `ocrService` channel without starting any actual service — Stage 1's PDF upload will therefore show "Local OCR service startup timed out" after `script.js`'s own 30-second timeout (this is the page's own timeout guard, not a shell bug). **Stage 2's "upload traditional-Chinese content directly" path needs no OCR and has been verified working.**
- **Version tracking is placeholder data**: `appMajor`/`appMinor`/`osTier` are hardcoded in `BuildDesktopUrl` in `MainWindow.xaml.cs`; the Windows build has no real version-tracking mechanism yet (the counterpart to the Mac build's `Info.plist` `CFBundleShortVersionString` plus the backend's `app_versions` table).
- **Win7/CPU-architecture compatibility, `.exe` packaging (including a `build_app_exe.bat` at the meta-repo root), and WebView2 Runtime auto-detection with silent offline install are all not implemented yet** — these are Stage 2/3 of the overall Windows rollout plan; see `zh-cn-to-tw`'s `CLAUDE.md`.
- **No full recovery UI for a crashed WebView2 render process**: the Mac build has dedicated handling for WKWebView's background process being killed by the system (`webContentProcessDied`, swapping in a native placeholder screen). WebView2 is inherently more resilient to this; currently only `CoreWebView2.ProcessFailed` is logged, and it hasn't been tested in practice whether the Mac build's level of recovery-UI complexity is actually needed here.

### File Structure

```
src/ZhCnToTw/
├── ZhCnToTw.csproj
├── App.xaml(.cs)              # Entry point
├── MainWindow.xaml(.cs)         # Main window, WebView2 init,
│                                # webkit-bridge shim injection/dispatch,
│                                # reload button
└── GoogleDesktopSignIn.cs      # Desktop Google sign-in (loopback + system browser)
```

---

# Setup Guide

## Part A. Development Environment Requirements

1. **.NET SDK 8.0 (LTS)** — check with `dotnet --version`; if missing, install with `winget install --id Microsoft.DotNet.SDK.8 --source winget`.
2. This repo must sit alongside `zh-cn-to-tw-web` under the same `zh-cn-to-tw` meta-repo (sibling submodules) — at runtime the shell walks upward to find `zh-cn-to-tw-web/index.html` (see "System Architecture"). If this repo is cloned standalone without `zh-cn-to-tw-web` next to it, the app will show "Frontend page not found".

## Part B. Running Locally

```bash
cd src/ZhCnToTw
dotnet run
```

On first run, WebView2 creates its own user data folder at `%LocalAppData%\ZhCnToTw\WebView2` (cookies, localStorage, login session), kept separate from the executable itself — this avoids startup failures from missing write permissions once the app is later installed under `Program Files`.

## Part C. Environment Variables

| Variable | Required | Description |
|---|---|---|
| `WEB_BASE_URL_OVERRIDE` | No | Development only — points to a local web server (e.g. `python -m http.server`), for testing frontend changes not yet in the sibling directory. When set, the shell navigates to this URL directly instead of `file://`. |
| `WEB_API_BASE_OVERRIDE` | No | Development only — points to a local backend (e.g. `http://127.0.0.1:5001`), for testing backend changes not yet deployed. Defaults to the production `https://zh-cn-to-tw-backend.onrender.com` when unset. |
