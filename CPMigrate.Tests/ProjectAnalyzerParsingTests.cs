using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Build.Exceptions;
using Xunit;

namespace CPMigrate.Tests;

public class ProjectAnalyzerParsingTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly ProjectAnalyzer _analyzer;

    public ProjectAnalyzerParsingTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateParsingTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _analyzer = new ProjectAnalyzer(new FakeConsoleService());
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void DiscoverProjectsFromSolution_ShouldFind_CSharp_FSharp_And_VB_Projects()
    {
        // Arrange
        var solutionPath = Path.Combine(_testDirectory, "TestSolution.sln");
        var projectCsPath = Path.Combine(_testDirectory, "ProjectCS", "ProjectCS.csproj");
        var projectFsPath = Path.Combine(_testDirectory, "ProjectFS", "ProjectFS.fsproj");
        var projectVbPath = Path.Combine(_testDirectory, "ProjectVB", "ProjectVB.vbproj");

        Directory.CreateDirectory(Path.GetDirectoryName(projectCsPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(projectFsPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(projectVbPath)!);

        File.WriteAllText(projectCsPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(projectFsPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(projectVbPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        // Create a minimal valid SLN file content using concatenation
        var slnContent = "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
                         "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"ProjectCS\", \"ProjectCS\\ProjectCS.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\n" +
                         "EndProject\n" +
                         "Project(\"{F2A71F9B-5D33-465A-A702-920D77279786}\") = \"ProjectFS\", \"ProjectFS\\ProjectFS.fsproj\", \"{22222222-2222-2222-2222-222222222222}\"\n" +
                         "EndProject\n" +
                         "Project(\"{F184B08F-C81C-45F6-A57F-5ABD9991F28F}\") = \"ProjectVB\", \"ProjectVB\\ProjectVB.vbproj\", \"{33333333-3333-3333-3333-333333333333}\"\n" +
                         "EndProject\n" +
                         "Global\n" +
                         "EndGlobal";

        File.WriteAllText(solutionPath, slnContent);

        // Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution(solutionPath);

        // Assert
        projectPaths.Should().HaveCount(3);
        projectPaths.Should().Contain(p => p.EndsWith("ProjectCS.csproj"));
        projectPaths.Should().Contain(p => p.EndsWith("ProjectFS.fsproj"));
        projectPaths.Should().Contain(p => p.EndsWith("ProjectVB.vbproj"));
    }

    [Fact]
    public void DiscoverProjectsFromSolution_ShouldHandle_DifferentPathSeparators()
    {
        // Arrange
        var solutionPath = Path.Combine(_testDirectory, "Backslash.sln");
        var projectPath = Path.Combine(_testDirectory, "SubDir", "MyProject.csproj");
        
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        File.WriteAllText(projectPath, "<Project></Project>");

        // Use backslashes in SLN even on non-Windows
        var slnContent = "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
                         "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"MyProject\", \"SubDir\\MyProject.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\n" +
                         "EndProject\n" +
                         "Global\n" +
                         "EndGlobal";
        File.WriteAllText(solutionPath, slnContent);

        // Act
        var (_, projectPaths) = _analyzer.DiscoverProjectsFromSolution(solutionPath);

        // Assert
        projectPaths.Should().HaveCount(1);
        File.Exists(projectPaths[0]).Should().BeTrue();
    }

    [Fact]
    public void DiscoverProjectsFromSolution_ShouldThrow_OnInvalidSlnFile()
    {
        // Arrange
        var solutionPath = Path.Combine(_testDirectory, "Invalid.sln");
        File.WriteAllText(solutionPath, "This is not a valid solution file");

        // Act
        var action = () => _analyzer.DiscoverProjectsFromSolution(solutionPath);

        // Assert
        action.Should().Throw<InvalidProjectFileException>();
    }

    [Fact]
    public void DiscoverProjectsFromSolution_ShouldSkip_MissingFiles()
    {
        // Arrange
        var solutionPath = Path.Combine(_testDirectory, "MissingFile.sln");
        // Define a project but don't create the file
        var slnContent = "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
                         "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"MissingProject\", \"MissingProject.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\n" +
                         "EndProject\n" +
                         "Global\n" +
                         "EndGlobal";
        File.WriteAllText(solutionPath, slnContent);

        // Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution(solutionPath);

        // Assert
        projectPaths.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverProjectFromPath_ShouldSupport_FSharp_And_VB_Projects()
    {
        // Arrange
        var projectFsPath = Path.Combine(_testDirectory, "ProjectFS.fsproj");
        var projectVbPath = Path.Combine(_testDirectory, "ProjectVB.vbproj");
        
        File.WriteAllText(projectFsPath, "<Project></Project>");
        File.WriteAllText(projectVbPath, "<Project></Project>");

        // Act & Assert for F#
        var (_, pathsFs) = _analyzer.DiscoverProjectFromPath(projectFsPath);
        pathsFs.Should().ContainSingle().Which.Should().EndWith("ProjectFS.fsproj");

        // Act & Assert for VB
        var (_, pathsVb) = _analyzer.DiscoverProjectFromPath(projectVbPath);
        pathsVb.Should().ContainSingle().Which.Should().EndWith("ProjectVB.vbproj");
    }
}
