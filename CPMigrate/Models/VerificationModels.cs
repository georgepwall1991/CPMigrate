using NuGet.Versioning;

namespace CPMigrate.Models;

/// <summary>
/// Settles two spellings of one release onto a single form.
/// </summary>
/// <remarks>
/// One implementation, shared by everything the verification compares. The two sides of that
/// comparison come from different places — one from the assets file restore wrote, one from the
/// version the migration chose — and if only one of them is normalized then <c>4.3</c> and
/// <c>4.3.0</c> are two different releases. That does not read as a formatting bug: it reports drift
/// nobody caused, on a migration that did exactly what it was told, and rolls it back.
/// </remarks>
public static class VersionText
{
    /// <summary>
    /// The canonical spelling of a version, or the input verbatim when it cannot be parsed — still
    /// comparable against itself, which is all a diff needs.
    /// </summary>
    public static string Normalize(string raw)
    {
        return NuGetVersion.TryParse(raw, out var parsed) ? parsed.ToNormalizedString() : raw;
    }
}

/// <summary>
/// One package as restore resolved it, for one project and one target framework.
/// </summary>
/// <param name="PackageId">The package ID as the assets file spells it. Compared case-insensitively.</param>
/// <param name="Version">The resolved version, normalized so equivalent spellings compare equal.</param>
/// <param name="IsDirect">
/// Whether the project references this package itself, as opposed to receiving it through another
/// package. The distinction is what lets a report say "you moved Serilog, and that dragged
/// System.Text.Json with it" instead of listing thirty equally-weighted rows.
/// </param>
/// <param name="DependsOn">
/// The package IDs this one pulls in, as the resolved graph records them. Carried so a report can
/// prove a claim rather than imply one: without the edges, "System.Text.Json moved because you moved
/// Serilog" is a guess that happens to be right often enough to be trusted when it is wrong.
/// </param>
public sealed record ResolvedPackage(
    string PackageId,
    string Version,
    bool IsDirect,
    IReadOnlyList<string>? DependsOn = null
)
{
    /// <summary>The packages this one pulls in. Empty rather than null when nothing was recorded.</summary>
    public IReadOnlyList<string> Dependencies => DependsOn ?? [];
}

/// <summary>
/// What one target framework of one project resolved to.
/// </summary>
/// <param name="TargetFramework">The short framework name, as the assets file keys it.</param>
/// <param name="Resolved">
/// Whether restore actually described this framework. A framework the project declares but that is
/// absent from <c>targets</c> is <em>not</em> a framework with no packages: reading it as empty is how a
/// framework that stopped resolving between two snapshots would show up as "everything removed", or as
/// nothing at all. Callers treat a framework that was resolved before and is not now as a failed run.
/// </param>
/// <param name="Packages">The packages resolved for this framework, empty when <paramref name="Resolved"/> is false.</param>
public sealed record ResolvedFramework(
    string TargetFramework,
    bool Resolved,
    IReadOnlyList<ResolvedPackage> Packages
);

/// <summary>
/// The resolved dependency graph of a single project, as restore left it.
/// </summary>
public sealed record ProjectResolvedGraph(
    string ProjectPath,
    IReadOnlyList<ResolvedFramework> Frameworks
);

/// <summary>
/// A project whose resolved graph could not be read, and why.
/// </summary>
public sealed record UnreadableProject(string ProjectPath, string Reason);

/// <summary>
/// The resolved graph of every project in scope at one point in time — the shipping manifest a
/// migration is measured against.
/// </summary>
public sealed class ResolvedGraphSnapshot
{
    public ResolvedGraphSnapshot(
        IReadOnlyList<ProjectResolvedGraph> projects,
        IReadOnlyList<UnreadableProject> unreadable
    )
    {
        Projects = projects;
        Unreadable = unreadable;
    }

    /// <summary>Projects whose assets file was read.</summary>
    public IReadOnlyList<ProjectResolvedGraph> Projects { get; }

    /// <summary>
    /// Projects that could not be read. Carried rather than dropped: a snapshot that silently covers
    /// fewer projects than the run intended is the failure mode this feature exists to catch.
    /// </summary>
    public IReadOnlyList<UnreadableProject> Unreadable { get; }

    /// <summary>Every project that was expected in this snapshot, readable or not.</summary>
    public int ProjectCount => Projects.Count + Unreadable.Count;

    /// <summary>Total resolved package versions across every project and framework.</summary>
    public int ResolvedVersionCount =>
        Projects
            .SelectMany((ProjectResolvedGraph project) => project.Frameworks)
            .Sum((ResolvedFramework framework) => framework.Packages.Count);
}

/// <summary>Where a conflict resolution came from.</summary>
public enum ConflictDecisionSource
{
    /// <summary>The <c>--conflict-strategy Highest</c> default.</summary>
    Highest,

    /// <summary><c>--conflict-strategy Lowest</c>.</summary>
    Lowest,

    /// <summary>Chosen at the prompt under <c>--interactive-conflicts</c>.</summary>
    Interactive,
}

/// <summary>One version a package was declared at, and the projects that declared it.</summary>
public sealed record VersionCandidate(string Version, IReadOnlyList<string> Projects);

/// <summary>
/// A choice the migration made on the user's behalf: which version of a package every project will get.
/// </summary>
/// <remarks>
/// Recorded because the payload previously carried <c>ConflictsResolved</c> as an integer and nothing
/// else. A count cannot answer the only question a reviewer of a migration PR has — which version won,
/// out of what, and at whose direction — and it is what lets a change in the resolved graph be
/// attributed to a deliberate decision rather than merely noticed.
/// </remarks>
public sealed record MigrationDecision(
    string PackageId,
    string ResolvedVersion,
    IReadOnlyList<VersionCandidate> Candidates,
    ConflictDecisionSource Source
)
{
    /// <summary>A one-line account of the decision, for a report a human reads.</summary>
    public string Describe()
    {
        var others = Candidates
            .Where(candidate =>
                !string.Equals(
                    candidate.Version,
                    ResolvedVersion,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .Select(candidate => candidate.Version)
            .ToList();

        var from = others.Count == 0 ? string.Empty : $" over {string.Join(", ", others)}";

        return $"conflict unified to {ResolvedVersion}{from} ({Source})";
    }
}

/// <summary>
/// The outcome of capturing one snapshot: whether restore worked, what it said, and what was read.
/// </summary>
/// <param name="RestoreSucceeded">
/// False leaves <paramref name="Snapshot"/> empty by design — a failed restore leaves the previous
/// run's assets on disk, and those parse perfectly while describing a tree that no longer exists.
/// </param>
public sealed record GraphSnapshotResult(
    bool RestoreSucceeded,
    string RestoreOutput,
    ResolvedGraphSnapshot Snapshot
);
