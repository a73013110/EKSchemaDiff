using EKSchemaDiff.Core.Config;
using Spectre.Console;

namespace EKSchemaDiff.Cli.Tui;

public static class Prompts
{
    /// <summary>SQL 驗證缺密碼時的互動輸入。</summary>
    public static string PromptPassword(string label) =>
        AnsiConsole.Prompt(new TextPrompt<string>($"請輸入 [yellow]{Markup.Escape(label)}[/]：").Secret());

    /// <summary>
    /// 從多組 profile 中互動挑選；與主選單同一套畫面（Banner + 游標保留 + Esc 返回）。
    /// 回傳選取的 profile；按 Esc 取消時回傳 null。只有一組時直接回傳，不顯示選單。
    /// </summary>
    public static Profile? PickProfile(IReadOnlyList<Profile> profiles)
    {
        if (profiles.Count == 1) return profiles[0];

        var items = profiles
            .Select(p => new MenuItem
            {
                Label = $"[bold]{Markup.Escape(p.Name)}[/]",
                Description = $"{p.Source.ToSafeDisplay()} → {p.Target.ToSafeDisplay()}",
            })
            .ToList();

        int idx = Menu.Show(
            "選擇要使用的 [yellow]profile[/]",
            () => items,
            header: Banner.Show);

        return idx < 0 ? null : profiles[idx];
    }
}
