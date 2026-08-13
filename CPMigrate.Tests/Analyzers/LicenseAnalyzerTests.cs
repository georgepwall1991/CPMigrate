using CPMigrate.Analyzers;
using CPMigrate.Licensing;
using CPMigrate.Models;
using FluentAssertions;

namespace CPMigrate.Tests.Analyzers;

/// <summary>
/// LicenseRisk reports what the scan resolved, not a hardcoded package-name table. Without
/// <c>--licenses</c> the scan data is absent, and a missing table entry must not look like a pass.
/// </summary>
public class LicenseAnalyzerTests
{
    private readonly LicenseAnalyzer _analyzer = new();

    [Fact]
    public void Analyze_NullLicenses_ReportsNothingEvenForKnownCopyleftPackages()
    {
        var packageInfo = new ProjectPackageInfo(
            [new PackageReference("MySql.Data", "8.0.33", "/repo/Api.csproj", "Api.csproj")]
        );

        _analyzer.Analyze(packageInfo).Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_EmptyLicenses_ReportsNothing()
    {
        var packageInfo = new ProjectPackageInfo([], Licenses: []);

        _analyzer.Analyze(packageInfo).Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_PermissiveLicense_IsNotAFinding()
    {
        var packageInfo = WithLicense(
            "Newtonsoft.Json",
            "MIT",
            LicenseClassification.Permissive,
            "expression"
        );

        _analyzer.Analyze(packageInfo).Issues.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_NameIdentifiesTheAnalyzer()
    {
        _analyzer.Name.Should().Be("Package Licenses");
    }

    [Fact]
    public void Analyze_StrongCopyleft_IsHigh()
    {
        var packageInfo = WithLicense(
            "MySql.Data",
            "GPL-2.0-only",
            LicenseClassification.StrongCopyleft,
            "expression"
        );

        var issue = _analyzer.Analyze(packageInfo).Issues.Should().ContainSingle().Subject;
        issue.IssueCode.Should().Be(AnalysisIssueCode.LicenseRisk);
        issue.Severity.Should().Be(AnalysisSeverity.High);
        issue.Fixable.Should().BeFalse();
        issue.PackageName.Should().Be("MySql.Data");
        issue.Description.Should().Contain("GPL-2.0-only");
        issue.Description.Should().Contain("copyleft");
        issue.Metadata.Should().ContainKey("license").WhoseValue.Should().Be("GPL-2.0-only");
        issue.Metadata.Should().ContainKey("risk").WhoseValue.Should().Be("copyleft");
        issue.Metadata.Should().ContainKey("licenseType").WhoseValue.Should().Be("expression");
        issue.AffectedProjects.Should().ContainSingle().Which.Should().Be("Api.csproj");
    }

    [Fact]
    public void Analyze_WeakCopyleft_IsModerate()
    {
        var issue = _analyzer
            .Analyze(WithLicense("LibGit2Sharp", "LGPL-2.1-only", LicenseClassification.WeakCopyleft, "expression"))
            .Issues.Should()
            .ContainSingle()
            .Subject;

        issue.Severity.Should().Be(AnalysisSeverity.Moderate);
        issue.Metadata!["risk"].Should().Be("copyleft");
        issue.Description.Should().Contain("LGPL-2.1-only");
        issue.Description.Should().Contain("weak copyleft");
    }

    [Fact]
    public void Analyze_Proprietary_IsModerate()
    {
        var issue = _analyzer
            .Analyze(WithLicense("Oracle.ManagedDataAccess", "Oracle", LicenseClassification.Proprietary, "expression"))
            .Issues.Should()
            .ContainSingle()
            .Subject;

        issue.Severity.Should().Be(AnalysisSeverity.Moderate);
        issue.Description.Should().Contain("proprietary");
        issue.Metadata!["risk"].Should().Be("proprietary");
    }

    [Fact]
    public void Analyze_Unknown_IsLow()
    {
        var issue = _analyzer
            .Analyze(WithLicense("Mystery.Pkg", "LICENSE.txt", LicenseClassification.Unknown, "file"))
            .Issues.Should()
            .ContainSingle()
            .Subject;

        issue.Severity.Should().Be(AnalysisSeverity.Low);
        issue.Metadata!["risk"].Should().Be("unknown");
        issue.Metadata["licenseType"].Should().Be("file");
        issue.Description.Should().Contain("LICENSE.txt");
        issue.Description.Should().Contain("unverified");
    }

    [Fact]
    public void Analyze_GroupsTheSamePackageAcrossProjects()
    {
        var packageInfo = new ProjectPackageInfo(
            [],
            Licenses:
            [
                Info("MySql.Data", "GPL-2.0-only", LicenseClassification.StrongCopyleft, "expression", "/repo/Api.csproj", "Api.csproj"),
                Info("MySql.Data", "GPL-2.0-only", LicenseClassification.StrongCopyleft, "expression", "/repo/Worker.csproj", "Worker.csproj"),
            ]
        );

        var issue = _analyzer.Analyze(packageInfo).Issues.Should().ContainSingle().Subject;
        issue.AffectedProjects.Should().BeEquivalentTo("Api.csproj", "Worker.csproj");
    }

    [Fact]
    public void Analyze_MixedClassificationsForOnePackage_ReportsTheWorse()
    {
        var packageInfo = new ProjectPackageInfo(
            [],
            Licenses:
            [
                Info("Contoso.Lib", "MIT", LicenseClassification.Permissive, "expression", "/repo/A/A.csproj", "A.csproj"),
                Info("Contoso.Lib", "GPL-3.0-only", LicenseClassification.StrongCopyleft, "expression", "/repo/B/B.csproj", "B.csproj"),
            ]
        );

        var issue = _analyzer.Analyze(packageInfo).Issues.Should().ContainSingle().Subject;
        issue.Severity.Should().Be(AnalysisSeverity.High);
        issue.Metadata!["license"].Should().Be("GPL-3.0-only");
    }

    private static ProjectPackageInfo WithLicense(
        string package,
        string license,
        LicenseClassification classification,
        string licenseType
    )
    {
        return new ProjectPackageInfo(
            [],
            Licenses: [Info(package, license, classification, licenseType, "/repo/Api.csproj", "Api.csproj")]
        );
    }

    private static LicenseInfo Info(
        string package,
        string license,
        LicenseClassification classification,
        string licenseType,
        string projectPath,
        string projectName
    )
    {
        return new LicenseInfo(
            package,
            "1.0.0",
            projectPath,
            projectName,
            license,
            classification,
            licenseType
        );
    }
}
