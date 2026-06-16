using System.Text;
using EKSchemaDiff.Core.Config;
using Spectre.Console;

namespace EKSchemaDiff.Cli.Tui;

public static class Prompts
{
    /// <summary>SQL 驗證缺密碼時的互動輸入。</summary>
    public static string PromptPassword(string label) =>
        TextInput.PromptSecret($"請輸入 [{Theme.Accent}]{Markup.Escape(label)}[/]：");

    /// <summary>
    /// 從多組 profile 中互動挑選：以「卡片框」方式呈現每組情境（名稱＋來源／目標），
    /// 游標所在卡片以橘色外框＋左側箭頭標示。回傳選取的 profile；按 Esc 取消回傳 null。
    /// 只有一組時直接回傳，不顯示選單。
    /// </summary>
    public static Profile? PickProfile(IReadOnlyList<Profile> profiles, Banner banner)
    {
        if (profiles.Count == 1) return profiles[0];

        // 後援：非互動環境改用 Spectre 標準選單（無卡片框，但不會卡住）。
        if (!ConsoleUI.Interactive)
            return FallbackPick(profiles);

        int cursor = 0;
        // 進入時清一次，抹掉主選單殘留；之後逐格重繪維持無閃爍。
        AnsiConsole.Clear();
        try
        {
            Console.CursorVisible = false;
            while (true)
            {
                RenderCards(profiles, cursor, banner);
                var key = Console.ReadKey(intercept: true).Key;
                switch (key)
                {
                    case ConsoleKey.UpArrow:
                    case ConsoleKey.K:
                        cursor = (cursor - 1 + profiles.Count) % profiles.Count;
                        break;
                    case ConsoleKey.DownArrow:
                    case ConsoleKey.J:
                        cursor = (cursor + 1) % profiles.Count;
                        break;
                    case ConsoleKey.Enter:
                        return profiles[cursor];
                    case ConsoleKey.Escape:
                        return null;
                }
            }
        }
        finally
        {
            Console.CursorVisible = true;
        }
    }

    private const int CardLines = 5;   // 上框＋名稱＋來源＋目標＋下框

    /// <summary>把所有 profile 畫成卡片框，游標所在者以橘框標示；卡片過多時以游標為中心開窗。</summary>
    private static void RenderCards(IReadOnlyList<Profile> profiles, int cursor, Banner banner)
    {
        ConsoleUI.BeginFrame();
        banner.Compact();
        ConsoleUI.Line($"[{Theme.Accent}]選擇要使用的 profile[/]　[{Theme.TextFaint}]↑↓ 移動 · Enter 選擇 · Esc 返回[/]");
        ConsoleUI.Line();

        int boxW = Math.Min(ConsoleUI.Width - 4, 72);
        int innerW = boxW - 4;                       // 去掉左右框線各 1 與內縮空白各 1

        // 可見卡片數：保留頂端 banner/標題與底部說明的高度後，能放幾張算幾張。
        int perCard = CardLines + 1;                 // 卡片之間留一空行
        int budget = Math.Max(perCard, ConsoleUI.Height - 8);
        int maxCards = Math.Clamp(budget / perCard, 1, profiles.Count);
        int top = ConsoleUI.ScrollTop(cursor, profiles.Count, maxCards);

        if (top > 0)
            ConsoleUI.Line($"  [{Theme.TextFaint}]▲ 上方還有 {top} 組[/]");

        for (int i = top; i < Math.Min(profiles.Count, top + maxCards); i++)
            RenderCard(profiles[i], i == cursor, boxW, innerW);

        int below = profiles.Count - (top + maxCards);
        if (below > 0)
            ConsoleUI.Line($"  [{Theme.TextFaint}]▼ 下方還有 {below} 組[/]");

        // 底部顯示游標所在 profile 的說明（若有）。
        ConsoleUI.Line();
        var desc = profiles[cursor].Description;
        ConsoleUI.Line(string.IsNullOrWhiteSpace(desc)
            ? $"[{Theme.TextFaint}](此 profile 未填說明)[/]"
            : $"[{Theme.TextMuted}]{ConsoleUI.Esc(ConsoleUI.Truncate(desc!, ConsoleUI.Width - 2))}[/]");
        ConsoleUI.EndFrame();
    }

