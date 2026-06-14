using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using EKSchemaDiff.Cli.Tui;
using EKSchemaDiff.Core.Compare;
using EKSchemaDiff.Core.Config;
using EKSchemaDiff.Core.Export;
using Spectre.Console;

namespace EKSchemaDiff.Cli;

/// <summary>比對 → 預覽勾選 → 匯出的共用流程，供主選單與 compare 命令使用（DI singleton）。</summary>
public sealed class CompareWorkflow
{
    private readonly IAppLog _log;
    private readonly Banner _banner;
    private readonly ConfigStoreFactory _configStores;

    public CompareWorkflow(IAppLog log, Banner banner, ConfigStoreFactory configStores)
    {
        _log = log;
        _banner = banner;
        _configStores = configStores;
    }

    /// <summary>
    /// 自設定探索並執行比對流程（供 compare 命令與主選單快速模式共用）：
    /// 探索 → 驗證有 profile → 解析（互動時可挑選）→ 解析輸出形式 → 執行。
    /// </summary>
    public int RunFromConfig(string? startDir, string? profileName, string? outOverride, string? exportRaw, bool interactive)
    {
        ConfigStore store;
        try { store = _configStores.Discover(startDir); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]設定載入失敗：{ex.Message}[/]");
            return ExitCode.UsageError;
        }

