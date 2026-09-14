using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

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

    [Fact]
    public async Task DiscoverProjectsDetailedAsync_ReportsMissingProjectsAsData()
    {
        // A project the solution names but the filesystem lacks must surface as data, not only as
        // a console warning — a machine-readable run silences that console, and a missing project
        // that vanishes entirely reads as "scanned, absent" about a project nobody opened.
        var projectDir = Path.Combine(_testDirectory, "Present");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(
            Path.Combine(projectDir, "Present.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"
        );

        var slnxPath = Path.Combine(_testDirectory, "Partial.slnx");
        File.WriteAllText(
            slnxPath,
            @"<Solution>
  <Project Path=""Present/Present.csproj"" />
  <Project Path=""Gone/Gone.csproj"" />
</Solution>"
        );

        var result = await _analyzer.DiscoverProjectsDetailedAsync(slnxPath);

        result.ProjectPaths.Should().ContainSingle();
        result.MissingProjects.Should().ContainSingle()
            .Which.Should().EndWith(Path.Combine("Gone", "Gone.csproj"));
    }

    [Fact]
    public void DiscoverProjectsFromSolution_DirectoryWithoutSolution_DiscoversProjectsRecursively()
    {
        // Pointing at a folder is a reasonable way to say "this workspace"; a directory holding
        // projects but no .sln/.slnx should scan them rather than report nothing.
        var appDir = Path.Combine(_testDirectory, "App");
        var libDir = Path.Combine(_testDirectory, "nested", "Lib");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(libDir);
        var appPath = Path.Combine(appDir, "App.csproj");
        var libPath = Path.Combine(libDir, "Lib.csproj");
        File.WriteAllText(appPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(libPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution(_testDirectory);

        projectPaths.Should().HaveCount(2);
        projectPaths.Should().Contain(Path.GetFullPath(appPath));
        projectPaths.Should().Contain(Path.GetFullPath(libPath));
        basePath.Should().Be(Path.GetFullPath(_testDirectory));
    }

    [Fact]
    public void DiscoverProjectsFromSolution_DirectoryWithoutSolution_SkipsBuildOutput()
    {
        // obj/bin carry generated project assets — discovering them would migrate files a build
        // regenerates.
        var appDir = Path.Combine(_testDirectory, "App");
        var objDir = Path.Combine(appDir, "obj");
        Directory.CreateDirectory(objDir);
        var appPath = Path.Combine(appDir, "App.csproj");
        File.WriteAllText(appPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(
            Path.Combine(objDir, "Stray.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var (_, projectPaths) = _analyzer.DiscoverProjectsFromSolution(_testDirectory);

        projectPaths.Should().ContainSingle().Which.Should().Be(Path.GetFullPath(appPath));
    }

    [Fact]
    public void DiscoverProjectsFromSolution_EmptyDirectory_ReportsNoProjects()
    {
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution(_testDirectory);

        projectPaths.Should().BeEmpty();
        basePath.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverProjectsFromSolution_ProjectFile_DiscoversSingleProject()
    {
        // A .csproj passed as the target is a one-project scope, not an unsupported format.
        var projectDir = Path.Combine(_testDirectory, "App");
        Directory.CreateDirectory(projectDir);
        var projectPath = Path.Combine(projectDir, "App.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution(projectPath);

        projectPaths.Should().ContainSingle().Which.Should().Be(Path.GetFullPath(projectPath));
        basePath.Should().Be(Path.GetFullPath(projectDir));
    }

    [Fact]
    public void DiscoverProjectsFromSolution_DirectoryWithSolution_StillPrefersSolution()
    {
        // The directory scan is a fallback for the no-solution case — a real solution keeps
        // defining the project set, including projects it deliberately leaves out.
        var inSlnDir = Path.Combine(_testDirectory, "InSln");
        var looseDir = Path.Combine(_testDirectory, "Loose");
        Directory.CreateDirectory(inSlnDir);
        Directory.CreateDirectory(looseDir);
        var inSlnPath = Path.Combine(inSlnDir, "InSln.csproj");
        File.WriteAllText(inSlnPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(
            Path.Combine(looseDir, "Loose.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(
            Path.Combine(_testDirectory, "Test.slnx"),
            @"<Solution>
  <Project Path=""InSln/InSln.csproj"" />
</Solution>");

        var (_, projectPaths) = _analyzer.DiscoverProjectsFromSolution(_testDirectory);

        projectPaths.Should().ContainSingle().Which.Should().Be(Path.GetFullPath(inSlnPath));
    }
}
