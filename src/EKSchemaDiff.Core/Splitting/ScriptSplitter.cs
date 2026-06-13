using System.Text.RegularExpressions;

namespace EKSchemaDiff.Core.Splitting;

/// <summary>單一物件的部署檔：由 DacFx 逐物件腳本清理而來。</summary>
public sealed class ObjectScriptFile
{
    public string Action { get; init; } = "";
    public string ObjectType { get; init; } = "";
    public string ObjectName { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Content { get; init; } = "";
    public int OperationBatchCount { get; init; }
    /// <summary>清理後的操作批次是否與來源（DacFx 原始腳本）一致（內容不增不減不改）。</summary>
    public bool VerificationPassed { get; init; }
    public string? VerificationMessage { get; init; }
}

/// <summary>
/// 把 DacFx「單一物件」的 GenerateScript() 輸出整理成乾淨、可直接執行的單檔：
/// 去除 SQLCMD 部署樣板（:setvar/:on error/SQLCMD 偵測）、PRINT 進度與原 USE，
/// 改套統一的 USE [部署庫] + 必要 SET 標頭，其餘操作批次（DDL、描述、相依刷新…由 DacFx 決定）原樣保留。
/// 不做跨物件切分（每個檔本來就只含一個物件），因此沒有歸屬猜測的風險。
/// </summary>
public static partial class ScriptSplitter
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

    [GeneratedRegex(@"(?im)@level1name\s*=\s*N?'(?<v>[^']+)'")]
    private static partial Regex Level1Name();

    [GeneratedRegex(@"(?im)@level2name\s*=\s*N?'(?<v>[^']+)'")]
    private static partial Regex Level2Name();

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
            VerificationPassed = verified,
            VerificationMessage = message,
        };
    }

    /// <summary>
    /// 把完整部署腳本（DacFx 的 GenerateScript 輸出）清成與切分檔一致的乾淨單檔：
    /// 去除 SQLCMD 樣板、註解標頭、PRINT 進度與原 USE，改套統一的 USE [部署庫] + 必要 SET 標頭，
    /// 其餘所有操作批次（依相依順序）原樣保留。
    /// </summary>
    public static string CleanFullScript(string fullScript, string databaseName)
    {
        var (sessionSetup, operationBatches) = PartitionBatches(fullScript);
        return Compose(databaseName, sessionSetup, operationBatches);
    }

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
