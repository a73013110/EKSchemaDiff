using Spectre.Console;

namespace ConsoleKit.Tui;

/// <summary>互動式文字輸入的純靜態輔助（中性，不含品牌文字）。逐欄輸入請用 ConsoleUI.ReadLineOrEsc。</summary>
public static class TextInput
{
    /// <summary>遮蔽輸入（密碼等）。promptMarkup 為完整提示字串（可含 markup，由呼叫端組裝與逸出）。</summary>
    public static string PromptSecret(string promptMarkup) =>
        AnsiConsole.Prompt(new TextPrompt<string>(promptMarkup).Secret());
}
