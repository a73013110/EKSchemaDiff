using EKSchemaDiff.Core.Compare;
using Spectre.Console;

namespace EKSchemaDiff.Cli.Tui;

/// <summary>使用者在相依確認頁的決定。</summary>
public enum InclusionDecision { Confirm, Back, Cancel }

/// <summary>
/// 套用勾選後的「相依確認頁」：列出實際要部署的全部物件，分「你勾選的」與「相依自動補入的」兩段，
/// 讓使用者在匯出前看清系統為部署安全補了哪些。Enter 確認匯出、B/← 返回重勾、Esc 取消。
/// 清單過長時可上下捲動。
/// </summary>
public static class InclusionConfirmScreen
{
    public static InclusionDecision Run(InclusionResult resolved, Banner banner)
    {
        // 非互動環境：無法確認，直接放行（維持既有行為）。
        if (!ConsoleUI.Interactive) return InclusionDecision.Confirm;

        var picked = resolved.Picked.OrderBy(d => d.ObjectTypeName).ThenBy(d => d.Name).ToList();
        var deps = resolved.Dependencies.OrderBy(d => d.ObjectTypeName).ThenBy(d => d.Name).ToList();
        var lines = BuildLines(picked, deps);

        int top = 0;
        AnsiConsole.Clear();
        try
        {
            ConsoleUI.EnterRedrawMode();
            while (true)
            {
                int maxRows = Render(banner, picked.Count, deps.Count, lines, top);
                int maxTop = Math.Max(0, lines.Count - maxRows);
                if (top > maxTop) { top = maxTop; continue; }   // 視窗縮放後修正捲動位置

                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.Enter: return InclusionDecision.Confirm;
                    case ConsoleKey.Escape: return InclusionDecision.Cancel;
                    case ConsoleKey.B:
                    case ConsoleKey.LeftArrow: return InclusionDecision.Back;
                    case ConsoleKey.UpArrow: case ConsoleKey.K: top = Math.Max(0, top - 1); break;
                    case ConsoleKey.DownArrow: case ConsoleKey.J: top = Math.Min(maxTop, top + 1); break;
                    case ConsoleKey.PageUp: top = Math.Max(0, top - maxRows); break;
                    case ConsoleKey.PageDown: top = Math.Min(maxTop, top + maxRows); break;
                    case ConsoleKey.Home: top = 0; break;
                    case ConsoleKey.End: top = maxTop; break;
                }
            }
        }
        finally { ConsoleUI.ExitRedrawMode(); }
    }

    private static List<string> BuildLines(IReadOnlyList<ObjectDifference> picked, IReadOnlyList<ObjectDifference> deps)
    {
        var lines = new List<string>
        {
            $"[{Theme.Accent}]▌[/] [bold {Theme.Accent}]你勾選的[/] [{Theme.TextFaint}]({picked.Count})[/]",
        };
        foreach (var d in picked) lines.Add($"  [{Theme.Success}]✓[/] {Tag(d)}");
        lines.Add("");
        lines.Add($"[{Theme.Accent}]▌[/] [bold {Theme.Accent}]相依自動補入[/] [{Theme.TextFaint}]({deps.Count})[/] " +
                  $"[{Theme.TextMuted}]（為部署安全由系統補上，非你勾選）[/]");
        if (deps.Count == 0) lines.Add($"  [{Theme.TextFaint}](無)[/]");
        foreach (var d in deps) lines.Add($"  [{Theme.Warning}]+[/] {Tag(d)}");
        return lines;
    }

    private static string Tag(ObjectDifference d)
    {
        var kind = d.Kind switch
        {
            ChangeKind.Add => $"[{Theme.DiffAdd}]新增[/]",
            ChangeKind.Change => $"[{Theme.Warning}]變更[/]",
            ChangeKind.Delete => $"[{Theme.DiffDelete}]刪除[/]",
            _ => $"[{Theme.TextMuted}]其他[/]",
        };
        var name = ConsoleUI.Esc(ConsoleUI.Truncate(d.Name, Math.Max(10, ConsoleUI.Width - 24)));
        return $"{kind} [{Theme.TextMuted}]{ConsoleUI.Esc(d.ObjectTypeName)}[/] {name}";
    }

    /// <summary>畫一幀，回傳清單可用列數（供捲動計算）。整幀高度控制在視窗高度內，避免捲動殘影。</summary>
    private static int Render(Banner banner, int pickedCount, int depCount, List<string> lines, int top)
    {
        ConsoleUI.BeginFrame();
        banner.Compact();
        ConsoleUI.Line($"[{Theme.Success}]✔[/] [{Theme.Accent}]套用勾選結果[/]　" +
                       $"[{Theme.TextFaint}]Enter 確認匯出　B 返回重勾　Esc 取消[/]");
        ConsoleUI.Line();
        int total = pickedCount + depCount;
        ConsoleUI.Line($"勾選 [bold]{pickedCount}[/] 項，為部署安全自動補入相依 " +
                       $"[bold {Theme.Warning}]{depCount}[/] 項，共 [bold]{total}[/] 項");
        ConsoleUI.Line();

        // 固定開銷：精簡 banner(2)+標題(1)+空白(1)+摘要(1)+空白(1)=6，再留上下捲動提示(2)與安全邊界(1)。
        int maxRows = Math.Max(1, ConsoleUI.Height - 9);

        bool hasUp = top > 0;
        bool hasDown = top + maxRows < lines.Count;
        ConsoleUI.Line(hasUp ? $"  [{Theme.TextFaint}]↑ 上面還有 {top} 列[/]" : "");
        for (int i = top; i < Math.Min(lines.Count, top + maxRows); i++) ConsoleUI.Line(lines[i]);
        ConsoleUI.Line(hasDown ? $"  [{Theme.TextFaint}]↓ 下面還有 {lines.Count - (top + maxRows)} 列[/]" : "");

        ConsoleUI.EndFrame();
        return maxRows;
    }
}
