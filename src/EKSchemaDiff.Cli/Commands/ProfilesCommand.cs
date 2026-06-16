using EKSchemaDiff.Cli.Tui;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EKSchemaDiff.Cli.Commands;

public sealed class ProfilesCommand : Command
{
    private readonly ConfigStoreFactory _configStores;

    public ProfilesCommand(ConfigStoreFactory configStores) => _configStores = configStores;

    protected override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        var store = _configStores.Discover();
        if (store.Effective.Profiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{Theme.Warning}]尚無 profile。請執行 eksd init。[/]");
            return ExitCode.UsageError;
        }

        AnsiConsole.MarkupLineInterpolated($"[{Theme.TextMuted}]專案設定：{store.ProjectConfigPath ?? "(無)"}[/]");
        AnsiConsole.MarkupLineInterpolated($"[{Theme.TextMuted}]全域設定：{store.GlobalConfigPath}[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.Write(ProfileTable.Build(store.Effective.Profiles, store.Effective.DefaultProfile));
        return ExitCode.Ok;
    }
}
