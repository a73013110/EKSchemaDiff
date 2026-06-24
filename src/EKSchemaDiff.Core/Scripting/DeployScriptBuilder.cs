using System.Text.RegularExpressions;

namespace EKSchemaDiff.Core.Scripting;

/// <summary>單一物件的部署檔：由 DacFx 逐物件腳本清理而來。</summary>
public sealed class ObjectScriptFile
{
    public string Action { get; init; } = "";
    public string ObjectType { get; init; } = "";
    public string ObjectName { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Content { get; init; } = "";
    public int OperationBatchCount { get; init; }
    /// <summary>所屬物件的正規化名稱（schema.object，小寫無括號），供呼叫端與差異清單對映。</summary>
    public string OwnerKey { get; init; } = "";
    /// <summary>清理後的操作批次是否與來源（DacFx 原始腳本）一致（內容不增不減不改）。</summary>
    public bool VerificationPassed { get; init; }
    public string? VerificationMessage { get; init; }
}

/// <summary>把完整部署腳本依物件切分的結果：一檔一物件（依建立順序），加上需提醒使用者的警告。</summary>
public sealed class SplitResult
{
    public IReadOnlyList<ObjectScriptFile> Files { get; init; } = Array.Empty<ObjectScriptFile>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 把 DacFx「單一物件」的 GenerateScript() 輸出整理成乾淨、可直接執行的單檔：
/// 去除 SQLCMD 部署樣板（:setvar/:on error/SQLCMD 偵測）、PRINT 進度與原 USE，
/// 改套統一的 USE [部署庫] + 必要 SET 標頭，其餘操作批次（DDL、描述、相依刷新…由 DacFx 決定）原樣保留。
/// 每個檔「以單一物件為主」，但 DacFx 可能一併帶入該物件所依賴、且同樣有變更的前置物件
/// （例如函式依賴的檢視），以確保此檔可獨立執行；這些都由官方引擎決定，沒有我們自己猜測歸屬的風險。
/// </summary>
public static partial class DeployScriptBuilder
{
    private const string CrLf = "\r\n";

    [GeneratedRegex(@"(?im)^\s*GO\s*(?:--[^\r\n]*)?$")]
    private static partial Regex GoSplit();

    [GeneratedRegex(@"(?im)^\s*SET\s+(ANSI_NULLS|ANSI_PADDING|ANSI_WARNINGS|ARITHABORT|CONCAT_NULL_YIELDS_NULL|QUOTED_IDENTIFIER|NUMERIC_ROUNDABORT)\b")]
    private static partial Regex SessionSetup();

    [GeneratedRegex(@"(?im)^\s*USE\s+")]
    private static partial Regex UseBatch();

    [GeneratedRegex(@"(?im)^\s*:(setvar|on\s+error|connect)\b")]
    private static partial Regex SqlCmdDirective();

    [GeneratedRegex(@"\$\([^)]+\)")]
    private static partial Regex SqlCmdVar();

    [GeneratedRegex(@"(?im)^\s*PRINT\b")]
    private static partial Regex PrintStart();

    [GeneratedRegex(@"(?im)\b(CREATE|ALTER|DROP|EXEC(?:UTE)?|MERGE|INSERT|UPDATE|DELETE|GRANT|DENY|REVOKE|sp_\w+)\b")]
    private static partial Regex OperationKeyword();

    [GeneratedRegex(@"(?im)^\s*(?<Action>CREATE\s+OR\s+ALTER|CREATE|ALTER|DROP)\s+(?<Type>PROCEDURE|PROC|VIEW|FUNCTION|TABLE|TRIGGER|SYNONYM|SEQUENCE|TYPE|SCHEMA|INDEX)\s+(?<Name>(?:\[[^\]\r\n]+\]|[A-Za-z0-9_#$@]+)(?:\s*\.\s*(?:\[[^\]\r\n]+\]|[A-Za-z0-9_#$@]+))?)")]
    private static partial Regex DdlMatch();

    [GeneratedRegex(@"(?im)sp_(?<Op>add|update|drop)extendedproperty\b")]
    private static partial Regex ExtPropOp();

