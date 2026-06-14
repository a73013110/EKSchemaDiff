using System.Runtime.CompilerServices;

namespace ConsoleKit.Diagnostics;

/// <summary>
/// 應用程式記錄行為的抽象。實作（如 FileAppLog）負責落地與生命週期。
/// 注入此介面的型別只關心「記錄什麼」，不關心「寫到哪、何時初始化」。
/// </summary>
public interface IAppLog
{
    /// <summary>目前記錄檔的完整路徑（無法寫檔或尚未初始化時為 null）。</summary>
    string? FilePath { get; }

    void Info(string message);
    void Warn(string message);
    void Debug(string message);

    /// <summary>記錄一段流程的進入點（含呼叫端方法名），方便追出在哪一步崩潰。</summary>
    void Step(string message, [CallerMemberName] string member = "");

    /// <summary>記錄錯誤，並（若有）攤平例外的 InnerException 鏈與堆疊。</summary>
    void Error(string message, Exception? ex = null);
}
