using System.Text.RegularExpressions;

namespace EKSchemaDiff.Report;

public enum DiffKind { Same, Modified, Added, Removed }

public sealed class DiffRow
{
    public int LeftNumber { get; init; }
    public int RightNumber { get; init; }
    public string Left { get; init; } = string.Empty;
    public string Right { get; init; } = string.Empty;
    public DiffKind Kind { get; init; }
}

/// <summary>
/// 逐行 LCS 差異引擎。移植自原 4.產生差異比對.ps1 內嵌的 SqlReleaseDiff.DiffEngine，行為一致。
/// 左 = 更版（來源），右 = 原版（目標）。
/// </summary>
public static class DiffEngine
{
    private sealed class Op
    {
        public string Kind = "";
        public string Text = "";
        public int Number;
    }

    private static string[] Lines(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
        return normalized.Length == 0 ? Array.Empty<string>() : normalized.Split('\n');
    }

    private static string Key(string text, bool ignoreWhitespace) =>
        ignoreWhitespace ? Regex.Replace(text.Trim(), @"\s+", " ") : text;

    public static List<DiffRow> Compare(string leftText, string rightText, bool ignoreWhitespace)
    {
        var left = Lines(leftText);
        var right = Lines(rightText);
        var dp = new int[left.Length + 1, right.Length + 1];

        for (int i = left.Length - 1; i >= 0; i--)
            for (int j = right.Length - 1; j >= 0; j--)
                dp[i, j] = string.Equals(Key(left[i], ignoreWhitespace), Key(right[j], ignoreWhitespace), StringComparison.Ordinal)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var ops = new List<Op>();
        int x = 0, y = 0;
        while (x < left.Length || y < right.Length)
        {
            if (x < left.Length && y < right.Length &&
                string.Equals(Key(left[x], ignoreWhitespace), Key(right[y], ignoreWhitespace), StringComparison.Ordinal))
            {
                ops.Add(new Op { Kind = "Same", Text = left[x], Number = x + 1 });
                x++; y++;
            }
            else if (x < left.Length && (y >= right.Length || dp[x + 1, y] >= dp[x, y + 1]))
            {
                ops.Add(new Op { Kind = "Left", Text = left[x], Number = x + 1 });
                x++;
            }
            else
            {
                ops.Add(new Op { Kind = "Right", Text = right[y], Number = y + 1 });
                y++;
            }
        }

        var rows = new List<DiffRow>();
        int p = 0;
        int rightSameNumber = 0;
        while (p < ops.Count)
        {
            if (ops[p].Kind == "Same")
            {
                rightSameNumber++;
                rows.Add(new DiffRow
                {
                    LeftNumber = ops[p].Number,
                    RightNumber = rightSameNumber,
                    Left = ops[p].Text,
                    Right = ops[p].Text,
                    Kind = DiffKind.Same,
                });
                p++;
                continue;
            }

            var leftBlock = new List<Op>();
            var rightBlock = new List<Op>();
            while (p < ops.Count && ops[p].Kind != "Same")
            {
                if (ops[p].Kind == "Left") leftBlock.Add(ops[p]);
                else rightBlock.Add(ops[p]);
                p++;
            }

            int blockLength = Math.Max(leftBlock.Count, rightBlock.Count);
            for (int z = 0; z < blockLength; z++)
            {
                Op? leftOp = z < leftBlock.Count ? leftBlock[z] : null;
                Op? rightOp = z < rightBlock.Count ? rightBlock[z] : null;
                if (rightOp is not null) rightSameNumber = rightOp.Number;

                rows.Add(new DiffRow
                {
                    LeftNumber = leftOp?.Number ?? 0,
                    RightNumber = rightOp?.Number ?? 0,
                    Left = leftOp?.Text ?? "",
                    Right = rightOp?.Text ?? "",
                    Kind = leftOp is not null && rightOp is not null
                        ? DiffKind.Modified
                        : (leftOp is not null ? DiffKind.Added : DiffKind.Removed),
                });
            }
        }

        return rows;
    }
}
