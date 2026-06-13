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
}
