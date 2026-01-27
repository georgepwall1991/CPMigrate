using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Tests for ProjectAnalyzer covering project discovery, solution parsing, and package extraction.
/// Hunting for bugs in file discovery, path handling, and edge cases.
/// </summary>
public class ProjectAnalyzerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly FakeConsoleService _console;
    private readonly ProjectAnalyzer _analyzer;

    public ProjectAnalyzerTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateProjectAnalyzerTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _console = new FakeConsoleService();
        _analyzer = new ProjectAnalyzer(_console);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    #region DiscoverProjectsFromSolution Tests

    [Fact]
    public void DiscoverProjectsFromSolution_ValidSolutionFile_FindsProjects()
    {
        // Arrange
        var project1 = Path.Combine(_testDirectory, "Project1.csproj");
        var project2 = Path.Combine(_testDirectory, "Project2.csproj");
        CreateMinimalProject(project1);
        CreateMinimalProject(project2);

        var solutionPath = CreateTestSolution("Test.sln", project1, project2);

        // Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution(solutionPath);

        // Assert
        basePath.Should().Be(_testDirectory);
        projectPaths.Should().HaveCount(2);
        projectPaths.Should().Contain(project1);
        projectPaths.Should().Contain(project2);
    }

    [Fact]
    public void DiscoverProjectsFromSolution_NonExistentFile_ReturnsEmpty()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "NonExistent.sln");

        // Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution(nonExistentPath);

        // Assert
        basePath.Should().BeEmpty();
        projectPaths.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverProjectsFromSolution_DirectoryWithNoSolution_ReturnsEmpty()
    {
        // Arrange - empty directory with no solution files

        // Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution(_testDirectory);

        // Assert
        basePath.Should().BeEmpty();
        projectPaths.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverProjectsFromSolution_DirectoryWithOneSolution_UsesItAutomatically()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "Project1.csproj");
        CreateMinimalProject(projectPath);
        var solutionPath = CreateTestSolution("Test.sln", projectPath);

        // Act - Pass directory instead of file
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution(_testDirectory);

        // Assert
        basePath.Should().Be(_testDirectory);
        projectPaths.Should().HaveCount(1);
        projectPaths[0].Should().Be(projectPath);
    }

    [Fact]
    public void DiscoverProjectsFromSolution_ProjectFilesMissing_WarnsAndSkips()
    {
        // Arrange
        var project1 = Path.Combine(_testDirectory, "Exists.csproj");
        var project2 = Path.Combine(_testDirectory, "Missing.csproj");
        CreateMinimalProject(project1);
        // Don't create project2

        var solutionPath = CreateTestSolution("Test.sln", project1, project2);

        // Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution(solutionPath);

        // Assert
        basePath.Should().Be(_testDirectory);
        projectPaths.Should().HaveCount(1); // Only existing project
        projectPaths.Should().Contain(project1);
    }

    [Fact]
    public void DiscoverProjectsFromSolution_EmptySolution_ReturnsEmptyList()
    {
        // Arrange
        var solutionPath = CreateTestSolution("Empty.sln"); // No projects

        // Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution(solutionPath);

        // Assert
        basePath.Should().Be(_testDirectory);
        projectPaths.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverProjectsFromSolution_MixedProjectTypes_FindsAllSupportedTypes()
    {
        // Arrange
        var csproj = Path.Combine(_testDirectory, "CSharp.csproj");
        var fsproj = Path.Combine(_testDirectory, "FSharp.fsproj");
        var vbproj = Path.Combine(_testDirectory, "VisualBasic.vbproj");
        CreateMinimalProject(csproj);
        CreateMinimalProject(fsproj);
        CreateMinimalProject(vbproj);

        var solutionPath = CreateTestSolution("Test.sln", csproj, fsproj, vbproj);

        // Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution(solutionPath);

        // Assert
        projectPaths.Should().HaveCount(3);
        projectPaths.Should().Contain(csproj);
        projectPaths.Should().Contain(fsproj);
        projectPaths.Should().Contain(vbproj);
    }

    [Fact]
    public void DiscoverProjectsFromSolution_NonExistentDirectory_ReturnsEmpty()
    {
        var (basePath, projectPaths) = _analyzer.DiscoverProjectsFromSolution("/non/existent/path");

        projectPaths.Should().BeEmpty();
    }

    #endregion

    #region DiscoverProjectFromPath Tests

    [Fact]
    public void DiscoverProjectFromPath_ValidProjectFile_FindsProject()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        CreateMinimalProject(projectPath);

        // Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectFromPath(projectPath);

        // Assert
        basePath.Should().Be(_testDirectory);
        projectPaths.Should().HaveCount(1);
        projectPaths[0].Should().Be(projectPath);
    }

    [Fact]
    public void DiscoverProjectFromPath_DirectoryWithOneProject_FindsProject()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        CreateMinimalProject(projectPath);

        // Act - Pass directory instead of file
        var (basePath, projectPaths) = _analyzer.DiscoverProjectFromPath(_testDirectory);

        // Assert
        basePath.Should().Be(_testDirectory);
        projectPaths.Should().HaveCount(1);
        projectPaths[0].Should().Be(projectPath);
    }

    [Fact]
    public void DiscoverProjectFromPath_DirectoryWithMultipleProjects_FindsFirst()
    {
        // Arrange
        var project1 = Path.Combine(_testDirectory, "Project1.csproj");
        var project2 = Path.Combine(_testDirectory, "Project2.csproj");
        CreateMinimalProject(project1);
        CreateMinimalProject(project2);

        // Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectFromPath(_testDirectory);

        // Assert
        basePath.Should().Be(_testDirectory);
        projectPaths.Should().HaveCount(1);
        // Should find one of them (implementation uses EnumerateFiles which is not deterministic in order)
        projectPaths[0].Should().BeOneOf(project1, project2);
    }

    [Fact]
    public void DiscoverProjectFromPath_NonExistentFile_ReturnsEmpty()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "NonExistent.csproj");

        // Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectFromPath(nonExistentPath);

        // Assert
        basePath.Should().BeEmpty();
        projectPaths.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverProjectFromPath_DirectoryWithNoProjects_ReturnsEmpty()
    {
        // Arrange - empty directory

        // Act
        var (basePath, projectPaths) = _analyzer.DiscoverProjectFromPath(_testDirectory);

        // Assert
        basePath.Should().BeEmpty();
        projectPaths.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverProjectFromPath_NonExistentPath_ReturnsEmpty()
    {
        var (basePath, projectPaths) = _analyzer.DiscoverProjectFromPath("/non/existent/project.csproj");

        projectPaths.Should().BeEmpty();
    }

    #endregion

    #region GetTargetFramework Tests

    [Fact]
    public void GetTargetFramework_SingleTargetFramework_ReturnsFramework()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        var content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>";
        File.WriteAllText(projectPath, content);

        // Act
        var framework = ProjectAnalyzer.GetTargetFramework(projectPath);

        // Assert
        framework.Should().Be("net8.0");
    }

    [Fact]
    public void GetTargetFramework_MultipleTargetFrameworks_ReturnsFrameworks()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        var content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFrameworks>net6.0;net8.0</TargetFrameworks>
  </PropertyGroup>
