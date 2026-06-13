using Spectre.Console;

namespace EKSchemaDiff.Cli.Tui;

/// <summary>自訂互動畫面的共用低階輔助：尺寸、截斷、捲動視窗、逐格重繪。</summary>
public static class ConsoleUi
{
    private const string Vt = "";   // ESC，VT 控制碼前綴

    /// <summary>是否能進行鍵盤互動（非重導向）。</summary>
    public static bool Interactive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public static int Width => SafeDim(() => Console.WindowWidth, 100, 40, 400);
    public static int Height => SafeDim(() => Console.WindowHeight, 30, 10, 200);

    private static int SafeDim(Func<int> get, int fallback, int min, int max)
    {
        try { return Math.Clamp(get(), min, max); }
        catch { return fallback; }
    }

    public static string Esc(string? text) => Markup.Escape(text ?? string.Empty);

    /// <summary>截斷到指定可見寬度，過長以 … 結尾。對純文字操作（呼叫端尚未加 markup）。</summary>
    public static string Truncate(string text, int maxWidth)
    {
        text = text.Replace("\t", "    ");
        if (maxWidth <= 1) return "";
        return text.Length <= maxWidth ? text : text[..(maxWidth - 1)] + "…";
    }

    /// <summary>字串的終端顯示寬度（CJK 全形字算 2）。</summary>
    public static int DisplayWidth(string text)
    {
        int w = 0;
        foreach (var ch in text) w += IsWide(ch) ? 2 : 1;
        return w;
    }

    /// <summary>把字串以空白補到指定顯示寬度（CJK 對齊用）。</summary>
    public static string PadDisplay(string text, int columns)
    {
        int pad = columns - DisplayWidth(text);
        return pad > 0 ? text + new string(' ', pad) : text;
    }

    private static bool IsWide(char c) =>
        c is >= 'ᄀ' and <= 'ᅟ'      // Hangul Jamo
        or >= '⺀' and <= '〾'        // CJK 部首、標點
        or >= 'ぁ' and <= '㏿'        // 假名、CJK 符號
        or >= '㐀' and <= '䶿'        // CJK 擴充 A
        or >= '一' and <= '鿿'        // CJK 統一
        or >= 'ꀀ' and <= '꓏'        // 彝文
        or >= '가' and <= '힣'        // 諺文音節
        or >= '豈' and <= '﫿'        // CJK 相容
        or >= '︰' and <= '﹏'        // CJK 相容形式
        or >= '＀' and <= '｠'        // 全形 ASCII
        or >= '￠' and <= '￦';       // 全形符號

    /// <summary>
    /// 讀一行文字，支援 Backspace、Enter（送出）、Esc（取消，回傳 null）。
    /// 直接回顯；空白送出時回傳 defaultValue。給連線設定等逐欄輸入用，讓 Esc 與全系統一致。
    /// </summary>
    public static string? ReadLineOrEsc(string promptMarkup, string? defaultValue = null)
    {
        AnsiConsole.Markup(promptMarkup);
        if (!string.IsNullOrEmpty(defaultValue))
            AnsiConsole.Markup($"[grey39]({Markup.Escape(defaultValue)})[/] ");

        Console.CursorVisible = true;
        var buf = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    Console.WriteLine();
                    return null;
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return buf.Length == 0 ? (defaultValue ?? string.Empty) : buf.ToString();
                case ConsoleKey.Backspace:
                    if (buf.Length > 0)
                    {
                        buf.Length--;
                        Console.Write("\b \b");
                    }
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buf.Append(key.KeyChar);
                        Console.Write(key.KeyChar);
                    }
                    break;
            }
        }
    }

    /// <summary>計算讓 cursor 可見的捲動起點。</summary>
    public static int ScrollTop(int cursor, int count, int visibleRows)
    {
        if (count <= visibleRows) return 0;
        int top = cursor - visibleRows / 2;
        return Math.Clamp(top, 0, count - visibleRows);
    }

    /// <summary>
    /// 開始重繪一格：游標歸位到左上，但不做整頁 Console.Clear()（那會在 Windows 造成閃爍）。
    /// 配合每行的 Line()（清到行尾）與 EndFrame()（清到畫面底）即可無閃爍覆寫。
    /// </summary>
    public static void BeginFrame()
    {
        Console.Out.Write(Vt + "[H");
    }

    /// <summary>輸出一行 markup，並清除該行游標後殘留（避免上一格較長內容留尾巴）。</summary>
    public static void Line(string markup = "")
    {
        AnsiConsole.Markup(markup);
        Console.Out.Write(Vt + "[K\n");
    }

    /// <summary>結束一格：清除游標以下到畫面底的所有殘留（處理上一格較高的情形）。</summary>
    public static void EndFrame()
    {
        Console.Out.Write(Vt + "[0J");
    }
}
