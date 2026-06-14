using ConsoleKit.Hosting;
using EKSchemaDiff.Cli.Commands;
using EKSchemaDiff.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

Log.Init();
NativeConsole.EnableVirtualTerminal();

var app = new CommandApp<HomeCommand>();
app.Configure(config =>
{
    config.SetApplicationName("eksd");

    config.AddCommand<CompareCommand>("compare")
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
