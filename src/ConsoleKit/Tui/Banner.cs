using Spectre.Console;

namespace ConsoleKit.Tui;

/// <summary>
/// 應用程式 Banner（大字 Logo + 副標）。中性元件：品牌文字來自注入的 <see cref="AppInfo"/>，
/// 顏色與排版取自 <see cref="Theme"/>（不寫死顏色）。Logo 走金屬漸層，副標以髮絲規則線收斂出層次。
/// </summary>
public sealed class Banner
{
    private readonly AppInfo _app;

    public Banner(AppInfo app) => _app = app;

    /// <summary>
    /// 完整 Banner：金屬漸層大字 Logo + 全名副標規則線。用於主選單、設定頁等頁面頂端。
    /// Logo 取短版字標（<see cref="AppInfo.LogoText"/>），以粗塊字型渲染後逐列由亮到暗著色，營造金屬反光。
    /// </summary>
    public void Show()
    {
        var lines = BlockFont.Render(_app.LogoText);
        int n = lines.Count;

        var version = _app.Version;
        bool hasVersion = !string.IsNullOrWhiteSpace(version);

        AnsiConsole.WriteLine();   // 頂部留白，讓 Logo 有呼吸空間
        for (int i = 0; i < n; i++)
        {
            var color = Theme.MetallicAt(n <= 1 ? 0 : (double)i / (n - 1));
            // 縮排兩格，與選單項目左緣對齊，避免 Logo 緊貼邊框。
            var line = $"  [{color}]{Markup.Escape(lines[i])}[/]";
            // 版號角標：貼在 Logo 末列（基線）尾端，暗色細字，像產品字標旁的小角標——點到為止、不搶 Logo。
            if (hasVersion && i == n - 1)
                line += $"  [{Theme.TextFaint}]v{Markup.Escape(version)}[/]";
            AnsiConsole.MarkupLine(line);
        }
        AnsiConsole.WriteLine();   // Logo 與副標之間留白，避免擠在一起
        AnsiConsole.Write(Subtitle());
        AnsiConsole.WriteLine();   // 底部留白
    }

    /// <summary>精簡單行 Banner：給空間有限的畫面（例如資料密集的列表）使用，無大字 Logo。</summary>
    public void Compact()
    {
        var name = Markup.Escape(_app.DisplayName);
        var primary = Markup.Escape(_app.PrimaryTagline);
        var author = Markup.Escape(_app.Author);
        var versionTag = string.IsNullOrWhiteSpace(_app.Version)
            ? string.Empty
            : $"  [{Theme.TextFaint}]v{Markup.Escape(_app.Version)}[/]";
        AnsiConsole.Write(new Rule(
            $"[{Theme.Accent}]{name}[/] [{Theme.Hairline}]·[/] [{Theme.TextMuted}]{primary} · by {author}[/]{versionTag}")
        {
            Justification = Justify.Left,
            Style = new Style(foreground: Theme.Hairline.ToSpectre()),
        });
        AnsiConsole.WriteLine();
    }

    /// <summary>全名副標：大字 Logo 是短版字標，這裡用一行小字補上專案全名與標語，並以髮絲線收尾。</summary>
    private Rule Subtitle()
    {
        var name = Markup.Escape(_app.DisplayName);
        var primary = Markup.Escape(_app.PrimaryTagline);
        var secondary = Markup.Escape(_app.SecondaryTagline);
        var author = Markup.Escape(_app.Author);
        return new Rule(
            $"[{Theme.Accent}]{name}[/]  [{Theme.Hairline}]·[/]  " +
            $"[{Theme.TextMuted}]{primary} · {secondary}[/]   [{Theme.TextFaint}]by {author}[/]")
        {
            Justification = Justify.Left,
            Style = new Style(foreground: Theme.Hairline.ToSpectre()),
        };
    }
}
