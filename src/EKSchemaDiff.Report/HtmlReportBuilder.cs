using System.Net;
using System.Text;

namespace EKSchemaDiff.Report;

/// <summary>總覽頁的一列資料。</summary>
public sealed class ReportIndexRow
{
    public string Sequence { get; init; } = "";
    public string Action { get; init; } = "";
    public string ObjectType { get; init; } = "";
    public string ObjectName { get; init; } = "";
    public string Status { get; init; } = "";
    public int DifferenceRows { get; init; }
    public string ReportFile { get; init; } = "";
}

/// <summary>
/// 產生暖色系差異 HTML 報告。樣式沿用原 4.產生差異比對.ps1 的設計：
/// 左側更版／右側原版、逐物件報告頁、總覽頁。
/// 為避免 CSS 大括號與 C# 內插衝突，模板使用 __TOKEN__ 占位符再做替換。
/// </summary>
public static class HtmlReportBuilder
{
    private static string Enc(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    /// <summary>產生單一物件的逐行差異報告。回傳 (html, 差異列數)。</summary>
    public static (string Html, int DifferenceCount) BuildObjectReport(
        string objectName, string action, string newText, string oldText,
        bool ignoreWhitespace, DateTime generatedAt)
    {
        var rows = DiffEngine.Compare(newText, oldText, ignoreWhitespace);
        int differenceCount = rows.Count(r => r.Kind != DiffKind.Same);
        var stamp = generatedAt.ToString("yyyy/MM/dd HH:mm:ss");

        var rowHtml = new StringBuilder();
        foreach (var row in rows)
        {
            var className = row.Kind.ToString().ToLowerInvariant();
            var leftNumber = row.LeftNumber == 0 ? "" : row.LeftNumber.ToString();
            var rightNumber = row.RightNumber == 0 ? "" : row.RightNumber.ToString();
            var marker = row.Kind switch
            {
                DiffKind.Modified => "M",
                DiffKind.Added => "+",
                DiffKind.Removed => "−",
                _ => "",
            };
            rowHtml.Append($"<tr class=\"{className}\"><td class=\"num\">{leftNumber}</td><td class=\"code\"><pre>{Enc(row.Left)}</pre></td><td class=\"mark\">{marker}</td><td class=\"num\">{rightNumber}</td><td class=\"code\"><pre>{Enc(row.Right)}</pre></td></tr>\r\n");
        }

        var html = ObjectReportTemplate
            .Replace("__OBJNAME__", Enc(objectName))
            .Replace("__ACTION__", Enc(action))
            .Replace("__STAMP__", stamp)
            .Replace("__COUNT__", differenceCount.ToString())
            .Replace("__ROWS__", rowHtml.ToString().TrimEnd());

        return (html, differenceCount);
    }

    /// <summary>產生總覽頁。</summary>
    public static string BuildOverview(
        IReadOnlyList<ReportIndexRow> rows, string profileName, DateTime generatedAt,
        IReadOnlyList<string>? warnings = null)
    {
        var stamp = generatedAt.ToString("yyyy/MM/dd HH:mm:ss");
        int totalCount = rows.Count;
        int changedCount = rows.Count(r => r.Status != "無內容差異");
        int differenceRowCount = rows.Sum(r => r.DifferenceRows);

        var indexRows = new StringBuilder();
        foreach (var r in rows)
        {
            var statusClass = r.Status == "有差異" ? "changed" : (r.Status == "無內容差異" ? "same" : "notice");
            indexRows.Append($"<tr><td class=\"seq\">{Enc(r.Sequence)}</td><td>{Enc(r.Action)}</td><td>{Enc(r.ObjectType)}</td><td class=\"object\">{Enc(r.ObjectName)}</td><td><span class=\"status {statusClass}\">{Enc(r.Status)}</span></td><td class=\"count\">{r.DifferenceRows}</td><td><a href=\"{Enc(r.ReportFile)}\">檢視</a></td></tr>\r\n");
        }

        var warningHtml = "";
        if (warnings is { Count: > 0 })
        {
            var items = string.Concat(warnings.Select(w => $"<li>{Enc(w)}</li>"));
            warningHtml = $"<h2>警告項目</h2><ul>{items}</ul>";
        }

        return OverviewTemplate
            .Replace("__PROFILE__", Enc(profileName))
            .Replace("__TOTAL__", totalCount.ToString())
            .Replace("__CHANGED__", changedCount.ToString())
            .Replace("__DIFFROWS__", differenceRowCount.ToString())
            .Replace("__STAMP__", stamp)
            .Replace("__INDEXROWS__", indexRows.ToString().TrimEnd())
            .Replace("__WARNINGS__", warningHtml);
    }

    private const string ObjectReportTemplate = """
<!DOCTYPE html>
<html lang="zh-Hant">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>SQL 差異比對｜__OBJNAME__</title>
<style>
:root{--bg:#fffaf2;--surface:#fffdf9;--surface2:#f8f0e5;--line:#e5d8c8;--line2:#d3c0aa;--muted:#6b5848;--text:#35271d;--primary:#92400e;--mod:#fff0d6;--add:#eaf7ed;--del:#fdebec}*{box-sizing:border-box}html{background:var(--bg);color:var(--text)}body{margin:0;font-family:"Segoe UI","Noto Sans TC","Microsoft JhengHei",sans-serif;font-size:16px;line-height:1.6;font-variant-numeric:tabular-nums}.mast{display:grid;grid-template-columns:1fr 220px;border-bottom:1px solid var(--line2);background:var(--surface)}.identity{padding:30px 34px}.eyebrow{margin:0 0 10px;color:var(--primary);font-size:14px;font-weight:700;letter-spacing:.08em}.identity h1{margin:0 0 16px;font-size:clamp(25px,3vw,40px);font-weight:700;line-height:1.25;overflow-wrap:anywhere}.meta{display:flex;flex-wrap:wrap;color:var(--muted);font-size:15px}.meta span{padding-right:16px;margin-right:16px;border-right:1px solid var(--line2)}.meta span:last-child{border:0}.score{display:flex;flex-direction:column;justify-content:space-between;padding:24px;border-left:1px solid var(--line2);background:var(--surface2)}.score strong{color:var(--primary);font-size:68px;line-height:1;font-weight:700}.score span{color:var(--muted);font-size:14px;font-weight:700;letter-spacing:.06em}.toolbar{display:flex;align-items:center;justify-content:space-between;gap:18px;padding:13px 22px;border-bottom:1px solid var(--line2);background:var(--surface)}.legend{display:flex;flex-wrap:wrap;gap:20px;color:var(--muted);font-size:14px}.legend span{display:flex;align-items:center;gap:8px}.swatch{display:inline-block;width:14px;height:14px;border:1px solid var(--line2)}.swatch.mod{border-color:#d69b52;background:var(--mod)}.swatch.add{border-color:#77a982;background:var(--add)}.swatch.del{border-color:#c77b80;background:var(--del)}.direction{color:var(--primary);font-size:14px;font-weight:700}.wrap{width:100%;overflow:auto}.diff{width:100%;min-width:1120px;border-collapse:collapse;table-layout:fixed;background:var(--surface)}.diff col.line{width:58px}.diff col.marker{width:38px}.diff col.source{width:calc((100% - 154px)/2)}thead th{position:sticky;top:0;z-index:2;padding:12px 14px;border-bottom:2px solid var(--primary);background:#5b3a26;color:#fffdf9;font-size:15px;text-align:left}thead th.side-right{text-align:right}.num{padding:7px 10px;border-right:1px solid var(--line);border-bottom:1px solid var(--line);background:var(--surface2);color:#876f5b;font:14px/1.65 Consolas,"Courier New",monospace;text-align:right;vertical-align:top}.code{border-bottom:1px solid var(--line);vertical-align:top}.code pre{min-height:25px;margin:0;padding:7px 11px;white-space:pre-wrap;overflow-wrap:anywhere;color:var(--text);font:14px/1.65 Consolas,"Courier New",monospace}.mark{position:relative;border-right:1px solid var(--line2);border-left:1px solid var(--line2);border-bottom:1px solid var(--line);background:var(--surface2);color:var(--primary);font-size:15px;font-weight:700;text-align:center;vertical-align:middle}.mark:before{content:"";position:absolute;top:0;bottom:0;left:50%;width:1px;background:var(--line2)}.modified .code{background:var(--mod)}.modified .num{color:var(--primary);font-weight:700}.modified .mark{background:#d69b52;color:#fff}.added td.code:nth-child(2){background:var(--add)}.added .mark{background:#568c61;color:#fff}.removed td.code:nth-child(5){background:var(--del)}.removed .mark{background:#ad5960;color:#fff}.modified .mark:before,.added .mark:before,.removed .mark:before{display:none}tbody tr:hover .code,tbody tr:hover .num{outline:2px solid rgba(146,64,14,.14);outline-offset:-2px}@media(max-width:800px){body{font-size:16px}.mast{grid-template-columns:1fr}.score{min-height:125px;border-top:1px solid var(--line2);border-left:0}.score strong{font-size:50px}.identity{padding:22px}.toolbar{align-items:flex-start;flex-direction:column}.code pre{font-size:14px}}
</style>
</head>
<body>
<header class="mast"><section class="identity"><p class="eyebrow">SQL 差異比對</p><h1>__OBJNAME__</h1><div class="meta"><span>__ACTION__</span><span>左側更版／右側原版</span><span>__STAMP__</span></div></section><aside class="score"><strong>__COUNT__</strong><span>差異列數</span></aside></header>
<div class="toolbar"><div class="legend"><span><i class="swatch mod"></i>修改</span><span><i class="swatch add"></i>新增</span><span><i class="swatch del"></i>刪除</span></div><div class="direction">更版 → 原版</div></div>
<div class="wrap"><table class="diff">
<colgroup><col class="line"><col class="source"><col class="marker"><col class="line"><col class="source"></colgroup>
<thead><tr><th colspan="2">更版</th><th></th><th colspan="2" class="side-right">原版</th></tr></thead>
<tbody>
__ROWS__
</tbody>
</table></div>
</body>
</html>
""";

    private const string OverviewTemplate = """
<!DOCTYPE html>
<html lang="zh-Hant"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>SQL 差異比對總覽</title>
<style>:root{--bg:#fffaf2;--surface:#fffdf9;--surface2:#f8f0e5;--line:#e5d8c8;--line2:#d3c0aa;--muted:#6b5848;--text:#35271d;--primary:#92400e;--tint:#fff0d6}*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font-family:"Segoe UI","Noto Sans TC","Microsoft JhengHei",sans-serif;font-size:16px;line-height:1.6;font-variant-numeric:tabular-nums}.mast{display:grid;grid-template-columns:1fr repeat(3,180px);border-bottom:1px solid var(--line2);background:var(--surface)}.title{padding:36px}.title p{margin:0 0 10px;color:var(--primary);font-size:14px;font-weight:700;letter-spacing:.08em}.title h1{margin:0;font-size:clamp(32px,4vw,56px);font-weight:700;line-height:1.15}.metric{display:flex;flex-direction:column;justify-content:space-between;padding:24px;border-left:1px solid var(--line2);background:var(--surface2)}.metric strong{color:var(--primary);font-size:56px;line-height:1;font-weight:700}.metric span{color:var(--muted);font-size:14px;font-weight:700}.sub{display:flex;justify-content:space-between;padding:13px 22px;border-bottom:1px solid var(--line2);background:var(--surface2);color:var(--muted);font-size:14px}.wrap{overflow:auto}table{width:100%;min-width:980px;border-collapse:collapse;background:var(--surface)}th,td{padding:15px 14px;border-right:1px solid var(--line);border-bottom:1px solid var(--line);text-align:left;font-size:15px}th{background:#5b3a26;color:#fffdf9;font-size:14px}tbody tr:hover{background:#fff5e7}.count{color:var(--primary);font-size:20px;font-weight:700}.object{color:#4b3829;font:14px/1.6 Consolas,"Courier New",monospace}a{color:var(--primary);font-weight:700;text-decoration-thickness:2px;text-underline-offset:4px;cursor:pointer}a:focus-visible{outline:3px solid #d69b52;outline-offset:3px}.status{display:inline-block;padding:5px 9px;border:1px solid var(--line2);color:var(--muted);font-size:14px;font-weight:700}.status.changed,.status.notice{border-color:#d69b52;color:var(--primary);background:var(--tint)}h2,ul{margin-right:24px;margin-left:24px}@media(max-width:900px){body{font-size:16px}.mast{grid-template-columns:1fr}.metric{min-height:115px;border-top:1px solid var(--line2);border-left:0}.sub{gap:8px;flex-direction:column}th,td{font-size:15px}}</style>
</head><body><header class="mast"><section class="title"><p>SQL 發版檢核 · __PROFILE__</p><h1>差異比對總覽</h1></section><section class="metric"><strong>__TOTAL__</strong><span>物件</span></section><section class="metric hot"><strong>__CHANGED__</strong><span>有異動</span></section><section class="metric hot"><strong>__DIFFROWS__</strong><span>差異列數</span></section></header><div class="sub"><span>左側更版／右側原版</span><span>產生時間：__STAMP__</span></div>
<div class="wrap"><table><thead><tr><th>順序</th><th>動作</th><th>類型</th><th>物件</th><th>狀態</th><th>差異列數</th><th>報告</th></tr></thead><tbody>__INDEXROWS__</tbody></table></div>
__WARNINGS__
</body></html>
""";
}
