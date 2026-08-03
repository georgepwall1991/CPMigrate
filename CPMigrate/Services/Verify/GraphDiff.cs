using CPMigrate.Models;
using NuGet.Versioning;

namespace CPMigrate.Services.Verify;

/// <summary>
/// Compares two resolved dependency graphs — what restored before a change, and what restores after.
/// </summary>
/// <remarks>
/// Most of this type is about refusing to compare. A diff between two snapshots that do not describe
/// the same set of projects and frameworks is worse than no diff at all: it reports "0 changed" over a
/// project that stopped resolving, and that result is indistinguishable from a migration that was
/// genuinely safe. So the coverage of the two snapshots is established first, and a mismatch in either
/// direction abandons the comparison rather than narrowing it.
/// </remarks>
public static class GraphDiff
{
    /// <summary>
    /// Diffs <paramref name="before"/> against <paramref name="after"/>.
    /// </summary>
    public static GraphDiffResult Compare(ResolvedGraphSnapshot before, ResolvedGraphSnapshot after)
    {
        var integrityFailures = FindIntegrityFailures(before, after);

        if (integrityFailures.Count > 0)
        {
            // Deliberately no changes and no unchanged count. A caller that renders the change list
            // without checking the failures first must not be handed a plausible-looking empty one.
            return new GraphDiffResult([], integrityFailures, UnchangedCount: 0);
        }

        List<GraphChange> changes = [];
        var unchanged = 0;

        foreach (var (project, framework) in ResolvedFrameworks(before))
        {
            var afterPackages = IndexPackages(
                FindFramework(after, project.ProjectPath, framework.TargetFramework)
            );
            var beforePackages = IndexPackages(framework);

            foreach (
                var packageId in beforePackages
                    .Keys.Concat(afterPackages.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
            )
            {
                var found = beforePackages.TryGetValue(packageId, out var was);
                var stillThere = afterPackages.TryGetValue(packageId, out var now);

                if (
                    found
                    && stillThere
                    && string.Equals(was!.Version, now!.Version, StringComparison.OrdinalIgnoreCase)
                )
                {
                    unchanged++;
                    continue;
                }

                changes.Add(
                    new GraphChange(
                        project.ProjectPath,
                        framework.TargetFramework,
                        (now ?? was)!.PackageId,
                        was?.Version,
                        now?.Version,
                        (now ?? was)!.IsDirect
                    )
                );
            }
        }

        return new GraphDiffResult(Order(changes), [], unchanged);
    }

    /// <summary>
    /// Establishes that the two snapshots describe the same thing, before anything is compared.
    ///
    /// Both directions disqualify. A project or framework that resolved before and not after is the
    /// headline case — silent loss dressed as a clean result. One that appears only afterwards is just
    /// as disqualifying for the opposite reason: the baseline never covered it, so there is nothing to
    /// measure it against, and a report claiming to have verified the solution would be overstating
    /// what it looked at.
    /// </summary>
    private static List<GraphIntegrityFailure> FindIntegrityFailures(
        ResolvedGraphSnapshot before,
        ResolvedGraphSnapshot after
    )
    {
        List<GraphIntegrityFailure> failures = [];

        foreach (var unreadable in before.Unreadable)
        {
            failures.Add(
                new GraphIntegrityFailure(
                    unreadable.ProjectPath,
                    TargetFramework: null,
                    $"could not be read before the change ({unreadable.Reason}), so there is no baseline to compare against"
                )
            );
        }

        foreach (var unreadable in after.Unreadable)
        {
            failures.Add(
                new GraphIntegrityFailure(
                    unreadable.ProjectPath,
                    TargetFramework: null,
                    $"could not be read after the change ({unreadable.Reason})"
                )
            );
        }

        // A project already reported as unreadable will also be missing every framework it used to
        // resolve. Those are consequences of the one problem, and listing them turns a single
        // unreadable project into a wall — the same reason CpmNotEnabled is reported once per props
        // file rather than once per project beneath it.
        HashSet<string> alreadyReported =
        [
            .. failures.Select((GraphIntegrityFailure failure) => failure.ProjectPath),
        ];

        // A framework restore never described is unmeasured, and unmeasured is not unchanged. It
        // matters most when *both* captures lack it: the two context sets then agree, nothing is
        // reported missing, and the run comes back Unchanged having never looked at that framework's
        // graph at all — a clean verdict over a blind spot, which is the exact shape this whole
        // feature exists to refuse. Cross-review caught it; the missing-context comparison below only
        // ever caught the asymmetric case.
        // Reported once per (project, framework) however many of the two captures lack it. One thing
        // is unknown, so it is one line: saying it twice is the wall this file avoids everywhere else.
        failures.AddRange(
            UnresolvedFrameworks(before, alreadyReported)
                .Concat(UnresolvedFrameworks(after, alreadyReported))
                .DistinctBy(
                    (GraphIntegrityFailure failure) =>
                        (failure.ProjectPath, failure.TargetFramework)
                )
                .OrderBy(
                    (GraphIntegrityFailure failure) => failure.ProjectPath,
                    StringComparer.Ordinal
                )
                .ThenBy(
                    (GraphIntegrityFailure failure) => failure.TargetFramework,
                    StringComparer.Ordinal
                )
        );

        foreach (var failure in failures)
        {
            alreadyReported.Add(failure.ProjectPath);
        }

        var beforeContexts = ResolvedContexts(before);
        var afterContexts = ResolvedContexts(after);

        foreach (var context in beforeContexts.Except(afterContexts).OrderBy(c => c))
        {
            if (alreadyReported.Contains(context.Project))
            {
                continue;
            }

            failures.Add(
                Missing(context, "resolved before the change and does not resolve after it")
            );
        }

        foreach (var context in afterContexts.Except(beforeContexts).OrderBy(c => c))
        {
            if (alreadyReported.Contains(context.Project))
            {
                continue;
            }

            failures.Add(
                Missing(
                    context,
                    "resolves after the change but was not in the baseline, so nothing can be said about what it changed from"
                )
            );
        }

        return failures;
    }

    /// <summary>
    /// Frameworks the project declares that restore did not describe, in one snapshot.
    /// </summary>
    private static IEnumerable<GraphIntegrityFailure> UnresolvedFrameworks(
        ResolvedGraphSnapshot snapshot,
        HashSet<string> alreadyReported
    )
    {
        return snapshot
            .Projects.Where(
                (ProjectResolvedGraph project) => !alreadyReported.Contains(project.ProjectPath)
            )
            .SelectMany(
                (ProjectResolvedGraph project) =>
                    project
                        .Frameworks.Where((ResolvedFramework framework) => !framework.Resolved)
                        .Select(
                            (ResolvedFramework framework) =>
                                new GraphIntegrityFailure(
                                    project.ProjectPath,
                                    framework.TargetFramework,
                                    "is declared by the project but restore did not describe it, so "
                                        + "nothing is known about what it resolves to"
                                )
                        )
            );
    }

    private static GraphIntegrityFailure Missing(
        (string Project, string Framework) context,
        string reason
    ) => new(context.Project, context.Framework, reason);

    /// <summary>
    /// Every (project, framework) pair that restore actually described. A framework the project
    /// declares but that restore did not write is excluded here and caught by the comparison of the
    /// two sets, which is what turns it into a failure rather than an empty package list.
    /// </summary>
    private static HashSet<(string Project, string Framework)> ResolvedContexts(
        ResolvedGraphSnapshot snapshot
    )
    {
        return
        [
            .. ResolvedFrameworks(snapshot)
                .Select(pair => (pair.Project.ProjectPath, pair.Framework.TargetFramework)),
        ];
    }

    private static IEnumerable<(
        ProjectResolvedGraph Project,
        ResolvedFramework Framework
    )> ResolvedFrameworks(ResolvedGraphSnapshot snapshot)
    {
        return snapshot.Projects.SelectMany(
            (ProjectResolvedGraph project) =>
                project
                    .Frameworks.Where((ResolvedFramework framework) => framework.Resolved)
                    .Select((ResolvedFramework framework) => (project, framework))
        );
    }

    private static ResolvedFramework? FindFramework(
        ResolvedGraphSnapshot snapshot,
        string projectPath,
        string targetFramework
    )
    {
        return snapshot
            .Projects.FirstOrDefault(
                (ProjectResolvedGraph p) =>
                    string.Equals(p.ProjectPath, projectPath, StringComparison.Ordinal)
            )
            ?.Frameworks.FirstOrDefault(
                (ResolvedFramework f) =>
                    string.Equals(f.TargetFramework, targetFramework, StringComparison.Ordinal)
            );
    }

    private static Dictionary<string, ResolvedPackage> IndexPackages(ResolvedFramework? framework)
    {
        Dictionary<string, ResolvedPackage> index = new(StringComparer.OrdinalIgnoreCase);

        foreach (var package in framework?.Packages ?? [])
        {
            index[package.PackageId] = package;
        }

        return index;
    }

    private static List<GraphChange> Order(List<GraphChange> changes)
    {
        return
        [
            .. changes
                .OrderBy(c => c.ProjectPath, StringComparer.Ordinal)
                .ThenBy(c => c.TargetFramework, StringComparer.Ordinal)
                .ThenBy(c => c.PackageId, StringComparer.OrdinalIgnoreCase),
        ];
    }
}

/// <summary>What happened to one package in one project's graph.</summary>
public enum GraphChangeKind
{
    /// <summary>Resolved to a different version than before.</summary>
    Changed,

