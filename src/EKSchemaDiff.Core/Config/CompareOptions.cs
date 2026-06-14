using System.Text.Json.Serialization;
using Microsoft.SqlServer.Dac;

namespace EKSchemaDiff.Core.Config;

/// <summary>
/// 比對／部署選項，對應 DacFx 的 DacDeployOptions。預設值以「安全發版」為準。
/// 最關鍵的是 IgnorePermissions：杜絕「用無權限的開發庫比對正式庫，產出移除權限 SQL」的事故。
/// </summary>
public sealed class CompareOptions
{
    /// <summary>不比對/不產生 GRANT/DENY/REVOKE。預設 true（安全）。</summary>
    [JsonPropertyName("ignorePermissions")]
    public bool IgnorePermissions { get; set; } = true;

    /// <summary>即使比對權限，也不刪除目標多出的權限。預設 false。</summary>
    [JsonPropertyName("dropPermissionsNotInSource")]
    public bool DropPermissionsNotInSource { get; set; } = false;

    /// <summary>忽略角色成員資格。預設 true。</summary>
    [JsonPropertyName("ignoreRoleMembership")]
    public bool IgnoreRoleMembership { get; set; } = true;

    /// <summary>忽略登入 SID。預設 true。</summary>
    [JsonPropertyName("ignoreLoginSids")]
    public bool IgnoreLoginSids { get; set; } = true;

    /// <summary>忽略擴充屬性。預設 false —— 我們要比對 MS_Description（資料表/欄位描述）。</summary>
    [JsonPropertyName("ignoreExtendedProperties")]
    public bool IgnoreExtendedProperties { get; set; } = false;

    /// <summary>可能造成資料遺失時阻擋部署。預設 true。</summary>
    [JsonPropertyName("blockOnPossibleDataLoss")]
    public bool BlockOnPossibleDataLoss { get; set; } = true;

    /// <summary>刪除目標中來源沒有的物件。預設 false（避免誤刪）。</summary>
    [JsonPropertyName("dropObjectsNotInSource")]
    public bool DropObjectsNotInSource { get; set; } = false;

    /// <summary>比對時忽略空白差異。預設 true。</summary>
    [JsonPropertyName("ignoreWhitespace")]
    public bool IgnoreWhitespace { get; set; } = true;

    /// <summary>忽略資料庫/伺服器層級的雜項設定差異。預設 true。</summary>
    [JsonPropertyName("ignoreKeywordCasing")]
    public bool IgnoreKeywordCasing { get; set; } = true;

    /// <summary>
    /// 整類排除的物件類型（雙保險）。字串對應 DacFx ObjectType，如 "Permissions"、"Users"、"Logins"、"RoleMembership"。
    /// 預設排除權限與帳號相關類型，徹底避免動到權限。
    /// </summary>
    [JsonPropertyName("excludedObjectTypes")]
    public List<string> ExcludedObjectTypes { get; set; } = new()
    {
        "Permissions", "Users", "Logins", "RoleMembership", "Credentials"
    };

    /// <summary>
    /// 將設定套用到 DacFx 的 DacDeployOptions。
    /// 設定中無法辨識的排除類型名稱會收集到 <paramref name="unrecognized"/>（傳入時）供警示。
    /// </summary>
    public void ApplyTo(DacDeployOptions options, List<string>? unrecognized = null)
    {
        options.IgnorePermissions = IgnorePermissions;
        options.DropPermissionsNotInSource = DropPermissionsNotInSource;
        options.IgnoreRoleMembership = IgnoreRoleMembership;
        options.IgnoreLoginSids = IgnoreLoginSids;
        options.IgnoreExtendedProperties = IgnoreExtendedProperties;
        options.BlockOnPossibleDataLoss = BlockOnPossibleDataLoss;
        options.DropObjectsNotInSource = DropObjectsNotInSource;
        options.IgnoreWhitespace = IgnoreWhitespace;
        options.IgnoreKeywordCasing = IgnoreKeywordCasing;

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
        IgnorePermissions = IgnorePermissions,
        DropPermissionsNotInSource = DropPermissionsNotInSource,
        IgnoreRoleMembership = IgnoreRoleMembership,
        IgnoreLoginSids = IgnoreLoginSids,
        IgnoreExtendedProperties = IgnoreExtendedProperties,
        BlockOnPossibleDataLoss = BlockOnPossibleDataLoss,
        DropObjectsNotInSource = DropObjectsNotInSource,
        IgnoreWhitespace = IgnoreWhitespace,
        IgnoreKeywordCasing = IgnoreKeywordCasing,
        ExcludedObjectTypes = new List<string>(ExcludedObjectTypes),
    };
}
