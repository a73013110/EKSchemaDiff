namespace EKSchemaDiff.Cli;

/// <summary>
/// EKSchemaDiff 領域專屬結束碼，補充 ConsoleKit.ExitCode 的通用碼（Ok=0/UsageError=1/SoftwareError=70）。
/// 供 CI 依結束碼判斷各階段結果。
/// </summary>
public static class EksdExitCode
{
    /// <summary>比對階段失敗（連線或比對結果無效）。</summary>
    public const int CompareFailed = 2;

    /// <summary>匯出階段失敗。</summary>
    public const int ExportFailed = 3;

    /// <summary>逐物件部署檔整理驗證未通過。</summary>
    public const int VerificationFailed = 4;
}
