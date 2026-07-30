using System.Text.Json;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Pins the properties any change to the scan's scheduling has to hold, rather than its speed.
///
/// A timing assertion in CI is a flake generator, and it would not catch what actually goes wrong here.
/// Every performance change to this scan has had the same failure mode available to it: parallelism that
/// silently *erases* findings. It happened in 3.15.0 — MSBuild's static caches are not thread-safe, and
/// concurrent reads had projects reporting each other's package versions — and it happened again while an
/// attempt to parallelise the <c>dotnet package list</c> phase was being reviewed for 3.24.0, where eight
/// distinct routes to a shared <c>project.assets.json</c> turned up in succession. That attempt was
/// abandoned; see the note in <c>AnalysisHandler.ScanProjectsAsync</c> for why the safety could not be
/// established. These tests outlived it, because they are what a future attempt has to satisfy.
///
/// Each of them holds whatever the implementation does: findings do not depend on the parallelism, a
/// solution whose projects share a directory still reports its inconsistency, layouts that redirect their
/// intermediate output still report theirs, and the same solution produces the same report twice. Every one
/// of the failures they guard against produces a *clean* report with a successful exit code.
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

        // No assertion on ScanConcurrencyGate.Permits here. An earlier draft checked it, to prove the
        // parallelism under test was really in force — cross-review had caught that the gate is sized once
        // per process and silently ignored the second request. That mattered while the resolved-package phase
        // used the gate; it is serial now, so the only honest thing to assert is the invariant: whatever
        // --max-parallelism is set to, the findings are the same.
        var serial = await AnalyzeWith(parallelism: 1);
        ScanConcurrencyGate.ResetForTests();
        var parallel = await AnalyzeWith(parallelism: 8);

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

        findings
            .Should()
            .Contain(
                "VersionInconsistency",
                "the two versions differ however their intermediate output is arranged"
            );
    }

    [Fact]
    public async Task MSBuildProjectExtensionsPathDecidesTheLock_WhenBothPropertiesAreSet()
    {
        // Cross-review caught this: MSBuildProjectExtensionsPath is the property that decides where
        // project.assets.json goes, so two projects with *different* base paths but a shared extensions
        // path share the file. Taking whichever appeared first in the XML gave them separate locks.
        var shared = Path.Combine(_root, "artifacts", "ext") + Path.DirectorySeparatorChar;
        WriteProjectWithBothPaths(
            "src/Api/Api.csproj",
            "13.0.1",
            basePath: Path.Combine(_root, "artifacts", "api-base") + Path.DirectorySeparatorChar,
            extensionsPath: shared
        );
        WriteProjectWithBothPaths(
            "src/Lib/Lib.csproj",
            "12.0.3",
            basePath: Path.Combine(_root, "artifacts", "lib-base") + Path.DirectorySeparatorChar,
            extensionsPath: shared
        );
        WriteSolution("src/Api/Api.csproj", "src/Lib/Lib.csproj");

        ScanConcurrencyGate.ResetForTests();
        var findings = await AnalyzeWith(parallelism: 8);

        findings.Should().Contain("VersionInconsistency");
    }

    [Fact]
    public async Task ProjectsWithConditionalRedirectsThatDoNotApply_StillShareTheirDirectoryLock()
    {
        // Cross-review caught this: a conditional redirect cannot be evaluated in this phase, so keying on
        // its value guessed — and a wrong guess split a lock that should have been shared. These two share a
        // directory and both declare a redirect behind a condition that is false, so both restores land in
        // the shared default obj/. Locking every candidate rather than the most likely one covers it.
        WriteConditionalRedirect("Api.csproj", "13.0.1", "api-only");
        WriteConditionalRedirect("Lib.csproj", "12.0.3", "lib-only");
        WriteSolution("Api.csproj", "Lib.csproj");

        ScanConcurrencyGate.ResetForTests();
        var findings = await AnalyzeWith(parallelism: 8);

        findings
            .Should()
            .Contain(
                "VersionInconsistency",
                "whichever path the restores actually used, they used the same one"
            );
    }

    [Fact]
    public async Task ProjectsSharingAnUnresolvableIntermediatePath_StillShareALock()
    {
        // Cross-review caught this: a path built from MSBuild properties cannot be resolved here, and
        // discarding it left two projects that write to the same place holding only their own directory
        // locks. Two projects declaring the same text almost certainly mean the same place, so the text is
        // the key.
        WriteRedirect("src/Api/Api.csproj", "13.0.1", "$(MSBuildThisFileDirectory)../../artifacts/obj/");
        WriteRedirect("src/Lib/Lib.csproj", "12.0.3", "$(MSBuildThisFileDirectory)../../artifacts/obj/");
        WriteSolution("src/Api/Api.csproj", "src/Lib/Lib.csproj");

        ScanConcurrencyGate.ResetForTests();

        (await AnalyzeWith(parallelism: 8)).Should().Contain("VersionInconsistency");
    }

    [Fact]
    public async Task AProjectRedirectedIntoAnotherProjectsObj_SharesThatLock()
    {
        // Cross-review caught this: the default candidate was the project directory while declared ones were
        // the obj path, so ProjectA and a project redirected into ProjectA/obj got different keys for the
        // same file.
        WriteProject("src/Api/Api.csproj", "13.0.1");
        WriteRedirect(
            "src/Lib/Lib.csproj",
            "12.0.3",
            Path.Combine(_root, "src", "Api", "obj") + Path.DirectorySeparatorChar
        );
        WriteSolution("src/Api/Api.csproj", "src/Lib/Lib.csproj");

        ScanConcurrencyGate.ResetForTests();

        (await AnalyzeWith(parallelism: 8)).Should().Contain("VersionInconsistency");
    }

    [Fact]
    public async Task AnInvalidDeclaredPath_DoesNotAbortTheAnalysis()
    {
        // Cross-review caught this: path normalisation sat outside the protected region, so a declared value
        // containing an invalid character took the whole run down.
        WriteProject("src/Api/Api.csproj", "13.0.1");
        WriteRedirect("src/Lib/Lib.csproj", "12.0.3", "obj\0bad|path");
        WriteSolution("src/Api/Api.csproj", "src/Lib/Lib.csproj");

        ScanConcurrencyGate.ResetForTests();
        var act = async () => await AnalyzeWith(parallelism: 8);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ARepoWideIntermediatePathFromDirectoryBuildProps_IsHonoured()
    {
        // Cross-review caught this, and it is the case I had documented as a known limit rather than closed:
        // a repo-wide artifacts/obj is normally set once in a Directory.Build.props, so looking only at the
        // project files saw nothing and every project got its own default key while all of them wrote to one
        // place. Common enough to be worth closing, and still only XML.
        File.WriteAllText(
            Path.Combine(_root, "Directory.Build.props"),
            $"""
            <Project>
              <PropertyGroup>
                <BaseIntermediateOutputPath>{Path.Combine(_root, "artifacts", "obj") + Path.DirectorySeparatorChar}</BaseIntermediateOutputPath>
              </PropertyGroup>
            </Project>
            """
        );
        WriteProject("src/Api/Api.csproj", "13.0.1");
        WriteProject("src/Lib/Lib.csproj", "12.0.3");
        WriteSolution("src/Api/Api.csproj", "src/Lib/Lib.csproj");

        ScanConcurrencyGate.ResetForTests();

        (await AnalyzeWith(parallelism: 8))
            .Should()
            .Contain(
                "VersionInconsistency",
                "both projects restore into the same imported intermediate directory"
            );
    }

    [Fact]
    public async Task ProjectAssetsFilePointingAtOneFile_DoesNotLoseFindings()
    {
        // Cross-review caught this: ProjectAssetsFile names the assets file outright, and a key-discovery
        // approach that inspected only the two *directory* properties missed it. Asking "could anything have
        // moved the file" instead of "where did it go" covers this without knowing about it specifically.
        var shared = Path.Combine(_root, "artifacts", "project.assets.json");
        WriteWithProperty("src/Api/Api.csproj", "13.0.1", "ProjectAssetsFile", shared);
        WriteWithProperty("src/Lib/Lib.csproj", "12.0.3", "ProjectAssetsFile", shared);
        WriteSolution("src/Api/Api.csproj", "src/Lib/Lib.csproj");

        ScanConcurrencyGate.ResetForTests();

        (await AnalyzeWith(parallelism: 8)).Should().Contain("VersionInconsistency");
    }

    [Fact]
    public async Task ARedirectBehindAnImportChain_DoesNotLoseFindings()
    {
        // Cross-review caught this twice: first the import itself was never followed, then the regex that
        // followed it only matched double quotes. Single quotes are just as valid MSBuild, so the fixture uses
        // them — an import that is not seen is a redirect that is not seen, which is a silent race.
        File.WriteAllText(
            Path.Combine(_root, "Directory.Build.props"),
            """
            <Project>
              <Import Project='build/Paths.props' />
            </Project>
            """
        );
        Directory.CreateDirectory(Path.Combine(_root, "build"));
        File.WriteAllText(
            Path.Combine(_root, "build", "Paths.props"),
            $"""
            <Project>
              <PropertyGroup>
                <BaseIntermediateOutputPath>{Path.Combine(_root, "artifacts", "obj") + Path.DirectorySeparatorChar}</BaseIntermediateOutputPath>
              </PropertyGroup>
            </Project>
            """
        );
        WriteProject("src/Api/Api.csproj", "13.0.1");
        WriteProject("src/Lib/Lib.csproj", "12.0.3");
        WriteSolution("src/Api/Api.csproj", "src/Lib/Lib.csproj");

        ScanConcurrencyGate.ResetForTests();

        (await AnalyzeWith(parallelism: 8)).Should().Contain("VersionInconsistency");
    }

    [Fact]
    public async Task AnUnreadableProject_DoesNotAbortTheWholeAnalysis()
    {
        // Cross-review caught this as a regression I introduced: the lock lookup reads the project file
        // before it is scanned, so an exception there took down the entire run — where the scanners are
        // equipped to report that one project as an incomplete scan and carry on past it.
        WriteProject("src/Api/Api.csproj", "13.0.1");
        WriteProject("src/Lib/Lib.csproj", "12.0.3");
        var unreadable = Path.Combine(_root, "src", "Broken", "Broken.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(unreadable)!);
        File.WriteAllText(unreadable, "<Project><NotClosed>");
        WriteSolution("src/Api/Api.csproj", "src/Lib/Lib.csproj", "src/Broken/Broken.csproj");

        ScanConcurrencyGate.ResetForTests();

        var act = async () => await AnalyzeWith(parallelism: 8);

        await act.Should().NotThrowAsync();
        (await AnalyzeWith(parallelism: 8))
            .Should()
            .Contain(
                "VersionInconsistency",
                "the readable projects must still be analysed and reported"
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

    private void WriteProjectWithBothPaths(
        string relativePath,
        string version,
        string basePath,
        string extensionsPath
    )
    {
        var fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <BaseIntermediateOutputPath>{basePath}</BaseIntermediateOutputPath>
                <MSBuildProjectExtensionsPath>{extensionsPath}</MSBuildProjectExtensionsPath>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="{version}" />
              </ItemGroup>
            </Project>
            """
        );
    }

    private void WriteConditionalRedirect(string relativePath, string version, string folder)
    {
        var fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var redirect = Path.Combine(_root, "artifacts", folder) + Path.DirectorySeparatorChar;
        File.WriteAllText(
            fullPath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <PropertyGroup Condition="'$(NeverTrue)' == 'yes'">
                <BaseIntermediateOutputPath>{redirect}</BaseIntermediateOutputPath>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="{version}" />
              </ItemGroup>
            </Project>
            """
        );
    }

    private void WriteRedirect(string relativePath, string version, string intermediatePath)
    {
        var fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <BaseIntermediateOutputPath>{intermediatePath}</BaseIntermediateOutputPath>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="{version}" />
              </ItemGroup>
            </Project>
            """
        );
    }

    private void WriteWithProperty(
        string relativePath,
        string version,
        string property,
        string value
    )
    {
        var fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <{property}>{value}</{property}>
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
