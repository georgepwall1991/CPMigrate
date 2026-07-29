using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Models;

/// <summary>
/// Findings identify projects by their path relative to the scan root. Two properties matter: the
/// value must distinguish projects that share a file name, and it must be identical on every machine
/// — a committed baseline is matched against runs on developer laptops and CI runners alike.
/// </summary>
public class ProjectIdentityTests
{
    [Fact]
    public void ProjectId_IsRelativeToTheScanRootAndUsesForwardSlashes()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");
        var info = new ProjectPackageInfo(Array.Empty<PackageReference>(), BasePath: root);

        var id = info.ProjectId(Path.Combine(root, "src", "Api", "Api.csproj"));

        id.Should().Be("src/Api/Api.csproj");
    }

    [Fact]
    public void ProjectId_DistinguishesProjectsSharingAFileName()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");
        var info = new ProjectPackageInfo(Array.Empty<PackageReference>(), BasePath: root);

        var source = info.ProjectId(Path.Combine(root, "src", "App", "App.csproj"));
        var test = info.ProjectId(Path.Combine(root, "tests", "App", "App.csproj"));

        source.Should().NotBe(test);
    }

    [Fact]
    public void ProjectId_ContainsNoAbsolutePath_SoBaselinesArePortable()
    {
        var root = Path.Combine(Path.GetTempPath(), "some-machine-specific-checkout");
        var info = new ProjectPackageInfo(Array.Empty<PackageReference>(), BasePath: root);

        var id = info.ProjectId(Path.Combine(root, "src", "Api", "Api.csproj"));

        id.Should().NotContain(Path.GetTempPath()).And.NotContain("some-machine-specific-checkout");
    }

    [Fact]
    public void ProjectId_ProjectOutsideTheRoot_FallsBackToTheFileNameRatherThanAnAbsolutePath()
    {
        // '..' segments are not valid in a SARIF artifact URI and an absolute path would make a
        // committed baseline machine-specific, so the name is the least-bad answer.
        var root = Path.Combine(Path.GetTempPath(), "repo", "build");
        var info = new ProjectPackageInfo(Array.Empty<PackageReference>(), BasePath: root);

        var id = info.ProjectId(Path.Combine(Path.GetTempPath(), "elsewhere", "Api.csproj"));

        id.Should().Be("Api.csproj");
    }

    [Fact]
    public void ProjectId_WithoutAScanRoot_FallsBackToTheFileName()
    {
        var info = new ProjectPackageInfo(Array.Empty<PackageReference>());

        info.ProjectId(Path.Combine("any", "where", "Api.csproj")).Should().Be("Api.csproj");
    }

    [Fact]
    public void ProjectId_EmptyPath_IsEmpty()
    {
        new ProjectPackageInfo(Array.Empty<PackageReference>()).ProjectId("").Should().BeEmpty();
    }
}
