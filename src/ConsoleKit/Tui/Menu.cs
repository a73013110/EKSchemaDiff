using Spectre.Console;

namespace ConsoleKit.Tui;

/// <summary>
/// 游標保留的單選選單（自訂鍵盤迴圈）。每次重繪會重新取得標籤，可顯示即時值。
/// 解決 Spectre SelectionPrompt 每輪跳回第一項的問題。
/// </summary>
public static class Menu
{
    /// <summary>
    /// 顯示選單，回傳選取索引；Esc 回傳 -1。itemsProvider 每格回傳最新標籤（markup），可顯示即時值。
    /// 參數依畫面由上而下排列：header（最上）→ title → items → footer（最下），最後才是行為參數。
    /// header：每格頂端要印的內容（例如 Banner），會跟著畫面一起重繪而不被清掉。
    /// onActivate：Enter 時呼叫，回傳 true 表示已就地處理（停留並重繪，游標不動），false 表示以此索引離開。
    /// </summary>
    public static int Show(
        string title,
        Func<IReadOnlyList<MenuItem>> itemsProvider,
        Action? header = null,
        string? footer = null,
        int initial = 0,
        Func<int, bool>? onActivate = null)
    {
        var items = itemsProvider();
        if (items.Count == 0) return -1;

        if (!ConsoleUI.Interactive)
        {
            // 後援：用 Spectre 標準選單（無游標保留，但不會卡住）。
            var labels = items.Select((it, i) => $"{i} {it.Label}").ToList();
            var pick = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title(title).AddChoices(labels).UseConverter(s => s.Split(' ')[1]));
            return int.Parse(pick.Split(' ')[0]);
        }

        int cursor = SkipSeparators(items, Math.Clamp(initial, 0, items.Count - 1), +1);
        // 進入畫面時清一次（只此一次，非每格重繪），抹掉上一個全螢幕畫面（如選 profile）的殘留。
        // 之後的逐格重繪維持無閃爍策略（BeginFrame 歸位 + 逐行清行尾 + 清到底）。
        AnsiConsole.Clear();
        try
        {
            ConsoleUI.EnterRedrawMode();
            while (true)
            {
                items = itemsProvider();
                if (cursor >= items.Count) cursor = items.Count - 1;
                cursor = SkipSeparators(items, cursor, +1);
                Render(title, footer, items, cursor, header);

                var key = Console.ReadKey(intercept: true).Key;
                switch (key)
                {
                    case ConsoleKey.UpArrow:
                    case ConsoleKey.K:
                        cursor = Move(items, cursor, -1);
                        break;
                    case ConsoleKey.DownArrow:
                    case ConsoleKey.J:
                        cursor = Move(items, cursor, +1);
                        break;
                    case ConsoleKey.Enter:
                        if (onActivate is null || !onActivate(cursor))
                            return cursor;
                        // onActivate 可能跑過 Spectre 提示（如設定頁的文字輸入），會還原游標與自動換行；
                        // 返回後重新確立逐格重繪模式，避免後續重繪因 autowrap 而漂移。
                        ConsoleUI.EnterRedrawMode();
                        break;
                    case ConsoleKey.Escape:
                        return -1;
                }
            }
        }
        finally
        {
            ConsoleUI.ExitRedrawMode();
        }
    }

    /// <summary>往 dir 方向移動一格，並跳過分隔列（無可選項時原地不動）。</summary>
    private static int Move(IReadOnlyList<MenuItem> items, int cursor, int dir)
    {
        for (int step = 0; step < items.Count; step++)
        {
            cursor = (cursor + dir + items.Count) % items.Count;
            if (!items[cursor].IsSeparator) return cursor;
        }
        return cursor;
    }

    /// <summary>若目前落在分隔列，往 dir 方向找到第一個可選項。</summary>
    private static int SkipSeparators(IReadOnlyList<MenuItem> items, int cursor, int dir)
    {
        if (cursor >= 0 && cursor < items.Count && !items[cursor].IsSeparator) return cursor;
        return Move(items, cursor, dir);
    }

    private static void Render(string title, string? footer, IReadOnlyList<MenuItem> items, int cursor, Action? header)
    {
        ConsoleUI.BeginFrame();
        header?.Invoke();
        ConsoleUI.Line(title);
        ConsoleUI.Line();

        int w = ConsoleUI.Width;
        // 可見列數：上限為終端高度可容納列數（至少 4），但不超過實際項目數。
        // 扣除的固定開銷＝Banner（留白+Logo+副標 約 9 列）＋標題/空白/說明/頁尾。
        // 注意不可用 Math.Clamp(x, 4, items.Count)：當項目數 < 4 時 min>max 會丟 ArgumentException。
        int visible = Math.Min(items.Count, Math.Max(4, ConsoleUI.Height - 11));
        int top = ConsoleUI.ScrollTop(cursor, items.Count, visible);

        for (int i = top; i < Math.Min(items.Count, top + visible); i++)
        {
            var it = items[i];
            if (it.IsSeparator)
            {
                // 有標題的分隔列當「分類標題」呈現；沒有就畫一條淡線。
                ConsoleUI.Line(string.IsNullOrWhiteSpace(it.Label)
                    ? $"  [{Theme.Hairline}]──────────[/]"
                    : $"  {it.Label}");
                continue;
            }
            if (i == cursor)
                ConsoleUI.Line($"[{Theme.Accent}]›[/] {it.Label}");
            else
                ConsoleUI.Line($"  {it.Label}");
        }

        var desc = items[cursor].Description;
        ConsoleUI.Line();
        if (!string.IsNullOrWhiteSpace(desc))
            ConsoleUI.Line($"[{Theme.TextMuted}]{ConsoleUI.Esc(ConsoleUI.Truncate(desc!, w - 2))}[/]");
        ConsoleUI.Line(footer ?? $"[{Theme.TextFaint}]↑↓ 移動 · Enter 選擇 · Esc 返回[/]");
        ConsoleUI.EndFrame();
    }
}
