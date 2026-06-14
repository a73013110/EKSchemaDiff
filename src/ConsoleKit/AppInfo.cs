namespace ConsoleKit;

/// <summary>
/// 應用程式的品牌與路徑描述。以注入方式取得（DI singleton），取代寫死於各檔的品牌字串，
/// 讓 ConsoleKit 骨架保持中性：拆版到新 CLI 時只需提供一份新的 <see cref="AppInfo"/>。
/// 所有文字皆為純文字（不含 Spectre markup），顏色與排版由 Banner 等元件決定。
/// </summary>
public sealed record AppInfo
{
    /// <summary>可執行檔／命令名稱（如 myapp），用於命令列說明與提示。</summary>
    public required string ExecutableName { get; init; }

    /// <summary>顯示名稱（如 MyApp），用於 Banner 大字 Logo。</summary>
    public required string DisplayName { get; init; }

    /// <summary>主標語（顯示於 Banner 規則線）。</summary>
    public string PrimaryTagline { get; init; } = string.Empty;

    /// <summary>次標語（顯示於 Banner 規則線）。</summary>
    public string SecondaryTagline { get; init; } = string.Empty;

    /// <summary>作者署名。</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>專案層設定檔名（如 .myapp.json）。</summary>
    public string ProjectConfigFileName { get; init; } = string.Empty;

    /// <summary>全域設定的使用者目錄子資料夾（如 .myapp）。</summary>
    public string GlobalConfigSubdir { get; init; } = string.Empty;

    /// <summary>全域設定檔名（如 config.json）。</summary>
    public string GlobalConfigFileName { get; init; } = string.Empty;

    /// <summary>記錄目錄覆寫用的環境變數名（如 MYAPP_LOG_DIR）。</summary>
    public string LogDirEnvVar { get; init; } = string.Empty;

    /// <summary>記錄檔名前綴（如 myapp → myapp-yyyyMMdd.log）。預設沿用 <see cref="ExecutableName"/>。</summary>
    public string? LogFilePrefix { get; init; }

    /// <summary>全域設定檔完整路徑：%USERPROFILE%\&lt;GlobalConfigSubdir&gt;\&lt;GlobalConfigFileName&gt;。</summary>
    public string GlobalConfigPath
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, GlobalConfigSubdir, GlobalConfigFileName);
        }
    }

    /// <summary>
    /// 記錄目錄：優先用 <see cref="LogDirEnvVar"/> 環境變數覆寫，否則 %LOCALAPPDATA%\&lt;DisplayName&gt;\logs。
    /// </summary>
    public string LogDirectory
    {
        get
        {
            var overrideDir = string.IsNullOrWhiteSpace(LogDirEnvVar)
                ? null
                : Environment.GetEnvironmentVariable(LogDirEnvVar);
            if (!string.IsNullOrWhiteSpace(overrideDir)) return overrideDir;

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir)) baseDir = AppContext.BaseDirectory;
            return Path.Combine(baseDir, DisplayName, "logs");
        }
    }
}
