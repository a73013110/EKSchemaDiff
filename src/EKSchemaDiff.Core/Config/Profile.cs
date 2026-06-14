using System.Text.Json.Serialization;

namespace EKSchemaDiff.Core.Config;

/// <summary>
/// 一組具名的比對情境：來源 → 目標、輸出位置、比對選項、輸出選項。
/// 例：uat2prod（Sample_DB_UAT → Sample_DB_PROD）。
/// </summary>
public sealed class Profile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>來源資料庫（更版內容的依據；差異報告左側）。</summary>
    [JsonPropertyName("source")]
    public ConnectionConfig Source { get; set; } = new();

    /// <summary>目標資料庫（被更新對象；差異報告右側）。</summary>
    [JsonPropertyName("target")]
    public ConnectionConfig Target { get; set; } = new();

    /// <summary>輸出根目錄。可為相對路徑（相對於設定檔所在目錄）。</summary>
    [JsonPropertyName("outputDir")]
    public string OutputDir { get; set; } = "比對結果";

    [JsonPropertyName("compareOptions")]
    public CompareOptionsConfig CompareOptions { get; set; } = new();

    [JsonPropertyName("exportOptions")]
    public ExportOptionsConfig ExportOptions { get; set; } = new();

    /// <summary>
    /// 解析部署腳本／逐物件部署檔頂端 USE 要用的資料庫名稱：
    /// 優先用 ExportOptions.DeployDatabaseName（覆寫），否則用目標資料庫名稱。
    /// </summary>
    public string ResolveDeployDatabaseName()
    {
        var overrideName = ExportOptions.DeployDatabaseName;
        if (!string.IsNullOrWhiteSpace(overrideName)) return overrideName.Trim();
        var targetDb = Target.ResolveDatabaseName();
        return string.IsNullOrWhiteSpace(targetDb) ? "TargetDatabase" : targetDb;
    }
}
