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
