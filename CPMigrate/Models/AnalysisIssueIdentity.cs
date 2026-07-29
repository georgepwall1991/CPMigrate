using System.Security.Cryptography;
using System.Text;

namespace CPMigrate.Models;

/// <summary>
/// Stable identity for an analyzer finding, used wherever a finding has to be recognised as "the
/// same one" across runs: SARIF <c>partialFingerprints</c> (so code scanning tracks a finding rather
/// than reopening it every build) and baseline suppression (so accepted debt stays accepted).
///
/// Both consumers must agree, so the computation lives here rather than in either of them.
/// </summary>
public static class AnalysisIssueIdentity
{
    /// <summary>
    /// Versioned so the scheme can change without silently invalidating stored fingerprints: a
    /// baseline written under v1 is recognisably v1.
    /// </summary>
    public const string Version = "v1";

    /// <summary>
    /// Computes the fingerprint for a finding.
    ///
    /// Deliberately excludes the description, because it carries the observed versions — a version
    /// inconsistency that drifts from "13.0.1, 12.0.3" to "13.0.2, 12.0.3" is still the same
    /// unresolved finding, and a fingerprint that changed with it would defeat both tracking and
    /// suppression. Package IDs are lowercased because NuGet treats them case-insensitively, and
    /// project names are sorted because analyzers do not guarantee ordering.
    /// </summary>
    /// <param name="issue">The finding to identify.</param>
    /// <returns>A 32-character lowercase hex fingerprint.</returns>
    public static string Compute(AnalysisIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);

        var projects = issue
            .AffectedProjects.Select(name => name.ToLowerInvariant())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        // Unit separator: it cannot occur in a package ID or project name, so the parts stay
        // unambiguous — "A" + "B,C" cannot collide with "A,B" + "C".
        var seed = string.Join(
            '\u001F',
            issue.IssueCode.ToString(),
            issue.PackageName.ToLowerInvariant(),
            string.Join(',', projects)
        );

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
