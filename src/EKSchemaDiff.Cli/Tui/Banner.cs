using Spectre.Console;

namespace EKSchemaDiff.Cli.Tui;

public static class Banner
{
    /// <summary>完整 Banner：大字 Logo + 漸層色 + 標語規則線。用於主選單、設定頁等頁面頂端。</summary>
    public static void Show()
    {
        var figlet = new FigletText("EKSchemaDiff")
        {
            Justification = Justify.Left,
            Color = Color.DarkOrange3,
        };
        AnsiConsole.Write(figlet);

        var rule = new Rule("[orange3]SQL Server 結構比對[/] [grey39]·[/] [orange1]發版差異 CLI[/]   [grey]by Yikai[/]")
        {
            Justification = Justify.Left,
            Style = Style.Parse("grey39"),
        };
        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();
    }

    /// <summary>精簡單行 Banner：給空間有限的畫面（例如資料密集的列表）使用。</summary>
    public static void Compact()
    {
        AnsiConsole.Write(new Rule("[darkorange3]EKSchemaDiff[/] [grey39]·[/] [grey]SQL Server 結構比對 · by Yikai[/]")
        {
            Justification = Justify.Left,
            Style = Style.Parse("grey39"),
        });
        AnsiConsole.WriteLine();
    }
}
