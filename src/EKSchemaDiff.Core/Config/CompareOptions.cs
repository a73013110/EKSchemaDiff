using System.Text.Json.Serialization;
using Microsoft.SqlServer.Dac;

namespace EKSchemaDiff.Core.Config;

/// <summary>
/// 比對／部署選項，對應 DacFx 的 DacDeployOptions。預設值以「安全發版」為準。
/// 依用途分組：<see cref="Safety"/>（防誤刪）、<see cref="Comparison"/>（比對範圍／行為）、
/// 以及整類排除的 <see cref="ExcludedObjectTypes"/>。
/// 最關鍵的是 <see cref="SafetyOptions.IgnorePermissions"/>：杜絕「用無權限的開發庫比對正式庫，
/// 產出移除權限 SQL」的事故。
/// </summary>
public sealed class CompareOptions
{
    /// <summary>安全防護：避免動到權限、誤刪物件、遺失資料。</summary>
    [JsonPropertyName("safety")]
    public SafetyOptions Safety { get; set; } = new();

    /// <summary>比對範圍與行為：要不要忽略某些差異。</summary>
    [JsonPropertyName("comparison")]
    public ComparisonOptions Comparison { get; set; } = new();

    /// <summary>
    /// 整類排除的物件類型（雙保險）。字串對應 DacFx ObjectType，如 "Permissions"、"Users"、"Logins"。
    /// 預設排除權限與帳號相關類型，徹底避免動到權限（對齊 VS：權限/使用者/登入/角色成員/資料庫角色/應用程式角色/認證取消勾選）。
    /// </summary>
    [JsonPropertyName("excludedObjectTypes")]
    public List<string> ExcludedObjectTypes { get; set; } = new()
    {
        "Permissions", "Users", "Logins", "RoleMembership", "Credentials",
        "DatabaseRoles", "ApplicationRoles"
    };

    /// <summary>
    /// 將設定套用到 DacFx 的 DacDeployOptions。
    /// 設定中無法辨識的排除類型名稱會收集到 <paramref name="unrecognized"/>（傳入時）供警示。
    /// </summary>
    public void ApplyTo(DacDeployOptions options, List<string>? unrecognized = null)
    {
        options.IgnorePermissions = Safety.IgnorePermissions;
        options.DropPermissionsNotInSource = Safety.DropPermissionsNotInSource;
        options.DropObjectsNotInSource = Safety.DropObjectsNotInSource;
        options.BlockOnPossibleDataLoss = Safety.BlockOnPossibleDataLoss;

        // DacFx 對「目標有、來源沒有」的子元素（約束／索引／DML 觸發程序／統計／擴充屬性）預設一律 DROP——
        // 這幾個 Drop*NotInSource 選項預設皆為 true，且與 DropObjectsNotInSource（預設 false）是各自獨立的旗標。
        // 若不一併關閉，會發生「明明沒勾某張表，卻因它在目標多了幾個 default 約束，就被產生 DROP CONSTRAINT」的
        // 範圍外誤刪——而且這是「全域部署選項」，連單物件腳本也會夾帶（與哪個 diff 被 Included 無關）。
        // 故統一綁定到 DropObjectsNotInSource：使用者選擇「不刪來源沒有的物件」時，連同其子元素也一律不刪。
        options.DropConstraintsNotInSource = Safety.DropObjectsNotInSource;
        options.DropIndexesNotInSource = Safety.DropObjectsNotInSource;
        options.DropDmlTriggersNotInSource = Safety.DropObjectsNotInSource;
        options.DropStatisticsNotInSource = Safety.DropObjectsNotInSource;
        options.DropExtendedPropertiesNotInSource = Safety.DropObjectsNotInSource;

        options.IgnoreRoleMembership = Comparison.IgnoreRoleMembership;
        options.IgnoreLoginSids = Comparison.IgnoreLoginSids;
        options.IgnoreExtendedProperties = Comparison.IgnoreExtendedProperties;
        options.IgnoreWhitespace = Comparison.IgnoreWhitespace;
        options.IgnoreKeywordCasing = Comparison.IgnoreKeywordCasing;
        options.IgnoreColumnOrder = Comparison.IgnoreColumnOrder;

        options.ExcludeObjectTypes = ResolveExcludedObjectTypes(unrecognized);
    }

