using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace ZhCnToTw;

/// <summary>
/// 管理本機 zh-cn-to-tw-ocr-service 子行程的生命週期：啟動、從 stdout 讀取
/// 實際綁定的 port（絕不預先猜測或寫死，見該 repo 的設計說明）、產生只有
/// 這次啟動才知道的隨機 token、監控存活狀態、結束時確實把子行程關掉。
/// 對應 Mac 版 OCRServiceManager.swift，行為刻意保持一致。
/// </summary>
internal sealed class OcrServiceManager
{
    // 啟動逾時 + 重試：全部都在本機跑，正常情況下 process 起來、綁好
    // port、印出那一行，應該是秒等級的事——如果等超過這個時間還沒拿到
    // port，代表這次啟動大概率是卡住了，直接強制關掉重來，不要無止盡
    // 等下去，也不要讓使用者永遠卡在「本機 OCR 服務啟動中」卻不知道
    //發生什麼事。
    private const int StartupTimeoutSeconds = 20;
    private const int MaxStartupAttempts = 3;

    public string Token { get; } = GenerateToken();
    public int? Port { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Port 或 LastError 變動時觸發，可能不在 UI 執行緒上——訂閱端自己負責派回 UI 執行緒。</summary>
    public event Action? StateChanged;

    private readonly Func<(string FileName, string Arguments, string WorkingDirectory)> _resolveExecutable;
    private Process? _process;
    private CancellationTokenSource? _startupTimeoutCts;
    private int _startupAttempt;

    public OcrServiceManager(Func<(string, string, string)> resolveExecutable)
    {
        _resolveExecutable = resolveExecutable;
    }

    private static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public void Start()
    {
        if (_process is not null) return;
        _startupAttempt++;

        (string FileName, string Arguments, string WorkingDirectory) target;
        try
        {
            target = _resolveExecutable();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StateChanged?.Invoke();
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = target.FileName,
            Arguments = target.Arguments,
            WorkingDirectory = target.WorkingDirectory,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // OCR_SERVICE_PORT 故意不設——讓 ocr-service 自己跟作業系統要一個
        // 目前沒人用的空 port，殼從 stdout 讀實際拿到的值。這個專案本機
        // 測試階段吃過很多次「port 被舊 process 卡住」的虧，桌面 App 不能
        // 重蹈覆轍。
        psi.EnvironmentVariables["OCR_SERVICE_TOKEN"] = Token;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // OutputDataReceived/Exited 都是非同步 callback，可能延遲觸發——
        // 如果這個 process 已經死掉、而且已經有一個新的 process 被拉起來，
        // 舊 process 這兩個 callback 才姍姍來遲，絕對不能無條件覆寫
        // Port/_process，那樣會把新 process 已經正確設好、正常運作中的
        // 狀態蓋掉。用 identity 比對（ReferenceEquals(_process, process)）
        // 確認這個 callback 還是「目前這一個」process 才生效。
        process.OutputDataReceived += (_, e) => OnOutputLine(process, e.Data);
        process.Exited += (_, _) => OnExited(process);

        _process = process;
        try
        {
            process.Start();
            ProcessJobObject.Assign(process);
            process.BeginOutputReadLine();
            ScheduleStartupTimeout(process);
        }
        catch (Exception ex)
        {
            _process = null;
            LastError = $"啟動 ocr-service 失敗：{ex.Message}";
            StateChanged?.Invoke();
        }
    }

    private void OnOutputLine(Process process, string? line)
    {
        // .NET 的 OutputDataReceived 在管線寫入端關閉（子行程結束）時會用
        // e.Data == null 通知一次，不是像原始 readability callback 那樣
        // 一直重複被呼叫——這裡不需要像 Mac 版 Swift 那樣手動偵測空資料、
        // 手動解掉 handler 避免忙迴圈，.NET 這層 API 本身就是正確設計。
        if (line is null) return;
        const string prefix = "OCR_SERVICE_PORT=";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return;
        if (!int.TryParse(line.AsSpan(prefix.Length), out var port)) return;
        if (!ReferenceEquals(_process, process)) return;

        Port = port;
        LastError = null;
        // 真的拿到 port 了，這次啟動成功，取消逾時保險、重置重試計數，
        // 不要讓下一次（例如閒置後重啟）的失敗次數繼續往上疊加。
        _startupTimeoutCts?.Cancel();
        _startupAttempt = 0;
        StateChanged?.Invoke();
    }

    private void OnExited(Process process)
    {
        if (!ReferenceEquals(_process, process)) return;
        _process = null;
        Port = null;
        if (process.ExitCode != 0)
        {
            LastError = $"ocr-service 已結束（exit code {process.ExitCode}）";
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// process 活著（沒走到 OnExited）但一直沒印出 port，本機服務不應該
    /// 這麼慢，等超過這個時間就當它卡住了：強制關掉、視情況自動重試。
    /// </summary>
    private void ScheduleStartupTimeout(Process process)
    {
        var cts = new CancellationTokenSource();
        _startupTimeoutCts = cts;
        _ = RunStartupTimeoutAsync(process, cts.Token);
    }

    private async Task RunStartupTimeoutAsync(Process process, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(StartupTimeoutSeconds), ct);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (!ReferenceEquals(_process, process) || Port is not null) return;

        // 我們自己主動關的，先把 _process 清成 null，讓 OnExited 的
        // identity check 直接判定「不是目前這個」而變成 no-op——不然
        // 一般的終止處理會設一個「已結束（exit code ...)」的錯誤訊息，
        // 蓋掉這裡接下來要設的、更明確的逾時訊息。
        _process = null;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 已經在結束路上、或系統層級的 kill 失敗，不影響接下來的重試邏輯。
        }

        if (_startupAttempt < MaxStartupAttempts)
        {
            LastError = $"本機 OCR 服務啟動逾時，正在重試（{_startupAttempt}/{MaxStartupAttempts}）";
            StateChanged?.Invoke();
            Start();
        }
        else
        {
            LastError = $"本機 OCR 服務啟動逾時，已重試 {MaxStartupAttempts} 次仍失敗，請按重新整理再試一次";
            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// 確保服務是活的。網頁在真的要用 OCR 之前會請殼呼叫這裡——服務平常
    /// 是關著的，用到才開、用完就關。刻意不做定時的自動重啟：那會讓
    /// 「用完就關」完全失去意義。
    /// </summary>
    public void EnsureRunning()
    {
        if (_process is not null && !_process.HasExited) return;
        // process 還在但已經不是 running，代表它死了、只是 Exited 事件
        // 還沒觸發（那是非同步派送的，中間有空窗）。這裡一定要先清成
        // null：Start() 開頭有 `if (_process is not null) return;`，不清
        // 的話這個 guard 會直接擋掉，EnsureRunning 變成靜默的什麼都沒做。
        if (_process is not null)
        {
            _process = null;
            Port = null;
        }
        Start();
    }

    /// <summary>讓使用者可以手動重新觸發啟動流程，不受 MaxStartupAttempts 已經用完的限制。</summary>
    public void RetryStart()
    {
        _startupAttempt = 0;
        LastError = null;
        Start();
    }

    /// <summary>關掉服務並釋放記憶體。網頁在 OCR 階段結束、結果也拿走之後會請殼呼叫這裡，App 結束時也會呼叫。</summary>
    public void Stop()
    {
        _startupTimeoutCts?.Cancel();
        _startupTimeoutCts = null;
        if (_process is not null)
        {
            var process = _process;
            // 這是我們自己主動關的，先清成 null 讓 OnExited 的 identity
            // check 變成 no-op（那裡會設 lastError，正常關閉不該讓使用者
            // 看到錯誤訊息）。
            _process = null;
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 已經結束或系統層級 kill 失敗，都不影響接下來要清空的狀態。
            }
        }
        Port = null;
        LastError = null;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 找 zh-cn-to-tw-ocr-service 執行檔，優先順序對應 Mac 版
    /// resolveOCRServiceExecutable()：
    ///   1. 安裝目錄旁的 ocr-service\ 資料夾 -- Stage 3 的 build 腳本會把
    ///      PyInstaller 打包好的執行檔放到這裡。
    ///   2. OCR_SERVICE_DEV_PATH 環境變數 -- 指到本機已經用 PyInstaller
    ///      打包好的執行檔，跟 Mac 版開發模式的用法一致。
    ///   3. OCR_SERVICE_DEV_PYTHON_DIR 環境變數 -- Windows 專屬的開發期
    ///      捷徑，指到 zh-cn-to-tw-ocr-service repo 根目錄，直接用它的
    ///      venv\Scripts\python.exe app.py 啟動，不需要每次改動都重新
    ///      跑一次 PyInstaller 才能測，方便快速迭代。
    /// </summary>
    public static (string FileName, string Arguments, string WorkingDirectory) ResolveExecutable()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "ocr-service", "zh-cn-to-tw-ocr-service.exe");
        if (File.Exists(bundled))
        {
            return (bundled, "", Path.GetDirectoryName(bundled)!);
        }

        var devPath = Environment.GetEnvironmentVariable("OCR_SERVICE_DEV_PATH");
        if (!string.IsNullOrEmpty(devPath) && File.Exists(devPath))
        {
            return (devPath, "", Path.GetDirectoryName(devPath)!);
        }

        var devPythonDir = Environment.GetEnvironmentVariable("OCR_SERVICE_DEV_PYTHON_DIR");
        if (!string.IsNullOrEmpty(devPythonDir))
        {
            var pythonExe = Path.Combine(devPythonDir, "venv", "Scripts", "python.exe");
            return (pythonExe, "app.py", devPythonDir);
        }

        throw new InvalidOperationException(
            "找不到 zh-cn-to-tw-ocr-service 執行檔。正式打包時應該被放進安裝目錄的 ocr-service\\ " +
            "底下；開發階段請設定 OCR_SERVICE_DEV_PATH（指到 PyInstaller 打包好的執行檔）或 " +
            "OCR_SERVICE_DEV_PYTHON_DIR（指到 zh-cn-to-tw-ocr-service repo 根目錄，直接用它的 " +
            "venv\\Scripts\\python.exe app.py 啟動，不需要先打包）。");
    }
}
