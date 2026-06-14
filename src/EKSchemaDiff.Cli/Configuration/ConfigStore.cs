using ConsoleKit.Configuration;
using EKSchemaDiff.Core.Config;

namespace EKSchemaDiff.Cli.Configuration;

/// <summary>
/// EKSchemaDiff 的設定探索快照（領域包裝）：在 ConsoleKit 的泛型 <see cref="LayeredConfigSnapshot{TConfig}"/>
/// 之上補充領域 API（ResolveProfile）。由 <see cref="ConfigStoreFactory"/> 建立；
/// startDir 為執行命令後才取得的參數，故本型別不註冊 DI，而是每次 Discover 取得獨立快照。
/// </summary>
public sealed class ConfigStore
{
    private readonly LayeredConfigSnapshot<EksdConfig> _snapshot;

    internal ConfigStore(LayeredConfigSnapshot<EksdConfig> snapshot) => _snapshot = snapshot;

    /// <summary>找到的專案層設定檔路徑（可能為 null）。</summary>
    public string? ProjectConfigPath => _snapshot.ProjectConfigPath;

    /// <summary>全域設定檔路徑。</summary>
    public string GlobalConfigPath => _snapshot.GlobalConfigPath;

    /// <summary>合併後的有效設定（全域 + 專案層覆寫）。</summary>
    public EksdConfig Effective => _snapshot.Effective;

    public EksdConfig? ProjectConfig => _snapshot.ProjectConfig;
    public EksdConfig? GlobalConfig => _snapshot.GlobalConfig;

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
    public string SaveProject(EksdConfig config, string? targetDir = null) => _snapshot.SaveProject(config, targetDir);

    /// <summary>儲存到全域設定檔。</summary>
    public string SaveGlobal(EksdConfig config) => _snapshot.SaveGlobal(config);
}
