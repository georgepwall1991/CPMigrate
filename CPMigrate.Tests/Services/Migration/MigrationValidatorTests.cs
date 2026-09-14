using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Migration;
using FluentAssertions;
using Moq;

namespace CPMigrate.Tests.Services.Migration;

public class MigrationValidatorTests
{
    private readonly Mock<IConsoleService> _mockConsole;
    private readonly MigrationValidator _validator;

    public MigrationValidatorTests()
    {
        _mockConsole = new Mock<IConsoleService>();
        _validator = new MigrationValidator(_mockConsole.Object);
    }

    [Fact]
    public void TryValidate_ValidOptions_ReturnsTrue()
    {
        var options = new Options { SolutionFileDir = "." };
        var success = _validator.TryValidate(options, out var error);
        success.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidate_InvalidOptions_ReturnsFalse()
    {
        var options = new Options { OutputFile = "test.json", Output = OutputFormat.Terminal }; // Invalid combination
        var success = _validator.TryValidate(options, out var error);
        success.Should().BeFalse();
        error!.ExitCode.Should().Be(ExitCodes.ValidationError);
        _mockConsole.Verify(c => c.Error(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void ValidateOutputDirectory_DirectoryExists_ReturnsNull()
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(temp);
        try
        {
            var result = _validator.ValidateOutputDirectory(temp);
            result.Should().BeNull();
        }
        finally
        {
            Directory.Delete(temp);
        }
    }

    [Fact]
    public void ValidateOutputDirectory_DirectoryDoesNotExist_CreatesIt()
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var result = _validator.ValidateOutputDirectory(temp);
            result.Should().BeNull();
            Directory.Exists(temp).Should().BeTrue();
            _mockConsole.Verify(c => c.Info(It.Is<string>(s => s.Contains("Creating"))), Times.Once);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp);
            }
        }
    }

    [Fact]
    public void ValidateOutputDirectory_InvalidPath_ReturnsError()
    {
        var invalidPath = "/invalid/path/that/cannot/be/created/???/###";
        var result = _validator.ValidateOutputDirectory(invalidPath);
        result.Should().NotBeNull();
        result!.ExitCode.Should().Be(ExitCodes.FileOperationError);
        _mockConsole.Verify(c => c.Error(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void GetOutputPaths_PrefersOutputDir()
    {
        var options = new Options { OutputDir = "out", SolutionFileDir = "sln" };
        var (outputPath, propsPath) = MigrationValidator.GetOutputPaths(options);
        outputPath.Should().Be("out");
    }

    [Fact]
    public void GetOutputPaths_FallsBackToSolutionDir()
    {
        var options = new Options { OutputDir = ".", SolutionFileDir = "sln" };
        var (outputPath, propsPath) = MigrationValidator.GetOutputPaths(options);
        outputPath.Should().Be("sln");
    }

    [Fact]
    public void GetOutputPaths_SolutionFile_ResolvesToParentDirectory()
    {
        // The README's primary quickstart is `cpmigrate -s ./MySolution.sln`. When -s points
        // to a .sln file rather than a directory, the output Directory.Packages.props must be
        // written into the directory containing the solution, not into a directory literally
        // named "MySolution.sln" (which would collide with the existing file).
        // Build the paths with Path.Combine so the expectation uses the host's directory
        // separator — Path.GetDirectoryName normalizes to '\' on Windows.
        var repoDir = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "/", "repo");
        var solutionFile = Path.Combine(repoDir, "Contoso.sln");

        var options = new Options { OutputDir = ".", SolutionFileDir = solutionFile };
        var (outputPath, propsPath) = MigrationValidator.GetOutputPaths(options);
        outputPath.Should().Be(repoDir);
        propsPath.Should().Be(Path.Combine(repoDir, "Directory.Packages.props"));
    }

    [Fact]
    public void GetOutputPaths_PropsInAncestor_TargetsTheGoverningFile()
    {
        // NuGet walks up from each project for Directory.Packages.props and restore fails NU1507
        // when two sit in one ancestry. A props file above the solution directory is the file
        // already governing these projects — a second one next to the solution shadows nothing
        // and breaks every restore.
        var repoDir = Path.Combine(Path.GetTempPath(), $"CPMigrateAncestor_{Guid.NewGuid():N}");
        var srcDir = Path.Combine(repoDir, "src");
        Directory.CreateDirectory(srcDir);
        var ancestorProps = Path.Combine(repoDir, "Directory.Packages.props");
        File.WriteAllText(ancestorProps, "<Project />");

        try
        {
            var options = new Options { OutputDir = ".", SolutionFileDir = srcDir };
            var (outputPath, propsPath) = MigrationValidator.GetOutputPaths(options);

            outputPath.Should().Be(srcDir);
            propsPath.Should().Be(ancestorProps);
        }
        finally
        {
            Directory.Delete(repoDir, true);
        }
    }

    [Fact]
    public void GetOutputPaths_ExplicitOutputDir_HonoredVerbatim()
    {
        // -o is the caller's contract: an ancestor props file does not redirect an explicit
        // output directory.
        var repoDir = Path.Combine(Path.GetTempPath(), $"CPMigrateExplicit_{Guid.NewGuid():N}");
        var outDir = Path.Combine(repoDir, "out");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(repoDir, "Directory.Packages.props"), "<Project />");

        try
        {
            var options = new Options { OutputDir = outDir, SolutionFileDir = repoDir };
            var (_, propsPath) = MigrationValidator.GetOutputPaths(options);

            propsPath.Should().Be(Path.Combine(outDir, "Directory.Packages.props"));
        }
        finally
        {
            Directory.Delete(repoDir, true);
        }
    }

    [Fact]
    public void GetOutputPaths_DeclaredRedirect_TargetsDeclaredPath()
    {
        // DirectoryPackagesPropsPath points the import at a file of the repository's choosing —
        // creating the conventional name would produce a file NuGet never reads.
        var repoDir = Path.Combine(Path.GetTempPath(), $"CPMigrateRedirect_{Guid.NewGuid():N}");
        var srcDir = Path.Combine(repoDir, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(repoDir, "Directory.Build.props"), """
            <Project>
              <PropertyGroup>
                <DirectoryPackagesPropsPath>$(MSBuildThisFileDirectory)eng/Packages.props</DirectoryPackagesPropsPath>
              </PropertyGroup>
            </Project>
            """);

        try
        {
            var options = new Options { OutputDir = ".", SolutionFileDir = srcDir };
            var (_, propsPath) = MigrationValidator.GetOutputPaths(options);

            propsPath.Should().Be(Path.Combine(repoDir, "eng", "Packages.props"));
        }
        finally
        {
            Directory.Delete(repoDir, true);
        }
    }

    [Fact]
    public void GetOutputPaths_DeclaredRedirect_ExplicitOutputDir_StillTargetsDeclaredPath()
    {
        // -o chooses the output directory, but a declared redirect is stronger than convention:
        // NuGet imports the declared path, so a file written anywhere else is inert.
        var repoDir = Path.Combine(Path.GetTempPath(), $"CPMigrateRedirectOut_{Guid.NewGuid():N}");
        var outDir = Path.Combine(repoDir, "out");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(repoDir, "Directory.Build.props"), """
            <Project>
              <PropertyGroup>
                <DirectoryPackagesPropsPath>eng/Packages.props</DirectoryPackagesPropsPath>
              </PropertyGroup>
            </Project>
            """);

        try
        {
            var options = new Options { OutputDir = outDir, SolutionFileDir = repoDir };
            var (_, propsPath) = MigrationValidator.GetOutputPaths(options);

            propsPath.Should().Be(Path.Combine(repoDir, "eng", "Packages.props"));
        }
        finally
        {
            Directory.Delete(repoDir, true);
        }
    }

    [Fact]
    public void GetOutputPaths_NoPropsAnywhere_TargetsOutputDir()
    {
        var repoDir = Path.Combine(Path.GetTempPath(), $"CPMigrateNone_{Guid.NewGuid():N}");
        var srcDir = Path.Combine(repoDir, "src");
        Directory.CreateDirectory(srcDir);

        try
        {
            var options = new Options { OutputDir = ".", SolutionFileDir = srcDir };
            var (_, propsPath) = MigrationValidator.GetOutputPaths(options);

            propsPath.Should().Be(Path.Combine(srcDir, "Directory.Packages.props"));
        }
        finally
        {
            Directory.Delete(repoDir, true);
        }
    }
}
