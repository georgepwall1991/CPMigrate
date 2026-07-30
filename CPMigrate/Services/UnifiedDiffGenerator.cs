using System.Text;

namespace CPMigrate.Services;

/// <summary>
/// Generates unified diff output for file changes, so a dry-run can show
/// exactly what will change in a format developers already read daily.
/// </summary>
internal static class UnifiedDiffGenerator
{
    public static string Generate(string? originalContent, string newContent, string filePath)
    {
        var originalLines = originalContent?.Split('\n') ?? Array.Empty<string>();
        var newLines = newContent.Split('\n');

        var sb = new StringBuilder();
        var fileName = Path.GetFileName(filePath);

        sb.AppendLine($"--- a/{fileName}");
        sb.AppendLine($"+++ b/{fileName}");

        if (originalLines.Length == 0)
        {
            sb.AppendLine($"@@ -0,0 +1,{newLines.Length} @@");
            foreach (var line in newLines)
            {
                sb.AppendLine($"+{line.TrimEnd('\r')}");
            }

            return sb.ToString();
        }

        var lcs = ComputeLcs(originalLines, newLines);
        var hunks = BuildHunks(originalLines, newLines, lcs);

        foreach (var hunk in hunks)
        {
            sb.AppendLine(hunk);
        }

        return sb.ToString();
    }

    private static bool[,] ComputeLcs(string[] a, string[] b)
    {
        var m = a.Length;
        var n = b.Length;
        var dp = new int[m + 1, n + 1];

        for (var i = m - 1; i >= 0; i--)
        {
            for (var j = n - 1; j >= 0; j--)
            {
                dp[i, j] = a[i].TrimEnd('\r') == b[j].TrimEnd('\r')
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var inLcs = new bool[m, n];
        int x = 0, y = 0;
        while (x < m && y < n)
        {
            if (a[x].TrimEnd('\r') == b[y].TrimEnd('\r'))
            {
                inLcs[x, y] = true;
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        return inLcs;
    }

    private static List<string> BuildHunks(string[] original, string[] modified, bool[,] inLcs)
    {
        var result = new List<string>();
        var lines = new List<(char Op, string Text)>();

        int i = 0, j = 0;
        while (i < original.Length || j < modified.Length)
        {
            if (i < original.Length && j < modified.Length && inLcs[i, j])
            {
                lines.Add((' ', original[i].TrimEnd('\r')));
                i++;
                j++;
            }
            else if (j < modified.Length && (i >= original.Length || !inLcs[i, j]))
            {
                lines.Add(('+', modified[j].TrimEnd('\r')));
                j++;
            }
            else
            {
                lines.Add(('-', original[i].TrimEnd('\r')));
                i++;
            }
        }

        var hunkStart = -1;

        for (var k = 0; k < lines.Count; k++)
        {
            var (op, _) = lines[k];

            if (op != ' ')
            {
                if (hunkStart == -1)
                {
                    hunkStart = Math.Max(0, k - 3);
                }
            }
            else if (hunkStart != -1 && k - LastChangeIndex(lines, k) > 3)
            {
                result.Add(FormatHunk(lines, hunkStart, k));
                hunkStart = -1;
            }
        }

        if (hunkStart != -1)
        {
            result.Add(FormatHunk(lines, hunkStart, lines.Count));
        }

        if (result.Count == 0 && lines.All(l => l.Op == ' '))
        {
            result.Add("(no changes)");
        }

        return result;
    }

    private static int LastChangeIndex(List<(char Op, string Text)> lines, int current)
    {
        for (var k = current - 1; k >= 0; k--)
        {
            if (lines[k].Op != ' ')
            {
                return k;
            }
        }

        return 0;
    }

    private static string FormatHunk(List<(char Op, string Text)> lines, int start, int end)
    {
        var sb = new StringBuilder();

        var origStart = 0;
        var modStart = 0;
        for (var k = 0; k < start; k++)
        {
            if (lines[k].Op != '+')
            {
                origStart++;
            }

            if (lines[k].Op != '-')
            {
                modStart++;
            }
        }

        var origCount = 0;
        var modCount = 0;
        for (var k = start; k < end && k < lines.Count; k++)
        {
            if (lines[k].Op != '+')
            {
                origCount++;
            }

            if (lines[k].Op != '-')
            {
                modCount++;
            }
        }

        sb.AppendLine($"@@ -{origStart + 1},{origCount} +{modStart + 1},{modCount} @@");

        for (var k = start; k < end && k < lines.Count; k++)
        {
            sb.AppendLine($"{lines[k].Op}{lines[k].Text}");
        }

        return sb.ToString().TrimEnd();
    }
}
