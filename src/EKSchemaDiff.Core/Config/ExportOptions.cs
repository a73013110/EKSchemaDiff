using System.Text.Json.Serialization;

namespace EKSchemaDiff.Core.Config;

/// <summary>輸出選項。</summary>
public sealed class ExportOptions
{
    /// <summary>是否輸出單一完整部署腳本（完整部署腳本.sql，依相依順序的整批 DDL）。</summary>
    [JsonPropertyName("fullScript")]
    public bool FullScript { get; set; } = true;

    /// <summary>
    /// 是否輸出依相依順序編號的逐物件部署檔。
    /// 預設關閉：逐物件每件須單獨 GenerateScript（樞紐物件因相依刷新可達一分鐘／件），常態匯出毋須，
    /// 需要逐檔時再於設定開啟。
    /// </summary>
    [JsonPropertyName("perObjectScripts")]
    public bool PerObjectScripts { get; set; } = false;

    /// <summary>
    /// 是否輸出「完整還原腳本」（完整部署腳本的反向）：把來源/目標對調產生，
    /// 供完整部署腳本執行異常或需回版時，將目標還原回部署前的狀態。
    /// </summary>
    [JsonPropertyName("fullRollbackScript")]
    public bool FullRollbackScript { get; set; } = false;

    /// <summary>是否輸出暖色系差異 HTML 報告。</summary>
    [JsonPropertyName("exportHtml")]
    public bool ExportHtml { get; set; } = true;

    /// <summary>HTML 差異是否忽略空白（影響逐行比對著色，與比對選項獨立）。</summary>
    [JsonPropertyName("htmlIgnoreWhitespace")]
    public bool HtmlIgnoreWhitespace { get; set; } = false;

    /// <summary>
    /// 完整部署腳本與逐物件部署檔頂端 USE 要使用的資料庫名稱（覆寫）。
    /// 留空時沿用目標資料庫名稱。適用情境：你內部目標庫叫 A，但客戶端實際庫名是 B，
    /// 交付給客戶執行的腳本要 USE [B]。
    /// </summary>
    [JsonPropertyName("deployDatabaseName")]
    public string? DeployDatabaseName { get; set; }

    /// <summary>把目前會輸出的項目描述成一行（供日誌、情境摘要、profile 清單共用，用語一致）。</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (FullScript) parts.Add("完整部署腳本");
        if (FullRollbackScript) parts.Add("完整還原腳本");
        if (PerObjectScripts) parts.Add("逐物件部署檔");
        if (ExportHtml) parts.Add("差異 HTML");
        return parts.Count == 0 ? "(無輸出)" : string.Join(" + ", parts);
    }

    public ExportOptions Clone() => new()
    {
        FullScript = FullScript,
        PerObjectScripts = PerObjectScripts,
        FullRollbackScript = FullRollbackScript,
        ExportHtml = ExportHtml,
        HtmlIgnoreWhitespace = HtmlIgnoreWhitespace,
        DeployDatabaseName = DeployDatabaseName,
    };
}
