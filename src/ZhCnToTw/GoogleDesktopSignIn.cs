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

    /// <summary>
    /// 登入成功的結果。Nonce 是這次登入產生的原值，要跟 ID Token 一起交給
    /// 後端（見 StartAsync 裡的說明）。
    /// </summary>
    public sealed record SignInResult(string IdToken, string Nonce);

    /// <summary>
    /// 一個完整的回呼要怎麼處理，對應 Mac 版的 CallbackDecision。
    /// </summary>
    private enum CallbackDecision
    {
        /// <summary>state 相符、id_token 是 JWT 的形狀：這次登入成功。</summary>
        Accept,
        /// <summary>state 相符但沒有可用的 token（使用者取消、Google 回報錯誤）：這次登入失敗，不用再等。</summary>
        Reject,
        /// <summary>state 不符或沒帶：不是這次登入的回呼，當成沒收到，繼續等。</summary>
        Ignore,
    }

    public async Task<SignInResult> StartAsync()
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
            // nonce 原值只留在 App 裡：登入網址帶的是它的 SHA-256，Google
            // 把這個雜湊原樣放進 ID Token；登入成功後原值跟 token 一起交給
            // 後端，後端確認兩者對得上才換發 session。token 會經過系統瀏覽器
            // 跟本機的回呼，原值不會，所以只拿到 token 的人換不到 session。
            var nonce = RandomToken();
            Process.Start(new ProcessStartInfo(BuildAuthUrl(state, HashedNonce(nonce))) { UseShellExecute = true });

            using var cts = new CancellationTokenSource(SignInTimeout);

            // 實測發現（2026-08-20）：只接受第一條連線就把監聽關掉會漏接
            // 真正帶 id_token 的請求——瀏覽器在導到 127.0.0.1 這個 POST
            // 之前/之後可能會有額外的連線打進來（例如連線預熱、沒有 body
            // 的請求），第一條不一定是真正的 form_post callback。改成迴圈
            // 接受連線，直到收到這次登入的回呼或整體逾時為止；每條連線的
            // 原始請求都記到 debug log，之後好對照。
            //
            // state 不符的一律當成不是這次登入的回呼，回一個失敗頁面後繼續
            // 等。state 相符就是這次登入的結果：token 可用就成功；沒有可用的
            // token（使用者在 Google 那邊取消、Google 回報錯誤）就馬上失敗，
            // 不等到逾時——使用者已經看到瀏覽器裡的失敗頁面，App 不該再空等
            // 好幾分鐘才說登入失敗。
            while (true)
            {
                using var client = await AcceptAsync(listener, cts.Token);
                await using var stream = client.GetStream();

                var (fields, rawRequest) = await ReadCallbackAsync(stream, cts.Token);
                var (decision, token) = Decide(fields, state);
                LogDebug($"連線來自 {client.Client.RemoteEndPoint}，判斷：{decision}\n----- request -----\n{RedactIdToken(rawRequest)}");
                await RespondAsync(stream, success: decision == CallbackDecision.Accept);

                switch (decision)
                {
                    case CallbackDecision.Accept:
                        return new SignInResult(token!, nonce);
                    case CallbackDecision.Reject:
                        throw new InvalidOperationException(RejectionReason(fields));
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

    private static string BuildAuthUrl(string state, string hashedNonce)
    {
        var query = new[]
        {
            $"client_id={Uri.EscapeDataString(ClientId)}",
            $"redirect_uri={Uri.EscapeDataString($"http://127.0.0.1:{LoopbackPort}/callback")}",
            "response_type=id_token",
            "response_mode=form_post",
            $"scope={Uri.EscapeDataString("openid email profile")}",
            $"nonce={Uri.EscapeDataString(hashedNonce)}",
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

    /// 這個回呼怎麼處理（見 CallbackDecision）：state 要跟這次登入發出去的
    /// 一樣才算這次登入的回呼，id_token 要是 JWT 的形狀才算成功。成功時
    /// 一併回傳 token。對應 Mac 版的 decide(fields:expectedState:)。
    private static (CallbackDecision Decision, string? IdToken) Decide(Dictionary<string, string>? fields, string expectedState)
    {
        if (fields is null) return (CallbackDecision.Ignore, null);
        if (!fields.TryGetValue("state", out var state) || !string.Equals(state, expectedState, StringComparison.Ordinal)) return (CallbackDecision.Ignore, null);
        if (!fields.TryGetValue("id_token", out var token) || !IsCompactJwt(token)) return (CallbackDecision.Reject, null);
        return (CallbackDecision.Accept, token);
    }

    /// 登入失敗時給使用者看的原因（會顯示在 App 畫面上）。Google 回報的
    /// 錯誤代碼（例如使用者取消時的 access_denied）只有在看起來是代碼時
    /// 才帶上：只收小寫英文字母跟底線，其他內容一律不顯示。
    private static string RejectionReason(Dictionary<string, string>? fields)
    {
        if (fields is not null && fields.TryGetValue("error", out var error) && IsErrorCode(error))
        {
            return $"Google 沒有完成登入（{error}）";
        }
        return "Google 沒有完成登入（沒有收到登入憑證）";
    }

    private static bool IsErrorCode(string value)
    {
        if (value.Length == 0 || value.Length > 64) return false;
        foreach (var c in value)
        {
            if (!((c >= 'a' && c <= 'z') || c == '_')) return false;
        }
        return true;
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

    /// nonce 原值的 SHA-256，base64url、不帶「=」。後端用同一個算法比對
    /// （zh-cn-to-tw-backend 的 auth_utils/auth.py 的 hashed_nonce），兩邊
    /// 任何一點不同（大小寫、補不補「=」）登入都會失敗。
    private static string HashedNonce(string raw) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
