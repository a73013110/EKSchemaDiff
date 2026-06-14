using System.ComponentModel;
using EKSchemaDiff.Core.Config;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EKSchemaDiff.Cli.Commands;

public sealed class InitSettings : CommandSettings
{
    [CommandOption("-d|--dir <DIR>")]
    [Description("建立 .eksd.json 的目錄（預設為目前目錄）")]
    public string? Dir { get; init; }

    [CommandOption("-f|--force")]
    [Description("覆寫既有的 .eksd.json")]
    public bool Force { get; init; }
}

public sealed class InitCommand : Command<InitSettings>
{
    protected override int Execute(CommandContext context, InitSettings settings, CancellationToken cancellationToken)
    {
        var dir = settings.Dir ?? Directory.GetCurrentDirectory();
        var path = Path.Combine(dir, ConfigStore.ProjectFileName);

        if (File.Exists(path) && !settings.Force)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]已存在：{path}[/]（用 --force 覆寫）");
            return 1;
        }

        var config = new EksdConfig
        {
            DefaultProfile = "uat2prod",
            Profiles =
            {
                new Profile
                {
                    Name = "uat2prod",
                    Description = "UAT → PROD（請依實際環境修改）",
                    Source = new ConnectionConfig
                    {
                        Label = "UAT 來源",
                        Server = "your-sql-server",
                        Database = "Sample_DB_UAT",
                        Auth = "sql",
                        User = "your_account",
                        Password = "your_password",
                    },
                    Target = new ConnectionConfig
                    {
                        Label = "PROD 目標",
                        Server = "your-sql-server",
                        Database = "Sample_DB_PROD",
                        Auth = "sql",
                        User = "your_account",
                        Password = "your_password",
                    },
                    OutputDir = "比對結果",
                },
            },
        };

        Directory.CreateDirectory(dir);
        File.WriteAllText(path, ConfigStore.Serialize(config), new System.Text.UTF8Encoding(false));
        AnsiConsole.MarkupLineInterpolated($"[green]已建立：{path}[/]");
        AnsiConsole.MarkupLine("請填入 [bold]server / database / user / password[/]，或執行 [bold]eksd[/] 從主選單以互動方式設定。");
        AnsiConsole.MarkupLine("[grey]提示：設定檔放在本機，可直接填明碼；若想避免明碼，password 可用 ${env:變數名}。[/]");
        return 0;
    }
}
