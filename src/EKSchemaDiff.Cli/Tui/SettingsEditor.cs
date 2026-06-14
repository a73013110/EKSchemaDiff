using EKSchemaDiff.Core.Config;
using Spectre.Console;

namespace EKSchemaDiff.Cli.Tui;

/// <summary>
/// profile 的比對/輸出選項設定頁。用 Menu 的 onActivate 就地切換，游標保留、即時更新。
/// 選項依用途分成數個分類（分類標題以分隔列呈現、導覽時自動跳過），
/// 開關值用「顏色＋實心/空心圓」雙重區隔，避免一整片擠在一起難以辨識。
/// </summary>
public static class SettingsEditor
{
    /// <summary>一個可操作的設定項：標題、目前值（已含 markup）、Enter 動作、中文說明。</summary>
    private sealed record Row(string Title, Func<Profile, string> Value, Action<Profile> Activate, string Desc);

    /// <summary>一個分類：標題 + 底下的設定項。</summary>
    private sealed record Section(string Title, Row[] Rows);

    private enum SlotKind { Header, Item, Finish }

    /// <summary>選單上的一格：分類標題、設定項、或「完成」列。</summary>
    private sealed record Slot(SlotKind Kind, string? HeaderTitle = null, Row? Row = null);

    private static readonly Section[] Sections =
    {
        new("安全防護（防誤刪）", new[]
        {
            new Row("忽略權限 GRANT/DENY/REVOKE", p => Toggle(p.CompareOptions.IgnorePermissions),
                p => p.CompareOptions.IgnorePermissions = !p.CompareOptions.IgnorePermissions,
                "開啟後完全不比對也不產生權限語句，杜絕誤刪其他廠商權限的事故（建議開）。"),
            new Row("刪除目標多出的權限", p => Toggle(p.CompareOptions.DropPermissionsNotInSource),
                p => p.CompareOptions.DropPermissionsNotInSource = !p.CompareOptions.DropPermissionsNotInSource,
                "即使比對權限，是否刪除目標比來源多出的權限（建議關）。"),
            new Row("刪除目標多出的物件", p => Toggle(p.CompareOptions.DropObjectsNotInSource),
                p => p.CompareOptions.DropObjectsNotInSource = !p.CompareOptions.DropObjectsNotInSource,
                "是否刪除目標中來源沒有的物件。開啟有誤刪風險（建議關）。"),
            new Row("資料遺失時阻擋部署", p => Toggle(p.CompareOptions.BlockOnPossibleDataLoss),
                p => p.CompareOptions.BlockOnPossibleDataLoss = !p.CompareOptions.BlockOnPossibleDataLoss,
                "當變更可能造成資料遺失時，產生的腳本會阻擋（建議開）。"),
        }),
        new("比對範圍", new[]
        {
            new Row("忽略角色成員", p => Toggle(p.CompareOptions.IgnoreRoleMembership),
                p => p.CompareOptions.IgnoreRoleMembership = !p.CompareOptions.IgnoreRoleMembership,
                "不比對資料庫角色成員資格（建議開）。"),
            new Row("忽略登入 SID", p => Toggle(p.CompareOptions.IgnoreLoginSids),
                p => p.CompareOptions.IgnoreLoginSids = !p.CompareOptions.IgnoreLoginSids,
                "不比對登入帳號的 SID（建議開）。"),
            new Row("比對描述 MS_Description", p => Toggle(!p.CompareOptions.IgnoreExtendedProperties),
                p => p.CompareOptions.IgnoreExtendedProperties = !p.CompareOptions.IgnoreExtendedProperties,
                "是否比對資料表/欄位的描述（擴充屬性）。需要比對描述時請保持開。"),
            new Row("忽略空白差異", p => Toggle(p.CompareOptions.IgnoreWhitespace),
                p => p.CompareOptions.IgnoreWhitespace = !p.CompareOptions.IgnoreWhitespace,
                "比對時忽略排版空白差異（建議開）。"),
            new Row("排除的物件類型", p => Plain(Join(p.CompareOptions.ExcludedObjectTypes)),
                EditExcluded,
                "整類排除不比對的物件（如 Permissions/Users/Logins）。Enter 編輯，逗號分隔。"),
        }),
        new("輸出設定", new[]
        {
            new Row("部署 SQL 輸出形式", p => Plain(p.ExportOptions.DeployScript.ToString()),
                p => p.ExportOptions.DeployScript = Cycle(p.ExportOptions.DeployScript),
                "Both＝完整部署腳本＋逐物件部署檔；Single＝只完整部署腳本；PerObject＝只逐物件部署檔。Enter 循環切換。"),
            new Row("部署資料庫名稱（USE 覆寫）",
                p => Plain(string.IsNullOrWhiteSpace(p.ExportOptions.DeployDatabaseName) ? "(沿用目標庫名)" : p.ExportOptions.DeployDatabaseName!),
                EditDeployDb,
                "腳本頂端 USE [...] 要用的資料庫名。留空＝沿用目標庫名；客戶端實際庫名不同時填這裡。Enter 編輯。"),
            new Row("輸出差異 HTML", p => Toggle(p.ExportOptions.ExportHtml),
                p => p.ExportOptions.ExportHtml = !p.ExportOptions.ExportHtml,
                "是否產生暖色系差異 HTML 報告。"),
            new Row("HTML 差異忽略空白", p => Toggle(p.ExportOptions.HtmlIgnoreWhitespace),
                p => p.ExportOptions.HtmlIgnoreWhitespace = !p.ExportOptions.HtmlIgnoreWhitespace,
                "HTML 逐行差異著色時是否忽略空白。"),
        }),
    };