        if (store.Effective.Profiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]尚未設定任何 profile。[/]");
            AnsiConsole.MarkupLine("請執行 [bold]eksd[/] 從主選單建立連線設定，或 [bold]eksd init[/] 產生範本。");
            return ExitCode.UsageError;
        }

        Profile? profile;
        try
        {
            profile = store.ResolveProfile(profileName);
            if (profile is null)
            {
                if (!interactive)
                    throw new InvalidOperationException("有多組 profile，請以 --profile 指定。");
                profile = Prompts.PickProfile(store.Effective.Profiles, _banner);
                if (profile is null) return ExitCode.Ok;   // 按 Esc 取消挑選
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return ExitCode.UsageError;
        }

        DeployScriptMode? exportOverride = string.IsNullOrWhiteSpace(exportRaw)
            ? null
            : ParseExportMode(exportRaw!);

        return Run(store, profile, outOverride, exportOverride, interactive);
    }

    public int Run(
        ConfigStore store, Profile profile,
        string? outOverride, DeployScriptMode? exportOverride, bool interactive)
    {
        _log.Step($"開始比對流程 profile='{profile.Name}' interactive={interactive}");
        ShowProfileSummary(profile);

        CompareSession? session = null;
        try
        {
            AnsiConsole.Status().Spinner(Spinner.Known.Dots)
                .Start("正在比對兩個資料庫結構…", _ =>
                {
                    session = SchemaComparer.Run(profile, Prompts.PromptPassword);
                });
        }
        catch (Exception ex)
        {
            _log.Error("比對階段失敗", ex);
            AnsiConsole.MarkupLineInterpolated($"[red]比對失敗：{ex.Message}[/]");
            return EksdExitCode.CompareFailed;
        }

        if (session is null || !session.IsValid)
        {
            AnsiConsole.MarkupLine("[red]比對結果無效。[/]");
            foreach (var e in session?.GetErrors() ?? Enumerable.Empty<string>())
                AnsiConsole.MarkupLineInterpolated($"  [red]- {e}[/]");
            return EksdExitCode.CompareFailed;
        }

        foreach (var u in session.UnrecognizedExcludedTypes)
            AnsiConsole.MarkupLineInterpolated($"[yellow]警告：排除類型無法辨識，已忽略：{u}[/]");

        if (session.Differences.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]兩個資料庫結構一致，沒有差異。[/]");
            return ExitCode.Ok;
        }

        _log.Step($"比對完成，差異 {session.Differences.Count} 項");
        ShowDiffOverview(session);

        var exportMode = exportOverride ?? profile.ExportOptions.DeployScript;

        if (interactive)
        {
            _log.Step("進入勾選預覽畫面 ReviewScreen");
            var included = ReviewScreen.Run(session.Differences, profile.ExportOptions.HtmlIgnoreWhitespace, _log);
            _log.Step($"離開勾選預覽畫面，結果={(included is null ? "取消" : included.Count + " 項")}");
            if (included is null)
            {
                AnsiConsole.MarkupLine("[yellow]已取消，未匯出。[/]");
                return ExitCode.Ok;
            }
            session.ApplyInclusion(included);
            if (included.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]未勾選任何物件，已取消匯出。[/]");
                return ExitCode.Ok;
            }
            // 輸出形式一律沿用設定頁的設定（不再每次詢問，否則設定形同虛設）。
            // 需臨時改變時，用 compare --export single|perobject|both。
        }

        profile.ExportOptions.DeployScript = exportMode;
        var outputDir = ResolveOutputDir(store, profile, outOverride);
        _log.Step($"開始匯出 mode={exportMode} outputDir='{outputDir}'");

        ExportSummary summary;
        try
        {
            if (interactive)
            {
                var (captured, cancelled) = RunExportWithProgress(session!, outputDir);
                if (cancelled || captured is null)
                {
                    AnsiConsole.MarkupLine("[yellow]已中斷匯出（部分檔案可能已寫出）。[/]");
                    return ExitCode.Ok;
                }
                summary = captured;
            }
            else
            {
                summary = Exporter.Export(session!, outputDir, DateTime.Now);
            }
        }
        catch (Exception ex)
        {
            _log.Error("匯出階段失敗", ex);
            AnsiConsole.MarkupLineInterpolated($"[red]匯出失敗：{ex.Message}[/]");
            return EksdExitCode.ExportFailed;
        }

        _log.Step($"匯出完成，HTML {summary.HtmlReportCount} 份、逐物件部署檔 {summary.ObjectScriptCount} 個");
        ShowExportSummary(summary);

        if (interactive && summary.HtmlReportCount > 0)
        {
            var overview = Path.Combine(outputDir, "差異報告", "00_比對總覽.html");
            if (File.Exists(overview) && AnsiConsole.Confirm("要開啟比對總覽 HTML 嗎？", defaultValue: false))
                OpenFile(overview);
        }

        return summary.ObjectScriptVerificationPassed ? ExitCode.Ok : EksdExitCode.VerificationFailed;
    }

    /// <summary>
    /// 在背景執行匯出，前景以自繪畫面顯示進度（含大 Logo），可按 Esc 中斷。
    /// 回傳 (摘要, 是否中斷)。中斷時摘要為 null。
    /// </summary>
    private (ExportSummary? Summary, bool Cancelled) RunExportWithProgress(
        CompareSession session, string outputDir)
    {
        using var cts = new CancellationTokenSource();
        var gate = new object();
        var cur = new ExportProgress("準備中", "", 0, 0);
        var history = new List<string>();   // 依序記錄處理過的項目（去除連續重複）
        void Report(ExportProgress p)
        {
            lock (gate)
            {
                cur = p;
                if (!string.IsNullOrWhiteSpace(p.Item) && (history.Count == 0 || history[^1] != p.Item))
                    history.Add(p.Item);
            }
        }

        ExportSummary? summary = null;
        Exception? error = null;
        var task = Task.Run(() =>
        {
            try { summary = Exporter.Export(session, outputDir, DateTime.Now, Report, cts.Token); }
            catch (OperationCanceledException) { /* 使用者中斷 */ }
            catch (Exception ex) { error = ex; }
        });

        try
        {
            Console.CursorVisible = false;
            int tick = 0;
            while (!task.IsCompleted)
            {
                ExportProgress snap; List<string> hist;
                lock (gate) { snap = cur; hist = new List<string>(history); }
                RenderExportFrame(snap, cts.IsCancellationRequested, tick, hist, done: false);
                if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape)
                    cts.Cancel();
                Thread.Sleep(80);
                tick++;
            }
            ExportProgress last; List<string> lastHist;
            lock (gate) { last = cur; lastHist = new List<string>(history); }
            RenderExportFrame(last, cts.IsCancellationRequested, tick, lastHist, done: !cts.IsCancellationRequested);
        }
        finally
        {
            Console.CursorVisible = true;
        }

        task.Wait();
        if (error is not null) throw error;
        return (summary, cts.IsCancellationRequested);
    }

    private static readonly string[] SpinnerFrames =
        { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

    private void RenderExportFrame(
        ExportProgress p, bool cancelling, int tick, IReadOnlyList<string> history, bool done)
    {
        ConsoleUI.BeginFrame();
        _banner.Show();

        string spin = done ? "[green]✔[/]" : $"[orange3]{SpinnerFrames[tick % SpinnerFrames.Length]}[/]";
        ConsoleUI.Line($"{spin} [orange3]產出進度[/]　[grey39]Esc 中斷[/]");
        ConsoleUI.Line();

        int total = Math.Max(1, p.Total);
        int current = Math.Clamp(p.Current, 0, total);
        int barWidth = Math.Clamp(ConsoleUI.Width - 26, 10, 60);
        int filled = (int)Math.Round((double)current / total * barWidth);
        filled = Math.Clamp(filled, 0, barWidth);

        // 進度條：已完成段用實心；未完成段放一個來回跑動的指示點，即使卡在大物件也看得出在動。
        var empty = new System.Text.StringBuilder(new string('░', Math.Max(0, barWidth - filled)));
        if (!done && empty.Length > 0)
        {
            int span = empty.Length;
            int pos = tick % (span * 2);
            if (pos >= span) pos = span * 2 - 1 - pos;   // 來回反彈
            empty[Math.Clamp(pos, 0, span - 1)] = '▒';
        }
        var bar = $"[green]{new string('█', filled)}[/][grey39]{empty}[/]";
        int pct = (int)Math.Round((double)current / total * 100);
        ConsoleUI.Line($"{bar}  [bold]{current}/{total}[/] [grey]({pct}%)[/]");
        ConsoleUI.Line();

        ConsoleUI.Line($"[grey]階段：[/]{ConsoleUI.Esc(p.Phase)}");
        if (!done && !string.IsNullOrWhiteSpace(p.Item))
            ConsoleUI.Line($"{spin} [grey]處理中：[/]{ConsoleUI.Esc(p.Item)}");

        // 已處理清單：顯示最近數筆，最後一筆若仍在處理中則不標完成。
        int avail = Math.Max(3, ConsoleUI.Height - 14);
        int inProgressTail = done ? 0 : 1;   // 最後一筆通常是進行中那筆
        int completedCount = Math.Max(0, history.Count - inProgressTail);
        if (completedCount > 0)
        {
            ConsoleUI.Line();
            ConsoleUI.Line($"[grey39]── 已處理 {completedCount} 項 ──[/]");
            int show = Math.Min(avail, completedCount);
            for (int i = completedCount - show; i < completedCount; i++)
                ConsoleUI.Line($"  [green]✓[/] [grey]{ConsoleUI.Esc(history[i])}[/]");
            if (completedCount > show)
                ConsoleUI.Line($"  [grey39]…（前 {completedCount - show} 項略）[/]");
        }

        if (cancelling)
            ConsoleUI.Line("[yellow]正在中斷…[/]");
        ConsoleUI.EndFrame();
    }

    private static void ShowProfileSummary(Profile profile)
    {
        var co = profile.CompareOptions;
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]Profile[/]", $"[bold]{Markup.Escape(profile.Name)}[/]");
        grid.AddRow("[grey]來源（更版）[/]", Markup.Escape(profile.Source.ToSafeDisplay()));
        grid.AddRow("[grey]目標（原版）[/]", Markup.Escape(profile.Target.ToSafeDisplay()));
        var deployDb = profile.ResolveDeployDatabaseName();
        var deployDbNote = string.IsNullOrWhiteSpace(profile.ExportOptions.DeployDatabaseName)
            ? "[grey](沿用目標庫名)[/]" : "[yellow](已覆寫)[/]";
        grid.AddRow("[grey]部署 USE 資料庫[/]", $"[bold]{Markup.Escape(deployDb)}[/] {deployDbNote}");
        grid.AddRow("[grey]忽略權限[/]", co.IgnorePermissions
            ? "[green]是（不動 GRANT/DENY/REVOKE）[/]"
            : "[red]否（注意誤刪權限風險）[/]");
        grid.AddRow("[grey]刪除目標多出物件[/]", co.DropObjectsNotInSource ? "[red]是[/]" : "[green]否[/]");
        grid.AddRow("[grey]資料遺失阻擋[/]", co.BlockOnPossibleDataLoss ? "[green]是[/]" : "[yellow]否[/]");
        grid.AddRow("[grey]比對描述(MS_Description)[/]", co.IgnoreExtendedProperties ? "[yellow]否[/]" : "[green]是[/]");
        grid.AddRow("[grey]輸出形式[/]", $"[yellow]{profile.ExportOptions.DeployScript}[/]"
            + (profile.ExportOptions.ExportHtml ? " + HTML" : ""));
        AnsiConsole.Write(new Panel(grid)
        {
            Header = new PanelHeader(" 比對情境 "),
            Border = BoxBorder.Rounded,
        }.BorderColor(Color.DarkOrange3));
        AnsiConsole.WriteLine();
    }

    private static void ShowDiffOverview(CompareSession session)
    {
        int add = session.Differences.Count(d => d.UpdateAction == ChangeKind.Add);
        int chg = session.Differences.Count(d => d.UpdateAction == ChangeKind.Change);
        int del = session.Differences.Count(d => d.UpdateAction == ChangeKind.Delete);
        AnsiConsole.MarkupLineInterpolated(
            $"找到 [bold]{session.Differences.Count}[/] 項差異：[green]+{add} 新增[/]　[yellow]~{chg} 變更[/]　[red]-{del} 刪除[/]");
        AnsiConsole.WriteLine();
    }

    private static void ShowExportSummary(ExportSummary summary)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[green]匯出完成[/]") { Justification = Justify.Left });

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey39);
        table.AddColumn("項目");
        table.AddColumn("結果");
        if (summary.FullScriptPath is not null)
            table.AddRow("完整部署腳本", Markup.Escape(summary.FullScriptPath));
        if (summary.ObjectScriptCount > 0)
        {
            var v = summary.ObjectScriptVerificationPassed
                ? "[green]整理驗證通過[/]"
                : $"[red]驗證未過：{Markup.Escape(summary.ObjectScriptVerificationMessage ?? "")}[/]";
            table.AddRow("逐物件部署檔", $"{summary.ObjectScriptCount} 個　{v}");
        }
        if (summary.HtmlReportCount > 0)
            table.AddRow("差異 HTML", $"{summary.HtmlReportCount} 份 + 總覽");
        table.AddRow("輸出目錄", Markup.Escape(summary.OutputDir));
        AnsiConsole.Write(table);

        foreach (var w in summary.Warnings)
            AnsiConsole.MarkupLineInterpolated($"[yellow]! {w}[/]");
    }

    private static string ResolveOutputDir(ConfigStore store, Profile profile, string? overrideDir)
    {
        var dir = overrideDir ?? profile.OutputDir;
        if (Path.IsPathRooted(dir)) return Path.GetFullPath(dir);
        var baseDir = store.ProjectConfigPath is not null
            ? Path.GetDirectoryName(store.ProjectConfigPath)!
            : Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(baseDir, dir));
    }

    public static DeployScriptMode ParseExportMode(string s) => s.Trim().ToLowerInvariant() switch
    {
        "single" => DeployScriptMode.Single,
        "perobject" or "split" => DeployScriptMode.PerObject,   // split 為舊值別名，向後相容
        "both" => DeployScriptMode.Both,
        _ => throw new InvalidOperationException($"未知的 --export 值：{s}（可用 single|perobject|both）"),
    };

    private static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { AnsiConsole.MarkupLineInterpolated($"[yellow]無法自動開啟：{ex.Message}[/]"); }
    }
}
