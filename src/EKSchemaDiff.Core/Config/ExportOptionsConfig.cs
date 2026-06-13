using System.Text.Json.Serialization;

namespace EKSchemaDiff.Core.Config;

/// <summary>部署 SQL 的輸出形式。</summary>
public enum DeployScriptMode
{
    /// <summary>只輸出單一完整檔 FullScript.sql。</summary>
    Single,
    /// <summary>只輸出依相依順序切分的個別編號檔。</summary>
    SplitOrdered,
    /// <summary>同時輸出單一檔與切分檔（預設）。</summary>
    Both,
}

/// <summary>輸出選項。</summary>
public sealed class ExportOptionsConfig
{
    [JsonPropertyName("deployScript")]
    [JsonConverter(typeof(JsonStringEnumConverter<DeployScriptMode>))]
    public DeployScriptMode DeployScript { get; set; } = DeployScriptMode.Both;

    /// <summary>是否輸出暖色系差異 HTML 報告。</summary>
    [JsonPropertyName("exportHtml")]
    public bool ExportHtml { get; set; } = true;

    /// <summary>HTML 差異是否忽略空白（影響逐行比對著色，與比對選項獨立）。</summary>
    [JsonPropertyName("htmlIgnoreWhitespace")]
    public bool HtmlIgnoreWhitespace { get; set; } = false;

    /// <summary>
    /// 部署腳本與切分檔頂端 USE 要使用的資料庫名稱（覆寫）。
    /// 留空時沿用目標資料庫名稱。適用情境：你內部目標庫叫 A，但客戶端實際庫名是 B，
    /// 交付給客戶執行的腳本要 USE [B]。
    /// </summary>
    [JsonPropertyName("deployDatabaseName")]
    public string? DeployDatabaseName { get; set; }

    public ExportOptionsConfig Clone() => new()
    {
        DeployScript = DeployScript,
        ExportHtml = ExportHtml,
        HtmlIgnoreWhitespace = HtmlIgnoreWhitespace,
        DeployDatabaseName = DeployDatabaseName,
    };
}
