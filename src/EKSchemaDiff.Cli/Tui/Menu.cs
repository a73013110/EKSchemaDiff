using Spectre.Console;

namespace EKSchemaDiff.Cli.Tui;

public sealed class MenuItem
{
    public required string Label { get; init; }       // 可含 Spectre markup
    public string? Description { get; init; }          // 選中時顯示的中文說明
    public bool IsSeparator { get; init; }
}

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

        if (!ConsoleUi.Interactive)
        {
            // 後援：用 Spectre 標準選單（無游標保留，但不會卡住）。
            var labels = items.Select((it, i) => $"{i} {it.Label}").ToList();
            var pick = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title(title).AddChoices(labels).UseConverter(s => s.Split(' ')[1]));
            return int.Parse(pick.Split(' ')[0]);
        }

        int cursor = SkipSeparators(items, Math.Clamp(initial, 0, items.Count - 1), +1);
        try
        {
            Console.CursorVisible = false;
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
                        break;
                    case ConsoleKey.Escape:
                        return -1;
                }
            }
        }
        finally
        {
            Console.CursorVisible = true;
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
        ConsoleUi.BeginFrame();
        header?.Invoke();
        ConsoleUi.Line(title);
        ConsoleUi.Line();

        int w = ConsoleUi.Width;
        // 可見列數：上限為終端高度可容納列數（至少 4），但不超過實際項目數。
        // 注意不可用 Math.Clamp(x, 4, items.Count)：當項目數 < 4 時 min>max 會丟 ArgumentException。
        int visible = Math.Min(items.Count, Math.Max(4, ConsoleUi.Height - 9));
        int top = ConsoleUi.ScrollTop(cursor, items.Count, visible);

        for (int i = top; i < Math.Min(items.Count, top + visible); i++)
        {
            var it = items[i];
            if (it.IsSeparator)
            {
                // 有標題的分隔列當「分類標題」呈現；沒有就畫一條淡線。
                ConsoleUi.Line(string.IsNullOrWhiteSpace(it.Label)
                    ? "  [grey39]──────────[/]"
                    : $"  {it.Label}");
                continue;
            }
            if (i == cursor)
                ConsoleUi.Line($"[orange3]>[/] {it.Label}");
            else
                ConsoleUi.Line($"  {it.Label}");
        }

        var desc = items[cursor].Description;
        ConsoleUi.Line();
        if (!string.IsNullOrWhiteSpace(desc))
            ConsoleUi.Line($"[grey]{ConsoleUi.Esc(ConsoleUi.Truncate(desc!, w - 2))}[/]");
        ConsoleUi.Line(footer ?? "[grey39]↑↓ 移動 · Enter 選擇 · Esc 返回[/]");
        ConsoleUi.EndFrame();
    }
}
