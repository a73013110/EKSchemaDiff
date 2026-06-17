using ConsoleKit.Text;
using ConsoleKit.Tui;
using EKSchemaDiff.Core.Compare;
using Spectre.Console;

namespace EKSchemaDiff.Cli.Tui;

/// <summary>
/// 單一物件的全螢幕差異詳檢（pager）：完整 diff 可上下捲動，並可在「折疊（只看差異周邊）」與
/// 「完整檔案（顯示整個物件腳本含所有未變更行，對應 git expand entire file）」之間切換。
/// unified 風格：每行最左一道色邊（綠＝新版／紅＝原版／灰＝未變更）＋行號＋程式碼，變更字詞行內高亮。
/// </summary>
public static class DiffScreen
{
    /// <summary>折疊模式下，每個變更區塊上下各保留的未變更行數。</summary>
    private const int ContextLines = 3;

    public static void Show(ObjectDifference diff, bool ignoreWhitespace, IAppLog log)
    {
        if (!ConsoleUI.Interactive) return;

        var rows = DiffEngine.Compare(diff.SourceScript, diff.TargetScript, ignoreWhitespace);
        int diffCount = rows.Count(r => r.Kind != DiffKind.Same);

        (ThemeColor kindColor, string action) = diff.Kind switch
        {
            ChangeKind.Add => (Theme.DiffAdd, "新增"),
            ChangeKind.Change => (Theme.Warning, "變更"),
            ChangeKind.Delete => (Theme.DiffDelete, "刪除"),
            _ => (Theme.TextMuted, "其他"),
        };

        bool full = false;
        int top = 0;
        int lastWidth = ConsoleUI.Width;
        var lines = BuildLines(rows, full, lastWidth);

        AnsiConsole.Clear();
        try
        {
            ConsoleUI.EnterRedrawMode();
            while (true)
            {
                int w = ConsoleUI.Width;
                int h = ConsoleUI.Height;
                if (w != lastWidth)
                {
                    lines = BuildLines(rows, full, w);
                    lastWidth = w;
                }

                int viewH = Math.Max(3, h - 5);
                int maxTop = Math.Max(0, lines.Count - viewH);
                top = Math.Clamp(top, 0, maxTop);

                Render(diff.Name, kindColor, action, diffCount, full, lines, top, viewH, w);

                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                    case ConsoleKey.K:
                        top--;
                        break;
                    case ConsoleKey.DownArrow:
                    case ConsoleKey.J:
                        top++;
                        break;
                    case ConsoleKey.PageUp:
                        top -= viewH;
                        break;
                    case ConsoleKey.PageDown:
                    case ConsoleKey.Spacebar:
                        top += viewH;
                        break;
                    case ConsoleKey.Home:
                        top = 0;
                        break;
                    case ConsoleKey.End:
                        top = maxTop;
                        break;
                    case ConsoleKey.F:
                        full = !full;
                        lines = BuildLines(rows, full, w);
                        top = 0;
                        log.Step($"DiffScreen 切換為{(full ? "完整檔案" : "折疊")}模式：{diff.Name}");
                        break;
                    case ConsoleKey.Escape:
                    case ConsoleKey.LeftArrow:
                    case ConsoleKey.Q:
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

    // ── 渲染 ──────────────────────────────────────────────────────────────

    private static void Render(
        string name, ThemeColor kindColor, string action, int diffCount,
        bool full, IReadOnlyList<string> lines, int top, int viewH, int w)
    {
        ConsoleUI.BeginFrame();

        // 標題列：物件名 + 種類
        var nm = ConsoleUI.Esc(ConsoleUI.Truncate(name, Math.Max(10, w - 16)));
        ConsoleUI.Line($"[{Theme.Accent}]▌[/] [bold {Theme.TextPrimary}]{nm}[/]　[{kindColor}]●[/] [{Theme.TextMuted}]{action}[/]");

        // meta 列：差異數 · 模式 · 捲動位置 · 圖例
        int total = lines.Count;
        int from = total == 0 ? 0 : top + 1;
        int to = Math.Min(top + viewH, total);
        var modeMarkup = full
            ? $"[bold {Theme.Accent}]完整檔案[/]"
            : $"[{Theme.TextMuted}]折疊[/]";
        ConsoleUI.Line(
            $"[{Theme.TextMuted}]{diffCount} 處差異[/] [{Theme.Hairline}]·[/] {modeMarkup} [{Theme.Hairline}]·[/] " +
            $"[{Theme.TextFaint}]{from}–{to}/{total}[/] [{Theme.Hairline}]·[/] " +
            $"[{Theme.TextMuted}]行號 [/][{Theme.DiffGutter}]新[/][{Theme.Hairline}]｜[/][{Theme.Hairline}]原[/] [{Theme.Hairline}]·[/] " +
            $"[{Theme.DiffAdd}]▏[/][{Theme.TextMuted}]新版[/] [{Theme.DiffDelete}]▏[/][{Theme.TextMuted}]原版[/]");

        // 上規則線
        ConsoleUI.Line($"[{Theme.Hairline}]{new string('─', Math.Max(2, w - 1))}[/]");

        // 內容窗：固定輸出 viewH 列（不足補空行，維持狀態列位置穩定）
        for (int i = 0; i < viewH; i++)
        {
            int idx = top + i;
            ConsoleUI.Line(idx < lines.Count ? lines[idx] : "");
        }

        // 下規則線 + 操作提示
        ConsoleUI.Line($"[{Theme.Hairline}]{new string('─', Math.Max(2, w - 1))}[/]");
        ConsoleUI.Line(
            $"[{Theme.TextFaint}]↑↓ 捲動 · PgUp/PgDn 翻頁 · Home/End 首尾 · [/]" +
            $"[bold {Theme.Accent}]f[/] [{Theme.TextFaint}]{(full ? "折疊" : "完整檔案")} · Esc 返回[/]");

        ConsoleUI.EndFrame();
    }

    // ── 顯示列建構 ────────────────────────────────────────────────────────

    private static string AddColor => Theme.DiffAdd.ToString();
    private static string DelColor => Theme.DiffDelete.ToString();
    private static string CtxColor => Theme.DiffContext.ToString();
    private static string NumColor => Theme.DiffGutter.ToString();
    private static string BarCtx => Theme.DiffBar.ToString();

    /// <summary>
    /// 把差異列攤平（<see cref="DiffView.Flatten"/>）後上色成可捲動的 markup 顯示列。
    /// Modified 列展開成原版／更版兩行；折疊摘要列顯示「⋯ N 行未變更」。
    /// </summary>
    private static List<string> BuildLines(IReadOnlyList<DiffRow> rows, bool full, int totalWidth)
    {
        int maxNum = 0;
        foreach (var r in rows) maxNum = Math.Max(maxNum, Math.Max(r.LeftNumber, r.RightNumber));
        int gw = Math.Max(2, maxNum.ToString().Length);
        // 雙行號欄：新版號｜原版號，各欄獨立、自然遞增（避免單欄交錯造成「行號跳動」的錯覺）。
        int textWidth = Math.Max(10, totalWidth - 2 * gw - 5);

        string Gutter(int n) => n > 0 ? n.ToString().PadLeft(gw) : new string(' ', gw);
        // newNum＝新版（更版）行號、oldNum＝原版行號；該側不存在時留空。
        string Row(string bar, int newNum, int oldNum, IReadOnlyList<DiffSegment> segs, string baseColor, string changedColor) =>
            $"[{bar}]▏[/] [{NumColor}]{Gutter(newNum)}[/] [{Theme.Hairline}]{Gutter(oldNum)}[/] {DiffMarkup.Segments(segs, baseColor, changedColor, textWidth)}";

        var outLines = new List<string>(rows.Count);
        foreach (var d in DiffView.Flatten(rows, full, ContextLines))
        {
            if (d.IsFold)
            {
                outLines.Add(
                    $"[{Theme.DiffBar}]▏[/] [{NumColor}]{new string(' ', gw)}[/] [{Theme.Hairline}]{new string(' ', gw)}[/] " +
                    $"[{Theme.TextFaint}]⋯ {d.HiddenCount} 行未變更[/]");
                continue;
            }

            var r = d.Row!;
            switch (r.Kind)
            {
                case DiffKind.Same:
                    outLines.Add(Row(BarCtx, r.LeftNumber, r.RightNumber, r.RightSegments, CtxColor, CtxColor));
                    break;
                case DiffKind.Removed:
                    outLines.Add(Row(DelColor, 0, r.RightNumber, r.RightSegments, DelColor, DelColor));
                    break;
                case DiffKind.Added:
                    outLines.Add(Row(AddColor, r.LeftNumber, 0, r.LeftSegments, AddColor, AddColor));
                    break;
                case DiffKind.Modified:
                    outLines.Add(Row(DelColor, 0, r.RightNumber, r.RightSegments, CtxColor, DelColor));
                    outLines.Add(Row(AddColor, r.LeftNumber, 0, r.LeftSegments, CtxColor, AddColor));
                    break;
            }
        }

        if (outLines.Count == 0)
            outLines.Add($"[{BarCtx}]▏[/] [{Theme.TextMuted}](無內容差異)[/]");
        return outLines;
    }
}
