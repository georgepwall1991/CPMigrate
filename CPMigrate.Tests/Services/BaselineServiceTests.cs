using System.Text.Json;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// A baseline is how a repository with existing debt adopts a CI gate: record what is already there,
/// then fail only on what is new. That makes two properties load-bearing — a finding must be
/// recognised as the same one across runs even as its details drift, and a baseline must never
/// suppress a finding it does not actually contain.
/// </summary>
public class BaselineServiceTests : IDisposable
{
    private readonly string _root;
    private readonly BaselineService _service = new();

    public BaselineServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CPMigrateBaseline_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Create_RecordsEveryFindingWithReviewableContext()
    {
        var report = ReportWith(
            Issue(
                "Newtonsoft.Json",
                AnalysisIssueCode.VersionInconsistency,
                AnalysisSeverity.Moderate
            ),
            Issue(
                "System.Text.Json",
                AnalysisIssueCode.SecurityVulnerability,
                AnalysisSeverity.Critical
            )
        );

        var baseline = _service.Create(report);

        baseline.Findings.Should().HaveCount(2);
        baseline.BaselineVersion.Should().Be(BaselineFile.CurrentVersion);
        baseline.FingerprintVersion.Should().Be(AnalysisIssueIdentity.Version);
        baseline.CreatedWith.Should().NotBeNullOrWhiteSpace();

        var vulnerability = baseline.Findings.Single(f => f.IssueCode == "SecurityVulnerability");
        vulnerability.Package.Should().Be("System.Text.Json");
        vulnerability.Severity.Should().Be("Critical");
        vulnerability.Projects.Should().Equal("Api.csproj");
        vulnerability.Fingerprint.Should().HaveLength(32);
    }

    [Fact]
    public void Create_IsDeterministic_SoRegeneratingProducesNoDiff()
    {
        var report = ReportWith(
            Issue("Zed", AnalysisIssueCode.OutdatedPackage, AnalysisSeverity.Low),
            Issue("Alpha", AnalysisIssueCode.VersionInconsistency, AnalysisSeverity.Moderate)
        );

        var first = JsonSerializer.Serialize(_service.Create(report).Findings);
        var second = JsonSerializer.Serialize(_service.Create(report).Findings);

        first.Should().Be(second);
    }

    [Fact]
    public void Create_DeduplicatesFindingsThatShareAnIdentity()
    {
        var duplicate = Issue(
            "Newtonsoft.Json",
            AnalysisIssueCode.VersionInconsistency,
            AnalysisSeverity.Moderate
        );

        var baseline = _service.Create(ReportWith(duplicate, duplicate));

        baseline.Findings.Should().HaveCount(1);
    }

    [Fact]
    public void Apply_SuppressesBaselinedFindingsAndLeavesNewOnesAlone()
    {
        var accepted = Issue(
            "Newtonsoft.Json",
            AnalysisIssueCode.VersionInconsistency,
            AnalysisSeverity.Moderate
        );
        var baseline = _service.Create(ReportWith(accepted));

        var newFinding = Issue(
            "System.Text.Json",
            AnalysisIssueCode.SecurityVulnerability,
            AnalysisSeverity.Critical
        );
        var match = _service.Apply(ReportWith(accepted, newFinding), baseline);

        match.Suppressed.Should().Be(1);
        var issues = match.Report.Results.SelectMany(r => r.Issues).ToList();
        issues.Single(i => i.PackageName == "Newtonsoft.Json").Suppressed.Should().BeTrue();
        issues.Single(i => i.PackageName == "System.Text.Json").Suppressed.Should().BeFalse();
    }

    [Fact]
    public void Apply_KeepsSuppressedFindingsInTheReport()
    {
        // Accepting debt must not hide it. The finding stays in the report — and therefore in JSON
        // and SARIF — it simply stops failing the build.
        var accepted = Issue(
            "Newtonsoft.Json",
            AnalysisIssueCode.VersionInconsistency,
            AnalysisSeverity.Moderate
        );
        var baseline = _service.Create(ReportWith(accepted));

        var match = _service.Apply(ReportWith(accepted), baseline);

        match.Report.TotalIssues.Should().Be(1);
        match.Report.HasIssues.Should().BeTrue();
    }

    [Fact]
    public void Apply_ReportsBaselineEntriesThatNoLongerMatch()
    {
        // A fixed finding leaves a dead entry behind. Surfacing it is what stops a baseline growing
        // forever and quietly suppressing findings that have come back.
        var fixedFinding = Issue(
            "Gone.Package",
            AnalysisIssueCode.OutdatedPackage,
            AnalysisSeverity.Low
        );
        var stillThere = Issue(
            "Newtonsoft.Json",
            AnalysisIssueCode.VersionInconsistency,
            AnalysisSeverity.Moderate
        );
        var baseline = _service.Create(ReportWith(fixedFinding, stillThere));

        var match = _service.Apply(ReportWith(stillThere), baseline);

        match.Suppressed.Should().Be(1);
        match.Stale.Should().ContainSingle(f => f.Package == "Gone.Package");
    }

