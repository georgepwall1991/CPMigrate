using CPMigrate.Services;

namespace CPMigrate;

/// <summary>
/// CPMigrate is a flag-driven CLI — it has no verbs, so `cpmigrate analyze -s .` parses as
/// a migration with a stray positional argument that CommandLineParser silently discards.
/// That turns a read-only intent into a real file-rewriting migration, so a leading bare word
/// is rejected up front with the flag the user almost certainly meant.
/// </summary>
internal static class CliVerbGuard
{
    /// <summary>
    /// Bare words users reach for, mapped to the flag(s) that actually do the job. A word maps
    /// to more than one flag where the intent is genuinely ambiguous — "update" could mean either
    /// `--update` (update CPMigrate itself) or `--update-packages` (update the solution's NuGet
    /// references), so both are offered rather than guessing.
    /// </summary>
    private static readonly Dictionary<string, string[]> VerbToFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["analyze"] = ["--analyze"],
        ["analyse"] = ["--analyze"],
        ["audit"] = ["--audit"],
        ["licenses"] = ["--licenses"],
        ["fix"] = ["--fix"],
        ["update"] = ["--update-packages", "--update"],
        ["upgrade"] = ["--update-packages", "--update"],
        ["rollback"] = ["--rollback"],
        ["batch"] = ["--batch"],
        ["migrate"] = [], // migration is the default action — no flag needed
        ["doctor"] = ["--doctor"],
        ["init"] = ["--init"],
        ["status"] = ["--status"],
    };

    /// <summary>
    /// Returns true when the arguments start with a bare word rather than an option, after
    /// writing an explanatory message. The caller should exit with a validation error.
    /// </summary>
    public static bool RejectsLeadingVerb(string[] args, IConsoleService console)
    {
        // Only args[0] is checked: with no preceding option there is nothing that could
        // legitimately consume it as a value, so a bare word here is always a mistake.
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            return false;
        }

        var verb = args[0];
        console.Error($"Unrecognized argument '{verb}'. CPMigrate takes flags, not sub-commands.");

        if (VerbToFlags.TryGetValue(verb, out var flags))
        {
            var rest = args.Length > 1 ? " " + string.Join(' ', args[1..]) : string.Empty;

            if (flags.Length == 0)
            {
                console.Info($"Migration is the default action — did you mean: cpmigrate{rest}");
            }
            else
            {
                foreach (var flag in flags)
                {
                    console.Info($"Did you mean: cpmigrate {flag}{rest}");
                }
            }
        }

        console.Dim("Run 'cpmigrate --help' to see every available flag.");
        return true;
    }
}
