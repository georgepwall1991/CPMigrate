using CPMigrate.Models;

namespace CPMigrate.Services.Verify;

/// <summary>
/// Decides whether a migration's own decisions account for each change in the resolved graph.
/// </summary>
/// <remarks>
/// This is what makes the report a verdict rather than a list. Thirty changed versions tell a reviewer
/// nothing; "one of these is the conflict you asked me to unify, twenty-nine follow from it, and none
/// are unaccounted for" is something they can act on.
///
/// The claims are earned from the graph rather than inferred from proximity. A transitive change counts
/// as fallout only when the package is actually reachable from one whose own change a decision
/// explains — and only within the project and framework the change occurred in, because a central pin
/// is solution-wide while its consequences are not. Explaining by adjacency instead would be right most
/// of the time, which is the worst possible property for a check that exists to be trusted when it
/// disagrees.
/// </remarks>
public static class DriftAttributor
{
    /// <summary>
    /// Classifies every change as accounted for by a decision, following from one, or unexplained.
    /// </summary>
    /// <param name="before">
    /// Needed as well as <paramref name="after"/> because a removed package cannot be reached in the
    /// graph it is absent from. Judging removals only against what remains would leave every dropped
    /// package permanently unexplained.
    /// </param>
    public static IReadOnlyList<AttributedChange> Attribute(
        IReadOnlyList<GraphChange> changes,
        IReadOnlyList<MigrationDecision> decisions,
        ResolvedGraphSnapshot before,
        ResolvedGraphSnapshot after
    )
    {
        // Grouped rather than deduplicated, because this is the boundary where the "one decision per
        // package" assumption would otherwise break silently. RecordDecisions walks a conflict list
        // that is already one entry per package today, but nothing downstream enforces that: a
        // ToDictionary here would throw and turn a safety feature into the thing that takes the
        // migration down at the moment the caller relies on it, while keeping only the first entry
        // would drop the other resolution without a word — and if the dropped one was the version
        // that actually landed, a change the migration made gets reported as unexplained drift. So
        // every decision recorded for a package is kept, and attribution matches the change against
        // each; agreeing duplicates are harmless, and disagreeing ones each explain only the version
        // they produced.
        var byPackage = decisions
            .GroupBy(
                (MigrationDecision decision) => decision.PackageId,
                StringComparer.OrdinalIgnoreCase
            )
            .ToDictionary(
                (IGrouping<string, MigrationDecision> group) => group.Key,
                (IGrouping<string, MigrationDecision> group) =>
                    (IReadOnlyList<MigrationDecision>)[.. group],
                StringComparer.OrdinalIgnoreCase
            );

        List<AttributedChange> attributed = [];
        List<AttributedChange> pending = [];

        // Direct changes first: they are the ones a decision can name outright, and every fallout
        // claim hangs off one of them.
        foreach (var change in changes)
        {
            var decision = ExplainingDecision(change, byPackage);

            if (decision is not null)
            {
                attributed.Add(
                    new AttributedChange(
                        change,
                        DriftExplanation.ConflictUnified,
                        CausedBy: null,
                        decision.Describe()
                    )
                );
                continue;
            }

            pending.Add(
                new AttributedChange(change, DriftExplanation.Unexplained, null, string.Empty)
            );
        }

        List<AttributedChange> result = [.. attributed];

        foreach (var candidate in pending)
        {
            var cause = FindCause(candidate.Change, attributed, before, after);

            result.Add(
                cause is null
                    ? candidate with
                    {
                        Description = "nothing this migration decided accounts for this change",
                    }
                    : new AttributedChange(
                        candidate.Change,
                        DriftExplanation.TransitiveFallout,
                        cause,
                        $"reachable from {cause}, which this migration moved"
                    )
            );
        }

        return
        [
            .. result
                .OrderBy(a => a.Change.ProjectPath, StringComparer.Ordinal)
                .ThenBy(a => a.Change.TargetFramework, StringComparer.Ordinal)
                .ThenBy(a => a.Change.PackageId, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// The decision that accounts for a change, or null.
    ///
    /// The version has to match. A package the migration decided about that nonetheless landed
    /// somewhere else was not moved by that decision, and accepting it because the name matches is how
    /// an unrelated fault gets waved through under a decision's name. Duplicate decisions for one
    /// package are tolerated only when they agree — a decision IS the one version every project
    /// should receive, so disagreeing duplicates are an internal fault, and attributing anything
    /// under either of them would let reachable drift pass as explained. Those changes stay
    /// unexplained and roll back.
    /// </summary>
    private static MigrationDecision? ExplainingDecision(
        GraphChange change,
        Dictionary<string, IReadOnlyList<MigrationDecision>> byPackage
    )
    {
        if (change.After is null || !byPackage.TryGetValue(change.PackageId, out var candidates))
        {
            return null;
        }

        var distinctVersions = candidates
            .Select(decision => decision.ResolvedVersion)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctVersions.Count > 1)
        {
            return null;
        }

        return candidates.FirstOrDefault(
            (MigrationDecision decision) =>
                string.Equals(
                    decision.ResolvedVersion,
                    change.After,
                    StringComparison.OrdinalIgnoreCase
                )
        );
    }

    /// <summary>
    /// The explained package this change hangs off, if any — searched in the same project and target
    /// framework, and in the graph the change is present in.
    /// </summary>
    private static string? FindCause(
        GraphChange change,
        List<AttributedChange> explained,
        ResolvedGraphSnapshot before,
        ResolvedGraphSnapshot after
    )
    {
        // A removal is only present in the earlier graph; everything else is judged against the graph
        // the migration produced.
        var snapshot = change.Kind == GraphChangeKind.Removed ? before : after;
        var edges = Edges(snapshot, change.ProjectPath, change.TargetFramework);

        return explained
            .Where(
                (AttributedChange a) =>
                    string.Equals(
                        a.Change.ProjectPath,
                        change.ProjectPath,
                        StringComparison.Ordinal
                    )
                    && string.Equals(
                        a.Change.TargetFramework,
                        change.TargetFramework,
                        StringComparison.Ordinal
                    )
            )
            .Select((AttributedChange a) => a.Change.PackageId)
            .FirstOrDefault((string root) => Reaches(edges, root, change.PackageId));
    }

    private static Dictionary<string, IReadOnlyList<string>> Edges(
        ResolvedGraphSnapshot snapshot,
        string projectPath,
        string targetFramework
    )
    {
        Dictionary<string, IReadOnlyList<string>> edges = new(StringComparer.OrdinalIgnoreCase);

        var framework = snapshot
            .Projects.FirstOrDefault(
                (ProjectResolvedGraph p) =>
                    string.Equals(p.ProjectPath, projectPath, StringComparison.Ordinal)
            )
            ?.Frameworks.FirstOrDefault(
                (ResolvedFramework f) =>
                    string.Equals(f.TargetFramework, targetFramework, StringComparison.Ordinal)
            );

        foreach (var package in framework?.Packages ?? [])
        {
            edges[package.PackageId] = package.Dependencies;
        }

        return edges;
    }

    /// <summary>
    /// Whether <paramref name="target"/> is reachable from <paramref name="root"/>. The visited set
    /// doubles as the cycle guard — mutually-referencing packages do occur.
    /// </summary>
    private static bool Reaches(
        Dictionary<string, IReadOnlyList<string>> edges,
        string root,
        string target
    )
    {
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> queue = new();
        queue.Enqueue(root);
        visited.Add(root);

        while (queue.Count > 0)
        {
            if (!edges.TryGetValue(queue.Dequeue(), out var dependencies))
            {
                continue;
            }

            foreach (var dependency in dependencies)
            {
                if (string.Equals(dependency, target, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (visited.Add(dependency))
                {
                    queue.Enqueue(dependency);
                }
            }
        }

        return false;
    }
}

/// <summary>How a change in the resolved graph is accounted for.</summary>
public enum DriftExplanation
{
    /// <summary>Nothing the migration decided accounts for it. The alarm.</summary>
    Unexplained,

    /// <summary>The migration chose this version to settle a conflict between projects.</summary>
    ConflictUnified,

    /// <summary>Reachable from a package the migration moved deliberately.</summary>
    TransitiveFallout,
}

/// <summary>One change, and what accounts for it.</summary>
public sealed record AttributedChange(
    GraphChange Change,
    DriftExplanation Kind,
    string? CausedBy,
    string Description
);
