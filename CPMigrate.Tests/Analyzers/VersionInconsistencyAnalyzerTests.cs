using CPMigrate.Analyzers;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Analyzers;

public class VersionInconsistencyAnalyzerTests
{
    private readonly VersionInconsistencyAnalyzer _analyzer;

    public VersionInconsistencyAnalyzerTests()
    {
        _analyzer = new VersionInconsistencyAnalyzer();
    }

    [Fact]
    public void Analyze_ConsistentVersions_ReturnsEmptyIssues()
    {
        // Arrange
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new PackageReference("Pkg", "1.0.0", "P1.csproj", "P1.csproj"),
                new PackageReference("Pkg", "1.0.0", "P2.csproj", "P2.csproj")
            },
            new List<VulnerabilityInfo>()
        );

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_InconsistentVersions_ReturnsIssue()
    {
        // Arrange
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new PackageReference("Pkg", "1.0.0", "P1.csproj", "P1.csproj"),
                new PackageReference("Pkg", "2.0.0", "P2.csproj", "P2.csproj")
            },
            new List<VulnerabilityInfo>()
        );

        // Act
        var result = _analyzer.Analyze(packageInfo);

        // Assert
        result.Issues.Should().HaveCount(1);
        result.Issues[0].PackageName.Should().Be("Pkg");
        result.Issues[0].Description.Should().Contain("1.0.0 (P1.csproj)");
        result.Issues[0].Description.Should().Contain("2.0.0 (P2.csproj)");
        result.Issues[0].AffectedProjects.Should().Contain(new[] { "P1.csproj", "P2.csproj" });
    }

    [Fact]
    public void Analyze_ConditionallyDeclaredUpdateReference_DoesNotReportIssue()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"CPMigrateVersionAnalyzer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var conditionalPath = Path.Combine(directory, "Conditional.csproj");

        try
        {
            File.WriteAllText(
                conditionalPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Pkg" Version="1.0.0" />
                  </ItemGroup>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                    <PackageReference Update="Pkg" Version="99.0.0" />
                  </ItemGroup>
                </Project>
                """
            );

            var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
            var (conditionalDeclaration, scanSucceeded) = scanner.ScanDeclaredPackages(conditionalPath);
            scanSucceeded.Should().BeTrue();
            conditionalDeclaration.Should().HaveCount(2);
            conditionalDeclaration.Should().Contain(reference =>
                reference.Version == "99.0.0" && reference.IsConditional
            );

            var otherDeclaration = new PackageReference("Pkg", "2.0.0", "P2.csproj", "P2.csproj");
            var packageInfo = new ProjectPackageInfo(
                new List<PackageReference>
                {
                    new("Pkg", "99.0.0", conditionalPath, "Conditional.csproj"),
                    otherDeclaration,
                },
                DeclaredReferences: conditionalDeclaration.Append(otherDeclaration).ToList()
            );

            var result = _analyzer.Analyze(packageInfo);

            result.Issues.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Analyze_ConditionallyDeclaredUpdateVersionOverride_DoesNotReportIssue()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"CPMigrateVersionOverrideAnalyzer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var conditionalPath = Path.Combine(directory, "Conditional.csproj");

        try
        {
            File.WriteAllText(
                conditionalPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Pkg" />
                  </ItemGroup>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                    <PackageReference Update="Pkg" VersionOverride="99.0.0" />
                  </ItemGroup>
                </Project>
                """
            );

            var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
            var (conditionalDeclaration, scanSucceeded) = scanner.ScanDeclaredPackages(conditionalPath);
            scanSucceeded.Should().BeTrue();
            conditionalDeclaration.Should().HaveCount(2);
            conditionalDeclaration.Should().Contain(reference =>
                reference.VersionOverride == "99.0.0" && reference.IsConditional
            );

            var otherDeclaration = new PackageReference("Pkg", "2.0.0", "P2.csproj", "P2.csproj");
            var packageInfo = new ProjectPackageInfo(
                new List<PackageReference>
                {
                    new("Pkg", "99.0.0", conditionalPath, "Conditional.csproj"),
                    otherDeclaration,
                },
                DeclaredReferences: conditionalDeclaration.Append(otherDeclaration).ToList()
            );

            var result = _analyzer.Analyze(packageInfo);

            result.Issues.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Analyze_ConditionalNonLiteralPinProtectsAnUnconditionalMatch()
    {
        const string conditionalProjectPath = "Conditional.csproj";
        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Pkg", "2.0.0", conditionalProjectPath, conditionalProjectPath),
                new("Pkg", "3.0.0", "Other.csproj", "Other.csproj"),
            },
            DeclaredReferences: new List<PackageReference>
            {
                new("Pkg", string.Empty, conditionalProjectPath, conditionalProjectPath, VersionOverride: "2.0.0")
                {
                    HasVersionOverrideMetadata = true,
                },
                new(
                    "Pkg",
                    string.Empty,
                    conditionalProjectPath,
                    conditionalProjectPath,
                    IsConditional: true,
                    VersionOverride: "$(SpecialVersion)"
                )
                {
                    HasVersionOverrideMetadata = true,
                    ConditionalScope = "'$(TargetFramework)' == 'net8.0'",
                },
                new("Pkg", "3.0.0", "Other.csproj", "Other.csproj"),
            }
        );

        var result = _analyzer.Analyze(packageInfo);

        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_ConditionallyDeclaredOverrideClearPreservesInheritedVersion()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"CPMigrateOverrideClearAnalyzer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var conditionalPath = Path.Combine(directory, "Conditional.csproj");

        try
        {
            File.WriteAllText(
                conditionalPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Pkg" Version="3.0.0" VersionOverride="2.0.0" />
                  </ItemGroup>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                    <PackageReference Update="Pkg" VersionOverride="" />
                  </ItemGroup>
                </Project>
                """
            );

            var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
            var (conditionalDeclaration, scanSucceeded) = scanner.ScanDeclaredPackages(conditionalPath);
            scanSucceeded.Should().BeTrue();
            conditionalDeclaration.Should().Contain(reference =>
                reference.IsConditional
                && reference.Version == "3.0.0"
                && reference.VersionOverride == null
            );

            var otherDeclaration = new PackageReference("Pkg", "2.0.0", "P2.csproj", "P2.csproj");
            var packageInfo = new ProjectPackageInfo(
                new List<PackageReference>
                {
                    new("Pkg", "3.0.0", conditionalPath, "Conditional.csproj"),
                    otherDeclaration,
                },
                DeclaredReferences: conditionalDeclaration.Append(otherDeclaration).ToList()
            );

            _analyzer.Analyze(packageInfo).Issues.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Analyze_ConditionallyDeclaredMetadataOnlyUpdate_DoesNotHideVersionConflict()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"CPMigrateMetadataOnlyAnalyzer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var conditionalPath = Path.Combine(directory, "Conditional.csproj");

        try
        {
            File.WriteAllText(
                conditionalPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                    <PackageReference Update="Pkg" PrivateAssets="all" />
                  </ItemGroup>
                </Project>
                """
            );

            var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
            var (conditionalDeclaration, scanSucceeded) = scanner.ScanDeclaredPackages(conditionalPath);
            scanSucceeded.Should().BeTrue();
            conditionalDeclaration.Should().ContainSingle();
            conditionalDeclaration[0].Version.Should().BeEmpty();

            var otherDeclaration = new PackageReference("Pkg", "2.0.0", "P2.csproj", "P2.csproj");
            var packageInfo = new ProjectPackageInfo(
                new List<PackageReference>
                {
                    new("Pkg", "1.0.0", conditionalPath, "Conditional.csproj"),
                    otherDeclaration,
                },
                DeclaredReferences: conditionalDeclaration.Append(otherDeclaration).ToList()
            );

            var result = _analyzer.Analyze(packageInfo);

            result.Issues.Should().ContainSingle();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Analyze_ConditionallyDeclaredFloatingVersionOverride_DoesNotReportIssue()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"CPMigrateFloatingVersionAnalyzer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var conditionalPath = Path.Combine(directory, "Conditional.csproj");

        try
        {
            File.WriteAllText(
                conditionalPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Pkg" />
                  </ItemGroup>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                    <PackageReference Update="Pkg" VersionOverride="99.*" />
                  </ItemGroup>
                </Project>
                """
            );

            var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
            var (conditionalDeclaration, scanSucceeded) = scanner.ScanDeclaredPackages(conditionalPath);
            scanSucceeded.Should().BeTrue();
            conditionalDeclaration.Should().HaveCount(2);
            conditionalDeclaration.Should().Contain(reference =>
                reference.VersionOverride == "99.*" && reference.IsConditional
            );

            var otherDeclaration = new PackageReference("Pkg", "2.0.0", "P2.csproj", "P2.csproj");
            var packageInfo = new ProjectPackageInfo(
                new List<PackageReference>
                {
                    new("Pkg", "99.5.0", conditionalPath, "Conditional.csproj"),
                    otherDeclaration,
                },
                DeclaredReferences: conditionalDeclaration.Append(otherDeclaration).ToList()
            );

            var result = _analyzer.Analyze(packageInfo);

            result.Issues.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Analyze_ConditionallyVersionlessDeclaration_DoesNotHideUnconditionalVersion()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"CPMigrateVersionlessConditionalAnalyzer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var conditionalPath = Path.Combine(directory, "Conditional.csproj");

        try
        {
            File.WriteAllText(
                conditionalPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Pkg" Version="1.0.0" />
                  </ItemGroup>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net472'">
                    <PackageReference Include="Pkg" />
                  </ItemGroup>
                </Project>
                """
            );

            var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
            var (conditionalDeclaration, scanSucceeded) = scanner.ScanDeclaredPackages(conditionalPath);
            scanSucceeded.Should().BeTrue();
            conditionalDeclaration.Should().HaveCount(2);

            var otherDeclaration = new PackageReference("Pkg", "2.0.0", "P2.csproj", "P2.csproj");
            var packageInfo = new ProjectPackageInfo(
                new List<PackageReference>
                {
                    new("Pkg", "1.0.0", conditionalPath, "Conditional.csproj"),
                    otherDeclaration,
                },
                DeclaredReferences: conditionalDeclaration.Append(otherDeclaration).ToList()
            );

            var result = _analyzer.Analyze(packageInfo);

            result.Issues.Should().ContainSingle();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Analyze_UpdateBeforeConditionalInclude_RemainsVisibleAsUnconditional()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"CPMigrateConditionalIncludeAnalyzer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, "Project.csproj");

        try
        {
            File.WriteAllText(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Update="Pkg" Version="1.0.0" />
                  </ItemGroup>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                    <PackageReference Include="Pkg" Version="9.0.0" />
                  </ItemGroup>
                </Project>
                """
            );

            var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
            var (declarations, scanSucceeded) = scanner.ScanDeclaredPackages(projectPath);
            scanSucceeded.Should().BeTrue();
            declarations.Should().HaveCount(2);
            declarations.Should().Contain(reference => reference.Version == "1.0.0" && !reference.IsConditional);

            var otherDeclaration = new PackageReference("Pkg", "2.0.0", "P2.csproj", "P2.csproj");
            var packageInfo = new ProjectPackageInfo(
                new List<PackageReference>
                {
                    new("Pkg", "1.0.0", projectPath, "Project.csproj"),
                    otherDeclaration,
                },
                DeclaredReferences: declarations.Append(otherDeclaration).ToList()
            );

            var result = _analyzer.Analyze(packageInfo);

            result.Issues.Should().ContainSingle();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Analyze_UnconditionalUpdateAfterConditionalUpdate_RemainsVisible()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"CPMigrateConditionalUpdateAnalyzer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, "Project.csproj");

        try
        {
            File.WriteAllText(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                    <PackageReference Update="Pkg" Version="1.0.0" />
                  </ItemGroup>
                  <ItemGroup>
                    <PackageReference Update="Pkg" Version="4.0.0" />
                  </ItemGroup>
                </Project>
                """
            );

            var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
            var (declarations, scanSucceeded) = scanner.ScanDeclaredPackages(projectPath);
            scanSucceeded.Should().BeTrue();
            declarations.Should().ContainSingle(reference =>
                reference.Version == "4.0.0" && !reference.IsConditional
            );

            var otherDeclaration = new PackageReference("Pkg", "2.0.0", "P2.csproj", "P2.csproj");
            var packageInfo = new ProjectPackageInfo(
                new List<PackageReference>
                {
                    new("Pkg", "4.0.0", projectPath, "Project.csproj"),
                    otherDeclaration,
                },
                DeclaredReferences: declarations.Append(otherDeclaration).ToList()
            );

            var result = _analyzer.Analyze(packageInfo);

            result.Issues.Should().ContainSingle();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Analyze_UnconditionalUpdateAfterConditionalVersionOverride_RemainsVisible()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"CPMigrateConditionalOverrideAnalyzer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, "Project.csproj");

        try
        {
            File.WriteAllText(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                    <PackageReference Update="Pkg" VersionOverride="9.0.0" />
                  </ItemGroup>
                  <ItemGroup>
                    <PackageReference Update="Pkg" Version="3.0.0" />
                  </ItemGroup>
                </Project>
                """
            );

            var scanner = new ProjectFileScanner(SilentConsoleService.Instance);
            var (declarations, scanSucceeded) = scanner.ScanDeclaredPackages(projectPath);
            scanSucceeded.Should().BeTrue();
            declarations.Should().HaveCount(2);

            var otherDeclaration = new PackageReference("Pkg", "2.0.0", "P2.csproj", "P2.csproj");
            var packageInfo = new ProjectPackageInfo(
                new List<PackageReference>
                {
                    new("Pkg", "3.0.0", projectPath, "Project.csproj"),
                    otherDeclaration,
                },
                DeclaredReferences: declarations.Append(otherDeclaration).ToList()
            );

            var result = _analyzer.Analyze(packageInfo);

            result.Issues.Should().ContainSingle();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