    [GeneratedRegex(@"(?im)@level0name\s*=\s*N?'(?<v>[^']+)'")]
    private static partial Regex Level0Name();

    [GeneratedRegex(@"(?im)@level1name\s*=\s*N?'(?<v>[^']+)'")]
    private static partial Regex Level1Name();

    [GeneratedRegex(@"(?im)@level2name\s*=\s*N?'(?<v>[^']+)'")]
    private static partial Regex Level2Name();

    // 索引的「擁有者」是 ON 子句的資料表，不是索引名本身（故須先於一般 DDL 比對）。
    [GeneratedRegex(@"(?i)^\s*CREATE\s+(?:UNIQUE\s+)?(?:(?:NON)?CLUSTERED\s+)?INDEX\s+[\s\S]+?\bON\s+(?<tbl>(?:\[[^\]\r\n]+\]|[A-Za-z0-9_#$@]+)(?:\s*\.\s*(?:\[[^\]\r\n]+\]|[A-Za-z0-9_#$@]+))?)")]
    private static partial Regex IndexOnTable();

    // 外鍵指向的資料表（用於偵測跨表前向參照）。
    [GeneratedRegex(@"(?i)\bREFERENCES\s+(?<tbl>(?:\[[^\]\r\n]+\]|[A-Za-z0-9_#$@]+)(?:\s*\.\s*(?:\[[^\]\r\n]+\]|[A-Za-z0-9_#$@]+))?)")]
    private static partial Regex FkReferences();

