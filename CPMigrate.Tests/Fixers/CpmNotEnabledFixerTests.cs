using CPMigrate.Fixers;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Fixers;

/// <summary>
/// The <c>CpmNotEnabled</c> fixer sets <c>ManagePackageVersionsCentrally</c> in the props file
/// that already carries <c>PackageVersion</c> entries. It refuses when any project still declares
/// an inline <c>Version</c>: enabling central management over inline versions turns every one
/// into NU1008, so the finding is real but the fix is <c>--migrate</c>, not this.
/// </summary>
public class CpmNotEnabledFixerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly CpmNotEnabledFixer _fixer;

    public CpmNotEnabledFixerTests()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"CPMigrateCpmFixerTest_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_testDirectory);
        _fixer = new CpmNotEnabledFixer();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void CanFix_CpmNotEnabled_ReturnsTrue()
    {
        _fixer.CanFix(Issue()).Should().BeTrue();
    }

    [Fact]
    public void CanFix_OtherIssueCode_ReturnsFalse()
    {
        _fixer
            .CanFix(Issue() with { IssueCode = AnalysisIssueCode.TransitiveConflict })
            .Should()
            .BeFalse();
    }

    [Fact]
    public void Fix_PropertyAbsent_AddsIt()
    {
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.2.0" />
              </ItemGroup>
            </Project>
            """
        );

        var result = _fixer.Fix(Issue(), PackageInfo(), Request(dryRun: false));

        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);
        File.ReadAllText(propsPath).Should().Contain("ManagePackageVersionsCentrally");
    }

    [Fact]
    public void Fix_PropertyFalse_SetsTrue()
    {
        var propsPath = WriteProps(
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.2.0" />
              </ItemGroup>
            </Project>
            """
        );

        var result = _fixer.Fix(Issue(), PackageInfo(), Request(dryRun: false));

        result.Success.Should().BeTrue();
        File.ReadAllText(propsPath).Should().Contain(">true<");
    }

    [Fact]
    public void Fix_InlineVersionPresent_Refuses()
    {
        // Enabling CPM over a project that still declares Version inline turns every one into
        // NU1008 — the finding is real but the fix is --migrate, which strips them as it moves
        // the versions central. The fixer must refuse rather than break the restore.
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.2.0" />
              </ItemGroup>
            </Project>
            """
        );
        var before = File.ReadAllText(propsPath);
        var projectPath = Path.Combine(_testDirectory, "App.csproj");
        File.WriteAllText(projectPath, "<Project />");

        var packageInfo = new ProjectPackageInfo(
            new List<PackageReference>
            {
                new("Serilog", "4.2.0", projectPath, "App.csproj") { HasVersionMetadata = true },
            },
            BasePath: _testDirectory
        );

        var result = _fixer.Fix(Issue(), packageInfo, Request(dryRun: false));

        result.Success.Should().BeFalse();
        result.Description.Should().Contain("NU1008");
        result.Description.Should().Contain("--migrate");
        File.ReadAllText(propsPath).Should().Be(before);
    }

    [Fact]
    public void Fix_DryRun_ReportsWithoutWriting()
    {
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.2.0" />
              </ItemGroup>
            </Project>
            """
        );
        var before = File.ReadAllText(propsPath);

        var result = _fixer.Fix(Issue(), PackageInfo(), Request(dryRun: true));

        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);
        File.ReadAllText(propsPath).Should().Be(before);
    }

    [Fact]
    public void Fix_PropsFileMissing_Fails()
    {
        var result = _fixer.Fix(Issue(), PackageInfo(), Request(dryRun: false));

        result.Success.Should().BeFalse();
        result.Description.Should().Contain("not found");
    }

    private string WriteProps(string content)
    {
        var path = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(path, content);
        return path;
    }

    private static AnalysisIssue Issue() =>
        new(
            "Directory.Packages.props",
            "exists but does not set ManagePackageVersionsCentrally",
            Array.Empty<string>(),
            AnalysisIssueCode.CpmNotEnabled,
            AnalysisSeverity.High,
            Fixable: true
        );

    private ProjectPackageInfo PackageInfo() =>
        new(
            new List<PackageReference>
            {
                // Empty Version: under CPM-off a reference with no inline version declares none —
                // the version lives nowhere, which is exactly the state the finding describes.
                new("Serilog", "", Path.Combine(_testDirectory, "App.csproj"), "App.csproj"),
            },
            BasePath: _testDirectory
        );

    private FixRequest Request(bool dryRun) =>
        new(Path.Combine(_testDirectory, "Directory.Packages.props"), ConflictStrategy.Highest, dryRun);
}
