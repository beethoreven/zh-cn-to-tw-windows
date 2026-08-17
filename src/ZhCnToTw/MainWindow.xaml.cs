using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace ZhCnToTw;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    // zh-cn-to-tw-web 的 script.js 是針對 WKWebView 寫的：desktopSignIn/
    // ocrService/activityGuard/legacyDownload 這幾個管道全部直接呼叫
    // window.webkit.messageHandlers.<name>.postMessage(...)，這是
    // Safari/WebKit 專有的橋接 API，Chromium 為底的 WebView2 沒有這個
    // 全域物件。刻意不改 zh-cn-to-tw-web（那是 Mac/Windows 共用的前端，
    // 改了要兩邊都重新驗證），改成在殼這邊注入一段 shim，把同樣的呼叫
    // 轉送到 WebView2 原生的 window.chrome.webview.postMessage，殼再用
    // WebMessageReceived 依 channel 分派——網頁那邊完全不用知道自己現在
    // 是跑在哪一種殼上面。
    private const string BridgeShimScript = """
        (function () {
          if (window.webkit && window.webkit.messageHandlers) return;
          function handler(channel) {
            return {
              postMessage: function (body) {
                window.chrome.webview.postMessage({ channel: channel, body: body });
              }
            };
          }
          window.webkit = {
            messageHandlers: {
              consoleLog: handler('consoleLog'),
              desktopSignIn: handler('desktopSignIn'),
              ocrService: handler('ocrService'),
              activityGuard: handler('activityGuard'),
              legacyDownload: handler('legacyDownload')
            }
          };
        })();
        """;

    // 桌面版登入用系統瀏覽器（見 GoogleDesktopSignIn 的說明），跟頁面上
    // 其他常駐狀態一樣掛在 Window 生命週期上，避免使用者按了登入之後
    // 這個物件被提早回收。
    private GoogleDesktopSignIn? _activeSignIn;

    private readonly OcrServiceManager _ocrServiceManager = new(OcrServiceManager.ResolveExecutable);

    public MainWindow()
    {
        InitializeComponent();
        _ocrServiceManager.StateChanged += () => Dispatcher.Invoke(PushOcrPort);
        Loaded += async (_, _) => await InitializeWebViewAsync();
        // App 關閉時務必把 OCR 子行程收乾淨——雖然 ProcessJobObject 已經
        // 提供「殼被系統砍掉」那種異常情況下的保險，但正常關閉時應該
        // 直接主動關，不用等作業系統層級的機制介入。
        Closed += (_, _) => _ocrServiceManager.Stop();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            // 跟 Mac 版一樣，把 WebView2 的使用者資料夾放到 %LocalAppData%，不要
            // 用預設值（緊鄰執行檔的資料夾）。這支殼之後打包上線會被裝到
            // Program Files 底下，一般使用者帳號對那裡沒有寫入權限，
            // CoreWebView2 建立 profile 時會直接失敗。
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZhCnToTw", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await Browser.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex)
        {
            // 這裡掛在 Loaded 的 async void lambda 底下——沒有這層
            // try/catch 的話，任何初始化失敗（最現實的情境：機器上沒裝
            // WebView2 Runtime，見 README「已知限制」，偵測/靜默安裝是
            // Stage 2/3 才要做的事）都會變成未處理例外，整個 App 直接
            // 無預警崩潰，使用者只會看到視窗憑空消失，完全不知道發生
            // 什麼事。先攔下來給一個看得懂的訊息，好過一句話都沒有的
            // 崩潰。
            System.Diagnostics.Trace.WriteLine($"[webview-init] 初始化失敗：{ex}");
            MessageBox.Show(
                $"無法啟動內嵌瀏覽器元件（WebView2），App 無法繼續執行。\n\n" +
                $"可能是這台機器還沒安裝 Microsoft Edge WebView2 Runtime。\n\n錯誤訊息：{ex.Message}",
                "初始化失敗", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        var core = Browser.CoreWebView2;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeShimScript);
        core.WebMessageReceived += OnWebMessageReceived;
        // 對應 Mac 版 webViewWebContentProcessDidTerminate：背後的
        // render process 被系統砍掉時的通知。WebView2 對這種狀況比
        // WKWebView 穩健，通常直接 Reload() 就能恢復，先不做到 Mac 版
        // 那種整個物件拆掉重建、換原生提示畫面的複雜度，只留 log
        // 方便之後如果真的觀察到需要再加。
        core.ProcessFailed += (_, args) =>
            System.Diagnostics.Trace.WriteLine($"[webview-process-failed] {args.ProcessFailedKind}：{args.Reason}");
        // 頁面（重新）載好之後，window 是全新的，之前推進去的
        // window.__OCR_PORT__ 會跟著消失——對應 Mac 版 WebView.swift 的
        // didFinish 補推邏輯，每次導覽完成都重新推一次目前最新的值。
        core.NavigationCompleted += (_, _) => PushOcrPort();
        // 下載（Stage 1/2 的「下載結果」按鈕，走 fetch+blob+<a download>
        // 這條標準路徑，WebView2 原生就認得）預設會存到系統的「下載」
        // 資料夾，改成存到桌面——ResultFilePath 進來時已經是 WebView2
        // 自己組好的「下載資料夾 + 建議檔名」路徑，這裡只取檔名部分，
        // 換掉目錄；檔名沿用既有的 UniqueDestination 邏輯避免覆蓋掉
        // 使用者前一次下載的同名檔案。
        core.DownloadStarting += (_, args) =>
        {
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var suggestedName = Path.GetFileName(args.ResultFilePath);
            args.ResultFilePath = UniqueDestination(desktopDir, suggestedName);
        };

        var (kind, target) = ResolveWebSource();
        switch (kind)
        {
            case WebSourceKind.LocalIndexHtml:
            case WebSourceKind.RemoteOverride:
                // WebView2（Chromium）可以直接用 file:// 導覽到本機 HTML，
                // 相對路徑引用的同目錄 script.js/style.css 會正常運作，
                // 不需要像 WKWebView 那樣額外呼叫 allowingReadAccessTo 之類
                // 的 API 明確授權整個目錄。
                Browser.Source = BuildDesktopUrl(target);
                break;
            case WebSourceKind.NotFound:
                Browser.NavigateToString(
                    "<html><body style='font-family:sans-serif;padding:2em'>"
                    + "<h2>找不到前端網頁</h2><p>"
                    + "沒有設定 WEB_BASE_URL_OVERRIDE，也找不到 web/ 資料夾或"
                    + " sibling 的 zh-cn-to-tw-web 目錄。</p></body></html>");
                break;
        }
    }

    /// <summary>
    /// 把 base URL 接上 ?desktop=1&amp;... 這些查詢參數，對應 Mac 版
    /// ContentView.desktopURL()。ocrToken 一定要用 _ocrServiceManager.Token
    /// 這個真正的值（早期版本這裡曾經誤用另外產生的隨機值，跟
    /// OCR_SERVICE_TOKEN 傳給子行程的值對不上，本機 OCR 服務的
    /// X-OCR-Token 驗證會全部被拒絕）；apiBase 預設打正式 Render
    /// 後端，WEB_API_BASE_OVERRIDE 只在開發階段用來指到本機另外跑的
    /// backend。appMajor/appMinor 目前寫死成跟 Mac 版同步的版本號（每次
    /// Mac 版出新版、Windows 版跟著補上功能對等的改動時一起手動更新），
    /// 不是真正的版本追蹤機制——Windows 版還沒有自己的 build 編號/發布
    /// 流程，之後要接上真正的版本追蹤時再改（見 zh-cn-to-tw-backend 的
    /// app_versions 表）。
    ///
    /// osTier 刻意不能沿用 Mac 版的 "13+"：zh-cn-to-tw-web 的
    /// script.js 呼叫 /api/version_check 時 os 參數是寫死的
    /// "macos"（共用前端本身的既有限制，這支殼不修改那份程式碼），
    /// 如果 osTier 也用 "13+"，(os=macos, os_version=13+) 這組查詢鍵
    /// 會直接對上 Mac 版真正在用的那筆政策資料——appMajor/appMinor
    /// 這裡的佔位值一定比 Mac 版真正的版本號舊，於是被誤判成「Mac 版
    /// 版本過舊要求強制更新」（實測撞過：畫面跳出「請更新到 1.1」，
    /// 但 Windows 版根本還沒有 1.1 這個概念）。改用 "windows" 這個
    /// 後端資料庫裡不存在的值，(os=macos, os_version=windows) 查無
    /// 政策資料，get_policy 回 None，version_check 一律回報
    /// force_update=false（見 app.py 的說明：「查無這個 os 的門檻
    /// 資料時一律不擋」）。之後 Windows 版有自己的版本追蹤機制時，
    /// 這裡跟 script.js 的 os=macos 硬編碼都要一併檢討。
    /// </summary>
    private Uri BuildDesktopUrl(string baseUrl)
    {
        var apiBase = Environment.GetEnvironmentVariable("WEB_API_BASE_OVERRIDE")
            ?? "https://zh-cn-to-tw-backend.onrender.com";
        var separator = baseUrl.Contains('?') ? '&' : '?';
        var query = string.Join("&",
            "desktop=1",
            $"ocrToken={Uri.EscapeDataString(_ocrServiceManager.Token)}",
            $"apiBase={Uri.EscapeDataString(apiBase)}",
            "appMajor=1",
            "appMinor=3",
            "osTier=windows");
        return new Uri($"{baseUrl}{separator}{query}");
    }

    /// <summary>
    /// 把最新的 OCR port 寫進頁面的 window.__OCR_PORT__（見
    /// zh-cn-to-tw-web 的 script.js 的 desktopOcrBase()）。刻意不重新
    /// 載入頁面——服務自己開開關關是背景行為，不該把使用者做到一半的
    /// 工作狀態清掉。一定要在 UI 執行緒上呼叫（OcrServiceManager 的
    /// StateChanged 可能從背景執行緒觸發，建構子裡已經用
    /// Dispatcher.Invoke 包過）。
    /// </summary>
    private void PushOcrPort()
    {
        if (Browser.CoreWebView2 is null) return;
        var value = _ocrServiceManager.Port?.ToString() ?? "null";
        _ = Browser.CoreWebView2.ExecuteScriptAsync($"window.__OCR_PORT__ = {value};");
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        Browser.CoreWebView2?.Reload();
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var channel = root.GetProperty("channel").GetString();
            var body = root.TryGetProperty("body", out var b) ? b : default;

            switch (channel)
            {
                case "consoleLog":
                    System.Diagnostics.Trace.WriteLine($"[webview-console] {body}");
                    break;
                case "desktopSignIn":
                    await HandleDesktopSignInAsync();
                    break;
                case "ocrService":
                    HandleOcrService(body);
                    break;
                case "activityGuard":
                    HandleActivityGuard(body);
                    break;
                case "legacyDownload":
                    HandleLegacyDownload(body);
                    break;
                default:
                    System.Diagnostics.Trace.WriteLine($"[webview-bridge] 不認得的 channel：{channel}");
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[webview-bridge] 處理訊息失敗：{ex}");
        }
    }

    private async Task HandleDesktopSignInAsync()
    {
        var signIn = new GoogleDesktopSignIn();
        _activeSignIn = signIn;
        try
        {
            var idToken = await signIn.StartAsync();
            var escaped = idToken.Replace("'", "\\'");
            // 網頁原本 Google Identity Services 登入成功時就是呼叫這個
            // 函式（見 script.js 的 handleCredentialResponse）——桌面版
            // 系統瀏覽器登入完成後只是換一種方式把 token 交回網頁，後續
            // 驗證/儲存/UI 更新完全沿用網頁原本的邏輯。
            await Browser.CoreWebView2.ExecuteScriptAsync($"handleCredentialResponse({{credential: '{escaped}'}})");
            Activate();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[desktop-signin] 失敗：{ex}");
            var message = ex.Message.Replace("'", "\\'");
            await Browser.CoreWebView2.ExecuteScriptAsync(
                $"window.alert && window.alert('登入失敗，請重試（{message}）')");
        }
        finally
        {
            _activeSignIn = null;
        }
    }

    private void HandleOcrService(JsonElement body)
    {
        var action = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("action", out var a)
            ? a.GetString()
            : null;
        switch (action)
        {
            case "start":
                _ocrServiceManager.EnsureRunning();
                break;
            case "stop":
                _ocrServiceManager.Stop();
                PushOcrPort();
                break;
            default:
                System.Diagnostics.Trace.WriteLine($"[ocr-service] 不認得的指令：{action}");
                break;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    /// <summary>
    /// 對應 Mac 版 SystemActivityGuard：Stage 1/2 有工作在跑時擋掉系統
    /// 睡眠，工作結束或殼關閉時解除。Windows 上對應的 API 是
    /// SetThreadExecutionState——EsContinuous 讓這個狀態持續生效直到
    /// 下一次呼叫（不是只擋一次），不加 EsDisplayRequired 是因為只需要
    /// 系統不要睡眠、不需要螢幕保持不關閉。
    /// </summary>
    private static void HandleActivityGuard(JsonElement body)
    {
        var action = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("action", out var a)
            ? a.GetString()
            : null;
        switch (action)
        {
            case "start":
                SetThreadExecutionState(EsContinuous | EsSystemRequired);
                break;
            case "stop":
                SetThreadExecutionState(EsContinuous);
                break;
            default:
                System.Diagnostics.Trace.WriteLine($"[activity-guard] 不認得的指令：{action}");
                break;
        }
    }

    /// <summary>
    /// 對應 Mac 版 12- 分流的 legacyDownload channel：網頁把檔案內容轉成
    /// base64 直接 postMessage 過來，這裡解碼寫進桌面。script.js 只有在
    /// osTier === "12-" 才會走這條路，這支殼固定回報 "windows"（見
    /// BuildDesktopUrl），正常情況下載會走 DownloadStarting 那條標準
    /// 路徑，不會用到這裡——這裡實作只是求完整、當作備援，不是目前預期
    /// 會被觸發的路徑。
    /// </summary>
    private static void HandleLegacyDownload(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("filename", out var filenameEl)
            || !body.TryGetProperty("base64", out var base64El))
        {
            System.Diagnostics.Trace.WriteLine("[legacy-download] 收到的訊息格式不對");
            return;
        }

        var filename = filenameEl.GetString() ?? "download";
        try
        {
            var data = Convert.FromBase64String(base64El.GetString() ?? "");
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var destination = UniqueDestination(desktopDir, filename);
            File.WriteAllBytes(destination, data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[legacy-download] 寫檔失敗：{ex}");
        }
    }

    private static string UniqueDestination(string directory, string filename)
    {
        var ext = Path.GetExtension(filename);
        var baseName = Path.GetFileNameWithoutExtension(filename);
        var candidate = Path.Combine(directory, filename);
        var suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{baseName} ({suffix}){ext}");
            suffix++;
        }
        return candidate;
    }

    private enum WebSourceKind { RemoteOverride, LocalIndexHtml, NotFound }

    /// <summary>
    /// 決定要載入哪個網頁來源，優先順序對應 Mac 版 ContentView.swift 的
    /// webURL：
    ///   1. WEB_BASE_URL_OVERRIDE 環境變數 -- 開發階段用，指到本機另外跑的
    ///      網頁伺服器（例如 python3 -m http.server），測試還沒進打包產物
    ///      的前端改動。
    ///   2. 執行檔旁邊的 web/ 資料夾 -- Stage 3 的 build 腳本會把
    ///      zh-cn-to-tw-web 的內容複製到這裡，對應 Mac 版 .app 內的
    ///      Contents/Resources/web/。
    ///   3. 從執行檔位置往上找，直到某層目錄旁邊有 sibling 的
    ///      zh-cn-to-tw-web/index.html -- 純本機開發、還沒跑過打包腳本時
    ///      的退路，對應這個 meta-repo 底下 zh-cn-to-tw-windows 跟
    ///      zh-cn-to-tw-web 互為 sibling submodule 的目錄結構。
    /// </summary>
    private static (WebSourceKind, string) ResolveWebSource()
    {
        var overrideUrl = Environment.GetEnvironmentVariable("WEB_BASE_URL_OVERRIDE");
        if (!string.IsNullOrEmpty(overrideUrl))
        {
            return (WebSourceKind.RemoteOverride, overrideUrl);
        }

        var bundledIndex = Path.Combine(AppContext.BaseDirectory, "web", "index.html");
        if (File.Exists(bundledIndex))
        {
            return (WebSourceKind.LocalIndexHtml, new Uri(bundledIndex).AbsoluteUri);
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "zh-cn-to-tw-web", "index.html");
            if (File.Exists(candidate))
            {
                return (WebSourceKind.LocalIndexHtml, new Uri(candidate).AbsoluteUri);
            }
            dir = dir.Parent;
        }

        return (WebSourceKind.NotFound, string.Empty);
    }
}
