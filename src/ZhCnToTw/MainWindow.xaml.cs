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
    // ocrService/activityGuard 這幾個管道全部直接呼叫
    // window.webkit.messageHandlers.<name>.postMessage(...)，這是
    // Safari/WebKit 專有的橋接 API，Chromium 為底的 WebView2 沒有這個
    // 全域物件。刻意不改 zh-cn-to-tw-web（那是 Mac/Windows 共用的前端，
    // 改了要兩邊都重新驗證），改成在殼這邊注入一段 shim，把同樣的呼叫
    // 轉送到 WebView2 原生的 window.chrome.webview.postMessage，殼再用
    // WebMessageReceived 依 channel 分派——網頁那邊完全不用知道自己現在
    // 是跑在哪一種殼上面。
    //
    // 只補這個殼真的會處理的管道。legacyDownload（網頁把檔案內容交給殼
    // 寫檔）是 Mac 10.15 那包才需要的：這裡的下載走 WebView2 自己的
    // DownloadStarting，用不到——讓網頁直接叫殼寫檔的管道，沒有需要就不開。
    // 網頁用「這個管道存不存在」判斷能不能用，這裡不補，它就不會去呼叫。
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
              activityGuard: handler('activityGuard')
            }
          };
        })();
        """;

    // 網頁前端的固定來源（https://zh-cn-to-tw.example），見
    // InitializeWebViewAsync 的說明。.example 是 RFC 6761 保留給範例用
    // 的頂級網域，保證不會有真實網站；也刻意不用 .local（WebView2 文件
    // 註明會讓導覽變慢）。改這個值等於換來源：localStorage 裡的登入狀態
    // 會全部讀不到，而且 zh-cn-to-tw-backend 與 zh-cn-to-tw-ocr-service
    // 的 CORS 允許清單都要跟著改。
    private const string WebVirtualHost = "zh-cn-to-tw.example";

    // 桌面版登入用系統瀏覽器（見 GoogleDesktopSignIn 的說明），跟頁面上
    // 其他常駐狀態一樣掛在 Window 生命週期上，避免使用者按了登入之後
    // 這個物件被提早回收。
    private GoogleDesktopSignIn? _activeSignIn;

    // 殼自己載入的那一頁的網址。網頁送來的訊息，只收來源跟它相同的，
    // 見 IsFromOwnPage。
    private Uri? _pageUri;

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
#if !DEBUG
        // 正式版關掉 DevTools（F12、右鍵「檢查」）：它能讀到 localStorage 裡
        // 的登入 session、在頁面裡執行任意程式，同一台電腦上的其他人都能
        // 打開。開發時（Debug 設定，例如 dotnet run）照常可以用。
        core.Settings.AreDevToolsEnabled = false;
#endif
        // 只在這個視窗裡打開自己頁面的來源（見 IsAllowedInAppNavigation）。
        // 這個殼沒有網址列，任何網站在這裡打開，使用者都分不出那是不是這個
        // App 的畫面。使用者自己點的 http(s) 連結改用系統瀏覽器開，其他
        // （頁面程式改網址、轉址）直接擋下。
        core.NavigationStarting += (_, args) =>
        {
            if (IsAllowedInAppNavigation(args.Uri, _pageUri)) return;
            args.Cancel = true;
            if (args.IsUserInitiated && IsWebUrl(args.Uri))
            {
                OpenInSystemBrowser(args.Uri);
            }
            else
            {
                System.Diagnostics.Trace.WriteLine("[webview-nav] 擋下導覽到其他來源");
            }
        };
        // 網頁要開新視窗（window.open()、target="_blank"）時一律不在 App 裡開：
        // 使用者自己點的 http(s) 連結交給系統瀏覽器，其他直接忽略。Handled 設成
        // true、又不給 NewWindow，WebView2 就不會開出新視窗。
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (args.IsUserInitiated && IsWebUrl(args.Uri))
            {
                OpenInSystemBrowser(args.Uri);
            }
        };
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
        //
        // 載入失敗時一定要留 log：WebView2 導覽失敗不會拋例外，畫面只是
        // 停在錯誤頁或空白，沒有這行就完全不知道原因（Mac 版 loadFileURL
        // 實際吃過這個虧：錯誤只會送到沒註冊的回呼，什麼都看不到）。2.0 改成虛擬
        // 主機對應之後多了資料夾對應這一層可能出錯的地方，更需要它。
        core.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[webview-nav] 載入失敗：{args.WebErrorStatus}（HTTP {args.HttpStatusCode}）{core.Source}");
            }
            PushOcrPort();
        };
        // 下載（Stage 1/2 的「下載結果」按鈕，走 fetch+blob+<a download>
        // 這條標準路徑，WebView2 原生就認得）預設會存到系統的「下載」
        // 資料夾，改成存到桌面——ResultFilePath 進來時已經是 WebView2
        // 自己組好的「下載資料夾 + 建議檔名」路徑，這裡只取檔名部分，
        // 換掉目錄；檔名沿用既有的 UniqueDestination 邏輯避免覆蓋掉
        // 使用者前一次下載的同名檔案。
        //
        // 只存 .txt／.docx（見 IsAllowedDownloadName），其他的取消下載，
        // 並請網頁顯示一則提示，不然使用者按了下載卻什麼都沒發生。
        core.DownloadStarting += (_, args) =>
        {
            var suggestedName = Path.GetFileName(args.ResultFilePath);
            if (!IsAllowedDownloadName(suggestedName))
            {
                // 不印出檔名：它是網頁給的，不是殼自己產生的內容
                System.Diagnostics.Trace.WriteLine("[webview-download] 檔名不能用或不是 .txt／.docx，取消下載");
                args.Cancel = true;
                _ = core.ExecuteScriptAsync(
                    "window.showToast && window.showToast(\"下載失敗：檔名不合法，或不是 .txt／.docx 檔\", \"error\")");
                return;
            }
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            args.ResultFilePath = UniqueDestination(desktopDir, suggestedName);
        };

        var (kind, target) = ResolveWebSource();
        switch (kind)
        {
            case WebSourceKind.LocalFolder:
                // 2.0 起不再用 file:// 載入，改用虛擬主機對應：把網頁資料夾
                // 對應到固定的 https://zh-cn-to-tw.example/，頁面因此有一個
                // 真正的來源。
                //
                // 為什麼要換：file:// 頁面在瀏覽器眼中沒有來源（Origin 是
                // "null"），ES module 的 import 會被 CORS 擋下，前端沒辦法
                // 拆成多個模組檔案；執行期間用 fetch 讀同一份安裝裡的其他
                // 檔案也一律被擋。
                //
                // 當初選 file:// 是為了「來源固定」：更早之前用本機 HTTP
                // 伺服器供應網頁，port 每次啟動都不一樣，localStorage 跟著
                // 換來源，每次開 App 都要重新登入（localStorage 是照來源
                // 分開存的，來源含 port）。虛擬主機沒有 port、名稱寫死，來源一樣固定，這個
                // 問題不會回來。代價只有一次：從 1.x 升級上來時，舊的
                // file:// 來源存的登入狀態讀不到，要重新登入一次。
                //
                // Deny：只有這個來源自己的頁面能讀這個資料夾，其他網站
                // 一律不行。頁面自己的 script/style/module 都是同源請求，
                // 不受影響（WebView2 文件建議「用不到跨來源存取就用 Deny」）。
                //
                // 已知的未來風險：Chromium 的 Local Network Access（LNA）會
                // 限制網頁連到 127.0.0.1，本機 OCR 服務正好在那裡。WebView2
                // 目前整個關掉 LNA，微軟預告之後會用新的 PermissionKind 讓
                // 殼自己決定放不放行；哪天真的啟用了，要在這裡替這個來源
                // 開權限。
                //
                // try/catch 跟上面初始化 WebView2 那段同一個理由：這裡在
                // Loaded 的 async void lambda 底下，對應失敗（例如資料夾路徑
                // 超過 MAX_PATH）沒攔的話是未處理例外，視窗會憑空消失。
                try
                {
                    core.SetVirtualHostNameToFolderMapping(
                        WebVirtualHost, target, CoreWebView2HostResourceAccessKind.Deny);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[webview-init] 虛擬主機對應失敗：{ex}");
                    Browser.NavigateToString(
                        "<html><body style='font-family:sans-serif;padding:2em'>"
                        + "<h2>無法載入前端網頁</h2><p>網頁資料夾對應失敗："
                        + System.Net.WebUtility.HtmlEncode(ex.Message)
                        + "</p></body></html>");
                    break;
                }
                await ClearDiskCacheAsync(core);
                _pageUri = BuildDesktopUrl($"https://{WebVirtualHost}/index.html");
                Browser.Source = _pageUri;
                break;
            case WebSourceKind.RemoteOverride:
                _pageUri = BuildDesktopUrl(target);
                Browser.Source = _pageUri;
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
    /// backend。appMajor/appMinor 是這個 App 的版本號（跟 Mac 版同步），
    /// 寫死在這裡、ZhCnToTw.csproj、packaging/installer.iss 三個地方，
    /// 出新版要一起改。
    ///
    /// osTier=windows：網頁看到這個值，版本檢查會送
    /// os=windows&amp;os_version=default，查 zh-cn-to-tw-backend 的
    /// app_versions 裡 ('windows','default') 那一列（見 zh-cn-to-tw-web 的
    /// js/core/version.js）。不能沿用 Mac 版的分流值（11+、10.15）：那樣
    /// 查到的是 Mac 版的門檻，Windows 會被 Mac 的強制更新擋下（實測撞過：
    /// 畫面跳出「請更新到 1.1」，但 Windows 版當時根本還沒有 1.1）。
    /// 更早的 Windows 版前端送的是 os=macos&amp;os_version=windows，後端還
    /// 留著那一列給它們。
    /// </summary>
    private Uri BuildDesktopUrl(string baseUrl)
    {
        var apiBase = DevelopmentOverride("WEB_API_BASE_OVERRIDE")
            ?? "https://zh-cn-to-tw-backend.onrender.com";
        var separator = baseUrl.Contains('?') ? '&' : '?';
        var query = string.Join("&",
            "desktop=1",
            $"ocrToken={Uri.EscapeDataString(_ocrServiceManager.Token)}",
            $"apiBase={Uri.EscapeDataString(apiBase)}",
            "appMajor=2",
            "appMinor=3",
            "osTier=windows");
        return new Uri($"{baseUrl}{separator}{query}");
    }

    /// <summary>
    /// 每次啟動、載入網頁之前清掉 WebView2 的 HTTP 磁碟快取。
    ///
    /// file:// 載入的檔案不會進 HTTP 快取，改成 https 虛擬主機之後就會了：
    /// App 更新、web/ 資料夾換成新版的檔案之後，WebView2 可能還拿快取裡的
    /// 舊 script.js/style.css 出來用，畫面跑舊版程式而且看不出來（Mac 版
    /// 以前載入線上網址時實際撞過同一件事，見 zh-cn-to-tw-mac 的
    /// WebView.swift 的 reloadIgnoringLocalCacheData）。這個 App 的快取
    /// 裡只有自己的網頁檔案，API 回應本來就不快取，每次清掉沒有實際成本。
    ///
    /// 只清磁碟快取，localStorage（登入狀態）是另一種資料，不受影響。
    /// 清不掉也不擋啟動：最壞情況只是可能吃到舊快取，好過整個 App 打不開。
    /// </summary>
    private static async Task ClearDiskCacheAsync(CoreWebView2 core)
    {
        try
        {
            await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[webview-init] 清除磁碟快取失敗：{ex}");
        }
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

    /// <summary>
    /// 這則訊息是不是殼自己載入的那一頁送來的：來源（scheme、主機、port）
    /// 要跟殼載入的網址一樣。
    ///
    /// 訊息能做的事（開始登入、啟動 OCR）只該給殼自己的頁面用；WebView
    /// 萬一被帶到別的網站，那個網站一樣呼叫得到
    /// window.chrome.webview.postMessage，所以每一則都要看是誰送的。
    /// 依 WebView2 的文件，CoreWebView2.WebMessageReceived 只會收到最外層
    /// 文件送的訊息（iframe 送的走 CoreWebView2Frame 自己的事件，這個殼
    /// 沒有訂閱），所以這裡只需要比來源。
    /// </summary>
    private bool IsFromOwnPage(string? source)
    {
        if (_pageUri is null || string.IsNullOrEmpty(source)) return false;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var senderUri)) return false;
        return string.Equals(senderUri.Scheme, _pageUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(senderUri.Host, _pageUri.Host, StringComparison.OrdinalIgnoreCase)
            && senderUri.Port == _pageUri.Port;
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (!IsFromOwnPage(e.Source))
            {
                System.Diagnostics.Trace.WriteLine("[webview-bridge] 略過不是自己頁面送來的訊息");
                return;
            }

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
            var signedIn = await signIn.StartAsync();
            // 網頁原本 Google Identity Services 登入成功時就是呼叫這個
            // 函式（見 js/auth/login.js 的 handleCredentialResponse）——桌面版
            // 系統瀏覽器登入完成後只是換一種方式把 token 交回網頁，後續
            // 驗證/儲存/UI 更新完全沿用網頁原本的邏輯。nonce 是這次登入的
            // 原值，網頁會跟 token 一起送給後端（見 GoogleDesktopSignIn 的
            // StartAsync）。
            //
            // token 是從本機那個 port 收進來的內容，放進 JS 之前一律用
            // JsonSerializer 轉成字串字面值（跟下面失敗那條路同一個做法），
            // 不自己跳脫字元：自己跳脫只要漏掉一種字元，字串就會在預期
            // 之外的地方結束，後面的內容被當成程式執行。GoogleDesktopSignIn
            // 已經只放行 JWT 形狀的值，這裡是第二道。
            var tokenLiteral = JsonSerializer.Serialize(signedIn.IdToken);
            var nonceLiteral = JsonSerializer.Serialize(signedIn.Nonce);
            await Browser.CoreWebView2.ExecuteScriptAsync(
                $"handleCredentialResponse({{credential: {tokenLiteral}, nonce: {nonceLiteral}}})");
            Activate();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[desktop-signin] 失敗：{ex}");
            // 一定要通知網頁、不能只跳 alert：網頁如果是因為工作途中登入過期
            // 才請殼登入，它正卡在「等重新登入」，只有殼回報結果才出得來（見
            // 網頁 js/core/api.js 的 handleDesktopSignInFailed）。
            // JsonSerializer 產生的是合法的 JS 字串字面值，訊息裡有引號也安全。
            var literal = JsonSerializer.Serialize(ex.Message);
            await Browser.CoreWebView2.ExecuteScriptAsync(
                $"window.__desktopSignInFailed ? window.__desktopSignInFailed({literal})"
                + $" : (window.alert && window.alert('登入失敗，請重試（' + {literal} + '）'))");
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
    /// 開發用的環境變數（例如 WEB_API_BASE_OVERRIDE）。只有 Debug 版才讀；
    /// 正式版（安裝檔用 Release 設定編譯，見 packaging/build_installer.bat）
    /// 一律當作沒設。這些變數能讓 App 連到別的後端、載入別的網頁、執行別的
    /// 程式，對裝好的 App 來說等於多一個讓別人改變它行為的入口。
    /// </summary>
    internal static string? DevelopmentOverride(string name)
    {
#if DEBUG
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? null : value;
#else
        return null;
#endif
    }

    /// <summary>
    /// 這個網址能不能在 App 的視窗裡打開：跟殼載入的頁面同來源（scheme、
    /// 主機、port），或是 about:／data:（殼自己用 NavigateToString 顯示的
    /// 錯誤頁），或是自己頁面產生的 blob: 網址（blob:https://zh-cn-to-tw.example/...，
    /// 來源寫在 blob: 後面）。
    /// </summary>
    private static bool IsAllowedInAppNavigation(string? uri, Uri? page)
    {
        if (string.IsNullOrEmpty(uri)) return false;
        // about:／data: 用字串判斷，不交給 Uri 解析：Uri 有長度上限，太長的
        // data: 網址會解析失敗，殼自己的錯誤頁就會被擋掉
        if (uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (page is null) return false;
        if (uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(uri.Substring("blob:".Length), UriKind.Absolute, out var inner)
                && IsSameOrigin(inner, page);
        }
        return Uri.TryCreate(uri, UriKind.Absolute, out var target) && IsSameOrigin(target, page);
    }

    private static bool IsSameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    private static bool IsWebUrl(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var target)
        && (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps);

    private static void OpenInSystemBrowser(string uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[webview-nav] 無法用系統瀏覽器開啟：{ex.Message}");
        }
    }

    // 這個 App 只會存這兩種檔案（網頁那邊也只收這兩種，見 zh-cn-to-tw-web
    // 的 js/core/filename.js）。殼再擋一次，網頁萬一出問題（例如被塞進
    // 不該有的內容），也沒辦法讓殼把其他類型的檔案存進使用者的電腦。
    private static readonly string[] AllowedDownloadExtensions = { ".txt", ".docx" };

    /// <summary>
    /// 下載的檔名能不能用：不能是空的、不能含 Windows 檔名不允許的字元
    /// （例如「:」，會被當成 NTFS 的替代資料流寫到別的檔案裡），副檔名要是
    /// .txt 或 .docx。
    /// </summary>
    private static bool IsAllowedDownloadName(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return false;
        if (filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        var ext = Path.GetExtension(filename);
        foreach (var allowed in AllowedDownloadExtensions)
        {
            if (string.Equals(ext, allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
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

    private enum WebSourceKind { RemoteOverride, LocalFolder, NotFound }

    /// <summary>
    /// 決定要載入哪個網頁來源，優先順序對應 Mac 版 ContentView.swift 的
    /// webURL：
    ///   1. WEB_BASE_URL_OVERRIDE 環境變數 -- 開發階段用，指到本機另外跑的
    ///      網頁伺服器（例如 python3 -m http.server），測試還沒進打包產物
    ///      的前端改動。回傳的是網址。只有 Debug 版讀得到（見
    ///      DevelopmentOverride）。
    ///   2. 執行檔旁邊的 web/ 資料夾 -- Stage 3 的 build 腳本會把
    ///      zh-cn-to-tw-web 的內容複製到這裡，對應 Mac 版 .app 內的
    ///      Contents/Resources/web/。回傳的是資料夾路徑（要拿去做虛擬
    ///      主機對應，不是直接導覽過去）。
    ///   3. 從執行檔位置往上找，直到某層目錄旁邊有 sibling 的
    ///      zh-cn-to-tw-web/index.html -- 純本機開發、還沒跑過打包腳本時
    ///      的退路，對應這個 meta-repo 底下 zh-cn-to-tw-windows 跟
    ///      zh-cn-to-tw-web 互為 sibling submodule 的目錄結構。一樣回傳
    ///      資料夾路徑。
    /// </summary>
    private static (WebSourceKind, string) ResolveWebSource()
    {
        var overrideUrl = DevelopmentOverride("WEB_BASE_URL_OVERRIDE");
        if (overrideUrl is not null)
        {
            return (WebSourceKind.RemoteOverride, overrideUrl);
        }

        var bundledFolder = Path.Combine(AppContext.BaseDirectory, "web");
        if (File.Exists(Path.Combine(bundledFolder, "index.html")))
        {
            return (WebSourceKind.LocalFolder, bundledFolder);
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "zh-cn-to-tw-web");
            if (File.Exists(Path.Combine(candidate, "index.html")))
            {
                return (WebSourceKind.LocalFolder, candidate);
            }
            dir = dir.Parent;
        }

        return (WebSourceKind.NotFound, string.Empty);
    }
}
