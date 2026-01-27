namespace CPMigrate;

/// <summary>
/// Parses command-line arguments to determine which were explicitly provided.
/// </summary>
internal static class CliArgumentParser
{
    /// <summary>
    /// Gets the set of CLI argument names that were explicitly provided.
    /// </summary>
    /// <param name="args">Raw command-line arguments.</param>
    /// <returns>HashSet of explicitly provided argument names.</returns>
    public static HashSet<string> GetExplicitArguments(string[] args)
    {
        var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var arg in args)
        {
            var argName = ParseArgumentName(arg);
            if (argName != null)
            {
                provided.Add(argName);
            }
        }

        return provided;
    }

    /// <summary>
    /// Parses an argument string to extract the argument name.
    /// </summary>
    private static string? ParseArgumentName(string arg)
    {
        if (arg.StartsWith("--", StringComparison.Ordinal))
        {
            return ParseLongOption(arg);
        }

        if (arg.StartsWith('-') && arg.Length == 2)
        {
            return ParseShortOption(arg);
        }

        return null;
    }

    /// <summary>
    /// Parses a long-form option (e.g., --option or --option=value).
    /// </summary>
    private static string ParseLongOption(string arg)
    {
        var name = arg[2..];
        var equalsIndex = name.IndexOf('=');

        if (equalsIndex > 0)
        {
            name = name[..equalsIndex];
        }

        return name;
    }

    /// <summary>
    /// Parses a short-form option (e.g., -s) and maps to long name.
    /// </summary>
    private static string? ParseShortOption(string arg)
    {
        var shortOpt = arg[1];

        return shortOpt switch
        {
            's' => "solution",
            'p' => "project",
            'o' => "output-dir",
            'k' => "keep-attrs",
            'n' => "no-backup",
            'd' => "dry-run",
            'r' => "rollback",
            'a' => "analyze",
            'i' => "interactive",
            'q' => "quiet",
            _ => null
        };
    }
}
