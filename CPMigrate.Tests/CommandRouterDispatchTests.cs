using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests;

/// <summary>
/// Which mode wins when several flags are present used to be decided implicitly, by the order of an
/// if/else chain that had grown to seven branches. Precedence is a real part of the CLI's behaviour —
/// <c>--update --interactive</c> has to resolve the same way every time — so it is worth asserting
/// rather than inferring from the source.
/// </summary>
[Collection("Sequential")]
public class CommandRouterDispatchTests : IDisposable
{
    private readonly string _testDirectory;

    public CommandRouterDispatchTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateDispatch_{Guid.NewGuid():N}");
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

    [Fact]
    public async Task Completions_WinOverEveryOtherMode()
    {
        // Pure output must never be preceded by anything that writes to stdout, or the documented
        // redirection produces a corrupt script.
        var console = new FakeConsoleService();

        var stdout = await CaptureStdoutAsync(() =>
            ProgramRunner.RunAsync(
                new[] { "--completions", "Bash", "--interactive", "-s", _testDirectory },
                console
            )
        );

        stdout.Should().Contain("complete -F _cpmigrate_completions");
        console.SelectionResponses.Should().BeEmpty("the wizard must not have been entered");
    }

    [Fact]
    public async Task Explain_WinsOverTheDefaultMigrateAction()
    {
        // The default action rewrites project files. A read-only intent must never become a write.
        var console = new FakeConsoleService();

        var stdout = await CaptureStdoutAsync(() =>
            ProgramRunner.RunAsync(
                new[] { "--explain", "VersionInconsistency", "-s", _testDirectory },
                console
            )
        );

        stdout.Should().Contain("VersionInconsistency");
        File.Exists(Path.Combine(_testDirectory, "Directory.Packages.props"))
            .Should()
            .BeFalse("nothing may be written");
    }

    [Fact]
    public async Task Interactive_DoesNotPreemptUpdatePackages()
    {
        // Precedence: --update-packages sits above --interactive in the table, so passing both runs
        // the update rather than opening the wizard. Asserted because it was previously implicit.
        var console = new FakeConsoleService();

        var exitCode = await ProgramRunner.RunAsync(
            new[] { "--update-packages", "--interactive", "--dry-run", "-s", _testDirectory },
            console
        );

        exitCode.Should().NotBe(ExitCodes.Success, "there is no props file to update");
        console
            .OutputMessages.Should()
            .NotContain(m => m.Contains("Mission Control"), "the wizard must not have opened");
    }

    [Fact]
    public async Task NoModeFlags_FallsThroughToTheDefaultAction()
    {
        // The table must not swallow the ordinary path.
        CreateProject("Api.csproj", "13.0.1");
        CreateSolution("Test.sln", "Api.csproj");
        var console = new FakeConsoleService();

        var exitCode = await ProgramRunner.RunAsync(
            new[] { "--dry-run", "--quiet", "-s", _testDirectory },
            console
        );

        exitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task Doctor_RunsBeforeTheDefaultMigration()
    {
        var console = new FakeConsoleService();

        var exitCode = await ProgramRunner.RunAsync(
            new[] { "--doctor", "-s", _testDirectory },
            console
        );

        exitCode.Should().BeOneOf(ExitCodes.Success, ExitCodes.UnexpectedError);
        File.Exists(Path.Combine(_testDirectory, "Directory.Packages.props"))
            .Should()
            .BeFalse("doctor must not write any files");
    }

    [Fact]
    public async Task ReportingContractIsCheckedBeforeAnyModeRuns()
    {
        // The modes run instead of an analysis, so a contract enforced afterwards would be bypassed:
        // --update --output Sarif would perform a real self-update and emit nothing.
        var console = new FakeConsoleService();

        var exitCode = await ProgramRunner.RunAsync(
            new[] { "--update", "--analyze", "--output", "Sarif", "-s", _testDirectory },
            console
        );

        exitCode.Should().Be(ExitCodes.ValidationError);
        console.ErrorMessages.Should().Contain(m => m.Contains("Sarif"));
    }

    private void CreateProject(string name, string version)
    {
        File.WriteAllText(
            Path.Combine(_testDirectory, name),
            $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""{version}"" />
  </ItemGroup>
</Project>"
        );
    }

    private void CreateSolution(string name, params string[] projectNames)
    {
        var content = "Microsoft Visual Studio Solution File, Format Version 12.00\n";
        foreach (var projectFile in projectNames)
        {
            var guid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            var projectName = Path.GetFileNameWithoutExtension(projectFile);
            content +=
                $@"Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}"") = ""{projectName}"", ""{projectFile}"", ""{guid}""
EndProject
";
        }

        File.WriteAllText(Path.Combine(_testDirectory, name), content);
    }

    private static async Task<string> CaptureStdoutAsync(Func<Task<int>> action)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            await action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return writer.ToString();
    }
}
