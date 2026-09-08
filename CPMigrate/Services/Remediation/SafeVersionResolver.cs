using NuGet.Versioning;

namespace CPMigrate.Services.Remediation;

/// <summary>
/// Why a package has, or does not have, a remediation target.
/// </summary>
public enum SafeVersionStatus
{
    /// <summary>A version within the current major clears every advisory.</summary>
    InMajor,

    /// <summary>
    /// Only a higher major version clears the advisories. Reported separately because a major bump is
    /// an API break the test suite may not catch, so it is a decision rather than a fix.
    /// </summary>
    CrossMajor,

    /// <summary>No published version clears the advisories.</summary>
    NoFixAvailable,

    /// <summary>
    /// The advisories do not describe the version actually resolved. The SDK reported an exposure the
    /// advisory database does not account for, so no version can be proven safe from this data.
    /// </summary>
    AdvisoryDoesNotCoverResolvedVersion,
}

/// <summary>
/// The outcome of resolving a remediation target for one package.
/// </summary>
/// <param name="Status">Which of the cases below applies.</param>
/// <param name="Version">The version to move to, when one was found.</param>
public readonly record struct SafeVersionOutcome(SafeVersionStatus Status, NuGetVersion? Version);

/// <summary>
/// Picks the lowest published version that clears every advisory against a package.
///
/// The "lowest" is the entire point, and the reason this cannot reuse the update pipeline's version
/// selection. <c>--update-packages</c> asks what is newest, which is the right question for staying
/// current and the wrong one for clearing a CVE: taking the newest turns a one-patch security fix
/// into an unrelated feature upgrade, drags in every behaviour change made since, and produces
/// exactly the "update everything and pray" diff a security patch most needs to avoid. A remediation
/// diff should be the smallest change that makes the advisory go away, so a reviewer can see that it
/// is a security fix and nothing else.
/// </summary>
public static class SafeVersionResolver
{
    /// <summary>
    /// Resolves the minimum version of a package that no advisory in the set applies to.
    /// </summary>
    /// <param name="currentVersion">The version the graph currently resolves to.</param>
    /// <param name="advisories">Every advisory the SDK reported against this package.</param>
    /// <param name="availableVersions">Published versions, in any order.</param>
    /// <param name="includePrerelease">Whether pre-release versions are acceptable targets.</param>
    /// <returns>The chosen version and why.</returns>
    public static SafeVersionOutcome Resolve(
        NuGetVersion currentVersion,
        IReadOnlyList<AdvisoryRecord> advisories,
        IReadOnlyList<NuGetVersion> availableVersions,
        bool includePrerelease
    )
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(advisories);
        ArgumentNullException.ThrowIfNull(availableVersions);

        if (advisories.Count == 0)
        {
            return new SafeVersionOutcome(SafeVersionStatus.NoFixAvailable, null);
        }

        if (!advisories.Any(a => a.Affects(currentVersion)))
        {
            // The SDK says this version is exposed and the advisory data says it is not. Something is
            // inconsistent — a normalisation difference, or a database that disagrees with the one the
            // SDK consulted — and the safe response is to say so rather than to compute a fix version
            // from ranges that evidently do not describe this package's history. Reporting "already
            // safe" here would dismiss a real finding on the strength of data that just proved itself
            // unreliable.
            return new SafeVersionOutcome(SafeVersionStatus.AdvisoryDoesNotCoverResolvedVersion, null);
        }

        // Ascending, because the first acceptable candidate is the answer. Only versions strictly
        // above the current one are candidates: remediation moves forward, and a downgrade that
        // happens to predate the flaw is not a fix anyone wants applied automatically.
        var candidates = availableVersions
            .Where(v => v > currentVersion)
            .Where(v => includePrerelease || !v.IsPrerelease)
            .OrderBy(v => v)
            .ToList();

        var inMajor = candidates.FirstOrDefault(v =>
            v.Major == currentVersion.Major && IsClear(v, advisories)
        );

        if (inMajor != null)
        {
            return new SafeVersionOutcome(SafeVersionStatus.InMajor, inMajor);
        }

        var crossMajor = candidates.FirstOrDefault(v => IsClear(v, advisories));

        return crossMajor != null
            ? new SafeVersionOutcome(SafeVersionStatus.CrossMajor, crossMajor)
            : new SafeVersionOutcome(SafeVersionStatus.NoFixAvailable, null);
    }

    /// <summary>
    /// Whether a candidate version escapes every advisory. Every one, not the worst one: packages
    /// routinely carry several advisories whose fixed versions differ, and clearing only the highest
    /// severity would leave a lower-severity CVE in place while reporting the package remediated.
    /// </summary>
    private static bool IsClear(NuGetVersion candidate, IReadOnlyList<AdvisoryRecord> advisories)
    {
        return !advisories.Any(a => a.Affects(candidate));
    }
}
