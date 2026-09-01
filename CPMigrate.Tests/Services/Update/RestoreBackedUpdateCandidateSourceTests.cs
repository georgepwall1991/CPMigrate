using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Update;
using FluentAssertions;
using Moq;

namespace CPMigrate.Tests.Services.Update;

/// <summary>
/// Pins the restore-backed candidate merge: highest latest-version wins, a central pin is never
/// reclassified as transitive, a failed project is named rather than treated as "up to date",
/// and discovery order — not completion order — is what a later report would replay.
/// </summary>
[Collection("Sequential")]
public class RestoreBackedUpdateCandidateSourceTests : IDisposable
{
    public RestoreBackedUpdateCandidateSourceTests()
    {
        ScanConcurrencyGate.ResetForTests();
    }

    public void Dispose()
    {
        ScanConcurrencyGate.ResetForTests();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void MergeCandidates_HighestLatestVersionWinsAcrossProjects()
    {
        var current = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Newtonsoft.Json"] = ["12.0.1"]
        };

        var merged = RestoreBackedUpdateCandidateSource.MergeCandidates(
            [
                new("Newtonsoft.Json", "12.0.1", "13.0.1", "/a/A.csproj", "A.csproj"),
                new("Newtonsoft.Json", "12.0.1", "13.0.3", "/b/B.csproj", "B.csproj"),
                new("Newtonsoft.Json", "12.0.1", "13.0.2", "/c/C.csproj", "C.csproj"),
            ],
            current,
            includeTransitive: false
        );

        merged.Should().ContainSingle();
        merged[0].PackageName.Should().Be("Newtonsoft.Json");
        merged[0].CurrentVersion.Should().Be("12.0.1");
        merged[0].LatestVersion.Should().Be("13.0.3");
        merged[0].IsTransitive.Should().BeFalse();
        merged[0].IsMajorUpdate.Should().BeTrue();
    }

    [Fact]
    public void MergeCandidates_CurrentVersionComesFromPropsNotResolved()
    {
        var current = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Serilog"] = ["3.1.1"]
        };

        var merged = RestoreBackedUpdateCandidateSource.MergeCandidates(
            [new("Serilog", "3.0.0", "4.0.0", "/a/A.csproj", "A.csproj")],
            current,
            includeTransitive: false
        );

