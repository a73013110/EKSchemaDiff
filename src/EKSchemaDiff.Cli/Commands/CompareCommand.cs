using System.ComponentModel;
using Spectre.Console.Cli;

namespace EKSchemaDiff.Cli.Commands;

public sealed class CompareSettings : CommandSettings
{
    [CommandOption("-p|--profile <NAME>")]
    [Description("要使用的 profile 名稱（指定時跳過主選單，直接比對）")]
    public string? Profile { get; init; }

    [CommandOption("-o|--out <DIR>")]
    [Description("輸出目錄（覆寫 profile 設定）")]
    public string? Out { get; init; }

    [CommandOption("-e|--export <MODE>")]
    [Description("部署 SQL 輸出：single | perobject | both")]
    public string? Export { get; init; }

    [CommandOption("-y|--yes")]
    [Description("非互動：沿用引擎建議的勾選與 profile 設定，直接匯出")]
    public bool Yes { get; init; }

    [CommandOption("--start-dir <DIR>")]
    [Description("設定檔探索起始目錄（預設為目前目錄）")]
    public string? StartDir { get; init; }
}

/// <summary>compare 命令：直接比對並匯出（不經主選單）。</summary>
public sealed class CompareCommand : Command<CompareSettings>
{
    private readonly Banner _banner;
    private readonly CompareWorkflow _workflow;

    public CompareCommand(Banner banner, CompareWorkflow workflow)
    {
        _banner = banner;
        _workflow = workflow;
    }

    protected override int Execute(CommandContext context, CompareSettings settings, CancellationToken cancellationToken)
    {
        _banner.Show();
        return _workflow.RunFromConfig(
            settings.StartDir, settings.Profile, settings.Out, settings.Export, interactive: !settings.Yes);
    }
}
