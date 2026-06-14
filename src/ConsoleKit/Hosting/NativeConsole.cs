using System.Runtime.InteropServices;
using System.Text;

namespace ConsoleKit.Hosting;

/// <summary>
/// 主控台原生互通：在 Windows 啟用 VT（ENABLE_VIRTUAL_TERMINAL_PROCESSING）並設 UTF-8，
/// 讓自繪畫面用的 ESC[H/ESC[K/ESC[0J 控制碼與中文都能正確輸出。
/// P/Invoke 原生常數沿用 Win32 的 SCREAMING_CASE 命名（文件化例外）。
/// </summary>
public static class NativeConsole
{
    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    /// <summary>設 UTF-8 輸出並在 Windows 啟用 VT 處理。非主控台或無權限時靜默忽略。</summary>
    public static void EnableVirtualTerminal()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 重導向時忽略 */ }

        if (!OperatingSystem.IsWindows()) return;
        try
        {
            nint handle = GetStdHandle(STD_OUTPUT_HANDLE);
            if (handle == nint.Zero || handle == (nint)(-1)) return;
            if (GetConsoleMode(handle, out uint mode))
                SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
        }
        catch { /* 非主控台或無權限時忽略 */ }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);
}
