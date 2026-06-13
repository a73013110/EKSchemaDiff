using EKSchemaDiff.Core.Config;
using Spectre.Console;

namespace EKSchemaDiff.Cli.Tui;

/// <summary>引導式建立/編輯 profile 的連線（server/database/帳密），存回專案層 .eksd.json。</summary>
public static class ProfileEditor
{
    /// <summary>任一欄位按 Esc 時丟出，整個引導流程取消並返回主選單。</summary>
    private sealed class CancelException : Exception { }

    /// <summary>引導式設定一組 profile；存到專案層設定檔。回傳是否有儲存。</summary>
    public static bool GuidedSetup(ConfigStore store)
    {
        AnsiConsole.Clear();
        Banner.Show();
        AnsiConsole.MarkupLine("[orange3]新增 / 編輯連線設定[/]　[grey](設定檔放本機，可直接填明碼)[/]");
        AnsiConsole.MarkupLine("[grey39]逐欄輸入，Enter 確認該欄；任一欄位按 [bold]Esc[/] 取消並返回主選單。[/]");
        AnsiConsole.WriteLine();

        try
        {
            // 以專案層設定為基礎（沒有就新建）。
            var config = store.ProjectConfig ?? new EksdConfig();

            var name = Ask("Profile 名稱：",
                config.Profiles.Count > 0 ? config.Profiles[0].Name : "uat2prod");

            var existing = config.FindProfile(name);
            var profile = existing ?? new Profile { Name = name };
            profile.Name = name;

            AnsiConsole.MarkupLine("\n[grey]── 來源（更版內容的依據；差異報告左側）──[/]");
            profile.Source = PromptConnection(profile.Source);

            AnsiConsole.MarkupLine("\n[grey]── 目標（被更新對象；差異報告右側）──[/]");
            profile.Target = PromptConnection(profile.Target);

            profile.OutputDir = Ask("輸出目錄：",
                string.IsNullOrWhiteSpace(profile.OutputDir) ? "EKSchemaDiff輸出" : profile.OutputDir);

            if (existing is null) config.Profiles.Add(profile);
            if (string.IsNullOrWhiteSpace(config.DefaultProfile)) config.DefaultProfile = profile.Name;

            var dir = store.ProjectConfigPath is not null
                ? Path.GetDirectoryName(store.ProjectConfigPath)
                : Directory.GetCurrentDirectory();
            var path = store.SaveProject(config, dir);
            Console.CursorVisible = false;

            AnsiConsole.MarkupLineInterpolated($"[green]已儲存：{path}[/]");
            return true;
        }
        catch (CancelException)
        {
            Console.CursorVisible = false;
            AnsiConsole.MarkupLine("[yellow]已取消，未儲存。[/]");
            return false;
        }
    }

    private static ConnectionConfig PromptConnection(ConnectionConfig current)
    {
        var c = new ConnectionConfig
        {
            Label = current.Label,
            TrustServerCertificate = true,
        };

        c.Server = Ask("  伺服器（名稱或 IP）：", current.Server);
        c.Database = Ask("  資料庫名稱：", current.Database);

        var defaultAuth = string.Equals(current.Auth, "integrated", StringComparison.OrdinalIgnoreCase) ? "2" : "1";
        string authChoice;
        while (true)
        {
            authChoice = Ask("  驗證方式（1=SQL 帳密登入　2=Windows 整合驗證）：", defaultAuth).Trim();
            if (authChoice is "1" or "2") break;
            AnsiConsole.MarkupLine("  [yellow]請輸入 1 或 2。[/]");
        }
        c.Auth = authChoice == "2" ? "integrated" : "sql";

        if (c.IsSqlAuth)
        {
            c.User = Ask("  帳號：", current.User);
            c.Password = Ask("  密碼（直接存於本機設定檔）：", current.Password);
        }

        return c;
    }

    /// <summary>讀一個文字欄位；按 Esc 取消整個流程。空白沿用預設。</summary>
    private static string Ask(string label, string? defaultValue)
    {
        var input = ConsoleUi.ReadLineOrEsc($"[white]{Markup.Escape(label)}[/]", defaultValue);
        if (input is null) throw new CancelException();
        return input;
    }
}
