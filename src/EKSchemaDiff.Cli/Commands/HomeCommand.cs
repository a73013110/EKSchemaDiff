using EKSchemaDiff.Cli.Tui;
using EKSchemaDiff.Core.Config;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EKSchemaDiff.Cli.Commands;

/// <summary>預設命令：顯示主選單。給 --profile / --yes 時直接進入比對（快速啟動/CI）。</summary>
public sealed class HomeCommand : Command<RunSettings>
{
    protected override int Execute(CommandContext context, RunSettings settings, CancellationToken cancellationToken)
    {
        // 直接模式：指定 profile 或非互動時，跳過主選單。
        if (!string.IsNullOrWhiteSpace(settings.Profile) || settings.Yes)
        {
            Banner.Show();
            return RunCommand.RunDirect(settings);
        }

        if (!ConsoleUi.Interactive)
        {
            Banner.Show();
            AnsiConsole.MarkupLine("[yellow]目前不是互動終端機，無法顯示主選單。[/]");
            AnsiConsole.MarkupLine("請在終端機直接執行 [bold]eksd[/]，或用 [bold]eksd compare --profile <名稱>[/] 直接比對。");
            return 1;
        }

        while (true)
        {
            ConfigStore store;
            try { store = ConfigStore.Discover(settings.StartDir); }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]設定載入失敗：{ex.Message}[/]");
                return 1;
            }

            int profileCount = store.Effective.Profiles.Count;

            var pick = Menu.Show(
                "[orange3]主選單[/]　[grey]選擇要進行的作業[/]",
                () => new List<MenuItem>
                {
                    new() { Label = "開始比對", Description = profileCount > 0
                        ? "連線來源與目標資料庫，比對結構並匯出部署 SQL 與差異報告。"
                        : "尚未設定任何連線；請先用「新增/編輯連線設定」。" },
                    new() { Label = "設定選項", Description = "調整比對與輸出選項（忽略權限、輸出形式等），每項有中文說明。" },
                    new() { Label = "新增/編輯連線設定", Description = "引導式填入伺服器、資料庫與帳密，存到本機 .eksd.json。" },
                    new() { Label = "列出 profile", Description = "顯示目前已發現的所有 profile 與設定檔位置。" },
                    new() { Label = "離開", Description = "結束程式。" },
                },
                footer: $"[grey39]設定檔：{Markup.Escape(store.ProjectConfigPath ?? "(尚未建立)")}　·　profile：{profileCount}[/]",
                header: Banner.Show);

            if (pick is >= 0 and <= 3) AnsiConsole.Clear();   // 進入動作前清一次（非每次按鍵，不會閃）
            switch (pick)
            {
                case 0: // 開始比對
                    DoCompare(store, settings);
                    break;
                case 1: // 設定選項
                    DoSettings(store);
                    break;
                case 2: // 新增/編輯連線
                    ProfileEditor.GuidedSetup(store);
                    Pause();
                    break;
                case 3: // 列出 profile
                    ListProfiles(store);
                    Pause();
                    break;
                case 4: // 離開
                case -1:
                    AnsiConsole.Clear();
                    AnsiConsole.MarkupLine("[grey]再見。[/]");
                    return 0;
            }
        }
    }

    private static void DoCompare(ConfigStore store, RunSettings settings)
    {
        if (store.Effective.Profiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]尚未設定任何 profile，請先用「新增/編輯連線設定」。[/]");
            Pause();
            return;
        }

        var profile = store.Effective.Profiles.Count == 1
            ? store.Effective.Profiles[0]
            : Prompts.PickProfile(store.Effective.Profiles);

        CompareWorkflow.Run(store, profile, settings.Out, null, interactive: true);
        Pause();
    }

    private static void DoSettings(ConfigStore store)
    {
        // 編輯專案層設定（沒有就退回全域）。
        bool useGlobal = store.ProjectConfigPath is null;
        var config = useGlobal ? store.GlobalConfig : store.ProjectConfig;
        if (config is null || config.Profiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]尚無 profile 可設定，請先用「新增/編輯連線設定」。[/]");
            Pause();
            return;
        }

        var profile = config.Profiles.Count == 1
            ? config.Profiles[0]
            : Prompts.PickProfile(config.Profiles);

        SettingsEditor.Edit(profile);
        var path = useGlobal ? store.SaveGlobal(config) : store.SaveProject(config);
        AnsiConsole.MarkupLineInterpolated($"[green]設定已儲存：{path}[/]");
        Pause();
    }

    private static void ListProfiles(ConfigStore store)
    {
        Banner.Show();
        AnsiConsole.MarkupLineInterpolated($"[grey]專案設定：{store.ProjectConfigPath ?? "(無)"}[/]");
        AnsiConsole.MarkupLineInterpolated($"[grey]全域設定：{store.GlobalConfigPath}[/]");
        if (store.Effective.Profiles.Count == 0) { AnsiConsole.MarkupLine("[yellow](尚無 profile)[/]"); return; }

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey39);
        table.AddColumn("Profile");
        table.AddColumn("來源（更版）");
        table.AddColumn("目標（原版）");
        table.AddColumn("忽略權限");
        table.AddColumn("輸出");
        foreach (var p in store.Effective.Profiles)
        {
            var isDefault = string.Equals(p.Name, store.Effective.DefaultProfile, StringComparison.OrdinalIgnoreCase);
            table.AddRow(
                isDefault ? $"[bold]{Markup.Escape(p.Name)}[/] [grey](預設)[/]" : Markup.Escape(p.Name),
                Markup.Escape(p.Source.ToSafeDisplay()),
                Markup.Escape(p.Target.ToSafeDisplay()),
                p.CompareOptions.IgnorePermissions ? "[green]是[/]" : "[red]否[/]",
                p.ExportOptions.DeployScript.ToString());
        }
        AnsiConsole.Write(table);
    }

    private static void Pause()
    {
        Console.CursorVisible = true;
        AnsiConsole.Markup("[grey]按 Enter 返回主選單…[/]");
        Console.ReadLine();
    }
}