    /// <summary>把字串清單轉成 DacFx ObjectType[]；無法辨識的名稱會加入 <paramref name="unrecognized"/> 供警示。</summary>
    private ObjectType[] ResolveExcludedObjectTypes(List<string>? unrecognized = null)
    {
        var result = new List<ObjectType>();
        foreach (var name in ExcludedObjectTypes)
        {
            if (Enum.TryParse<ObjectType>(name, ignoreCase: true, out var ot))
                result.Add(ot);
            else
                unrecognized?.Add(name);
        }
        return result.ToArray();
    }

    public CompareOptions Clone() => new()
    {
        Safety = Safety.Clone(),
        Comparison = Comparison.Clone(),
        ExcludedObjectTypes = new List<string>(ExcludedObjectTypes),
    };
}

/// <summary>安全防護選項（防誤刪）。</summary>
public sealed class SafetyOptions
{
    /// <summary>不比對/不產生 GRANT/DENY/REVOKE。預設 true（安全）。</summary>
    [JsonPropertyName("ignorePermissions")]
    public bool IgnorePermissions { get; set; } = true;

    /// <summary>即使比對權限，也不刪除目標多出的權限。預設 false。</summary>
    [JsonPropertyName("dropPermissionsNotInSource")]
    public bool DropPermissionsNotInSource { get; set; } = false;

    /// <summary>刪除目標中來源沒有的物件。預設 false（避免誤刪）。</summary>
    [JsonPropertyName("dropObjectsNotInSource")]
    public bool DropObjectsNotInSource { get; set; } = false;

    /// <summary>可能造成資料遺失時阻擋部署。預設 true。</summary>
    [JsonPropertyName("blockOnPossibleDataLoss")]
    public bool BlockOnPossibleDataLoss { get; set; } = true;

    public SafetyOptions Clone() => new()
    {
        IgnorePermissions = IgnorePermissions,
        DropPermissionsNotInSource = DropPermissionsNotInSource,
        DropObjectsNotInSource = DropObjectsNotInSource,
        BlockOnPossibleDataLoss = BlockOnPossibleDataLoss,
    };
}

/// <summary>比對範圍與行為選項。</summary>
public sealed class ComparisonOptions
{
    /// <summary>忽略角色成員資格。預設 true。</summary>
    [JsonPropertyName("ignoreRoleMembership")]
    public bool IgnoreRoleMembership { get; set; } = true;

    /// <summary>忽略登入 SID。預設 true。</summary>
    [JsonPropertyName("ignoreLoginSids")]
    public bool IgnoreLoginSids { get; set; } = true;

    /// <summary>忽略擴充屬性。預設 false —— 我們要比對 MS_Description（資料表/欄位描述）。</summary>
    [JsonPropertyName("ignoreExtendedProperties")]
    public bool IgnoreExtendedProperties { get; set; } = false;

    /// <summary>比對時忽略空白差異。預設 true。</summary>
    [JsonPropertyName("ignoreWhitespace")]
    public bool IgnoreWhitespace { get; set; } = true;

    /// <summary>忽略 T-SQL 關鍵字的大小寫差異（CREATE vs create）。預設 true。</summary>
    [JsonPropertyName("ignoreKeywordCasing")]
    public bool IgnoreKeywordCasing { get; set; } = true;

    /// <summary>
    /// 忽略資料表欄位的實體排列順序。預設 false（與 VS 相同）。
    /// 開啟後在資料表中間插欄不會產生「建暫存表→搬資料→drop→rename」的整表重建，改為單純 ALTER TABLE ADD。
    /// 代價：目標表新欄一律接在尾端，欄位實體順序可能與來源不一致。
    /// </summary>
    [JsonPropertyName("ignoreColumnOrder")]
    public bool IgnoreColumnOrder { get; set; } = false;

    public ComparisonOptions Clone() => new()
    {
        IgnoreRoleMembership = IgnoreRoleMembership,
        IgnoreLoginSids = IgnoreLoginSids,
        IgnoreExtendedProperties = IgnoreExtendedProperties,
        IgnoreWhitespace = IgnoreWhitespace,
        IgnoreKeywordCasing = IgnoreKeywordCasing,
        IgnoreColumnOrder = IgnoreColumnOrder,
    };
}
