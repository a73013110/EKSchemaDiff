using System.Text.Json;

namespace EKSchemaDiff.Core.Config;

/// <summary>
/// 設定檔的探索、載入與儲存。
/// 探索順序：從起始目錄往上層找專案層 .eksd.json；另載入全域 %USERPROFILE%\.eksd\config.json。
/// 專案層 profile 覆寫同名全域 profile。
/// </summary>
public sealed class ConfigStore
{
    public const string ProjectFileName = ".eksd.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>找到的專案層設定檔路徑（可能為 null）。</summary>
    public string? ProjectConfigPath { get; private set; }

    /// <summary>全域設定檔路徑。</summary>
    public string GlobalConfigPath { get; }

    /// <summary>合併後的有效設定（全域 + 專案層覆寫）。</summary>
    public EksdConfig Effective { get; private set; } = new();

    public EksdConfig? ProjectConfig { get; private set; }
    public EksdConfig? GlobalConfig { get; private set; }

    private ConfigStore(string globalConfigPath)
    {
        GlobalConfigPath = globalConfigPath;
    }

    public static string DefaultGlobalPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".eksd", "config.json");
    }

    /// <summary>從指定起始目錄（預設 CWD）探索並載入設定。</summary>
    public static ConfigStore Discover(string? startDir = null, string? globalPath = null)
    {
        var store = new ConfigStore(globalPath ?? DefaultGlobalPath());
        startDir ??= Directory.GetCurrentDirectory();

        store.ProjectConfigPath = FindProjectConfig(startDir);
        store.GlobalConfig = LoadFile(store.GlobalConfigPath);
        store.ProjectConfig = store.ProjectConfigPath is null ? null : LoadFile(store.ProjectConfigPath);

        store.Effective = Merge(store.GlobalConfig, store.ProjectConfig);
        return store;
    }

    private static string? FindProjectConfig(string startDir)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDir));
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, ProjectFileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static EksdConfig? LoadFile(string path)
    {
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return new EksdConfig();
        try
        {
            return JsonSerializer.Deserialize<EksdConfig>(json, JsonOptions) ?? new EksdConfig();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"設定檔格式錯誤：{path}\n{ex.Message}", ex);
        }
    }

    private static EksdConfig Merge(EksdConfig? global, EksdConfig? project)
    {
        var merged = new EksdConfig();
        var byName = new Dictionary<string, Profile>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in global?.Profiles ?? Enumerable.Empty<Profile>())
            byName[p.Name] = p;
        foreach (var p in project?.Profiles ?? Enumerable.Empty<Profile>())
            byName[p.Name] = p; // 專案層覆寫

        merged.Profiles = byName.Values.ToList();
        merged.DefaultProfile = project?.DefaultProfile ?? global?.DefaultProfile;
        return merged;
    }

    /// <summary>
    /// 解析要使用的 profile：優先 --profile；否則用 defaultProfile；否則唯一一組時直接用。
    /// 都不符合時回傳 null（呼叫端應走互動挑選）。
    /// </summary>
    public Profile? ResolveProfile(string? requestedName)
    {
        if (!string.IsNullOrWhiteSpace(requestedName))
            return Effective.FindProfile(requestedName!)
                   ?? throw new InvalidOperationException($"找不到名為 '{requestedName}' 的 profile。");

        if (!string.IsNullOrWhiteSpace(Effective.DefaultProfile))
        {
            var d = Effective.FindProfile(Effective.DefaultProfile!);
            if (d is not null) return d;
        }

        return Effective.Profiles.Count == 1 ? Effective.Profiles[0] : null;
    }

    /// <summary>儲存到專案層設定檔（不存在時建立於 targetDir）。回傳寫入路徑。</summary>
    public string SaveProject(EksdConfig config, string? targetDir = null)
    {
        var path = ProjectConfigPath
                   ?? Path.Combine(targetDir ?? Directory.GetCurrentDirectory(), ProjectFileName);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions),
            new System.Text.UTF8Encoding(false));
        ProjectConfigPath = path;
        return path;
    }

    /// <summary>儲存到全域設定檔。</summary>
    public string SaveGlobal(EksdConfig config)
    {
        var dir = Path.GetDirectoryName(GlobalConfigPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(GlobalConfigPath, JsonSerializer.Serialize(config, JsonOptions),
            new System.Text.UTF8Encoding(false));
        return GlobalConfigPath;
    }

    public static string Serialize(EksdConfig config) => JsonSerializer.Serialize(config, JsonOptions);
}
