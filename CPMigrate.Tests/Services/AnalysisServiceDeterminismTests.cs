using System.Text.Json;
using System.Text.Json.Serialization;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Analyzer findings have to be order-stable across repeated runs on identical input: a CI job
/// diffing two JSON reports must see only real changes, not hash-order noise. (The payload's run
/// timestamp is the one deliberately-varying field; these tests serialize the report itself.)
///
/// The scan merges per-project results concurrently, so the reference list an analyzer sees can
/// interleave batches differently run to run even when every batch is identical. These tests pin
/// the guarantee that <see cref="AnalysisService.Analyze"/> output does not depend on that
/// interleaving.
/// </summary>
public class AnalysisServiceDeterminismTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void Analyze_IsByteStable_WhenBatchesArriveInDifferentOrders()
    {
        var first = new AnalysisService().Analyze(BuildPackageInfo(ForwardBatches()));
        var second = new AnalysisService().Analyze(BuildPackageInfo(InterleavedBatches()));

        var firstJson = JsonSerializer.Serialize(first, SerializerOptions);
        var secondJson = JsonSerializer.Serialize(second, SerializerOptions);

        secondJson.Should().Be(firstJson);
    }

    [Fact]
    public void CasingVariants_AcrossBatchOrders_PickTheSameRepresentative()
    {
        // DuplicatePackageCasing reports whichever casing appeared first as the package name.
        // If declaration batches from different projects arrive in a different order, the
        // "first" casing must not change — the batch grouping guarantees it does not.
        PackageReference Declared(string casing, string project) =>
            new(casing, Version: "", project, project, IsTransitive: false);

        var batchA = new List<PackageReference[]> { new[] { Declared("newtonsoft.json", "src/A/A.csproj") } };
        var batchB = new List<PackageReference[]> { new[] { Declared("Newtonsoft.Json", "src/B/B.csproj") } };
        var declaredForward = new List<PackageReference> { batchA[0][0], batchB[0][0] };
        var declaredReversed = new List<PackageReference> { batchB[0][0], batchA[0][0] };

        var forward = new AnalysisService().Analyze(
            BuildPackageInfo(batchA, declared: declaredForward)
        );
        var reversed = new AnalysisService().Analyze(
            BuildPackageInfo(batchB, declared: declaredReversed)
        );

        var forwardIssue = forward
            .Results.SelectMany(r => r.Issues)
            .Single(issue => issue.IssueCode == AnalysisIssueCode.DuplicatePackageCasing);
        var reversedIssue = reversed
            .Results.SelectMany(r => r.Issues)
            .Single(issue => issue.IssueCode == AnalysisIssueCode.DuplicatePackageCasing);

        forwardIssue.PackageName.Should().Be(reversedIssue.PackageName);
        forwardIssue.Description.Should().Be(reversedIssue.Description);
    }

    [Fact]
    public void Analyze_EmitsIssuesInCanonicalOrder_RegardlessOfReferenceOrder()
    {
        var reversed = new AnalysisService()
            .Analyze(BuildPackageInfo(ReversedBatches()))
            .Results
            .Select(result => result.Issues.Select(issue => (issue.IssueCode, issue.PackageName)))
            .ToList();
        var forward = new AnalysisService()
            .Analyze(BuildPackageInfo(ForwardBatches()))
            .Results
            .Select(result => result.Issues.Select(issue => (issue.IssueCode, issue.PackageName)))
            .ToList();

        forward.Should().BeEquivalentTo(reversed, options => options.WithStrictOrdering());
    }

    /// <summary>
    /// The same four per-project batches, replayed in discovery order.
    /// </summary>
    private static List<PackageReference[]> ForwardBatches()
    {
        return [BatchAlpha(), BatchBeta(), BatchGamma(), BatchDelta()];
    }

    /// <summary>
    /// The same batches as <see cref="ForwardBatches"/>, interleaved the way concurrent workers
    /// completing out of order would merge them.
    /// </summary>
    private static List<PackageReference[]> InterleavedBatches()
    {
        return [BatchGamma(), BatchAlpha(), BatchDelta(), BatchBeta()];
    }

    private static List<PackageReference[]> ReversedBatches()
    {
        return [BatchDelta(), BatchGamma(), BatchBeta(), BatchAlpha()];
    }

    private static PackageReference[] BatchAlpha()
    {
        return
        [
            new("Newtonsoft.Json", "13.0.1", "/repo/src/Api/Api.csproj", "Api.csproj"),
            new("Serilog", "4.1.0", "/repo/src/Api/Api.csproj", "Api.csproj"),
            new("newtonsoft.json", "13.0.1", "/repo/src/Api/Api.csproj", "Api.csproj"),
        ];
    }

    private static PackageReference[] BatchBeta()
    {
        return
        [
            new("Serilog", "3.1.1", "/repo/src/Worker/Worker.csproj", "Worker.csproj"),
            new("Newtonsoft.Json", "12.0.3", "/repo/src/Worker/Worker.csproj", "Worker.csproj"),
            new("Polly", "8.4.0", "/repo/src/Worker/Worker.csproj", "Worker.csproj", IsTransitive: true),
        ];
    }

    private static PackageReference[] BatchGamma()
    {
        return
        [
            new("Polly", "7.2.3", "/repo/tests/Tests/Tests.csproj", "Tests.csproj"),
            new("Serilog", "4.1.0", "/repo/tests/Tests/Tests.csproj", "Tests.csproj"),
            new("Moq", "4.20.70", "/repo/tests/Tests/Tests.csproj", "Tests.csproj"),
        ];
    }

    private static PackageReference[] BatchDelta()
    {
        return
        [
            new("Moq", "4.18.4", "/repo/tests/Integration/Integration.csproj", "Integration.csproj"),
            new("polly", "7.2.3", "/repo/tests/Integration/Integration.csproj", "Integration.csproj"),
        ];
    }

    private static ProjectPackageInfo BuildPackageInfo(
        List<PackageReference[]> batches,
        IReadOnlyList<PackageReference>? declared = null
    )
    {
        return new ProjectPackageInfo(
            batches.SelectMany(batch => batch).ToList(),
            Vulnerabilities: ReverseIf(batches, BatchVulnerabilities()),
            OutdatedPackages: ReverseIf(batches, BatchOutdated()),
            DeprecatedPackages: ReverseIf(batches, BatchDeprecated()),
            BasePath: "/repo",
            ScannedProjects: ReverseIf(
                batches,
                new[]
                {
                    "/repo/src/Api/Api.csproj",
                    "/repo/src/Worker/Worker.csproj",
                    "/repo/tests/Tests/Tests.csproj",
                    "/repo/tests/Integration/Integration.csproj",
                }
            ),
            DeclaredReferences: declared ?? ReverseIf(batches, BatchDeclarations())
        );
    }

    private static List<T> ReverseIf<T>(List<PackageReference[]> batches, T[] items)
    {
        // A different batch arrival order for the deep-scan lists too: whatever produced the
        // reference interleaving produces this one.
        return (batches[0][0].PackageName == "Newtonsoft.Json" ? items : items.Reverse().ToArray())
            .ToList();
    }

    private static VulnerabilityInfo[] BatchVulnerabilities()
    {
        return
        [
            new(
                "Newtonsoft.Json",
                "High",
                "GHSA-5crp-9r3c-p9vr",
                "12.0.3",
                "13.0.1",
                "Worker.csproj",
                "/repo/src/Worker/Worker.csproj"
            ),
            new(
                "Polly",
                "Moderate",
                "GHSA-example-0001",
                "7.2.3",
                "8.0.0",
                "Tests.csproj",
                "/repo/tests/Tests/Tests.csproj"
            ),
        ];
    }

    private static OutdatedPackageInfo[] BatchOutdated()
    {
        return
        [
            new(
                "Serilog",
                "3.1.1",
                "4.1.0",
                "/repo/src/Worker/Worker.csproj",
                "Worker.csproj"
            ),
            new(
                "Moq",
                "4.18.4",
                "4.20.70",
                "/repo/tests/Integration/Integration.csproj",
                "Integration.csproj"
            ),
        ];
    }

    private static DeprecatedPackageInfo[] BatchDeprecated()
    {
        return
        [
            new(
                "Polly",
                "7.2.3",
                "/repo/tests/Tests/Tests.csproj",
                "Tests.csproj",
                ["Legacy"],
                "Polly.Core",
                ">= 8.0.0"
            ),
        ];
    }

    private static PackageReference[] BatchDeclarations()
    {
        return
        [
            new("Newtonsoft.Json", "13.0.1", "/repo/src/Api/Api.csproj", "Api.csproj"),
            new("Serilog", "4.1.0", "/repo/src/Api/Api.csproj", "Api.csproj"),
            new("Newtonsoft.Json", "12.0.3", "/repo/src/Worker/Worker.csproj", "Worker.csproj"),
            new("Serilog", "3.1.1", "/repo/src/Worker/Worker.csproj", "Worker.csproj"),
            new("Moq", "4.20.70", "/repo/tests/Tests/Tests.csproj", "Tests.csproj"),
        ];
    }
}
