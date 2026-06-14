using EKSchemaDiff.Cli.Tui;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EKSchemaDiff.Cli.Commands;

/// <summary>預設命令：顯示主選單。給 --profile / --yes 時直接進入比對（快速啟動/CI）。</summary>
public sealed class HomeCommand : Command<CompareSettings>
{
    private readonly Banner _banner;
    private readonly IAppLog _log;
    private readonly ConfigStoreFactory _configStores;
    private readonly CompareWorkflow _workflow;

    public HomeCommand(Banner banner, IAppLog log, ConfigStoreFactory configStores, CompareWorkflow workflow)
    {
        _banner = banner;
        _log = log;
        _configStores = configStores;
        _workflow = workflow;
    }

    protected override int Execute(CommandContext context, CompareSettings settings, CancellationToken cancellationToken)
    {
        // 直接模式：指定 profile 或非互動時，跳過主選單。
        if (!string.IsNullOrWhiteSpace(settings.Profile) || settings.Yes)
        {
            _banner.Show();
            return _workflow.RunFromConfig(
                settings.StartDir, settings.Profile, settings.Out, settings.Export, interactive: !settings.Yes);
        }

        if (!ConsoleUI.Interactive)
        {
            _banner.Show();
            AnsiConsole.MarkupLine("[yellow]目前不是互動終端機，無法顯示主選單。[/]");
            AnsiConsole.MarkupLine("請在終端機直接執行 [bold]eksd[/]，或用 [bold]eksd compare --profile <名稱>[/] 直接比對。");
            return ExitCode.UsageError;
        }

        while (true)
        {
            ConfigStore store;
            try { store = _configStores.Discover(settings.StartDir); }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]設定載入失敗：{ex.Message}[/]");
                return ExitCode.UsageError;
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
                header: _banner.Show,
                footer: $"[grey39]設定檔：{Markup.Escape(store.ProjectConfigPath ?? "(尚未建立)")}　·　profile：{profileCount}[/]");

            if (pick is >= 0 and <= 3) AnsiConsole.Clear();   // 進入動作前清一次（非每次按鍵，不會閃）
            switch (pick)
            {
                case 0: // 開始比對
                    Guard("開始比對", () => DoCompare(store, settings));
                    break;
                case 1: // 設定選項
                    Guard("設定選項", () => DoSettings(store));
                    break;
                case 2: // 新增/編輯連線
                    Guard("新增/編輯連線", () => { ProfileEditor.GuidedSetup(store, _banner); Pause(); });
                    break;
                case 3: // 列出 profile
                    Guard("列出 profile", () => { ListProfiles(store); Pause(); });
                    break;
                case 4: // 離開
                case -1:
                    AnsiConsole.Clear();
                    AnsiConsole.MarkupLine("[grey]再見。[/]");
                    _log.Info("使用者由主選單離開");
                    return ExitCode.Ok;
            }
        }
    }

    /// <summary>
    /// 執行一個主選單動作；攔截任何例外、寫入 log 並提示，然後回到主選單而非讓整個程式關閉。
    /// 這是「比對後 Enter 程式整個關掉」這類問題的保險：就算動作內部崩潰，使用者仍留在選單且看得到原因。
    /// </summary>
    private void Guard(string action, Action body)
    {
        _log.Step($"進入動作：{action}");
        try
        {
            body();
            _log.Step($"完成動作：{action}");
        }
        catch (Exception ex)
        {
            _log.Error($"動作「{action}」發生未處理例外", ex);
            try { Console.CursorVisible = true; } catch { }
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLineInterpolated($"[red]「{action}」發生錯誤：{ex.Message}[/]");
            if (_log.FilePath is not null)
                AnsiConsole.MarkupLineInterpolated($"[grey]完整堆疊已寫入記錄檔：{_log.FilePath}[/]");
            Pause();
        }
    }

    private void DoCompare(ConfigStore store, CompareSettings settings)
    {
        if (store.Effective.Profiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]尚未設定任何 profile，請先用「新增/編輯連線設定」。[/]");
            Pause();
            return;
        }

        var profile = Prompts.PickProfile(store.Effective.Profiles, _banner);
        if (profile is null) return;   // 按 Esc 取消挑選，回主選單

        _workflow.Run(store, profile, settings.Out, null, interactive: true);
        Pause();
    }

    private void DoSettings(ConfigStore store)
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

        var profile = Prompts.PickProfile(config.Profiles, _banner);
        if (profile is null) return;   // 按 Esc 取消挑選，回主選單

        SettingsEditor.Edit(profile, _banner);
        var path = useGlobal ? store.SaveGlobal(config) : store.SaveProject(config);
        AnsiConsole.MarkupLineInterpolated($"[green]設定已儲存：{path}[/]");
        Pause();
    }

    private void ListProfiles(ConfigStore store)
    {
        _banner.Show();
        AnsiConsole.MarkupLineInterpolated($"[grey]專案設定：{store.ProjectConfigPath ?? "(無)"}[/]");
        AnsiConsole.MarkupLineInterpolated($"[grey]全域設定：{store.GlobalConfigPath}[/]");
        if (store.Effective.Profiles.Count == 0) { AnsiConsole.MarkupLine("[yellow](尚無 profile)[/]"); return; }

        AnsiConsole.Write(ProfileTable.Build(store.Effective.Profiles, store.Effective.DefaultProfile));
    }

    private static void Pause()
    {
        Console.CursorVisible = true;
        AnsiConsole.Markup("[grey]按 Enter 返回主選單…[/]");
        Console.ReadLine();
    }
}
