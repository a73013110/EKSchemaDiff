using System.Text;
using EKSchemaDiff.Core.Compare;
using EKSchemaDiff.Core.Config;
using EKSchemaDiff.Core.Scripting;
using EKSchemaDiff.Report;

namespace EKSchemaDiff.Core.Export;

public sealed class ExportSummary
{
    public string OutputDir { get; init; } = "";
    public string? FullScriptPath { get; set; }
    public string? ReverseScriptPath { get; set; }
    public int ObjectScriptCount { get; set; }
    public int HtmlReportCount { get; set; }
    public bool ObjectScriptVerificationPassed { get; set; } = true;
    public string? ObjectScriptVerificationMessage { get; set; }
    public bool Cancelled { get; set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>匯出計畫中一個待產生的項目目前的狀態。</summary>
public enum ExportItemState { Pending, Running, Done, Skipped }

/// <summary>匯出計畫中的一個項目：所屬群組、顯示標籤、目前狀態、是否完成但帶警告。</summary>
public sealed class ExportItem
{
    public ExportItem(string group, string label) { Group = group; Label = label; }

    /// <summary>群組標題（部署 SQL／逐物件部署檔／差異報告）。</summary>
    public string Group { get; }

    /// <summary>顯示標籤（檔名或「序號　物件名」）。</summary>
    public string Label { get; }

    public ExportItemState State { get; internal set; } = ExportItemState.Pending;

    /// <summary>已完成但有警告（如逐物件驗證未過），供 UI 以警示色呈現。</summary>
    public bool Warned { get; internal set; }
}

/// <summary>
/// 將比對結果落地：產生部署 SQL（完整／還原／逐物件）與暖色差異 HTML。
/// 進度以「預先列出所有待產生項目、再逐項翻轉狀態」回報，呼叫端可據此畫出可勾消的清單。
/// 輸出方向：HTML 左側=更版(來源)、右側=原版(目標)。
/// </summary>
public static class Exporter
{
    private const string SqlGroup = "部署 SQL";
    private const string PerObjectGroup = "逐物件部署檔";
    private const string HtmlGroup = "差異報告";

    private static readonly UTF8Encoding Utf8Bom = new(true);

    private static void WriteBom(string path, string text) =>
        File.WriteAllText(path, text, Utf8Bom);

    /// <summary>建立目錄；若已存在，刪除符合樣式的舊檔（避免新舊輸出混雜）。</summary>
    private static void CleanDirectory(string dir, params string[] patterns)
    {
        Directory.CreateDirectory(dir);
        foreach (var pattern in patterns)
            foreach (var file in Directory.EnumerateFiles(dir, pattern))
            {
                try { File.Delete(file); } catch { /* 檔案被佔用時略過 */ }
            }
    }

    public static ExportSummary Export(
        CompareSession session, string outputDir, DateTime generatedAt,
        Action<IReadOnlyList<ExportItem>>? report = null, CancellationToken cancel = default)
    {
        var profile = session.Profile;
        var export = profile.ExportOptions;
        Directory.CreateDirectory(outputDir);

        var summary = new ExportSummary { OutputDir = outputDir };
        foreach (var u in session.UnrecognizedExcludedTypes)
            summary.Warnings.Add($"設定的排除類型無法辨識，已忽略：{u}");

        var included = session.Differences.Where(d => d.Included).ToList();

        bool doFull = export.FullScript;
        bool doReverse = export.FullRollbackScript;
        bool doPerObject = export.PerObjectScripts;
        bool doHtml = export.ExportHtml;

        // 1) 先建立完整計畫清單（所有待產生項目），供 UI 一次列出、逐項勾消。
        var items = new List<ExportItem>();
        var fullItem = doFull ? Add(items, SqlGroup, "完整部署腳本.sql") : null;
        var reverseItem = doReverse ? Add(items, SqlGroup, "完整還原腳本.sql") : null;
        var perObjectItems = doPerObject
            ? included.Select((d, i) => Add(items, PerObjectGroup, $"{(i + 1):D2}　{d.Name}")).ToList()
            : new List<ExportItem>();
        var htmlItems = doHtml
            ? included.Select((d, i) => Add(items, HtmlGroup, $"{(i + 1):D2}　{d.Name}")).ToList()
            : new List<ExportItem>();
        var overviewItem = doHtml ? Add(items, HtmlGroup, "00_比對總覽.html") : null;

        void Refresh() => report?.Invoke(items);
        void SetState(ExportItem? item, ExportItemState state)
        {
            if (item is null) return;
            item.State = state;
            Refresh();
        }
        Refresh();

        var targetDb = profile.ResolveDeployDatabaseName();

        // 2) 完整部署腳本
        if (doFull)
        {
            cancel.ThrowIfCancellationRequested();
            SetState(fullItem, ExportItemState.Running);
            // 清掉 DacFx 的 SQLCMD 樣板（:setvar/:on error/SQLCMD 偵測）與註解標頭，
            // 改套統一 USE [部署庫] 標頭，與逐物件部署檔一致，可直接執行。
            var fullScript = DeployScriptBuilder.CleanFullScript(session.GenerateScript(), targetDb);
            var path = Path.Combine(outputDir, "完整部署腳本.sql");
            WriteBom(path, fullScript);
            summary.FullScriptPath = path;
            SetState(fullItem, ExportItemState.Done);
        }

        // 3) 完整還原腳本（完整部署腳本的反向，供回版/異常還原）
        if (doReverse)
        {
            cancel.ThrowIfCancellationRequested();
            SetState(reverseItem, ExportItemState.Running);
            var reverseScript = DeployScriptBuilder.CleanFullScript(session.GenerateReverseScript(), targetDb);
            if (string.IsNullOrWhiteSpace(reverseScript))
            {
                summary.Warnings.Add("無法產生完整還原腳本（反向比對無結果）。");
                if (reverseItem is not null) reverseItem.Warned = true;
                SetState(reverseItem, ExportItemState.Skipped);
            }
            else
            {
                var path = Path.Combine(outputDir, "完整還原腳本.sql");
                WriteBom(path, reverseScript);
                summary.ReverseScriptPath = path;
                SetState(reverseItem, ExportItemState.Done);
            }
        }

        // 4) 逐物件部署檔
        if (doPerObject)
        {
            // 逐物件：對每個納入的物件，由 DacFx 官方引擎單獨產生腳本（含其描述、相依刷新…由引擎決定），
            // 再整理成乾淨單檔。等同 VS「結構描述比較」逐物件勾選匯出，沒有跨物件歸屬猜測。
            var objectScriptDir = Path.Combine(outputDir, "逐物件部署腳本");
            CleanDirectory(objectScriptDir, "*.sql", "*.csv");   // 清掉上一輪殘留，避免新舊檔混雜

            var csv = new StringBuilder();
            csv.AppendLine("Sequence,Action,ObjectType,ObjectName,OperationBatches,Verified,FileName");
            int failed = 0;
            for (int i = 0; i < included.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var diff = included[i];
                var item = perObjectItems[i];
                var seqText = (i + 1).ToString("D2");
                SetState(item, ExportItemState.Running);

                var script = session.GenerateObjectScript(diff);
                if (string.IsNullOrWhiteSpace(script))
                {
                    summary.Warnings.Add($"物件 {diff.Name} 無法產生部署腳本，已略過。");
                    item.Warned = true;
                    SetState(item, ExportItemState.Skipped);
                    continue;
                }

                var f = DeployScriptBuilder.BuildObjectFile(script, targetDb, diff.Name);
                var fileName = $"{seqText}_{f.FileName}";
                WriteBom(Path.Combine(objectScriptDir, fileName), f.Content);

                if (!f.VerificationPassed)
                {
                    failed++;
                    item.Warned = true;
                    summary.Warnings.Add($"{fileName} 驗證未過：{f.VerificationMessage}");
                }

                csv.AppendLine($"{seqText},{f.Action},{f.ObjectType}," +
                               $"\"{f.ObjectName.Replace("\"", "\"\"")}\",{f.OperationBatchCount}," +
                               $"{(f.VerificationPassed ? "OK" : "FAIL")},{fileName}");
                summary.ObjectScriptCount++;
                SetState(item, ExportItemState.Done);
            }
            session.RestoreInclusion(included);
            File.WriteAllText(Path.Combine(objectScriptDir, "00_部署清單.csv"), csv.ToString(), Utf8Bom);

            summary.ObjectScriptVerificationPassed = failed == 0;
            if (failed > 0)
                summary.ObjectScriptVerificationMessage = $"{failed} 個物件檔驗證未過。";
        }

        // 5) 差異 HTML
        if (doHtml)
        {
            var htmlDir = Path.Combine(outputDir, "差異報告");
            CleanDirectory(htmlDir, "*.html");   // 清掉上一輪殘留

            var indexRows = new List<ReportIndexRow>();
            for (int i = 0; i < included.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var d = included[i];
                var item = htmlItems[i];
                SetState(item, ExportItemState.Running);

                var action = d.Kind switch
                {
                    ChangeKind.Add => "新增",
                    ChangeKind.Change => "變更",
                    ChangeKind.Delete => "刪除",
                    _ => "其他",
                };
                var seqText = (i + 1).ToString("D2");
                var (html, diffCount) = HtmlReportBuilder.BuildObjectReport(
                    d.Name, action, d.SourceScript, d.TargetScript,
                    export.HtmlIgnoreWhitespace, generatedAt);

                var safe = DeployScriptBuilder.ConvertToSafeName(d.Name);
                var fileName = $"{seqText}_{safe}.html";
                WriteBom(Path.Combine(htmlDir, fileName), html);

                var status = d.Kind switch
                {
                    ChangeKind.Add => "新增物件",
                    ChangeKind.Delete => "刪除物件",
                    _ => diffCount == 0 ? "無內容差異" : "有差異",
                };
                indexRows.Add(new ReportIndexRow
                {
                    Sequence = seqText,
                    Action = action,
                    ObjectType = d.ObjectTypeName,
                    ObjectName = d.Name,
                    Status = status,
                    DifferenceRows = diffCount,
                    ReportFile = fileName,
                });
                SetState(item, ExportItemState.Done);
            }

            cancel.ThrowIfCancellationRequested();
            SetState(overviewItem, ExportItemState.Running);
            var overview = HtmlReportBuilder.BuildOverview(
                indexRows, profile.Name, generatedAt, summary.Warnings);
            WriteBom(Path.Combine(htmlDir, "00_比對總覽.html"), overview);
            summary.HtmlReportCount = indexRows.Count;
            SetState(overviewItem, ExportItemState.Done);
        }

        return summary;
    }

    private static ExportItem Add(List<ExportItem> items, string group, string label)
    {
        var item = new ExportItem(group, label);
        items.Add(item);
        return item;
    }
}
