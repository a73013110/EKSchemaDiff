using System.Runtime.InteropServices;
using System.Text;
using EKSchemaDiff.Cli.Commands;
using EKSchemaDiff.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

Log.Init();
EnableVirtualTerminal();

var app = new CommandApp<HomeCommand>();
app.Configure(config =>
{
    config.SetApplicationName("eksd");

    config.AddCommand<RunCommand>("compare")
        .WithDescription("直接比對兩個資料庫並匯出（不經主選單）");

    config.AddCommand<ConfigCommand>("config")
        .WithDescription("設定頁：調整 profile 的比對與輸出選項");

    config.AddCommand<InitCommand>("init")
        .WithDescription("在目前目錄建立 .eksd.json 範本");

    config.AddCommand<ProfilesCommand>("profiles")
        .WithDescription("列出已發現的 profile");
});

try
{
    Log.Info($"執行命令列：eksd {string.Join(' ', args)}");
    int code = app.Run(args);
    Log.Info($"命令結束，結束碼 {code}");
    return code;
}
catch (Exception ex)
{
    // 任何冒泡到最頂層的例外都要留下完整記錄，並提示使用者 log 位置（避免視窗一閃就關、不知原因）。
    Log.Error("頂層未處理例外，程式即將終止", ex);
    try
    {
        AnsiConsole.MarkupLineInterpolated($"[red]發生未預期錯誤：{ex.Message}[/]");
        if (Log.FilePath is not null)
            AnsiConsole.MarkupLineInterpolated($"[grey]詳細記錄已寫入：{Log.FilePath}[/]");
        AnsiConsole.MarkupLine("[grey]按 Enter 結束…[/]");
        if (!Console.IsInputRedirected) Console.ReadLine();
    }
    catch { /* 連終端輸出都失敗時，至少 log 已寫入 */ }
    return 70; // EX_SOFTWARE
}

// 在 Windows 主控台啟用 VT（ENABLE_VIRTUAL_TERMINAL_PROCESSING）並設 UTF-8，
// 讓自繪畫面用的 ESC[H/ESC[K/ESC[0J 控制碼與中文都能正確輸出。
static void EnableVirtualTerminal()
{
    try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 重導向時忽略 */ }

    if (!OperatingSystem.IsWindows()) return;
    try
    {
        nint handle = NativeConsole.GetStdHandle(NativeConsole.STD_OUTPUT_HANDLE);
        if (handle == nint.Zero || handle == (nint)(-1)) return;
        if (NativeConsole.GetConsoleMode(handle, out uint mode))
            NativeConsole.SetConsoleMode(handle, mode | NativeConsole.ENABLE_VIRTUAL_TERMINAL_PROCESSING);
    }
    catch { /* 非主控台或無權限時忽略 */ }
}

static class NativeConsole
{
    public const int STD_OUTPUT_HANDLE = -11;
    public const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);
}
