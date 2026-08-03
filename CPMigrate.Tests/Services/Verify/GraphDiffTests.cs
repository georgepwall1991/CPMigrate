using CPMigrate.Models;
using CPMigrate.Services.Verify;
using FluentAssertions;

namespace CPMigrate.Tests.Services.Verify;

/// <summary>
/// Comparing two resolved graphs.
///
/// Half of these are about the comparison and half are about refusing to make one. A diff between two
/// snapshots that do not describe the same thing is worse than no diff: it reports "0 changed" over a
/// project that stopped resolving, which reads as proof the migration was safe.
/// </summary>
public class GraphDiffTests
{
    [Fact]
    public void ReportsNoChanges_WhenTheGraphIsIdentical()
    {
        var before = Snapshot(Project("A.csproj", Framework("net10.0", ("Serilog", "4.3.0"))));
        var after = Snapshot(Project("A.csproj", Framework("net10.0", ("Serilog", "4.3.0"))));

        var diff = GraphDiff.Compare(before, after);

        diff.IsClean.Should().BeTrue();
        diff.Changes.Should().BeEmpty();
        diff.UnchangedCount.Should().Be(1);
    }

    [Fact]
    public void ReportsAVersionThatMoved()
    {
        var before = Snapshot(Project("A.csproj", Framework("net10.0", ("Serilog", "3.1.1"))));
        var after = Snapshot(Project("A.csproj", Framework("net10.0", ("Serilog", "4.4.0"))));

        var change = GraphDiff.Compare(before, after).Changes.Single();

        change.Kind.Should().Be(GraphChangeKind.Changed);
        change.PackageId.Should().Be("Serilog");
        change.Before.Should().Be("3.1.1");
        change.After.Should().Be("4.4.0");
        change.ProjectPath.Should().Be("A.csproj");
        change.TargetFramework.Should().Be("net10.0");
    }

    [Theory]
    [InlineData("3.1.1", "4.4.0", VersionDirection.Upgrade)]
    [InlineData("4.4.0", "3.1.1", VersionDirection.Downgrade)]
    [InlineData("not-a-version", "4.4.0", VersionDirection.Unknown)]
    public void RecordsWhichWayAVersionMoved(string before, string after, VersionDirection expected)
    {
        // A downgrade is the one a reviewer must never skim past: it is how a migration silently
        // reverts a security fix that only one project had picked up.
        var diff = GraphDiff.Compare(
            Snapshot(Project("A.csproj", Framework("net10.0", ("Serilog", before)))),
            Snapshot(Project("A.csproj", Framework("net10.0", ("Serilog", after))))
        );

        diff.Changes.Single().Direction.Should().Be(expected);
    }

    [Fact]
    public void ReportsAPackageThatEnteredTheGraph()
    {
        var before = Snapshot(Project("A.csproj", Framework("net10.0", ("Serilog", "4.3.0"))));
        var after = Snapshot(
            Project("A.csproj", Framework("net10.0", ("Serilog", "4.3.0"), ("Polly", "8.0.0")))
        );

        var change = GraphDiff.Compare(before, after).Changes.Single();

        change.Kind.Should().Be(GraphChangeKind.Added);
        change.PackageId.Should().Be("Polly");
        change.Before.Should().BeNull();
        change.After.Should().Be("8.0.0");
    }

    [Fact]
    public void ReportsAPackageThatLeftTheGraph()
    {
        var before = Snapshot(
            Project("A.csproj", Framework("net10.0", ("Serilog", "4.3.0"), ("Polly", "8.0.0")))
        );
        var after = Snapshot(Project("A.csproj", Framework("net10.0", ("Serilog", "4.3.0"))));

        var change = GraphDiff.Compare(before, after).Changes.Single();

        change.Kind.Should().Be(GraphChangeKind.Removed);
        change.PackageId.Should().Be("Polly");
        change.Before.Should().Be("8.0.0");
        change.After.Should().BeNull();
    }

    [Fact]
    public void MatchesPackageIdsCaseInsensitively()
    {
        // Assets files are not consistent about casing. Reading one spelling as a different package
        // would report a removal and an addition where nothing moved at all.
        var before = Snapshot(Project("A.csproj", Framework("net10.0", ("Serilog", "4.3.0"))));
        var after = Snapshot(Project("A.csproj", Framework("net10.0", ("serilog", "4.3.0"))));

        GraphDiff.Compare(before, after).Changes.Should().BeEmpty();
    }

    [Fact]
    public void OrdersChangesDeterministically()
    {
        // Two runs over the same tree must produce the same report, so a diff of two reports shows
        // what changed rather than what order the dictionaries enumerated in.
        var before = Snapshot(
            Project("B.csproj", Framework("net10.0", ("Zeta", "1.0.0"), ("Alpha", "1.0.0"))),
            Project("A.csproj", Framework("net10.0", ("Mid", "1.0.0")))
        );
        var after = Snapshot(
            Project("B.csproj", Framework("net10.0", ("Zeta", "2.0.0"), ("Alpha", "2.0.0"))),
            Project("A.csproj", Framework("net10.0", ("Mid", "2.0.0")))
        );

        GraphDiff
            .Compare(before, after)
            .Changes.Select(c => $"{c.ProjectPath}/{c.PackageId}")
            .Should()
            .Equal("A.csproj/Mid", "B.csproj/Alpha", "B.csproj/Zeta");
    }

