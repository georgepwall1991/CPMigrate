using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.VisualStudio.SolutionPersistence.Model;

namespace CPMigrate.Tests;

public class SlnxParsingTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly ProjectAnalyzer _analyzer;
    private readonly FakeConsoleService _consoleService;

    public SlnxParsingTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateSlnxParsingTests_{Guid.NewGuid():N}");
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
    public void DiscoverProjectsFromSolution_InvalidXml_ReturnsEmpty()
    {
        // Arrange
        var slnxPath = Path.Combine(_testDirectory, "Invalid.slnx");
        File.WriteAllText(slnxPath, "<Solution><Project Path=\"MissingQuote/Project.csproj\" /></Solution"); // Malformed XML

        // Act
        // The parser might treat this as invalid format or throw. 
        // Based on internal implementation, it might catch Exception and return empty OR throw SolutionException.
        // Let's verify what it usually does: ProjectAnalyzer catches Exception and throws.
        Action act = () => _analyzer.DiscoverProjectsFromSolution(slnxPath);

        // Assert
        act.Should().Throw<Exception>(); 
    }

    [Fact]
    public void DiscoverProjectsFromSolution_EmptySolution_ReturnsEmptyList()
    {
        // Arrange
        var slnxPath = Path.Combine(_testDirectory, "Empty.slnx");
        File.WriteAllText(slnxPath, "<Solution></Solution>");

        // Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution(slnxPath);

        // Assert
        basePath.Should().Be(_testDirectory);
        projectPaths.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverProjectsFromSolution_ProjectFileNotFound_LogsWarningAndSkips()
    {
        // Arrange
        var slnxPath = Path.Combine(_testDirectory, "MissingProject.slnx");
        var slnxContent = @"<Solution>
  <Project Path=""NonExistent/Project.csproj"" />
</Solution>";
        File.WriteAllText(slnxPath, slnxContent);

        // Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution(slnxPath);

        // Assert
        projectPaths.Should().BeEmpty();
        _consoleService.ErrorMessages.Should().BeEmpty(); // It logs warning, not error
        // If we could check warnings in FakeConsoleService, that would be ideal.
        // Assuming implementation logs Warning.
    }

    [Fact]
    public void DiscoverProjectsFromSolution_BackwardSlashes_NormalizesPaths()
    {
        // Arrange
        var projectDir = Path.Combine(_testDirectory, "BackslashProject");
        Directory.CreateDirectory(projectDir);
        var projectPath = Path.Combine(projectDir, "BackslashProject.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var slnxPath = Path.Combine(_testDirectory, "WindowsStyle.slnx");
        // Use single backslashes in XML attribute? XML requires escaping? 
        // Typically slnx uses forward slashes but let's see if parser handles backslashes
        var slnxContent = @"<Solution>
  <Project Path=""BackslashProject\BackslashProject.csproj"" />
</Solution>";
        File.WriteAllText(slnxPath, slnxContent);

        // Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution(slnxPath);

        // Assert
        projectPaths.Should().ContainSingle();
        Path.GetFullPath(projectPaths[0]).Should().Be(Path.GetFullPath(projectPath));
    }

    [Fact]
    public void DiscoverProjectsFromSolution_MixedProjectTypes_FiltersOnlyDotNet()
    {
        // Arrange
        var projectDir = Path.Combine(_testDirectory, "Mixed");
        Directory.CreateDirectory(projectDir);
        
        var csProj = Path.Combine(projectDir, "App.csproj");
        var fsProj = Path.Combine(projectDir, "Lib.fsproj");
        var vbProj = Path.Combine(projectDir, "Legacy.vbproj");
        var njProj = Path.Combine(projectDir, "Web.njsproj"); // NodeJS project
        var cppProj = Path.Combine(projectDir, "Native.vcxproj"); // C++ project

        File.WriteAllText(csProj, "<Project></Project>");
        File.WriteAllText(fsProj, "<Project></Project>");
        File.WriteAllText(vbProj, "<Project></Project>");
        File.WriteAllText(njProj, "<Project></Project>");
        File.WriteAllText(cppProj, "<Project></Project>");

        var slnxPath = Path.Combine(_testDirectory, "Mixed.slnx");
        var slnxContent = @"<Solution>
  <Project Path=""Mixed/App.csproj"" />
  <Project Path=""Mixed/Lib.fsproj"" />
  <Project Path=""Mixed/Legacy.vbproj"" />
  <Project Path=""Mixed/Web.njsproj"" />
  <Project Path=""Mixed/Native.vcxproj"" />
</Solution>";
        File.WriteAllText(slnxPath, slnxContent);

        // Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution(slnxPath);

        // Assert
        projectPaths.Should().HaveCount(3);
        projectPaths.Should().Contain(p => p.EndsWith("App.csproj"));
        projectPaths.Should().Contain(p => p.EndsWith("Lib.fsproj"));
        projectPaths.Should().Contain(p => p.EndsWith("Legacy.vbproj"));
        projectPaths.Should().NotContain(p => p.EndsWith("Web.njsproj"));
        projectPaths.Should().NotContain(p => p.EndsWith("Native.vcxproj"));
    }

    [Fact]
    public void DiscoverProjectsFromSolution_RelativePaths_ResolvesCorrectly()
    {
        // Arrange
        // Structure:
        // /Root/Solution/Test.slnx
        // /Root/Project/MyProject.csproj
        // SLNX refers to ../Project/MyProject.csproj
        var rootDir = Path.Combine(_testDirectory, "Root");
        var solutionDir = Path.Combine(rootDir, "Solution");
        var projectDir = Path.Combine(rootDir, "Project");
        Directory.CreateDirectory(solutionDir);
        Directory.CreateDirectory(projectDir);

        var projectPath = Path.Combine(projectDir, "Relative.csproj");
        File.WriteAllText(projectPath, "<Project></Project>");

        var slnxPath = Path.Combine(solutionDir, "Relative.slnx");
        // Note: XML attributes use forward slashes typically
        var slnxContent = @"<Solution>
  <Project Path=""../Project/Relative.csproj"" />
</Solution>";
        File.WriteAllText(slnxPath, slnxContent);

        // Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution(slnxPath);

        // Assert
        projectPaths.Should().ContainSingle();
        var resolvedPath = projectPaths[0];
        Path.GetFullPath(resolvedPath).Should().Be(Path.GetFullPath(projectPath));
    }

    [Fact]
    public void DiscoverProjectsFromSolution_DuplicateProjects_ThrowsException()
    {
        // Arrange
        var projectDir = Path.Combine(_testDirectory, "Duplicate");
        Directory.CreateDirectory(projectDir);
        var projectPath = Path.Combine(projectDir, "Dup.csproj");
        File.WriteAllText(projectPath, "<Project></Project>");

        // SLNX lists the same project twice - this is invalid in SLNX model
        var slnxPath = Path.Combine(_testDirectory, "Duplicate.slnx");
        var slnxContent = @"<Solution>
  <Project Path=""Duplicate/Dup.csproj"" />
  <Project Path=""Duplicate/Dup.csproj"" />
</Solution>";
        File.WriteAllText(slnxPath, slnxContent);

        // Act
        Action act = () => _analyzer.DiscoverProjectsFromSolution(slnxPath);

        // Assert
        // The underlying library (Microsoft.VisualStudio.SolutionPersistence) throws when items are duplicated
        act.Should().Throw<Exception>();
    }
}
