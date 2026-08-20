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
            Process.Start(new ProcessStartInfo(BuildAuthUrl()) { UseShellExecute = true });

            using var cts = new CancellationTokenSource(SignInTimeout);

            // 實測發現（2026-08-20）：只接受第一條連線就把監聽關掉會漏接
            // 真正帶 id_token 的請求——瀏覽器在導到 127.0.0.1 這個 POST
            // 之前/之後可能會有額外的連線打進來（例如連線預熱、沒有 body
            // 的請求），第一條不一定是真正的 form_post callback。改成迴圈
            // 接受連線，直到解析出 id_token 或整體逾時為止；每條連線的
            // 原始請求都記到 debug log，之後好對照。
            while (true)
            {
                using var client = await AcceptAsync(listener, cts.Token);
                await using var stream = client.GetStream();

                var (token, rawHeaders) = await ReadIdTokenAsync(stream, cts.Token);
                LogDebug($"連線來自 {client.Client.RemoteEndPoint}，token={(token is null ? "null" : "有")}\n----- headers -----\n{RedactIdToken(rawHeaders)}");
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

    private static string BuildAuthUrl()
    {
        var query = new[]
        {
            $"client_id={Uri.EscapeDataString(ClientId)}",
            $"redirect_uri={Uri.EscapeDataString($"http://127.0.0.1:{LoopbackPort}/callback")}",
            "response_type=id_token",
            "response_mode=form_post",
            $"scope={Uri.EscapeDataString("openid email profile")}",
            $"nonce={RandomToken()}",
            $"state={RandomToken()}",
            "prompt=select_account",
            // 明確指定登入/同意畫面的語言，不然預設會是英文（跟 Mac 版
            // 同樣的理由）。
            "hl=zh-TW",
        };
        return "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", query);
    }

    /// 只處理這個一次性用途需要的最小 HTTP 解析：找 headers/body 分界、
    /// 讀 Content-Length 對應長度的 body，從 form-urlencoded 格式解析出
    /// id_token 欄位——不是通用 HTTP 伺服器，跟 Mac 版的
    /// extractIDToken(from:) 對應同一套邏輯。
    ///
    /// 順手處理 Expect: 100-continue 握手（有些 HTTP client 送 body 前會
    /// 先等這個）——這不是真正救回登入失敗的原因（實測用真實 Chrome
    /// 151 送出的 callback 請求並沒有帶 Expect header），純粹是符合
    /// HTTP 語意的防禦性寫法，留著無害。
    private static async Task<(string? Token, string RawHeaders)> ReadIdTokenAsync(NetworkStream stream, CancellationToken ct)
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
            if (Encoding.UTF8.GetByteCount(bodyPart) < contentLength.Value) continue; // body 還沒收完整，等下一輪

            return (ParseIdToken(bodyPart), headerPart + "\n\n" + bodyPart);
        }
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

    private static string? ParseIdToken(string body)
    {
        foreach (var pair in body.Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length != 2 || kv[0] != "id_token") continue;
            // application/x-www-form-urlencoded：+ 代表空白，要先換成
            // 空白再做 percent-decoding，順序不能反過來。
            var normalized = kv[1].Replace("+", " ");
            return Uri.UnescapeDataString(normalized);
        }
        return null;
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
