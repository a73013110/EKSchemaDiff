using EKSchemaDiff.Core.Config;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EKSchemaDiff.Cli.Commands;

public sealed class ProfilesCommand : Command
{
    protected override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        var store = ConfigStore.Discover();
        if (store.Effective.Profiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]尚無 profile。請執行 eksd init。[/]");
            return 1;
        }

        AnsiConsole.MarkupLineInterpolated($"[grey]專案設定：{store.ProjectConfigPath ?? "(無)"}[/]");
        AnsiConsole.MarkupLineInterpolated($"[grey]全域設定：{store.GlobalConfigPath}[/]");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey39);
        table.AddColumn("Profile");
        table.AddColumn("來源（更版）");
        table.AddColumn("目標（原版）");
        table.AddColumn("忽略權限");
        table.AddColumn("輸出");

        foreach (var p in store.Effective.Profiles)
        {
            var isDefault = string.Equals(p.Name, store.Effective.DefaultProfile, StringComparison.OrdinalIgnoreCase);
            var name = isDefault ? $"[bold]{Markup.Escape(p.Name)}[/] [grey](預設)[/]" : Markup.Escape(p.Name);
            table.AddRow(
                name,
                Markup.Escape(p.Source.ToSafeDisplay()),
                Markup.Escape(p.Target.ToSafeDisplay()),
                p.CompareOptions.IgnorePermissions ? "[green]是[/]" : "[red]否[/]",
                p.ExportOptions.DeployScript.ToString());
        }
        AnsiConsole.Write(table);
        return 0;
    }
}
