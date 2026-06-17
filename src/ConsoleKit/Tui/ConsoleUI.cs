using Spectre.Console;

namespace ConsoleKit.Tui;

/// <summary>自訂互動畫面的共用低階輔助：尺寸、截斷、捲動視窗、逐格重繪。</summary>
public static class ConsoleUI
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
            AnsiConsole.Markup($"[{Theme.TextFaint}]({Markup.Escape(defaultValue)})[/] ");

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
    /// 進入替代螢幕緩衝區（alternate screen buffer）：給持續重繪的動畫畫面用，
    /// 期間的輸出不會留進正常的捲動歷史；離開時還原進入前的畫面。配合 <see cref="LeaveAltScreen"/>。
    /// </summary>
    public static void EnterAltScreen()
    {
        Console.Out.Write(Vt + "[?1049h" + Vt + "[H");
    }

    /// <summary>離開替代螢幕緩衝區，還原進入前的畫面（動畫殘影不會污染捲動歷史）。</summary>
    public static void LeaveAltScreen()
    {
        Console.Out.Write(Vt + "[?1049l");
    }

    /// <summary>
    /// 停用終端自動換行（DECAWM，<c>ESC[?7l</c>）：逐格重繪時，可見寬度逼近視窗寬的一行會被截在
    /// 右邊界、**不**折成第二實體列；否則實體行數 &gt; 邏輯行數會撐破預算的幀高，使 <see cref="BeginFrame"/>
    /// 的歸位基準逐格漂移、畫面整體上移殘留——這是「內容一多就壞」的元兇。與 <see cref="EnableLineWrap"/> 成對。
    /// </summary>
    public static void DisableLineWrap() => Console.Out.Write(Vt + "[?7l");

    /// <summary>恢復終端自動換行（<c>ESC[?7h</c>）。離開逐格重繪畫面時呼叫，避免污染正常輸出。</summary>
    public static void EnableLineWrap() => Console.Out.Write(Vt + "[?7h");

    /// <summary>
    /// 進入逐格重繪模式：隱藏游標並停用自動換行。與 <see cref="ExitRedrawMode"/> 成對。
    /// 從子畫面（如 DiffScreen）返回後**需再呼叫一次**重新確立——子畫面離開時會還原這些終端狀態。
    /// </summary>
    public static void EnterRedrawMode()
    {
        try { Console.CursorVisible = false; } catch { /* 重導向或不支援時略過 */ }
        DisableLineWrap();
    }

    /// <summary>離開逐格重繪模式：恢復自動換行並顯示游標。應置於畫面的 finally。</summary>
    public static void ExitRedrawMode()
    {
        EnableLineWrap();
        try { Console.CursorVisible = true; } catch { /* 重導向或不支援時略過 */ }
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

    /// <summary>
    /// 輸出整幀的「最後一列」：清除行尾殘留（<c>ESC[K</c>）但**不換行**，讓游標停在該列、
    /// 不在畫面最底列觸發終端的底部捲動。這是把畫面鋪滿到最後一列（row h-1）又不讓整幀上移殘留的關鍵：
    /// 用 <see cref="Line"/> 印滿 h 列時，最後一行的 <c>\n</c> 會把整幀往上頂一列；改以本方法收尾即可避免。
    /// 必須是該幀最後一個輸出，後接 <see cref="EndFrame"/>。
    /// </summary>
    public static void LineLast(string markup = "")
    {
        AnsiConsole.Markup(markup);
        Console.Out.Write(Vt + "[K");
    }

    /// <summary>結束一格：清除游標以下到畫面底的所有殘留（處理上一格較高的情形）。</summary>
    public static void EndFrame()
    {
        Console.Out.Write(Vt + "[0J");
    }
}
