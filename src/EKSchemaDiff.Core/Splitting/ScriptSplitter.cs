using System.Text;
using System.Text.RegularExpressions;

namespace EKSchemaDiff.Core.Splitting;

public sealed class SplitFile
{
    public string Sequence { get; init; } = "";
    public string Action { get; init; } = "";
    public string ObjectType { get; init; } = "";
    public string ObjectName { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Content { get; init; } = "";
}

public sealed class SplitResult
{
    public List<SplitFile> Files { get; } = new();
    public string ExecutionHeader { get; init; } = "";
    /// <summary>切分後重組的操作批次是否與來源完全一致（內容與順序）。</summary>
    public bool VerificationPassed { get; set; }
    public string? VerificationMessage { get; set; }
}

/// <summary>
/// 將 DacFx GenerateScript() 的單一部署腳本，依 DDL 語句切分為依序編號的個別檔案。
/// 不依賴 PRINT 標記，改由 DDL 本身（CREATE/ALTER/DROP TYPE NAME）辨識物件，較穩健。
/// 移植並簡化自原 2.拆分FullScript.ps1 的切分與「批次內容/順序一致」嚴格驗證。
/// </summary>
public static partial class ScriptSplitter
{
    private const string CrLf = "\r\n";

    [GeneratedRegex(@"(?im)^\s*GO\s*(?:--[^\r\n]*)?$")]
    private static partial Regex GoSplit();

    // 連線層級 SET，會被當作每個檔案都需要的執行環境標頭保留。
    [GeneratedRegex(@"(?im)^\s*SET\s+(ANSI_NULLS|ANSI_PADDING|ANSI_WARNINGS|ARITHABORT|CONCAT_NULL_YIELDS_NULL|QUOTED_IDENTIFIER|NUMERIC_ROUNDABORT)\b")]
    private static partial Regex SessionSetup();

    [GeneratedRegex(@"(?im)^\s*USE\s+")]
    private static partial Regex UseBatch();

    // SQLCMD 部署樣板（:setvar / :on error / $(var)）—— 會被排除。
    [GeneratedRegex(@"(?im)^\s*:(setvar|on\s+error|connect)\b")]
    private static partial Regex SqlCmdDirective();

    [GeneratedRegex(@"\$\([^)]+\)")]
    private static partial Regex SqlCmdVar();

    // 純 PRINT 批次（DacFx 的進度提示），屬裝飾性，切分時跳過。
    [GeneratedRegex(@"(?im)^\s*PRINT\b")]
    private static partial Regex PrintStart();

    // 批次內是否含真正的操作關鍵字（DDL 或 EXEC）。
    [GeneratedRegex(@"(?im)\b(CREATE|ALTER|DROP|EXEC(?:UTE)?|MERGE|INSERT|UPDATE|DELETE|GRANT|DENY|REVOKE|sp_\w+)\b")]
    private static partial Regex OperationKeyword();

    // 辨識 DDL：動作 + 類型 + 名稱。
    [GeneratedRegex(@"(?im)^\s*(?<Action>CREATE\s+OR\s+ALTER|CREATE|ALTER|DROP)\s+(?<Type>PROCEDURE|PROC|VIEW|FUNCTION|TABLE|TRIGGER|SYNONYM|SEQUENCE|TYPE|SCHEMA|INDEX)\s+(?<Name>(?:\[[^\]\r\n]+\]|[A-Za-z0-9_#$@]+)(?:\s*\.\s*(?:\[[^\]\r\n]+\]|[A-Za-z0-9_#$@]+))?)")]
    private static partial Regex DdlMatch();

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

    private static IEnumerable<string> NormalizedBatches(string text) =>
        GoSplit().Split(NormalizeCrLf(text))
            .Select(b => b.Trim())
            .Where(b => !string.IsNullOrWhiteSpace(b));

