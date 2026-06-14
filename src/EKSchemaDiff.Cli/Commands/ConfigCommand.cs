using System.ComponentModel;
using EKSchemaDiff.Cli.Tui;
using EKSchemaDiff.Core.Config;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EKSchemaDiff.Cli.Commands;

public sealed class ConfigSettings : CommandSettings
{
    [CommandOption("-p|--profile <NAME>")]
    [Description("要編輯的 profile 名稱")]
    public string? Profile { get; init; }

    [CommandOption("-g|--global")]
    [Description("編輯全域設定（預設優先編輯專案設定）")]
    public bool Global { get; init; }
}

/// <summary>config 命令：開啟設定頁編輯 profile 的比對與輸出選項，存回設定檔。</summary>
public sealed class ConfigCommand : Command<ConfigSettings>
{
    protected override int Execute(CommandContext context, ConfigSettings settings, CancellationToken cancellationToken)
    {
        var store = ConfigStore.Discover();
        bool useGlobal = settings.Global || store.ProjectConfigPath is null;

        var config = useGlobal ? store.GlobalConfig : store.ProjectConfig;
        if (config is null || config.Profiles.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]{(useGlobal ? "全域" : "專案")}設定沒有任何 profile。請先執行 eksd（主選單）建立連線，或 eksd init。[/]");
            return 1;
        }

        var profile = settings.Profile is not null
            ? config.FindProfile(settings.Profile) ?? throw new InvalidOperationException($"找不到 profile：{settings.Profile}")
            : Prompts.PickProfile(config.Profiles);
        if (profile is null) return 0;   // 按 Esc 取消挑選

        SettingsEditor.Edit(profile);

        var path = useGlobal ? store.SaveGlobal(config) : store.SaveProject(config);
        AnsiConsole.MarkupLineInterpolated($"[green]已儲存：{path}[/]");
        return 0;
    }
}
