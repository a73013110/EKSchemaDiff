namespace ConsoleKit.Configuration;

/// <summary>
/// 一次探索的設定快照：專案層／全域原始設定、合併後的有效設定，以及對應的檔案路徑。
/// 存檔方法委派回建立它的 <see cref="LayeredConfigStore{TConfig}"/>；存到專案層後會更新 ProjectConfigPath。
/// </summary>
public sealed class LayeredConfigSnapshot<TConfig> where TConfig : class
{
    private readonly LayeredConfigStore<TConfig> _store;

    internal LayeredConfigSnapshot(LayeredConfigStore<TConfig> store) => _store = store;

    /// <summary>找到的專案層設定檔路徑（可能為 null）。</summary>
    public string? ProjectConfigPath { get; internal set; }

    /// <summary>全域設定檔路徑。</summary>
    public string GlobalConfigPath { get; internal init; } = string.Empty;

    /// <summary>合併後的有效設定。</summary>
    public TConfig Effective { get; internal set; } = default!;

    public TConfig? ProjectConfig { get; internal init; }
    public TConfig? GlobalConfig { get; internal init; }

    /// <summary>儲存到專案層設定檔（不存在時建立於 targetDir）。回傳寫入路徑。</summary>
    public string SaveProject(TConfig config, string? targetDir = null)
    {
        var path = _store.SaveProject(ProjectConfigPath, config, targetDir);
        ProjectConfigPath = path;
        return path;
    }

    /// <summary>儲存到全域設定檔。回傳寫入路徑。</summary>
    public string SaveGlobal(TConfig config) => _store.SaveGlobal(config);
}
