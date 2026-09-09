using NuGet.Versioning;

namespace CPMigrate.Services.Remediation;

/// <summary>
/// One half-open version window an advisory applies to, as OSV expresses it: a sequence of
/// <c>introduced</c> / <c>fixed</c> / <c>last_affected</c> events over a single package.
///
/// Kept as the raw event list rather than a <see cref="VersionRange"/> because a single OSV range
/// can open and close several times (a flaw reintroduced in a later branch is one range with four
/// events), and collapsing that into one interval would report the gap between them as vulnerable.
/// </summary>
/// <summary>
/// Which side of the vulnerable window a boundary marks.
/// </summary>
public enum AdvisoryBoundaryKind
{
    /// <summary>OSV <c>introduced</c>: the window opens at this version, inclusive.</summary>
    Introduced,

    /// <summary>OSV <c>fixed</c>: the window closes at this version, exclusive of it.</summary>
    Fixed,

    /// <summary>OSV <c>last_affected</c>: the window closes after this version, inclusive of it.</summary>
    LastAffected,
}

/// <summary>
/// One boundary of an advisory's vulnerable window.
/// </summary>
/// <param name="Version">The version the boundary sits at.</param>
/// <param name="Kind">Which side it marks, and whether it includes its own version.</param>
public readonly record struct AdvisoryBoundary(NuGetVersion Version, AdvisoryBoundaryKind Kind);

/// <summary>
/// The version windows an advisory applies to, as OSV expresses them: a sequence of
/// <c>introduced</c> / <c>fixed</c> / <c>last_affected</c> events over a single package.
///
/// Kept as the raw event list rather than a <see cref="VersionRange"/> because a single OSV range
/// can open and close several times (a flaw reintroduced in a later branch is one range with four
/// events), and collapsing that into one interval would report the gap between them as vulnerable.
/// </summary>
public sealed class AdvisoryVersionRange
{
    private readonly IReadOnlyList<AdvisoryBoundary> _boundaries;

    /// <summary>
    /// Builds a range from its boundary events.
    /// </summary>
    /// <param name="boundaries">The boundaries, in any order; they are sorted here.</param>
    public AdvisoryVersionRange(IEnumerable<AdvisoryBoundary> boundaries)
    {
        ArgumentNullException.ThrowIfNull(boundaries);

        // Sorted ascending so the sweep reads boundaries in version order regardless of the order
        // the document listed them. An "introduced" sorts before a close at the same version: a
        // release cannot both start and end being vulnerable, and letting the close win matches
        // OSV's own resolution of that degenerate case.
        _boundaries = boundaries
            .OrderBy(b => b.Version)
            .ThenBy(b => b.Kind == AdvisoryBoundaryKind.Introduced ? 0 : 1)
            .ToList();
    }

    /// <summary>
    /// Whether this range covers a version.
    ///
    /// Walks the boundaries in ascending order and keeps the state the last applicable one left
    /// behind — the sweep OSV's specification describes, which is why a range that reopens needs no
    /// special case. <see cref="AdvisoryBoundaryKind.LastAffected"/> is the one inclusive close, so
    /// it applies strictly below the candidate while the others apply at or below it.
    /// </summary>
    /// <param name="version">The version to test.</param>
    /// <returns>True when the advisory applies to this version.</returns>
    public bool Contains(NuGetVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        var affected = false;

        foreach (var boundary in _boundaries)
        {
            var applies = boundary.Kind == AdvisoryBoundaryKind.LastAffected
                ? boundary.Version < version
                : boundary.Version <= version;

            if (applies)
            {
                affected = boundary.Kind == AdvisoryBoundaryKind.Introduced;
            }
        }

        return affected;
    }
}

/// <summary>
/// What an advisory database knows about one advisory: which versions of a package it affects, and
/// the identifiers a human will recognise it by.
///
/// This is deliberately not a finding. CPMigrate's vulnerability findings come from the .NET SDK and
/// nothing here adds to or removes from them; an <see cref="AdvisoryRecord"/> only answers the
/// narrower question the SDK cannot — <em>which versions does this advisory apply to</em> — so that a
/// fix version can be computed instead of guessed.
/// </summary>
/// <param name="Id">The advisory identifier, e.g. <c>GHSA-5crp-9r3c-p9vr</c>.</param>
/// <param name="Aliases">
/// Other identifiers for the same advisory, typically the CVE number. Worth carrying because the SDK
/// reports only the GHSA URL, so this is the first point at which CPMigrate can name a CVE at all.
/// </param>
/// <param name="Severity">The database's own severity label, when it publishes one.</param>
/// <param name="Ranges">The version windows the advisory applies to.</param>
/// <param name="ExplicitVersions">
/// Versions the database enumerates as affected outright. Unioned with <see cref="Ranges"/> rather
/// than replacing them: the two disagree occasionally, and the union is the conservative reading.
/// Claiming a version is safe when it is not is the only error here with a security consequence.
/// </param>
public sealed record AdvisoryRecord(
    string Id,
    IReadOnlyList<string> Aliases,
    string? Severity,
    IReadOnlyList<AdvisoryVersionRange> Ranges,
    IReadOnlySet<string> ExplicitVersions
)
{
    /// <summary>
    /// Whether this advisory applies to a specific version of the package.
    /// </summary>
    /// <param name="version">The version to test.</param>
    /// <returns>True when the version is affected.</returns>
    public bool Affects(NuGetVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return MatchesExplicitVersion(version) || Ranges.Any(r => r.Contains(version));
    }

    /// <summary>
    /// Whether the advisory's enumerated version list names this version.
    ///
    /// Compared as versions rather than as strings: OSV spells them the way the feed published them
    /// and the candidate comes from NuGet's own index, so <c>4.5</c> and <c>4.5.0</c> are routinely
    /// the same release written two ways. A string compare misses that, and the miss is in the
    /// dangerous direction — a version the database calls affected would be offered as the fix.
    /// </summary>
    private bool MatchesExplicitVersion(NuGetVersion version)
    {
        return ExplicitVersions.Count != 0 && _parsedExplicitVersions.Value.Contains(version);
    }

    /// <summary>
    /// The enumerated versions, parsed once. <see cref="NuGetVersion"/>'s own equality is what makes
    /// <c>4.5</c> and <c>4.5.0</c> compare equal, which is the whole reason this is not a string set.
    /// A version the database spells in a way NuGet cannot parse is dropped: the ranges remain, and
    /// an unparseable entry cannot describe a candidate that came from NuGet's index anyway.
    /// </summary>
    private readonly Lazy<HashSet<NuGetVersion>> _parsedExplicitVersions = new(() =>
    {
        var parsed = new HashSet<NuGetVersion>();

        foreach (var raw in ExplicitVersions)
        {
            if (NuGetVersion.TryParse(raw, out var version))
            {
                parsed.Add(version);
            }
        }

        return parsed;
    });

    /// <summary>
    /// The identifier to show a human: the CVE number when the database publishes one, otherwise the
    /// advisory's own id. A CVE is what appears in a compliance report, so it leads.
    /// </summary>
    public string DisplayId =>
        Aliases.FirstOrDefault(a => a.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase)) ?? Id;
}