</Project>";
        File.WriteAllText(projectPath, content);

        // Act
        var framework = ProjectAnalyzer.GetTargetFramework(projectPath);

        // Assert
        framework.Should().Be("net6.0;net8.0");
    }

    [Fact]
    public void GetTargetFramework_NoTargetFramework_ReturnsUnknown()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        var content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
  </PropertyGroup>
</Project>";
        File.WriteAllText(projectPath, content);

        // Act
        var framework = ProjectAnalyzer.GetTargetFramework(projectPath);

        // Assert
        framework.Should().Be("Unknown");
    }

    [Fact]
    public void GetTargetFramework_InvalidXml_ReturnsUnknown()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        File.WriteAllText(projectPath, "<Invalid>XML");

        // Act
        var framework = ProjectAnalyzer.GetTargetFramework(projectPath);

        // Assert
        framework.Should().Be("Unknown");
    }

    [Fact]
    public void GetTargetFramework_NonExistentFile_ReturnsUnknown()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "NonExistent.csproj");

        // Act
        var framework = ProjectAnalyzer.GetTargetFramework(nonExistentPath);

        // Assert
        framework.Should().Be("Unknown");
    }

    #endregion

    #region ProcessProject Tests

    [Fact]
    public void ProcessProject_WithPackageReferences_ExtractsVersionsAndRemovesThem()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        var content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
    <PackageReference Include=""Serilog"" Version=""3.0.0"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(projectPath, content);

        var packageVersions = new Dictionary<string, HashSet<string>>();

        // Act
        var modifiedContent = ProjectAnalyzer.ProcessProject(projectPath, packageVersions, keepVersionAttributes: false);

        // Assert
        packageVersions.Should().HaveCount(2);
        packageVersions["Newtonsoft.Json"].Should().Contain("13.0.1");
        packageVersions["Serilog"].Should().Contain("3.0.0");

        // Version attributes should be removed
        modifiedContent.Should().NotContain("Version=\"13.0.1\"");
        modifiedContent.Should().NotContain("Version=\"3.0.0\"");
        // But packages should still be present
        modifiedContent.Should().Contain("Newtonsoft.Json");
        modifiedContent.Should().Contain("Serilog");
    }

    [Fact]
    public void ProcessProject_KeepVersionAttributes_ExtractsButDoesNotRemove()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        var content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(projectPath, content);

        var packageVersions = new Dictionary<string, HashSet<string>>();

        // Act
        var modifiedContent = ProjectAnalyzer.ProcessProject(projectPath, packageVersions, keepVersionAttributes: true);

        // Assert
        packageVersions.Should().HaveCount(1);
        packageVersions["Newtonsoft.Json"].Should().Contain("13.0.1");

        // Version attributes should still be present
        modifiedContent.Should().Contain("Version=\"13.0.1\"");
    }

    [Fact]
    public void ProcessProject_NoPackageReferences_ReturnsOriginalContent()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        var content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>";
        File.WriteAllText(projectPath, content);

        var packageVersions = new Dictionary<string, HashSet<string>>();

        // Act
        var modifiedContent = ProjectAnalyzer.ProcessProject(projectPath, packageVersions);

        // Assert
        packageVersions.Should().BeEmpty();
        modifiedContent.Should().Contain("TargetFramework");
    }

    [Fact]
    public void ProcessProject_PackageWithoutVersion_Skips()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        var content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" />
    <PackageReference Include=""Serilog"" Version=""3.0.0"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(projectPath, content);

        var packageVersions = new Dictionary<string, HashSet<string>>();

        // Act
        var modifiedContent = ProjectAnalyzer.ProcessProject(projectPath, packageVersions);

        // Assert
        packageVersions.Should().HaveCount(1); // Only Serilog with version
        packageVersions.Should().ContainKey("Serilog");
        packageVersions.Should().NotContainKey("Newtonsoft.Json");
    }

    [Fact]
    public void ProcessProject_DuplicatePackageDifferentVersions_AccumulatesVersions()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        var content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""12.0.0"" />
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(projectPath, content);

        var packageVersions = new Dictionary<string, HashSet<string>>();

        // Act
        var modifiedContent = ProjectAnalyzer.ProcessProject(projectPath, packageVersions);

        // Assert
        packageVersions.Should().HaveCount(1);
        packageVersions["Newtonsoft.Json"].Should().HaveCount(2);
        packageVersions["Newtonsoft.Json"].Should().Contain("12.0.0");
        packageVersions["Newtonsoft.Json"].Should().Contain("13.0.1");
    }

    #endregion

    #region ScanProjectPackages Tests

    [Fact]
    public void ScanProjectPackages_ValidProject_ExtractsPackageReferences()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        var content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
    <PackageReference Include=""Serilog"" Version=""3.0.0"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(projectPath, content);

        // Act
        var (references, success) = _analyzer.ScanProjectPackages(projectPath);

        // Assert
        success.Should().BeTrue();
        references.Should().HaveCount(2);
        references.Should().Contain(r => r.PackageName == "Newtonsoft.Json" && r.Version == "13.0.1");
        references.Should().Contain(r => r.PackageName == "Serilog" && r.Version == "3.0.0");
        references.All(r => r.ProjectPath == projectPath).Should().BeTrue();
    }

    [Fact]
    public void ScanProjectPackages_NoPackageReferences_ReturnsEmptyList()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        var content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>";
        File.WriteAllText(projectPath, content);

        // Act
        var (references, success) = _analyzer.ScanProjectPackages(projectPath);

        // Assert
        success.Should().BeTrue();
        references.Should().BeEmpty();
    }

    [Fact]
    public void ScanProjectPackages_InvalidXml_ReturnsFalse()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        File.WriteAllText(projectPath, "<Invalid>XML");

        // Act
        var (references, success) = _analyzer.ScanProjectPackages(projectPath);

        // Assert
        success.Should().BeFalse();
        references.Should().BeEmpty();
    }

    [Fact]
    public void ScanProjectPackages_NonExistentFile_ReturnsFalse()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "NonExistent.csproj");

        // Act
        var (references, success) = _analyzer.ScanProjectPackages(nonExistentPath);

        // Assert
        success.Should().BeFalse();
        references.Should().BeEmpty();
    }

    [Fact]
    public void ScanProjectPackages_PopulatesDictionary_AccumulatesVersions()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "Test.csproj");
        var content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(projectPath, content);

        var packageVersions = new Dictionary<string, HashSet<string>>();

        // Act
        var success = _analyzer.ScanProjectPackages(projectPath, packageVersions);

        // Assert
        success.Should().BeTrue();
        packageVersions.Should().HaveCount(1);
        packageVersions["Newtonsoft.Json"].Should().Contain("13.0.1");
    }

    #endregion

    #region GetSolutionFiles Tests

    [Fact]
    public void GetSolutionFiles_DirectoryWithSlnFiles_FindsThem()
    {
        // Arrange
        var sln1 = Path.Combine(_testDirectory, "Solution1.sln");
        var sln2 = Path.Combine(_testDirectory, "Solution2.sln");
        File.WriteAllText(sln1, "");
        File.WriteAllText(sln2, "");

        // Act
        var solutionFiles = ProjectAnalyzer.GetSolutionFiles(_testDirectory);

        // Assert
        solutionFiles.Should().HaveCount(2);
        solutionFiles.Should().Contain(sln1);
        solutionFiles.Should().Contain(sln2);
    }

    [Fact]
    public void GetSolutionFiles_DirectoryWithSlnxFiles_FindsThem()
    {
        // Arrange
        var slnx1 = Path.Combine(_testDirectory, "Solution1.slnx");
        var slnx2 = Path.Combine(_testDirectory, "Solution2.slnx");
        File.WriteAllText(slnx1, "");
        File.WriteAllText(slnx2, "");

        // Act
        var solutionFiles = ProjectAnalyzer.GetSolutionFiles(_testDirectory);

        // Assert
        solutionFiles.Should().HaveCount(2);
        solutionFiles.Should().Contain(slnx1);
        solutionFiles.Should().Contain(slnx2);
    }

    [Fact]
    public void GetSolutionFiles_DirectoryWithMixedFiles_FindsAll()
    {
        // Arrange
        var sln = Path.Combine(_testDirectory, "Solution.sln");
        var slnx = Path.Combine(_testDirectory, "SolutionX.slnx");
        File.WriteAllText(sln, "");
        File.WriteAllText(slnx, "");

        // Act
        var solutionFiles = ProjectAnalyzer.GetSolutionFiles(_testDirectory);

        // Assert
        solutionFiles.Should().HaveCount(2);
        solutionFiles.Should().Contain(sln);
        solutionFiles.Should().Contain(slnx);
    }

    [Fact]
    public void GetSolutionFiles_EmptyDirectory_ReturnsEmpty()
    {
        // Arrange - empty directory

        // Act
        var solutionFiles = ProjectAnalyzer.GetSolutionFiles(_testDirectory);

        // Assert
        solutionFiles.Should().BeEmpty();
    }

    #endregion

    #region Helper Methods

    private void CreateMinimalProject(string projectPath)
    {
        var content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>";
        File.WriteAllText(projectPath, content);
    }

    private string CreateTestSolution(string solutionName, params string[] projectPaths)
    {
        var solutionPath = Path.Combine(_testDirectory, solutionName);
        var solutionContent = @"
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
";

        foreach (var projectPath in projectPaths)
        {
            var projectGuid = Guid.NewGuid().ToString("B").ToUpper();
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var relativePath = Path.GetRelativePath(Path.GetDirectoryName(solutionPath)!, projectPath);

            solutionContent += $@"Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}"") = ""{projectName}"", ""{relativePath}"", ""{projectGuid}""
EndProject
";
        }

        solutionContent += @"Global
    GlobalSection(SolutionConfigurationPlatforms) = preSolution
        Debug|Any CPU = Debug|Any CPU
        Release|Any CPU = Release|Any CPU
    EndGlobalSection
EndGlobal
";

        File.WriteAllText(solutionPath, solutionContent);
        return solutionPath;
    }

    #endregion
}
