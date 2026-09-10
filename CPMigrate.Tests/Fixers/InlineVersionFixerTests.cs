using CPMigrate.Fixers;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Fixers;

/// <summary>
/// The <c>InlineVersionUnderCpm</c> fixer removes the inline <c>Version</c> from a
/// <c>&lt;PackageReference&gt;</c> so the central pin applies — the fix the finding's own hint
/// names. A <c>VersionOverride</c> is deliberate and stays.
/// </summary>
public class InlineVersionFixerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly InlineVersionFixer _fixer;

    public InlineVersionFixerTests()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"CPMigrateInlineFixerTest_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_testDirectory);
        _fixer = new InlineVersionFixer();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void CanFix_InlineVersionUnderCpm_ReturnsTrue()
    {
        _fixer.CanFix(Issue("Newtonsoft.Json")).Should().BeTrue();
    }

    [Fact]
    public void CanFix_OtherIssueCode_ReturnsFalse()
    {
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Some other issue",
            new[] { "App.csproj" }
        );

        _fixer.CanFix(issue).Should().BeFalse();
    }

    [Fact]
    public void Fix_InlineVersionAttribute_RemovesIt()
    {
        var projectPath = WriteProject(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="12.0.3" />
              </ItemGroup>
            </Project>
            """
        );

        var result = _fixer.Fix(Issue("Newtonsoft.Json"), PackageInfo(projectPath), Request(dryRun: false));

        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);
        var content = File.ReadAllText(projectPath);
        content.Should().Contain("Newtonsoft.Json");
        content.Should().NotContain("Version=");
    }

    [Fact]
    public void Fix_InlineVersionChildElement_RemovesIt()
    {
        // The child-element form is legal MSBuild and carries the same override.
        var projectPath = WriteProject(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json"><Version>12.0.3</Version></PackageReference>
              </ItemGroup>
            </Project>
            """
        );

        var result = _fixer.Fix(Issue("Newtonsoft.Json"), PackageInfo(projectPath), Request(dryRun: false));

        result.Success.Should().BeTrue();
        var content = File.ReadAllText(projectPath);
        content.Should().Contain("Newtonsoft.Json");
        content.Should().NotContain("<Version>");
    }

    [Fact]
    public void Fix_VersionOverride_Stays()
    {
        // A VersionOverride is NuGet's supported per-project escape hatch — the analyzer reports it
        // at a lower severity because it is deliberate, and deleting it would silently change which
        // version the project restores.
        var projectPath = WriteProject(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" VersionOverride="12.0.3" />
              </ItemGroup>
            </Project>
            """
        );
        var before = File.ReadAllText(projectPath);

        var result = _fixer.Fix(Issue("Newtonsoft.Json"), PackageInfo(projectPath), Request(dryRun: false));

        result.Success.Should().BeTrue();
        result.Changes.Should().BeEmpty();
        File.ReadAllText(projectPath).Should().Be(before);
    }

    [Fact]
    public void Fix_DryRun_ReportsWithoutWriting()
    {
        var projectPath = WriteProject(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="12.0.3" />
              </ItemGroup>
            </Project>
            """
        );
        var before = File.ReadAllText(projectPath);

        var result = _fixer.Fix(Issue("Newtonsoft.Json"), PackageInfo(projectPath), Request(dryRun: true));

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
                <PackageReference Include="Newtonsoft.Json" Version="12.0.3" />
                <PackageReference Include="Serilog" Version="4.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var result = _fixer.Fix(Issue("Newtonsoft.Json"), PackageInfo(projectPath), Request(dryRun: false));

        result.Success.Should().BeTrue();
        var content = File.ReadAllText(projectPath);
        content.Should().NotContain("Version=\"12.0.3\"");
        content.Should().Contain("Version=\"4.0.0\"");
    }

    private string WriteProject(string content)
    {
        var path = Path.Combine(_testDirectory, "App.csproj");
        File.WriteAllText(path, content);
        return path;
    }

    private static AnalysisIssue Issue(string packageName) =>
        new(
            packageName,
            "Declares Version inline, overriding the central pin",
            new[] { "App.csproj" },
            AnalysisIssueCode.InlineVersionUnderCpm,
            AnalysisSeverity.Moderate,
            Fixable: true
        );

    private ProjectPackageInfo PackageInfo(string projectPath) =>
        new(
            new List<PackageReference>
            {
                new("Newtonsoft.Json", "12.0.3", projectPath, "App.csproj"),
            },
            BasePath: _testDirectory
        );

    private static FixRequest Request(bool dryRun) =>
        new("Directory.Packages.props", ConflictStrategy.Highest, dryRun);
}
