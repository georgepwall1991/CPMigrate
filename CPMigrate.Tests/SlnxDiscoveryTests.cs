using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CPMigrate.Tests;

public class SlnxDiscoveryTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly ProjectAnalyzer _analyzer;
    private readonly FakeConsoleService _consoleService;

    public SlnxDiscoveryTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateSlnxTests_{Guid.NewGuid():N}");
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
    public void DiscoverProjectsFromSolution_DiscoversProjectsInSlnx()
    {
        // Arrange
        // Create a fake project file
        var projectDir = Path.Combine(_testDirectory, "MyProject");
        Directory.CreateDirectory(projectDir);
        var projectPath = Path.Combine(projectDir, "MyProject.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        // Create a simple .slnx file
        var slnxPath = Path.Combine(_testDirectory, "Test.slnx");
        var slnxContent = @"<Solution>
  <Project Path=""MyProject/MyProject.csproj"" />
</Solution>";
        File.WriteAllText(slnxPath, slnxContent);

        // Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution(slnxPath);

        // Assert
        projectPaths.Should().ContainSingle();
        Path.GetFullPath(projectPaths[0]).Should().Be(Path.GetFullPath(projectPath));
    }

    [Fact]
    public void DiscoverProjectsFromSolution_HandlesSolutionFoldersInSlnx()
    {
        // Arrange
        var projectDir = Path.Combine(_testDirectory, "MyProject2");
        Directory.CreateDirectory(projectDir);
        var projectPath = Path.Combine(projectDir, "MyProject2.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var slnxPath = Path.Combine(_testDirectory, "WithFolders.slnx");
        var slnxContent = @"<Solution>
  <Folder Name=""/Solution Items/"">
    <File Path=""README.md"" />
  </Folder>
  <Project Path=""MyProject2/MyProject2.csproj"" />
</Solution>";
        File.WriteAllText(slnxPath, slnxContent);

        // Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution(slnxPath);

        // Assert
        projectPaths.Should().ContainSingle();
        Path.GetFullPath(projectPaths[0]).Should().Be(Path.GetFullPath(projectPath));
    }
}
