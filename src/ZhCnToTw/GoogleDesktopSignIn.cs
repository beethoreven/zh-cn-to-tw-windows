using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ZhCnToTw;

/// <summary>
/// 桌面版 Google 登入，對應 Mac 版 GoogleDesktopSignIn.swift 的同一套協定：
/// 開系統瀏覽器走 Google 官方建議的原生 App 登入流程（RFC 8252），
/// redirect_uri 指到本機一個一次性的 loopback HTTP 監聽（固定 port，
/// 因為 Google Cloud Console 的「已授權的重新導向 URI」是精確字串比對）。
/// 這個 client_id 在 Cloud Console 上已經登記過
/// http://127.0.0.1:53682/callback，Mac 版共用同一個，Windows 沿用不用
/// 另外申請。
///
/// 刻意不用 HttpListener（HTTP.SYS）：非管理員身分在某些 Windows
/// 環境下對 HttpListener 的 prefix 監聽會要求先用 netsh 保留 URL
/// ACL，一般使用者的機器上沒辦法保證有這個保留、也不該要求使用者自己
/// 跑一次系統管理員指令才能登入。改用最原始的 TcpListener 手動解析最
/// 小可用的 HTTP 請求/回應，跟 Mac 版用 NWListener 直接處理原始 TCP
/// 是同一個理由。
/// </summary>
internal sealed class GoogleDesktopSignIn
{
    private const int LoopbackPort = 53682;
    private const string ClientId = "415031055130-73moi9aantfm5hjojmt1r0isk2uo35mr.apps.googleusercontent.com";
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromSeconds(300);

    // 一個請求最多收多少。Google 的回呼只有一個 ID Token 加幾個短欄位，
    // 兩三 KB；超過這個大小的不可能是它，不必繼續收下去。
    private const int MaxRequestBytes = 64 * 1024;
    // ID Token 的長度上限，同樣遠大於實際值（一兩 KB）。
    private const int MaxTokenChars = 16 * 1024;

