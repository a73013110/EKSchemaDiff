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
    /// <summary>逐物件平行產生時的 worker 數（每 worker 開一個獨立比對、重抽模型）。差異多時才會啟用平行。</summary>
    private const int PerObjectWorkerCount = 3;

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
        // 故丟背景執行緒、與「逐物件部署檔」並行產生。排除清單必須在逐物件改動納入狀態「之前」擷取。
        Task? reverseTask = null;
        if (doReverse)
        {
            var reverseExclusions = session.GetDeployExclusions();
            reverseTask = Task.Run(() =>
            {
                cancel.ThrowIfCancellationRequested();
                SetState(reverseItem, ExportItemState.Running);
                var reverseScript = DeployScriptBuilder.CleanFullScript(
                    session.GenerateReverseScript(reverseExclusions), targetDb);
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

            // 4) 逐物件部署檔（可平行）
            if (doPerObject)
            {
                // 逐物件：對每個納入的物件，由 DacFx 官方引擎單獨產生腳本（含其描述、相依刷新…由引擎決定），
                // 再整理成乾淨單檔。等同 VS「結構描述比較」逐物件勾選匯出，沒有跨物件歸屬猜測。
                // 每個物件一次 GenerateScript 是硬成本，故差異多時切成數個 worker（各持「獨立、僅含自己物件」的
                // 正向比對）並行產生；少量時維持單緒（省去多開比對重抽模型的成本）。
                var objectScriptDir = Path.Combine(outputDir, "逐物件部署腳本");
                CleanDirectory(objectScriptDir, "*.sql", "*.csv");   // 清掉上一輪殘留，避免新舊檔混雜

                var indexOf = new Dictionary<ObjectDifference, int>();
                for (int i = 0; i < included.Count; i++) indexOf[included[i]] = i;

                var rows = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
                int failed = 0;
                int produced = 0;

                // 以某工作階段 + 名稱對照產生單一物件的腳本並落地。共用狀態（rows/計數/Warnings）均已保護，
                // 可被多 worker 並行呼叫。
                void ProcessOne(CompareSession s, IReadOnlyDictionary<string, ObjectDifference> map, ObjectDifference origDiff)
                {
                    int i = indexOf[origDiff];
                    var item = perObjectItems[i];
                    var seqText = (i + 1).ToString("D2");
                    SetState(item, ExportItemState.Running);

                    var script = map.TryGetValue(origDiff.Name, out var wd) ? s.GenerateObjectScript(wd) : string.Empty;
                    if (string.IsNullOrWhiteSpace(script))
                    {
                        lock (summaryLock) summary.Warnings.Add($"物件 {origDiff.Name} 無法產生部署腳本，已略過。");
                        item.Warned = true;
                        SetState(item, ExportItemState.Skipped);
                        return;
                    }

                    var f = DeployScriptBuilder.BuildObjectFile(script, targetDb, origDiff.Name);
                    var fileName = $"{seqText}_{f.FileName}";
                    WriteBom(Path.Combine(objectScriptDir, fileName), f.Content);

                    if (!f.VerificationPassed)
                    {
                        System.Threading.Interlocked.Increment(ref failed);
                        item.Warned = true;
                        lock (summaryLock) summary.Warnings.Add($"{fileName} 驗證未過：{f.VerificationMessage}");
                    }

                    var source = session.WasPicked(origDiff) ? "勾選" : "相依";
                    rows[i] = $"{seqText},{f.Action},{f.ObjectType}," +
                              $"\"{f.ObjectName.Replace("\"", "\"\"")}\",{f.OperationBatchCount}," +
                              $"{(f.VerificationPassed ? "OK" : "FAIL")},{source},{fileName}";
                    System.Threading.Interlocked.Increment(ref produced);
                    SetState(item, ExportItemState.Done);
                }

                static IReadOnlyDictionary<string, ObjectDifference> MapByName(IEnumerable<ObjectDifference> diffs)
                {
                    var map = new Dictionary<string, ObjectDifference>(StringComparer.OrdinalIgnoreCase);
                    foreach (var d in diffs) map[d.Name] = d;   // 同名極罕見，後者覆蓋即可
                    return map;
                }

                int workerCount = Math.Clamp(included.Count, 1, PerObjectWorkerCount);
                if (included.Count >= 2 * workerCount)
                {
                    // 平行（動態派工）：固定開 workerCount 個 worker（各一條獨立連線、一個 scoped 比對，
                    // 只含已納入物件），再從共享佇列「誰閒誰取」逐件處理 —— 避免靜態切份時「拿到大物件的
                    // worker 累死、其他閒置」（部分樞紐物件因相依刷新，單件 GenerateScript 可達一分鐘，落點不均
                    // 影響極大）。固定 worker 數也讓 DB 連線數可預期。排除清單沿用部署排除（未納入物件）。
                    var exclusions = session.GetDeployExclusions();
                    var queue = new System.Collections.Concurrent.ConcurrentQueue<ObjectDifference>(included);

                    var workers = new Task[workerCount];
                    for (int w = 0; w < workerCount; w++)
                    {
                        workers[w] = Task.Run(() =>
                        {
                            var worker = session.CreateScopedForwardSession(exclusions);
                            var map = MapByName(worker.Differences);
                            while (queue.TryDequeue(out var origDiff))
                            {
                                cancel.ThrowIfCancellationRequested();
                                ProcessOne(worker, map, origDiff);
                            }
                        }, cancel);
                    }

                    try { Task.WaitAll(workers); }
                    catch (AggregateException ae)
                    {
                        // 取消時各 worker 拋 OCE → 統一還原為 OperationCanceledException 交主流程處理；
                        // 否則為真錯誤，重拋（單一則直接拋原例外）。
                        cancel.ThrowIfCancellationRequested();
                        throw ae.InnerExceptions.Count == 1 ? ae.InnerExceptions[0] : ae;
                    }
                }
                else
                {
                    // 少量：直接用主比對結果逐一產生，完成後還原納入狀態（GenerateObjectScript 會改動主結果）。
                    var map = MapByName(session.Differences);
                    foreach (var origDiff in included)
                    {
                        cancel.ThrowIfCancellationRequested();
                        ProcessOne(session, map, origDiff);
                    }
                    session.RestoreInclusion(included);
                }

                var csv = new StringBuilder();
                csv.AppendLine("Sequence,Action,ObjectType,ObjectName,OperationBatches,Verified,Source,FileName");
                foreach (var i in rows.Keys.OrderBy(k => k)) csv.AppendLine(rows[i]);
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
