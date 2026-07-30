using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// A project that reports no frameworks did not resolve, whatever the exit code says — and until this was
/// added, every caller downstream saw a successful scan that happened to find nothing.
///
/// That is how 3.26.0 lost three of Serilog's six projects for a whole release. The isolation it introduced
/// broke any project with a <c>ProjectReference</c>, those queries came back with no frameworks, and the
/// scan counted each as a success with zero packages. The project simply vanished from the report:
/// <c>scanFailures: 0</c>, no warning, no error. The breakage was in the output the whole time and nothing
/// was looking at it.
/// </summary>
public class FrameworklessScanTests
{
    [Fact]
    public void AProjectReportingNoFrameworksIsNotAUsableResult()
    {
        // The exact shape returned when a restore is broken by a redirected intermediate directory.
        const string output = """
            {"version":1,"parameters":"","projects":[{"path":"/src/App/App.csproj"}]}
            """;

        DotNetPackageQueryService.DescribesAnyFramework(output).Should().BeFalse();
    }

    [Fact]
    public void AnExplicitlyNullFrameworksListIsNotUsableEither()
    {
        const string output = """
            {"version":1,"projects":[{"path":"/src/App/App.csproj","frameworks":null}]}
            """;

        DotNetPackageQueryService.DescribesAnyFramework(output).Should().BeFalse();
    }

    [Fact]
    public void AnEmptyFrameworksListIsNotUsableEither()
    {
        const string output = """
            {"version":1,"projects":[{"path":"/src/App/App.csproj","frameworks":[]}]}
            """;

        DotNetPackageQueryService.DescribesAnyFramework(output).Should().BeFalse();
    }

    [Fact]
    public void AProjectWithNoPackagesIsStillAUsableResult()
    {
        // The distinction the whole check rests on, and it is not an assumption — a real project with zero
        // PackageReferences was run through `dotnet package list` and reports its framework with an empty
        // package list. Treating that as a failure would turn every package-free project into a scan error.
        const string output = """
            {"version":1,"projects":[{"path":"/src/App/App.csproj","frameworks":[
              {"framework":"net10.0","topLevelPackages":[]}]}]}
            """;

        DotNetPackageQueryService.DescribesAnyFramework(output).Should().BeTrue();
    }

    [Fact]
    public void UnparseableOutputIsLeftToThePathThatReportsItProperly()
    {
        // Saying "no frameworks" for malformed JSON would replace a specific error with a vaguer one.
        DotNetPackageQueryService.DescribesAnyFramework("not json at all").Should().BeTrue();
    }
}
