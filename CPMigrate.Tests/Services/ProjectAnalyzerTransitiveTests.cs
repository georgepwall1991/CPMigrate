using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

public class ProjectAnalyzerTransitiveTests
{
    [Fact]
    public void ParsePackageReferencesFromJson_IncludeTransitiveFalse_ReturnsTopLevelOnly()
    {
        // Arrange
        var json = """
            {
              "version": 1,
              "projects": [
                {
                  "path": "/path/to/MyProject.csproj",
                  "frameworks": [
                    {
                      "framework": "net8.0",
                      "topLevelPackages": [
                        { "id": "Newtonsoft.Json", "resolvedVersion": "13.0.1" }
                      ],
                      "transitivePackages": [
                        { "id": "Microsoft.NETCore.Platforms", "resolvedVersion": "1.1.0" }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
        var projectFilePath = "/path/to/MyProject.csproj";
        var projectName = "MyProject.csproj";

        // Act
        var result = ProjectAnalyzer.ParsePackageReferencesFromJson(json, projectFilePath, projectName, includeTransitive: false);

        // Assert
        result.Should().ContainSingle();
        result[0].PackageName.Should().Be("Newtonsoft.Json");
        result[0].Version.Should().Be("13.0.1");
        result[0].IsTransitive.Should().BeFalse();
    }

    [Fact]
    public void ParsePackageReferencesFromJson_IncludeTransitiveTrue_ReturnsTopLevelAndTransitive()
    {
        // Arrange
        var json = """
            {
              "version": 1,
              "projects": [
                {
                  "path": "/path/to/MyProject.csproj",
                  "frameworks": [
                    {
                      "framework": "net8.0",
                      "topLevelPackages": [
                        { "id": "Newtonsoft.Json", "resolvedVersion": "13.0.1" }
                      ],
                      "transitivePackages": [
                        { "id": "Microsoft.NETCore.Platforms", "resolvedVersion": "1.1.0" }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
        var projectFilePath = "/path/to/MyProject.csproj";
        var projectName = "MyProject.csproj";

        // Act
        var result = ProjectAnalyzer.ParsePackageReferencesFromJson(json, projectFilePath, projectName, includeTransitive: true);

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(r => r.PackageName == "Newtonsoft.Json" && !r.IsTransitive);
        result.Should().Contain(r => r.PackageName == "Microsoft.NETCore.Platforms" && r.IsTransitive);
    }

    [Fact]
    public void ParseVulnerabilitiesFromJson_ValidOutput_ReturnsVulnerabilities()
    {
        // Arrange
        var json = """
            {
              "version": 1,
              "projects": [
                {
                  "path": "/path/to/MyProject.csproj",
                  "frameworks": [
                    {
                      "framework": "net8.0",
                      "topLevelPackages": [
                        {
                          "id": "System.Text.Json",
                          "resolvedVersion": "8.0.0",
                          "vulnerabilities": [
                            { "severity": "High", "advisoryurl": "GHSA-aaaa-bbbb" }
                          ]
                        }
                      ],
                      "transitivePackages": [
                        {
                          "id": "Package.Transitive",
                          "resolvedVersion": "1.0.0",
                          "vulnerabilities": [
                            { "severity": "Low", "advisoryurl": "CVE-0001" }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
        var projectName = "MyProject.csproj";

        // Act
        var result = ProjectAnalyzer.ParseVulnerabilitiesFromJson(json, projectName);

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(v => v.PackageName == "System.Text.Json" && v.Severity == "High" && v.Id == "GHSA-aaaa-bbbb");
        result.Should().Contain(v => v.PackageName == "Package.Transitive" && v.Severity == "Low" && v.Id == "CVE-0001");
    }

    [Fact]
    public void ParseVulnerabilitiesFromJson_ExtractsFixedVersionAndCaseInsensitiveFields()
    {
        // Arrange
        var json = """
            {
              "version": 1,
              "projects": [
                {
                  "path": "/path/to/MyProject.csproj",
                  "frameworks": [
                    {
                      "framework": "net8.0",
                      "topLevelPackages": [
                        {
                          "id": "System.Text.Json",
                          "resolvedVersion": "8.0.0",
                          "latestVersion": "8.0.4",
                          "vulnerabilities": [
                            { "severity": "High", "advisoryUrl": "GHSA-case-sensitive", "fixedVersion": "8.0.4" }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
        var projectName = "MyProject.csproj";

        // Act
        var result = ProjectAnalyzer.ParseVulnerabilitiesFromJson(json, projectName);

        // Assert
        result.Should().ContainSingle();
        var vulnerability = result[0];
        vulnerability.Id.Should().Be("GHSA-case-sensitive");
        vulnerability.FixedVersion.Should().Be("8.0.4");
    }

    [Fact]
    public void ParseVulnerabilitiesFromJson_PreservesLegacyFixedVersionFallbacks()
    {
        // Arrange
        var json = """
            {
              "version": 1,
              "projects": [
                {
                  "path": "/path/to/MyProject.csproj",
                  "frameworks": [
                    {
                      "framework": "net8.0",
                      "topLevelPackages": [
                        {
                          "id": "Patched.Array",
                          "resolvedVersion": "1.0.0",
                          "latestVersion": "1.0.9",
                          "vulnerabilities": [
                            { "severity": "High", "advisoryUrl": "GHSA-array", "patchedVersions": ["1.0.5", "1.0.6"] }
                          ]
                        },
                        {
                          "id": "Patched.First",
                          "resolvedVersion": "2.0.0",
                          "latestVersion": "2.0.9",
                          "vulnerabilities": [
                            { "severity": "High", "advisoryUrl": "GHSA-first", "firstPatchedVersion": "2.0.4" }
                          ]
                        },
                        {
                          "id": "Patched.Recommended",
                          "resolvedVersion": "3.0.0",
                          "latestVersion": "3.0.9",
                          "vulnerabilities": [
                            { "severity": "High", "advisoryUrl": "GHSA-recommended", "recommendedVersion": "3.0.7" }
                          ]
                        },
                        {
                          "id": "Patched.LatestFallback",
                          "resolvedVersion": "4.0.0",
                          "latestVersion": "4.0.8",
                          "vulnerabilities": [
                            { "severity": "High", "advisoryUrl": "GHSA-latest" }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        // Act
        var result = ProjectAnalyzer.ParseVulnerabilitiesFromJson(json, "MyProject.csproj");

        // Assert
        result.Should().Contain(v => v.Id == "GHSA-array" && v.FixedVersion == "1.0.5");
        result.Should().Contain(v => v.Id == "GHSA-first" && v.FixedVersion == "2.0.4");
        result.Should().Contain(v => v.Id == "GHSA-recommended" && v.FixedVersion == "3.0.7");
        result.Should().Contain(v => v.Id == "GHSA-latest" && v.FixedVersion == "4.0.8");
    }

    [Fact]
    public void ParseOutdatedPackagesFromJson_ReturnsOutdatedPackages()
    {
        // Arrange
        var json = """
            {
              "version": 1,
              "projects": [
                {
                  "path": "/path/to/MyProject.csproj",
                  "frameworks": [
                    {
                      "framework": "net8.0",
                      "topLevelPackages": [
                        { "id": "Top.Package", "resolvedVersion": "1.0.0", "latestVersion": "1.2.0" }
                      ],
                      "transitivePackages": [
                        { "id": "Transitive.Package", "resolvedVersion": "2.0.0", "latestVersion": "2.1.0" }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
        var projectFilePath = "/path/to/MyProject.csproj";
        var projectName = "MyProject.csproj";

        // Act
        var result = ProjectAnalyzer.ParseOutdatedPackagesFromJson(json, projectFilePath, projectName, includeTransitive: true);

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(p => p.PackageName == "Top.Package" && p.ResolvedVersion == "1.0.0" && p.LatestVersion == "1.2.0" && !p.IsTransitive);
        result.Should().Contain(p => p.PackageName == "Transitive.Package" && p.ResolvedVersion == "2.0.0" && p.LatestVersion == "2.1.0" && p.IsTransitive);
    }

    [Fact]
    public void ParseDeprecatedPackagesFromJson_ReturnsDeprecationDetails()
    {
        // Arrange
        var json = """
            {
              "version": 1,
              "projects": [
                {
                  "path": "/path/to/MyProject.csproj",
                  "frameworks": [
                    {
                      "framework": "net8.0",
                      "topLevelPackages": [
                        {
                          "id": "Old.Package",
                          "resolvedVersion": "3.0.0",
                          "deprecationReasons": [ "Legacy", "CriticalBugs" ],
                          "alternativePackage": { "id": "New.Package", "versionRange": "[4.0.0, )" }
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
        var projectFilePath = "/path/to/MyProject.csproj";
        var projectName = "MyProject.csproj";

        // Act
        var result = ProjectAnalyzer.ParseDeprecatedPackagesFromJson(json, projectFilePath, projectName, includeTransitive: false);

        // Assert
        result.Should().ContainSingle();
        var package = result[0];
        package.PackageName.Should().Be("Old.Package");
        package.Reasons.Should().Contain(new[] { "Legacy", "CriticalBugs" });
        package.AlternativePackage.Should().Be("New.Package");
        package.AlternativeVersionRange.Should().Be("[4.0.0, )");
    }

    [Fact]
    public void ParseJson_MissingProjectsArray_ReturnsEmptyCollections()
    {
        // Arrange
        const string json = """{ "version": 1 }""";
        var projectFilePath = "/path/to/MyProject.csproj";
        var projectName = "MyProject.csproj";

        // Act
        var references = ProjectAnalyzer.ParsePackageReferencesFromJson(json, projectFilePath, projectName, includeTransitive: true);
        var vulnerabilities = ProjectAnalyzer.ParseVulnerabilitiesFromJson(json, projectName);
        var outdated = ProjectAnalyzer.ParseOutdatedPackagesFromJson(json, projectFilePath, projectName, includeTransitive: true);
        var deprecated = ProjectAnalyzer.ParseDeprecatedPackagesFromJson(json, projectFilePath, projectName, includeTransitive: true);

        // Assert
        references.Should().BeEmpty();
        vulnerabilities.Should().BeEmpty();
        outdated.Should().BeEmpty();
        deprecated.Should().BeEmpty();
    }
}
