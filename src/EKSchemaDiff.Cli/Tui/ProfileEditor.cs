using EKSchemaDiff.Core.Config;
using Spectre.Console;

namespace EKSchemaDiff.Cli.Tui;

/// <summary>引導式建立/編輯 profile 的連線（server/database/帳密），存回專案層 .eksd.json。</summary>
public static class ProfileEditor
{
    private const string CancelToken = ":q";

    /// <summary>任一欄位輸入 :q 時丟出，整個引導流程取消。</summary>
    private sealed class CancelException : Exception { }

    /// <summary>引導式設定一組 profile；存到專案層設定檔。回傳是否有儲存。</summary>
    public static bool GuidedSetup(ConfigStore store)
    {
        Console.CursorVisible = true;
        AnsiConsole.Clear();
        Banner.Show();
        AnsiConsole.MarkupLine("[orange3]新增 / 編輯連線設定[/]　[grey](設定檔放本機，可直接填明碼)[/]");
        AnsiConsole.MarkupLine($"[grey39]任一欄位輸入 [bold]{CancelToken}[/] 可隨時取消並返回主選單。[/]");

        try
        {
            // 以專案層設定為基礎（沒有就新建）。
            var config = store.ProjectConfig ?? new EksdConfig();

            var name = AskText("Profile 名稱：",
                config.Profiles.Count > 0 ? config.Profiles[0].Name : "uat2prod");

            var existing = config.FindProfile(name);
            var profile = existing ?? new Profile { Name = name };
            profile.Name = name;

            AnsiConsole.MarkupLine("\n[grey]── 來源（更版內容的依據；差異報告左側）──[/]");
            profile.Source = PromptConnection(profile.Source);

            AnsiConsole.MarkupLine("\n[grey]── 目標（被更新對象；差異報告右側）──[/]");
            profile.Target = PromptConnection(profile.Target);

            profile.OutputDir = AskText("輸出目錄：",
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

        c.Server = AskText("  伺服器（名稱或 IP）：", current.Server);
        c.Database = AskText("  資料庫名稱：", current.Database);

        var auth = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("  驗證方式：")
                .AddChoices("SQL 帳密登入", "Windows 整合驗證", $"{CancelToken} 取消"));
        if (auth.StartsWith(CancelToken)) throw new CancelException();
        c.Auth = auth.StartsWith("SQL") ? "sql" : "integrated";

        if (c.IsSqlAuth)
        {
            c.User = AskText("  帳號：", current.User);
            c.Password = AskText("  密碼（直接存於本機設定檔）：", current.Password);
        }

        return c;
    }

    /// <summary>讀一個文字欄位；輸入 :q 取消整個流程。空白沿用預設。</summary>
    private static string AskText(string label, string? defaultValue)
    {
        var prompt = new TextPrompt<string>(label).AllowEmpty();
        if (!string.IsNullOrWhiteSpace(defaultValue)) prompt.DefaultValue(defaultValue!);
        var input = AnsiConsole.Prompt(prompt);
        if (string.Equals(input?.Trim(), CancelToken, StringComparison.OrdinalIgnoreCase))
            throw new CancelException();
        return input ?? string.Empty;
    }
}