    /// <summary>
    /// 切分腳本。databaseName 用於每個檔案的 USE 標頭（傳入目標/部署資料庫名）。
    /// groupByObject＝true 時，同一物件的多個批次（例如資料表 DDL 與其描述）會合併到同一個檔案。
    /// </summary>
    public static SplitResult Split(string fullScript, string databaseName, bool groupByObject = true)
    {
        var content = NormalizeCrLf(fullScript);
        var rawBatches = GoSplit().Split(content)
            .Select(b => b.Trim())
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .ToList();

        // 分出前置環境批次（USE/SET/SQLCMD/註解）與操作批次（含 DDL）。
        var sessionSetup = new List<string>();
        var operationBatches = new List<string>();

        foreach (var batch in rawBatches)
        {
            bool isSet = SessionSetup().IsMatch(batch);
            bool isUse = UseBatch().IsMatch(batch);
            bool isSqlCmd = SqlCmdDirective().IsMatch(batch) || SqlCmdVar().IsMatch(batch);
            bool isCommentOnly = Regex.Replace(
                Regex.Replace(batch, @"(?s)/\*.*?\*/", ""),
                @"(?m)--.*$", "").Trim().Length == 0;

            // 純 PRINT 進度提示（無實際操作關鍵字）視為裝飾，跳過。
            bool isPrintOnly = PrintStart().IsMatch(batch) && !OperationKeyword().IsMatch(batch);

            if (isSet) { sessionSetup.Add(batch); continue; }
            if (isUse || isSqlCmd || isCommentOnly || isPrintOnly) continue; // 丟棄部署樣板/提示，改用統一標頭
            operationBatches.Add(batch);
        }

        // 組執行環境標頭：USE [db] + 必要 SET。
        var headerParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(databaseName))
            headerParts.Add($"USE [{databaseName.Replace("]", "]]")}];{CrLf}GO");
        foreach (var s in sessionSetup)
            headerParts.Add(s + CrLf + "GO");
        var executionHeader = string.Join(CrLf + CrLf, headerParts);

        var result = new SplitResult { ExecutionHeader = executionHeader };

        // 組成單位：
        //  - 連續的 sp_refreshsqlmodule 批次併成單一單位（DacFx 常一次刷新大量相依模組）。
        //  - groupByObject＝true 時，指向同一物件的批次（資料表 DDL + 其描述、同表多個描述等）
        //    併到該物件第一次出現的單位（後出現的批次往前併，依物件分檔；描述相依於物件本身，安全）。
        //  - 其餘維持原順序各自成單位。
        var units = new List<(bool IsRefresh, string? Key, List<string> Batches)>();
        var keyToUnit = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var batch in operationBatches)
        {
            bool isRefresh = RefreshModule().IsMatch(batch);
            if (isRefresh)
            {
                if (units.Count > 0 && units[^1].IsRefresh)
                    units[^1].Batches.Add(batch);
                else
                    units.Add((true, null, new List<string> { batch }));
                continue;
            }

            string? key = groupByObject ? OwningKey(batch) : null;
            if (key is not null && keyToUnit.TryGetValue(key, out int idx))
            {
                units[idx].Batches.Add(batch);
            }
            else
            {
                units.Add((false, key, new List<string> { batch }));
                if (key is not null) keyToUnit[key] = units.Count - 1;
            }
        }

        int index = 1;
        foreach (var unit in units)
        {
            var seq = index.ToString("D2");
            string action, type, name, fileName;

            if (unit.IsRefresh)
            {
                var modules = unit.Batches
                    .SelectMany(b => RefreshModule().Matches(b).Select(mm => mm.Groups["v"].Value.Trim()))
                    .ToList();
                action = "REFRESH";
                type = "SQLMODULE";
                name = string.Join(", ", modules);
                var suffix = modules.Count == 1 ? ConvertToSafeName(modules[0]) : $"{modules.Count}_OBJECTS";
                fileName = $"{seq}_REFRESH_SQLMODULE_{suffix}.sql";
            }
            else
            {
                (action, type, name) = DetectMetadata(unit.Batches[0]);
                fileName = $"{seq}_{action}_{type}_{ConvertToSafeName(name)}.sql";
            }

            var inner = string.Join(CrLf + CrLf, unit.Batches.Select(b => b + CrLf + "GO"));
            var body = string.IsNullOrWhiteSpace(executionHeader)
                ? inner
                : executionHeader + CrLf + CrLf + inner;

            result.Files.Add(new SplitFile
            {
                Sequence = seq,
                Action = action,
                ObjectType = type,
                ObjectName = name,
                FileName = fileName,
                Content = NormalizeCrLf(body).TrimEnd() + CrLf,
            });
            index++;
        }

        Verify(result, operationBatches, ordered: !groupByObject);
        return result;
    }

    [GeneratedRegex(@"(?im)\bsp_refreshsqlmodule\s+N?'(?<v>(?:[^']|'')+)'")]
    private static partial Regex RefreshModule();

    [GeneratedRegex(@"(?im)sp_(?<Op>add|update|drop)extendedproperty\b")]
    private static partial Regex ExtPropOp();

    [GeneratedRegex(@"(?im)@level0name\s*=\s*N?'(?<v>[^']+)'")]
    private static partial Regex Level0Name();

    [GeneratedRegex(@"(?im)@level1name\s*=\s*N?'(?<v>[^']+)'")]
    private static partial Regex Level1Name();

    [GeneratedRegex(@"(?im)@level2name\s*=\s*N?'(?<v>[^']+)'")]
    private static partial Regex Level2Name();

    /// <summary>判斷批次「所屬物件」的正規化鍵；同鍵的批次會併入同一個切分檔。取不到回傳 null。</summary>
    private static string? OwningKey(string batch)
    {
        var ddl = DdlMatch().Match(batch);
        if (ddl.Success) return CanonicalName(ddl.Groups["Name"].Value);

        // 擴充屬性（描述）：所屬物件 = [level0name(schema)].[level1name(table/proc...)]。
        if (ExtPropOp().IsMatch(batch))
        {
            var l1 = Level1Name().Match(batch);
            if (l1.Success)
            {
                var l0 = Level0Name().Match(batch);
                var schema = l0.Success ? l0.Groups["v"].Value : "dbo";
                return CanonicalName($"[{schema}].[{l1.Groups["v"].Value}]");
            }
        }
        return null;
    }

    /// <summary>把物件名稱正規化成比對用鍵：去括號、去空白、小寫，schema.object 形式。</summary>
    private static string CanonicalName(string name)
    {
        var s = name.Trim();
        s = Regex.Replace(s, @"[\[\]]", "");
        s = Regex.Replace(s, @"\s*\.\s*", ".");
        s = Regex.Replace(s, @"\s+", "");
        return s.ToLowerInvariant();
    }

    private static (string Action, string Type, string Name) DetectMetadata(string batch)
    {
        var m = DdlMatch().Match(batch);
        if (m.Success)
        {
            var action = Regex.Replace(m.Groups["Action"].Value.Trim(), @"\s+", "_").ToUpperInvariant();
            var type = m.Groups["Type"].Value.Trim().ToUpperInvariant();
            if (type == "PROC") type = "PROCEDURE";
            return (action, type, m.Groups["Name"].Value.Trim());
        }

        // 擴充屬性（描述 MS_Description）：依 sp_xxxextendedproperty 的 level1/level2 命名。
        var ep = ExtPropOp().Match(batch);
        if (ep.Success)
        {
            var action = ep.Groups["Op"].Value.ToUpperInvariant() switch
            {
                "ADD" => "ADD",
                "UPDATE" => "UPDATE",
                "DROP" => "DROP",
                _ => "CHANGE",
            };
            var l1 = Level1Name().Match(batch);
            var l2 = Level2Name().Match(batch);
            var name = l1.Success ? $"[{l1.Groups["v"].Value}]" : "EXTENDED_PROPERTY";
            if (l2.Success) name += $".[{l2.Groups["v"].Value}]";
            return (action, "DESCRIPTION", name);
        }

        return ("CHANGE", "OBJECT", "UNKNOWN_OBJECT");
    }

    /// <summary>
    /// 嚴格驗證：切分後每個檔案去掉標頭的操作批次，與來源操作批次一致。
    /// ordered＝true 時要求內容與順序完全相同（未分組）；
    /// ordered＝false 時（依物件分組，順序會變）改驗「多重集合一致」——每個批次內容不增不減不改，僅檔案歸屬與順序不同。
    /// </summary>
    private static void Verify(SplitResult result, List<string> expectedOperationBatches, bool ordered)
    {
        var headerBatches = NormalizedBatches(result.ExecutionHeader).ToList();
        var actual = new List<string>();

        foreach (var file in result.Files)
        {
            var fileBatches = NormalizedBatches(file.Content).ToList();
            if (fileBatches.Count < headerBatches.Count)
            {
                result.VerificationPassed = false;
                result.VerificationMessage = $"檔案 {file.FileName} 批次數少於標頭。";
                return;
            }
            for (int h = 0; h < headerBatches.Count; h++)
            {
                if (!string.Equals(fileBatches[h], headerBatches[h], StringComparison.Ordinal))
                {
                    result.VerificationPassed = false;
                    result.VerificationMessage = $"檔案 {file.FileName} 的 USE/SET 標頭不正確。";
                    return;
                }
            }
            for (int b = headerBatches.Count; b < fileBatches.Count; b++)
                actual.Add(fileBatches[b]);
        }

        var expected = expectedOperationBatches
            .SelectMany(NormalizedBatches)
            .ToList();

        if (actual.Count != expected.Count)
        {
            result.VerificationPassed = false;
            result.VerificationMessage = $"來源操作批次={expected.Count}，切分後={actual.Count}。";
            return;
        }

        if (ordered)
        {
            for (int i = 0; i < expected.Count; i++)
            {
                if (!string.Equals(actual[i], expected[i], StringComparison.Ordinal))
                {
                    result.VerificationPassed = false;
                    result.VerificationMessage = $"第 {i + 1} 個操作批次內容或順序不一致。";
                    return;
                }
            }
        }
        else
        {
            // 依物件分組：順序會變，改驗多重集合一致（內容不增不減不改）。
            var expSorted = expected.OrderBy(x => x, StringComparer.Ordinal).ToList();
            var actSorted = actual.OrderBy(x => x, StringComparer.Ordinal).ToList();
            for (int i = 0; i < expSorted.Count; i++)
            {
                if (!string.Equals(actSorted[i], expSorted[i], StringComparison.Ordinal))
                {
                    result.VerificationPassed = false;
                    result.VerificationMessage = "切分後的操作批次集合與來源不一致（有批次遺失、重複或被竄改）。";
                    return;
                }
            }
        }

        result.VerificationPassed = true;
    }
}
