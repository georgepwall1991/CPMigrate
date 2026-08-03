using System.Text.Json;
using CPMigrate;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests;

/// <summary>
/// Drives <c>--verify</c> through the real CLI, against real project files, with two real restores.
///
/// The unit tests prove the snapshot reader, the diff, and the attribution in isolation. None of them
/// can prove that the pipeline in front of those pieces delivers the shape they expect — which is
/// exactly how three analyzers in this repository shipped for many releases while being incapable of
/// firing, each with green unit tests over fixtures the real pipeline never produces. A feature whose
/// entire job is to notice that something changed is worthless if it silently notices nothing, and
/// that failure is invisible from a fixture.
///
/// These need the NuGet feed, because a resolved graph is a thing restore produces and nothing else.
/// They are the slowest tests in the suite for the same reason: two restores each, by design.
/// </summary>
public class VerifyEndToEndTests : IDisposable
{
    private readonly string _root;

    public VerifyEndToEndTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CPMigrateVerifyE2E_{Guid.NewGuid():N}");
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
    public async Task ReportsACleanGraph_WhenTheMigrationChangesNothingThatShips()
    {
        // Two projects agreeing on a version: moving those versions into the props file cannot change
        // what restores. This is the case the whole feature exists to be able to *assert*, and the
        // half that stops "verification passed" from meaning "the check does nothing".
        WriteProject("src/Api/Api.csproj", "Newtonsoft.Json", "13.0.3");
        WriteProject("src/Worker/Worker.csproj", "Newtonsoft.Json", "13.0.3");
        WriteSolution("src/Api/Api.csproj", "src/Worker/Worker.csproj");

        var (exitCode, verification) = await Verify();

        exitCode.Should().Be(ExitCodes.Success);
        verification.GetProperty("verdict").GetString().Should().Be("unchanged");
        verification.GetProperty("changed").GetInt32().Should().Be(0);
        verification
            .GetProperty("resolvedVersions")
            .GetInt32()
            .Should()
            .BeGreaterThan(0, "a graph of nothing would compare clean against anything");
        verification.GetProperty("projectsRestored").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ReportsTheUnification_WhenTwoProjectsDisagree()
    {
        // The change a migration makes and never used to mention: --conflict-strategy Highest is not
        // a no-op, and the project on the losing side is silently upgraded.
        WriteProject("src/Api/Api.csproj", "Newtonsoft.Json", "13.0.1");
        WriteProject("src/Worker/Worker.csproj", "Newtonsoft.Json", "13.0.3");
        WriteSolution("src/Api/Api.csproj", "src/Worker/Worker.csproj");

        var (exitCode, verification) = await Verify();

        exitCode.Should().Be(ExitCodes.Success, "the change is accounted for");
        verification.GetProperty("verdict").GetString().Should().Be("explainedDrift");
        verification.GetProperty("unexplained").GetInt32().Should().Be(0);

        var decision = verification.GetProperty("decisions").EnumerateArray().Single();
        decision.GetProperty("packageId").GetString().Should().Be("Newtonsoft.Json");
        decision.GetProperty("resolvedVersion").GetString().Should().Be("13.0.3");
        decision.GetProperty("source").GetString().Should().Be("highest");

        var change = verification
            .GetProperty("changes")
            .EnumerateArray()
            .Single(c => c.GetProperty("packageId").GetString() == "Newtonsoft.Json");

        change.GetProperty("project").GetString().Should().Be("src/Api/Api.csproj");
        change.GetProperty("before").GetString().Should().Be("13.0.1");
        change.GetProperty("after").GetString().Should().Be("13.0.3");
        change.GetProperty("direction").GetString().Should().Be("upgrade");
        change.GetProperty("explanation").GetString().Should().Be("conflictUnified");
    }

    [Fact]
    public async Task NamesProjectsRelativeToTheScanRoot()
    {
        // An absolute path would make the receipt differ between a developer's machine and CI while
        // describing exactly the same result — the reason analysisIssues[].affectedProjects moved to
        // relative paths in output schema 1.3.0.
        WriteProject("src/Api/Api.csproj", "Newtonsoft.Json", "13.0.1");
        WriteProject("src/Worker/Worker.csproj", "Newtonsoft.Json", "13.0.3");
        WriteSolution("src/Api/Api.csproj", "src/Worker/Worker.csproj");

        var (_, verification) = await Verify();

        verification
            .GetProperty("changes")
            .EnumerateArray()
            .Select(c => c.GetProperty("project").GetString())
            .Should()
            .OnlyContain(path => path!.StartsWith("src/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestoresARelativeTarget()
    {
        // RunRestoreAsync runs from the target's own directory while passing the path through as the
        // argument, so a relative `-s src/Solution.slnx` resolved to `src/src/Solution.slnx` and the
        // baseline restore failed - reported as "this solution does not restore" about a solution
        // that restores perfectly well, which is the worst kind of finding this feature can produce.
        // Cross-review caught it; every other test here passes an absolute path.
        //
        // The relative path is built against the process working directory rather than by changing
        // it. Directory.SetCurrentDirectory is global to the process and xUnit runs test classes in
        // parallel, so two classes each saving and restoring it will hand one another a temp
        // directory that the other then deletes - after which every GetCurrentDirectory in the run
        // throws. That surfaces as dozens of unrelated failures a long way from the cause, which is
        // exactly what happened when this test was first written that way.
        WriteProject("src/Api/Api.csproj", "Newtonsoft.Json", "13.0.3");
        WriteSolutionAt("src/Solution.slnx", "Api/Api.csproj");

        var relativeSolution = Path.GetRelativePath(
            Directory.GetCurrentDirectory(),
            Path.Combine(_root, "src", "Solution.slnx")
        );

        Path.IsPathRooted(relativeSolution)
            .Should()
            .BeFalse("the point of the test is that a relative target resolves correctly");

        var (exitCode, verification) = await Verify(target: relativeSolution);

        exitCode.Should().Be(ExitCodes.Success);
        verification.GetProperty("verdict").GetString().Should().Be("unchanged");
    }

    [Fact]
    public async Task FailsUnderStrict_OnDriftItCanNonethelessExplain()
    {
        // --verify-strict is not a stricter analysis; it is a different question. "Every change is
        // accounted for" and "nothing changed" are not the same claim, and a team can require the
        // second.
        WriteProject("src/Api/Api.csproj", "Newtonsoft.Json", "13.0.1");
        WriteProject("src/Worker/Worker.csproj", "Newtonsoft.Json", "13.0.3");
        WriteSolution("src/Api/Api.csproj", "src/Worker/Worker.csproj");

        var (exitCode, verification) = await Verify(target: null, "--verify-strict");

        exitCode.Should().Be(ExitCodes.GraphDrift);
        verification.GetProperty("verdict").GetString().Should().Be("explainedDrift");
        verification.GetProperty("passed").GetBoolean().Should().BeFalse();
        verification
            .GetProperty("rolledBack")
            .GetBoolean()
            .Should()
            .BeFalse("drift the report accounts for is left in place so it can be read");
    }

    [Fact]
    public async Task FailsWithoutWritingAnything_WhenTheBaselineCannotBeEstablished()
    {
        // The baseline is captured before a single byte is written, so a solution that cannot restore
        // to begin with costs one failed restore rather than a half-migrated tree. And a run that
        // reached no verdict is never reported as clean.
        WriteProject("src/Api/Api.csproj", "This.Package.Does.Not.Exist.Cpmigrate", "9.9.9");
        WriteSolution("src/Api/Api.csproj");

        var (exitCode, verification) = await Verify();

        exitCode.Should().Be(ExitCodes.GraphDrift);
        verification.GetProperty("verdict").GetString().Should().Be("failed");
        verification.GetProperty("passed").GetBoolean().Should().BeFalse();
        verification
            .GetProperty("failureReason")
            .GetString()
            .Should()
            .Contain("before the migration");

        File.Exists(Path.Combine(_root, "Directory.Packages.props"))
            .Should()
            .BeFalse("nothing should be written when no baseline could be taken");
        (await File.ReadAllTextAsync(Path.Combine(_root, "src", "Api", "Api.csproj")))
            .Should()
            .Contain("Version=\"9.9.9\"", "the project file must be untouched");
    }

    private async Task<(int ExitCode, JsonElement Verification)> Verify(
        string? target = null,
        params string[] extraArgs
    )
    {
        var reportPath = Path.Combine(_root, "verify.json");

        string[] args =
        [
            "--verify",
            "--force",
            "--quiet",
            "--output",
            "Json",
            "--output-file",
            reportPath,
            "-s",
            target ?? _root,
            .. extraArgs,
        ];

        var exitCode = await ProgramRunner.RunAsync(args, new FakeConsoleService());

        File.Exists(reportPath).Should().BeTrue("the run must have produced a report");

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));

        document
            .RootElement.TryGetProperty("verification", out var verification)
            .Should()
            .BeTrue("a --verify run must publish its receipt");

        return (exitCode, verification.Clone());
    }

    private void WriteProject(string relativePath, string package, string version)
    {
        WriteFile(
            relativePath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{package}" Version="{version}" />
              </ItemGroup>
            </Project>
            """
        );
    }

    private void WriteSolution(params string[] projectPaths) =>
        WriteSolutionAt("Solution.slnx", projectPaths);

    private void WriteSolutionAt(string solutionPath, params string[] projectPaths)
    {
        var projects = string.Join(
            Environment.NewLine,
            projectPaths.Select(path =>
                $"""    <Project Path="{path.Replace('/', Path.DirectorySeparatorChar)}" />"""
            )
        );

        // .slnx rather than .sln: hand-written .sln fixtures are rejected by
        // Microsoft.VisualStudio.SolutionPersistence, and CPMigrate supports both.
        WriteFile(
            solutionPath,
            $"""
            <Solution>
            {projects}
            </Solution>
            """
        );
    }

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
