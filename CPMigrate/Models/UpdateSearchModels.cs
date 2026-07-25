namespace CPMigrate.Models;

/// <summary>
/// Why a verification run of a candidate update subset ended the way it did.
/// </summary>
public enum VerificationOutcome
{
    /// <summary>Restore and tests both succeeded.</summary>
    Passed,

    /// <summary><c>dotnet restore</c> failed, so tests never ran.</summary>
    RestoreFailed,

    /// <summary>Restore succeeded but <c>dotnet test</c> reported failures.</summary>
    TestsFailed
}

/// <summary>
/// Result of verifying one candidate subset of package updates.
/// </summary>
/// <param name="Outcome">Whether the subset restored and tested cleanly.</param>
/// <param name="Output">Captured CLI output, used to explain failures to the user.</param>
public sealed record VerificationResult(VerificationOutcome Outcome, string Output)
{
    /// <summary>Whether this subset is safe to keep.</summary>
    public bool Passed => Outcome == VerificationOutcome.Passed;

    /// <summary>A passing result with no captured output.</summary>
    public static VerificationResult Success() => new(VerificationOutcome.Passed, string.Empty);
}

/// <summary>
/// Outcome of searching for the largest subset of updates that keeps the build and tests green.
/// </summary>
/// <param name="Applied">Updates present in the final, verified-good state of the props file.</param>
/// <param name="HeldBack">Updates excluded because they could not be kept without breaking verification.</param>
/// <param name="VerificationRuns">Number of restore+test cycles actually executed (cache hits excluded).</param>
/// <param name="BudgetExhausted">Whether the search stopped early because it hit the run budget.</param>
/// <param name="BaselineBroken">Whether verification failed even with zero updates applied.</param>
/// <param name="FailureOutput">CLI output from the most recent failing verification, if any.</param>
/// <param name="FailureOutcome">
/// How the most recent failing verification failed, so callers can say "restore failed" rather than
/// "tests failed" when tests never ran. Null when nothing failed.
/// </param>
public sealed record UpdateSearchResult(
    IReadOnlyList<PackageUpdateEntry> Applied,
    IReadOnlyList<PackageUpdateEntry> HeldBack,
    int VerificationRuns,
    bool BudgetExhausted = false,
    bool BaselineBroken = false,
    string? FailureOutput = null,
    VerificationOutcome? FailureOutcome = null)
{
    /// <summary>Whether any update survived the search.</summary>
    public bool AnyApplied => Applied.Count > 0;

    /// <summary>Whether every candidate update survived the search.</summary>
    public bool AllApplied => HeldBack.Count == 0;
}
