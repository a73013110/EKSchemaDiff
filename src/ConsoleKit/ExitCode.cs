namespace ConsoleKit;

/// <summary>
/// 通用結束碼常數，取代各處 magic number。領域專屬結束碼請於 CLI 端另建（如 EksdExitCode）。
/// 數值沿用慣例：0 成功、1 用法錯誤、70 程式內部錯誤（EX_SOFTWARE）。
/// </summary>
public static class ExitCode
{
    /// <summary>成功。</summary>
    public const int Ok = 0;

    /// <summary>用法／設定錯誤（使用者可修正）。</summary>
    public const int UsageError = 1;

    /// <summary>程式內部未預期錯誤（EX_SOFTWARE）。</summary>
    public const int SoftwareError = 70;
}
