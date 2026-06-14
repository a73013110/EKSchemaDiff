using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConsoleKit;
using ConsoleKit.Configuration;
using EKSchemaDiff.Core.Config;

namespace EKSchemaDiff.Cli.Configuration;

/// <summary>
/// 設定探索的工廠（DI singleton）。封裝 EksdConfig 的 JSON 設定與合併語義，
/// 每次 <see cref="Discover"/> 回傳獨立的 <see cref="ConfigStore"/> 快照。
/// 序列化與品牌檔名集中於此（取自注入的 <see cref="AppInfo"/>）。
/// </summary>
public sealed class ConfigStoreFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly LayeredConfigStore<EksdConfig> _store;

    public ConfigStoreFactory(AppInfo app)
    {
        var options = new LayeredConfigOptions
        {
            ProjectFileName = app.ProjectConfigFileName,
            GlobalConfigPath = app.GlobalConfigPath,
            JsonOptions = JsonOptions,
        };
        _store = new LayeredConfigStore<EksdConfig>(options, () => new EksdConfig(), Merge);
    }

    /// <summary>從指定起始目錄（預設 CWD）探索並載入設定，回傳獨立快照。</summary>
    public ConfigStore Discover(string? startDir = null) => new(_store.Discover(startDir));

    /// <summary>序列化設定（供 init 範本等用）。</summary>
    public string Serialize(EksdConfig config) => _store.Serialize(config);

    /// <summary>合併：全域為底、專案層同名 profile 覆寫；defaultProfile 專案層優先。</summary>
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
}
