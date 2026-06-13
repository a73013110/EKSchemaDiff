using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace EKSchemaDiff.Core.Config;

/// <summary>
/// 單一資料庫連線設定。可用完整連線字串，或以離散欄位描述。
/// 安全原則：不建議在 commit 的設定檔放明碼密碼；改用整合驗證或 ${env:VAR} 插值，或執行時互動輸入。
/// </summary>
public sealed class ConnectionConfig
{
    /// <summary>顯示用標籤（如「UAT 來源」）。</summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>完整連線字串。提供時優先使用，其餘離散欄位忽略。可含 ${env:VAR}。</summary>
    [JsonPropertyName("connectionString")]
    public string? ConnectionString { get; set; }

    [JsonPropertyName("server")]
    public string? Server { get; set; }

    [JsonPropertyName("database")]
    public string? Database { get; set; }

    /// <summary>驗證方式：integrated（Windows 整合驗證，預設）或 sql。</summary>
    [JsonPropertyName("auth")]
    public string Auth { get; set; } = "integrated";

    [JsonPropertyName("user")]
    public string? User { get; set; }

    /// <summary>密碼。建議用 ${env:VAR}，勿放明碼。空白時視 auth 與互動模式而定。</summary>
    [JsonPropertyName("password")]
    public string? Password { get; set; }

    /// <summary>是否信任伺服器憑證（內網自簽常見）。預設 true。</summary>
    [JsonPropertyName("trustServerCertificate")]
    public bool TrustServerCertificate { get; set; } = true;

    [JsonPropertyName("connectTimeoutSeconds")]
    public int ConnectTimeoutSeconds { get; set; } = 30;

    [JsonIgnore]
    public bool IsSqlAuth => string.Equals(Auth, "sql", StringComparison.OrdinalIgnoreCase);

    /// <summary>取得目標資料庫名稱（供 DacFx 部署腳本標頭使用）。</summary>
    public string ResolveDatabaseName()
    {
        if (!string.IsNullOrWhiteSpace(Database)) return Database!;
        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            var b = new SqlConnectionStringBuilder(EnvInterpolation.Expand(ConnectionString!));
            if (!string.IsNullOrWhiteSpace(b.InitialCatalog)) return b.InitialCatalog;
        }
        return string.Empty;
    }

    /// <summary>
    /// 組出實際連線字串。會展開 ${env:VAR}；SQL 驗證缺密碼時呼叫 passwordPrompt 取得。
    /// </summary>
    public string BuildConnectionString(Func<string, string>? passwordPrompt = null)
    {
        SqlConnectionStringBuilder builder;
        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            builder = new SqlConnectionStringBuilder(EnvInterpolation.Expand(ConnectionString!));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Server))
                throw new InvalidOperationException("連線設定缺少 server（或 connectionString）。");
            if (string.IsNullOrWhiteSpace(Database))
                throw new InvalidOperationException("連線設定缺少 database（或 connectionString）。");

            builder = new SqlConnectionStringBuilder
            {
                DataSource = EnvInterpolation.Expand(Server!),
                InitialCatalog = EnvInterpolation.Expand(Database!),
            };

            if (IsSqlAuth)
            {
                builder.IntegratedSecurity = false;
                builder.UserID = EnvInterpolation.Expand(User ?? string.Empty);
                var pwd = EnvInterpolation.Expand(Password ?? string.Empty);
                if (string.IsNullOrEmpty(pwd) && passwordPrompt is not null)
                    pwd = passwordPrompt($"{builder.DataSource}/{builder.InitialCatalog} 的密碼");
                builder.Password = pwd;
            }
            else
            {
                builder.IntegratedSecurity = true;
            }
        }

        builder.TrustServerCertificate = TrustServerCertificate;
        if (builder.ConnectTimeout == 15) // SqlClient 預設值，未顯式設定時才覆寫
            builder.ConnectTimeout = ConnectTimeoutSeconds;

        return builder.ConnectionString;
    }

    /// <summary>遮蔽密碼後的安全顯示字串。</summary>
    public string ToSafeDisplay()
    {
        var db = ResolveDatabaseName();
        var srv = !string.IsNullOrWhiteSpace(Server)
            ? Server
            : (!string.IsNullOrWhiteSpace(ConnectionString)
                ? new SqlConnectionStringBuilder(EnvInterpolation.Expand(ConnectionString!)).DataSource
                : "?");
        var auth = IsSqlAuth ? $"sql:{User}" : "integrated";
        return $"{srv} / {db} ({auth})";
    }
}
