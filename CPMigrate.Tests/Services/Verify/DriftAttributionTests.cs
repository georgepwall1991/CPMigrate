using CPMigrate.Models;
using CPMigrate.Services.Verify;
using FluentAssertions;

namespace CPMigrate.Tests.Services.Verify;

/// <summary>
/// Deciding whether the migration's own decisions account for what moved.
///
/// This is the part that makes the feature more than a diff. A list of thirty changed versions tells a
/// reviewer nothing; "one of these is the conflict you asked me to unify, twenty-nine follow from it,
/// and none are unaccounted for" is a verdict they can act on. The claims have to be earned from the
/// graph, though — an explanation that is usually right is the kind that gets believed when it is wrong.
/// </summary>
public class DriftAttributionTests
{
    [Fact]
    public void ExplainsADirectChange_ThatLandedOnTheVersionTheMigrationChose()
    {
        var change = Changed("Serilog", "3.1.1", "4.4.0", direct: true);

        var attributed = Attribute(
            [change],
            [UnifiedTo("Serilog", "4.4.0")],
            After(("Serilog", []))
        );

        attributed.Single().Kind.Should().Be(DriftExplanation.ConflictUnified);
        attributed.Single().Description.Should().Contain("Highest");
    }

    [Fact]
    public void DoesNotExplainADirectChange_ThatLandedSomewhereElse()
    {
        // The migration decided 4.4.0 and restore produced 5.0.0. Whatever caused that, it was not the
        // decision — and accepting it because the package name matches is how an unrelated fault gets
        // waved through under a decision's name.
        var change = Changed("Serilog", "3.1.1", "5.0.0", direct: true);

        var attributed = Attribute(
            [change],
            [UnifiedTo("Serilog", "4.4.0")],
            After(("Serilog", []))
        );

        attributed.Single().Kind.Should().Be(DriftExplanation.Unexplained);
    }

    [Fact]
    public void ExplainsATransitiveChange_ReachableFromAnExplainedPackage()
    {
        var changes = new[]
        {
            Changed("Serilog", "3.1.1", "4.4.0", direct: true),
            Changed("System.Text.Json", "8.0.0", "9.0.0", direct: false),
        };

        var attributed = Attribute(
            changes,
            [UnifiedTo("Serilog", "4.4.0")],
            After(("Serilog", ["System.Text.Json"]), ("System.Text.Json", []))
        );

        var fallout = attributed.Single(a => a.Change.PackageId == "System.Text.Json");
        fallout.Kind.Should().Be(DriftExplanation.TransitiveFallout);
        fallout.CausedBy.Should().Be("Serilog");
    }

    [Fact]
    public void ExplainsATransitiveChange_SeveralHopsFromAnExplainedPackage()
    {
        var changes = new[]
        {
            Changed("Serilog", "3.1.1", "4.4.0", direct: true),
            Changed("Leaf", "1.0.0", "2.0.0", direct: false),
        };

        var attributed = Attribute(
            changes,
            [UnifiedTo("Serilog", "4.4.0")],
            After(("Serilog", ["Middle"]), ("Middle", ["Leaf"]), ("Leaf", []))
        );

        attributed
            .Single(a => a.Change.PackageId == "Leaf")
            .Kind.Should()
            .Be(DriftExplanation.TransitiveFallout);
    }

    [Fact]
    public void DoesNotExplainATransitiveChange_UnreachableFromAnythingTheMigrationDecided()
    {
        // The alarm. Nothing the tool did accounts for this, which is exactly what a reviewer needs
        // told — and what the three known gaps in the migration writer produce.
        var changes = new[]
        {
            Changed("Serilog", "3.1.1", "4.4.0", direct: true),
            Changed("Unrelated", "1.0.0", "2.0.0", direct: false),
        };

        var attributed = Attribute(
            changes,
            [UnifiedTo("Serilog", "4.4.0")],
            After(("Serilog", []), ("Unrelated", []))
        );

        attributed
            .Single(a => a.Change.PackageId == "Unrelated")
            .Kind.Should()
            .Be(DriftExplanation.Unexplained);
    }

    [Fact]
    public void DoesNotExplainAnythingReachableFromAnUnexplainedPackage()
    {
        // Reachability from a package whose own change is unaccounted for explains nothing: the chain
        // has to start at a decision, or the whole tree hangs off a fault.
        var changes = new[]
        {
            Changed("Mystery", "1.0.0", "2.0.0", direct: true),
            Changed("Downstream", "1.0.0", "2.0.0", direct: false),
        };

        var attributed = Attribute(
            changes,
            decisions: [],
            After(("Mystery", ["Downstream"]), ("Downstream", []))
        );

        attributed.Should().OnlyContain(a => a.Kind == DriftExplanation.Unexplained);
    }

    [Fact]
    public void ExplainsAPackageThatEnteredTheGraphBeneathADecidedPackage()
    {
        // A higher version of a package brings dependencies the old one did not have. That is an
        // addition, and it follows from the decision just as a version move does.
        var changes = new[]
        {
            Changed("Serilog", "3.1.1", "4.4.0", direct: true),
            new GraphChange("A.csproj", "net10.0", "New.Dep", null, "1.0.0", IsDirect: false),
        };

        var attributed = Attribute(
            changes,
            [UnifiedTo("Serilog", "4.4.0")],
            After(("Serilog", ["New.Dep"]), ("New.Dep", []))
        );

        attributed
            .Single(a => a.Change.PackageId == "New.Dep")
            .Kind.Should()
            .Be(DriftExplanation.TransitiveFallout);
    }

