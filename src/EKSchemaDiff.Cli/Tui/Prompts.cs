using EKSchemaDiff.Core.Config;
using Spectre.Console;

namespace EKSchemaDiff.Cli.Tui;

public static class Prompts
{
    /// <summary>SQL 驗證缺密碼時的互動輸入。</summary>
    public static string PromptPassword(string label) =>
        AnsiConsole.Prompt(new TextPrompt<string>($"請輸入 [yellow]{Markup.Escape(label)}[/]：").Secret());

    /// <summary>從多組 profile 中互動挑選。</summary>
    public static Profile PickProfile(IReadOnlyList<Profile> profiles)
    {
        if (profiles.Count == 1) return profiles[0];

        var byLabel = new Dictionary<string, Profile>();
        foreach (var p in profiles)
        {
            var label = $"{p.Name}  [grey]{Markup.Escape(p.Source.ToSafeDisplay())} → {Markup.Escape(p.Target.ToSafeDisplay())}[/]";
            byLabel[label] = p;
        }

        var picked = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("選擇要使用的 [yellow]profile[/]")
                .PageSize(15)
                .AddChoices(byLabel.Keys));
        return byLabel[picked];
    }

    /// <summary>匯出前選擇部署 SQL 的輸出形式。</summary>
    public static DeployScriptMode SelectExportMode(DeployScriptMode current)
    {
        var map = new Dictionary<string, DeployScriptMode>
        {
            ["單一完整檔 + 依序切分檔（建議）"] = DeployScriptMode.Both,
            ["只要單一完整檔 FullScript.sql"] = DeployScriptMode.Single,
            ["只要依序切分的個別檔"] = DeployScriptMode.SplitOrdered,
        };
        var defaultLabel = map.First(kv => kv.Value == current).Key;
        var ordered = new List<string> { defaultLabel };
        ordered.AddRange(map.Keys.Where(k => k != defaultLabel));

        var pick = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("部署 SQL 要輸出哪一種？")
                .AddChoices(ordered));
        return map[pick];
    }
}
