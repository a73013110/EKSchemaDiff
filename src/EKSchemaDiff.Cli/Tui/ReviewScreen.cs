using EKSchemaDiff.Core.Compare;
using EKSchemaDiff.Core.Diagnostics;
using EKSchemaDiff.Report;
using Spectre.Console;

namespace EKSchemaDiff.Cli.Tui;

/// <summary>
/// 合併的「勾選 + 即時預覽」畫面：上方可勾選的物件清單，下方即時顯示游標所在物件的差異預覽。
/// 自訂鍵盤迴圈，游標位置保留。回傳要納入的物件集合；Esc 取消回傳 null。
/// </summary>
public static class ReviewScreen
{
    public static HashSet<ObjectDifference>? Run(IReadOnlyList<ObjectDifference> diffs, bool ignoreWhitespace)
    {
        var ordered = diffs
            .OrderBy(d => d.UpdateAction switch
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
        try
        {
            Console.CursorVisible = false;
            while (true)
            {
                try
                {
                    Render(ordered, included, cursor, ignoreWhitespace);
                }
                catch (Exception ex)
                {
                    // 預覽/差異繪製若失敗，記錄並跳過該格重繪，避免整個畫面崩潰把程式帶掉。
                    Log.Error($"ReviewScreen 重繪失敗（cursor={cursor}）", ex);
                    ConsoleUI.BeginFrame();
                    ConsoleUI.Line("[red]預覽繪製發生錯誤，已略過此格。詳見記錄檔。[/]");
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
                    case ConsoleKey.Enter:
                        var picked = Collect(ordered, included);
                        Log.Step($"ReviewScreen 確認勾選 {picked.Count} 項");
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
        IReadOnlyList<ObjectDifference> items, bool[] included, int cursor, bool ignoreWhitespace)
    {
        ConsoleUI.BeginFrame();
        int w = ConsoleUI.Width;
        int h = ConsoleUI.Height;
        int chosen = included.Count(x => x);

        ConsoleUI.Line("[orange3]勾選要納入此次部署的物件[/]　[grey39]↑↓ 移動 · 空白 勾選 · A 全選 · N 全不選 · Enter 確認 · Esc 返回主選單[/]");
        ConsoleUI.Line($"已勾選 [green]{chosen}[/] / 共 {items.Count}");
        ConsoleUI.Line();

        int previewHeaderRows = 2;
        int listMax = Math.Clamp((h - 5 - previewHeaderRows) / 2, 4, 14);
        int listRows = Math.Min(items.Count, listMax);
        int top = ConsoleUI.ScrollTop(cursor, items.Count, listRows);

        for (int i = top; i < Math.Min(items.Count, top + listRows); i++)
        {
            var d = items[i];
            var (icon, color) = d.UpdateAction switch
            {
                ChangeKind.Add => ("+", "green"),
                ChangeKind.Change => ("~", "yellow"),
                ChangeKind.Delete => ("-", "red"),
                _ => ("?", "grey"),
            };
            var box = included[i] ? "[green][[x]][/]" : "[grey][[ ]][/]";
            var arrow = i == cursor ? "[orange3]>[/]" : " ";
            var type = ConsoleUI.Esc(PadType(d.ObjectTypeName));
            var nameMax = Math.Max(10, w - 18);
            var name = ConsoleUI.Esc(ConsoleUI.Truncate(d.Name, nameMax));
            var nameMarkup = i == cursor ? $"[bold]{name}[/]" : name;
            ConsoleUI.Line($"{arrow} {box} [{color}]{icon}[/] [grey]{type}[/] {nameMarkup}");
        }

        // 分隔 + 預覽
        ConsoleUI.Line($"[grey39]{new string('-', Math.Max(10, w - 1))}[/]");
        var cur = items[cursor];
        var action = cur.UpdateAction switch
        {
            ChangeKind.Add => "新增", ChangeKind.Change => "變更",
            ChangeKind.Delete => "刪除", _ => "其他",
        };
        int previewRows = Math.Max(4, h - 5 - listRows - previewHeaderRows);
        var lines = BuildPreviewLines(cur.SourceScript, cur.TargetScript, ignoreWhitespace, w, previewRows, out int diffCount);

        ConsoleUI.Line($"[orange3]預覽[/] {ConsoleUI.Esc(cur.Name)}　[grey]{action} · {diffCount} 差異列 · 左為行號 · (-) 原版 / (+) 更版[/]");
        foreach (var line in lines) ConsoleUI.Line(line);
        ConsoleUI.EndFrame();
    }

    private static string PadType(string type)
    {
        type ??= "";
        return type.Length >= 8 ? type[..8] : type.PadRight(8);
    }

    /// <summary>
    /// 產生 unified 風格的預覽 markup 列，折疊相同段落，限制寬高。
    /// 每行最前面加行號欄：context/(+) 顯示更版行號，(-) 顯示原版行號。
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
        // 文字可用寬度 = 總寬 - 行號欄(gw) - 一空格 - 標記("+ "/"- "/"  " 共 2 格)
        int textWidth = Math.Max(10, totalWidth - gw - 3);

        var outLines = new List<string>();
        int contextRun = 0;
        const int maxContext = 2;

        foreach (var r in rows)
        {
            if (outLines.Count >= maxRows) { outLines.Add("[grey39]  … （內容過長，完整差異請見 HTML 報告）[/]"); break; }
            switch (r.Kind)
            {
                case DiffKind.Same:
                    if (contextRun < maxContext)
                        outLines.Add($"[grey39]{Gutter(r.LeftNumber)}[/] [grey]  {ConsoleUI.Esc(ConsoleUI.Truncate(r.Right, textWidth))}[/]");
                    else if (contextRun == maxContext)
                        outLines.Add($"[grey39]{new string(' ', gw)}   ···[/]");
                    contextRun++;
                    break;
                case DiffKind.Removed:
                    outLines.Add($"[grey39]{Gutter(r.RightNumber)}[/] [red]- {ConsoleUI.Esc(ConsoleUI.Truncate(r.Right, textWidth))}[/]");
                    contextRun = 0;
                    break;
                case DiffKind.Added:
                    outLines.Add($"[grey39]{Gutter(r.LeftNumber)}[/] [green]+ {ConsoleUI.Esc(ConsoleUI.Truncate(r.Left, textWidth))}[/]");
                    contextRun = 0;
                    break;
                case DiffKind.Modified:
                    if (outLines.Count < maxRows)
                        outLines.Add($"[grey39]{Gutter(r.RightNumber)}[/] [red]- {ConsoleUI.Esc(ConsoleUI.Truncate(r.Right, textWidth))}[/]");
                    if (outLines.Count < maxRows)
                        outLines.Add($"[grey39]{Gutter(r.LeftNumber)}[/] [green]+ {ConsoleUI.Esc(ConsoleUI.Truncate(r.Left, textWidth))}[/]");
                    contextRun = 0;
                    break;
            }
        }
        if (outLines.Count == 0) outLines.Add("[grey](無內容差異)[/]");
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