    /// <summary>Entered the graph.</summary>
    Added,

    /// <summary>Left the graph.</summary>
    Removed,
}

/// <summary>Which way a version moved.</summary>
public enum VersionDirection
{
    /// <summary>Not a version change, or one side is absent.</summary>
    None,

    Upgrade,

    /// <summary>
    /// The one a reviewer must never skim past: how a change silently reverts a fix that only one
    /// project had picked up.
    /// </summary>
    Downgrade,

    /// <summary>At least one side could not be parsed as a version, so the direction is not known.</summary>
    Unknown,
}

/// <summary>One package's fate in one project and target framework.</summary>
public sealed record GraphChange(
    string ProjectPath,
    string TargetFramework,
    string PackageId,
    string? Before,
    string? After,
    bool IsDirect
)
{
    public GraphChangeKind Kind =>
        (Before, After) switch
        {
            (null, not null) => GraphChangeKind.Added,
            (not null, null) => GraphChangeKind.Removed,
            _ => GraphChangeKind.Changed,
        };

    public VersionDirection Direction
    {
        get
        {
            if (Kind != GraphChangeKind.Changed)
            {
                return VersionDirection.None;
            }

            if (
                !NuGetVersion.TryParse(Before, out var was)
                || !NuGetVersion.TryParse(After, out var now)
            )
            {
                return VersionDirection.Unknown;
            }

            return now > was ? VersionDirection.Upgrade : VersionDirection.Downgrade;
        }
    }
}

/// <summary>
/// A reason the two snapshots cannot be compared. Never a finding about the code — always a statement
/// that the run does not know something it needs to know.
/// </summary>
public sealed record GraphIntegrityFailure(
    string ProjectPath,
    string? TargetFramework,
    string Reason
);

/// <summary>The outcome of comparing two graphs.</summary>
public sealed record GraphDiffResult(
    IReadOnlyList<GraphChange> Changes,
    IReadOnlyList<GraphIntegrityFailure> IntegrityFailures,
    int UnchangedCount
)
{
    /// <summary>
    /// True only when the comparison could be made and found nothing. An incomparable pair is never
    /// clean, however empty its change list.
    /// </summary>
    public bool IsClean => IntegrityFailures.Count == 0 && Changes.Count == 0;
}
