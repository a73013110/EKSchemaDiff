using EKSchemaDiff.Core.Config;
using Spectre.Console;

namespace EKSchemaDiff.Cli.Tui;

/// <summary>
/// profile 的比對／輸出選項設定頁，採「勾選物件」頁同款 master–detail：
/// 左側分組選項清單（開關以顏色＋實心/空心圓雙重區隔），右側即時顯示游標所在選項的
/// 說明、範例與對應的 Visual Studio 結構描述比較選項名，以及預設值與「是否已改」。
/// 空白切換、Enter 編輯（清單/文字）、R 重設此項、⇧R 全部重設、Esc 儲存返回。
/// </summary>
public static class SettingsEditor
{
    /// <summary>一個可操作的設定項。Value 已含 markup；Default 為預設值的可讀字串。</summary>
    private sealed record Opt(
        string Title,
        Func<Profile, string> Value,
        Action<Profile> Activate,
        Action<Profile> Reset,
        Func<Profile, bool> Changed,
        string Desc,
        string Example,
        string VsName,
        string Default,
        bool NeedsRestore = false);

    private sealed record Section(string Title, Opt[] Opts);

    private enum SlotKind { Header, Item }
    private sealed record Slot(SlotKind Kind, string? Header = null, Opt? Opt = null);

    private static readonly HashSet<string> DefaultExcluded =
        new(new CompareOptions().ExcludedObjectTypes, StringComparer.OrdinalIgnoreCase);

