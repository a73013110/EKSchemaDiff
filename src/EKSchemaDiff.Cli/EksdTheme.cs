using ConsoleKit.Tui;

namespace EKSchemaDiff.Cli;

/// <summary>
/// EKSchemaDiff 的品牌色票（領域端佈景）。骨架 <see cref="Theme"/> 只內建中性預設，
/// 品牌外觀由領域提供並於組合根注入（<c>ConsoleHost.Run</c> 的 theme 參數）。
/// </summary>
public static class EksdTheme
{
    /// <summary>
    /// 香檳金（暖金）主題：中性灰為底、單一香檳金強調，Banner 走金屬漸層。
    /// 強調色克制，靠中性階的明度層次建立秩序——終端機版的「Apple 高級感」。
    /// </summary>
    public static ThemePalette Champagne { get; } = new()
    {
        Name = "champagne",

        Accent = new("#C8A45C"),

        TextPrimary = new("#F5F5F7"),
        TextSecondary = new("#B0B0B5"),
        TextMuted = new("#86868B"),
        TextFaint = new("#6E6E73"),
        Hairline = new("#3A3A3C"),

        Success = new("#3FB950"),
        Warning = new("#E3B341"),
        Danger = new("#F0726A"),

        DiffAdd = new("#7EE787"),
        DiffDelete = new("#FF7B72"),
        DiffContext = new("#86868B"),
        DiffGutter = new("#6E6E73"),
        DiffBar = new("#3A3A3C"),

        BannerTop = new("#EAD29A"),
        BannerMid = new("#C8A45C"),
        BannerBottom = new("#7E6838"),
    };
}
