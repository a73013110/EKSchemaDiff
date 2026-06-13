using EKSchemaDiff.Core.Config;
using Spectre.Console;

namespace EKSchemaDiff.Cli.Tui;

/// <summary>
/// profile 的比對/輸出選項設定頁。用 Menu 的 onActivate 就地切換，游標保留、即時更新。
/// 每個選項都有中文說明。
/// </summary>
public static class SettingsEditor
{
    private sealed record Row(string Title, Func<Profile, string> Value, Action<Profile> Activate, string Desc);

    private static readonly Row[] Rows =
    {
        new("忽略權限 GRANT/DENY/REVOKE", p => OnOff(p.CompareOptions.IgnorePermissions),
            p => p.CompareOptions.IgnorePermissions = !p.CompareOptions.IgnorePermissions,
            "開啟後完全不比對也不產生權限語句，杜絕誤刪其他廠商權限的事故（建議開）。"),
        new("刪除目標多出的權限", p => OnOff(p.CompareOptions.DropPermissionsNotInSource),
            p => p.CompareOptions.DropPermissionsNotInSource = !p.CompareOptions.DropPermissionsNotInSource,
            "即使比對權限，是否刪除目標比來源多出的權限（建議關）。"),
        new("忽略角色成員", p => OnOff(p.CompareOptions.IgnoreRoleMembership),
            p => p.CompareOptions.IgnoreRoleMembership = !p.CompareOptions.IgnoreRoleMembership,
            "不比對資料庫角色成員資格（建議開）。"),
        new("忽略登入 SID", p => OnOff(p.CompareOptions.IgnoreLoginSids),
            p => p.CompareOptions.IgnoreLoginSids = !p.CompareOptions.IgnoreLoginSids,
            "不比對登入帳號的 SID（建議開）。"),
        new("比對描述 MS_Description", p => OnOff(!p.CompareOptions.IgnoreExtendedProperties),
            p => p.CompareOptions.IgnoreExtendedProperties = !p.CompareOptions.IgnoreExtendedProperties,
            "是否比對資料表/欄位的描述（擴充屬性）。需要比對描述時請保持開。"),
        new("資料遺失時阻擋部署", p => OnOff(p.CompareOptions.BlockOnPossibleDataLoss),
            p => p.CompareOptions.BlockOnPossibleDataLoss = !p.CompareOptions.BlockOnPossibleDataLoss,
            "當變更可能造成資料遺失時，產生的腳本會阻擋（建議開）。"),
        new("刪除目標多出的物件", p => OnOff(p.CompareOptions.DropObjectsNotInSource),
            p => p.CompareOptions.DropObjectsNotInSource = !p.CompareOptions.DropObjectsNotInSource,
            "是否刪除目標中來源沒有的物件。開啟有誤刪風險（建議關）。"),
        new("忽略空白差異", p => OnOff(p.CompareOptions.IgnoreWhitespace),
            p => p.CompareOptions.IgnoreWhitespace = !p.CompareOptions.IgnoreWhitespace,
            "比對時忽略排版空白差異（建議開）。"),
        new("排除的物件類型", p => Join(p.CompareOptions.ExcludedObjectTypes),
            EditExcluded,
            "整類排除不比對的物件（如 Permissions/Users/Logins）。Enter 編輯，逗號分隔。"),
        new("部署 SQL 輸出形式", p => p.ExportOptions.DeployScript.ToString(),
            p => p.ExportOptions.DeployScript = Cycle(p.ExportOptions.DeployScript),
            "Both＝單一檔＋切分檔；Single＝只單一檔；SplitOrdered＝只切分檔。Enter 循環切換。"),
        new("部署資料庫名稱（USE 覆寫）", p => string.IsNullOrWhiteSpace(p.ExportOptions.DeployDatabaseName) ? "(沿用目標庫名)" : p.ExportOptions.DeployDatabaseName!,
            EditDeployDb,
            "腳本頂端 USE [...] 要用的資料庫名。留空＝沿用目標庫名；客戶端實際庫名不同時填這裡。Enter 編輯。"),
        new("同物件批次併入同一檔", p => OnOff(p.ExportOptions.GroupSplitByObject),
            p => p.ExportOptions.GroupSplitByObject = !p.ExportOptions.GroupSplitByObject,
            "開啟後，同一物件的多個批次（如資料表 DDL 與其描述）會合併到同一個切分檔（建議開）。"),
        new("輸出差異 HTML", p => OnOff(p.ExportOptions.ExportHtml),
            p => p.ExportOptions.ExportHtml = !p.ExportOptions.ExportHtml,
            "是否產生暖色系差異 HTML 報告。"),
        new("HTML 差異忽略空白", p => OnOff(p.ExportOptions.HtmlIgnoreWhitespace),
            p => p.ExportOptions.HtmlIgnoreWhitespace = !p.ExportOptions.HtmlIgnoreWhitespace,
            "HTML 逐行差異著色時是否忽略空白。"),
    };

    /// <summary>編輯 profile 選項。回傳是否有變動（呼叫端決定是否存檔）。</summary>
    public static void Edit(Profile profile)
    {
        var title = $"[orange3]設定頁[/] · profile [bold]{Markup.Escape(profile.Name)}[/]";

        Menu.Show(
            title,
            () =>
            {
                var list = new List<MenuItem>();
                foreach (var r in Rows)
                    list.Add(new MenuItem
                    {
                        Label = $"{Markup.Escape(PadTitle(r.Title))}  [yellow]{Markup.Escape(r.Value(profile))}[/]",
                        Description = r.Desc,
                    });
                list.Add(new MenuItem { Label = "[green]完成並返回[/]", Description = "結束設定頁。" });
                return list;
            },
            footer: "[grey39]↑↓ 移動 · Enter 切換/編輯 · Esc 返回[/]",
            onActivate: idx =>
            {
                if (idx >= Rows.Length) return false; // 完成列 → 離開
                Rows[idx].Activate(profile);
                return true; // 就地處理，停留
            },
            header: Banner.Show);
    }

    private static string OnOff(bool v) => v ? "開" : "關";
    private static string Join(List<string> items) => items.Count == 0 ? "(無)" : string.Join(",", items);
    private static string PadTitle(string t) => t.Length >= 26 ? t : t.PadRight(26);

    private static void EditExcluded(Profile p)
    {
        Console.CursorVisible = true;
        var input = AnsiConsole.Prompt(
            new TextPrompt<string>("排除的物件類型（逗號分隔，留空表示不排除）：")
                .AllowEmpty()
                .DefaultValue(string.Join(",", p.CompareOptions.ExcludedObjectTypes)));
        p.CompareOptions.ExcludedObjectTypes = input
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        Console.CursorVisible = false;
    }

    private static void EditDeployDb(Profile p)
    {
        Console.CursorVisible = true;
        var input = AnsiConsole.Prompt(
            new TextPrompt<string>("部署資料庫名稱（USE，留空＝沿用目標庫名）：")
                .AllowEmpty()
                .DefaultValue(p.ExportOptions.DeployDatabaseName ?? ""));
        p.ExportOptions.DeployDatabaseName = string.IsNullOrWhiteSpace(input) ? null : input.Trim();
        Console.CursorVisible = false;
    }

    private static DeployScriptMode Cycle(DeployScriptMode m) => m switch
    {
        DeployScriptMode.Both => DeployScriptMode.Single,
        DeployScriptMode.Single => DeployScriptMode.SplitOrdered,
        _ => DeployScriptMode.Both,
    };
}
