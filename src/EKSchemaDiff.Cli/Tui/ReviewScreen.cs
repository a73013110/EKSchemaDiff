using EKSchemaDiff.Core.Compare;
using EKSchemaDiff.Report;
using Spectre.Console;

namespace EKSchemaDiff.Cli.Tui;

/// <summary>
/// 合併的「勾選 + 即時預覽」畫面：上方可勾選的物件清單，下方即時顯示游標所在物件的差異預覽。
/// 自訂鍵盤迴圈，游標位置保留。回傳要納入的物件集合；Esc 取消回傳 null。
/// </summary>
public static class ReviewScreen
{
    public static HashSet<ObjectDifference>? Run(IReadOnlyList<ObjectDifference> diffs, bool ignoreWhitespace, IAppLog log, Banner banner)
    {
        var ordered = diffs
            .OrderBy(d => d.Kind switch
            {
                ChangeKind.Add => 0, ChangeKind.Change => 1, ChangeKind.Delete => 2, _ => 3,
            })
            .ThenBy(d => d.ObjectTypeName)
            .ThenBy(d => d.Name)
            .ToList();

        var included = new bool[ordered.Count];
        for (int i = 0; i < ordered.Count; i++) included[i] = ordered[i].Included;

        // 後援：非互動環境改用 Spectre 標準多選（無即時預覽）。
        if (!ConsoleUI.Interactive)
            return FallbackMultiSelect(ordered);

        int cursor = 0;
        // 進入時清一次，抹掉前一畫面（比對情境/總覽）的殘留；之後逐格重繪維持無閃爍。
        AnsiConsole.Clear();
        try
        {
            Console.CursorVisible = false;
            while (true)
            {
                try
                {
                    Render(ordered, included, cursor, ignoreWhitespace, banner);
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
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                    case ConsoleKey.K:
                        cursor = (cursor - 1 + ordered.Count) % ordered.Count;
                        break;
                    case ConsoleKey.DownArrow:
                    case ConsoleKey.J:
                        cursor = (cursor + 1) % ordered.Count;
                        break;
                    case ConsoleKey.Spacebar:
                        included[cursor] = !included[cursor];
                        break;
                    case ConsoleKey.A:
                        for (int i = 0; i < included.Length; i++) included[i] = true;
                        break;
                    case ConsoleKey.N:
                        for (int i = 0; i < included.Length; i++) included[i] = false;
                        break;
                    case ConsoleKey.RightArrow:
                        // 展開游標物件的全螢幕差異詳檢（可捲動、可切換完整檔案）；返回後恢復隱藏游標。
                        DiffScreen.Show(ordered[cursor], ignoreWhitespace, log);
                        Console.CursorVisible = false;
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
            Console.CursorVisible = true;
        }
    }

    private static HashSet<ObjectDifference> Collect(IReadOnlyList<ObjectDifference> ordered, bool[] included)
    {
        var set = new HashSet<ObjectDifference>();
        for (int i = 0; i < ordered.Count; i++) if (included[i]) set.Add(ordered[i]);
        return set;
    }

    private static void Render(
        IReadOnlyList<ObjectDifference> items, bool[] included, int cursor, bool ignoreWhitespace, Banner banner)
    {
        ConsoleUI.BeginFrame();
        banner.Compact();
        int w = ConsoleUI.Width;
        int h = ConsoleUI.Height;
        int chosen = included.Count(x => x);
        int add = items.Count(d => d.Kind == ChangeKind.Add);
        int chg = items.Count(d => d.Kind == ChangeKind.Change);
        int del = items.Count(d => d.Kind == ChangeKind.Delete);

        // ── 清單區表頭：標題 + 操作提示 + 統計，最後留一空行與清單分開。
        ConsoleUI.Line($"[{Theme.Accent}]▌[/] [bold {Theme.Accent}]勾選要納入此次部署的物件[/]");
        ConsoleUI.Line($"[{Theme.TextFaint}]↑↓ 移動 · 空白 勾選 · [/][bold {Theme.Accent}]→[/] [{Theme.TextFaint}]展開差異 · A 全選 · N 全不選 · Enter 確認 · Esc 返回[/]");
        ConsoleUI.Line($"已勾選 [bold {Theme.Success}]{chosen}[/] [{Theme.TextMuted}]/ {items.Count}[/]　[{Theme.TextFaint}]·[/]　" +
                       $"[{Theme.DiffAdd}]+{add}[/] [{Theme.TextMuted}]新增[/]　[{Theme.Warning}]~{chg}[/] [{Theme.TextMuted}]變更[/]　[{Theme.DiffDelete}]-{del}[/] [{Theme.TextMuted}]刪除[/]");
        ConsoleUI.Line();

        // 版面：banner(2)+表頭(3)+空行(1)=6 列固定；清單與預覽之間 空行+標題+資訊=3 列；底部留 1 列安全邊界。
        int listMax = Math.Clamp((h - 10) / 2, 4, 14);
        int listRows = Math.Min(items.Count, listMax);
        int top = ConsoleUI.ScrollTop(cursor, items.Count, listRows);

        for (int i = top; i < Math.Min(items.Count, top + listRows); i++)
        {
            var d = items[i];
            (string icon, ThemeColor color) = d.Kind switch
            {
                ChangeKind.Add => ("+", Theme.DiffAdd),
                ChangeKind.Change => ("~", Theme.Warning),
                ChangeKind.Delete => ("-", Theme.DiffDelete),
                _ => ("?", Theme.TextMuted),
            };
            bool here = i == cursor;
            var box = included[i] ? $"[{Theme.Success}]◉[/]" : $"[{Theme.TextFaint}]○[/]";
            var bar = here ? $"[{Theme.Accent}]▌[/]" : " ";
            var type = ConsoleUI.Esc(PadType(d.ObjectTypeName));
            var nameMax = Math.Max(10, w - 18);
            var name = ConsoleUI.Esc(ConsoleUI.Truncate(d.Name, nameMax));
            var nameMarkup = here ? $"[bold {Theme.Accent}]{name}[/]" : $"[{Theme.TextSecondary}]{name}[/]";
            var typeMarkup = here ? $"[{Theme.TextSecondary}]{type}[/]" : $"[{Theme.TextMuted}]{type}[/]";
            ConsoleUI.Line($"{bar} {box} [{color}]{icon}[/] {typeMarkup} {nameMarkup}");
        }

        // ── 預覽區：先空一行，再以細規則線＋小標籤明確切出與清單的界線，營造留白與層次。
        var cur = items[cursor];
        (ThemeColor kindColor, string action) = cur.Kind switch
        {
            ChangeKind.Add => (Theme.DiffAdd, "新增"),
            ChangeKind.Change => (Theme.Warning, "變更"),
            ChangeKind.Delete => (Theme.DiffDelete, "刪除"),
            _ => (Theme.TextMuted, "其他"),
        };
        int previewRows = Math.Max(4, h - 10 - listRows);
        var lines = BuildPreviewLines(cur.SourceScript, cur.TargetScript, ignoreWhitespace, w, previewRows, out int diffCount);

        ConsoleUI.Line();
        var label = "差異預覽";
        var hint = "→ 展開完整";
        int ruleLen = Math.Max(2, w - ConsoleUI.DisplayWidth(label) - ConsoleUI.DisplayWidth(hint) - 3);
        ConsoleUI.Line($"[{Theme.TextMuted}]{label}[/] [{Theme.Hairline}]{new string('─', ruleLen)}[/] [{Theme.TextFaint}]{hint}[/]");
        var nm = ConsoleUI.Esc(ConsoleUI.Truncate(cur.Name, Math.Max(10, w - 34)));
        ConsoleUI.Line($"[bold {Theme.TextPrimary}]{nm}[/]　[{kindColor}]●[/] [{Theme.TextMuted}]{action}[/] [{Theme.Hairline}]·[/] " +
                       $"[{Theme.TextMuted}]{diffCount} 處差異[/]　[{Theme.Hairline}]·[/] [{Theme.DiffAdd}]▏[/][{Theme.TextMuted}]新版[/] [{Theme.DiffDelete}]▏[/][{Theme.TextMuted}]原版[/]");
        foreach (var line in lines) ConsoleUI.Line(line);
        ConsoleUI.EndFrame();
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
