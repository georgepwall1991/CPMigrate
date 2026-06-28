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
            if (Directory.Exists(temp)) Directory.Delete(temp);
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
        var options = new Options { OutputDir = ".", SolutionFileDir = "/repo/Contoso.sln" };
        var (outputPath, propsPath) = MigrationValidator.GetOutputPaths(options);
        outputPath.Should().Be("/repo");
        propsPath.Should().Be("/repo/Directory.Packages.props");
    }
}