    // ── 選項目錄（只放使用者實際會調整的「常用」集合；其餘 DacFx 選項維持預設＝VS 預設） ──────
    private static readonly Section[] Sections =
    {
        new("★ 常用", new[]
        {
            Bool("略過資料行順序",
                p => p.CompareOptions.Comparison.IgnoreColumnOrder, (p, v) => p.CompareOptions.Comparison.IgnoreColumnOrder = v, false,
                "比對時不計較資料表欄位的實體排列順序。",
                "在資料表「中間」插一欄時，預設會產出「建暫存表 → 搬資料 → DROP 原表 → 改名」的整表重建腳本；開啟後改為單純 ALTER TABLE ADD，新欄接在尾端、不重建大表。",
                "一般 ▸ 略過資料行順序"),
            Bool("比對描述 MS_Description",
                p => !p.CompareOptions.Comparison.IgnoreExtendedProperties, (p, v) => p.CompareOptions.Comparison.IgnoreExtendedProperties = !v, true,
                "是否比對資料表／欄位的描述（擴充屬性 MS_Description）。",
                "關閉＝忽略描述差異；你們需要在差異報告看到欄位說明異動，故預設開。",
                "一般 ▸ 忽略擴充屬性（此處為其反向）"),
            Bool("忽略空白差異",
                p => p.CompareOptions.Comparison.IgnoreWhitespace, (p, v) => p.CompareOptions.Comparison.IgnoreWhitespace = v, true,
                "比對時忽略排版空白（縮排、換行）造成的差異。",
                "只差在格式排版的物件不會被當成有變更，避免雜訊。",
                "一般 ▸ 忽略空白字元"),
            Bool("資料遺失時阻擋部署",
                p => p.CompareOptions.Safety.BlockOnPossibleDataLoss, (p, v) => p.CompareOptions.Safety.BlockOnPossibleDataLoss = v, true,
                "當變更可能造成資料遺失時，在腳本中加入阻擋。",
                "例如縮短欄位長度、刪除有資料的欄位時，腳本會擋下避免誤刪資料（建議開）。",
                "一般 ▸ 可能遺失資料時即封鎖"),
        }),
        new("安全防護（避免動到權限／誤刪）", new[]
        {
            Bool("忽略權限 GRANT/DENY/REVOKE",
                p => p.CompareOptions.Safety.IgnorePermissions, (p, v) => p.CompareOptions.Safety.IgnorePermissions = v, true,
                "完全不比對、也不產生任何權限語句。",
                "杜絕「用無權限的開發庫比正式庫、產出移除權限 SQL」的事故（建議開）。與下方排除『權限』物件類型為雙保險。",
                "一般 ▸ 忽略權限"),
            Bool("刪除目標多出的權限",
                p => p.CompareOptions.Safety.DropPermissionsNotInSource, (p, v) => p.CompareOptions.Safety.DropPermissionsNotInSource = v, false,
                "即使有比對權限，是否刪除目標比來源多出的權限。",
                "開啟有移除既有權限的風險（建議關）。",
                "一般 ▸ 卸除不在來源中的權限"),
            Bool("刪除目標多出的物件",
                p => p.CompareOptions.Safety.DropObjectsNotInSource, (p, v) => p.CompareOptions.Safety.DropObjectsNotInSource = v, false,
                "是否刪除目標中、來源沒有的物件。",
                "開啟＝來源沒有的表/檢視/程序會被 DROP，有誤刪風險（建議關，採增量更新）。",
                "一般 ▸ 卸除不在來源中的物件"),
            Bool("忽略角色成員",
                p => p.CompareOptions.Comparison.IgnoreRoleMembership, (p, v) => p.CompareOptions.Comparison.IgnoreRoleMembership = v, true,
                "不比對資料庫角色成員資格。",
                "避免因兩端角色成員不同而產出 sp_addrolemember/卸除語句（建議開）。",
                "一般 ▸（角色成員相關）"),
            Bool("忽略登入 SID",
                p => p.CompareOptions.Comparison.IgnoreLoginSids, (p, v) => p.CompareOptions.Comparison.IgnoreLoginSids = v, true,
                "不比對登入帳號的 SID。",
                "不同伺服器同名登入 SID 常不同，忽略可避免無謂差異（建議開）。",
                "一般 ▸ 忽略登入 SID"),
            Bool("忽略關鍵字大小寫",
                p => p.CompareOptions.Comparison.IgnoreKeywordCasing, (p, v) => p.CompareOptions.Comparison.IgnoreKeywordCasing = v, true,
                "忽略 T-SQL 關鍵字的大小寫差異。",
                "例如 CREATE 與 create、PROCEDURE 與 procedure 不算差異。",
                "一般 ▸ 忽略關鍵字大小寫"),
            new Opt("排除的物件類型",
                p => CountValue(ExcludedCount(p), "類已排除"),
                p => ObjectTypesScreen.Edit(p),
                p => p.CompareOptions.ExcludedObjectTypes = new CompareOptions().ExcludedObjectTypes,
                p => !DefaultExcluded.SetEquals(p.CompareOptions.ExcludedObjectTypes),
                "整類排除、完全不納入比對的物件類型（雙保險）。",
                "預設排除權限與帳號相關類（權限/使用者/登入/角色成員/資料庫角色/應用程式角色/認證），對齊 VS 物件型別頁的取消勾選。Enter 開啟勾選清單。",
                "物件型別 ▸ 取消勾選整類",
                "權限/帳號相關 7 類",
                NeedsRestore: true),
        }),
        new("部署 SQL 輸出", new[]
        {
            Bool("完整部署腳本",
                p => p.ExportOptions.DeploySql.FullScript, (p, v) => p.ExportOptions.DeploySql.FullScript = v, true,
                "輸出依相依順序排好的單一完整部署腳本。",
                "檔名：完整部署腳本.sql。由 DacFx 官方引擎排序，可直接整批執行。",
                ""),
            Bool("完整還原腳本（回版用）",
                p => p.ExportOptions.DeploySql.FullRollbackScript, (p, v) => p.ExportOptions.DeploySql.FullRollbackScript = v, false,
                "輸出完整部署腳本的反向腳本。",
                "部署異常或需回版時，執行它可把目標還原回部署前狀態（檔名：完整還原腳本.sql）。",
                ""),
            Bool("逐物件部署檔",
                p => p.ExportOptions.DeploySql.PerObjectScripts, (p, v) => p.ExportOptions.DeploySql.PerObjectScripts = v, false,
                "以單一物件為主、逐物件各自一檔的部署檔。",
                "供逐一檢視與選擇性套用。樞紐物件單件產生較慢，常態匯出可關。",
                ""),
            new Opt("部署資料庫名稱（USE 覆寫）",
                p => DeployDbValue(p),
                EditDeployDb,
                p => p.ExportOptions.DeploySql.DeployDatabaseName = null,
                p => !string.IsNullOrWhiteSpace(p.ExportOptions.DeploySql.DeployDatabaseName),
                "腳本頂端 USE [...] 要使用的資料庫名稱。",
                "留空＝沿用目標庫名。內部目標庫叫 A、但客戶端實際庫名是 B 時，填 B。Enter 編輯。",
                "",
                "(沿用目標庫名)",
                NeedsRestore: true),
        }),
        new("差異報告（HTML）", new[]
        {
            Bool("輸出差異 HTML",
                p => p.ExportOptions.HtmlReport.Enabled, (p, v) => p.ExportOptions.HtmlReport.Enabled = v, true,
                "是否產生暖色系的差異 HTML 報告。",
                "逐物件一份 + 一份比對總覽，方便非技術人員檢視。",
                ""),
            Bool("HTML 差異忽略空白",
                p => p.ExportOptions.HtmlReport.IgnoreWhitespace, (p, v) => p.ExportOptions.HtmlReport.IgnoreWhitespace = v, false,
                "HTML 逐行差異著色時是否忽略空白。",
                "與上方「忽略空白差異」（影響比對結果）獨立，這裡只影響報告呈現。",
                ""),
        }),
    };

