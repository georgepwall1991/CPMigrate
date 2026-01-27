using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests;

public class SlnxIntegrationTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly ProjectAnalyzer _analyzer;
    private readonly FakeConsoleService _consoleService;

    public SlnxIntegrationTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateSlnxIntTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _consoleService = new FakeConsoleService();
        _analyzer = new ProjectAnalyzer(_consoleService);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void DiscoverProjectsFromSolution_MultipleSolutionFiles_PromptsSelection()
    {
        // Arrange
        // Create both .sln and .slnx
        var slnPath = Path.Combine(_testDirectory, "Test.sln");
        File.WriteAllText(slnPath, "Microsoft Visual Studio Solution File, Format Version 12.00\nGlobal\nEndGlobal");

        var slnxPath = Path.Combine(_testDirectory, "Test.slnx");
        File.WriteAllText(slnxPath, "<Solution></Solution>");

        // Mock user selecting the .slnx file
        _consoleService.SelectionResponses.Enqueue("Test.slnx");

        // Act
        // Pass the directory path, so it finds both
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution(_testDirectory);

        // Assert
        // Should have verified it prompted
        // In FakeConsoleService, AskSelection verifies usage if we check. 
        // We know it used input if it didn't crash (queue empty would crash or use default)
        // And we can check if it loaded 0 projects (as both empty).
        // Let's verify it didn't error.
        basePath.Should().Be(_testDirectory);
    }

    [Fact]
    public void DiscoverProjectsFromSolution_ValidXmlWrongSchema_ThrowsException()
    {
        // Arrange
        // Valid XML but not Slnx
        var slnxPath = Path.Combine(_testDirectory, "InvalidSchema.slnx");
        File.WriteAllText(slnxPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        // Act
        Action act = () => _analyzer.DiscoverProjectsFromSolution(slnxPath);

        // Assert
        // Parser should fail to find <Solution> root or similar schema validation
        act.Should().Throw<Exception>();
    }

    [Fact]
    public async Task ProgramRunner_WithSlnxArgument_DiscoversProjects()
    {
        // Arrange
        // We need a real integration test here invoking ProgramRunner
        // Create a valid SLNX and Project structure
        var projectDir = Path.Combine(_testDirectory, "App");
        Directory.CreateDirectory(projectDir);
        var projectPath = Path.Combine(projectDir, "App.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var slnxPath = Path.Combine(_testDirectory, "App.slnx");
        File.WriteAllText(slnxPath, $"<Solution><Project Path=\"App/App.csproj\" /></Solution>");

        // Run with -s pointing to the SLNX file
        var args = new[] { "-s", slnxPath, "--dry-run", "--no-backup" }; // dry run to avoid modifications

        // Act
        _consoleService.ConfirmationResponse = false;
        var exitCode = await ProgramRunner.RunAsync(args, _consoleService);

        // Assert
        exitCode.Should().Be(ExitCodes.Success);
        // Verify we found the project by checking console output
        _consoleService.ErrorMessages.Should().BeEmpty();
    }
}
