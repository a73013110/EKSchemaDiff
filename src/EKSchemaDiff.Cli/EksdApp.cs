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
        PrimaryTagline = "SQL Server 結構比對",
        SecondaryTagline = "發版差異 CLI",
        Author = "Yikai",
        ProjectConfigFileName = ".eksd.json",
        GlobalConfigSubdir = ".eksd",
        GlobalConfigFileName = "config.json",
        LogDirEnvVar = "EKSD_LOG_DIR",
        LogFilePrefix = "eksd",
    };
}
