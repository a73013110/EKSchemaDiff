namespace EKSchemaDiff.Report;

/// <summary>
/// 攤平後的一筆顯示列：<see cref="Row"/> 非 null 表示一筆差異列；
/// 否則為折疊摘要列（代表壓縮掉的 <see cref="HiddenCount"/> 行連續未變更內容）。
/// </summary>
public sealed class DiffDisplayRow
{
    public DiffRow? Row { get; init; }
    public int HiddenCount { get; init; }
    public bool IsFold => Row is null;
}

/// <summary>
/// 差異顯示規劃（與渲染無關的純邏輯）：把逐行差異攤平成可捲動的顯示列序列，
/// 支援「折疊（只保留變更周邊 context）」與「完整檔案（全部保留）」兩種模式。
/// 對應 git/GitHub diff 的折疊與 expand entire file。渲染端再把每筆顯示列上色。
/// </summary>
public static class DiffView
{
    /// <summary>
    /// 攤平差異列。<paramref name="full"/>=true 時全部保留；否則只保留變更行與其前後
    /// <paramref name="contextLines"/> 行未變更內容，其餘連續未變更行壓成一筆折疊摘要列。
    /// </summary>
    public static List<DiffDisplayRow> Flatten(IReadOnlyList<DiffRow> rows, bool full, int contextLines)
    {
        int n = rows.Count;
        var keep = new bool[n];
        if (!full)
        {
            for (int i = 0; i < n; i++)
            {
                if (rows[i].Kind == DiffKind.Same) continue;
                int lo = Math.Max(0, i - contextLines);
                int hi = Math.Min(n - 1, i + contextLines);
                for (int j = lo; j <= hi; j++) keep[j] = true;
            }
        }

        var result = new List<DiffDisplayRow>(n);
        int hidden = 0;
        void Flush()
        {
            if (hidden == 0) return;
            result.Add(new DiffDisplayRow { HiddenCount = hidden });
            hidden = 0;
        }

        for (int i = 0; i < n; i++)
        {
            if (rows[i].Kind == DiffKind.Same && !full && !keep[i])
            {
                hidden++;
                continue;
            }
            Flush();
            result.Add(new DiffDisplayRow { Row = rows[i] });
        }
        Flush();

        return result;
    }
}
