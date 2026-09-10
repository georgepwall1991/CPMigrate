using CPMigrate.Fixers;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Fixers;

/// <summary>
/// The <c>RedundantDirectReference</c> fixer removes a direct <c>&lt;PackageReference&gt;</c>
/// that is already provided transitively — the fix the finding's own hint names. It only runs
/// under central package management: without a central pin the direct reference may be the only
/// thing holding the package at its version, and removing it would silently downgrade.
/// </summary>
public class RedundantDirectReferenceFixerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly RedundantDirectReferenceFixer _fixer;

    public RedundantDirectReferenceFixerTests()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"CPMigrateRedundantFixerTest_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_testDirectory);
        _fixer = new RedundantDirectReferenceFixer();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void CanFix_RedundantDirectReference_ReturnsTrue()
    {
        _fixer.CanFix(Issue("Serilog")).Should().BeTrue();
    }

    [Fact]
    public void CanFix_OtherIssueCode_ReturnsFalse()
    {
        _fixer
            .CanFix(Issue("Serilog") with { IssueCode = AnalysisIssueCode.TransitiveConflict })
            .Should()
            .BeFalse();
    }

    [Fact]
    public void Fix_DirectReference_RemovesIt()
    {
        var projectPath = WriteProject(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog.AspNetCore" />
                <PackageReference Include="Serilog" />
              </ItemGroup>
            </Project>
            """
        );
        WriteProps();

        var result = _fixer.Fix(Issue("Serilog"), PackageInfo(projectPath), Request(dryRun: false));

        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);
        var content = File.ReadAllText(projectPath);
        content.Should().Contain("Serilog.AspNetCore");
        content.Should().NotContain("\"Serilog\"");
    }

    [Fact]
    public void Fix_NoPropsFile_RefusesToDowngrade()
    {
        // Without a central pin the direct reference may be the only thing holding the package at
        // its version — removing it would silently downgrade to whatever the transitive graph
        // resolves. The fixer must refuse rather than apply the finding's hint.
        var projectPath = WriteProject(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.3.0" />
              </ItemGroup>
            </Project>
            """
        );
        var before = File.ReadAllText(projectPath);

        var result = _fixer.Fix(Issue("Serilog"), PackageInfo(projectPath), Request(dryRun: false));

        result.Success.Should().BeFalse();
        result.Description.Should().Contain("central props");
        File.ReadAllText(projectPath).Should().Be(before);
    }

    [Fact]
    public void Fix_DryRun_ReportsWithoutWriting()
    {
        var projectPath = WriteProject(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" />
              </ItemGroup>
            </Project>
            """
        );
        WriteProps();
        var before = File.ReadAllText(projectPath);

        var result = _fixer.Fix(Issue("Serilog"), PackageInfo(projectPath), Request(dryRun: true));

        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);
        File.ReadAllText(projectPath).Should().Be(before);
    }

    [Fact]
    public void Fix_OtherPackageUntouched()
    {
        var projectPath = WriteProject(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" />
                <PackageReference Include="Newtonsoft.Json" />
              </ItemGroup>
            </Project>
            """
        );
        WriteProps();

        var result = _fixer.Fix(Issue("Serilog"), PackageInfo(projectPath), Request(dryRun: false));

        result.Success.Should().BeTrue();
        File.ReadAllText(projectPath).Should().Contain("Newtonsoft.Json");
    }

    [Fact]
    public void Fix_ReferenceNotPresent_NoFixNeeded()
    {
        var projectPath = WriteProject(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" />
              </ItemGroup>
            </Project>
            """
        );
        WriteProps();

        var result = _fixer.Fix(Issue("Serilog"), PackageInfo(projectPath), Request(dryRun: false));

        result.Success.Should().BeTrue();
        result.Description.Should().Contain("No direct reference");
    }

    private string WriteProject(string content)
    {
        var path = Path.Combine(_testDirectory, "App.csproj");
        File.WriteAllText(path, content);
        return path;
    }

    private void WriteProps()
    {
        File.WriteAllText(
            Path.Combine(_testDirectory, "Directory.Packages.props"),
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.2.0" />
              </ItemGroup>
            </Project>
            """
        );
    }

    private static AnalysisIssue Issue(string packageName) =>
        new(
            packageName,
            "Already provided transitively by another top-level package",
            new[] { "App.csproj" },
            AnalysisIssueCode.RedundantDirectReference,
            AnalysisSeverity.Low,
            Fixable: true
        );

    private ProjectPackageInfo PackageInfo(string projectPath) =>
        new(
            new List<PackageReference>
            {
                new("Serilog", "4.2.0", projectPath, "App.csproj"),
            },
            BasePath: _testDirectory
        );

    private FixRequest Request(bool dryRun) =>
        new(Path.Combine(_testDirectory, "Directory.Packages.props"), ConflictStrategy.Highest, dryRun);
}
