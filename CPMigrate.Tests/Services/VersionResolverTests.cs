using CPMigrate.Services;
using FluentAssertions;
using Xunit;

namespace CPMigrate.Tests.Services;

public class VersionResolverTests
{
    private readonly VersionResolver _resolver = new();

    [Fact]
    public void DetectConflicts_NoConflicts_ReturnsEmptyList()
    {
        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            ["PackageA"] = new() { "1.0.0" },
            ["PackageB"] = new() { "2.0.0" }
        };

        var conflicts = _resolver.DetectConflicts(packageVersions);

        conflicts.Should().BeEmpty();
    }

    [Fact]
    public void DetectConflicts_WithConflicts_ReturnsConflictingPackages()
    {
        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            ["PackageA"] = new() { "1.0.0", "2.0.0" },
            ["PackageB"] = new() { "3.0.0" },
            ["PackageC"] = new() { "4.0.0", "5.0.0", "6.0.0" }
        };

        var conflicts = _resolver.DetectConflicts(packageVersions);

        conflicts.Should().HaveCount(2);
        conflicts.Should().Contain("PackageA");
        conflicts.Should().Contain("PackageC");
        conflicts.Should().NotContain("PackageB");
    }

    [Fact]
    public void DetectConflicts_ResultIsSortedAlphabetically()
    {
        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            ["Zulu"] = new() { "1.0.0", "2.0.0" },
            ["Alpha"] = new() { "1.0.0", "2.0.0" },
            ["Mango"] = new() { "1.0.0", "2.0.0" }
        };

        var conflicts = _resolver.DetectConflicts(packageVersions);

        conflicts.Should().BeInAscendingOrder();
    }

    [Fact]
    public void ResolveVersion_HighestStrategy_ReturnsHighestVersion()
    {
        var versions = new[] { "1.0.0", "3.0.0", "2.0.0" };

        var result = _resolver.ResolveVersion(versions, ConflictStrategy.Highest);

        result.Should().Be("3.0.0");
    }

    [Fact]
    public void ResolveVersion_LowestStrategy_ReturnsLowestVersion()
    {
        var versions = new[] { "1.0.0", "3.0.0", "2.0.0" };

        var result = _resolver.ResolveVersion(versions, ConflictStrategy.Lowest);

        result.Should().Be("1.0.0");
    }

    [Fact]
    public void ResolveVersion_WithPrereleaseVersions_ComparesNumericPartOnly()
    {
        var versions = new[] { "1.0.0", "2.0.0-beta", "1.5.0-alpha" };

        var result = _resolver.ResolveVersion(versions, ConflictStrategy.Highest);

        result.Should().Be("2.0.0-beta");
    }

    [Fact]
    public void ResolveVersion_WithMajorVersionDifference_ComparesCorrectly()
    {
        var versions = new[] { "2.0.0", "10.0.0", "1.0.0" };

        var result = _resolver.ResolveVersion(versions, ConflictStrategy.Highest);

        result.Should().Be("10.0.0");
    }
}