        merged.Should().ContainSingle();
        merged[0].CurrentVersion.Should().Be("3.1.1", "the pin in Directory.Packages.props is the version we would rewrite");
    }

    [Fact]
    public void MergeCandidates_PackageInPropsIsDirectEvenWhenOutdatedRowIsTransitive()
    {
        var current = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Newtonsoft.Json"] = ["13.0.1"]
        };

        var merged = RestoreBackedUpdateCandidateSource.MergeCandidates(
            [new("Newtonsoft.Json", "13.0.1", "13.0.3", "/a/A.csproj", "A.csproj", IsTransitive: true)],
            current,
            includeTransitive: true
        );

        merged.Should().ContainSingle();
        merged[0].IsTransitive.Should().BeFalse();
    }

    [Fact]
    public void MergeCandidates_PackageAbsentFromPropsIsTransitiveOnlyWhenRequested()
    {
        var current = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Newtonsoft.Json"] = ["13.0.3"]
        };
        var rows = new[]
        {
            new OutdatedPackageInfo("System.Text.Encodings.Web", "7.0.0", "8.0.0", "/a/A.csproj", "A.csproj", IsTransitive: true)
        };

        RestoreBackedUpdateCandidateSource.MergeCandidates(rows, current, includeTransitive: false)
            .Should().BeEmpty();

        var withTransitive = RestoreBackedUpdateCandidateSource.MergeCandidates(rows, current, includeTransitive: true);
        withTransitive.Should().ContainSingle();
        withTransitive[0].IsTransitive.Should().BeTrue();
        withTransitive[0].CurrentVersion.Should().Be("7.0.0");
        withTransitive[0].LatestVersion.Should().Be("8.0.0");
    }

    [Fact]
    public void MergeCandidates_PackageInPropsButAbsentFromOutdatedRows_IsNotProposed()
    {
        var current = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Contoso.Core"] = ["1.2.0"],
            ["Newtonsoft.Json"] = ["13.0.1"]
        };

        var merged = RestoreBackedUpdateCandidateSource.MergeCandidates(
            [new("Newtonsoft.Json", "13.0.1", "13.0.3", "/a/A.csproj", "A.csproj")],
            current,
            includeTransitive: false
        );

        merged.Should().ContainSingle(u => u.PackageName == "Newtonsoft.Json");
        merged.Should().NotContain(u => u.PackageName == "Contoso.Core");
    }

    [Fact]
    public void ResolveCurrentVersion_PicksHighestWhenPropsDisagree()
    {
        RestoreBackedUpdateCandidateSource.ResolveCurrentVersion(["1.0.0", "2.0.0", "1.5.0"])
            .Should().Be("2.0.0");
    }

    [Fact]
    public async Task FindAsync_OneFailedProject_IsNamedInUnscannedWhileOthersStillYieldCandidates()
    {
        var analyzer = new Mock<IProjectAnalyzer>();
        var good = Path.Combine(Path.GetTempPath(), "good-dir", "Good.csproj");
        var bad = Path.Combine(Path.GetTempPath(), "bad-dir", "Bad.csproj");

        analyzer.Setup(a => a.ScanOutdatedPackagesAsync(good, false, false, null))
            .ReturnsAsync(
                ([new OutdatedPackageInfo("Newtonsoft.Json", "12.0.3", "13.0.3", good, "Good.csproj")], true));
        analyzer.Setup(a => a.ScanOutdatedPackagesAsync(bad, false, false, null))
            .ReturnsAsync(([], false));

        var source = new RestoreBackedUpdateCandidateSource(analyzer.Object);
        var current = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Newtonsoft.Json"] = ["12.0.3"]
        };

        var scan = await source.FindAsync([good, bad], current, false, false, maxParallelism: 2);

        scan.UnscannedProjects.Should().Equal("Bad.csproj");
        scan.Updates.Should().ContainSingle(u => u.PackageName == "Newtonsoft.Json" && u.LatestVersion == "13.0.3");
    }

    [Fact]
    public async Task FindAsync_PassesIncludePrereleaseToOutdatedScan()
    {
        var analyzer = new Mock<IProjectAnalyzer>();
        var project = Path.Combine(Path.GetTempPath(), "prerelease-dir", "App.csproj");
        analyzer.Setup(a => a.ScanOutdatedPackagesAsync(project, false, true, null))
            .ReturnsAsync(([], true))
            .Verifiable();

        var source = new RestoreBackedUpdateCandidateSource(analyzer.Object);
        await source.FindAsync(
            [project],
            new Dictionary<string, HashSet<string>> { ["Pkg"] = ["1.0.0"] },
            includeTransitive: false,
            includePrerelease: true,
            maxParallelism: 1);

        analyzer.Verify();
    }

    [Fact]
    public async Task FindAsync_ResultsStayInDiscoveryOrder_EvenWhenLaterProjectsFinishFirst()
    {
        var analyzer = new Mock<IProjectAnalyzer>();
        var first = Path.Combine(Path.GetTempPath(), "first-dir", "First.csproj");
        var second = Path.Combine(Path.GetTempPath(), "second-dir", "Second.csproj");

        analyzer.Setup(a => a.ScanOutdatedPackagesAsync(first, false, false, null))
            .Returns(async () =>
            {
                await Task.Delay(40);
                return (new List<OutdatedPackageInfo>
                {
                    new("Slow.Package", "1.0.0", "2.0.0", first, "First.csproj")
                }, true);
            });
        analyzer.Setup(a => a.ScanOutdatedPackagesAsync(second, false, false, null))
            .ReturnsAsync(
                ([new OutdatedPackageInfo("Fast.Package", "1.0.0", "2.0.0", second, "Second.csproj")], true));

        var source = new RestoreBackedUpdateCandidateSource(analyzer.Object);
        var current = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Slow.Package"] = ["1.0.0"],
            ["Fast.Package"] = ["1.0.0"]
        };

        var scan = await source.FindAsync([first, second], current, false, false, maxParallelism: 2);

        scan.UnscannedProjects.Should().BeEmpty();
        scan.Updates.Select(u => u.PackageName).Should().Equal("Fast.Package", "Slow.Package");
    }

    [Fact]
    public async Task FindAsync_PeakConcurrencyStaysWithinTheCap()
    {
        ScanConcurrencyGate.ResetForTests();
        const int ceiling = 2;
        var analyzer = new Mock<IProjectAnalyzer>();
        var inFlight = 0;
        var peak = 0;
        var paths = Enumerable.Range(0, 6)
            .Select(i => Path.Combine(Path.GetTempPath(), $"conc-{i}", $"P{i}.csproj"))
            .ToList();

        foreach (var path in paths)
        {
            analyzer.Setup(a => a.ScanOutdatedPackagesAsync(path, false, false, null))
                .Returns(async () =>
                {
                    var now = Interlocked.Increment(ref inFlight);
                    InterlockedMax(ref peak, now);
                    await Task.Delay(30);
                    Interlocked.Decrement(ref inFlight);
                    return (new List<OutdatedPackageInfo>(), true);
                });
        }

        var source = new RestoreBackedUpdateCandidateSource(analyzer.Object);
        await source.FindAsync(
            paths,
            new Dictionary<string, HashSet<string>> { ["Pkg"] = ["1.0.0"] },
            false,
            false,
            ceiling);

        peak.Should().BeLessThanOrEqualTo(ceiling);
        peak.Should().BeGreaterThan(1, "distinct directories must scan together");
    }

    [Fact]
    public async Task FindAsync_CountsUniqueTransitivePackagesWhenRequested()
    {
        var analyzer = new Mock<IProjectAnalyzer>();
        var project = Path.Combine(Path.GetTempPath(), "trans-dir", "App.csproj");
        analyzer.Setup(a => a.ScanOutdatedPackagesAsync(project, true, false, null))
            .ReturnsAsync(([], true));
        analyzer.Setup(a => a.ScanTransitivePackagesAsync(project))
            .ReturnsAsync(
                ([
                    new PackageReference("A", "1.0.0", project, "App.csproj", IsTransitive: true),
                    new PackageReference("A", "1.1.0", project, "App.csproj", IsTransitive: true),
                    new PackageReference("B", "2.0.0", project, "App.csproj", IsTransitive: true),
                ], true));

        var source = new RestoreBackedUpdateCandidateSource(analyzer.Object);
        var scan = await source.FindAsync(
            [project],
            new Dictionary<string, HashSet<string>> { ["Newtonsoft.Json"] = ["13.0.3"] },
            includeTransitive: true,
            includePrerelease: false,
            maxParallelism: 1);

        scan.TransitivePackagesFound.Should().Be(2);
        scan.TransitiveScanFailed.Should().BeFalse();
    }

    [Fact]
    public async Task FindAsync_TransitiveInventoryFailure_IsFlaggedWithoutPoisoningDirects()
    {
        var analyzer = new Mock<IProjectAnalyzer>();
        var project = Path.Combine(Path.GetTempPath(), "fail-trans", "App.csproj");
        analyzer.Setup(a => a.ScanOutdatedPackagesAsync(project, true, false, null))
            .ReturnsAsync(
                ([new OutdatedPackageInfo("Newtonsoft.Json", "13.0.1", "13.0.3", project, "App.csproj")], true));
        analyzer.Setup(a => a.ScanTransitivePackagesAsync(project))
            .ReturnsAsync(([], false));

        var source = new RestoreBackedUpdateCandidateSource(analyzer.Object);
        var scan = await source.FindAsync(
            [project],
            new Dictionary<string, HashSet<string>> { ["Newtonsoft.Json"] = ["13.0.1"] },
            includeTransitive: true,
            includePrerelease: false,
            maxParallelism: 1);

        scan.TransitiveScanFailed.Should().BeTrue();
        scan.TransitivePackagesFound.Should().Be(0);
        scan.Updates.Should().ContainSingle(u => u.PackageName == "Newtonsoft.Json");
        scan.UnscannedProjects.Should().BeEmpty();
    }

    private static void InterlockedMax(ref int location, int value)
    {
        int snapshot;
        do
        {
            snapshot = Volatile.Read(ref location);
            if (snapshot >= value)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref location, value, snapshot) != snapshot);
    }
}