    private static readonly Slot[] Layout = BuildLayout();
    private static readonly int[] ItemSlots =
        Enumerable.Range(0, Layout.Length).Where(i => Layout[i].Kind == SlotKind.Item).ToArray();
    private static readonly int TotalOpts = ItemSlots.Length;

    private static Slot[] BuildLayout()
    {
        var slots = new List<Slot>();
        foreach (var s in Sections)
        {
            slots.Add(new Slot(SlotKind.Header, Header: s.Title));
            foreach (var o in s.Opts) slots.Add(new Slot(SlotKind.Item, Opt: o));
        }
        return slots.ToArray();
    }

    /// <summary>編輯 profile 選項。就地切換，呼叫端負責存檔。</summary>
    public static void Edit(Profile profile, Banner banner)
    {
        if (!ConsoleUI.Interactive) return;   // 非互動環境不開設定頁

        int cursor = ItemSlots.Length > 0 ? ItemSlots[0] : 0;

        AnsiConsole.Clear();
        try
        {
            ConsoleUI.EnterRedrawMode();
            while (true)
            {
                Render(profile, cursor);
                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                    case ConsoleKey.K:
                        cursor = NextItem(cursor, -1);
                        break;
                    case ConsoleKey.DownArrow:
                    case ConsoleKey.J:
                        cursor = NextItem(cursor, +1);
                        break;
                    case ConsoleKey.Spacebar:
                    case ConsoleKey.Enter:
                    {
                        var opt = Layout[cursor].Opt!;
                        opt.Activate(profile);
                        if (opt.NeedsRestore) { AnsiConsole.Clear(); ConsoleUI.EnterRedrawMode(); }
                        break;
                    }
                    case ConsoleKey.R:
                        if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                            foreach (var i in ItemSlots) Layout[i].Opt!.Reset(profile);
                        else
                            Layout[cursor].Opt!.Reset(profile);
                        break;
                    case ConsoleKey.Escape:
                        AnsiConsole.Clear();
                        return;
                }
            }
        }
        finally
        {
            ConsoleUI.ExitRedrawMode();
        }
    }

    private static int NextItem(int from, int dir)
    {
        int i = from;
        for (int step = 0; step < Layout.Length; step++)
        {
            i = (i + dir + Layout.Length) % Layout.Length;
            if (Layout[i].Kind == SlotKind.Item) return i;
        }
        return from;
    }

    /// <summary>低於此寬度改用上下堆疊版面，避免左右並排把兩欄都擠爛。</summary>
    private const int SideBySideMinWidth = 100;

    private static void Render(Profile profile, int cursor)
    {
        ConsoleUI.BeginFrame();
        int w = ConsoleUI.Width;
        int h = ConsoleUI.Height;
        int changed = ItemSlots.Count(i => Layout[i].Opt!.Changed(profile));

        // L1：標題 ＋「已改 N / 共 M」計量（呼應勾選頁的已勾選計量）。
        ConsoleUI.Line(
            $"[{Theme.Accent}]▌[/] [bold {Theme.Accent}]比對與輸出設定[/]" +
            $"   [{Theme.Hairline}]│[/]   profile [bold]{ConsoleUI.Esc(profile.Name)}[/]" +
            $"   [{Theme.Hairline}]│[/]   " +
            $"[{Theme.Warning}]◉[/] [{Theme.TextMuted}]已改[/] [bold {Theme.Warning}]{changed}[/] [{Theme.TextFaint}]/[/] [{Theme.TextSecondary}]{TotalOpts}[/]");

        if (w >= SideBySideMinWidth)
            RenderSideBySide(profile, cursor, w, h);
        else
            RenderStacked(profile, cursor, w, h);

        ConsoleUI.EndFrame();
    }

    // ── 左右並排（寬螢幕）：左選項清單 / 右選項說明，垂直線對齊；指令列收於底部。 ───────────────
    private static void RenderSideBySide(Profile profile, int cursor, int w, int h)
    {
        int leftW = Math.Clamp((w - 3) * 50 / 100, 34, 64);
        int rightW = Math.Max(24, w - 3 - leftW);
        int bodyRows = Math.Max(4, h - 4);   // L1(1)+上線(1)+下線(1)+底列(1)=4

        int leftDashes = leftW + 1;
        int rightDashes = Math.Max(0, w - leftW - 3);
        string topRule = $"[{Theme.Hairline}]{new string('─', leftDashes)}┬{new string('─', rightDashes)}[/]";
        string botRule = $"[{Theme.Hairline}]{new string('─', leftDashes)}┴{new string('─', rightDashes)}[/]";

        var detail = BuildDetail(profile, Layout[cursor].Opt!, rightW);
        int top = ConsoleUI.ScrollTop(cursor, Layout.Length, bodyRows);

        ConsoleUI.Line(topRule);
        for (int r = 0; r < bodyRows; r++)
        {
            int si = top + r;
            string left = si < Layout.Length ? BuildLeftCell(profile, Layout[si], si == cursor, leftW)
                                              : new string(' ', leftW);
            string right = r < detail.Count ? detail[r] : "";
            ConsoleUI.Line($"{left} [{Theme.Hairline}]│[/] {right}");
        }
        ConsoleUI.Line(botRule);
        ConsoleUI.LineLast(CommandBar());
    }

    // ── 上下堆疊（窄螢幕後援）：清單在上、說明在下。 ───────────────────────────────────────
    private static void RenderStacked(Profile profile, int cursor, int w, int h)
    {
        int listRows = Math.Clamp((h - 8), 4, Layout.Length);
        int top = ConsoleUI.ScrollTop(cursor, Layout.Length, listRows);
        for (int r = 0; r < listRows; r++)
        {
            int si = top + r;
            if (si >= Layout.Length) { ConsoleUI.Line(); continue; }
            ConsoleUI.Line(BuildLeftCell(profile, Layout[si], si == cursor, w - 1));
        }

        ConsoleUI.Line();
        ConsoleUI.Line($"[{Theme.TextMuted}]說明[/] [{Theme.Hairline}]{new string('─', Math.Max(2, w - 6))}[/]");
        foreach (var line in BuildDetail(profile, Layout[cursor].Opt!, w - 1)) ConsoleUI.Line(line);
        ConsoleUI.LineLast(CommandBar());
    }

    private static string CommandBar() =>
        $"[{Theme.TextMuted}]↑↓[/] [{Theme.TextFaint}]移動[/]    " +
        $"[{Theme.TextMuted}]空白[/] [{Theme.TextFaint}]切換[/]    " +
        $"[{Theme.TextMuted}]⏎[/] [{Theme.TextFaint}]編輯[/]    " +
        $"[{Theme.TextMuted}]R[/] [{Theme.TextFaint}]重設此項[/]    " +
        $"[{Theme.TextMuted}]⇧R[/] [{Theme.TextFaint}]全部重設[/]    " +
        $"[{Theme.TextMuted}]esc[/] [{Theme.TextFaint}]儲存返回[/]";

    /// <summary>左欄一列：分類標題、或（游標條＋開關／值＋標題），補滿到 cellW 以對齊分隔線。</summary>
    private static string BuildLeftCell(Profile profile, Slot slot, bool here, int cellW)
    {
        if (slot.Kind == SlotKind.Header)
        {
            string ht = ConsoleUI.Truncate(slot.Header!, Math.Max(6, cellW - 2));
            int hpad = Math.Max(0, cellW - ConsoleUI.DisplayWidth($"▌ {ht}"));
            return $"[{Theme.Accent}]▌[/] [bold {Theme.Accent}]{ConsoleUI.Esc(ht)}[/]" + new string(' ', hpad);
        }

        var opt = slot.Opt!;
        var bar = here ? $"[{Theme.Accent}]▌[/]" : " ";
        bool ch = opt.Changed(profile);
        // 已改項以小圓點標示（呼應頂端「已改」計量）。
        var mark = ch ? $"[{Theme.Warning}]•[/]" : " ";
        var value = opt.Value(profile);
        string title = ConsoleUI.Truncate(opt.Title, Math.Max(8, cellW - 22));
        var titleM = here ? $"[bold {Theme.TextPrimary}]{ConsoleUI.Esc(title)}[/]" : $"[{Theme.TextSecondary}]{ConsoleUI.Esc(title)}[/]";

        // 量測用純文字（markup 零寬不計）：bar、mark 各 1 欄，value 取其顯示寬。
        string plain = $"X {ConsoleUI.Truncate(title, Math.Max(8, cellW - 22))}";
        int used = ConsoleUI.DisplayWidth(plain) + 1 /*mark*/ + 1 /*space*/;
        // 右側放 value：先算 value 顯示寬（去 markup 後）。
        return AlignValue($"{bar} {mark} {titleM}", value, used, cellW);
    }

    /// <summary>把 value 靠右對齊補到 cellW（value 為 markup，需用其純文字寬度量測）。</summary>
    private static string AlignValue(string left, string value, int leftUsed, int cellW)
    {
        // value 純文字寬度：剝除 markup 標籤後量測。
        string valuePlain = StripMarkup(value);
        int valW = ConsoleUI.DisplayWidth(valuePlain);
        int pad = Math.Max(1, cellW - leftUsed - valW);
        return $"{left}{new string(' ', pad)}{value}";
    }

    private static string StripMarkup(string s)
    {
        var sb = new System.Text.StringBuilder();
        bool inTag = false;
        foreach (var c in s)
        {
            if (c == '[') { inTag = true; continue; }
            if (c == ']') { inTag = false; continue; }
            if (!inTag) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>右側說明面板：標題、說明、範例、VS 對應名、預設值與是否已改。</summary>
    private static List<string> BuildDetail(Profile profile, Opt opt, int width)
    {
        var lines = new List<string>();
        foreach (var l in Wrap(opt.Title, width)) lines.Add($"[bold {Theme.TextPrimary}]{ConsoleUI.Esc(l)}[/]");
        lines.Add("");
        foreach (var l in Wrap(opt.Desc, width)) lines.Add($"[{Theme.TextSecondary}]{ConsoleUI.Esc(l)}[/]");

        if (!string.IsNullOrEmpty(opt.Example))
        {
            lines.Add("");
            var wrapped = Wrap("〈範例〉" + opt.Example, width);
            foreach (var l in wrapped) lines.Add($"[{Theme.TextMuted}]{ConsoleUI.Esc(l)}[/]");
        }
        if (!string.IsNullOrEmpty(opt.VsName))
        {
            lines.Add("");
            foreach (var l in Wrap("〈VS〉" + opt.VsName, width)) lines.Add($"[{Theme.TextFaint}]{ConsoleUI.Esc(l)}[/]");
        }

        lines.Add("");
        bool ch = opt.Changed(profile);
        string def = $"[{Theme.TextFaint}]〈預設〉{ConsoleUI.Esc(opt.Default)}[/]";
        string state = ch
            ? $"   [{Theme.Warning}]●[/] [{Theme.TextMuted}]已改（R 還原此項）[/]"
            : $"   [{Theme.Success}]●[/] [{Theme.TextMuted}]同預設[/]";
        lines.Add(def + state);
        return lines;
    }

    /// <summary>依顯示寬度（CJK 全形算 2 欄）折行。</summary>
    private static List<string> Wrap(string text, int width)
    {
        var outLines = new List<string>();
        if (string.IsNullOrEmpty(text)) { outLines.Add(""); return outLines; }
        int max = Math.Max(8, width);
        var sb = new System.Text.StringBuilder();
        int wsum = 0;
        foreach (var ch in text)
        {
            int cw = ConsoleUI.DisplayWidth(ch.ToString());
            if (wsum + cw > max)
            {
                outLines.Add(sb.ToString());
                sb.Clear();
                wsum = 0;
            }
            sb.Append(ch);
            wsum += cw;
        }
        if (sb.Length > 0) outLines.Add(sb.ToString());
        return outLines;
    }

    // ── 值呈現 ──────────────────────────────────────────────────────────────────────
    private static string Toggle(bool v) => v ? $"[{Theme.Success}]● 開[/]" : $"[{Theme.TextMuted}]○ 關[/]";
    private static string CountValue(int n, string suffix) => $"[{Theme.Warning}]{n} {suffix}[/]";
    private static int ExcludedCount(Profile p) => p.CompareOptions.ExcludedObjectTypes.Count;
    private static string DeployDbValue(Profile p) =>
        string.IsNullOrWhiteSpace(p.ExportOptions.DeploySql.DeployDatabaseName)
            ? $"[{Theme.TextMuted}](沿用目標庫名)[/]"
            : $"[{Theme.Warning}]{ConsoleUI.Esc(p.ExportOptions.DeploySql.DeployDatabaseName!)}[/]";

    /// <summary>建立一個布林開關選項。</summary>
    private static Opt Bool(
        string title, Func<Profile, bool> get, Action<Profile, bool> set, bool def,
        string desc, string example, string vsName) =>
        new(title,
            p => Toggle(get(p)),
            p => set(p, !get(p)),
            p => set(p, def),
            p => get(p) != def,
            desc, example, vsName,
            def ? "開" : "關");

    private static void EditDeployDb(Profile p)
    {
        Console.CursorVisible = true;
        var input = AnsiConsole.Prompt(
            new TextPrompt<string>("部署資料庫名稱（USE，留空＝沿用目標庫名）：")
                .AllowEmpty()
                .DefaultValue(p.ExportOptions.DeploySql.DeployDatabaseName ?? ""));
        p.ExportOptions.DeploySql.DeployDatabaseName = string.IsNullOrWhiteSpace(input) ? null : input.Trim();
        Console.CursorVisible = false;
    }
}
