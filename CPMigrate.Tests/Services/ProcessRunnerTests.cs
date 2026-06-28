using System.Diagnostics;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

public class ProcessRunnerTests
{
    [Fact]
    public void Run_DotNetVersionCommand_ReturnsZeroExitCodeAndOutput()
    {
        // Arrange
        var runner = new ProcessRunner();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "--version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Act
        var (exitCode, output, error) = runner.Run(startInfo);

        // Assert
        exitCode.Should().Be(0);
        output.Should().NotBeNullOrEmpty();
        error.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Run_InvalidCommand_ReturnsNonZeroExitCode()
    {
        // Arrange
        var runner = new ProcessRunner();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "nonexistent-command-that-does-not-exist",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Act
        var (exitCode, output, error) = runner.Run(startInfo);

        // Assert
        exitCode.Should().NotBe(0);
    }
}
