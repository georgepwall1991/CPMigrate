using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Moq;

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
        var output = """
            {
              "version": 1,
              "projects": [
                {
                  "path": "/test/Project.csproj",
                  "frameworks": [
                    {
                      "framework": "net8.0",
                      "topLevelPackages": [
                        { "id": "TopPackage", "requestedVersion": "1.0.0", "resolvedVersion": "1.0.0" }
                      ],
                      "transitivePackages": [
                        { "id": "PackageA", "resolvedVersion": "1.0.0" },
                        { "id": "PackageB", "resolvedVersion": "2.0.0" }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        _mockDotNetCli
            .Setup(x => x.RunPackageListJsonAsync(
                It.IsAny<string>(),
                It.Is<DotNetPackageListOptions>(o => o.IncludeTransitive && !o.Vulnerable && !o.Outdated && !o.Deprecated)))
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
        _mockDotNetCli
            .Setup(x => x.RunPackageListJsonAsync(It.IsAny<string>(), It.IsAny<DotNetPackageListOptions>()))
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
        _mockDotNetCli
            .Setup(x => x.RunPackageListJsonAsync(It.IsAny<string>(), It.IsAny<DotNetPackageListOptions>()))
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
        var output = """
            {
              "version": 1,
              "projects": [
                {
                  "path": "/test/Project.csproj",
                  "frameworks": [
                    {
                      "framework": "net8.0",
                      "topLevelPackages": [
                        {
                          "id": "PackageA",
                          "resolvedVersion": "1.0.0",
                          "vulnerabilities": [
                            { "severity": "High", "advisoryurl": "CVE-123", "advisorytitle": "Issue A", "fixedVersion": "1.1.0" }
                          ]
                        }
                      ],
                      "transitivePackages": [
                        {
                          "id": "PackageB",
                          "resolvedVersion": "2.0.0",
                          "vulnerabilities": [
                            { "severity": "Critical", "advisoryurl": "GHSA-456", "advisorytitle": "Issue B", "fixedVersion": "2.1.0" }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        _mockDotNetCli
            .Setup(x => x.RunPackageListJsonAsync(
                It.IsAny<string>(),
                It.Is<DotNetPackageListOptions>(o => o.IncludeTransitive && o.Vulnerable)))
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
        v1.FixedVersion.Should().Be("1.1.0");

        var v2 = result.Vulnerabilities.First(v => v.PackageName == "PackageB");
        v2.Severity.Should().Be("Critical");
        v2.Id.Should().Be("GHSA-456");
        v2.FixedVersion.Should().Be("2.1.0");
    }

    [Fact]
    public async Task ScanVulnerabilitiesAsync_ReturnsEmpty_WhenCliCommandFails()
    {
        // Arrange
        var projectPath = "/test/Project.csproj";
        _mockDotNetCli
            .Setup(x => x.RunPackageListJsonAsync(It.IsAny<string>(), It.IsAny<DotNetPackageListOptions>()))
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
        _mockDotNetCli
            .Setup(x => x.RunPackageListJsonAsync(It.IsAny<string>(), It.IsAny<DotNetPackageListOptions>()))
            .ThrowsAsync(new Exception("Simulated CLI failure"));

        // Act
        var result = await _analyzer.ScanVulnerabilitiesAsync(projectPath);

        // Assert
        result.Success.Should().BeFalse();
        result.Vulnerabilities.Should().BeEmpty();
        _consoleService.OutputMessages.Should().Contain(m => m.Contains("Simulated CLI failure"));
    }

    [Fact]
    public async Task ScanResolvedPackagesAsync_UsesTopLevelPackages_ForCpmStyleProjects()
    {
        // Arrange
        var projectPath = "/test/Project.csproj";
        var output = """
            {
              "version": 1,
              "projects": [
                {
                  "path": "/test/Project.csproj",
                  "frameworks": [
                    {
                      "framework": "net8.0",
                      "topLevelPackages": [
                        { "id": "PackageA", "requestedVersion": "[1.0.0, )", "resolvedVersion": "1.2.3" },
                        { "id": "PackageB", "requestedVersion": "[4.0.0, )", "resolvedVersion": "4.5.6" }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        _mockDotNetCli
            .Setup(x => x.RunPackageListJsonAsync(
                It.IsAny<string>(),
                It.Is<DotNetPackageListOptions>(o => !o.IncludeTransitive && !o.Vulnerable && !o.Outdated && !o.Deprecated)))
            .ReturnsAsync((output, true));

        // Act
        var result = await _analyzer.ScanResolvedPackagesAsync(projectPath);

        // Assert
        result.Success.Should().BeTrue();
        result.References.Should().HaveCount(2);
        result.References.Should().Contain(r => r.PackageName == "PackageA" && r.Version == "1.2.3" && !r.IsTransitive);
        result.References.Should().Contain(r => r.PackageName == "PackageB" && r.Version == "4.5.6" && !r.IsTransitive);
    }
}
