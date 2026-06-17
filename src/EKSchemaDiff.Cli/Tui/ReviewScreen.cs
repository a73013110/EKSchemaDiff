using ConsoleKit.Text;
using EKSchemaDiff.Core.Compare;
using Spectre.Console;

namespace EKSchemaDiff.Cli.Tui;

/// <summary>
/// 合併的「勾選 + 即時預覽」畫面：上方可勾選的物件清單，下方即時顯示游標所在物件的差異預覽。
/// 自訂鍵盤迴圈，游標位置保留。回傳要納入的物件集合；Esc 取消回傳 null。
/// </summary>
public static class ReviewScreen
{
    public static HashSet<ObjectDifference>? Run(IReadOnlyList<ObjectDifference> diffs, bool ignoreWhitespace, IAppLog log, Banner banner,
        ISet<ObjectDifference>? preselected = null)
    {
        var ordered = diffs
            .OrderBy(d => d.Kind switch
            {
                ChangeKind.Add => 0, ChangeKind.Change => 1, ChangeKind.Delete => 2, _ => 3,
            })
            .ThenBy(d => d.ObjectTypeName)
            .ThenBy(d => d.Name)
            .ToList();

        // 初始勾選：自「確認頁」返回時沿用上次勾選，否則用差異本身的納入狀態。
        var included = new bool[ordered.Count];
        for (int i = 0; i < ordered.Count; i++)
            included[i] = preselected is not null ? preselected.Contains(ordered[i]) : ordered[i].Included;

        // 後援：非互動環境改用 Spectre 標準多選（無即時預覽）。
        if (!ConsoleUI.Interactive)
            return FallbackMultiSelect(ordered);

        // 篩選 / 搜尋狀態。view = 目前可見的 ordered 索引清單；cursor 指向 view 內位置。
        // included[] 與 ordered 對齊、為勾選的單一真實來源，篩選與搜尋僅重算 view、不動勾選。
        ChangeKind? filter = null;          // null=全部，否則僅顯示該類別
        var search = string.Empty;          // 比對 Name / ObjectTypeName（不分大小寫）
        bool searchMode = false;            // true 時按鍵流導向搜尋輸入緩衝區
        var view = BuildView(ordered, filter, search);
        int cursor = 0;

        // 進入時清一次，抹掉前一畫面（比對情境/總覽）的殘留；之後逐格重繪維持無閃爍。
        AnsiConsole.Clear();
        try
        {
            ConsoleUI.EnterRedrawMode();
            while (true)
            {
                try
                {
                    Render(ordered, included, view, cursor, filter, search, searchMode, ignoreWhitespace);
                }
                catch (Exception ex)
                {
                    // 預覽/差異繪製若失敗，記錄並跳過該格重繪，避免整個畫面崩潰把程式帶掉。
                    log.Error($"ReviewScreen 重繪失敗（cursor={cursor}）", ex);
                    ConsoleUI.BeginFrame();
                    ConsoleUI.Line($"[{Theme.Danger}]預覽繪製發生錯誤，已略過此格。詳見記錄檔。[/]");
                    ConsoleUI.EndFrame();
                }
                var key = Console.ReadKey(intercept: true);

                // ── 搜尋模式：按鍵即時編輯查詢字串並重算 view；Enter 套用、Esc 清除並離開。
                if (searchMode)
                {
                    switch (key.Key)
                    {
                        case ConsoleKey.Enter:
                            searchMode = false;
                            break;
                        case ConsoleKey.Escape:
                            search = string.Empty;
                            searchMode = false;
                            view = BuildView(ordered, filter, search);
                            cursor = 0;
                            break;
                        case ConsoleKey.Backspace:
                            if (search.Length > 0)
                            {
                                search = search[..^1];
                                view = BuildView(ordered, filter, search);
                                cursor = 0;
                            }
                            break;
                        default:
                            if (!char.IsControl(key.KeyChar))
                            {
                                search += key.KeyChar;
                                view = BuildView(ordered, filter, search);
                                cursor = 0;
                            }
                            break;
                    }
                    continue;
                }

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                    case ConsoleKey.K:
                        if (view.Count > 0) cursor = (cursor - 1 + view.Count) % view.Count;
                        break;
                    case ConsoleKey.DownArrow:
                    case ConsoleKey.J:
                        if (view.Count > 0) cursor = (cursor + 1) % view.Count;
                        break;
                    case ConsoleKey.Spacebar:
                        if (view.Count > 0) included[view[cursor]] = !included[view[cursor]];
                        break;
                    case ConsoleKey.A:
                        // 全選只作用於目前可見（篩選/搜尋後）的項目。
                        foreach (var idx in view) included[idx] = true;
                        break;
                    case ConsoleKey.N:
                        foreach (var idx in view) included[idx] = false;
                        break;
                    // ── 數字鍵直接切換篩選：0=全部 1=新增 2=變更 3=刪除（含數字鍵盤）。
                    case ConsoleKey.D0:
                    case ConsoleKey.NumPad0:
                        filter = null; view = BuildView(ordered, filter, search); cursor = 0;
                        break;
                    case ConsoleKey.D1:
                    case ConsoleKey.NumPad1:
                        filter = ChangeKind.Add; view = BuildView(ordered, filter, search); cursor = 0;
                        break;
                    case ConsoleKey.D2:
                    case ConsoleKey.NumPad2:
                        filter = ChangeKind.Change; view = BuildView(ordered, filter, search); cursor = 0;
                        break;
                    case ConsoleKey.D3:
                    case ConsoleKey.NumPad3:
                        filter = ChangeKind.Delete; view = BuildView(ordered, filter, search); cursor = 0;
                        break;
                    case ConsoleKey.Oem2:   // 「/」：進入搜尋模式
                        searchMode = true;
                        break;
                    case ConsoleKey.RightArrow:
                        // 展開游標物件的全螢幕差異詳檢（可捲動、可切換完整檔案）；
                        // 子畫面離開時會還原游標與自動換行，返回後需重新確立逐格重繪模式。
                        if (view.Count > 0)
                        {
                            DiffScreen.Show(ordered[view[cursor]], ignoreWhitespace, log);
                            ConsoleUI.EnterRedrawMode();
                        }
                        break;
                    case ConsoleKey.Enter:
                        var picked = Collect(ordered, included);
                        log.Step($"ReviewScreen 確認勾選 {picked.Count} 項");
                        AnsiConsole.Clear();
                        return picked;
                    case ConsoleKey.Escape:
                        AnsiConsole.Clear();
                        return null;
                }
            }
        }
        finally
        {
            ConsoleUI.ExitRedrawMode();
        }
    }

    /// <summary>依目前篩選類別與搜尋字串，重算可見的 ordered 索引清單。</summary>
    private static List<int> BuildView(IReadOnlyList<ObjectDifference> ordered, ChangeKind? filter, string search)
    {
        var view = new List<int>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var d = ordered[i];
            if (filter is not null && d.Kind != filter) continue;
            if (search.Length > 0
                && d.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0
                && d.ObjectTypeName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            view.Add(i);
        }
        return view;
    }

    private static HashSet<ObjectDifference> Collect(IReadOnlyList<ObjectDifference> ordered, bool[] included)
    {
        var set = new HashSet<ObjectDifference>();
        for (int i = 0; i < ordered.Count; i++) if (included[i]) set.Add(ordered[i]);
        return set;
    }

    /// <summary>低於此寬度（欄）改用上下堆疊版面，避免左右並排把兩邊都擠爛。</summary>
    private const int SideBySideMinWidth = 110;

    private static void Render(
        IReadOnlyList<ObjectDifference> ordered, bool[] included, IReadOnlyList<int> view, int cursor,
        ChangeKind? filter, string search, bool searchMode, bool ignoreWhitespace)
    {
        ConsoleUI.BeginFrame();
        // 不顯示上方 Banner：L1「勾選要納入此次部署的物件」即為本畫面標頭，空出的列數還給內容區。
        int w = ConsoleUI.Width;
        int h = ConsoleUI.Height;
        int chosen = included.Count(x => x);
        int add = ordered.Count(d => d.Kind == ChangeKind.Add);
        int chg = ordered.Count(d => d.Kind == ChangeKind.Change);
        int del = ordered.Count(d => d.Kind == ChangeKind.Delete);

        // ── 共用表頭（兩種版面都用）：L1 標題＋計量、L2 篩選控制＋搜尋。
        // 設計取向：克制的層級、充足留白，狀態與控制項一律「由左到右沿閱讀動線排」。

        // L1：標題 ＋ 已勾選計量（緊接標題、左側可見處；以 ◉ 圖示呼應清單的勾選符號、數字加亮）。
        ConsoleUI.Line(
            $"[{Theme.Accent}]▌[/] [bold {Theme.Accent}]勾選要納入此次部署的物件[/]" +
            $"   [{Theme.Hairline}]│[/]   " +
            $"[{Theme.Success}]◉[/] [{Theme.TextMuted}]已勾選[/] [bold {Theme.Success}]{chosen}[/] [{Theme.TextFaint}]/[/] [{Theme.TextSecondary}]{ordered.Count}[/]");

        // L2：分段篩選控制 ＋ 搜尋狀態（兩者同列、由左到右）。作用中段以填色膠囊呈現；
        //   膠囊前景依強調色亮度決定（亮底→深字／暗底→亮字），跨主題都保有足夠對比——取自設計系統的對比準則。
        var ac = Theme.Accent;
        double lum = (0.299 * ac.R + 0.587 * ac.G + 0.114 * ac.B) / 255.0;
        string onAccent = lum > 0.6 ? "#1C1C1E" : "#FFFFFF";   // Apple 近黑 / 純白
        string seg = BuildSegments(filter, ordered.Count, add, chg, del, onAccent);

        string searchSlot;
        if (searchMode)
        {
            // 搜尋中：即時查詢字串 + 閃爍游標 + 命中數，操作提示就近附在後面（打字時一眼可見）。
            var q = ConsoleUI.Esc(search);
            searchSlot = $"[bold {Theme.Accent}]⌕[/] [{Theme.TextPrimary}]{q}[/][blink {Theme.Accent}]▏[/]" +
                         $" [{Theme.TextFaint}]· {view.Count} 筆 · Enter 套用 · Esc 清除[/]";
        }
        else if (search.Length > 0)
        {
            var q = ConsoleUI.Esc(ConsoleUI.Truncate(search, 24));
            searchSlot = $"[bold {Theme.Accent}]⌕[/] [{Theme.TextSecondary}]「{q}」[/] [{Theme.TextFaint}]· {view.Count} 筆[/]";
        }
        else
        {
            searchSlot = $"[bold {Theme.Accent}]⌕[/] [{Theme.TextFaint}]按 / 搜尋[/]";
        }
        ConsoleUI.Line($"{seg}   [{Theme.Hairline}]│[/]   {searchSlot}");

        // ── 內容區：寬螢幕用左右並排（master–detail，清單與預覽同列）；窄螢幕退回上下堆疊。
        if (w >= SideBySideMinWidth && view.Count > 0)
            RenderSideBySide(ordered, included, view, cursor, ignoreWhitespace, w, h);
        else
            RenderStacked(ordered, included, view, cursor, ignoreWhitespace, w, h);

        ConsoleUI.EndFrame();
    }

    // ── 上下堆疊版面（窄螢幕後援）：清單在上、差異預覽在下，中間以標籤線分隔。 ──────────────
    private static void RenderStacked(
        IReadOnlyList<ObjectDifference> ordered, bool[] included, IReadOnlyList<int> view, int cursor,
        bool ignoreWhitespace, int w, int h)
    {
        // L3：克制的快捷鍵圖例（堆疊版才需要；並排版的指令列在最底部）。
        ConsoleUI.Line(
            $"[{Theme.TextMuted}]↑↓[/] [{Theme.TextFaint}]移動[/]    " +
            $"[{Theme.TextMuted}]空白[/] [{Theme.TextFaint}]勾選[/]    " +
            $"[{Theme.TextMuted}]→[/] [{Theme.TextFaint}]展開[/]    " +
            $"[{Theme.TextMuted}]A[/] [{Theme.TextFaint}]全選[/]    " +
            $"[{Theme.TextMuted}]N[/] [{Theme.TextFaint}]清除[/]    " +
            $"[{Theme.TextMuted}]⏎[/] [{Theme.TextFaint}]確認[/]    " +
            $"[{Theme.TextMuted}]esc[/] [{Theme.TextFaint}]返回[/]");
        ConsoleUI.Line();

        int listMax = Math.Clamp((h - 8) / 2, 4, 14);
        int listRows = Math.Min(Math.Max(view.Count, 1), listMax);

        if (view.Count == 0)
        {
            // 篩選/搜尋無符合項目：清單區與預覽區各給一行占位，維持版面不塌陷。
            ConsoleUI.Line($" [{Theme.TextMuted}]（無符合目前篩選或搜尋的物件）[/]");
            for (int r = 1; r < listRows; r++) ConsoleUI.Line();
            ConsoleUI.Line();
            ConsoleUI.Line($"[{Theme.TextMuted}]差異預覽[/] [{Theme.Hairline}]{new string('─', Math.Max(2, w - 12))}[/]");
            ConsoleUI.Line($"[{Theme.TextFaint}]調整篩選（0 回到全部）或修改搜尋字串以顯示物件。[/]");
            return;
        }

        int top = ConsoleUI.ScrollTop(cursor, view.Count, listRows);
        for (int vi = top; vi < Math.Min(view.Count, top + listRows); vi++)
        {
            var d = ordered[view[vi]];
            (string icon, ThemeColor color) = KindGlyph(d.Kind);
            bool here = vi == cursor;
            var box = included[view[vi]] ? $"[{Theme.Success}]◉[/]" : $"[{Theme.TextFaint}]○[/]";
            var bar = here ? $"[{Theme.Accent}]▌[/]" : " ";
            var type = ConsoleUI.Esc(PadType(d.ObjectTypeName));
            var nameMax = Math.Max(10, w - 18);
            var name = ConsoleUI.Esc(ConsoleUI.Truncate(d.Name, nameMax));
            var nameMarkup = here ? $"[bold {Theme.Accent}]{name}[/]" : $"[{Theme.TextSecondary}]{name}[/]";
            var typeMarkup = here ? $"[{Theme.TextSecondary}]{type}[/]" : $"[{Theme.TextMuted}]{type}[/]";
            ConsoleUI.Line($"{bar} {box} [{color}]{icon}[/] {typeMarkup} {nameMarkup}");
        }

        // ── 預覽區：先空一行，再以細規則線＋小標籤明確切出與清單的界線，營造留白與層次。
        var cur = ordered[view[cursor]];
        (ThemeColor kindColor, string action) = KindAction(cur.Kind);
        // 預覽刻意少留 1 列（整幀 h-2 而非 h-1）：給逐格重繪一格安全邊界，
        // 避免內容滿版時把頂端 banner／提示往上頂出（重複或消失）。
        int previewRows = Math.Max(3, h - 9 - listRows);
        var lines = BuildPreviewLines(cur.SourceScript, cur.TargetScript, ignoreWhitespace, w, previewRows, out int diffCount);

        ConsoleUI.Line();
        const string label = "差異預覽";
        const string hint = "→ 展開完整";
        int ruleLen = Math.Max(2, w - ConsoleUI.DisplayWidth(label) - ConsoleUI.DisplayWidth(hint) - 3);
        ConsoleUI.Line($"[{Theme.TextMuted}]{label}[/] [{Theme.Hairline}]{new string('─', ruleLen)}[/] [{Theme.TextFaint}]{hint}[/]");
        var nm = ConsoleUI.Esc(ConsoleUI.Truncate(cur.Name, Math.Max(10, w - 34)));
        ConsoleUI.Line($"[bold {Theme.TextPrimary}]{nm}[/]　[{kindColor}]●[/] [{Theme.TextMuted}]{action}[/] [{Theme.Hairline}]·[/] " +
                       $"[{Theme.TextMuted}]{diffCount} 處差異[/]　[{Theme.Hairline}]·[/] [{Theme.DiffAdd}]▏[/][{Theme.TextMuted}]新版[/] [{Theme.DiffDelete}]▏[/][{Theme.TextMuted}]原版[/]");
        foreach (var line in lines) ConsoleUI.Line(line);
    }

    // ── 左右並排版面（寬螢幕，master–detail）：左清單 / 右差異預覽，垂直線對齊；指令列收於底部。 ─────
    private static void RenderSideBySide(
        IReadOnlyList<ObjectDifference> ordered, bool[] included, IReadOnlyList<int> view, int cursor,
        bool ignoreWhitespace, int w, int h)
    {
        // 欄寬：左約 45%（夾在 30–70 欄），中間「 │ 」佔 3 欄，其餘給右側預覽。
        int leftW = Math.Clamp((w - 3) * 45 / 100, 30, 70);
        int rightW = Math.Max(20, w - 3 - leftW);
        // 高度：L1(1)+L2(1)=2、上線(1)、下線(1)、底部指令列(1) → 內容列 = h-5，
        // 讓整幀用滿 h 列、把指令列釘到畫面最底（與「展開完整」頁一致的鋪滿手法）。
        int bodyRows = Math.Max(4, h - 5);

        // 規則線：在分隔線（│ 落於第 leftW+1 欄）處放 ┬ / ┴，與內文垂直線對齊。
        int leftDashes = leftW + 1;
        int rightDashes = Math.Max(0, w - leftW - 3);
        string topRule = $"[{Theme.Hairline}]{new string('─', leftDashes)}┬{new string('─', rightDashes)}[/]";
        string botRule = $"[{Theme.Hairline}]{new string('─', leftDashes)}┴{new string('─', rightDashes)}[/]";

        // 右側：游標所在物件的預覽（resize 成右欄寬度），第一列為物件資訊、其後為差異列。
        var cur = ordered[view[cursor]];
        (ThemeColor kindColor, string action) = KindAction(cur.Kind);
        var preview = BuildPreviewLines(cur.SourceScript, cur.TargetScript, ignoreWhitespace, rightW, bodyRows - 1, out int diffCount);
        var nm = ConsoleUI.Esc(ConsoleUI.Truncate(cur.Name, Math.Max(10, rightW - 18)));
        string infoLine = $"[bold {Theme.TextPrimary}]{nm}[/] [{kindColor}]●[/] [{Theme.TextMuted}]{action} · {diffCount} 處差異[/]";

        int top = ConsoleUI.ScrollTop(cursor, view.Count, bodyRows);

        ConsoleUI.Line(topRule);
        for (int r = 0; r < bodyRows; r++)
        {
            int vi = top + r;
            string left = vi < view.Count
                ? BuildLeftCell(ordered[view[vi]], included[view[vi]], vi == cursor, leftW)
                : new string(' ', leftW);
            string right = r == 0 ? infoLine : (r - 1 < preview.Count ? preview[r - 1] : "");
            ConsoleUI.Line($"{left} [{Theme.Hairline}]│[/] {right}");
        }
        ConsoleUI.Line(botRule);

        // 底部指令列：整幀最後一列，以 LineLast 收尾（清行尾不換行），確保鋪滿到最底又不觸發底部捲動。
        ConsoleUI.LineLast(
            $"[{Theme.TextMuted}]↑↓[/] [{Theme.TextFaint}]移動[/]    " +
            $"[{Theme.TextMuted}]空白[/] [{Theme.TextFaint}]勾選[/]    " +
            $"[{Theme.TextMuted}]→[/] [{Theme.TextFaint}]展開完整[/]    " +
            $"[{Theme.TextMuted}]A[/] [{Theme.TextFaint}]全選[/]    " +
            $"[{Theme.TextMuted}]N[/] [{Theme.TextFaint}]清除[/]    " +
            $"[{Theme.TextMuted}]⏎[/] [{Theme.TextFaint}]確認[/]    " +
            $"[{Theme.TextMuted}]esc[/] [{Theme.TextFaint}]返回[/]    " +
            $"[{Theme.Hairline}]·[/]    [{Theme.DiffAdd}]▏[/][{Theme.TextMuted}]新版[/] [{Theme.DiffDelete}]▏[/][{Theme.TextMuted}]原版[/]");
    }

    /// <summary>建出左欄一列（勾選框＋種類符號＋型別＋名稱），補滿到 <paramref name="cellW"/> 顯示寬以對齊分隔線。</summary>
    private static string BuildLeftCell(ObjectDifference d, bool included, bool here, int cellW)
    {
        (string icon, ThemeColor color) = KindGlyph(d.Kind);
        const int typeCols = 10;                                   // 型別欄顯示寬（容得下「同義資料表」）
        const int prefixCols = 1 + 1 + 1 + 1 + 1 + 1 + typeCols + 1; // ▌ ◉ + {型別}  各段含一空格 = 17
        string type = FitDisplay(d.ObjectTypeName, typeCols);
        string name = ConsoleUI.Truncate(d.Name, Math.Max(6, cellW - prefixCols));

        // 直接以整列純文字量測補白（markup 為零寬不計入）；bar、box 兩個前導符各以 1 欄占位。
        int pad = Math.Max(0, cellW - ConsoleUI.DisplayWidth($"X X {icon} {type} {name}"));

        var box = included ? $"[{Theme.Success}]◉[/]" : $"[{Theme.TextFaint}]○[/]";
        var bar = here ? $"[{Theme.Accent}]▌[/]" : " ";
        var typeM = here ? $"[{Theme.TextSecondary}]{ConsoleUI.Esc(type)}[/]" : $"[{Theme.TextMuted}]{ConsoleUI.Esc(type)}[/]";
        var nameM = here ? $"[bold {Theme.Accent}]{ConsoleUI.Esc(name)}[/]" : $"[{Theme.TextSecondary}]{ConsoleUI.Esc(name)}[/]";
        return $"{bar} {box} [{color}]{icon}[/] {typeM} {nameM}" + new string(' ', pad);
    }

    /// <summary>截斷／補白到指定顯示寬（CJK 全形算 2 欄），過長以 … 結尾。</summary>
    private static string FitDisplay(string? text, int cols)
    {
        text ??= "";
        if (ConsoleUI.DisplayWidth(text) <= cols) return ConsoleUI.PadDisplay(text, cols);
        var sb = new System.Text.StringBuilder();
        int wsum = 0;
        foreach (var ch in text)
        {
            int cw = ConsoleUI.DisplayWidth(ch.ToString());
            if (wsum + cw > cols - 1) break;
            sb.Append(ch);
            wsum += cw;
        }
        sb.Append('…');
        return ConsoleUI.PadDisplay(sb.ToString(), cols);
    }

    private static (string icon, ThemeColor color) KindGlyph(ChangeKind kind) => kind switch
    {
        ChangeKind.Add => ("+", Theme.DiffAdd),
        ChangeKind.Change => ("~", Theme.Warning),
        ChangeKind.Delete => ("-", Theme.DiffDelete),
        _ => ("?", Theme.TextMuted),
    };

    private static (ThemeColor color, string action) KindAction(ChangeKind kind) => kind switch
    {
        ChangeKind.Add => (Theme.DiffAdd, "新增"),
        ChangeKind.Change => (Theme.Warning, "變更"),
        ChangeKind.Delete => (Theme.DiffDelete, "刪除"),
        _ => (Theme.TextMuted, "其他"),
    };

    /// <summary>
    /// 建出 Apple 風分段控制（segmented control）：全部／新增／變更／刪除四段，各帶數量；
    /// 標籤前以方括號鍵帽（如 <c>[1]</c>）標出對應快捷鍵，正常字級、清楚可讀又不與數量混淆。
    /// 作用中段以填色膠囊（前景 <paramref name="onAccent"/> on 強調色）凸顯，其餘以語意色低調呈現。
    /// </summary>
    private static string BuildSegments(
        ChangeKind? filter, int all, int add, int chg, int del, string onAccent)
    {
        var segs = new (string label, string key, int count, bool active, ThemeColor color)[]
        {
            ("全部", "0", all, filter is null,               Theme.TextSecondary),
            ("新增", "1", add, filter == ChangeKind.Add,     Theme.DiffAdd),
            ("變更", "2", chg, filter == ChangeKind.Change,  Theme.Warning),
            ("刪除", "3", del, filter == ChangeKind.Delete,  Theme.DiffDelete),
        };
        var m = new System.Text.StringBuilder();
        for (int i = 0; i < segs.Length; i++)
        {
            var s = segs[i];
            if (i > 0) m.Append("   ");     // 段間留白，營造分段控制的呼吸感
            // [[ ]] 在 markup 中會被轉義成字面的方括號，呈現如「[1]」的鍵帽。
            m.Append(s.active
                // 作用中：填色膠囊，鍵帽與數量同在膠囊內，保持整體感。
                ? $"[{onAccent} on {Theme.Accent}] [[{s.key}]] {s.label} {s.count} [/]"
                // 未作用：鍵帽（弱）＋標籤（中性）＋數量（語意色）。
                : $"[{Theme.TextFaint}][[{s.key}]][/] [{Theme.TextMuted}]{s.label}[/] [{s.color}]{s.count}[/]");
        }
        return m.ToString();
    }

    private static string PadType(string type)
    {
        type ??= "";
        return type.Length >= 8 ? type[..8] : type.PadRight(8);
    }

    // 預覽色盤取自佈景主題的 diff token（語意化，隨主題切換）。
    private static string AddColor => Theme.DiffAdd.ToString();       // 新版（更版）文字
    private static string DelColor => Theme.DiffDelete.ToString();    // 原版（被取代）文字
    private static string CtxColor => Theme.DiffContext.ToString();   // 未變更（context）文字
    private static string NumColor => Theme.DiffGutter.ToString();    // 行號
    private static string BarCtx => Theme.DiffBar.ToString();         // context 左側細邊

    /// <summary>
    /// 產生 unified 風格的預覽 markup 列，折疊相同段落，限制寬高。
    /// 版面：每行最左一道細色邊（▏新版綠／原版紅／context 灰）→ 雙行號欄（新版號｜原版號）→ 程式碼，
    /// 以色邊取代 +/- 符號；雙欄行號讓新／原各自獨立遞增，避免單欄交錯造成行號跳動的錯覺。
    /// </summary>
    private static List<string> BuildPreviewLines(
        string leftText, string rightText, bool ignoreWhitespace, int totalWidth, int maxRows, out int diffCount)
    {
        var rows = DiffEngine.Compare(leftText, rightText, ignoreWhitespace);
        diffCount = rows.Count(r => r.Kind != DiffKind.Same);

        int maxNum = 0;
        foreach (var r in rows) maxNum = Math.Max(maxNum, Math.Max(r.LeftNumber, r.RightNumber));
        int gw = Math.Max(2, maxNum.ToString().Length);
        string Gutter(int n) => n > 0 ? n.ToString().PadLeft(gw) : new string(' ', gw);
        string Blanks() => new string(' ', gw);
        // 雙行號欄：新版號｜原版號，各欄獨立遞增，避免單欄交錯造成行號跳動的錯覺。
        // 文字可用寬度 = 總寬 - 色邊(1) - 空格 - 新版欄(gw) - 空格 - 原版欄(gw) - 空格
        int textWidth = Math.Max(10, totalWidth - 2 * gw - 4);

        // 一列 = 「{色邊} {新版號} {原版號} {程式碼}」；該側不存在時行號留空。新版號較亮、原版號較暗。
        string Row(string bar, int newNum, int oldNum, IReadOnlyList<DiffSegment> segs, string baseColor, string changedColor) =>
            $"[{bar}]▏[/] [{NumColor}]{Gutter(newNum)}[/] [{Theme.Hairline}]{Gutter(oldNum)}[/] {DiffMarkup.Segments(segs, baseColor, changedColor, textWidth)}";

        var outLines = new List<string>();
        int contextRun = 0;
        const int maxContext = 2;

        foreach (var r in rows)
        {
            if (outLines.Count >= maxRows)
            {
                outLines.Add($"[{BarCtx}]▏[/] [{Theme.DiffGutter}]{Blanks()}[/] [{Theme.Hairline}]{Blanks()}[/] [{Theme.TextMuted}]… 內容過長，完整差異請見 HTML 報告[/]");
                break;
            }
            switch (r.Kind)
            {
                case DiffKind.Same:
                    if (contextRun < maxContext)
                        outLines.Add(Row(BarCtx, r.LeftNumber, r.RightNumber, r.RightSegments, CtxColor, CtxColor));
                    else if (contextRun == maxContext)
                        outLines.Add($"[{BarCtx}]▏[/] [{Theme.Hairline}]{Blanks()} {Blanks()} ⋯[/]");
                    contextRun++;
                    break;
                case DiffKind.Removed:
                    outLines.Add(Row(DelColor, 0, r.RightNumber, r.RightSegments, DelColor, DelColor));
                    contextRun = 0;
                    break;
                case DiffKind.Added:
                    outLines.Add(Row(AddColor, r.LeftNumber, 0, r.LeftSegments, AddColor, AddColor));
                    contextRun = 0;
                    break;
                case DiffKind.Modified:
                    // 變更行：未變更字詞用 context 色，只把改掉的字詞以紅／綠粗體高亮，凸顯真正差異處。
                    if (outLines.Count < maxRows)
                        outLines.Add(Row(DelColor, 0, r.RightNumber, r.RightSegments, CtxColor, DelColor));
                    if (outLines.Count < maxRows)
                        outLines.Add(Row(AddColor, r.LeftNumber, 0, r.LeftSegments, CtxColor, AddColor));
                    contextRun = 0;
                    break;
            }
        }
        if (outLines.Count == 0) outLines.Add($"[{BarCtx}]▏[/] [{Theme.TextMuted}](無內容差異)[/]");
        return outLines;
    }

    private static HashSet<ObjectDifference> FallbackMultiSelect(IReadOnlyList<ObjectDifference> ordered)
    {
        var prompt = new MultiSelectionPrompt<ObjectDifference>()
            .Title("勾選要納入此次部署的物件")
            .NotRequired()
            .PageSize(20)
            .UseConverter(d => $"{d.ObjectTypeName} {d.Name}");
        foreach (var d in ordered)
        {
            var item = prompt.AddChoice(d);
            if (d.Included) item.Select();
        }
        return new HashSet<ObjectDifference>(AnsiConsole.Prompt(prompt));
    }
}