    private static void RenderCard(Profile p, bool selected, int boxW, int innerW)
    {
        string bc = (selected ? Theme.Accent : Theme.TextFaint).ToString();   // 框線顏色
        string gut = "  ";                                     // 一般列左側留白
        string arrow = selected ? $"[{Theme.Accent}]›[/] " : "  ";   // 游標箭頭只放在名稱列
        string dot = selected ? $"[{Theme.Success}]●[/]" : $"[{Theme.TextFaint}]○[/]";

        string bar = new string('─', boxW - 2);
        ConsoleUI.Line($"{gut}[{bc}]╭{bar}╮[/]");

        // 名稱列：圓點 + profile 名稱（選中時加粗）；箭頭只標示在這一列。
        string name = FitDisplay(p.Name, innerW - 2);
        string nameMk = selected ? $"[bold]{ConsoleUI.Esc(name)}[/]" : ConsoleUI.Esc(name);
        BoxRow(arrow, bc, innerW, $"{dot} {nameMk}", 2 + ConsoleUI.DisplayWidth(name));

        BoxRow(gut, bc, innerW, ValueRow("來源", p.Source.ToSafeDisplay(), innerW, Theme.Success.ToString()), innerW);
        BoxRow(gut, bc, innerW, ValueRow("目標", p.Target.ToSafeDisplay(), innerW, Theme.Warning.ToString()), innerW, padded: true);

        ConsoleUI.Line($"{gut}[{bc}]╰{bar}╯[/]");
    }

    /// <summary>組出「標籤  值」一列的 markup，並回傳已含右側補白的完整內容（顯示寬度＝innerW）。</summary>
    private static string ValueRow(string label, string value, int innerW, string valueColor)
    {
        int labelW = ConsoleUI.DisplayWidth(label);
        int valCols = Math.Max(4, innerW - labelW - 2);
        string val = FitDisplay(value, valCols);
        int used = labelW + 2 + ConsoleUI.DisplayWidth(val);
        int pad = Math.Max(0, innerW - used);
        return $"[{Theme.TextMuted}]{ConsoleUI.Esc(label)}[/]  [{valueColor}]{ConsoleUI.Esc(val)}[/]{new string(' ', pad)}";
    }

    /// <summary>畫一列框內內容：左右框線中間放入 contentMarkup，並補白到 innerW 顯示寬度。</summary>
    private static void BoxRow(string lead, string bc, int innerW, string contentMarkup, int contentWidth, bool padded = false)
    {
        // ValueRow 已自行補白；名稱列等則在此補白到 innerW。
        string filler = padded || contentWidth >= innerW ? "" : new string(' ', innerW - contentWidth);
        ConsoleUI.Line($"{lead}[{bc}]│[/] {contentMarkup}{filler} [{bc}]│[/]");
    }

    /// <summary>截斷到指定「顯示寬度」（CJK 全形算 2），過長以 … 結尾。</summary>
    private static string FitDisplay(string text, int cols)
    {
        text = (text ?? string.Empty).Replace("\t", " ");
        if (cols <= 1) return "";
        if (ConsoleUI.DisplayWidth(text) <= cols) return text;

        var sb = new StringBuilder();
        int w = 0;
        foreach (var ch in text)
        {
            int cw = ConsoleUI.DisplayWidth(ch.ToString());
            if (w + cw > cols - 1) break;
            sb.Append(ch);
            w += cw;
        }
        sb.Append('…');
        return sb.ToString();
    }

    private static Profile? FallbackPick(IReadOnlyList<Profile> profiles)
    {
        var prompt = new SelectionPrompt<Profile>()
            .Title($"選擇要使用的 [{Theme.Accent}]profile[/]")
            .UseConverter(p => $"{p.Name}  ({p.Source.ToSafeDisplay()} → {p.Target.ToSafeDisplay()})");
        foreach (var p in profiles) prompt.AddChoice(p);
        return AnsiConsole.Prompt(prompt);
    }
}
