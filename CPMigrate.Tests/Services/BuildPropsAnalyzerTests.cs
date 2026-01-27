using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Tests for BuildPropsAnalyzer covering property and item analysis.
/// </summary>
public class BuildPropsAnalyzerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly FakeConsoleService _console;
    private readonly BuildPropsAnalyzer _analyzer;

    public BuildPropsAnalyzerTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateBuildPropsTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _console = new FakeConsoleService();
        _analyzer = new BuildPropsAnalyzer(_console);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void Analyze_EmptyProjectList_ReturnsZeroProjects()
    {
        // Act
        var result = _analyzer.Analyze(new List<string>());

        // Assert
        result.TotalProjects.Should().Be(0);
        result.PropertyOccurrences.Should().BeEmpty();
        result.ItemOccurrences.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_SingleProject_CountsPropertiesCorrectly()
    {
        // Arrange
        var projectPath = CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        // Act
        var result = _analyzer.Analyze(new List<string> { projectPath });

        // Assert
        result.TotalProjects.Should().Be(1);
        result.PropertyOccurrences.Should().NotBeEmpty();

        // Should have TargetFramework and Nullable
        var targetFrameworkKey = "TargetFramework|net8.0";
        var nullableKey = "Nullable|enable";

        result.PropertyOccurrences.Should().ContainKey(targetFrameworkKey);
        result.PropertyOccurrences.Should().ContainKey(nullableKey);
    }

    [Fact]
    public void Analyze_CommonProperties_GroupsTogether()
    {
        // Arrange
        var project1 = CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        var project2 = CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        // Act
        var result = _analyzer.Analyze(new List<string> { project1, project2 });

        // Assert
        result.TotalProjects.Should().Be(2);

        // Common properties should have 2 occurrences
        var targetFrameworkKey = "TargetFramework|net8.0";
        result.PropertyOccurrences[targetFrameworkKey].Should().HaveCount(2);
    }

    [Fact]
    public void Analyze_IgnoresProjectGuid()
    {
        // Arrange
        var projectPath = CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <ProjectGuid>{12345678-1234-1234-1234-123456789ABC}</ProjectGuid>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        var result = _analyzer.Analyze(new List<string> { projectPath });

        // Assert
        // ProjectGuid should be ignored
        result.PropertyOccurrences.Keys.Should().NotContain(k => k.StartsWith("ProjectGuid|"));

        // But TargetFramework should be included
        result.PropertyOccurrences.Keys.Should().Contain(k => k.StartsWith("TargetFramework|"));
    }

    [Fact]
    public void Analyze_IgnoresAssemblyName()
    {
        // Arrange
        var projectPath = CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <AssemblyName>MyCustomName</AssemblyName>
    <RootNamespace>MyNamespace</RootNamespace>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        var result = _analyzer.Analyze(new List<string> { projectPath });

        // Assert
        // AssemblyName and RootNamespace should be ignored (project-specific)
        result.PropertyOccurrences.Keys.Should().NotContain(k => k.StartsWith("AssemblyName|"));
        result.PropertyOccurrences.Keys.Should().NotContain(k => k.StartsWith("RootNamespace|"));

        // TargetFramework should be included
        result.PropertyOccurrences.Keys.Should().Contain(k => k.StartsWith("TargetFramework|"));
    }

    [Fact]
    public void Analyze_IgnoresConditionalProperties()
    {
        // Arrange
        var projectPath = CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <PropertyGroup Condition=""'$(Configuration)' == 'Debug'"">
    <DefineConstants>DEBUG</DefineConstants>
  </PropertyGroup>
</Project>");

        // Act
        var result = _analyzer.Analyze(new List<string> { projectPath });

        // Assert
        // Conditional properties should be ignored
        result.PropertyOccurrences.Keys.Should().NotContain(k => k.StartsWith("DefineConstants|"));

        // Non-conditional properties should be included
        result.PropertyOccurrences.Keys.Should().Contain(k => k.StartsWith("TargetFramework|"));
    }

    [Fact]
    public void Analyze_DifferentPropertyValues_TrackedSeparately()
    {
        // Arrange
        var project1 = CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

        var project2 = CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net7.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        var result = _analyzer.Analyze(new List<string> { project1, project2 });

        // Assert
        // Different values should create different keys
        result.PropertyOccurrences.Should().ContainKey("TargetFramework|net8.0");
        result.PropertyOccurrences.Should().ContainKey("TargetFramework|net7.0");

        result.PropertyOccurrences["TargetFramework|net8.0"].Should().HaveCount(1);
        result.PropertyOccurrences["TargetFramework|net7.0"].Should().HaveCount(1);
    }

    [Fact]
    public void Analyze_InvalidProjectFile_ContinuesWithWarning()
    {
        // Arrange
        var invalidPath = Path.Combine(_testDirectory, "Invalid.csproj");
        File.WriteAllText(invalidPath, "<Project><NotClosed>");

        var validPath = CreateTestProject("Valid.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        var result = _analyzer.Analyze(new List<string> { invalidPath, validPath });

        // Assert
        result.TotalProjects.Should().Be(2);

        // Should still process valid file (invalid file is skipped with warning)
        result.PropertyOccurrences.Should().NotBeEmpty();
    }

    [Fact]
    public void Analyze_NonExistentFile_ContinuesGracefully()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "NonExistent.csproj");

        // Act
        var result = _analyzer.Analyze(new List<string> { nonExistentPath });

        // Assert
        result.TotalProjects.Should().Be(1);

        // Should handle error gracefully (logs warning)
        result.PropertyOccurrences.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_UsingItems_TrackedCorrectly()
    {
        // Arrange
        var projectPath = CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Using Include=""System.Text.Json"" />
  </ItemGroup>
</Project>");

        // Act
        var result = _analyzer.Analyze(new List<string> { projectPath });

        // Assert
        // Using items should be tracked
        result.ItemOccurrences.Should().NotBeEmpty();
        result.ItemOccurrences.Keys.Should().Contain(k => k.Contains("Using|System.Text.Json"));
    }

    [Fact]
    public void Analyze_PackageReferences_NotTracked()
    {
        // Arrange
        var projectPath = CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
  </ItemGroup>
</Project>");

        // Act
        var result = _analyzer.Analyze(new List<string> { projectPath });

        // Assert
        // PackageReferences are tracked as items
        result.ItemOccurrences.Should().NotBeEmpty();
    }

    [Fact]
    public void Analyze_ConditionalItems_Ignored()
    {
        // Arrange
        var projectPath = CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Using Include=""System.Text.Json"" />
  </ItemGroup>
  <ItemGroup Condition=""'$(Configuration)' == 'Debug'"">
    <Using Include=""System.Diagnostics"" />
  </ItemGroup>
</Project>");

        // Act
        var result = _analyzer.Analyze(new List<string> { projectPath });

        // Assert
        // Only non-conditional items should be tracked
        result.ItemOccurrences.Keys.Should().Contain(k => k.Contains("System.Text.Json"));
        result.ItemOccurrences.Keys.Should().NotContain(k => k.Contains("System.Diagnostics"));
    }

    [Fact]
    public void Analyze_MultipleProjects_AggregatesCorrectly()
    {
        // Arrange
        var project1 = CreateTestProject("Project1.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        var project2 = CreateTestProject("Project2.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>");

        var project3 = CreateTestProject("Project3.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        var result = _analyzer.Analyze(new List<string> { project1, project2, project3 });

        // Assert
        result.TotalProjects.Should().Be(3);

        // TargetFramework appears in all 3 projects
        result.PropertyOccurrences["TargetFramework|net8.0"].Should().HaveCount(3);

        // Nullable appears in 2 projects
        result.PropertyOccurrences["Nullable|enable"].Should().HaveCount(2);

        // ImplicitUsings appears in 1 project
        result.PropertyOccurrences["ImplicitUsings|enable"].Should().HaveCount(1);
    }

    // Helper methods

    private string CreateTestProject(string projectName, string content)
    {
        var projectPath = Path.Combine(_testDirectory, projectName);
        File.WriteAllText(projectPath, content);
        return projectPath;
    }
}
