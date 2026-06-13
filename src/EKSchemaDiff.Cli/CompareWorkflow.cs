using System.Diagnostics;
using EKSchemaDiff.Cli.Tui;
using EKSchemaDiff.Core.Compare;
using EKSchemaDiff.Core.Config;
using EKSchemaDiff.Core.Export;
using Spectre.Console;

namespace EKSchemaDiff.Cli;

/// <summary>比對 → 預覽勾選 → 匯出的共用流程，供主選單與 compare 命令使用。</summary>
public static class CompareWorkflow
{
    public static int Run(
        ConfigStore store, Profile profile,
        string? outOverride, DeployScriptMode? exportOverride, bool interactive)
    {
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
            AnsiConsole.MarkupLineInterpolated($"[red]比對失敗：{ex.Message}[/]");
            return 2;
        }

        if (session is null || !session.IsValid)
        {
            AnsiConsole.MarkupLine("[red]比對結果無效。[/]");
            foreach (var e in session?.GetErrors() ?? Enumerable.Empty<string>())
                AnsiConsole.MarkupLineInterpolated($"  [red]- {e}[/]");
            return 2;
        }

        foreach (var u in session.UnrecognizedExcludedTypes)
            AnsiConsole.MarkupLineInterpolated($"[yellow]警告：排除類型無法辨識，已忽略：{u}[/]");

        if (session.Differences.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]兩個資料庫結構一致，沒有差異。[/]");
            return 0;
        }

        ShowDiffOverview(session);

        var exportMode = exportOverride ?? profile.ExportOptions.DeployScript;

        if (interactive)
        {
            var included = ReviewScreen.Run(session.Differences, profile.ExportOptions.HtmlIgnoreWhitespace);
            if (included is null)
            {
                AnsiConsole.MarkupLine("[yellow]已取消，未匯出。[/]");
                return 0;
            }
            session.ApplyInclusion(included);
            if (included.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]未勾選任何物件，已取消匯出。[/]");
                return 0;
            }
            if (exportOverride is null)
                exportMode = Prompts.SelectExportMode(exportMode);
        }

        profile.ExportOptions.DeployScript = exportMode;
        var outputDir = ResolveOutputDir(store, profile, outOverride);

        ExportSummary summary;
        try
        {
            ExportSummary? captured = null;
            AnsiConsole.Status().Spinner(Spinner.Known.Dots)
                .Start("正在產生部署 SQL 與差異報告…", _ =>
                {
                    captured = Exporter.Export(session!, outputDir, DateTime.Now);
                });
            summary = captured!;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]匯出失敗：{ex.Message}[/]");
            return 3;
        }

        ShowExportSummary(summary);

        if (interactive && summary.HtmlReportCount > 0)
        {
            var overview = Path.Combine(outputDir, "差異報告", "00_差異比對總覽.html");
            if (File.Exists(overview) && AnsiConsole.Confirm("要開啟差異比對總覽 HTML 嗎？", defaultValue: false))
                OpenFile(overview);
        }

        return summary.SplitVerificationPassed ? 0 : 4;
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
            table.AddRow("單一部署 SQL", Markup.Escape(summary.FullScriptPath));
        if (summary.SplitFileCount > 0)
        {
            var v = summary.SplitVerificationPassed
                ? "[green]嚴格驗證通過[/]"
                : $"[red]驗證未過：{Markup.Escape(summary.SplitVerificationMessage ?? "")}[/]";
            table.AddRow("依序切分檔", $"{summary.SplitFileCount} 個　{v}");
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
        "split" => DeployScriptMode.SplitOrdered,
        "both" => DeployScriptMode.Both,
        _ => throw new InvalidOperationException($"未知的 --export 值：{s}（可用 single|split|both）"),
    };

    private static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { AnsiConsole.MarkupLineInterpolated($"[yellow]無法自動開啟：{ex.Message}[/]"); }
    }
}
