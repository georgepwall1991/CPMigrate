using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Verify;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Moq;

namespace CPMigrate.Tests.Services.Verify;

/// <summary>
/// Capturing the whole solution's resolved graph: restore, then read every project.
/// </summary>
public class GraphSnapshotServiceTests
{
    private readonly Mock<IDotNetCliService> _cli = new();
    private readonly Mock<IDependencyGraphService> _graph = new();

    private AssetsGraphSnapshotService Sut =>
        new(_cli.Object, _graph.Object, new FakeConsoleService());

    [Fact]
    public async Task RestoresBeforeReading()
    {
        // The assets on disk describe whatever state the tree was last restored in. Reading them
        // without restoring first would compare the migration against itself.
        GivenRestore(succeeds: true);
        GivenGraph("A.csproj", Framework("net10.0", ("Serilog", "4.3.0", true)));

        await Sut.CaptureAsync("Sln.sln", ["A.csproj"], basePath: null);

        _cli.Verify(c => c.RunRestoreAsync("Sln.sln"), Times.Once);
    }

    [Fact]
    public async Task ReportsRestoreFailure_WithoutReadingStaleAssets()
    {
        // A failed restore leaves the previous run's assets in place. Reading them would produce a
        // graph that looks entirely valid and describes a tree that no longer exists — the exact
        // shape of "failure that looks like success" this feature exists to catch.
        GivenRestore(succeeds: false);

        var result = await Sut.CaptureAsync("Sln.sln", ["A.csproj"], basePath: null);

        result.RestoreSucceeded.Should().BeFalse();
        result.Snapshot.Projects.Should().BeEmpty();
        _graph.Verify(g => g.TryReadResolvedGraph(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RecordsAProjectWithNoReadableGraphAsUnreadable_NotAsAnEmptyOne()
    {
        // Dropping it silently would shrink the snapshot, and a smaller snapshot compared against a
        // larger one reports nothing wrong — the project simply stops being mentioned.
        GivenRestore(succeeds: true);
        var readable = Path.Combine("a", "A.csproj");
        var missing = Path.Combine("b", "B.csproj");
        GivenGraph(readable, Framework("net10.0", ("Serilog", "4.3.0", true)));
        _graph.Setup(g => g.TryReadResolvedGraph(missing)).Returns((ProjectResolvedGraph?)null);

        var result = await Sut.CaptureAsync("Sln.sln", [readable, missing], basePath: null);

        result.Snapshot.Projects.Select(p => p.ProjectPath).Should().Equal("A.csproj");
        result.Snapshot.Unreadable.Select(u => u.ProjectPath).Should().Equal("B.csproj");
    }

    [Fact]
    public async Task FailsClosed_WhenTwoProjectsShareADirectoryAndThereforeAnAssetsFile()
    {
        // NuGet writes obj/project.assets.json per *directory*, not per project, so two project files
        // beside each other overwrite one another. Reading "each" of them returns whichever restored
        // last — twice. The duplicate compares equal to itself and a real change in the other project
        // is never seen: a clean verdict over an unexamined project, which is the same collision that
        // cost three of Serilog's six projects in v3.28.1 arriving by another route. Cross-review
        // caught it.
        GivenRestore(succeeds: true);
        GivenGraph(
            Path.Combine("src", "A.csproj"),
            Framework("net10.0", ("Serilog", "4.3.0", true))
        );
        GivenGraph(
            Path.Combine("src", "B.csproj"),
            Framework("net10.0", ("Serilog", "4.3.0", true))
        );

        var result = await Sut.CaptureAsync(
            "Sln.sln",
            [Path.Combine("src", "A.csproj"), Path.Combine("src", "B.csproj")],
            basePath: null
        );

        result.Snapshot.Projects.Should().BeEmpty();
        result
            .Snapshot.Unreadable.Should()
            .HaveCount(2)
            .And.OnlyContain(u => u.Reason.Contains("shares a directory"));
    }

    [Fact]
    public async Task FailsClosed_WhenTwoProjectsWouldBeReportedUnderTheSameName()
    {
        // With no scan root to make paths relative to, ProjectId falls back to the file name, so two
        // same-named projects collapse onto one identity and the diff looks up one and finds the
        // other. A verification that cannot tell two projects apart has verified neither.
        GivenRestore(succeeds: true);
        GivenGraph(
            Path.Combine("one", "Api.csproj"),
            Framework("net10.0", ("Serilog", "4.3.0", true))
        );
        GivenGraph(
            Path.Combine("two", "Api.csproj"),
            Framework("net10.0", ("Serilog", "9.9.9", true))
        );

        var result = await Sut.CaptureAsync(
            "Sln.sln",
            [Path.Combine("one", "Api.csproj"), Path.Combine("two", "Api.csproj")],
            basePath: null
        );

        result.Snapshot.Projects.Should().BeEmpty();
        result
            .Snapshot.Unreadable.Should()
            .HaveCount(2)
            .And.OnlyContain(u => u.Reason.Contains("cannot be told apart"));
    }

    [Fact]
    public async Task CountsEveryResolvedVersionAcrossProjectsAndFrameworks()
    {
        GivenRestore(succeeds: true);
        var first = Path.Combine("a", "A.csproj");
        var second = Path.Combine("b", "B.csproj");
        GivenGraph(
            first,
            Framework("net10.0", ("Serilog", "4.3.0", true), ("System.Text.Json", "9.0.0", false)),
            Framework("netstandard2.0", ("Serilog", "2.12.0", true))
        );
        GivenGraph(second, Framework("net10.0", ("Polly", "8.0.0", true)));

        var result = await Sut.CaptureAsync("Sln.sln", [first, second], basePath: null);

        result.Snapshot.ResolvedVersionCount.Should().Be(4);
    }

    private void GivenRestore(bool succeeds)
    {
        _cli.Setup(c => c.RunRestoreAsync(It.IsAny<string>()))
            .ReturnsAsync((succeeds ? "restored" : "error NU1101", succeeds));
    }

    private void GivenGraph(string projectPath, params ResolvedFramework[] frameworks)
    {
        _graph
            .Setup(g => g.TryReadResolvedGraph(projectPath))
            .Returns(new ProjectResolvedGraph(projectPath, frameworks));
    }

    private static ResolvedFramework Framework(
        string tfm,
        params (string Id, string Version, bool Direct)[] packages
    )
    {
        return new ResolvedFramework(
            tfm,
            Resolved: true,
            [.. packages.Select(p => new ResolvedPackage(p.Id, p.Version, p.Direct))]
        );
    }
}
