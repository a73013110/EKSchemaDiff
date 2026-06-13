using EKSchemaDiff.Cli.Tui;
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

        AnsiConsole.Write(ProfileTable.Build(store.Effective.Profiles, store.Effective.DefaultProfile));
        return 0;
    }
}
