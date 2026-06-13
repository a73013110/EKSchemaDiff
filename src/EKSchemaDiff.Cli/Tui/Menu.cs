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
    /// onActivate：Enter 時呼叫，回傳 true 表示已就地處理（停留並重繪，游標不動），false 表示以此索引離開。
    /// header：每格頂端要印的內容（例如 Banner），會跟著畫面一起重繪而不被清掉。
    /// </summary>
    public static int Show(
        string title,
        Func<IReadOnlyList<MenuItem>> itemsProvider,
        int initial = 0,
        string? footer = null,
        Func<int, bool>? onActivate = null,
        Action? header = null)
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

        int cursor = Math.Clamp(initial, 0, items.Count - 1);
        try
        {
            Console.CursorVisible = false;
            while (true)
            {
                items = itemsProvider();
                Render(title, footer, items, cursor, header);

                var key = Console.ReadKey(intercept: true).Key;
                switch (key)
                {
                    case ConsoleKey.UpArrow:
                    case ConsoleKey.K:
                        cursor = (cursor - 1 + items.Count) % items.Count;
                        break;
                    case ConsoleKey.DownArrow:
                    case ConsoleKey.J:
                        cursor = (cursor + 1) % items.Count;
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

    private static void Render(string title, string? footer, IReadOnlyList<MenuItem> items, int cursor, Action? header)
    {
        ConsoleUi.BeginFrame();
        header?.Invoke();
        ConsoleUi.Line(title);
        ConsoleUi.Line();

        int w = ConsoleUi.Width;
        int visible = Math.Clamp(ConsoleUi.Height - 9, 4, items.Count);
        int top = ConsoleUi.ScrollTop(cursor, items.Count, visible);

        for (int i = top; i < Math.Min(items.Count, top + visible); i++)
        {
            var it = items[i];
            if (it.IsSeparator)
            {
                ConsoleUi.Line("  [grey39]----------[/]");
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
