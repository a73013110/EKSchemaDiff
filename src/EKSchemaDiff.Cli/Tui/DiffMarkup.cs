using System.Text;
using ConsoleKit.Text;
using ConsoleKit.Tui;

namespace EKSchemaDiff.Cli.Tui;

/// <summary>
/// diff 行內渲染共用工具：把一行的行內片段（<see cref="DiffSegment"/>）轉成 Spectre markup，
/// 變更字詞以粗體＋變更色高亮、未變更字詞用基底色，並限制在指定終端顯示寬度內（超出補 …）。
/// 由合併預覽（<see cref="ReviewScreen"/>）與全螢幕詳檢（<see cref="DiffScreen"/>）共用。
/// </summary>
internal static class DiffMarkup
{
    public static string Segments(
        IReadOnlyList<DiffSegment> segments, string baseColor, string changedColor, int width)
    {
        var sb = new StringBuilder();
        int remaining = width;
        foreach (var seg in segments)
        {
            if (remaining <= 0) break;
            var text = seg.Text.Replace("\t", "    ");
            var color = seg.Kind == SegmentKind.Changed ? changedColor : baseColor;
            var weight = seg.Kind == SegmentKind.Changed ? "bold " : "";
            int w = ConsoleUI.DisplayWidth(text);
            if (w <= remaining)
            {
                sb.Append($"[{weight}{color}]{ConsoleUI.Esc(text)}[/]");
                remaining -= w;
            }
            else
            {
                var clipped = Clip(text, Math.Max(1, remaining - 1));
                sb.Append($"[{weight}{color}]{ConsoleUI.Esc(clipped)}…[/]");
                remaining = 0;
            }
        }
        return sb.Length == 0 ? $"[{baseColor}] [/]" : sb.ToString();
    }

    /// <summary>截取字串前段，使其終端顯示寬度不超過 budget（CJK 全形算 2）。</summary>
    private static string Clip(string text, int budget)
    {
        var sb = new StringBuilder();
        int w = 0;
        foreach (var ch in text)
        {
            int cw = ConsoleUI.DisplayWidth(ch.ToString());
            if (w + cw > budget) break;
            sb.Append(ch);
            w += cw;
        }
        return sb.ToString();
    }
}