    public async Task<string> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, LoopbackPort);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"無法監聽本機 127.0.0.1:{LoopbackPort}（可能被其他程式占用）：{ex.Message}", ex);
        }

        try
        {
            // state 是這一次登入專屬的隨機值：放進登入網址，Google 會原樣
            // 放在回呼裡送回來。這個 port 是固定的，同一台電腦上的其他
            // 程式、使用者開著的其他網頁都連得到它；回呼裡的 state 跟這裡
            // 記住的一樣，才能確定它是這次登入的結果，不是別人送進來的內容。
            var state = RandomToken();
            Process.Start(new ProcessStartInfo(BuildAuthUrl(state)) { UseShellExecute = true });

            using var cts = new CancellationTokenSource(SignInTimeout);

            // 實測發現（2026-08-20）：只接受第一條連線就把監聽關掉會漏接
            // 真正帶 id_token 的請求——瀏覽器在導到 127.0.0.1 這個 POST
            // 之前/之後可能會有額外的連線打進來（例如連線預熱、沒有 body
            // 的請求），第一條不一定是真正的 form_post callback。改成迴圈
            // 接受連線，直到收到通過檢查的回呼或整體逾時為止；每條連線的
            // 原始請求都記到 debug log，之後好對照。
            //
            // 通過檢查 = state 跟這次登入的一樣，而且 id_token 是 JWT 的
            // 形狀。不符合的一律當成不是這次登入的回呼，回一個失敗頁面後
            // 繼續等。
            while (true)
            {
                using var client = await AcceptAsync(listener, cts.Token);
                await using var stream = client.GetStream();

                var (fields, rawRequest) = await ReadCallbackAsync(stream, cts.Token);
                var token = AcceptedIdToken(fields, state);
                LogDebug($"連線來自 {client.Client.RemoteEndPoint}，token={(token is null ? "不採用" : "有")}\n----- request -----\n{RedactIdToken(rawRequest)}");
                await RespondAsync(stream, success: token is not null);

                if (token is not null)
                {
                    return token;
                }
            }
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            LogDebug($"例外：{ex}");
            throw;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// id_token 是一組帶使用者 email/姓名、效期內可重放冒充登入的簽名
    /// JWT，不能整串明文寫進磁碟上的除錯 log，只留長度方便比對。
    private static string RedactIdToken(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            text, "id_token=[^&\\s]+", m => $"id_token=(已遮罩，長度 {m.Value.Length - "id_token=".Length})");

    private static void LogDebug(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZhCnToTw");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "oauth_debug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n\n");
        }
        catch
        {
            // 記錄失敗不影響登入流程本身
        }
    }

    private static async Task<TcpClient> AcceptAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            return await listener.AcceptTcpClientAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("登入逾時，請重試");
        }
    }

    private static string BuildAuthUrl(string state)
    {
        var query = new[]
        {
            $"client_id={Uri.EscapeDataString(ClientId)}",
            $"redirect_uri={Uri.EscapeDataString($"http://127.0.0.1:{LoopbackPort}/callback")}",
            "response_type=id_token",
            "response_mode=form_post",
            $"scope={Uri.EscapeDataString("openid email profile")}",
            $"nonce={RandomToken()}",
            $"state={Uri.EscapeDataString(state)}",
            "prompt=select_account",
            // 明確指定登入/同意畫面的語言，不然預設會是英文（跟 Mac 版
            // 同樣的理由）。
            "hl=zh-TW",
        };
        return "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", query);
    }

    /// 只處理這個一次性用途需要的最小 HTTP 解析：找 headers/body 分界、
    /// 讀 Content-Length 對應長度的 body，拆出 form-urlencoded 的欄位——
    /// 不是通用 HTTP 伺服器，跟 Mac 版的 parseCallback(_:) 對應同一套
    /// 邏輯。回傳的 Fields 是 null 代表這條連線沒有送來一個完整的 form
    /// POST。
    ///
    /// 順手處理 Expect: 100-continue 握手（有些 HTTP client 送 body 前會
    /// 先等這個）——這不是真正救回登入失敗的原因（實測用真實 Chrome
    /// 151 送出的 callback 請求並沒有帶 Expect header），純粹是符合
    /// HTTP 語意的防禦性寫法，留著無害。
    private static async Task<(Dictionary<string, string>? Fields, string RawRequest)> ReadCallbackAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var sentContinue = false;
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(chunk, ct);
            }
            catch (OperationCanceledException)
            {
                return (null, "(逾時，沒有讀到任何內容)");
            }
            if (read <= 0) return (null, "(連線關閉，沒有讀到任何內容)");
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxRequestBytes) return (null, "(請求過大，不是 callback)");

            var text = Encoding.UTF8.GetString(buffer.ToArray());
            var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0) continue;

            var headerPart = text[..headerEnd];
            var bodyPart = text[(headerEnd + 4)..];

            if (!sentContinue)
            {
                sentContinue = true;
                if (HasExpectContinue(headerPart))
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), ct);
                }
            }

            var contentLength = ParseContentLength(headerPart);
            if (contentLength is null) return (null, headerPart + "\n(沒有 Content-Length，視為不是 callback 的 POST)");
            if (contentLength.Value < 0 || contentLength.Value > MaxRequestBytes) return (null, headerPart + "\n(Content-Length 不合理，視為不是 callback 的 POST)");
            if (Encoding.UTF8.GetByteCount(bodyPart) < contentLength.Value) continue; // body 還沒收完整，等下一輪

            return (ParseFormFields(bodyPart), headerPart + "\n\n" + bodyPart);
        }
    }

    /// 這個回呼能不能當成這次登入的結果：state 要跟這次登入發出去的一樣，
    /// id_token 要是 JWT 的形狀。通過就回傳 token，否則回傳 null。
    private static string? AcceptedIdToken(Dictionary<string, string>? fields, string expectedState)
    {
        if (fields is null) return null;
        if (!fields.TryGetValue("state", out var state) || !string.Equals(state, expectedState, StringComparison.Ordinal)) return null;
        if (!fields.TryGetValue("id_token", out var token) || !IsCompactJwt(token)) return null;
        return token;
    }

    /// ID Token 是 JWT：三段 base64url，中間用「.」隔開，只會有英數、
    /// 「-」、「_」。形狀不對的一律不收——這個值接下來會交給網頁，不該讓
    /// 任何其他字元有機會跟著進去。
    private static bool IsCompactJwt(string token)
    {
        if (token.Length == 0 || token.Length > MaxTokenChars) return false;
        var segments = token.Split('.');
        if (segments.Length != 3) return false;
        foreach (var segment in segments)
        {
            if (segment.Length == 0) return false;
            foreach (var c in segment)
            {
                var allowed = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '-' || c == '_';
                if (!allowed) return false;
            }
        }
        return true;
    }

    private static bool HasExpectContinue(string headers)
    {
        foreach (var line in headers.Split("\r\n"))
        {
            var idx = line.IndexOf(':');
            if (idx < 0) continue;
            var name = line[..idx].Trim();
            if (!string.Equals(name, "Expect", StringComparison.OrdinalIgnoreCase)) continue;
            return line[(idx + 1)..].Trim().Equals("100-continue", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static int? ParseContentLength(string headers)
    {
        foreach (var line in headers.Split("\r\n"))
        {
            var idx = line.IndexOf(':');
            if (idx < 0) continue;
            var name = line[..idx].Trim();
            if (!string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            return int.TryParse(line[(idx + 1)..].Trim(), out var n) ? n : null;
        }
        return null;
    }

    private static Dictionary<string, string> ParseFormFields(string body)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in body.Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length != 2) continue;
            // application/x-www-form-urlencoded：+ 代表空白，要先換成
            // 空白再做 percent-decoding，順序不能反過來。
            var key = Uri.UnescapeDataString(kv[0].Replace("+", " "));
            var value = Uri.UnescapeDataString(kv[1].Replace("+", " "));
            // 同名欄位只認第一個
            fields.TryAdd(key, value);
        }
        return fields;
    }

    private static async Task RespondAsync(NetworkStream stream, bool success)
    {
        var message = success ? "登入完成，請關閉此頁面。" : "登入失敗，請回到「繁化助手」重試。";
        var body = $"<html><body style=\"font-family:sans-serif;text-align:center;padding-top:80px;\">{message}</body></html>";
        var status = success ? "200 OK" : "400 Bad Request";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var response =
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n{body}";
        var responseBytes = Encoding.UTF8.GetBytes(response);
        await stream.WriteAsync(responseBytes);
    }

    private static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
