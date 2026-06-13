using EKSchemaDiff.Core.Config;
using Microsoft.SqlServer.Dac.Compare;

namespace EKSchemaDiff.Core.Compare;

/// <summary>
/// DacFx SchemaComparison 的薄包裝：建立兩端點、套用比對選項、執行比對、產生差異清單與部署腳本。
/// 這是與 VS「結構描述比較」相同的官方引擎。
/// </summary>
public sealed class SchemaComparer
{
    /// <summary>依 profile 連線兩端並比對，回傳結果包裝。</summary>
    public static CompareSession Run(
        Profile profile,
        Func<string, string>? passwordPrompt = null)
    {
        var sourceConn = profile.Source.BuildConnectionString(passwordPrompt);
        var targetConn = profile.Target.BuildConnectionString(passwordPrompt);

        var source = new SchemaCompareDatabaseEndpoint(sourceConn);
        var target = new SchemaCompareDatabaseEndpoint(targetConn);
        var comparison = new SchemaComparison(source, target);

        var unrecognized = new List<string>();
        profile.CompareOptions.ApplyTo(comparison.Options, unrecognized);

        var result = comparison.Compare();
        return new CompareSession(profile, comparison, result, unrecognized);
    }
}

/// <summary>一次比對的結果與後續操作（勾選、預覽、產生腳本）。</summary>
public sealed class CompareSession
{
    private readonly SchemaComparison _comparison;
    private readonly SchemaComparisonResult _result;
    private readonly List<ObjectDifference> _differences;

    internal CompareSession(
        Profile profile,
        SchemaComparison comparison,
        SchemaComparisonResult result,
        IReadOnlyList<string> unrecognizedExcludedTypes)
    {
        Profile = profile;
        _comparison = comparison;
        _result = result;
        UnrecognizedExcludedTypes = unrecognizedExcludedTypes;
        _differences = result.IsValid
            ? result.Differences.Select(d => new ObjectDifference(d)).ToList()
            : new List<ObjectDifference>();
    }

    public Profile Profile { get; }

    public bool IsValid => _result.IsValid;

    /// <summary>設定檔指定但 DacFx 不認得的排除類型名稱（供警示）。</summary>
    public IReadOnlyList<string> UnrecognizedExcludedTypes { get; }

    public IReadOnlyList<ObjectDifference> Differences => _differences;

    public IEnumerable<string> GetErrors() =>
        _result.IsValid ? Enumerable.Empty<string>() : _result.GetErrors().Select(e => e.Message);

    /// <summary>依名稱（[schema].[obj]）勾選/取消要納入部署的物件。傳入要納入的集合，其餘排除。</summary>
    public void ApplyInclusion(ISet<ObjectDifference> include) => SetInclusion(include.Contains);

    /// <summary>產生部署 SQL（僅含已納入的差異，依相依順序）。</summary>
    public string GenerateScript()
    {
        var deployDb = Profile.ResolveDeployDatabaseName();
        var script = _result.GenerateScript(deployDb);
        return script.Script ?? string.Empty;
    }

    /// <summary>
    /// 逐物件產生官方部署腳本：只納入「該物件」，由 DacFx 官方引擎產生只含該物件變更
    /// （含其描述、相依模組刷新等，皆由引擎決定）的腳本——等同 VS「結構描述比較」中只勾一個物件再匯出。
    /// 會改動納入狀態；一批處理完請呼叫 <see cref="RestoreInclusion"/> 還原。
    /// </summary>
    public string GenerateObjectScript(ObjectDifference only)
    {
        SetInclusion(d => ReferenceEquals(d, only));
        try { return _result.GenerateScript(Profile.ResolveDeployDatabaseName()).Script ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>把納入狀態還原為指定集合（逐物件產生腳本後呼叫）。</summary>
    public void RestoreInclusion(IReadOnlyCollection<ObjectDifference> included)
    {
        var set = new HashSet<ObjectDifference>(included);
        SetInclusion(set.Contains);
    }

    private void SetInclusion(Func<ObjectDifference, bool> shouldInclude)
    {
        foreach (var d in _differences)
        {
            bool want = shouldInclude(d);
            if (want && !d.Inner.Included) _result.Include(d.Inner);
            else if (!want && d.Inner.Included) _result.Exclude(d.Inner);
        }
    }
}
