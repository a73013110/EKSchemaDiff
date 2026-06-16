using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace EKSchemaDiff.Report;

public enum DiffKind { Same, Modified, Added, Removed }

/// <summary>行內片段的種類：未變更或變更（供行內高亮上色用）。</summary>
public enum SegmentKind { Unchanged, Changed }

/// <summary>一行裡的一段文字＋是否為變更段。用於「同一行內只標出改掉的字詞」的行內高亮。</summary>
public sealed record DiffSegment(string Text, SegmentKind Kind);

public sealed class DiffRow
{
    public int LeftNumber { get; init; }
    public int RightNumber { get; init; }
    public string Left { get; init; } = string.Empty;
    public string Right { get; init; } = string.Empty;
    public DiffKind Kind { get; init; }

    /// <summary>左側（更版）此行的行內片段；Modified 行才會切成多段，其餘為單段。</summary>
    public IReadOnlyList<DiffSegment> LeftSegments { get; init; } = Array.Empty<DiffSegment>();

    /// <summary>右側（原版）此行的行內片段。</summary>
    public IReadOnlyList<DiffSegment> RightSegments { get; init; } = Array.Empty<DiffSegment>();
}

/// <summary>
/// 逐行差異引擎，後端為 DiffPlex 的 <see cref="SideBySideDiffBuilder"/>：
/// 行級對齊用 LCS，Modified 行再做字詞級（word-level）子差異，產出行內高亮所需的片段。
/// 方向：左＝更版（DiffPlex 的 new），右＝原版（DiffPlex 的 old）。
/// </summary>
public static class DiffEngine
{
    private static readonly SideBySideDiffBuilder Builder = new(new Differ());

    /// <summary>統一換行符並去除尾端空行，避免 DiffPlex 把尾端空白行算成多餘差異。</summary>
    private static string Normalize(string? text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');

    public static List<DiffRow> Compare(string leftText, string rightText, bool ignoreWhitespace)
    {
        // DiffPlex：BuildDiffModel(oldText, newText)。old＝原版（右），new＝更版（左）。
        var model = Builder.BuildDiffModel(
            Normalize(rightText), Normalize(leftText), ignoreWhitespace);

        var left = model.NewText.Lines;   // 更版
        var right = model.OldText.Lines;  // 原版
        int count = Math.Max(left.Count, right.Count);

        var rows = new List<DiffRow>(count);
        for (int i = 0; i < count; i++)
        {
            var l = i < left.Count ? left[i] : null;
            var r = i < right.Count ? right[i] : null;
            var kind = Classify(l, r);

            rows.Add(new DiffRow
            {
                LeftNumber = l?.Position ?? 0,
                RightNumber = r?.Position ?? 0,
                Left = l?.Text ?? string.Empty,
                Right = r?.Text ?? string.Empty,
                Kind = kind,
                LeftSegments = Segments(l, kind == DiffKind.Modified),
                RightSegments = Segments(r, kind == DiffKind.Modified),
            });
        }
        return rows;
    }

    private static DiffKind Classify(DiffPiece? left, DiffPiece? right)
    {
        var lt = left?.Type ?? ChangeType.Imaginary;
        var rt = right?.Type ?? ChangeType.Imaginary;
        if (lt == ChangeType.Unchanged && rt == ChangeType.Unchanged) return DiffKind.Same;
        if (lt == ChangeType.Inserted) return DiffKind.Added;   // 只存在於更版
        if (rt == ChangeType.Deleted) return DiffKind.Removed;  // 只存在於原版
        return DiffKind.Modified;                               // 兩側皆 Modified（同位置內容不同）
    }

    /// <summary>
    /// 把一行拆成行內片段：Modified 行用 DiffPlex 的字詞級 SubPieces（未變更字詞與變更字詞交錯），
    /// 其餘行整行視為單一片段（context＝未變更；純新增／刪除＝變更）。
    /// </summary>
    private static IReadOnlyList<DiffSegment> Segments(DiffPiece? line, bool modified)
    {
        if (line is null || line.Type == ChangeType.Imaginary)
            return Array.Empty<DiffSegment>();

        if (modified && line.SubPieces.Count > 0)
        {
            var segs = new List<DiffSegment>(line.SubPieces.Count);
            foreach (var p in line.SubPieces)
            {
                if (p.Type == ChangeType.Imaginary || string.IsNullOrEmpty(p.Text)) continue;
                var kind = p.Type == ChangeType.Unchanged ? SegmentKind.Unchanged : SegmentKind.Changed;
                segs.Add(new DiffSegment(p.Text, kind));
            }
            if (segs.Count > 0) return segs;
        }

        var whole = line.Type == ChangeType.Unchanged ? SegmentKind.Unchanged : SegmentKind.Changed;
        return new[] { new DiffSegment(line.Text ?? string.Empty, whole) };
    }
}
