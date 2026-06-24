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

    /// <summary>此項對應的物件是否為「相依自動補入」（非使用者勾選），供 UI／清單標示。</summary>
    public bool IsDependency { get; internal set; }

    /// <summary>本項開始執行（轉為 Running）的時刻（UTC）；尚未開始為 null。供 UI 即時計時。</summary>
    public DateTime? StartedAtUtc { get; internal set; }

    /// <summary>本項從開始到結束（Done/Skipped）的耗時；尚未結束為 null（執行中由 UI 用 StartedAtUtc 即時推算）。</summary>
    public TimeSpan? Elapsed { get; internal set; }
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

        bool doFull = export.DeploySql.FullScript;
        bool doReverse = export.DeploySql.FullRollbackScript;
        bool doPerObject = export.DeploySql.PerObjectScripts;
        bool doHtml = export.HtmlReport.Enabled;

        // 1) 先建立完整計畫清單（所有待產生項目），供 UI 一次列出、逐項勾消。
        var items = new List<ExportItem>();
        var fullItem = doFull ? Add(items, SqlGroup, "完整部署腳本.sql") : null;
        var reverseItem = doReverse ? Add(items, SqlGroup, "完整還原腳本.sql") : null;
        // 標示「相依自動補入」（非使用者勾選）的項目，供清單與報告區分。
        var perObjectItems = doPerObject
            ? included.Select((d, i) =>
            {
                var it = Add(items, PerObjectGroup, $"{(i + 1):D2}　{d.Name}");
                it.IsDependency = !session.WasPicked(d);
                return it;
            }).ToList()
            : new List<ExportItem>();
        var htmlItems = doHtml
            ? included.Select((d, i) =>
            {
                var it = Add(items, HtmlGroup, $"{(i + 1):D2}　{d.Name}");
                it.IsDependency = !session.WasPicked(d);
                return it;
            }).ToList()
            : new List<ExportItem>();
        var overviewItem = doHtml ? Add(items, HtmlGroup, "00_比對總覽.html") : null;

        void Refresh() => report?.Invoke(items);
        void SetState(ExportItem? item, ExportItemState state)
        {
            if (item is null) return;
            // 開始執行：記下起算時刻（UI 據此即時計時）。結束（Done/Skipped）：凍結耗時。
            if (state == ExportItemState.Running)
                item.StartedAtUtc ??= DateTime.UtcNow;
            else if (state is ExportItemState.Done or ExportItemState.Skipped && item.StartedAtUtc is { } s)
                item.Elapsed = DateTime.UtcNow - s;
            item.State = state;
            Refresh();
        }
        Refresh();

        var targetDb = profile.ResolveDeployDatabaseName();

        // 跨執行緒寫入 summary.Warnings 的保護鎖（反向腳本在背景執行緒產生）。
        var summaryLock = new object();

        // 3) 完整還原腳本：最大宗耗時，但用獨立的 SchemaComparison，與正向 _result 互不相干，
        // 故丟背景執行緒、與「逐物件部署檔」並行產生。部署物件名稱須在逐物件改動納入狀態「之前」於主執行緒擷取。
        Task? reverseTask = null;
        if (doReverse)
        {
            var forwardExclusions = session.GetDeployExclusions();
            var deployNames = session.GetDeployObjectNames();
            reverseTask = Task.Run(() =>
            {
                cancel.ThrowIfCancellationRequested();
                SetState(reverseItem, ExportItemState.Running);
                var reverseScript = DeployScriptBuilder.CleanFullScript(
                    session.GenerateReverseScript(forwardExclusions, deployNames), targetDb);
                if (string.IsNullOrWhiteSpace(reverseScript))
                {
                    lock (summaryLock) summary.Warnings.Add("無法產生完整還原腳本（反向比對無結果）。");
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
            }, cancel);
        }

        try
        {
            // 完整部署腳本是「逐物件部署檔」的權威來源（拓樸序、每物件僅一次、與 VS 一致），
            // 故只向 DacFx 產生「一次」，完整檔與逐物件切分共用，省去逐物件各自重產的高成本。
            // session.GenerateScript() 是這裡的主要耗時（~十餘秒）；把它計到「完整部署腳本」這步內，
            // 進度才直覺（否則它在項目開始計時「之前」就跑完，UI 會看到該項先靜止十幾秒再瞬間完成）。
            string? rawFullScript = null;

            // 2) 完整部署腳本
            if (doFull)
            {
                cancel.ThrowIfCancellationRequested();
                SetState(fullItem, ExportItemState.Running);
                rawFullScript = session.GenerateScript();   // 主要耗時，計入本步
                // 清掉 DacFx 的 SQLCMD 樣板（:setvar/:on error/SQLCMD 偵測）與註解標頭，
                // 改套統一 USE [部署庫] 標頭，與逐物件部署檔一致，可直接執行。
                var fullScript = DeployScriptBuilder.CleanFullScript(rawFullScript, targetDb);
                var path = Path.Combine(outputDir, "完整部署腳本.sql");
                WriteBom(path, fullScript);
                summary.FullScriptPath = path;
                SetState(fullItem, ExportItemState.Done);
            }

            // 4) 逐物件部署檔：切分權威完整腳本，一檔一物件、依建立順序。
            if (doPerObject)
            {
                // 未輸出完整檔時才在此補產（少見）；輸出完整檔時直接重用上面那份，不重複產生。
                rawFullScript ??= session.GenerateScript();
                // 不再逐物件向 DacFx 重產（會夾帶前置相依物件、檔案順序與相依拓樸序脫鉤、且每件一次 GenerateScript 極貴）。
                // 改切分上面那份完整腳本：把同一物件散在各階段（本體／索引／約束／描述）的批次聚合回它自己的單檔，
                // 檔案依「物件首次出現」（＝建立順序）編號。詳見 DeployScriptBuilder.SplitFullScriptByObject。
                cancel.ThrowIfCancellationRequested();
                var objectScriptDir = Path.Combine(outputDir, "逐物件部署腳本");
                CleanDirectory(objectScriptDir, "*.sql", "*.csv");   // 清掉上一輪殘留，避免新舊檔混雜

                var split = DeployScriptBuilder.SplitFullScriptByObject(rawFullScript!, targetDb);
                foreach (var w in split.Warnings)
                    lock (summaryLock) summary.Warnings.Add(w);

                // 以正規化名稱把切分檔對映回差異清單（標記 勾選／相依、更新對應進度項）。
                var diffByKey = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < included.Count; i++)
                    diffByKey[DeployScriptBuilder.CanonName(included[i].Name)] = i;

                var rows = new List<string>();
                int failed = 0, produced = 0;
                var matched = new HashSet<int>();

                for (int seq = 0; seq < split.Files.Count; seq++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var sf = split.Files[seq];
                    var seqText = (seq + 1).ToString("D2");

                    string source = "勾選";
                    ExportItem? item = null;
                    if (diffByKey.TryGetValue(sf.OwnerKey, out var di))
                    {
                        item = perObjectItems[di];
                        matched.Add(di);
                        bool picked = session.WasPicked(included[di]);
                        item.IsDependency = !picked;
                        source = picked ? "勾選" : "相依";
                    }
                    SetState(item, ExportItemState.Running);

                    var fileName = $"{seqText}_{sf.FileName}";
                    WriteBom(Path.Combine(objectScriptDir, fileName), sf.Content);

                    if (!sf.VerificationPassed)
                    {
                        failed++;
                        if (item is not null) item.Warned = true;
                        lock (summaryLock) summary.Warnings.Add($"{fileName} 驗證未過：{sf.VerificationMessage}");
                    }

                    rows.Add($"{seqText},{sf.Action},{sf.ObjectType}," +
                             $"\"{sf.ObjectName.Replace("\"", "\"\"")}\",{sf.OperationBatchCount}," +
                             $"{(sf.VerificationPassed ? "OK" : "FAIL")},{source},{fileName}");
                    produced++;
                    SetState(item, ExportItemState.Done);
                }

                // 差異清單中沒對應到任何切分檔的物件（理論上不該發生：完整腳本應含所有納入物件）。
                for (int i = 0; i < included.Count; i++)
                {
                    if (matched.Contains(i)) continue;
                    perObjectItems[i].Warned = true;
                    SetState(perObjectItems[i], ExportItemState.Skipped);
                    lock (summaryLock)
                        summary.Warnings.Add($"物件 {included[i].Name} 未出現在完整部署腳本中，無對應逐物件檔。");
                }

                var csv = new StringBuilder();
                csv.AppendLine("Sequence,Action,ObjectType,ObjectName,OperationBatches,Verified,Source,FileName");
                foreach (var r in rows) csv.AppendLine(r);
                File.WriteAllText(Path.Combine(objectScriptDir, "00_部署清單.csv"), csv.ToString(), Utf8Bom);

                summary.ObjectScriptCount = produced;
                summary.ObjectScriptVerificationPassed = failed == 0;
                if (failed > 0)
                    summary.ObjectScriptVerificationMessage = $"{failed} 個物件檔驗證未過。";
            }
        }
        finally
        {
            // 等背景反向腳本收束（正常路徑通常已與逐物件並行跑完；取消/錯誤路徑也須在此觀察其結果，
            // 避免遺留游離工作）。放在 HTML 之前，使總覽能含到反向產生的警告，且後續 HTML 已無並行寫入。
            if (reverseTask is not null)
            {
                try { reverseTask.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { /* 使用者中斷，由主流程處理 */ }
            }
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
                    export.HtmlReport.IgnoreWhitespace, generatedAt);

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
                    IsDependency = !session.WasPicked(d),
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
