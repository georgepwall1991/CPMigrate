using CPMigrate.Fixers;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Fixers;

/// <summary>
/// The <c>OrphanedPackageVersion</c> fixer removes the central <c>&lt;PackageVersion&gt;</c> entry
/// no project references — the fix the finding's own hint names. The props file comes from the
/// finding's metadata: the conventional root file when absent, the relative path it carries when
/// a nested or redirected file produced the finding.
/// </summary>
public class OrphanedPackageVersionFixerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly OrphanedPackageVersionFixer _fixer;

    public OrphanedPackageVersionFixerTests()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"CPMigrateOrphanFixerTest_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_testDirectory);
        _fixer = new OrphanedPackageVersionFixer();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void CanFix_OrphanedPackageVersion_ReturnsTrue()
    {
        var issue = Issue("Orphaned.Package");

        _fixer.CanFix(issue).Should().BeTrue();
    }

    [Fact]
    public void CanFix_OtherIssueCode_ReturnsFalse()
    {
        var issue = new AnalysisIssue(
            "Newtonsoft.Json",
            "Some other issue",
            new[] { "Project1.csproj" }
        );

        _fixer.CanFix(issue).Should().BeFalse();
    }

    [Fact]
    public void Fix_OrphanedEntry_RemovesIt()
    {
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Newtonsoft.Json" Version="13.0.1" />
                <PackageVersion Include="Orphaned.Package" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var result = _fixer.Fix(Issue("Orphaned.Package"), PackageInfo(), Request(dryRun: false));

        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);
        var content = File.ReadAllText(propsPath);
        content.Should().Contain("Newtonsoft.Json");
        content.Should().NotContain("Orphaned.Package");
    }

    [Fact]
    public void Fix_DryRun_ReportsWithoutWriting()
    {
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Orphaned.Package" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );
        var before = File.ReadAllText(propsPath);

        var result = _fixer.Fix(Issue("Orphaned.Package"), PackageInfo(), Request(dryRun: true));

        result.Success.Should().BeTrue();
        result.Changes.Should().HaveCount(1);
        File.ReadAllText(propsPath).Should().Be(before);
    }

    [Fact]
    public void Fix_UpdateAttribute_RemovesIt()
    {
        // PackageVersion entries declare the pin two ways — Include for a new pin, Update for
        // amending an existing one — and either can be the orphaned entry.
        var propsPath = WriteProps(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Update="Orphaned.Package" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var result = _fixer.Fix(Issue("Orphaned.Package"), PackageInfo(), Request(dryRun: false));

        result.Success.Should().BeTrue();
        File.ReadAllText(propsPath).Should().NotContain("Orphaned.Package");
    }

    [Fact]
    public void Fix_AbsentEntry_ReportsNoFixNeeded()
    {
        WriteProps(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Newtonsoft.Json" Version="13.0.1" />
              </ItemGroup>
            </Project>
            """
        );

        var result = _fixer.Fix(Issue("Orphaned.Package"), PackageInfo(), Request(dryRun: false));

        result.Success.Should().BeTrue();
        result.Changes.Should().BeEmpty();
        result.Description.Should().Contain("No PackageVersion entry");
    }

    [Fact]
    public void Fix_MissingPropsFile_ReportsFailure()
    {
        var result = _fixer.Fix(Issue("Orphaned.Package"), PackageInfo(), Request(dryRun: false));

        result.Success.Should().BeFalse();
        result.Description.Should().Contain("Props file not found");
    }

    [Fact]
    public void Fix_NestedPropsFile_UsesTheMetadataPath()
    {
        // A finding from a nested or redirected props file names it through metadata; the fixer
        // must edit that file, not the conventional root.
        var nestedDir = Path.Combine(_testDirectory, "nested");
        Directory.CreateDirectory(nestedDir);
        var nestedProps = Path.Combine(nestedDir, "Directory.Packages.props");
        File.WriteAllText(
            nestedProps,
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Orphaned.Package" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );

        var issue = new AnalysisIssue(
            "Orphaned.Package",
            "Pinned but referenced by no project",
            Array.Empty<string>(),
            AnalysisIssueCode.OrphanedPackageVersion,
            AnalysisSeverity.Low,
            Fixable: true,
            Metadata: new Dictionary<string, string> { ["propsFile"] = "nested/Directory.Packages.props" }
        );

        var result = _fixer.Fix(issue, PackageInfo(), Request(dryRun: false));

        result.Success.Should().BeTrue();
        File.ReadAllText(nestedProps).Should().NotContain("Orphaned.Package");
    }

    private string WriteProps(string content)
    {
        var path = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(path, content);
        return path;
    }

    private static AnalysisIssue Issue(string packageName) =>
        new(
            packageName,
            "Pinned but referenced by no project",
            Array.Empty<string>(),
            AnalysisIssueCode.OrphanedPackageVersion,
            AnalysisSeverity.Low,
            Fixable: true
        );

    private ProjectPackageInfo PackageInfo() =>
        new(new List<PackageReference>(), BasePath: _testDirectory);

    private static FixRequest Request(bool dryRun) =>
        new("Directory.Packages.props", ConflictStrategy.Highest, dryRun);
}