    /// <summary>展平成選單格序列：每個分類前插入一個標題分隔列，最後加上「完成」列。</summary>
    private static readonly Slot[] Layout = BuildLayout();

    private static Slot[] BuildLayout()
    {
        var slots = new List<Slot>();
        foreach (var s in Sections)
        {
            slots.Add(new Slot(SlotKind.Header, HeaderTitle: s.Title));
            foreach (var r in s.Rows)
                slots.Add(new Slot(SlotKind.Item, Row: r));
        }
        slots.Add(new Slot(SlotKind.Finish));
        return slots.ToArray();
    }

    /// <summary>編輯 profile 選項。就地切換，呼叫端負責存檔。</summary>
    public static void Edit(Profile profile)
    {
        var title = $"[orange3]設定頁[/] · profile [bold]{Markup.Escape(profile.Name)}[/]";

        Menu.Show(
            title,
            () =>
            {
                var list = new List<MenuItem>(Layout.Length);
                foreach (var slot in Layout)
                {
                    switch (slot.Kind)
                    {
                        case SlotKind.Header:
                            list.Add(new MenuItem
                            {
                                Label = $"[orange3]▌[/] [bold orange3]{Markup.Escape(slot.HeaderTitle!)}[/]",
                                IsSeparator = true,
                            });
                            break;
                        case SlotKind.Finish:
                            list.Add(new MenuItem { Label = "[green]✔ 完成並返回[/]", Description = "結束設定頁。" });
                            break;
                        default:
                            var r = slot.Row!;
                            list.Add(new MenuItem
                            {
                                Label = $"{Markup.Escape(ConsoleUi.PadDisplay(r.Title, 30))}  {r.Value(profile)}",
                                Description = r.Desc,
                            });
                            break;
                    }
                }
                return list;
            },
            header: Banner.Show,
            footer: "[grey39]↑↓ 移動 · Enter 切換/編輯 · Esc 返回[/]",
            onActivate: idx =>
            {
                var slot = Layout[idx];
                if (slot.Kind == SlotKind.Finish) return false; // 完成列 → 離開
                if (slot.Kind == SlotKind.Item) slot.Row!.Activate(profile);
                return true; // 就地處理，停留
            });
    }

    /// <summary>開關值：顏色＋實心/空心圓雙重區隔，遠看也能一眼分辨開或關。</summary>
    private static string Toggle(bool v) => v ? "[green]● 開[/]" : "[grey54]○ 關[/]";

    /// <summary>非開關的值（模式、名稱、清單）以黃色呈現，內容先逸出避免 markup 注入。</summary>
    private static string Plain(string text) => $"[yellow]{Markup.Escape(text)}[/]";

    private static string Join(List<string> items) => items.Count == 0 ? "(無)" : string.Join(",", items);

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
        DeployScriptMode.Single => DeployScriptMode.PerObject,
        _ => DeployScriptMode.Both,
    };
}
