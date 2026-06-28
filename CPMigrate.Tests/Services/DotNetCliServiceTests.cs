using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

public class DotNetCliServiceTests
{
    [Fact]
    public void BuildListPackageArguments_WithAllOptions_BuildsCorrectCommand()
    {
        var options = new DotNetPackageListOptions
        {
            Vulnerable = true,
            IncludeTransitive = true
        };

        var args = DotNetCliService.BuildListPackageArguments(options, "\"project.csproj\"");

        args.Should().Be("list \"project.csproj\" package --format json --output-version 1 --vulnerable --include-transitive");
    }

    [Theory]
    [InlineData(true, false, false, "--vulnerable")]
    [InlineData(false, true, false, "--outdated")]
    [InlineData(false, false, true, "--deprecated")]
    public void BuildListPackageArguments_MutuallyExclusiveFlags_AddsOnlyOne(
        bool vulnerable, bool outdated, bool deprecated, string expectedFlag)
    {
        var options = new DotNetPackageListOptions
        {
            Vulnerable = vulnerable,
            Outdated = outdated,
            Deprecated = deprecated
        };

        var args = DotNetCliService.BuildListPackageArguments(options, null);

        args.Should().Contain(expectedFlag);
        args.Should().NotContainAll("--vulnerable", "--outdated", "--deprecated");
    }

    [Fact]
    public void BuildPackageListArguments_WithProject_BuildsCorrectCommand()
    {
        var options = new DotNetPackageListOptions
        {
            Outdated = true,
            IncludeTransitive = true,
            IncludePrerelease = true
        };

        var args = DotNetCliService.BuildPackageListArguments(options, "project.csproj");

        args.Should().Be("package list --project project.csproj --format json --output-version 1 --outdated --include-transitive --include-prerelease");
    }

    [Fact]
    public void ShouldTryPackageVerbFallback_KnownFallbackMessages_ReturnsTrue()
    {
        DotNetCliService.ShouldTryPackageVerbFallback("Unrecognized command or argument 'list'").Should().BeTrue();
        DotNetCliService.ShouldTryPackageVerbFallback("No executable found matching command \"dotnet-list\"").Should().BeTrue();
        DotNetCliService.ShouldTryPackageVerbFallback("Unknown command 'package'").Should().BeTrue();
    }

    [Fact]
    public void ShouldTryPackageVerbFallback_OtherMessage_ReturnsFalse()
    {
        DotNetCliService.ShouldTryPackageVerbFallback("Some random error").Should().BeFalse();
    }

    [Fact]
    public void CombineOutput_WithStdErr_CombinesBoth()
    {
        var combined = DotNetCliService.CombineOutput("out", "err");

        combined.Should().Be("out\nerr");
    }

    [Fact]
    public void CombineOutput_WithoutStdErr_ReturnsStdOut()
    {
        var combined = DotNetCliService.CombineOutput("out", string.Empty);

        combined.Should().Be("out");
    }

    [Fact]
    public void ResolveProjectListTarget_WithFile_ReturnsQuotedPathAndDirectory()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"DotNetCliTest_{Guid.NewGuid():N}.csproj");
        File.WriteAllText(filePath, "<Project />");

        try
        {
            var (targetArg, workingDir) = DotNetCliService.ResolveProjectListTarget(filePath);

            targetArg.Should().Be($"\"{Path.GetFullPath(filePath)}\"");
            workingDir.Should().Be(Path.GetDirectoryName(Path.GetFullPath(filePath)));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void ResolveProjectListTarget_WithDirectory_ReturnsNullTargetAndFullPath()
    {
        var (targetArg, workingDir) = DotNetCliService.ResolveProjectListTarget(".");

        targetArg.Should().BeNull();
        workingDir.Should().Be(Path.GetFullPath("."));
    }
}
