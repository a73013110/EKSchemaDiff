using EKSchemaDiff.Core.Config;
using Spectre.Console;

namespace EKSchemaDiff.Cli.Tui;

/// <summary>profile 清單表格（Profile／來源／目標／忽略權限／輸出），供 profiles 命令與主選單共用。</summary>
public static class ProfileTable
{
    public static Table Build(IReadOnlyList<Profile> profiles, string? defaultProfile)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Theme.TextFaint.ToSpectre());
        table.AddColumn("Profile");
        table.AddColumn("來源（更版）");
        table.AddColumn("目標（原版）");
        table.AddColumn("忽略權限");
        table.AddColumn("輸出");

        foreach (var p in profiles)
        {
            var isDefault = string.Equals(p.Name, defaultProfile, StringComparison.OrdinalIgnoreCase);
            table.AddRow(
                isDefault ? $"[bold]{Markup.Escape(p.Name)}[/] [{Theme.TextMuted}](預設)[/]" : Markup.Escape(p.Name),
                Markup.Escape(p.Source.ToSafeDisplay()),
                Markup.Escape(p.Target.ToSafeDisplay()),
                p.CompareOptions.IgnorePermissions ? $"[{Theme.Success}]是[/]" : $"[{Theme.Danger}]否[/]",
                Markup.Escape(p.ExportOptions.Describe()));
        }
        return table;
    }
}
