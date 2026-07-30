using System.Text.Json;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Pins the properties the scan's concurrency depends on, rather than its speed.
///
/// A timing assertion in CI is a flake generator, and it would not catch what actually goes wrong here
/// anyway. Every performance change to this scan has had the same failure mode available to it:
/// parallelism that silently *erases* findings. It happened in 3.15.0 (MSBuild's static caches are not
/// thread-safe, and concurrent reads had projects reporting each other's package versions), and it happened
/// again while writing this — two projects in one directory share <c>obj/project.assets.json</c>, so
/// concurrent <c>dotnet package list</c> runs race on it and the loser reports the other project's
/// packages. Both produce a clean report with a successful exit code.
///
/// So what is asserted is that the findings do not depend on the parallelism, and that a solution's report
/// is identical run to run. Those hold whatever the implementation does, and they fail loudly when it goes
/// wrong.
/// </summary>
[Collection("Sequential")]
public class ScanWorkTests : IDisposable
{
    private readonly string _root;

    public ScanWorkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CPMigrateScanWork_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // The gate is process-wide; a test that resizes it must not leave that for the next one.
        ScanConcurrencyGate.ResetForTests();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task FindingsAreIdenticalAtEveryParallelism()
    {
        // The 3.15.0 regression in miniature: eight projects, two versions of one package. Parallel reads
        // that leak state between projects make them agree, and the inconsistency vanishes.
        for (var i = 0; i < 8; i++)
        {
            WriteProject($"src/P{i}/P{i}.csproj", i % 2 == 0 ? "13.0.1" : "12.0.3");
        }

        WriteSolution(Enumerable.Range(0, 8).Select(i => $"src/P{i}/P{i}.csproj").ToArray());

        // The gate is sized once per process and ignores later requests for a different limit, so without a
        // reset between runs the second scan inherits the first's single permit — and this test would pass
        // while never running anything concurrently. Cross-review caught that, which is the same
        // green-for-the-wrong-reason this suite exists to prevent.
        var serial = await AnalyzeWith(parallelism: 1);
        ScanConcurrencyGate.Permits.Should().Be(1, "the serial run must actually have been serial");

        ScanConcurrencyGate.ResetForTests();
        var parallel = await AnalyzeWith(parallelism: 8);
        ScanConcurrencyGate
            .Permits.Should()
            .Be(8, "otherwise the comparison below proves nothing about concurrency");

        parallel
            .Should()
            .BeEquivalentTo(
                serial,
                "a scan that finds less when it runs faster is worse than a slow one"
            );
        serial
            .Should()
            .NotBeEmpty("the fixture has a real inconsistency, so something must be found");
    }

    [Fact]
    public async Task ProjectsSharingADirectoryAreScannedWithoutLosingFindings()
    {
        // Two projects in one directory share obj/project.assets.json, because that path is relative to the
        // *project directory*. Restoring both at once races on that file and the loser comes back reporting
        // the other project's packages — so two projects with different versions report the same one and the
        // finding disappears. Legal layout, silent failure, successful exit code.
        WriteProject("Api.csproj", "13.0.1");
        WriteProject("Lib.csproj", "12.0.3");
        WriteSolution("Api.csproj", "Lib.csproj");

        ScanConcurrencyGate.ResetForTests();
        var findings = await AnalyzeWith(parallelism: 8);
        ScanConcurrencyGate.Permits.Should().Be(8, "the race only appears under real concurrency");

        findings
            .Should()
            .Contain(
                "VersionInconsistency",
                "13.0.1 and 12.0.3 in one solution is an inconsistency however the scan is scheduled"
            );
    }

