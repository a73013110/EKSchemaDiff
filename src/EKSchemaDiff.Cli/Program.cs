using System.Runtime.InteropServices;
using System.Text;
using EKSchemaDiff.Cli.Commands;
using Spectre.Console.Cli;

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

return app.Run(args);

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
