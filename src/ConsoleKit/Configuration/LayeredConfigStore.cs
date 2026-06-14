using System.Text;
using System.Text.Json;

namespace ConsoleKit.Configuration;

/// <summary>
/// 泛型分層設定載入器：從起始目錄往上探索專案層設定檔，另載入全域設定，並依注入的 merge 合併。
/// 不讀 AppInfo、不含領域知識，可獨立單元測試。合併語義（如「專案層覆寫全域」）由 <c>merge</c> 委派決定。
/// </summary>
public sealed class LayeredConfigStore<TConfig> where TConfig : class
{
    private readonly LayeredConfigOptions _options;
    private readonly Func<TConfig> _createEmpty;
    private readonly Func<TConfig?, TConfig?, TConfig> _merge;

    public LayeredConfigStore(
        LayeredConfigOptions options,
        Func<TConfig> createEmpty,
        Func<TConfig?, TConfig?, TConfig> merge)
    {
        _options = options;
        _createEmpty = createEmpty;
        _merge = merge;
    }

    public string GlobalConfigPath => _options.GlobalConfigPath;

    /// <summary>從指定起始目錄（預設 CWD）探索並載入設定，回傳獨立快照。</summary>
    public LayeredConfigSnapshot<TConfig> Discover(string? startDir = null)
    {
        startDir ??= Directory.GetCurrentDirectory();

        var projectPath = FindProjectConfig(startDir);
        var global = LoadFile(_options.GlobalConfigPath);
        var project = projectPath is null ? null : LoadFile(projectPath);
        var effective = _merge(global, project);

        return new LayeredConfigSnapshot<TConfig>(this)
        {
            ProjectConfigPath = projectPath,
            GlobalConfigPath = _options.GlobalConfigPath,
            GlobalConfig = global,
            ProjectConfig = project,
            Effective = effective,
        };
    }

    public string Serialize(TConfig config) => JsonSerializer.Serialize(config, _options.JsonOptions);

    /// <summary>儲存到專案層設定檔；existingPath 為 null 時建立於 targetDir（或 CWD）。回傳寫入路徑。</summary>
    internal string SaveProject(string? existingPath, TConfig config, string? targetDir)
    {
        var path = existingPath
                   ?? Path.Combine(targetDir ?? Directory.GetCurrentDirectory(), _options.ProjectFileName);
        WriteAllText(path, config);
        return path;
    }

    /// <summary>儲存到全域設定檔。回傳寫入路徑。</summary>
    internal string SaveGlobal(TConfig config)
    {
        WriteAllText(_options.GlobalConfigPath, config);
        return _options.GlobalConfigPath;
    }

    private void WriteAllText(string path, TConfig config)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Serialize(config), new UTF8Encoding(false));
    }

    private string? FindProjectConfig(string startDir)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDir));
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, _options.ProjectFileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private TConfig? LoadFile(string path)
    {
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return _createEmpty();
        try
        {
            return JsonSerializer.Deserialize<TConfig>(json, _options.JsonOptions) ?? _createEmpty();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"設定檔格式錯誤：{path}\n{ex.Message}", ex);
        }
    }
}
