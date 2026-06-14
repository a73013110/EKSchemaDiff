using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace ConsoleKit.Diagnostics;

/// <summary>
/// 檔案記錄器。寫到 AppInfo.LogDirectory\&lt;prefix&gt;-yyyyMMdd.log，並可掛上全域未處理例外攔截，
/// 確保任何崩潰都留下堆疊；主控台視窗即使瞬間關閉仍能事後查 log。執行緒安全；任何記錄失敗都不反噬主流程。
///
/// 由 ConsoleHost 直接 new 並擁有（非 DI 建立），故 Initialize()/HookGlobalHandlers() 為具體型別上的方法。
/// 移除了舊版靜態 Log 的全域可變狀態，改為實例化、可 Dispose（解除全域訂閱）、可在同程序多次建立。
/// </summary>
public sealed class FileAppLog : IAppLog, IDisposable
{
    private readonly object _gate = new();
    private readonly AppInfo _app;
    private string? _logFile;
    private bool _initialized;
    private bool _disposed;
    private bool _hooked;

    // 以具名欄位保存全域事件委派，Dispose 時用同一實例解除訂閱（避免同程序多次建立殘留）。
    private UnhandledExceptionEventHandler? _onUnhandled;
    private EventHandler<UnobservedTaskExceptionEventArgs>? _onUnobserved;
    private EventHandler? _onProcessExit;

    public FileAppLog(AppInfo app) => _app = app;

    /// <summary>目前 log 檔的完整路徑（尚未初始化或無法寫檔時為 null）。</summary>
    public string? FilePath
    {
        get { lock (_gate) return _logFile; }
    }

    /// <summary>初始化記錄器並寫入啟動訊息。重複呼叫安全。</summary>
    public void Initialize()
    {
        lock (_gate)
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                Directory.CreateDirectory(_app.LogDirectory);
                var prefix = string.IsNullOrWhiteSpace(_app.LogFilePrefix) ? _app.ExecutableName : _app.LogFilePrefix;
                _logFile = Path.Combine(_app.LogDirectory, $"{prefix}-{DateTime.Now:yyyyMMdd}.log");
            }
            catch
            {
                _logFile = null; // 無法寫檔時靜默降級，不影響主流程
            }
        }

        WriteLine("INFO", $"=== {_app.ExecutableName} 啟動 (PID {Environment.ProcessId}) ===");
    }

    /// <summary>掛上全域未處理例外攔截。重複呼叫安全。</summary>
    public void HookGlobalHandlers()
    {
        lock (_gate)
        {
            if (_hooked || _disposed) return;
            _hooked = true;

            _onUnhandled = (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    Error("AppDomain 未處理例外（程式即將終止）", ex);
                else
                    WriteLine("FATAL", $"AppDomain 未處理例外（非 Exception 物件）：{e.ExceptionObject}");
            };
            _onUnobserved = (_, e) =>
            {
                Error("Task 未觀察例外", e.Exception);
                e.SetObserved();
            };
            _onProcessExit = (_, _) =>
                WriteLine("INFO", $"=== {_app.ExecutableName} 結束 (PID {Environment.ProcessId}) ===");

            AppDomain.CurrentDomain.UnhandledException += _onUnhandled;
            TaskScheduler.UnobservedTaskException += _onUnobserved;
            AppDomain.CurrentDomain.ProcessExit += _onProcessExit;
        }
    }

    public void Info(string message) => WriteLine("INFO", message);

    public void Warn(string message) => WriteLine("WARN", message);

    public void Debug(string message) => WriteLine("DEBUG", message);

    public void Step(string message, [CallerMemberName] string member = "")
        => WriteLine("STEP", $"{member}: {message}");

    public void Error(string message, Exception? ex = null)
    {
        var sb = new StringBuilder(message);
        if (ex is not null)
        {
            sb.Append(Environment.NewLine);
            sb.Append(Describe(ex));
        }
        WriteLine("ERROR", sb.ToString());
    }

    /// <summary>把例外（含 InnerException 鏈）攤平成可讀字串。</summary>
    private static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        var cur = ex;
        int depth = 0;
        while (cur is not null)
        {
            if (depth > 0) sb.Append(Environment.NewLine).Append("  --> 內層例外：");
            sb.Append(cur.GetType().FullName).Append(": ").Append(cur.Message);
            if (!string.IsNullOrEmpty(cur.StackTrace))
                sb.Append(Environment.NewLine).Append(cur.StackTrace);
            cur = cur.InnerException;
            depth++;
        }
        return sb.ToString();
    }

    private void WriteLine(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] {message}";
        try { Trace.WriteLine(line); } catch { /* ignore */ }

        lock (_gate)
        {
            if (_disposed || _logFile is null) return;
            try { File.AppendAllText(_logFile, line + Environment.NewLine, Encoding.UTF8); }
            catch { /* 記錄失敗絕不反噬主流程 */ }
        }
    }

    /// <summary>解除全域訂閱並停止寫入。重複呼叫安全。結束訊息應於 Dispose 前記錄。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            if (_onUnhandled is not null) AppDomain.CurrentDomain.UnhandledException -= _onUnhandled;
            if (_onUnobserved is not null) TaskScheduler.UnobservedTaskException -= _onUnobserved;
            if (_onProcessExit is not null) AppDomain.CurrentDomain.ProcessExit -= _onProcessExit;
            _onUnhandled = null;
            _onUnobserved = null;
            _onProcessExit = null;
        }
    }
}