    private static string NormalizeCrLf(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", CrLf);

    public static string ConvertToSafeName(string name)
    {
        var safe = name.Trim();
        safe = Regex.Replace(safe, @"^\[|\]$", "");
        safe = Regex.Replace(safe, @"\]\s*\.\s*\[", "_");
        safe = Regex.Replace(safe, @"[\\/:*?""<>|.]", "_");
        safe = Regex.Replace(safe, @"\s+", "_");
        safe = safe.Trim('_');
        if (string.IsNullOrWhiteSpace(safe)) return "UNKNOWN_OBJECT";
        return safe.Length > 150 ? safe[..150] : safe;
    }

    private static List<string> Batches(string text) =>
        GoSplit().Split(NormalizeCrLf(text))
            .Select(b => b.Trim())
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .ToList();

    /// <summary>
    /// 把 DacFx 單一物件腳本整理成一個乾淨單檔。
    /// databaseName＝USE 標頭要用的部署資料庫名；fallbackObjectName＝取不到 DDL 名稱時的物件名（用差異物件名）。
    /// </summary>
    public static ObjectScriptFile BuildObjectFile(string objectScript, string databaseName, string fallbackObjectName)
    {
        var (sessionSetup, operationBatches) = PartitionBatches(objectScript);
        var content = Compose(databaseName, sessionSetup, operationBatches);

        var (action, type, name) = DetectMetadata(operationBatches, fallbackObjectName);
        var fileName = $"{action}_{type}_{ConvertToSafeName(name)}.sql";

        var (verified, message) = Verify(content, sessionSetup, operationBatches, databaseName);

        return new ObjectScriptFile
        {
            Action = action,
            ObjectType = type,
            ObjectName = name,
            FileName = fileName,
            Content = content,
            OperationBatchCount = operationBatches.Count,
            OwnerKey = CanonName(name),
            VerificationPassed = verified,
            VerificationMessage = message,
        };
    }

    /// <summary>
    /// 把完整部署腳本（DacFx 的 GenerateScript 輸出）清成與逐物件部署檔一致的乾淨單檔：
    /// 去除 SQLCMD 樣板、註解標頭、PRINT 進度與原 USE，改套統一的 USE [部署庫] + 必要 SET 標頭，
    /// 其餘所有操作批次（依相依順序）原樣保留。
    /// </summary>
    public static string CleanFullScript(string fullScript, string databaseName)
    {
        var (sessionSetup, operationBatches) = PartitionBatches(fullScript);
        return Compose(databaseName, sessionSetup, operationBatches);
    }

    /// <summary>
    /// 把「完整部署腳本」依所屬物件切分成多個乾淨單檔（一檔一物件），保留完整腳本的權威拓樸順序。
    ///
    /// 為什麼不再用 DacFx「逐物件 GenerateScript」獨立重產：那會為了讓單檔可獨立部署而<b>夾帶前置相依物件</b>
    /// （例如某 proc 的檔內含它依賴的資料表＋函式），造成跨檔重複、照檔名順序執行時撞「物件已存在」；
    /// 且檔案順序跟完整腳本的相依拓樸序脫鉤（如函式被排到所有 proc 之後）。
    ///
    /// 改法：完整腳本本身已是 DacFx 拓樸排序、每物件只出現一次、與 VS 逐行一致。但它是<b>階段式</b>輸出
    /// （資料表→約束→程式化物件→擴充屬性），同一物件的片段散在各階段。本方法掃描每個 <c>GO</c> 批次、
    /// 判定其所屬物件（DDL 名稱／索引的 ON 資料表／擴充屬性的 level0+level1），把同一物件的所有批次
    /// （本體＋索引＋自身約束＋欄位描述）<b>聚合回它自己的單檔</b>，檔案順序＝物件在完整腳本中「首次出現」
    /// （即建立順序）。如此一檔一物件、不重複、不夾帶、照檔名順序執行即等價於完整腳本。
    ///
    /// 唯一無法兼顧「一檔一物件」與「照順序可執行」的情形是<b>跨表外鍵的前向參照／循環</b>
    /// （子表檔排在父表檔之前）——這是 SQL Server 本身也須靠延後約束才能解的死結，故僅偵測並警告，不靜默產生壞腳本。
    /// </summary>
    public static SplitResult SplitFullScriptByObject(string fullScript, string databaseName)
    {
        var warnings = new List<string>();
        var (sessionSetup, operationBatches) = PartitionBatches(fullScript);

        var order = new List<string>();                                  // 物件 key，依首次出現
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var display = new Dictionary<string, string>(StringComparer.Ordinal);
        string? lastKey = null;
        int unattributed = 0;

        foreach (var batch in operationBatches)
        {
            string key, name;
            if (OwnerOf(batch) is { } o) { key = o.Key; name = o.Display; }
            else if (lastKey is not null) { key = lastKey; name = display[lastKey]; unattributed++; }
            else { key = "__前置__"; name = "前置批次"; unattributed++; }

            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<string>();
                groups[key] = list;
                display[key] = name;
                order.Add(key);
            }
            list.Add(batch);
            lastKey = key;
        }

        // 跨表外鍵前向參照偵測：FK 指向的物件若排在更後面的檔，照檔名順序執行時該外鍵會失敗。
        var indexOfKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < order.Count; i++) indexOfKey[order[i]] = i;
        foreach (var key in order)
            foreach (var batch in groups[key])
            {
                var target = ForeignKeyTarget(batch);
                if (target is not null && indexOfKey.TryGetValue(target, out var ti) && ti > indexOfKey[key])
                    warnings.Add($"{display[key]} 有外鍵參照 {target}，但後者排在更後面的檔；" +
                                 "照檔名順序執行時該外鍵會失敗（請手動把此外鍵約束移到結尾再執行）。");
            }
        if (unattributed > 0)
            warnings.Add($"有 {unattributed} 個批次無法判定所屬物件，已併入相鄰物件的檔案。");

        var files = new List<ObjectScriptFile>();
        foreach (var key in order)
        {
            var ops = groups[key];
            var content = Compose(databaseName, sessionSetup, ops);
            var (action, type, name) = DetectMetadata(ops, display[key]);
            var fileName = $"{action}_{type}_{ConvertToSafeName(name)}.sql";
            var (verified, message) = Verify(content, sessionSetup, ops, databaseName);
            files.Add(new ObjectScriptFile
            {
                Action = action, ObjectType = type, ObjectName = name, FileName = fileName,
                Content = content, OperationBatchCount = ops.Count, OwnerKey = key,
                VerificationPassed = verified, VerificationMessage = message,
            });
        }

