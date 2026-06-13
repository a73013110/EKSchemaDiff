using System.Text;
using EKSchemaDiff.Core.Compare;
using EKSchemaDiff.Core.Config;
using EKSchemaDiff.Core.Splitting;
using EKSchemaDiff.Report;

namespace EKSchemaDiff.Core.Export;

public sealed class ExportSummary
{
    public string OutputDir { get; init; } = "";
    public string? FullScriptPath { get; set; }
    public int SplitFileCount { get; set; }
    public int HtmlReportCount { get; set; }
    public bool SplitVerificationPassed { get; set; } = true;
    public string? SplitVerificationMessage { get; set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// 將比對結果落地：產生部署 SQL（單一/切分/兩者）與暖色差異 HTML。
/// 輸出方向：HTML 左側=更版(來源)、右側=原版(目標)。
/// </summary>
public static class Exporter
{
    private static readonly UTF8Encoding Utf8Bom = new(true);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static void WriteBom(string path, string text) =>
        File.WriteAllText(path, text, Utf8Bom);

    public static ExportSummary Export(
        CompareSession session, string outputDir, DateTime generatedAt)
    {
        var profile = session.Profile;
        var export = profile.ExportOptions;
        Directory.CreateDirectory(outputDir);

        var summary = new ExportSummary { OutputDir = outputDir };
        foreach (var u in session.UnrecognizedExcludedTypes)
            summary.Warnings.Add($"設定的排除類型無法辨識，已忽略：{u}");

        var included = session.Differences.Where(d => d.Included).ToList();

        // 1) 部署 SQL
        var fullScript = session.GenerateScript();
        var targetDb = profile.ResolveDeployDatabaseName();

        if (export.DeployScript is DeployScriptMode.Single or DeployScriptMode.Both)
        {
            var path = Path.Combine(outputDir, "FullScript.sql");
            WriteBom(path, fullScript);
            summary.FullScriptPath = path;
        }

        SplitResult? split = null;
        if (export.DeployScript is DeployScriptMode.SplitOrdered or DeployScriptMode.Both)
        {
            split = ScriptSplitter.Split(fullScript, targetDb, profile.ExportOptions.GroupSplitByObject);
            var splitDir = Path.Combine(outputDir, "切分SQL");
            Directory.CreateDirectory(splitDir);
            foreach (var f in split.Files)
                WriteBom(Path.Combine(splitDir, f.FileName), f.Content);

            // 拆分摘要
            var csv = new StringBuilder();
            csv.AppendLine("Sequence,Action,ObjectType,ObjectName,FileName");
            foreach (var f in split.Files)
                csv.AppendLine($"{f.Sequence},{f.Action},{f.ObjectType},\"{f.ObjectName.Replace("\"", "\"\"")}\",{f.FileName}");
            File.WriteAllText(Path.Combine(splitDir, "00_切分摘要.csv"), csv.ToString(), Utf8Bom);

            summary.SplitFileCount = split.Files.Count;
            summary.SplitVerificationPassed = split.VerificationPassed;
            summary.SplitVerificationMessage = split.VerificationMessage;
            if (!split.VerificationPassed)
                summary.Warnings.Add($"切分嚴格驗證未通過：{split.VerificationMessage}");
        }

        // 2) 差異 HTML
        if (export.ExportHtml)
        {
            var htmlDir = Path.Combine(outputDir, "差異報告");
            Directory.CreateDirectory(htmlDir);

            var indexRows = new List<ReportIndexRow>();
            int seq = 1;
            foreach (var d in included)
            {
                var action = d.UpdateAction switch
                {
                    ChangeKind.Add => "新增",
                    ChangeKind.Change => "變更",
                    ChangeKind.Delete => "刪除",
                    _ => "其他",
                };
                var seqText = seq.ToString("D2");
                var (html, diffCount) = HtmlReportBuilder.BuildObjectReport(
                    d.Name, action, d.SourceScript, d.TargetScript,
                    export.HtmlIgnoreWhitespace, generatedAt);

                var safe = ScriptSplitter.ConvertToSafeName(d.Name);
                var fileName = $"{seqText}_{safe}.html";
                WriteBom(Path.Combine(htmlDir, fileName), html);

                var status = d.UpdateAction switch
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
                seq++;
            }

            var overview = HtmlReportBuilder.BuildOverview(
                indexRows, profile.Name, generatedAt, summary.Warnings);
            WriteBom(Path.Combine(htmlDir, "00_差異比對總覽.html"), overview);
            summary.HtmlReportCount = indexRows.Count;
        }

        return summary;
    }
}
