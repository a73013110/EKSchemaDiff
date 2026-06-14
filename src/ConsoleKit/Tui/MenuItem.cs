namespace ConsoleKit.Tui;

/// <summary>選單上的一個項目。Label 可含 Spectre markup；分隔列以 IsSeparator 標記、導覽時自動跳過。</summary>
public sealed class MenuItem
{
    public required string Label { get; init; }       // 可含 Spectre markup
    public string? Description { get; init; }          // 選中時顯示的中文說明
    public bool IsSeparator { get; init; }
}
