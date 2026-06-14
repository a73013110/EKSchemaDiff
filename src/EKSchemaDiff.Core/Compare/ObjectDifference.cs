using System.Text;
using Microsoft.SqlServer.Dac.Compare;
using Microsoft.SqlServer.Dac.Model;

namespace EKSchemaDiff.Core.Compare;

public enum ChangeKind
{
    Add,     // 來源有、目標沒有 → 新增
    Change,  // 兩邊都有、內容不同 → 變更
    Delete,  // 目標有、來源沒有 → 刪除
    Unknown,
}

/// <summary>
/// 一筆物件層級差異的 UI 友善包裝，對應一個 DacFx SchemaDifference。
/// 提供顯示名稱、類型、變更種類，以及供差異報告用的來源/目標腳本。
/// </summary>
public sealed class ObjectDifference
{
    private readonly SchemaDifference _diff;

    internal ObjectDifference(SchemaDifference diff)
    {
        _diff = diff;
        Kind = diff.UpdateAction switch
        {
            SchemaUpdateAction.Add => ChangeKind.Add,
            SchemaUpdateAction.Change => ChangeKind.Change,
            SchemaUpdateAction.Delete => ChangeKind.Delete,
            _ => ChangeKind.Unknown,
        };

        ObjectTypeName = diff.Name ?? diff.SourceObject?.ObjectType.Name
                         ?? diff.TargetObject?.ObjectType.Name ?? "Object";
        Name = ResolveName(diff);
    }

    /// <summary>底層 DacFx 差異節點（供 include/exclude 用）。</summary>
    internal SchemaDifference Inner => _diff;

    /// <summary>物件完整名稱，如 [dbo].[DemoTable]。</summary>
    public string Name { get; }

    /// <summary>物件類型顯示名，如 Tables、Procedures。</summary>
    public string ObjectTypeName { get; }

    /// <summary>變更種類（新增／變更／刪除）。對應底層 DacFx 的 SchemaUpdateAction。</summary>
    public ChangeKind Kind { get; }

    /// <summary>是否納入此次部署（對應 DacFx Included）。</summary>
    public bool Included
    {
        get => _diff.Included;
    }

    /// <summary>
    /// 來源物件腳本（更版內容）。含本體 + 子差異（如欄位描述 MS_Description 等擴充屬性）腳本，
    /// 讓差異報告能顯示描述異動。取不到時為空字串。
    /// </summary>
    public string SourceScript => BuildScript(d => d.SourceObject);

    /// <summary>目標物件腳本（原版內容）。含本體 + 子差異腳本。取不到時為空字串。</summary>
    public string TargetScript => BuildScript(d => d.TargetObject);

    /// <summary>
    /// 組合一側的腳本：物件本體 + 所有子差異（遞迴）的腳本。
    /// DacFx 把表/欄位的描述（擴充屬性）視為子差異，本體 script 不含描述，
    /// 故需把子差異腳本一併帶出，差異報告才看得到 sp_addextendedproperty/sp_updateextendedproperty 之類異動。
    /// </summary>
    private string BuildScript(Func<SchemaDifference, TSqlObject?> pick)
    {
        var sb = new StringBuilder();
        Append(_diff, pick, sb);
        return sb.ToString().TrimEnd();
    }

    private static void Append(SchemaDifference diff, Func<SchemaDifference, TSqlObject?> pick, StringBuilder sb)
    {
        var script = TryScript(pick(diff));
        if (!string.IsNullOrWhiteSpace(script))
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(script.TrimEnd());
        }
        foreach (var child in SafeChildren(diff))
            Append(child, pick, sb);
    }

    private static IEnumerable<SchemaDifference> SafeChildren(SchemaDifference diff)
    {
        try { return diff.Children ?? Enumerable.Empty<SchemaDifference>(); }
        catch { return Enumerable.Empty<SchemaDifference>(); }
    }

    private static string TryScript(TSqlObject? obj)
    {
        if (obj is null) return string.Empty;
        try
        {
            return obj.TryGetScript(out var script) ? (script ?? string.Empty) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ResolveName(SchemaDifference diff)
    {
        var obj = diff.SourceObject ?? diff.TargetObject;
        if (obj is not null)
        {
            try
            {
                var parts = obj.Name.Parts;
                if (parts is { Count: > 0 })
                    return string.Join(".", parts.Select(p => $"[{p}]"));
            }
            catch
            {
                // 落到下方備援
            }
        }

        // 備援：用 DacFx 提供的名稱字串
        return string.IsNullOrWhiteSpace(diff.Name) ? "(unnamed)" : diff.Name!;
    }
}