    [Fact]
    public async Task ProjectsRedirectedToOneIntermediateDirectoryDoNotLoseFindings()
    {
        // Cross-review caught this: two projects in *different* directories that both redirect
        // BaseIntermediateOutputPath to the same place share an assets file just as surely as two in one
        // directory do — and a lock keyed on the project directory gives them different locks.
        var shared = Path.Combine(_root, "artifacts", "obj") + Path.DirectorySeparatorChar;
        WriteProject("src/Api/Api.csproj", "13.0.1", intermediatePath: shared);
        WriteProject("src/Lib/Lib.csproj", "12.0.3", intermediatePath: shared);
        WriteSolution("src/Api/Api.csproj", "src/Lib/Lib.csproj");

        ScanConcurrencyGate.ResetForTests();
        var findings = await AnalyzeWith(parallelism: 8);
        ScanConcurrencyGate.Permits.Should().Be(8);

        findings
            .Should()
            .Contain(
                "VersionInconsistency",
                "the two versions differ however their intermediate output is arranged"
            );
    }

    [Fact]
    public async Task TheSameSolutionProducesTheSameReportTwice()
    {
        // Concurrency that merges results in completion order rather than project order produces a report
        // that differs between runs — which turns any committed baseline or diff into noise.
        for (var i = 0; i < 6; i++)
        {
            WriteProject($"src/P{i}/P{i}.csproj", i % 2 == 0 ? "13.0.1" : "12.0.3");
        }

        WriteSolution(Enumerable.Range(0, 6).Select(i => $"src/P{i}/P{i}.csproj").ToArray());

        ScanConcurrencyGate.ResetForTests();
        var first = await AnalyzeRaw(parallelism: 4);
        ScanConcurrencyGate.Permits.Should().Be(4);
        var second = await AnalyzeRaw(parallelism: 4);

        StripTimestamp(second).Should().Be(StripTimestamp(first));
    }

    private static string StripTimestamp(string json)
    {
        using var document = JsonDocument.Parse(json);
        var filtered = document
            .RootElement.EnumerateObject()
            .Where(property => property.Name != "timestamp")
            .Select(property => $"{property.Name}={property.Value.GetRawText()}");

        return string.Join("\n", filtered);
    }

    private async Task<List<string>> AnalyzeWith(int parallelism)
    {
        using var document = JsonDocument.Parse(await AnalyzeRaw(parallelism));

        if (!document.RootElement.TryGetProperty("analysisIssues", out var issues))
        {
            return [];
        }

        return issues
            .EnumerateArray()
            .Select(issue => issue.GetProperty("issueCode").GetString() ?? "")
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<string> AnalyzeRaw(int parallelism)
    {
        var outputPath = Path.Combine(_root, $"report-{parallelism}-{Guid.NewGuid():N}.json");

        await ProgramRunner.RunAsync(
            [
                "--analyze",
                "--quiet",
                "--max-parallelism",
                parallelism.ToString(),
                "--output",
                "Json",
                "--output-file",
                outputPath,
                "-s",
                _root,
            ],
            new FakeConsoleService()
        );

        return await File.ReadAllTextAsync(outputPath);
    }

    private void WriteProject(string relativePath, string version, string? intermediatePath = null)
    {
        var fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var redirect = intermediatePath is null
            ? string.Empty
            : $"\n    <BaseIntermediateOutputPath>{intermediatePath}</BaseIntermediateOutputPath>";
        File.WriteAllText(
            fullPath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>{redirect}
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="{version}" />
              </ItemGroup>
            </Project>
            """
        );
    }

    private void WriteSolution(params string[] projectPaths)
    {
        var content = "Microsoft Visual Studio Solution File, Format Version 12.00\n";
        foreach (var projectPath in projectPaths)
        {
            var name = Path.GetFileNameWithoutExtension(projectPath);
            content +=
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \""
                + name
                + "\", \""
                + projectPath.Replace('/', '\\')
                + "\", \"{"
                + Guid.NewGuid().ToString().ToUpperInvariant()
                + "}\"\nEndProject\n";
        }

        File.WriteAllText(Path.Combine(_root, "App.sln"), content);
    }
}
