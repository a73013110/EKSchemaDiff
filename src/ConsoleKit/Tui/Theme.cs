using Spectre.Console;

namespace ConsoleKit.Tui;

/// <summary>
/// 一個語意色彩 token：同時可作為 Spectre markup 標籤（<c>$"[{Theme.Accent}]…[/]"</c>，
/// 透過 <see cref="ToString"/> 取 <c>#RRGGBB</c>）與 Spectre <see cref="Color"/>（API 參數，
/// 透過 <see cref="ToSpectre"/>）。一律以 hex 儲存，避免具名色在不同終端的色差。
/// </summary>
public readonly struct ThemeColor
{
    private readonly string _hex;   // 含 '#'，可直接內嵌進 markup

    public ThemeColor(string hex)
    {
        _hex = string.IsNullOrWhiteSpace(hex) ? "#FFFFFF" : hex.Trim();
        (R, G, B) = ParseRgb(_hex);
    }

    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    /// <summary>給需要 <see cref="Color"/> 物件的 Spectre API（FigletText、BorderColor、Style 等）。</summary>
    public Color ToSpectre() => new(R, G, B);

    /// <summary>內嵌進 markup 用的 <c>#RRGGBB</c> 字串：<c>$"[{token}]…[/]"</c>、<c>$"[bold {token}]…[/]"</c>。</summary>
    public override string ToString() => _hex;

    public static implicit operator ThemeColor(string hex) => new(hex);

    /// <summary>在本色與 <paramref name="other"/> 之間做 RGB 線性內插，回傳 <c>#RRGGBB</c>（給漸層用）。</summary>
    public string MixHex(ThemeColor other, double t)
    {
        t = Math.Clamp(t, 0, 1);
        int r = (int)Math.Round(R + (other.R - R) * t);
        int g = (int)Math.Round(G + (other.G - G) * t);
        int b = (int)Math.Round(B + (other.B - B) * t);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static (byte, byte, byte) ParseRgb(string hex)
    {
        var s = hex.StartsWith('#') ? hex[1..] : hex;
        if (s.Length == 6
            && byte.TryParse(s.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(s.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(s.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return (r, g, b);
        return (255, 255, 255);
    }
}

/// <summary>
/// 一套完整的佈景色票（語意 token 的具體取值）。新增主題＝多寫一個 <see cref="ThemePalette"/> 實例；
/// 切換主題＝把它指給 <see cref="Theme.Current"/>。token 為「語意」而非「顏色名」，
/// 讓畫面程式碼描述用途（強調／次要／危險…）而非寫死顏色，達成可複用、可維護、可換膚。
/// </summary>
public sealed record ThemePalette
{
    public required string Name { get; init; }

    // ── 強調（品牌主色）
    /// <summary>主強調：游標、區段標記 ▌、面板/卡片選取框、標題重點、進度轉圈。</summary>
    public required ThemeColor Accent { get; init; }

    // ── 文字層級（由亮到暗的中性階）
    /// <summary>主要文字（最高對比）：聚焦項、輸入標籤、強調名稱。</summary>
    public required ThemeColor TextPrimary { get; init; }
    /// <summary>次要文字：一般清單項、欄位值。</summary>
    public required ThemeColor TextSecondary { get; init; }
    /// <summary>弱化文字：標籤、欄位名、說明、統計數字旁註。</summary>
    public required ThemeColor TextMuted { get; init; }
    /// <summary>提示文字：頁尾、快捷鍵提示、未選取框線、規則線。</summary>
    public required ThemeColor TextFaint { get; init; }
    /// <summary>髮絲線：最弱的分隔——預覽細邊、行號槽、深層分隔。</summary>
    public required ThemeColor Hairline { get; init; }

    // ── 語意狀態
    /// <summary>成功／新增／開啟。</summary>
    public required ThemeColor Success { get; init; }
    /// <summary>警告／變更／待議。</summary>
    public required ThemeColor Warning { get; init; }
    /// <summary>危險／刪除／關閉（風險）。</summary>
    public required ThemeColor Danger { get; init; }

    // ── diff 預覽專用（柔和、不刺眼，呈現精緻層次）
    public required ThemeColor DiffAdd { get; init; }       // 新版（更版）文字／色邊
    public required ThemeColor DiffDelete { get; init; }    // 原版（被取代）文字／色邊
    public required ThemeColor DiffContext { get; init; }   // 未變更內容
    public required ThemeColor DiffGutter { get; init; }    // 行號
    public required ThemeColor DiffBar { get; init; }       // context 列的左側細邊

    // ── Banner 金屬漸層（由上而下：亮 → 中 → 暗，營造金屬反光）
    public required ThemeColor BannerTop { get; init; }
    public required ThemeColor BannerMid { get; init; }
    public required ThemeColor BannerBottom { get; init; }

    /// <summary>
    /// 骨架內建的中性預設色票（鋼灰）：中性灰為底、克制的鋼藍強調，Banner 走銀／鋼金屬漸層。
    /// 不帶任何品牌個性，純粹讓骨架「開箱即有合理外觀」；領域端應提供自己的 <see cref="ThemePalette"/>
    /// 並於組合根注入（見 <c>ConsoleHost.Run</c> 的 theme 參數），覆蓋此預設。
    /// </summary>
    public static ThemePalette Neutral { get; } = new()
    {
        Name = "neutral",

        Accent = new("#7FA8C9"),

        TextPrimary = new("#F0F0F2"),
        TextSecondary = new("#B5B5BA"),
        TextMuted = new("#8A8A90"),
        TextFaint = new("#6E6E73"),
        Hairline = new("#3A3A3C"),

        Success = new("#3FB950"),
        Warning = new("#E3B341"),
        Danger = new("#F0726A"),

        DiffAdd = new("#7EE787"),
        DiffDelete = new("#FF7B72"),
        DiffContext = new("#8A8A90"),
        DiffGutter = new("#6E6E73"),
        DiffBar = new("#3A3A3C"),

        BannerTop = new("#D8DEE4"),
        BannerMid = new("#9AA4AE"),
        BannerBottom = new("#5A636C"),
    };
}

/// <summary>
/// 佈景的環境式存取點（ambient static）。畫面程式碼一律透過 <c>Theme.Accent</c> 等 token 取色，
/// 不直接寫顏色；切換主題只需設定 <see cref="Current"/>，所有畫面隨之變更、呼叫端零改動。
/// 採靜態而非 DI，是為了讓專案內大量的 <c>static</c> UI 輔助（Menu/ConsoleUI/Prompts…）也能取用同一主題。
/// </summary>
public static class Theme
{
    /// <summary>
    /// 目前生效的色票。預設為骨架內建的中性 <see cref="ThemePalette.Neutral"/>；
    /// 領域端於組合根透過 <c>ConsoleHost.Run</c> 的 theme 參數（或直接呼叫 <see cref="Use"/>）覆蓋。
    /// </summary>
    public static ThemePalette Current { get; private set; } = ThemePalette.Neutral;

    /// <summary>切換主題：組合根注入領域色票，或未來提供「主題切換」功能時呼叫。</summary>
    public static void Use(ThemePalette palette) =>
        Current = palette ?? throw new ArgumentNullException(nameof(palette));

    public static ThemeColor Accent => Current.Accent;
    public static ThemeColor TextPrimary => Current.TextPrimary;
    public static ThemeColor TextSecondary => Current.TextSecondary;
    public static ThemeColor TextMuted => Current.TextMuted;
    public static ThemeColor TextFaint => Current.TextFaint;
    public static ThemeColor Hairline => Current.Hairline;
    public static ThemeColor Success => Current.Success;
    public static ThemeColor Warning => Current.Warning;
    public static ThemeColor Danger => Current.Danger;
    public static ThemeColor DiffAdd => Current.DiffAdd;
    public static ThemeColor DiffDelete => Current.DiffDelete;
    public static ThemeColor DiffContext => Current.DiffContext;
    public static ThemeColor DiffGutter => Current.DiffGutter;
    public static ThemeColor DiffBar => Current.DiffBar;

    /// <summary>Banner 金屬漸層在比例 <paramref name="t"/>（0=頂、1=底）處的顏色，回傳 <c>#RRGGBB</c>。</summary>
    public static string MetallicAt(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t < 0.5
            ? Current.BannerTop.MixHex(Current.BannerMid, t * 2)
            : Current.BannerMid.MixHex(Current.BannerBottom, (t - 0.5) * 2);
    }
}
