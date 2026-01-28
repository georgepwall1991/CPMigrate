using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Moq;
using Xunit;

namespace CPMigrate.Tests;

public class ProjectAnalyzerTransitiveTests
{
    private readonly FakeConsoleService _consoleService;
    private readonly Mock<IDotNetCliService> _mockDotNetCli;
    private readonly ProjectAnalyzer _analyzer;

    public ProjectAnalyzerTransitiveTests()
    {
        _consoleService = new FakeConsoleService();
        _mockDotNetCli = new Mock<IDotNetCliService>();
        _analyzer = new ProjectAnalyzer(_consoleService, _mockDotNetCli.Object);
    }

    [Fact]
    public async Task ScanTransitivePackagesAsync_ReturnsDependencies_WhenCliCommandSucceeds()
    {
        // Arrange
        var projectPath = "/test/Project.csproj";
        // Create an output that matches the regex logic in ProjectAnalyzer.Transitive.cs
        // logic: > PackageName ResolvedVersion
        // Regex: @">\s*([^\s]+)\s+([^\s]+)" (inside Transitive Package block)
        var output = @"
Project 'Test' has the following transitive packages
   [net8.0]: 
   Transitive Package      Resolved
   > PackageA              1.0.0
   > PackageB              2.0.0
";
        _mockDotNetCli.Setup(x => x.RunListPackageAsync(It.IsAny<string>(), true, false))
            .ReturnsAsync((output, true));

        // Act
        var result = await _analyzer.ScanTransitivePackagesAsync(projectPath);

        // Assert
        result.Success.Should().BeTrue();
        result.References.Should().HaveCount(2);
        result.References.Should().Contain(r => r.PackageName == "PackageA" && r.Version == "1.0.0" && r.IsTransitive);
        result.References.Should().Contain(r => r.PackageName == "PackageB" && r.Version == "2.0.0" && r.IsTransitive);
    }

    [Fact]
    public async Task ScanTransitivePackagesAsync_ReturnsEmpty_WhenCliCommandFails()
    {
        // Arrange
        var projectPath = "/test/Project.csproj";
        _mockDotNetCli.Setup(x => x.RunListPackageAsync(It.IsAny<string>(), true, false))
            .ReturnsAsync((string.Empty, false));

        // Act
        var result = await _analyzer.ScanTransitivePackagesAsync(projectPath);

        // Assert
        result.Success.Should().BeFalse();
        result.References.Should().BeEmpty();
    }

    [Fact]
    public async Task ScanTransitivePackagesAsync_HandlesExceptions_Gracefully()
    {
        // Arrange
        var projectPath = "/test/Project.csproj";
        _mockDotNetCli.Setup(x => x.RunListPackageAsync(It.IsAny<string>(), true, false))
            .ThrowsAsync(new Exception("Simulated CLI failure"));

        // Act
        var result = await _analyzer.ScanTransitivePackagesAsync(projectPath);

        // Assert
        result.Success.Should().BeFalse();
        result.References.Should().BeEmpty();
        _consoleService.OutputMessages.Should().Contain(m => m.Contains("Simulated CLI failure"));
    }

    [Fact]
    public async Task ScanVulnerabilitiesAsync_ReturnsVulnerabilities_WhenCliCommandSucceeds()
    {
         // Arrange
        var projectPath = "/test/Project.csproj";
        // Parsing logic expects: > PackageName Severity Id Resolved [Fixed]
        var output = @"
Project 'Test' has the following vulnerable packages
   [net8.0]: 
   Top-level Package      Severity   Advisory URL     Resolved   Fixed
   > PackageA             High       CVE-123          1.0.0      1.0.1
   > PackageB             Critical   GHSA-456         2.0.0      2.0.1
";
        _mockDotNetCli.Setup(x => x.RunListPackageAsync(It.IsAny<string>(), true, true))
            .ReturnsAsync((output, true));

        // Act
        var result = await _analyzer.ScanVulnerabilitiesAsync(projectPath);

        // Assert
        result.Success.Should().BeTrue();
        result.Vulnerabilities.Should().HaveCount(2);
        
        var v1 = result.Vulnerabilities.First(v => v.PackageName == "PackageA");
        v1.Severity.Should().Be("High");
        v1.Id.Should().Be("CVE-123");
        v1.ResolvedVersion.Should().Be("1.0.0");
        v1.FixedVersion.Should().Be("1.0.1");

        var v2 = result.Vulnerabilities.First(v => v.PackageName == "PackageB");
        v2.Severity.Should().Be("Critical");
    }

    [Fact]
    public async Task ScanVulnerabilitiesAsync_ReturnsEmpty_WhenCliCommandFails()
    {
        // Arrange
        var projectPath = "/test/Project.csproj";
        _mockDotNetCli.Setup(x => x.RunListPackageAsync(It.IsAny<string>(), true, true))
            .ReturnsAsync((string.Empty, false));

        // Act
        var result = await _analyzer.ScanVulnerabilitiesAsync(projectPath);

        // Assert
        result.Success.Should().BeFalse();
        result.Vulnerabilities.Should().BeEmpty();
    }

    [Fact]
    public async Task ScanVulnerabilitiesAsync_HandlesExceptions_Gracefully()
    {
        // Arrange
        var projectPath = "/test/Project.csproj";
        _mockDotNetCli.Setup(x => x.RunListPackageAsync(It.IsAny<string>(), true, true))
            .ThrowsAsync(new Exception("Simulated CLI failure"));

        // Act
        var result = await _analyzer.ScanVulnerabilitiesAsync(projectPath);

        // Assert
        result.Success.Should().BeFalse();
        result.Vulnerabilities.Should().BeEmpty();
        _consoleService.OutputMessages.Should().Contain(m => m.Contains("Simulated CLI failure"));
    }
}
