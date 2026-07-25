using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Every Ask* call site must be reachable only when the console can actually service a prompt —
/// Spectre throws "Cannot show selection prompt since the current terminal isn't interactive"
/// otherwise, which is what CI and `| tee` runs hit.
///
/// These tests pin the *fallback* chosen for each site, since the safe answer is not uniform:
/// writes decline, ambiguous selections fail loudly, and the post-failure rollback proceeds.
/// </summary>
public class NonInteractiveTerminalTests : IDisposable
{
    private readonly string _testDirectory;

    public NonInteractiveTerminalTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateNonInteractive_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private static FakeConsoleService NonInteractiveConsole() => new() { IsInteractive = false };

    [Fact]
    public async Task SolutionDiscovery_MultipleSolutions_FailsLoudlyInsteadOfGuessing()
    {
        // Picking a solution arbitrarily could migrate the wrong projects, so this site must not
        // fall back to a default — it names the candidates and asks for -s.
        var project = CreateTestProject("Api.csproj");
        CreateTestSolution("Alpha.sln", project);
        CreateTestSolution("Beta.sln", project);

        var console = NonInteractiveConsole();
        var discovery = new SolutionDiscovery(console);

        var (_, projectPaths) = await discovery.DiscoverProjectsFromSolutionAsync(_testDirectory);

        projectPaths.Should().BeEmpty();
        console.ErrorMessages.Should().Contain(m => m.Contains("non-interactive"));
        console.OutputMessages.Should().Contain(m => m.Contains("-s"));
        console.OutputMessages.Should().Contain(m => m.Contains("Alpha.sln"));
        console.OutputMessages.Should().Contain(m => m.Contains("Beta.sln"));
    }

    [Fact]
    public async Task SolutionDiscovery_SingleSolution_StillResolvesWithoutPrompting()
    {
        // The guard must engage only on the ambiguous case — one solution needs no prompt.
        CreateTestSolution("Only.sln", CreateTestProject("Api.csproj"));

        var console = NonInteractiveConsole();
        var discovery = new SolutionDiscovery(console);

        var (_, projectPaths) = await discovery.DiscoverProjectsFromSolutionAsync(_testDirectory);

        projectPaths.Should().ContainSingle();
        console.ErrorMessages.Should().NotContain(m => m.Contains("non-interactive"));
    }

    [Fact]
    public async Task BuildPropsService_DeclinesTheWriteAndPointsAtForce()
    {
        CreateTestSolution("Unify.sln",
            CreateTestProject("A.csproj"),
            CreateTestProject("B.csproj"));

        var console = NonInteractiveConsole();
        var service = new BuildPropsService(console, new ProjectAnalyzer(console));

        var exitCode = await service.UnifyPropertiesAsync(
            new Options { SolutionFileDir = _testDirectory, UnifyProps = true });

        exitCode.Should().Be(ExitCodes.Success);
        File.Exists(Path.Combine(_testDirectory, "Directory.Build.props")).Should().BeFalse(
            "the write was never confirmed");
        console.OutputMessages.Should().Contain(m => m.Contains("--force"));
    }

    [Fact]
    public async Task BuildPropsService_Force_StillWritesWithoutPrompting()
    {
        // --force is the documented unattended path; the new guard must not block it.
        CreateTestSolution("Unify.sln",
            CreateTestProject("A.csproj"),
            CreateTestProject("B.csproj"));

        var console = NonInteractiveConsole();
        var service = new BuildPropsService(console, new ProjectAnalyzer(console));

        var exitCode = await service.UnifyPropertiesAsync(
            new Options { SolutionFileDir = _testDirectory, UnifyProps = true, Force = true });

        exitCode.Should().Be(ExitCodes.Success);
        File.Exists(Path.Combine(_testDirectory, "Directory.Build.props")).Should().BeTrue();
    }

    private string CreateTestProject(string projectName)
    {
        var projectPath = Path.Combine(_testDirectory, projectName);
        File.WriteAllText(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        return projectPath;
    }

    private string CreateTestSolution(string solutionName, params string[] projectPaths)
    {
        var solutionPath = Path.Combine(_testDirectory, solutionName);
        var solutionContent = @"
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
";

        foreach (var projectPath in projectPaths)
        {
            var projectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var relativePath = Path.GetRelativePath(_testDirectory, projectPath);

            solutionContent += $@"Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}"") = ""{projectName}"", ""{relativePath}"", ""{projectGuid}""
EndProject
";
        }

        solutionContent += @"Global
EndGlobal
";

        File.WriteAllText(solutionPath, solutionContent);
        return solutionPath;
    }
}
