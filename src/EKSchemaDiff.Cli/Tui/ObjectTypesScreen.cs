using EKSchemaDiff.Core.Config;
using Spectre.Console;

namespace EKSchemaDiff.Cli.Tui;

/// <summary>
/// 「物件型別」勾選頁（對應 VS 結構描述比較選項的「物件型別」分頁）：
/// 勾選＝納入比對，取消勾選＝整類排除。分「應用程式範圍／非應用程式範圍」兩段（可篩選、可搜尋）。
/// 空白切換、0/1/2 切換範圍、/ 搜尋、R 還原預設、Enter/Esc 套用並返回。
/// 寫回 <see cref="CompareOptions.ExcludedObjectTypes"/>（存檔由上層負責）。
/// </summary>
public static class ObjectTypesScreen
{
    private enum Scope { App, NonApp }

    /// <summary>一個物件類型項：DacFx 列舉名（寫回用）、中文標題、所屬範圍。</summary>
    private sealed record Entry(string Name, string Zh, Scope Scope);

    /// <summary>中文標題覆蓋（取自 VS 物件型別頁）；未列出者顯示原列舉名。</summary>
    private static readonly Dictionary<string, string> Zh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tables"] = "資料表", ["Views"] = "檢視", ["StoredProcedures"] = "預存程序",
        ["ScalarValuedFunctions"] = "純量值函式", ["TableValuedFunctions"] = "資料表值函式",
        ["Synonyms"] = "同義資料表", ["Sequences"] = "序列", ["Defaults"] = "預設",
        ["ExtendedProperties"] = "擴充屬性", ["Filegroups"] = "檔案群組",
        ["Files"] = "檔案", ["FileTables"] = "FileTable",
        ["PartitionFunctions"] = "資料分割函式", ["PartitionSchemes"] = "資料分割配置",
        ["FullTextCatalogs"] = "全文檢索目錄", ["FullTextStoplists"] = "全文檢索停用字詞表",
        ["SearchPropertyLists"] = "搜尋屬性清單", ["Rules"] = "規則", ["Aggregates"] = "彙總",
        ["Assemblies"] = "組件", ["AssemblyFiles"] = "組件檔案",
        ["Contracts"] = "合約", ["Services"] = "服務", ["Queues"] = "佇列",
        ["MessageTypes"] = "訊息類型", ["BrokerPriorities"] = "Broker 優先權",
        ["RemoteServiceBindings"] = "遠端服務繫結", ["SecurityPolicies"] = "安全性原則",
        ["Certificates"] = "憑證", ["SymmetricKeys"] = "對稱金鑰", ["AsymmetricKeys"] = "非對稱金鑰",
        ["Signatures"] = "簽章",
        ["UserDefinedDataTypes"] = "使用者定義資料類型", ["UserDefinedTableTypes"] = "使用者定義資料表型別",
        ["ClrUserDefinedTypes"] = "使用者定義型別 (CLR)", ["XmlSchemaCollections"] = "XML 結構描述集合",
        ["ColumnEncryptionKeys"] = "資料行加密金鑰", ["ColumnMasterKeys"] = "資料行主要金鑰",
        ["ExternalDataSources"] = "外部資料來源", ["ExternalTables"] = "外部資料表",
        ["ExternalFileFormats"] = "外部檔案格式", ["ExternalLibraries"] = "外部程式庫",
        ["ExternalLanguages"] = "外部語言", ["ExternalModels"] = "外部模型",
        ["ExternalStreams"] = "外部串流", ["ExternalStreamingJobs"] = "外部串流工作",
        ["DatabaseTriggers"] = "資料庫觸發程序",
        // 安全/帳號（多為預設排除）
        ["Permissions"] = "權限", ["Users"] = "使用者", ["DatabaseRoles"] = "資料庫角色",
        ["ApplicationRoles"] = "應用程式角色", ["RoleMembership"] = "角色成員資格",
        // 非應用程式範圍
        ["Logins"] = "登入", ["Credentials"] = "認證", ["LinkedServers"] = "連結的伺服器",
        ["LinkedServerLogins"] = "連結的伺服器登入", ["Endpoints"] = "端點", ["Routes"] = "路由",
        ["Audits"] = "稽核", ["ServerAuditSpecifications"] = "伺服器稽核規格",
        ["DatabaseAuditSpecifications"] = "資料庫稽核規格", ["ServerRoles"] = "伺服器角色",
        ["ServerRoleMembership"] = "伺服器角色成員資格", ["ServerTriggers"] = "伺服器觸發程序",
        ["ErrorMessages"] = "錯誤訊息", ["EventNotifications"] = "事件告知",
        ["EventSessions"] = "事件工作階段", ["CryptographicProviders"] = "密碼編譯提供者",
        ["DatabaseEncryptionKeys"] = "資料庫加密金鑰", ["DatabaseScopedCredentials"] = "資料庫範圍認證",
        ["MasterKeys"] = "主要金鑰", ["DatabaseOptions"] = "資料庫選項",
        ["DatabaseWorkloadGroups"] = "資料庫工作負載群組", ["WorkloadClassifiers"] = "工作負載分類器",
    };

    /// <summary>歸入「非應用程式範圍」的列舉名（伺服器層級／安全帳號）；其餘視為應用程式範圍。</summary>
    private static readonly HashSet<string> NonAppScope = new(StringComparer.OrdinalIgnoreCase)
    {
        "Logins", "Credentials", "LinkedServers", "LinkedServerLogins", "Endpoints", "Routes",
        "Audits", "ServerAuditSpecifications", "DatabaseAuditSpecifications", "ServerRoles",
        "ServerRoleMembership", "ServerTriggers", "ErrorMessages", "EventNotifications",
        "EventSessions", "CryptographicProviders", "DatabaseEncryptionKeys",
        "DatabaseScopedCredentials", "MasterKeys", "DatabaseOptions",
        "DatabaseWorkloadGroups", "WorkloadClassifiers",
    };

    private static readonly Entry[] AllEntries = BuildEntries();

    private static Entry[] BuildEntries()
    {
        return ObjectTypeCatalog.AllNames
            .Select(n => new Entry(n, Zh.TryGetValue(n, out var z) ? z : n,
                                   NonAppScope.Contains(n) ? Scope.NonApp : Scope.App))
            .OrderBy(e => e.Scope)
            .ThenBy(e => e.Zh, StringComparer.CurrentCulture)
            .ToArray();
    }

    public static void Edit(Profile profile)
    {
        if (!ConsoleUI.Interactive) return;

        // excluded = 取消勾選（不納入比對）的列舉名集合。
        var excluded = new HashSet<string>(profile.CompareOptions.ExcludedObjectTypes, StringComparer.OrdinalIgnoreCase);

        Scope? filter = null;           // null=全部
        string search = string.Empty;
        bool searchMode = false;
        var view = BuildView(filter, search);
        int cursor = 0;

        AnsiConsole.Clear();
        try
        {
            ConsoleUI.EnterRedrawMode();
            while (true)
            {
                Render(excluded, view, cursor, filter, search, searchMode);
                var key = Console.ReadKey(intercept: true);

                if (searchMode)
                {
                    switch (key.Key)
                    {
                        case ConsoleKey.Enter: searchMode = false; break;
                        case ConsoleKey.Escape:
                            search = string.Empty; searchMode = false;
                            view = BuildView(filter, search); cursor = 0;
                            break;
                        case ConsoleKey.Backspace:
                            if (search.Length > 0) { search = search[..^1]; view = BuildView(filter, search); cursor = 0; }
                            break;
                        default:
                            if (!char.IsControl(key.KeyChar)) { search += key.KeyChar; view = BuildView(filter, search); cursor = 0; }
                            break;
                    }
                    continue;
                }

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow: case ConsoleKey.K:
                        if (view.Count > 0) cursor = (cursor - 1 + view.Count) % view.Count;
                        break;
                    case ConsoleKey.DownArrow: case ConsoleKey.J:
                        if (view.Count > 0) cursor = (cursor + 1) % view.Count;
                        break;
                    case ConsoleKey.Spacebar:
                        if (view.Count > 0)
                        {
                            var name = AllEntries[view[cursor]].Name;
                            if (!excluded.Remove(name)) excluded.Add(name);   // 切換：有就移除（改納入），無就排除
                        }
                        break;
                    case ConsoleKey.A:   // 全部納入（清空排除）— 只作用於目前可見項
                        foreach (var i in view) excluded.Remove(AllEntries[i].Name);
                        break;
                    case ConsoleKey.N:   // 全部排除 — 只作用於目前可見項
                        foreach (var i in view) excluded.Add(AllEntries[i].Name);
                        break;
                    case ConsoleKey.D0: case ConsoleKey.NumPad0:
                        filter = null; view = BuildView(filter, search); cursor = 0; break;
                    case ConsoleKey.D1: case ConsoleKey.NumPad1:
                        filter = Scope.App; view = BuildView(filter, search); cursor = 0; break;
                    case ConsoleKey.D2: case ConsoleKey.NumPad2:
                        filter = Scope.NonApp; view = BuildView(filter, search); cursor = 0; break;
                    case ConsoleKey.Oem2:   // 「/」搜尋
                        searchMode = true; break;
                    case ConsoleKey.R:      // 還原預設排除清單
                        excluded = new HashSet<string>(new CompareOptions().ExcludedObjectTypes, StringComparer.OrdinalIgnoreCase);
                        break;
                    case ConsoleKey.Enter:
                    case ConsoleKey.Escape:
                        profile.CompareOptions.ExcludedObjectTypes =
                            excluded.OrderBy(n => n, StringComparer.Ordinal).ToList();
                        AnsiConsole.Clear();
                        return;
                }
            }
        }
        finally { ConsoleUI.ExitRedrawMode(); }
    }

    private static List<int> BuildView(Scope? filter, string search)
    {
        var view = new List<int>();
        for (int i = 0; i < AllEntries.Length; i++)
        {
            var e = AllEntries[i];
            if (filter is not null && e.Scope != filter) continue;
            if (search.Length > 0
                && e.Zh.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0
                && e.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            view.Add(i);
        }
        return view;
    }

    private static void Render(
        HashSet<string> excluded, IReadOnlyList<int> view, int cursor, Scope? filter, string search, bool searchMode)
    {
        ConsoleUI.BeginFrame();
        int w = ConsoleUI.Width;
        int h = ConsoleUI.Height;
        int included = AllEntries.Count(e => !excluded.Contains(e.Name));

        ConsoleUI.Line(
            $"[{Theme.Accent}]▌[/] [bold {Theme.Accent}]物件型別[/] [{Theme.TextMuted}]（勾選＝納入比對）[/]" +
            $"   [{Theme.Hairline}]│[/]   " +
            $"[{Theme.Success}]◉[/] [{Theme.TextMuted}]納入[/] [bold {Theme.Success}]{included}[/] [{Theme.TextFaint}]/[/] [{Theme.TextSecondary}]{AllEntries.Length}[/]");

        // 範圍分段 ＋ 搜尋（同列）。
        int appN = AllEntries.Count(e => e.Scope == Scope.App);
        int nonN = AllEntries.Length - appN;
        var ac = Theme.Accent;
        double lum = (0.299 * ac.R + 0.587 * ac.G + 0.114 * ac.B) / 255.0;
        string onAccent = lum > 0.6 ? "#1C1C1E" : "#FFFFFF";
        string seg = Segments(filter, AllEntries.Length, appN, nonN, onAccent);

        string searchSlot;
        if (searchMode)
            searchSlot = $"[bold {Theme.Accent}]⌕[/] [{Theme.TextPrimary}]{ConsoleUI.Esc(search)}[/][blink {Theme.Accent}]▏[/]" +
                         $" [{Theme.TextFaint}]· {view.Count} 筆 · Enter 套用 · Esc 清除[/]";
        else if (search.Length > 0)
            searchSlot = $"[bold {Theme.Accent}]⌕[/] [{Theme.TextSecondary}]「{ConsoleUI.Esc(ConsoleUI.Truncate(search, 24))}」[/] [{Theme.TextFaint}]· {view.Count} 筆[/]";
        else
            searchSlot = $"[bold {Theme.Accent}]⌕[/] [{Theme.TextFaint}]按 / 搜尋[/]";
        ConsoleUI.Line($"{seg}   [{Theme.Hairline}]│[/]   {searchSlot}");

        int bodyRows = Math.Max(3, h - 3);   // L1(1)+L2(1)+底列(1)=3
        if (view.Count == 0)
        {
            ConsoleUI.Line($" [{Theme.TextMuted}]（無符合目前範圍或搜尋的物件類型）[/]");
            for (int r = 1; r < bodyRows; r++) ConsoleUI.Line();
        }
        else
        {
            int top = ConsoleUI.ScrollTop(cursor, view.Count, bodyRows);
            // 兩欄並排（清單較長時更省空間）；窄螢幕退單欄。
            int cols = w >= 90 ? 2 : 1;
            int colW = (w - (cols - 1) * 2) / cols;
            int rowsPerCol = (int)Math.Ceiling(view.Count / (double)cols);
            // 為簡化捲動，採單欄垂直捲動；雙欄僅在不需捲動時啟用。
            if (cols == 2 && view.Count <= bodyRows * 2)
            {
                for (int r = 0; r < bodyRows; r++)
                {
                    int li = r, ri = r + bodyRows;
                    string cell = li < view.Count ? Cell(excluded, view[li], li == cursor, colW) : new string(' ', colW);
                    string cell2 = ri < view.Count ? Cell(excluded, view[ri], ri == cursor, colW) : "";
                    ConsoleUI.Line($"{cell}  {cell2}");
                }
            }
            else
            {
                for (int r = 0; r < bodyRows; r++)
                {
                    int vi = top + r;
                    ConsoleUI.Line(vi < view.Count ? Cell(excluded, view[vi], vi == cursor, w - 1) : "");
                }
            }
        }

        ConsoleUI.LineLast(
            $"[{Theme.TextMuted}]↑↓[/] [{Theme.TextFaint}]移動[/]    " +
            $"[{Theme.TextMuted}]空白[/] [{Theme.TextFaint}]納入/排除[/]    " +
            $"[{Theme.TextMuted}]A[/] [{Theme.TextFaint}]全納入[/]    " +
            $"[{Theme.TextMuted}]N[/] [{Theme.TextFaint}]全排除[/]    " +
            $"[{Theme.TextMuted}]/[/] [{Theme.TextFaint}]搜尋[/]    " +
            $"[{Theme.TextMuted}]R[/] [{Theme.TextFaint}]還原預設[/]    " +
            $"[{Theme.TextMuted}]⏎[/] [{Theme.TextFaint}]套用返回[/]");

        ConsoleUI.EndFrame();
    }

    private static string Cell(HashSet<string> excluded, int entryIdx, bool here, int cellW)
    {
        var e = AllEntries[entryIdx];
        bool inc = !excluded.Contains(e.Name);
        var bar = here ? $"[{Theme.Accent}]▌[/]" : " ";
        var box = inc ? $"[{Theme.Success}]◉[/]" : $"[{Theme.TextFaint}]○[/]";

        // 前綴「▌ ◉ 」佔 4 欄；其餘給「中文標籤 · 英文鍵名」。英文鍵名即 JSON 寫入值，淡色並陳供對照。
        int avail = Math.Max(6, cellW - 4);
        string zh = ConsoleUI.Truncate(e.Zh, avail);
        int used = ConsoleUI.DisplayWidth(zh);

        // 中文標籤未對應（本身就是英文鍵名）時不重複加；空間不足以放 sep+英文時也略過。
        bool hasEng = !string.Equals(e.Zh, e.Name, StringComparison.Ordinal);
        string eng = "";
        if (hasEng && avail - used - 3 >= 4)
            eng = ConsoleUI.Truncate(e.Name, avail - used - 3);

        var zhM = here ? $"[bold {Theme.TextPrimary}]{ConsoleUI.Esc(zh)}[/]"
                       : inc ? $"[{Theme.TextSecondary}]{ConsoleUI.Esc(zh)}[/]"
                             : $"[{Theme.TextMuted}]{ConsoleUI.Esc(zh)}[/]";
        string engM = eng.Length == 0 ? "" : $"[{Theme.TextFaint}] · {ConsoleUI.Esc(eng)}[/]";

        int contentW = used + (eng.Length == 0 ? 0 : 3 + ConsoleUI.DisplayWidth(eng));
        int pad = Math.Max(0, cellW - 4 - contentW);
        return $"{bar} {box} {zhM}{engM}" + new string(' ', pad);
    }

    private static string Segments(Scope? filter, int all, int app, int non, string onAccent)
    {
        var segs = new (string label, string key, int count, bool active)[]
        {
            ("全部", "0", all, filter is null),
            ("應用程式範圍", "1", app, filter == Scope.App),
            ("非應用程式範圍", "2", non, filter == Scope.NonApp),
        };
        var m = new System.Text.StringBuilder();
        for (int i = 0; i < segs.Length; i++)
        {
            var s = segs[i];
            if (i > 0) m.Append("   ");
            m.Append(s.active
                ? $"[{onAccent} on {Theme.Accent}] [[{s.key}]] {s.label} {s.count} [/]"
                : $"[{Theme.TextFaint}][[{s.key}]][/] [{Theme.TextMuted}]{s.label}[/] [{Theme.TextSecondary}]{s.count}[/]");
        }
        return m.ToString();
    }
}
