using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Moq;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Pins the per-run deduplication of <c>dotnet list package</c> subprocess invocations. Each invocation
/// costs seconds of restore-dependent latency, and a single run routinely asks both the resolved and the
/// transitive question about the same project — those must share one subprocess. The cache is
/// shape-aware: a payload fetched without <c>--include-transitive</c> has no Transitive Packages section,
/// so a transitive request after a plain one upgrades with its own invocation rather than silently
/// reporting zero transitive packages. Vulnerable, outdated, and deprecated queries are different CLI
/// commands and stay uncached.
/// </summary>
public class DotNetPackageQueryServiceCacheTests
{
    private const string ProjectPath = "/repo/src/Api/Api.csproj";

    private const string TransitiveOutput = """
        {
          "version": 1,
          "projects": [
            {
              "path": "/repo/src/Api/Api.csproj",
              "frameworks": [
                {
                  "framework": "net10.0",
                  "topLevelPackages": [
                    { "id": "Serilog", "requestedVersion": "4.0.2", "resolvedVersion": "4.0.2" }
                  ],
                  "transitivePackages": [
                    { "id": "System.Diagnostics.DiagnosticSource", "resolvedVersion": "8.0.1" }
                  ]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task TransitiveViewThenResolvedView_InvokesSubprocessOnce_AndResolvedViewExcludesTransitiveRows()
    {
        var cli = new Mock<IDotNetCliService>();
        SetupSuccessfulList(cli, TransitiveOutput);
        var service = new DotNetPackageQueryService(new FakeConsoleService(), cli.Object);

        var (transitive, transitiveOk) = await service.ScanTransitivePackagesAsync(ProjectPath);
        var (resolved, resolvedOk) = await service.ScanResolvedPackagesAsync(ProjectPath);

        transitiveOk.Should().BeTrue();
        resolvedOk.Should().BeTrue();
        transitive.Select(r => r.PackageName).Should().ContainSingle()
            .Which.Should().Be("System.Diagnostics.DiagnosticSource");
        resolved.Select(r => r.PackageName).Should().ContainSingle()
            .Which.Should().Be("Serilog");

        cli.Verify(
            c => c.RunPackageListJsonAsync(It.IsAny<string>(), It.IsAny<DotNetPackageListOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task RepeatedResolvedScans_InvokeSubprocessOnce()
    {
        var cli = new Mock<IDotNetCliService>();
        SetupSuccessfulList(cli, TransitiveOutput);
        var service = new DotNetPackageQueryService(new FakeConsoleService(), cli.Object);

        await service.ScanResolvedPackagesAsync(ProjectPath);
        await service.ScanResolvedPackagesAsync(ProjectPath);

        cli.Verify(
            c => c.RunPackageListJsonAsync(It.IsAny<string>(), It.IsAny<DotNetPackageListOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task PlainScanThenTransitiveScan_UpgradesWithOneExtraInvocation_AndKeepsServingBothViews()
    {
        var cli = new Mock<IDotNetCliService>();
        SetupSuccessfulList(cli, TransitiveOutput);
        var service = new DotNetPackageQueryService(new FakeConsoleService(), cli.Object);

        var (first, _) = await service.ScanResolvedPackagesAsync(ProjectPath);
        var (transitive, transitiveOk) = await service.ScanTransitivePackagesAsync(ProjectPath);
        var (second, _) = await service.ScanResolvedPackagesAsync(ProjectPath);

        first.Should().NotBeEmpty();
        second.Should().Equal(first);
        transitiveOk.Should().BeTrue();
        transitive.Should().OnlyContain(r => r.IsTransitive);

        cli.Verify(
            c => c.RunPackageListJsonAsync(It.IsAny<string>(), It.IsAny<DotNetPackageListOptions>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task FailedScan_IsNotCached_NextCallerRetriesAndCanSucceed()
    {
        var cli = new Mock<IDotNetCliService>();
        cli.SetupSequence(c => c.RunPackageListJsonAsync(It.IsAny<string>(), It.IsAny<DotNetPackageListOptions>()))
            .ReturnsAsync((string.Empty, false))
            .ReturnsAsync((TransitiveOutput, true));
        var service = new DotNetPackageQueryService(new FakeConsoleService(), cli.Object);

        var (_, firstOk) = await service.ScanResolvedPackagesAsync(ProjectPath);
        var (references, secondOk) = await service.ScanResolvedPackagesAsync(ProjectPath);

        firstOk.Should().BeFalse();
        secondOk.Should().BeTrue();
        references.Should().NotBeEmpty();

        cli.Verify(
            c => c.RunPackageListJsonAsync(It.IsAny<string>(), It.IsAny<DotNetPackageListOptions>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ThrowingSubprocess_DoesNotPoisonCache_LaterCallRetries()
    {
        var cli = new Mock<IDotNetCliService>();
        cli.SetupSequence(c => c.RunPackageListJsonAsync(It.IsAny<string>(), It.IsAny<DotNetPackageListOptions>()))
            .ThrowsAsync(new InvalidOperationException("simulated CLI crash"))
            .ReturnsAsync((TransitiveOutput, true));
        var service = new DotNetPackageQueryService(new FakeConsoleService(), cli.Object);

        var (_, firstOk) = await service.ScanResolvedPackagesAsync(ProjectPath);
        var (references, secondOk) = await service.ScanResolvedPackagesAsync(ProjectPath);

        firstOk.Should().BeFalse();
        secondOk.Should().BeTrue();
        references.Should().ContainSingle();

        cli.Verify(
            c => c.RunPackageListJsonAsync(It.IsAny<string>(), It.IsAny<DotNetPackageListOptions>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ClearCache_ForcesFreshSubprocessInvocation()
    {
        var cli = new Mock<IDotNetCliService>();
        SetupSuccessfulList(cli, TransitiveOutput);
        var service = new DotNetPackageQueryService(new FakeConsoleService(), cli.Object);

        await service.ScanResolvedPackagesAsync(ProjectPath);
        service.ClearCache();
        await service.ScanResolvedPackagesAsync(ProjectPath);

        // This is the invalidation contract the fixer pass relies on: after files are rewritten, the
        // rescan must read the new restore output, not the cached pre-fix payload.
        cli.Verify(
            c => c.RunPackageListJsonAsync(It.IsAny<string>(), It.IsAny<DotNetPackageListOptions>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task DifferentIsolatedIntermediateDirectories_AreCachedSeparately()
    {
        var cli = new Mock<IDotNetCliService>();
        SetupSuccessfulList(cli, TransitiveOutput);
        var service = new DotNetPackageQueryService(new FakeConsoleService(), cli.Object);

        await service.ScanResolvedPackagesAsync(ProjectPath, isolatedIntermediateDirectory: "/tmp/iso-a");
        await service.ScanResolvedPackagesAsync(ProjectPath, isolatedIntermediateDirectory: "/tmp/iso-b");

        cli.Verify(
            c => c.RunPackageListJsonAsync(It.IsAny<string>(), It.IsAny<DotNetPackageListOptions>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ConcurrentResolvedScansOfOneProject_InvokeSubprocessOnce()
    {
        // The Lazy in the payload cache exists for the concurrent scan: --tree and --why fire one
        // resolved query per project across parallel directory groups, and callers that arrive while
        // the subprocess is still running must await its result rather than each paying for their own
        // restore. The mock's invocation delays long enough for every caller to pile up behind the
        // single in-flight query before it completes, so a second subprocess would be visible in the count.
        var cli = new Mock<IDotNetCliService>();
        var invocations = 0;
        cli.Setup(c => c.RunPackageListJsonAsync(It.IsAny<string>(), It.IsAny<DotNetPackageListOptions>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref invocations);
                await Task.Delay(50);
                return (TransitiveOutput, true);
            });
        var service = new DotNetPackageQueryService(new FakeConsoleService(), cli.Object);

        var scans = await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(_ => service.ScanResolvedPackagesAsync(ProjectPath))
        );

        scans.Should().OnlyContain(scan => scan.References.Select(r => r.PackageName).Contains("Serilog"));
        invocations.Should().Be(1);
    }

    private static void SetupSuccessfulList(Mock<IDotNetCliService> cli, string output)
    {
        cli.Setup(c => c.RunPackageListJsonAsync(It.IsAny<string>(), It.IsAny<DotNetPackageListOptions>()))
            .ReturnsAsync((output, true));
    }
}
