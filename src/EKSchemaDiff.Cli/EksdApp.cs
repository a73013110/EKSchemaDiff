using System.Reflection;

using ConsoleKit;

namespace EKSchemaDiff.Cli;

/// <summary>EKSchemaDiff 的品牌與路徑描述（提供給 ConsoleKit 骨架的唯一領域進入點）。</summary>
public static class EksdApp
{
    /// <summary>單一 AppInfo 實例，於 Program.cs 交給 ConsoleHost，並註冊為 DI singleton。</summary>
    public static readonly AppInfo Info = new()
    {
        ExecutableName = "eksd",
        DisplayName = "EKSchemaDiff",
        Wordmark = "EKSD",
        PrimaryTagline = "SQL Server 結構比對",
        SecondaryTagline = "發版差異 CLI",
        Author = "Yikai",
        Version = ResolveVersion(),
        ProjectConfigFileName = ".eksd.json",
        GlobalConfigSubdir = ".eksd",
        GlobalConfigFileName = "config.json",
        LogDirEnvVar = "EKSD_LOG_DIR",
        LogFilePrefix = "eksd",
    };

    /// <summary>
    /// 取得 MinVer 於 build 時寫入的組件版號（<c>AssemblyInformationalVersion</c>），
    /// 並切掉 <c>+git-hash</c> build metadata，只留乾淨版本（如 0.1.0、0.1.0-alpha.0.3）。
    /// 取不到時回傳空字串，Banner 隨之不顯示版號。
    /// </summary>
    private static string ResolveVersion()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info)) return string.Empty;

        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