        // 完整性防線：所有分檔的批次加總必須＝完整腳本的操作批次數（一個不多、一個不少、不重複）。
        // 由「每批次只進一組」保證恆等，但顯式檢查可在未來怪異輸入或程式回歸時明確告警，不靜默出錯。
        int emitted = files.Sum(f => f.OperationBatchCount);
        if (emitted != operationBatches.Count)
            warnings.Add($"切分批次數（{emitted}）與完整腳本（{operationBatches.Count}）不符，請檢查切分邏輯。");

        return new SplitResult { Files = files, Warnings = warnings };
    }

    /// <summary>
    /// 判定一個批次所屬的物件：(canonical key, 顯示名)。判不出回 null（交由呼叫端併入相鄰物件）。
    /// 順序很重要——索引須先於一般 DDL 判定（索引的擁有者是 ON 的資料表，不是索引名）。
    /// </summary>
    private static (string Key, string Display)? OwnerOf(string batch)
    {
        var idx = IndexOnTable().Match(batch);
        if (idx.Success) { var t = idx.Groups["tbl"].Value.Trim(); return (CanonName(t), t); }

        var ddl = DdlMatch().Match(batch);
        if (ddl.Success && !string.Equals(ddl.Groups["Type"].Value.Trim(), "INDEX", StringComparison.OrdinalIgnoreCase))
        {
            var nm = ddl.Groups["Name"].Value.Trim();
            return (CanonName(nm), nm);
        }

        if (ExtPropOp().IsMatch(batch))
        {
            var l1 = Level1Name().Match(batch);
            if (l1.Success)
            {
                var l0 = Level0Name().Match(batch);
                var raw = (l0.Success ? l0.Groups["v"].Value + "." : "") + l1.Groups["v"].Value;
                var disp = (l0.Success ? $"[{l0.Groups["v"].Value}]." : "") + $"[{l1.Groups["v"].Value}]";
                return (CanonName(raw), disp);
            }
        }
        return null;
    }

    /// <summary>批次若為外鍵約束，回傳其指向的資料表 canonical key；否則 null。</summary>
    private static string? ForeignKeyTarget(string batch)
    {
        if (!batch.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase)) return null;
        var m = FkReferences().Match(batch);
        return m.Success ? CanonName(m.Groups["tbl"].Value.Trim()) : null;
    }

    /// <summary>物件名正規化：去括號／空白、統一點號、轉小寫，作為跨物件比對／對映的 key。</summary>
    public static string CanonName(string n) =>
        Regex.Replace(Regex.Replace(n, @"[\[\]\s]", ""), @"\s*\.\s*", ".").ToLowerInvariant();

    /// <summary>
    /// 把 DacFx 腳本拆成（必要 SET 批次, 真正的操作批次）；
    /// 丟掉 USE、SQLCMD 指令/變數、純註解、純 PRINT 進度批次。
    /// </summary>
    private static (List<string> SessionSetup, List<string> OperationBatches) PartitionBatches(string script)
    {
        var sessionSetup = new List<string>();
        var operationBatches = new List<string>();
        foreach (var batch in Batches(script))
        {
            if (SessionSetup().IsMatch(batch)) { sessionSetup.Add(batch); continue; }

            bool isUse = UseBatch().IsMatch(batch);
            bool isSqlCmd = SqlCmdDirective().IsMatch(batch) || SqlCmdVar().IsMatch(batch);
            bool isCommentOnly = Regex.Replace(
                Regex.Replace(batch, @"(?s)/\*.*?\*/", ""), @"(?m)--.*$", "").Trim().Length == 0;
            bool isPrintOnly = PrintStart().IsMatch(batch) && !OperationKeyword().IsMatch(batch);

            if (isUse || isSqlCmd || isCommentOnly || isPrintOnly) continue;
            operationBatches.Add(batch);
        }
        return (sessionSetup, operationBatches);
    }

