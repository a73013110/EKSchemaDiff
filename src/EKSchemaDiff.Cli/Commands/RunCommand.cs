using System.ComponentModel;
using EKSchemaDiff.Cli.Tui;
using EKSchemaDiff.Core.Config;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EKSchemaDiff.Cli.Commands;

public sealed class RunSettings : CommandSettings
{
    [CommandOption("-p|--profile <NAME>")]
    [Description("要使用的 profile 名稱（指定時跳過主選單，直接比對）")]
    public string? Profile { get; init; }

    [CommandOption("-o|--out <DIR>")]
    [Description("輸出目錄（覆寫 profile 設定）")]
    public string? Out { get; init; }

    [CommandOption("-e|--export <MODE>")]
    [Description("部署 SQL 輸出：single | split | both")]
    public string? Export { get; init; }

    [CommandOption("-y|--yes")]
    [Description("非互動：沿用引擎建議的勾選與 profile 設定，直接匯出")]
    public bool Yes { get; init; }

    [CommandOption("--start-dir <DIR>")]
    [Description("設定檔探索起始目錄（預設為目前目錄）")]
    public string? StartDir { get; init; }
}

/// <summary>compare 命令：直接比對並匯出（不經主選單）。</summary>
public sealed class RunCommand : Command<RunSettings>
{
    protected override int Execute(CommandContext context, RunSettings settings, CancellationToken cancellationToken)
    {
        Banner.Show();
        return RunDirect(settings);
    }

    /// <summary>直接執行比對流程（供 compare 命令與主選單的快速模式共用）。</summary>
    public static int RunDirect(RunSettings settings)
    {
        ConfigStore store;
        try { store = ConfigStore.Discover(settings.StartDir); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]設定載入失敗：{ex.Message}[/]");
            return 1;
        }

        if (store.Effective.Profiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]尚未設定任何 profile。[/]");
            AnsiConsole.MarkupLine("請執行 [bold]eksd[/] 從主選單建立連線設定，或 [bold]eksd init[/] 產生範本。");
            return 1;
        }

        Profile profile;
        try
        {
            profile = store.ResolveProfile(settings.Profile)
                      ?? (settings.Yes
                          ? throw new InvalidOperationException("有多組 profile，請以 --profile 指定。")
                          : Prompts.PickProfile(store.Effective.Profiles));
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }

        DeployScriptMode? exportOverride = string.IsNullOrWhiteSpace(settings.Export)
            ? null
            : CompareWorkflow.ParseExportMode(settings.Export!);

        return CompareWorkflow.Run(store, profile, settings.Out, exportOverride, interactive: !settings.Yes);
    }
}
