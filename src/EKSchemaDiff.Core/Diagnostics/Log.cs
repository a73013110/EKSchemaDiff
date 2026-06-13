using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace EKSchemaDiff.Core.Diagnostics;

/// <summary>
/// 全系統檔案記錄器。寫到 %LOCALAPPDATA%\EKSchemaDiff\logs\eksd-yyyyMMdd.log，
/// 並可掛上全域未處理例外攔截，確保任何崩潰都留下堆疊。Console 視窗即使瞬間關閉，仍能事後查 log。
/// 執行緒安全；任何記錄失敗都不會反過來讓程式崩潰。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _logFile;
    private static bool _initialized;

    /// <summary>目前 log 檔的完整路徑（尚未初始化時為 null）。</summary>
    public static string? FilePath
    {
        get { lock (Gate) return _logFile; }
    }

    /// <summary>
    /// 初始化記錄器並（預設）掛上全域例外攔截。應在程式進入點最早呼叫一次，重複呼叫安全。
    /// </summary>
    public static void Init(bool hookGlobalHandlers = true)
    {
        lock (Gate)
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                var dir = ResolveLogDir();
                Directory.CreateDirectory(dir);
                _logFile = Path.Combine(dir, $"eksd-{DateTime.Now:yyyyMMdd}.log");
            }
            catch
            {
                _logFile = null; // 無法寫檔時靜默降級，不影響主流程
            }
        }

        WriteLine("INFO", $"=== eksd 啟動 (PID {Environment.ProcessId}) ===");

        if (hookGlobalHandlers)
            HookGlobalHandlers();
    }

    private static void HookGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Error("AppDomain 未處理例外（程式即將終止）", ex);
            else
                WriteLine("FATAL", $"AppDomain 未處理例外（非 Exception 物件）：{e.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Error("Task 未觀察例外", e.Exception);
            e.SetObserved();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            WriteLine("INFO", $"=== eksd 結束 (PID {Environment.ProcessId}) ===");
    }

    public static void Info(string message) => WriteLine("INFO", message);

    public static void Warn(string message) => WriteLine("WARN", message);

    public static void Debug(string message) => WriteLine("DEBUG", message);

    /// <summary>記錄一段流程的進入點（含呼叫端方法名），方便追出在哪一步崩潰。</summary>
    public static void Step(string message, [CallerMemberName] string member = "")
        => WriteLine("STEP", $"{member}: {message}");

    public static void Error(string message, Exception? ex = null)
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
    public static string Describe(Exception ex)
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

    private static void WriteLine(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] {message}";
        try { Trace.WriteLine(line); } catch { /* ignore */ }

        lock (Gate)
        {
            if (_logFile is null) return;
            try { File.AppendAllText(_logFile, line + Environment.NewLine, Encoding.UTF8); }
            catch { /* 記錄失敗絕不反噬主流程 */ }
        }
    }

    private static string ResolveLogDir()
    {
        // 優先用環境變數覆寫，方便部署到客戶端時指定固定位置。
        var overrideDir = Environment.GetEnvironmentVariable("EKSD_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return overrideDir;

        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
            baseDir = AppContext.BaseDirectory;
        return Path.Combine(baseDir, "EKSchemaDiff", "logs");
    }
}