    [Fact]
    public void ExplainsAPackageThatLeftTheGraph_UsingTheEdgesItHadBefore()
    {
        // A removal cannot be reached in the after-graph — it is not there. Judging it only against
        // what remains would leave every dropped package permanently unexplained, and a migration that
        // legitimately drops a package would never come back clean.
        var changes = new[]
        {
            Changed("Serilog", "3.1.1", "4.4.0", direct: true),
            new GraphChange("A.csproj", "net10.0", "Old.Dep", "1.0.0", null, IsDirect: false),
        };

        var attributed = DriftAttributor.Attribute(
            changes,
            [UnifiedTo("Serilog", "4.4.0")],
            before: After(("Serilog", ["Old.Dep"]), ("Old.Dep", [])),
            after: After(("Serilog", []))
        );

        attributed
            .Single(a => a.Change.PackageId == "Old.Dep")
            .Kind.Should()
            .Be(DriftExplanation.TransitiveFallout);
    }

    [Fact]
    public void OnlyExplainsChangesInAProjectTheDecidedPackageActuallyReaches()
    {
        // A central pin is solution-wide, but its consequences are not: a project that never
        // references the unified package cannot have been moved by it.
        var changes = new[]
        {
            Changed("Serilog", "3.1.1", "4.4.0", direct: true),
            new GraphChange("B.csproj", "net10.0", "System.Text.Json", "8.0.0", "9.0.0", false),
        };

        var after = new ResolvedGraphSnapshot(
            [
                Graph("A.csproj", ("Serilog", ["System.Text.Json"]), ("System.Text.Json", [])),
                Graph("B.csproj", ("System.Text.Json", [])),
            ],
            []
        );

        var attributed = DriftAttributor.Attribute(
            changes,
            [UnifiedTo("Serilog", "4.4.0")],
            before: after,
            after: after
        );

        attributed
            .Single(a => a.Change.ProjectPath == "B.csproj")
            .Kind.Should()
            .Be(DriftExplanation.Unexplained);
    }

    [Fact]
    public void MatchesADecisionRecordedInANonCanonicalSpelling()
    {
        // The graph always says 4.3.0, because the assets reader normalizes. A decision recorded as
        // "4.3" — which is what an interactively chosen version stays, since a single remaining
        // candidate is returned verbatim — would fail to match, and the migration that did exactly
        // what the user asked would be reported as unexplained drift and rolled back. Cross-review
        // caught it; the fix normalizes both sides through one shared helper, and this pins the
        // matching behaviour whatever the recording side does.
        var change = Changed("Serilog", "3.1.1", "4.3.0", direct: true);

        var attributed = Attribute(
            [change],
            [
                new MigrationDecision(
                    "Serilog",
                    VersionText.Normalize("4.3"),
                    [],
                    ConflictDecisionSource.Interactive
                ),
            ],
            After(("Serilog", []))
        );

        attributed.Single().Kind.Should().Be(DriftExplanation.ConflictUnified);
    }

    [Fact]
    public void TerminatesOnACycleInTheGraph()
    {
        var changes = new[] { Changed("Serilog", "3.1.1", "4.4.0", direct: true) };

        var act = () =>
            Attribute(changes, [UnifiedTo("Serilog", "4.4.0")], After(("Serilog", ["Serilog"])));

        act.Should().NotThrow();
    }

    [Fact]
    public void ExplainsNothing_WhenTheMigrationDecidedNothing()
    {
        // A migration that resolved no conflicts should move no versions. Anything that moved anyway
        // is unaccounted for by definition.
        var changes = new[] { Changed("Serilog", "3.1.1", "4.4.0", direct: true) };

        Attribute(changes, [], After(("Serilog", [])))
            .Single()
            .Kind.Should()
            .Be(DriftExplanation.Unexplained);
    }

    private static IReadOnlyList<AttributedChange> Attribute(
        IReadOnlyList<GraphChange> changes,
        IReadOnlyList<MigrationDecision> decisions,
        ResolvedGraphSnapshot after
    ) => DriftAttributor.Attribute(changes, decisions, before: after, after: after);

    private static GraphChange Changed(string id, string before, string after, bool direct) =>
        new("A.csproj", "net10.0", id, before, after, direct);

    private static MigrationDecision UnifiedTo(string packageId, string version) =>
        new(
            packageId,
            version,
            [
                new VersionCandidate("3.1.1", ["A.csproj"]),
                new VersionCandidate(version, ["B.csproj"]),
            ],
            ConflictDecisionSource.Highest
        );

    private static ResolvedGraphSnapshot After(params (string Id, string[] DependsOn)[] packages) =>
        new([Graph("A.csproj", packages)], []);

    private static ProjectResolvedGraph Graph(
        string projectPath,
        params (string Id, string[] DependsOn)[] packages
    ) =>
        new(
            projectPath,
            [
                new ResolvedFramework(
                    "net10.0",
                    Resolved: true,
                    [
                        .. packages.Select(p => new ResolvedPackage(
                            p.Id,
                            "0.0.0",
                            IsDirect: false,
                            p.DependsOn
                        )),
                    ]
                ),
            ]
        );
}