    // ── Refusing to compare ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Fails_WhenAProjectThatResolvedBeforeIsMissingAfter()
    {
        // The failure this whole feature exists to catch. Silently comparing the projects the two
        // snapshots happen to share reports "0 changed" over a project that stopped resolving — a
        // result indistinguishable from a migration that was genuinely safe.
        var before = Snapshot(
            Project("A.csproj", Framework("net10.0", ("Serilog", "4.3.0"))),
            Project("B.csproj", Framework("net10.0", ("Polly", "8.0.0")))
        );
        var after = Snapshot(Project("A.csproj", Framework("net10.0", ("Serilog", "4.3.0"))));

        var diff = GraphDiff.Compare(before, after);

        diff.IsClean.Should().BeFalse();
        diff.IntegrityFailures.Should().ContainSingle().Which.ProjectPath.Should().Be("B.csproj");
    }

    [Fact]
    public void Fails_WhenAProjectCouldNotBeReadAfter()
    {
        var before = Snapshot(Project("A.csproj", Framework("net10.0", ("Serilog", "4.3.0"))));
        var after = new ResolvedGraphSnapshot(
            [],
            [new UnreadableProject("A.csproj", "restore wrote nothing")]
        );

        GraphDiff
            .Compare(before, after)
            .IntegrityFailures.Should()
            .ContainSingle()
            .Which.ProjectPath.Should()
            .Be("A.csproj");
    }

    [Fact]
    public void Fails_WhenAProjectCouldNotBeReadBefore()
    {
        // No baseline means no claim can be made about that project either way. Reporting the rest as
        // clean would imply a coverage the run never had.
        var before = new ResolvedGraphSnapshot(
            [Project("A.csproj", Framework("net10.0", ("Serilog", "4.3.0")))],
            [new UnreadableProject("B.csproj", "never restored")]
        );
        var after = Snapshot(Project("A.csproj", Framework("net10.0", ("Serilog", "4.3.0"))));

        GraphDiff
            .Compare(before, after)
            .IntegrityFailures.Should()
            .ContainSingle()
            .Which.ProjectPath.Should()
            .Be("B.csproj");
    }

    [Fact]
    public void Fails_WhenAFrameworkStopsResolving()
    {
        // A framework restore no longer describes is not a framework with no packages. Read as empty
        // it would report every package under it as removed; read as absent, as nothing at all.
        var before = Snapshot(
            Project(
                "A.csproj",
                Framework("net10.0", ("Serilog", "4.3.0")),
                Framework("netstandard2.0", ("Serilog", "2.12.0"))
            )
        );
        var after = Snapshot(
            Project(
                "A.csproj",
                Framework("net10.0", ("Serilog", "4.3.0")),
                Unresolved("netstandard2.0")
            )
        );

        var failure = GraphDiff.Compare(before, after).IntegrityFailures.Should().ContainSingle();

        failure.Which.ProjectPath.Should().Be("A.csproj");
        failure.Which.TargetFramework.Should().Be("netstandard2.0");
    }

    [Fact]
    public void Fails_WhenAFrameworkIsUnresolvedInBothSnapshots()
    {
        // The blind spot cross-review found. Both sides agree the framework is unresolved, so the
        // context comparison sees nothing missing and the run comes back Unchanged having never read
        // that framework's graph at all — a clean verdict over something it did not look at, which is
        // the one result this feature must never produce. Unmeasured is not unchanged.
        var snapshot = Snapshot(
            Project(
                "A.csproj",
                Framework("net10.0", ("Serilog", "4.3.0")),
                Unresolved("netstandard2.0")
            )
        );

        var diff = GraphDiff.Compare(snapshot, snapshot);

        diff.IsClean.Should().BeFalse();
        diff.IntegrityFailures.Should()
            .ContainSingle()
            .Which.TargetFramework.Should()
            .Be("netstandard2.0");
    }

    [Fact]
    public void Fails_WhenAProjectAppearsOnlyAfter()
    {
        // The other direction, and just as disqualifying: it means the baseline never covered the
        // project, so any comparison against it is a comparison with nothing.
        var before = Snapshot(Project("A.csproj", Framework("net10.0", ("Serilog", "4.3.0"))));
        var after = Snapshot(
            Project("A.csproj", Framework("net10.0", ("Serilog", "4.3.0"))),
            Project("B.csproj", Framework("net10.0", ("Polly", "8.0.0")))
        );

        GraphDiff
            .Compare(before, after)
            .IntegrityFailures.Should()
            .ContainSingle()
            .Which.ProjectPath.Should()
            .Be("B.csproj");
    }

    [Fact]
    public void ClaimsNoChanges_WhenIntegrityFailed()
    {
        // Not merely "also reports a failure". A caller that renders the change list without checking
        // the failures first must not be handed a plausible-looking empty one.
        var before = Snapshot(
            Project("A.csproj", Framework("net10.0", ("Serilog", "3.1.1"))),
            Project("B.csproj", Framework("net10.0", ("Polly", "8.0.0")))
        );
        var after = Snapshot(Project("A.csproj", Framework("net10.0", ("Serilog", "4.4.0"))));

        var diff = GraphDiff.Compare(before, after);

        diff.IsClean.Should().BeFalse();
        diff.Changes.Should().BeEmpty("a diff over an incomparable pair is not a diff");
        diff.UnchangedCount.Should().Be(0);
    }

    private static ResolvedGraphSnapshot Snapshot(params ProjectResolvedGraph[] projects) =>
        new(projects, []);

    private static ProjectResolvedGraph Project(
        string path,
        params ResolvedFramework[] frameworks
    ) => new(path, frameworks);

    private static ResolvedFramework Framework(
        string tfm,
        params (string Id, string Version)[] packages
    ) =>
        new(
            tfm,
            Resolved: true,
            [.. packages.Select(p => new ResolvedPackage(p.Id, p.Version, IsDirect: true))]
        );

    private static ResolvedFramework Unresolved(string tfm) => new(tfm, Resolved: false, []);
}