    /// <summary>統一標頭（USE [db] + 必要 SET）＋操作批次，組成乾淨可直接執行的腳本。</summary>
    private static string Compose(string databaseName, List<string> sessionSetup, List<string> operationBatches)
    {
        var headerParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(databaseName))
            headerParts.Add($"USE [{databaseName.Replace("]", "]]")}];{CrLf}GO");
        foreach (var s in sessionSetup) headerParts.Add(s + CrLf + "GO");
        var header = string.Join(CrLf + CrLf, headerParts);

        var inner = string.Join(CrLf + CrLf, operationBatches.Select(b => b + CrLf + "GO"));
        var body = string.IsNullOrWhiteSpace(header) ? inner : header + CrLf + CrLf + inner;
        return NormalizeCrLf(body).TrimEnd() + CrLf;
    }

    /// <summary>從操作批次找出最能代表此物件的 動作/類型/名稱（優先用與 fallback 同名的 DDL）。</summary>
    private static (string Action, string Type, string Name) DetectMetadata(
        List<string> operationBatches, string fallbackObjectName)
    {
        string Canon(string n) => Regex.Replace(
            Regex.Replace(n, @"[\[\]\s]", ""), @"\s*\.\s*", ".").ToLowerInvariant();
        var wanted = Canon(fallbackObjectName);

        DdlInfo? firstDdl = null;
        foreach (var batch in operationBatches)
        {
            var m = DdlMatch().Match(batch);
            if (!m.Success) continue;
            var info = ToDdlInfo(m);
            firstDdl ??= info;
            if (Canon(info.Name) == wanted) return (info.Action, info.Type, info.Name);
        }
        if (firstDdl is { } d) return (d.Action, d.Type, d.Name);

        // 純描述（擴充屬性）物件
        foreach (var batch in operationBatches)
        {
            if (!ExtPropOp().IsMatch(batch)) continue;
            var ep = ExtPropOp().Match(batch);
            var act = ep.Groups["Op"].Value.ToUpperInvariant() switch
            {
                "ADD" => "ADD", "UPDATE" => "UPDATE", "DROP" => "DROP", _ => "CHANGE",
            };
            var l1 = Level1Name().Match(batch);
            var l2 = Level2Name().Match(batch);
            var nm = l1.Success ? $"[{l1.Groups["v"].Value}]" : "EXTENDED_PROPERTY";
            if (l2.Success) nm += $".[{l2.Groups["v"].Value}]";
            return (act, "DESCRIPTION", nm);
        }

        return ("CHANGE", "OBJECT", string.IsNullOrWhiteSpace(fallbackObjectName) ? "UNKNOWN_OBJECT" : fallbackObjectName);
    }

    private readonly record struct DdlInfo(string Action, string Type, string Name);

    private static DdlInfo ToDdlInfo(Match m)
    {
        var action = Regex.Replace(m.Groups["Action"].Value.Trim(), @"\s+", "_").ToUpperInvariant();
        var type = m.Groups["Type"].Value.Trim().ToUpperInvariant();
        if (type == "PROC") type = "PROCEDURE";
        return new DdlInfo(action, type, m.Groups["Name"].Value.Trim());
    }

    /// <summary>驗證整理後的單檔：去掉標頭後的操作批次，與來源操作批次內容/順序完全一致。</summary>
    private static (bool Ok, string? Message) Verify(
        string content, List<string> sessionSetup, List<string> expectedOps, string databaseName)
    {
        int headerCount = (string.IsNullOrWhiteSpace(databaseName) ? 0 : 1) + sessionSetup.Count;
        var fileBatches = Batches(content);
        if (fileBatches.Count - headerCount != expectedOps.Count)
            return (false, $"操作批次數不符（標頭{headerCount}＋預期{expectedOps.Count}，實得{fileBatches.Count}）。");

        for (int i = 0; i < expectedOps.Count; i++)
        {
            var expected = Batches(expectedOps[i]);
            // 單一操作批次正規化後應為單批；逐字比對。
            if (!string.Equals(fileBatches[headerCount + i], string.Join(CrLf, expected), StringComparison.Ordinal))
                return (false, $"第 {i + 1} 個操作批次內容或順序不一致。");
        }
        return (true, null);
    }
}
