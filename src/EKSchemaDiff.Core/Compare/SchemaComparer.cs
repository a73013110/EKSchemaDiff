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
    // 非 readonly：ApplyInclusion 會以「比對前排除 + 重比」取代逐筆翻轉，過程中整組替換。
    private SchemaComparison _comparison;
    private SchemaComparisonResult _result;
    private List<ObjectDifference> _differences;

    // 部署排除清單（未納入物件的識別）：ApplyInclusion 重比時記下，供還原腳本與逐物件平行重用，
    // 避免重新從（已縮小的）差異清單推導。未經 ApplyInclusion（如非互動全量）時為 null。
    private IReadOnlyList<SchemaComparisonExcludedObjectId>? _deployExclusions;

    // 使用者親自勾選的物件名稱（CommitInclusion 時記下）：供區分「勾選」與「相依自動補入」。null = 未經勾選流程。
    private HashSet<string>? _pickedNames;

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

    /// <summary>
    /// 解析勾選：以「比對前排除未勾選物件 + 重跑一次 Compare」算出實際要部署的完整差異集。
    /// 取代對全量差異逐筆 Include/Exclude——原作法在「全部納入」的結果上逐筆 Exclude 數百個未勾選項、
    /// 每次重算相依，極慢（實測 17/351 約 62s）；改法只需一次 Compare（約與初次比對同級 ~9-20s）。
    /// 為部署安全，DacFx 仍會把被勾選物件「相依到、且也有變更」的物件自動補回（即使被列入排除），
    /// 故結果常多於勾選數（以 <see cref="InclusionResult.WasPicked"/> 區分勾選／相依補入）。
    /// <b>不會更動本工作階段狀態</b>——供「確認頁」預覽；確認後再呼叫 <see cref="CommitInclusion"/> 提交。
    /// 重比失敗時退回對現狀逐筆翻轉並回傳現狀。
    /// </summary>
    public InclusionResult ResolveInclusion(ISet<ObjectDifference> include)
    {
        var keepNames = include.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exclusions = BuildExclusionsExcept(keepNames);   // 讀目前（全量）差異

        var comparison = new SchemaComparison(_comparison.Source, _comparison.Target);
        Profile.CompareOptions.ApplyTo(comparison.Options, new List<string>());
        foreach (var ex in exclusions)
        {
            comparison.ExcludedSourceObjects.Add(ex);
            comparison.ExcludedTargetObjects.Add(ex);
        }

        var result = comparison.Compare();
        if (!result.IsValid)
        {
            // 重比失敗：退回逐筆翻轉現狀（少數情況），回傳現狀供確認頁顯示。
            SetInclusion(include.Contains);
            return new InclusionResult
            {
                Comparison = _comparison, Result = _result, Differences = _differences,
                Exclusions = GetDeployExclusions(), PickedNames = keepNames,
            };
        }

        // 收斂到 VS 等價的嚴格範圍：
        // 「比對前排除」只能依物件識別排除，會留下兩類非勾選物件仍 Included——
        //   (1) 資料庫層級選項等無 TSqlObject 的差異（建不出識別碼，永遠留在結果）；
        //   (2) 與勾選物件有「軟相依」、被 DacFx 自動補回 Included 的有差異物件（VS 嚴格模式不納入）。
        // 在縮小後的結果上，對「仍 Included 但非勾選」者逐筆 Exclude，把上述兩類收掉。
        // 此時模型仍含全部差異節點，DacFx 會為「真正硬相依」（部署勾選物件所必需）自動保留 Included、
        // 不致漏掉部署必要物件；排除可能觸發連鎖回補，故反覆收斂直到穩定（上限數圈，避免極端情況空轉）。
        var wrapped = result.Differences.Select(d => new ObjectDifference(d)).ToList();
        for (int pass = 0; pass < 4; pass++)
        {
            bool changed = false;
            foreach (var od in wrapped)
            {
                if (od.Included && !keepNames.Contains(od.Name))
                {
                    try { result.Exclude(od.Inner); changed = true; } catch { /* 硬相依：無法排除→保留 */ }
                }
            }
            if (!changed) break;
        }

        // 依「收斂後的實際 Included」重建部署排除清單，供還原腳本／逐物件平行重用，
        // 確保它們的範圍與部署一致（不會把剛收掉的物件又拉回來）。
        var finalKeep = wrapped.Where(d => d.Included).Select(d => d.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var finalExclusions = BuildExclusionsFrom(wrapped, finalKeep);

        return new InclusionResult
        {
            Comparison = comparison, Result = result,
            Differences = wrapped, Exclusions = finalExclusions, PickedNames = keepNames,
        };
    }

    /// <summary>提交 <see cref="ResolveInclusion"/> 的結果：之後 GenerateScript／逐物件／還原皆以此為準。</summary>
    public void CommitInclusion(InclusionResult resolved)
    {
        _comparison = resolved.Comparison;
        _result = resolved.Result;
        _differences = resolved.Differences;
        _deployExclusions = resolved.Exclusions;
        _pickedNames = resolved.PickedNames;
    }

    /// <summary>套用勾選（解析並立即提交）。非互動匯出用；互動流程改用 ResolveInclusion + 確認 + CommitInclusion。</summary>
    public void ApplyInclusion(ISet<ObjectDifference> include) => CommitInclusion(ResolveInclusion(include));

    /// <summary>此差異是否為使用者親自勾選（否則為相依自動補入）；未經勾選流程（如非互動全量）時一律視為勾選。</summary>
    public bool WasPicked(ObjectDifference d) => _pickedNames is null || _pickedNames.Contains(d.Name);

    /// <summary>
    /// 取得「部署排除清單」（未納入部署的物件識別），供還原腳本與逐物件平行各自重比時於比對前排除。
    /// ApplyInclusion 重比後直接重用其記錄；未經 ApplyInclusion 時，依目前差異的納入狀態即時推導。
    /// </summary>
    public IReadOnlyList<SchemaComparisonExcludedObjectId> GetDeployExclusions()
        => _deployExclusions ?? BuildExclusionsExcept(
               _differences.Where(d => d.Included).Select(d => d.Name)
                   .ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>產生部署 SQL（僅含已納入的差異，依相依順序）。</summary>
    public string GenerateScript()
    {
        var deployDb = Profile.ResolveDeployDatabaseName();
        var script = _result.GenerateScript(deployDb);
        return script.Script ?? string.Empty;
    }

    /// <summary>
    /// 產生「完整還原腳本」（完整部署腳本的反向）：把來源/目標端點對調後重新比對，
    /// 由官方引擎產生可將目標還原回部署前狀態的腳本。
    /// <paramref name="excludedObjects"/>（來自 <see cref="CaptureExcludedForReverse"/>）為部署時未納入的物件，
    /// 於比對「之前」即排除，使反向比對結果只含已納入物件（預設全部 Included）——
    /// 免去對數百筆差異逐筆 Exclude、每次重算相依的高成本（差異多時可省下數分鐘）。
    /// 重用既有端點（連線字串已建立），不會再次詢問密碼，也不影響正向比對狀態。
    /// </summary>
    public string GenerateReverseScript(IReadOnlyList<SchemaComparisonExcludedObjectId> excludedObjects)
    {
        // 端點對調：原目標當來源、原來源當目標 → 產生「把新結構改回舊結構」的腳本。
        var reverse = new SchemaComparison(_comparison.Target, _comparison.Source);
        Profile.CompareOptions.ApplyTo(reverse.Options, new List<string>());

        // 比對前排除未納入物件：兩端皆加入排除清單（依物件識別，不存在於某端則無作用）。
        // 如此 reverse.Compare() 直接只回已納入物件的差異，無需任何事後 Include/Exclude。
        foreach (var ex in excludedObjects)
        {
            reverse.ExcludedSourceObjects.Add(ex);
            reverse.ExcludedTargetObjects.Add(ex);
        }

        var result = reverse.Compare();
        if (!result.IsValid) return string.Empty;

        // 還原腳本一樣在目標（客戶）資料庫上執行，故沿用相同的部署庫名。
        return result.GenerateScript(Profile.ResolveDeployDatabaseName()).Script ?? string.Empty;
    }

    /// <summary>
    /// 建立「除了 <paramref name="keepNames"/> 以外的所有差異物件」的排除清單。
    /// 供平行逐物件時讓各 worker 的比對在 Compare「之前」就只保留自己負責的物件，
    /// 避免在數百筆全納入的結果上逐筆 isolate 的高成本。
    /// 會讀取目前差異的物件識別，須在主執行緒呼叫（DacFx 模型物件不保證可跨執行緒並行讀取）。
    /// </summary>
    public IReadOnlyList<SchemaComparisonExcludedObjectId> BuildExclusionsExcept(ISet<string> keepNames)
        => BuildExclusionsFrom(_differences, keepNames);

    /// <summary>
    /// 由指定差異集建立「除了 <paramref name="keepNames"/> 以外」的排除清單。
    /// 無 TSqlObject 的差異（如資料庫層級選項）無法建立識別碼，會被略過——
    /// 這類只能靠比對後逐筆 Exclude 收斂（見 <see cref="ResolveInclusion"/>），無法用「比對前排除」處理。
    /// </summary>
    private static IReadOnlyList<SchemaComparisonExcludedObjectId> BuildExclusionsFrom(
        IEnumerable<ObjectDifference> diffs, ISet<string> keepNames)
    {
        var list = new List<SchemaComparisonExcludedObjectId>();
        foreach (var d in diffs)
        {
            if (keepNames.Contains(d.Name)) continue;
            var obj = d.Inner.SourceObject ?? d.Inner.TargetObject;
            if (obj is not null)
                list.Add(new SchemaComparisonExcludedObjectId(obj.ObjectType, obj.Name));
        }
        return list;
    }

    /// <summary>
    /// 平行逐物件用：建立獨立的正向比對工作階段（重連資料庫重新比對），但比對前即套用 <paramref name="exclusions"/>，
    /// 使結果只含目標物件、可在自己的執行緒上廉價地 isolate + <see cref="GenerateObjectScript"/>。
    /// 與本階段及其他 worker 各持獨立的 SchemaComparison/Result，互不干擾。
    /// 重用既有端點（連線字串已建立），不會再次詢問密碼。可在背景執行緒呼叫。
    /// </summary>
    public CompareSession CreateScopedForwardSession(IReadOnlyList<SchemaComparisonExcludedObjectId> exclusions)
    {
        var comparison = new SchemaComparison(_comparison.Source, _comparison.Target);
        Profile.CompareOptions.ApplyTo(comparison.Options, new List<string>());
        foreach (var ex in exclusions)
        {
            comparison.ExcludedSourceObjects.Add(ex);
            comparison.ExcludedTargetObjects.Add(ex);
        }
        var result = comparison.Compare();
        return new CompareSession(Profile, comparison, result, Array.Empty<string>());
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

/// <summary>
/// <see cref="CompareSession.ResolveInclusion"/> 的結果：實際要部署的完整差異集（含 DacFx 為部署安全
/// 自動補入的相依物件），並保留使用者原始勾選名稱，可分辨「勾選」與「相依補入」。尚未提交至工作階段。
/// </summary>
public sealed class InclusionResult
{
    internal SchemaComparison Comparison { get; init; } = null!;
    internal SchemaComparisonResult Result { get; init; } = null!;
    internal List<ObjectDifference> Differences { get; init; } = null!;
    internal IReadOnlyList<SchemaComparisonExcludedObjectId> Exclusions { get; init; }
        = Array.Empty<SchemaComparisonExcludedObjectId>();
    internal HashSet<string> PickedNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 解析後實際要部署的差異（僅含 <see cref="ObjectDifference.Included"/> 者）。
    /// <see cref="Differences"/> 保有比對回傳的全部差異節點（含未納入者），但只有納入部署的才是「會被產出的物件」，
    /// 故對外計數一律以 Included 為準——否則會把「沒被排除掉的全部差異」誤當成相依補入而嚴重灌水。
    /// </summary>
    public IReadOnlyList<ObjectDifference> All => Differences.Where(d => d.Included).ToList();

    /// <summary>此差異是否為使用者親自勾選（否則為相依自動補入）。</summary>
    public bool WasPicked(ObjectDifference d) => PickedNames.Contains(d.Name);

    /// <summary>使用者親自勾選且納入部署的項目。</summary>
    public IReadOnlyList<ObjectDifference> Picked => All.Where(WasPicked).ToList();

    /// <summary>因相依自動補入（使用者未勾但納入部署）的項目。</summary>
    public IReadOnlyList<ObjectDifference> Dependencies => All.Where(d => !WasPicked(d)).ToList();
}
