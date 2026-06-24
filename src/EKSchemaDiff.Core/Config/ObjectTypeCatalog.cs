using Microsoft.SqlServer.Dac;

namespace EKSchemaDiff.Core.Config;

/// <summary>
/// DacFx <see cref="ObjectType"/> 列舉的目錄來源：供「物件型別」勾選頁列出所有可排除的物件類型。
/// 名稱直接取自列舉，保證可被 <c>Enum.TryParse</c> 還原（不會落入無法辨識）。
/// 中文標題與「應用程式範圍／非應用程式範圍」分組屬 UI 層的覆蓋資訊，由 CLI 維護。
/// </summary>
public static class ObjectTypeCatalog
{
    /// <summary>所有 DacFx 物件類型的列舉名稱（依字母排序），供 UI 列出勾選清單。</summary>
    public static IReadOnlyList<string> AllNames { get; } =
        Enum.GetNames<ObjectType>().OrderBy(n => n, StringComparer.Ordinal).ToArray();
}