    [Fact]
    public void Apply_VersionDriftInTheDescription_StillMatches()
    {
        // The description carries the observed versions. A version inconsistency that drifts from
        // "13.0.1, 12.0.3" to "13.0.2, 12.0.3" is the same unresolved finding; a baseline that
        // stopped matching would start failing builds for debt the team already accepted.
        var original = Issue(
            "Newtonsoft.Json",
            AnalysisIssueCode.VersionInconsistency,
            AnalysisSeverity.Moderate
        ) with
        {
            Description = "13.0.1 (Api.csproj), 12.0.3 (Lib.csproj)",
        };
        var baseline = _service.Create(ReportWith(original));

        var drifted = original with { Description = "13.0.2 (Api.csproj), 12.0.3 (Lib.csproj)" };
        var match = _service.Apply(ReportWith(drifted), baseline);

        match.Suppressed.Should().Be(1);
        match.Stale.Should().BeEmpty();
    }

    [Fact]
    public void Apply_SeverityChange_StillMatches()
    {
        // An advisory being re-rated must not silently unsuppress or re-suppress: identity is the
        // finding, not its current severity. The gate still sees the *new* severity.
        var original = Issue(
            "System.Text.Json",
            AnalysisIssueCode.SecurityVulnerability,
            AnalysisSeverity.Moderate
        );
        var baseline = _service.Create(ReportWith(original));

        var escalated = original with { Severity = AnalysisSeverity.Critical };
        var match = _service.Apply(ReportWith(escalated), baseline);

        match.Suppressed.Should().Be(1);
    }

    [Fact]
    public void Apply_DifferentProjectSet_IsANewFinding()
    {
        // Spreading to another project is new information, not the accepted finding.
        var original = Issue(
            "Newtonsoft.Json",
            AnalysisIssueCode.VersionInconsistency,
            AnalysisSeverity.Moderate
        );
        var baseline = _service.Create(ReportWith(original));

        var spread = original with { AffectedProjects = new[] { "Api.csproj", "Web.csproj" } };
        var match = _service.Apply(ReportWith(spread), baseline);

        match.Suppressed.Should().Be(0);
        match.Report.Results.SelectMany(r => r.Issues).Single().Suppressed.Should().BeFalse();
    }

    [Fact]
    public void Apply_ProjectOrderingDoesNotAffectMatching()
    {
        var original = Issue(
            "Newtonsoft.Json",
            AnalysisIssueCode.VersionInconsistency,
            AnalysisSeverity.Moderate
        ) with
        {
            AffectedProjects = new[] { "Api.csproj", "Web.csproj" },
        };
        var baseline = _service.Create(ReportWith(original));

        var reordered = original with { AffectedProjects = new[] { "Web.csproj", "Api.csproj" } };

        _service.Apply(ReportWith(reordered), baseline).Suppressed.Should().Be(1);
    }

    [Fact]
    public async Task WriteAsync_ThenRead_RoundTrips()
    {
        var report = ReportWith(
            Issue(
                "Newtonsoft.Json",
                AnalysisIssueCode.VersionInconsistency,
                AnalysisSeverity.Moderate
            )
        );
        var path = Path.Combine(_root, "nested", BaselineService.DefaultFileName);

        await _service.WriteAsync(_service.Create(report), path);
        var (baseline, error) = _service.Read(path);

        error.Should().BeNull();
        baseline!.Findings.Should().HaveCount(1);
        _service.Apply(report, baseline).Suppressed.Should().Be(1);
    }

    [Fact]
    public void Read_MissingFile_ReportsAnError()
    {
        var (baseline, error) = _service.Read(Path.Combine(_root, "absent.json"));

        baseline.Should().BeNull();
        error.Should().Contain("not found");
    }

    [Fact]
    public void Read_MalformedFile_ReportsAnError()
    {
        var path = Path.Combine(_root, "broken.json");
        File.WriteAllText(path, "{ not json");

        var (baseline, error) = _service.Read(path);

        baseline.Should().BeNull();
        error.Should().Contain("parse");
    }

    [Fact]
    public void Read_UnknownFingerprintScheme_IsRejectedRatherThanSuppressingNothing()
    {
        // Silently matching nothing looks identical to "no debt accepted", which would turn a
        // suppressed backlog into a wall of build failures with no explanation.
        var path = Path.Combine(_root, "future.json");
        File.WriteAllText(
            path,
            """
            {
              "baselineVersion": "1.0.0",
              "fingerprintVersion": "v99",
              "findings": []
            }
            """
        );

        var (baseline, error) = _service.Read(path);

        baseline.Should().BeNull();
        error.Should().Contain("v99").And.Contain("--write-baseline");
    }

    private static AnalysisReport ReportWith(params AnalysisIssue[] issues)
    {
        return new AnalysisReport(1, issues.Length, new[] { new AnalyzerResult("Stub", issues) });
    }

    private static AnalysisIssue Issue(
        string package,
        AnalysisIssueCode code,
        AnalysisSeverity severity
    )
    {
        return new AnalysisIssue(
            package,
            $"{package}: {code}.",
            new[] { "Api.csproj" },
            code,
            severity
        );
    }
}
