using System.Text.Json;

namespace ConsoleKit.Configuration;

/// <summary>
/// 分層設定載入的參數（由領域端組裝注入，骨架不讀 AppInfo）。
/// ProjectFileName：往上層目錄探索的專案層檔名；GlobalConfigPath：全域設定檔完整路徑；
/// JsonOptions：序列化／反序列化設定（由領域決定縮排、null 處理、跳脫等）。
/// </summary>
public sealed class LayeredConfigOptions
{
    public required string ProjectFileName { get; init; }
    public required string GlobalConfigPath { get; init; }
    public required JsonSerializerOptions JsonOptions { get; init; }
}
