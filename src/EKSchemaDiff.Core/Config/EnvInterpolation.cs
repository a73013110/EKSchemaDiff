using System.Text.RegularExpressions;

namespace EKSchemaDiff.Core.Config;

/// <summary>
/// 將設定字串中的 ${env:VAR} 展開成環境變數值，避免在設定檔放明碼敏感資訊。
/// 找不到的環境變數會展開成空字串。
/// </summary>
public static partial class EnvInterpolation
{
    [GeneratedRegex(@"\$\{env:(?<name>[A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled)]
    private static partial Regex EnvPattern();

    public static string Expand(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
        return EnvPattern().Replace(input, m =>
            Environment.GetEnvironmentVariable(m.Groups["name"].Value) ?? string.Empty);
    }
}
