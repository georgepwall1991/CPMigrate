using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Migration;
using FluentAssertions;
using Moq;

namespace CPMigrate.Tests.Services.Migration;

public class MigrationDisplayTests
{
    private readonly Mock<IConsoleService> _mockConsole;
    private readonly MigrationDisplay _display;

    public MigrationDisplayTests()
    {
        _mockConsole = new Mock<IConsoleService>();
        _display = new MigrationDisplay(_mockConsole.Object);
    }

    [Fact]
    public void ShowDryRunBannerIfNeeded_DryRunTrue_ShowsBanner()
    {
        var options = new Options { DryRun = true };
        _display.ShowDryRunBannerIfNeeded(options);
        _mockConsole.Verify(c => c.DryRun(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void ShowDryRunBannerIfNeeded_DryRunFalse_DoesNotShowBanner()
    {
        var options = new Options { DryRun = false };
        _display.ShowDryRunBannerIfNeeded(options);
        _mockConsole.Verify(c => c.DryRun(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ShowDiscoveredProjects_ShowsCountAndPaths()
    {
        var paths = new List<string> { "/a/p1.csproj", "/a/b/p2.csproj" };
        _display.ShowDiscoveredProjects("/a", paths);
        _mockConsole.Verify(c => c.Info(It.Is<string>(s => s.Contains("2 project(s)"))), Times.Once);
        _mockConsole.Verify(c => c.Dim(It.Is<string>(s => s.Contains("p1.csproj"))), Times.Once);
        _mockConsole.Verify(c => c.Dim(It.Is<string>(s => s.Contains("b/p2.csproj"))), Times.Once);
    }

    [Fact]
    public void ShowMigrationSummary_ShowsCorrectStats()
    {
        _display.ShowMigrationSummary(5, 10, 2, "props", false);
        _mockConsole.Verify(c => c.Success(It.IsAny<string>()), Times.Once);
        _mockConsole.Verify(c => c.Info(It.Is<string>(s => s.Contains("5"))), Times.Once);
        _mockConsole.Verify(c => c.Info(It.Is<string>(s => s.Contains("10"))), Times.Once);
        _mockConsole.Verify(c => c.Info(It.Is<string>(s => s.Contains("2"))), Times.Once);
        _mockConsole.Verify(c => c.Info(It.Is<string>(s => s.Contains("props"))), Times.Once);
    }

    [Fact]
    public void CreateAlreadyMigratedResult_ReturnsCorrectResult()
    {
        var result = _display.CreateAlreadyMigratedResult("path");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.PropsFilePath.Should().Be("path");
        _mockConsole.Verify(c => c.Info(It.Is<string>(s => s.Contains("already migrated"))), Times.Once);
    }
}
