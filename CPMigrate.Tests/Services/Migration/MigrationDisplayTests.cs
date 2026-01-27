using System.Collections.Generic;
using CPMigrate.Models;
using CPMigrate.Services.Migration;
using CPMigrate.Services;
using Spectre.Console.Testing;
using FluentAssertions;
using Xunit;

namespace CPMigrate.Tests.Services.Migration;

public class MigrationDisplayTests
{
    private readonly TestConsole _console;
    private readonly SpectreConsoleService _consoleService;
    private readonly MigrationDisplay _display;

    public MigrationDisplayTests()
    {
        _console = new TestConsole().Interactive();
        _consoleService = new SpectreConsoleService(new VersionResolver(), _console);
        _display = new MigrationDisplay(_consoleService);
    }

    [Fact]
    public void ShowDryRunBannerIfNeeded_WhenDryRun_ShowsBanner()
    {
        var options = new Options { DryRun = true };
        _display.ShowDryRunBannerIfNeeded(options);
        _console.Output.Should().Contain("SIMULATION");
    }

    [Fact]
    public void ShowDiscoveredProjects_ShowsList()
    {
        var projects = new List<string> { "/root/p1.csproj", "/root/p2.csproj" };
        _display.ShowDiscoveredProjects("/root", projects);
        _console.Output.Should().Contain("Found 2 project(s)");
        _console.Output.Should().Contain("p1.csproj");
        _console.Output.Should().Contain("p2.csproj");
    }

    [Fact]
    public void ShowMigrationSummary_ShowsStats()
    {
        _display.ShowMigrationSummary(5, 20, 2, "Path/To/Props", false);
        _console.Output.Should().Contain("Migration completed successfully");
        _console.Output.Should().Contain("Projects processed: 5");
        _console.Output.Should().Contain("Packages centralized: 20");
        _console.Output.Should().Contain("Conflicts resolved: 2");
        _console.Output.Should().Contain("Path/To/Props");
    }

    [Fact]
    public void ShowPostMigrationGuidance_ShowsNextSteps()
    {
        var options = new Options { NoBackup = false };
        _console.Input.PushKey(System.ConsoleKey.Enter);
        _display.ShowPostMigrationGuidance(options, "Props/Path");
        _console.Output.Should().Contain("NEXT STEPS");
        _console.Output.Should().Contain("Props/Path");
        _console.Output.Should().Contain("backup was created");
    }

    [Fact]
    public void CreateAlreadyMigratedResult_ReturnsSuccessResultAndInformsUser()
    {
        var result = _display.CreateAlreadyMigratedResult("Props/Path");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.PropsFilePath.Should().Be("Props/Path");
        _console.Output.Should().Contain("already exists");
    }
}
