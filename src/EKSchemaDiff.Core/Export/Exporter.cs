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
    public int ObjectScriptCount { get; set; }
    public int HtmlReportCount { get; set; }
    public bool ObjectScriptVerificationPassed { get; set; } = true;
    public string? ObjectScriptVerificationMessage { get; set; }
    public bool Cancelled { get; set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>匯出進度回報：階段名、目前處理的項目、已完成數、總數。</summary>
public readonly record struct ExportProgress(string Phase, string Item, int Current, int Total);

/// <summary>
/// 將比對結果落地：產生部署 SQL（完整／逐物件／兩者）與暖色差異 HTML。
/// 輸出方向：HTML 左側=更版(來源)、右側=原版(目標)。
/// </summary>
public static class Exporter
{
    private static readonly UTF8Encoding Utf8Bom = new(true);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

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
        Action<ExportProgress>? report = null, CancellationToken cancel = default)
    {
        var profile = session.Profile;
        var export = profile.ExportOptions;
        Directory.CreateDirectory(outputDir);

        var summary = new ExportSummary { OutputDir = outputDir };
        foreach (var u in session.UnrecognizedExcludedTypes)
            summary.Warnings.Add($"設定的排除類型無法辨識，已忽略：{u}");

        var included = session.Differences.Where(d => d.Included).ToList();

        bool doFull = export.DeployScript is DeployScriptMode.Single or DeployScriptMode.Both;
        bool doPerObject = export.DeployScript is DeployScriptMode.PerObject or DeployScriptMode.Both;
        int total = (doFull ? 1 : 0) + (doPerObject ? included.Count : 0) + (export.ExportHtml ? included.Count : 0);
        int done = 0;
        void Report(string phase, string item) => report?.Invoke(new ExportProgress(phase, item, done, total));

        Report("準備中", "");

        // 1) 部署 SQL
        var targetDb = profile.ResolveDeployDatabaseName();

        if (doFull)
        {
            cancel.ThrowIfCancellationRequested();
            Report("產生完整部署腳本", "完整部署腳本.sql");
            // 清掉 DacFx 的 SQLCMD 樣板（:setvar/:on error/SQLCMD 偵測）與註解標頭，
            // 改套統一 USE [部署庫] 標頭，與逐物件部署檔一致，可直接執行。
            var fullScript = DeployScriptBuilder.CleanFullScript(session.GenerateScript(), targetDb);
            var path = Path.Combine(outputDir, "完整部署腳本.sql");
            WriteBom(path, fullScript);
            summary.FullScriptPath = path;
            done++;
            Report("產生完整部署腳本", "完整部署腳本.sql");
        }

        if (doPerObject)
        {
            // 逐物件：對每個納入的物件，由 DacFx 官方引擎單獨產生腳本（含其描述、相依刷新…由引擎決定），
            // 再整理成乾淨單檔。等同 VS「結構描述比較」逐物件勾選匯出，沒有跨物件歸屬猜測。
            var objectScriptDir = Path.Combine(outputDir, "逐物件部署腳本");
            CleanDirectory(objectScriptDir, "*.sql", "*.csv");   // 清掉上一輪殘留，避免新舊檔混雜

            var csv = new StringBuilder();
            csv.AppendLine("Sequence,Action,ObjectType,ObjectName,OperationBatches,Verified,FileName");
            int seq = 1;
            int failed = 0;
            foreach (var diff in included)
            {
                cancel.ThrowIfCancellationRequested();
                var seqText = seq.ToString("D2");
                Report("產生逐物件部署檔", $"{seqText}　{diff.Name}");

                var script = session.GenerateObjectScript(diff);
                if (string.IsNullOrWhiteSpace(script))
                {
                    summary.Warnings.Add($"物件 {diff.Name} 無法產生部署腳本，已略過。");
                    seq++; done++;
                    continue;
                }

                var f = DeployScriptBuilder.BuildObjectFile(script, targetDb, diff.Name);
                var fileName = $"{seqText}_{f.FileName}";
                WriteBom(Path.Combine(objectScriptDir, fileName), f.Content);

                if (!f.VerificationPassed)
                {
                    failed++;
                    summary.Warnings.Add($"{fileName} 驗證未過：{f.VerificationMessage}");
                }

                csv.AppendLine($"{seqText},{f.Action},{f.ObjectType}," +
                               $"\"{f.ObjectName.Replace("\"", "\"\"")}\",{f.OperationBatchCount}," +
                               $"{(f.VerificationPassed ? "OK" : "FAIL")},{fileName}");
                summary.ObjectScriptCount++;
                seq++; done++;
                Report("產生逐物件部署檔", $"{seqText}　{diff.Name}");
            }
            session.RestoreInclusion(included);
            File.WriteAllText(Path.Combine(objectScriptDir, "00_部署清單.csv"), csv.ToString(), Utf8Bom);

            summary.ObjectScriptVerificationPassed = failed == 0;
            if (failed > 0)
                summary.ObjectScriptVerificationMessage = $"{failed} 個物件檔驗證未過。";
        }

        // 2) 差異 HTML
        if (export.ExportHtml)
        {
            var htmlDir = Path.Combine(outputDir, "差異報告");
            CleanDirectory(htmlDir, "*.html");   // 清掉上一輪殘留

            var indexRows = new List<ReportIndexRow>();
            int seq = 1;
            foreach (var d in included)
            {
                cancel.ThrowIfCancellationRequested();
                var action = d.Kind switch
                {
                    ChangeKind.Add => "新增",
                    ChangeKind.Change => "變更",
                    ChangeKind.Delete => "刪除",
                    _ => "其他",
                };
                var seqText = seq.ToString("D2");
                Report("產生差異報告 HTML", $"{seqText}　{d.Name}");
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
                seq++; done++;
                Report("產生差異報告 HTML", $"{seqText}　{d.Name}");
            }

            var overview = HtmlReportBuilder.BuildOverview(
                indexRows, profile.Name, generatedAt, summary.Warnings);
            WriteBom(Path.Combine(htmlDir, "00_比對總覽.html"), overview);
            summary.HtmlReportCount = indexRows.Count;
        }

        return summary;
    }
}
