using Spectre.Console;

namespace ConsoleKit.Tui;

/// <summary>
/// 應用程式 Banner（大字 Logo + 標語）。中性元件：品牌文字來自注入的 <see cref="AppInfo"/>，
/// 顏色與排版固定於此。AppInfo 文字為純文字，組進 Rule markup 時逐一逸出，避免破壞畫面。
/// </summary>
public sealed class Banner
{
    private readonly AppInfo _app;

    public Banner(AppInfo app) => _app = app;

    /// <summary>完整 Banner：大字 Logo + 漸層色 + 標語規則線。用於主選單、設定頁等頁面頂端。</summary>
    public void Show()
    {
        var figlet = new FigletText(_app.DisplayName)   // FigletText 取純文字，不需逸出
        {
            Justification = Justify.Left,
            Color = Color.DarkOrange3,
        };
        AnsiConsole.Write(figlet);

        var primary = Markup.Escape(_app.PrimaryTagline);
        var secondary = Markup.Escape(_app.SecondaryTagline);
        var author = Markup.Escape(_app.Author);
        var rule = new Rule($"[orange3]{primary}[/] [grey39]·[/] [orange1]{secondary}[/]   [grey]by {author}[/]")
        {
            Justification = Justify.Left,
            Style = Style.Parse("grey39"),
        };
        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();
    }

    /// <summary>精簡單行 Banner：給空間有限的畫面（例如資料密集的列表）使用。</summary>
    public void Compact()
    {
        var name = Markup.Escape(_app.DisplayName);
        var primary = Markup.Escape(_app.PrimaryTagline);
        var author = Markup.Escape(_app.Author);
        AnsiConsole.Write(new Rule($"[darkorange3]{name}[/] [grey39]·[/] [grey]{primary} · by {author}[/]")
        {
            Justification = Justify.Left,
            Style = Style.Parse("grey39"),
        });
        AnsiConsole.WriteLine();
    }
}
