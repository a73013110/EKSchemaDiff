using System.Text.Json.Serialization;

namespace EKSchemaDiff.Core.Config;

/// <summary>設定檔根結構（.eksd.json）。</summary>
public sealed class EksdConfig
{
    /// <summary>設定檔結構版本。比對／輸出選項採分組格式（safety/comparison、deploySql/htmlReport）。</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 2;

    /// <summary>未指定 --profile 且只有一組時的預設 profile 名稱。</summary>
    [JsonPropertyName("defaultProfile")]
    public string? DefaultProfile { get; set; }

    [JsonPropertyName("profiles")]
    public List<Profile> Profiles { get; set; } = new();

    public Profile? FindProfile(string name) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
