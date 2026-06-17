using ConsoleKit.Diagnostics;
using ConsoleKit.Tui;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ConsoleKit.Hosting;

/// <summary>
/// CLI 組合根：啟用 VT → 建立並擁有記錄器 → 組 ServiceCollection 與命令 → 執行 → 頂層例外保險。
/// 拆版到新 CLI 時只需提供 <see cref="AppInfo"/> 與兩個設定委派，骨架其餘不動。
/// </summary>
public static class ConsoleHost
{
    /// <summary>
    /// 啟動並執行 CLI。<paramref name="configureServices"/> 註冊領域服務、
    /// <paramref name="configureCommands"/> 註冊命令（應用名稱已預設為 AppInfo.ExecutableName）。
    /// <paramref name="theme"/> 為領域端色票；省略則沿用骨架內建的中性預設。
    /// </summary>
    public static int Run<TDefaultCommand>(
        AppInfo app,
        string[] args,
        Action<IServiceCollection>? configureServices = null,
        Action<IConfigurator>? configureCommands = null,
        ThemePalette? theme = null)
        where TDefaultCommand : class, ICommand
    {
        NativeConsole.EnableVirtualTerminal();
        if (theme is not null) Theme.Use(theme);

        // 記錄器由 Host 直接建立並擁有（非經 DI 解析），才能在 Run 前就記錄 DI/命令設定階段的錯誤。
        FileAppLog? appLog = null;
        try
        {
            appLog = new FileAppLog(app);
            appLog.Initialize();
            appLog.HookGlobalHandlers();

            var services = new ServiceCollection();
            services.AddSingleton(app);
            services.AddSingleton(appLog);          // 具體型別 FileAppLog
            services.AddSingleton<IAppLog>(appLog); // 同一實例
            configureServices?.Invoke(services);

            var registrar = new ServiceCollectionTypeRegistrar(services);
            try
            {
                var cli = new CommandApp<TDefaultCommand>(registrar);
                cli.Configure(cfg =>
                {
                    cfg.SetApplicationName(app.ExecutableName);
                    configureCommands?.Invoke(cfg);
                });

                // 預設只記錄啟動／結束，不記錄完整命令列（可能含 token/密碼）。完整參數記錄為 opt-in。
                appLog.Info($"{app.ExecutableName} 啟動（引數 {args.Length} 個）");
                int code = cli.Run(args);   // 唯一的 Provider 由 Spectre 於 Run 期間透過 registrar.Build() 建立
                appLog.Info($"命令結束，結束碼 {code}");
                return code;
            }
            finally
            {
                registrar.Dispose();   // Registrar 擁有並 Dispose 其建立的 Provider
            }
        }
        catch (Exception ex)
        {
            // 頂層例外為「盡力」記錄：appLog 已建立則寫 log 並提示；極早期失敗則退回 Console.Error。
            if (appLog is not null)
            {
                appLog.Error("頂層未處理例外，程式即將終止", ex);
                try
                {
                    AnsiConsole.MarkupLineInterpolated($"[{Theme.Danger}]發生未預期錯誤：{ex.Message}[/]");
                    if (appLog.FilePath is not null)
                        AnsiConsole.MarkupLineInterpolated($"[{Theme.TextMuted}]詳細記錄已寫入：{appLog.FilePath}[/]");
                    AnsiConsole.MarkupLine($"[{Theme.TextMuted}]按 Enter 結束…[/]");
                    if (!Console.IsInputRedirected) Console.ReadLine();
                }
                catch { /* 連終端輸出都失敗時，至少 log 已寫入 */ }
            }
            else
            {
                try { Console.Error.WriteLine($"發生未預期錯誤：{ex}"); } catch { /* 真的無能為力 */ }
            }
            return ExitCode.SoftwareError;
        }
        finally
        {
            appLog?.Dispose();   // Host 擁有 appLog，由 Host 負責 Dispose（解除全域訂閱、結束訊息已於上方記錄）
        }
    }
}
