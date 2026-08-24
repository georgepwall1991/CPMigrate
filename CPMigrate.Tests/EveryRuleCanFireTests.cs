using System.Text.Json;
using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests;

/// <summary>
/// Drives the real CLI against real files on disk and asserts each rule actually fires.
///
/// This exists because of what 3.20.0 found: <c>RedundantDirectReference</c> had never produced a finding
/// on any project, and its unit tests passed the whole time because their fixtures used a shape real
/// restore output never contains. Every analyzer in the tree has unit tests; unit tests with a
/// hand-authored input prove the analyzer's logic, not that the pipeline in front of it delivers the shape
/// the analyzer expects. Nothing was asking the question end to end, so nothing noticed.
///
/// Two things are asserted here. Each rule that can be provoked from files alone is provoked, through
/// <see cref="ProgramRunner"/> and read back out of the JSON a consumer would parse. And <em>every</em>
/// member of <see cref="AnalysisIssueCode"/> is accounted for — a rule added without either an end-to-end
/// case or an explicit reason it cannot have one fails the build, so this cannot quietly fall behind the
/// analyzers the way the unit tests did.
/// </summary>
[Collection("Sequential")]
public class EveryRuleCanFireTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousNugetPackages;

    public EveryRuleCanFireTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CPMigrateRuleFire_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _previousNugetPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NUGET_PACKAGES", _previousNugetPackages);

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Rules with no end-to-end case here, and why. Empty, and meant to stay that way.
    ///
    /// It once held the four feed-dependent rules — vulnerabilities, outdated, deprecated, and transitive
    /// conflicts — on the grounds that they need a live NuGet feed. That is true of the *query* but not of
    /// anything after it, and anything after it is where the 3.20.0 defect lived: a parser reading a shape
    /// the feed does not produce, reporting nothing, and looking clean. Exempting the rules left exactly
    /// that part unexamined. <c>RecordedFeedOutputTests</c> drives all four from real recorded
    /// <c>dotnet package list</c> output instead, so no rule is taken on trust.
    /// </summary>
    private static readonly Dictionary<AnalysisIssueCode, string> NeedsTheNetwork = [];

    [Fact]
    public async Task VersionInconsistency_Fires()
    {
        WriteProject("src/Api/Api.csproj", ("Newtonsoft.Json", "13.0.1"));
        WriteProject("src/Worker/Worker.csproj", ("Newtonsoft.Json", "12.0.3"));
        WriteSolution("src/Api/Api.csproj", "src/Worker/Worker.csproj");

        (await Analyze()).Should().Contain(nameof(AnalysisIssueCode.VersionInconsistency));
    }

    [Fact]
    public async Task DuplicatePackageCasing_Fires()
    {
        WriteProject("src/Api/Api.csproj", ("Newtonsoft.Json", "13.0.1"));
        WriteProject("src/Worker/Worker.csproj", ("newtonsoft.json", "13.0.1"));
        WriteSolution("src/Api/Api.csproj", "src/Worker/Worker.csproj");

        (await Analyze()).Should().Contain(nameof(AnalysisIssueCode.DuplicatePackageCasing));
    }

    [Fact]
    public async Task RedundantReference_Fires()
    {
        // The same package referenced twice in one project.
        WriteProject(
            "src/Api/Api.csproj",
            ("Newtonsoft.Json", "13.0.1"),
            ("Newtonsoft.Json", "13.0.1")
        );
        WriteSolution("src/Api/Api.csproj");

        (await Analyze()).Should().Contain(nameof(AnalysisIssueCode.RedundantReference));
    }

    [Fact]
    public async Task FrameworkAlignment_Fires()
    {
        WriteProject("src/Api/Api.csproj", framework: "net10.0", packages: ("Serilog", "4.3.0"));
        WriteProject(
            "src/Legacy/Legacy.csproj",
            framework: "net8.0",
            packages: ("Serilog", "4.3.0")
        );
        WriteSolution("src/Api/Api.csproj", "src/Legacy/Legacy.csproj");

        (await Analyze()).Should().Contain(nameof(AnalysisIssueCode.FrameworkAlignment));
    }

    [Fact]
    public async Task EolTargetFramework_Fires()
    {
        // Read straight from the project file like FrameworkAlignment — no feed and no restore
        // output involved.
        WriteProject(
            "src/Legacy/Legacy.csproj",
            framework: "netcoreapp3.1",
            packages: ("Serilog", "4.3.0")
        );
        WriteSolution("src/Legacy/Legacy.csproj");

        (await Analyze()).Should().Contain(nameof(AnalysisIssueCode.EolTargetFramework));
    }

    [Fact]
    public async Task RedundantDirectReference_Fires()
    {
        // The rule 3.20.0 fixed, and the reason this file exists. The assets file is written by hand rather
        // than restored — the point is not to test NuGet, it is that the shape on disk is the shape the
        // analyzer reads. Written the way restore writes it: a range under the framework's dependencies,
        // the resolved version in the targets key.
        WriteProject("src/Api/Api.csproj", ("Serilog.Sinks.File", "7.0.0"), ("Serilog", "4.2.0"));
        WriteSolution("src/Api/Api.csproj");
        WriteFile(
            "src/Api/obj/project.assets.json",
            """
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Serilog.Sinks.File/7.0.0": {
                    "type": "package",
                    "dependencies": { "Serilog": "4.2.0" }
                  },
                  "Serilog/4.2.0": { "type": "package" }
                }
              },
              "project": {
                "frameworks": {
                  "net10.0": {
                    "dependencies": {
                      "Serilog.Sinks.File": { "target": "Package", "version": "[7.0.0, )" },
                      "Serilog": { "target": "Package", "version": "[4.2.0, )" }
                    }
                  }
                }
              }
            }
            """
        );

        (await Analyze()).Should().Contain(nameof(AnalysisIssueCode.RedundantDirectReference));
    }

    [Fact]
    public async Task CpmNotEnabled_Fires()
    {
        WriteProject("src/Api/Api.csproj", ("Serilog", "4.3.0"));
        WriteSolution("src/Api/Api.csproj");
        WriteFile(
            "Directory.Packages.props",
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.3.0" />
              </ItemGroup>
            </Project>
            """
        );

        (await Analyze()).Should().Contain(nameof(AnalysisIssueCode.CpmNotEnabled));
    }

    [Fact]
    public async Task InlineVersionUnderCpm_Fires()
    {
        WriteProject("src/Api/Api.csproj", ("Serilog", "4.0.0"));
        WriteSolution("src/Api/Api.csproj");
        WriteCentralProps(("Serilog", "4.3.0"));

        (await Analyze()).Should().Contain(nameof(AnalysisIssueCode.InlineVersionUnderCpm));
    }

    [Fact]
    public async Task MissingPackageVersion_Fires()
    {
        // Referenced with no version inline and none centrally: restore fails, so this is the one rule
        // whose absence is guaranteed to be noticed eventually — but not before someone wastes an hour.
        WriteFile(
            "src/Api/Api.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Serilog" />
              </ItemGroup>
            </Project>
            """
        );
        WriteSolution("src/Api/Api.csproj");
        WriteCentralProps(("Newtonsoft.Json", "13.0.1"));

        (await Analyze()).Should().Contain(nameof(AnalysisIssueCode.MissingPackageVersion));
    }

    [Fact]
    public async Task OrphanedPackageVersion_Fires()
    {
        WriteProject("src/Api/Api.csproj", ("Serilog", "4.3.0"));
        WriteSolution("src/Api/Api.csproj");
        WriteCentralProps(("Serilog", "4.3.0"), ("Nobody.References.This", "1.0.0"));

        (await Analyze()).Should().Contain(nameof(AnalysisIssueCode.OrphanedPackageVersion));
    }

    // ------------------------------------------------------------------ completeness

    [Fact]
    public void EveryRuleIsEitherProvenEndToEnd_OrSaysWhyItCannotBe()
    {
        // The guard that keeps this file honest. A rule added with neither an end-to-end case nor a stated
        // reason it cannot have one fails here — which is exactly the gap RedundantDirectReference sat in.
        var provenHere = GetType()
            .GetMethods()
            .Where(method => method.Name.EndsWith("_Fires", StringComparison.Ordinal))
            .Select(method => method.Name[..^"_Fires".Length])
            .ToHashSet(StringComparer.Ordinal);

        // A rule may be proven here, against files on disk, or in RecordedFeedOutputTests, against real
        // recorded feed output. Both drive the parse-to-report path; only the process launch differs.
        var provenFromRecordedOutput = typeof(Services.RecordedFeedOutputTests)
            .GetMethods()
            .Where(method =>
                method.Name.EndsWith("_FiresFromRecordedOutput", StringComparison.Ordinal)
            )
            .Select(method => method.Name[..^"_FiresFromRecordedOutput".Length])
            .ToHashSet(StringComparer.Ordinal);

        var unaccounted = Enum.GetValues<AnalysisIssueCode>()
            .Where(code => code != AnalysisIssueCode.Unknown)
            .Where(code => !provenHere.Contains(code.ToString()))
            .Where(code => !provenFromRecordedOutput.Contains(code.ToString()))
            .Where(code => !NeedsTheNetwork.ContainsKey(code))
            .Select(code => code.ToString())
            .ToList();

        unaccounted
            .Should()
            .BeEmpty(
                "every rule needs a test proving it fires — here against real files, or in "
                    + "RecordedFeedOutputTests against real recorded feed output — or an entry in "
                    + "NeedsTheNetwork explaining why it can have neither"
            );
    }

    [Fact]
    public void TheNetworkExemptionListDoesNotOutliveItsRules()
    {
        // An exemption for a rule that no longer exists, or one that has since gained an offline test, is a
        // stale claim about coverage.
        var real = Enum.GetValues<AnalysisIssueCode>().ToHashSet();

        NeedsTheNetwork.Keys.Should().OnlyContain(code => real.Contains(code));
        NeedsTheNetwork.Values.Should().OnlyContain(reason => reason.Length > 20);
        NeedsTheNetwork
            .Should()
            .BeEmpty(
                "every rule is currently proven end to end; an addition here needs a reason that "
                    + "survives the question 'could this be driven from recorded output instead?'"
            );
    }

    [Fact]
    public async Task LicenseRisk_Fires()
    {
        // MySql.Data ships under GPL-2.0. The scan reads that from the nuspec restore already
        // fetched — not from a hardcoded package-name table — so the fixture is a real nuspec
        // layout under NUGET_PACKAGES.
        WriteProject("src/Api/Api.csproj", ("MySql.Data", "8.0.33"));
        WriteSolution("src/Api/Api.csproj");
        SeedNuspec("mysql.data", "8.0.33", "GPL-2.0-only");

        (await Analyze("--licenses")).Should().Contain(nameof(AnalysisIssueCode.LicenseRisk));
    }

    [Fact]
    public async Task LicenseRisk_DoesNotFireWithoutTheFlag()
    {
        WriteProject("src/Api/Api.csproj", ("MySql.Data", "8.0.33"));
        WriteSolution("src/Api/Api.csproj");
        SeedNuspec("mysql.data", "8.0.33", "GPL-2.0-only");

        (await Analyze()).Should().NotContain(nameof(AnalysisIssueCode.LicenseRisk));
    }

    [Fact]
    public async Task FloatingVersion_Fires()
    {
        // A wildcard read straight from the project file — no feed, and deliberately not from the
        // resolved graph, where restore has already turned it into a concrete version.
        WriteProject("src/Api/Api.csproj", ("Newtonsoft.Json", "13.0.*"));
        WriteSolution("src/Api/Api.csproj");

        (await Analyze()).Should().Contain(nameof(AnalysisIssueCode.FloatingVersion));
    }

    [Fact]
    public async Task FloatingVersion_FiresOnACentralPin()
    {
        // After a migration every version lives in the props file. A rule confined to project files
        // would go quiet on exactly the solutions this tool produces, which is the same
        // never-fires shape the guard exists to catch.
        WriteFile(
            "src/Api/Api.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" />
              </ItemGroup>
            </Project>
            """
        );
        WriteSolution("src/Api/Api.csproj");
        WriteCentralProps(("Newtonsoft.Json", "[13.0.1,)"));

        (await Analyze()).Should().Contain(nameof(AnalysisIssueCode.FloatingVersion));
    }

    [Fact]
    public async Task FloatingVersion_DoesNotFireOnAnExactlyPinnedSolution()
    {
        // The other half of the guard: a rule that fires on everything is as useless as one that
        // fires on nothing, and [13.0.1] is the most exact form NuGet has.
        WriteProject("src/Api/Api.csproj", ("Newtonsoft.Json", "[13.0.1]"));
        WriteSolution("src/Api/Api.csproj");

        (await Analyze()).Should().NotContain(nameof(AnalysisIssueCode.FloatingVersion));
    }

    [Fact]
    public async Task RedundantReference_FiresUnderCentralPackageManagement()
    {
        // Cross-review caught this: the declaration scan reused a method that drops PackageReference items
        // with no Version — and under CPM a reference normally *has* no version. So for the majority of
        // this tool's users the rule still could not fire, having just been "fixed".
        WriteFile(
            "src/Api/Api.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" />
                <PackageReference Include="Newtonsoft.Json" />
              </ItemGroup>
            </Project>
            """
        );
        WriteSolution("src/Api/Api.csproj");
        WriteCentralProps(("Newtonsoft.Json", "13.0.1"));

        (await Analyze()).Should().Contain(nameof(AnalysisIssueCode.RedundantReference));
    }

    [Fact]
    public async Task ConditionalDeclarations_AreNotReportedAsDuplicates()
    {
        // Cross-review caught this, and it was the more serious of the two: declaring a package once per
        // target framework behind a Condition is how multi-targeting is written. Reporting it would be bad
        // enough, but the finding is *fixable* — so --fix would delete the declaration another framework
        // depends on. A rule that quietly reported nothing would have become a rule that breaks a build,
        // which is the worse failure of the two.
        WriteFile(
            "src/Api/Api.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
              </PropertyGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Include="Serilog" Version="4.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
                <PackageReference Include="Serilog" Version="4.3.0" />
              </ItemGroup>
            </Project>
            """
        );
        WriteSolution("src/Api/Api.csproj");

        // Nor as a version inconsistency — which is what the fix for the above uncovered. That rule read
        // the resolved list, where conditions no longer exist, so it saw 4.0.0 and 4.3.0 in one project and
        // called them inconsistent. Being fixable, --fix then unified them to 4.3.0 and broke net8.0.
        (await Analyze())
            .Should()
            .BeEmpty("a per-framework pin is deliberate, not a defect");
    }

    [Fact]
    public async Task Fix_LeavesConditionalDeclarationsUntouched()
    {
        // The consequence, asserted on the file: both framework-specific declarations must survive a --fix
        // run. Checking the finding is absent is not enough — the destructive step is the one to pin.
        WriteFile(
            "src/Api/Api.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
              </PropertyGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Include="Serilog" Version="4.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
                <PackageReference Include="Serilog" Version="4.3.0" />
              </ItemGroup>
            </Project>
            """
        );
        WriteSolution("src/Api/Api.csproj");
        var projectFile = Path.Combine(_root, "src", "Api", "Api.csproj");

        await ProgramRunner.RunAsync(
            ["--analyze", "--fix", "--no-backup", "--quiet", "-s", _root],
            new FakeConsoleService()
        );

        var content = await File.ReadAllTextAsync(projectFile);
        content.Should().Contain("4.0.0", "the net8.0 declaration must survive");
        content.Should().Contain("4.3.0", "the net10.0 declaration must survive");
    }

    [Fact]
    public async Task Fix_RemovesTheUnconditionalDuplicatesAndKeepsTheConditionalOne()
    {
        // Cross-review caught this: a project can hold two unconditional duplicates *and* a
        // framework-specific declaration. The analyzer's condition filter correctly still reports the real
        // duplicate, but the fixer removed everything after the first match without checking conditions —
        // deleting the very declaration that filter exists to protect, and calling it a tidy-up.
        WriteFile(
            "src/Api/Api.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.3.0" />
                <PackageReference Include="Serilog" Version="4.3.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Include="Serilog" Version="4.0.0" />
              </ItemGroup>
            </Project>
            """
        );
        WriteSolution("src/Api/Api.csproj");
        var projectFile = Path.Combine(_root, "src", "Api", "Api.csproj");

        await ProgramRunner.RunAsync(
            ["--analyze", "--fix", "--no-backup", "--quiet", "-s", _root],
            new FakeConsoleService()
        );

        var content = await File.ReadAllTextAsync(projectFile);
        content.Should().Contain("4.0.0", "the net8.0 declaration must survive");
        (content.Split("<PackageReference").Length - 1)
            .Should()
            .Be(2, "one unconditional duplicate removed, the conditional one kept");
    }

    [Fact]
    public async Task AConditionalPinDoesNotDecideTheVersionOtherProjectsAreUnifiedTo()
    {
        // Cross-review caught this: the analyzer excludes a conditional pin from the *comparison*, but the
        // fixer still drew its target version from every reference. A framework-conditional 99.0 in one
        // project would drag unconditional 1.0.0 and 2.0.0 in others up to 99.0 — on the strength of a
        // finding that only mentioned 1.0.0 and 2.0.0. Not writing to a conditional declaration is not
        // enough if it still decides what gets written elsewhere.
        WriteProject("src/Api/Api.csproj", ("Serilog", "1.0.0"));
        WriteProject("src/Worker/Worker.csproj", ("Serilog", "2.0.0"));
        WriteFile(
            "src/Legacy/Legacy.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
              </PropertyGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Include="Serilog" Version="99.0.0" />
              </ItemGroup>
            </Project>
            """
        );
        WriteSolution("src/Api/Api.csproj", "src/Worker/Worker.csproj", "src/Legacy/Legacy.csproj");

        await ProgramRunner.RunAsync(
            ["--analyze", "--fix", "--no-backup", "--quiet", "-s", _root],
            new FakeConsoleService()
        );

        var api = await File.ReadAllTextAsync(Path.Combine(_root, "src", "Api", "Api.csproj"));
        var worker = await File.ReadAllTextAsync(
            Path.Combine(_root, "src", "Worker", "Worker.csproj")
        );
        var legacy = await File.ReadAllTextAsync(
            Path.Combine(_root, "src", "Legacy", "Legacy.csproj")
        );

        api.Should().Contain("2.0.0").And.NotContain("99.0.0");
        worker.Should().Contain("2.0.0").And.NotContain("99.0.0");
        legacy.Should().Contain("99.0.0", "the conditional pin itself is untouched");
    }

    [Fact]
    public async Task AnOtherwiseBranchSurvives_EvenWithUnconditionalDuplicatesInTheSameFile()
    {
        // Cross-review caught this, and it showed the Choose/When test above was passing for the wrong
        // reason: <Otherwise> carries no Condition attribute, so it read as unconditional. On its own that
        // was harmless — one unconditional declaration is not a duplicate — but add real duplicates
        // elsewhere in the file and the fallback branch became a deletion candidate.
        WriteFile(
            "src/Api/Api.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.3.0" />
                <PackageReference Include="Serilog" Version="4.3.0" />
              </ItemGroup>
              <Choose>
                <When Condition="'$(TargetFramework)' == 'net8.0'">
                  <ItemGroup>
                    <PackageReference Include="Serilog" Version="4.0.0" />
                  </ItemGroup>
                </When>
                <Otherwise>
                  <ItemGroup>
                    <PackageReference Include="Serilog" Version="4.1.0" />
                  </ItemGroup>
                </Otherwise>
              </Choose>
            </Project>
            """
        );
        WriteSolution("src/Api/Api.csproj");
        var projectFile = Path.Combine(_root, "src", "Api", "Api.csproj");

        await ProgramRunner.RunAsync(
            ["--analyze", "--fix", "--no-backup", "--quiet", "-s", _root],
            new FakeConsoleService()
        );

        var content = await File.ReadAllTextAsync(projectFile);
        content.Should().Contain("4.0.0", "the When branch must survive");
        content.Should().Contain("4.1.0", "the Otherwise branch must survive");
    }

    [Fact]
    public async Task AProjectPinningBothWaysStillReportsARealInconsistency()
    {
        // Cross-review caught this: excluding a project/package pair as soon as *any* declaration was
        // conditional meant a project pinning unconditionally and overriding for one framework had its
        // unconditional pin excluded too — hiding a genuine inconsistency with another project.
        WriteFile(
            "src/Api/Api.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Serilog" Version="1.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Include="Serilog" Version="99.0.0" />
              </ItemGroup>
            </Project>
            """
        );
        WriteProject("src/Worker/Worker.csproj", ("Serilog", "2.0.0"));
        WriteSolution("src/Api/Api.csproj", "src/Worker/Worker.csproj");

        (await Analyze())
            .Should()
            .Contain(
                nameof(AnalysisIssueCode.VersionInconsistency),
                "1.0.0 against 2.0.0 is a real inconsistency, whatever else Api pins conditionally"
            );

        // And the conditional 99.0.0 must not be what everyone gets unified to. Cross-review caught this
        // as the mirror image of the previous round's fix: asking the question per package hid the
        // unconditional pin, asking it per version keeps the pin comparable while leaving the override out.
        await ProgramRunner.RunAsync(
            ["--analyze", "--fix", "--no-backup", "--quiet", "-s", _root],
            new FakeConsoleService()
        );

        var api = await File.ReadAllTextAsync(Path.Combine(_root, "src", "Api", "Api.csproj"));
        var worker = await File.ReadAllTextAsync(
            Path.Combine(_root, "src", "Worker", "Worker.csproj")
        );

        worker.Should().Contain("2.0.0").And.NotContain("99.0.0");
        api.Should()
            .Contain("99.0.0", "the conditional override itself is left alone")
            .And.NotContain("1.0.0", "the unconditional pin is unified to 2.0.0");
    }

    [Fact]
    public async Task ADeclarationInsideChooseWhen_CountsAsConditional()
    {
        // Cross-review caught this: <Choose><When Condition=…><ItemGroup> puts no condition on the item or
        // its group, so checking only those two levels read a mutually exclusive pair as duplicates of each
        // other — and the fixer would have deleted one branch.
        WriteFile(
            "src/Api/Api.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
              </PropertyGroup>
              <Choose>
                <When Condition="'$(TargetFramework)' == 'net8.0'">
                  <ItemGroup>
                    <PackageReference Include="Serilog" Version="4.0.0" />
                  </ItemGroup>
                </When>
                <Otherwise>
                  <ItemGroup>
                    <PackageReference Include="Serilog" Version="4.3.0" />
                  </ItemGroup>
                </Otherwise>
              </Choose>
            </Project>
            """
        );
        WriteSolution("src/Api/Api.csproj");
        var projectFile = Path.Combine(_root, "src", "Api", "Api.csproj");

        await ProgramRunner.RunAsync(
            ["--analyze", "--fix", "--no-backup", "--quiet", "-s", _root],
            new FakeConsoleService()
        );

        var content = await File.ReadAllTextAsync(projectFile);
        content.Should().Contain("4.0.0", "the When branch must survive");
        content.Should().Contain("4.3.0", "the Otherwise branch must survive");
    }

    [Fact]
    public async Task AFixableFinding_IsActuallyRepairedByFix()
    {
        // Reporting a finding as fixable and then not fixing it is its own silent failure, and this rule had
        // both halves broken: the fixer resolved projects by file name while AffectedProjects has carried
        // project ids since 3.10.0, so it matched nothing and returned "no fix needed". The run printed
        // "No changes were needed" over an unrepaired finding — two statements each true, jointly
        // misleading. Asserted on the file, because that is the only thing that proves work happened.
        WriteProject(
            "src/Api/Api.csproj",
            ("Newtonsoft.Json", "13.0.1"),
            ("Newtonsoft.Json", "13.0.1")
        );
        WriteSolution("src/Api/Api.csproj");
        var projectFile = Path.Combine(_root, "src", "Api", "Api.csproj");

        var exitCode = await ProgramRunner.RunAsync(
            ["--analyze", "--fix", "--no-backup", "--quiet", "-s", _root],
            new FakeConsoleService()
        );

        exitCode.Should().Be(ExitCodes.Success);
        var occurrences =
            (await File.ReadAllTextAsync(projectFile)).Split("<PackageReference").Length - 1;
        occurrences.Should().Be(1, "the duplicate reference must be gone from the project file");
    }

    [Fact]
    public async Task AProjectWithNothingWrong_ProducesNoFindings()
    {
        // Without this the suite above could be satisfied by an analyzer that reports unconditionally.
        WriteProject("src/Api/Api.csproj", ("Serilog", "4.3.0"));
        WriteProject("src/Worker/Worker.csproj", ("Serilog", "4.3.0"));
        WriteSolution("src/Api/Api.csproj", "src/Worker/Worker.csproj");

        (await Analyze()).Should().BeEmpty();
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Runs the CLI as a user would and returns the issue codes it reported, read out of the JSON payload
    /// rather than from any internal collection — so this covers the path from disk to output.
    /// </summary>
    private async Task<List<string>> Analyze(params string[] extraArgs)
    {
        var outputPath = Path.Combine(_root, "report.json");
        var args = new List<string>
        {
            "--analyze",
            "--quiet",
            "--output",
            "Json",
            "--output-file",
            outputPath,
            "-s",
            _root,
        };
        args.AddRange(extraArgs);

        await ProgramRunner.RunAsync(args.ToArray(), new FakeConsoleService());

        File.Exists(outputPath).Should().BeTrue("the run must have produced a report");

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));

        if (!document.RootElement.TryGetProperty("analysisIssues", out var issues))
        {
            return [];
        }

        return issues
            .EnumerateArray()
            .Select(issue => issue.GetProperty("issueCode").GetString() ?? "")
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private void SeedNuspec(string packageIdLower, string version, string expression)
    {
        var packages = Path.Combine(_root, "packages");
        var directory = Path.Combine(packages, packageIdLower, version);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, $"{packageIdLower}.nuspec"),
            $"""<package><metadata><license type="expression">{expression}</license></metadata></package>"""
        );
        Environment.SetEnvironmentVariable("NUGET_PACKAGES", packages);
    }

    private void WriteProject(
        string relativePath,
        params (string Name, string Version)[] packages
    ) => WriteProject(relativePath, "net10.0", packages);

    private void WriteProject(
        string relativePath,
        string framework = "net10.0",
        params (string Name, string Version)[] packages
    )
    {
        var references = string.Join(
            "\n    ",
            packages.Select(p =>
                $"""<PackageReference Include="{p.Name}" Version="{p.Version}" />"""
            )
        );

        WriteFile(
            relativePath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{framework}</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                {references}
              </ItemGroup>
            </Project>
            """
        );
    }

    private void WriteCentralProps(params (string Name, string Version)[] packages)
    {
        var entries = string.Join(
            "\n    ",
            packages.Select(p => $"""<PackageVersion Include="{p.Name}" Version="{p.Version}" />""")
        );

        WriteFile(
            "Directory.Packages.props",
            $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                {entries}
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
            var guid = $"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}";
            var winPath = projectPath.Replace('/', '\\');
            content +=
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \""
                + name
                + "\", \""
                + winPath
                + "\", \""
                + guid
                + "\"\nEndProject\n";
        }

        WriteFile("App.sln", content);
    }

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
