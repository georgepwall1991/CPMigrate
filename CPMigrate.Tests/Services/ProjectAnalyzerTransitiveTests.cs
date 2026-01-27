using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

public class ProjectAnalyzerTransitiveTests
{
    [Fact]
    public void ParseTransitivePackages_ValidOutput_ReturnsReferences()
    {
        // Arrange
        var output = @"
Project 'MyProject' has the following package references
   [net8.0]: 
   Top-level Package      Requested   Resolved
   > Newtonsoft.Json      13.0.1      13.0.1  

   Transitive Package                                        Resolved
   > Microsoft.NETCore.Platforms                             1.1.0   
   > Microsoft.Win32.Primitives                              4.3.0   
";
        var projectFilePath = "/path/to/MyProject.csproj";
        var projectName = "MyProject.csproj";

        // Act
        var result = ProjectAnalyzer.ParseTransitivePackages(output, projectFilePath, projectName);

        // Assert
        result.Should().HaveCount(2);
        result[0].PackageName.Should().Be("Microsoft.NETCore.Platforms");
        result[0].Version.Should().Be("1.1.0");
        result[0].IsTransitive.Should().BeTrue();
        result[1].PackageName.Should().Be("Microsoft.Win32.Primitives");
        result[1].Version.Should().Be("4.3.0");
    }

    [Fact]
    public void ParseVulnerabilities_ValidOutput_ReturnsVulnerabilities()
    {
        // Arrange
        var output = @"
Project 'MyProject' has the following vulnerable packages
   [net8.0]: 
   Package                  Severity   Vulnerability      Resolved   Fixed in
   > System.Text.Json       High       GHSA-xxxx-xxxx     8.0.0      8.0.4   
";
        var projectName = "MyProject.csproj";

        // Act
        var result = ProjectAnalyzer.ParseVulnerabilities(output, projectName);

        // Assert
        result.Should().HaveCount(1);
        result[0].PackageName.Should().Be("System.Text.Json");
        result[0].Severity.Should().Be("High");
        result[0].Id.Should().Be("GHSA-xxxx-xxxx");
        result[0].ResolvedVersion.Should().Be("8.0.0");
        result[0].FixedVersion.Should().Be("8.0.4");
        result[0].ProjectName.Should().Be(projectName);
    }

    [Fact]
    public void ParseTransitivePackages_NoTransitiveHeader_ReturnsEmptyList()
    {
        // Arrange
        var output = @"
Project 'MyProject' has the following package references
   [net8.0]: 
   Top-level Package      Requested   Resolved
   > Newtonsoft.Json      13.0.1      13.0.1  
";
        var projectFilePath = "/path/to/MyProject.csproj";
        var projectName = "MyProject.csproj";

        // Act
        var result = ProjectAnalyzer.ParseTransitivePackages(output, projectFilePath, projectName);

        // Assert
        result.Should().BeEmpty();
    }
}
