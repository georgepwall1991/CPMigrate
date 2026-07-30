using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// What <c>--fix</c> says when it could not do what it was asked.
///
/// Both fixers that edit project XML used to swallow every exception and return null, which the caller could
/// only read as "there was nothing to change". So <c>--fix</c> on a read-only project file printed
/// <em>"No changes were needed"</em> — directly above <em>"1 issue(s) could not be fixed automatically"</em>,
/// two lines contradicting each other with the reassuring one first — and threw away the actual cause.
///
/// The exit code was already right: the post-fix rescan finds the unrepaired issue and gates on it, so
/// nothing shipped broken. What was wrong was everything a human reads. Someone running this interactively
/// was told the finding needed no action, and given no hint that a permission problem existed.
/// </summary>
[Collection("Sequential")]
public class FixFailureReportingTests : IDisposable
{
    private readonly string _root;

    public FixFailureReportingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CPMigrateFixFail_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "src", "Api"));
    }

    public void Dispose()
    {
        var project = ProjectPath;
        if (File.Exists(project))
        {
            // Restore write permission, or the directory cannot be removed.
            File.SetAttributes(project, FileAttributes.Normal);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string ProjectPath => Path.Combine(_root, "src", "Api", "Api.csproj");

    [Fact]
    public async Task AFixThatCannotWrite_SaysWhy_AndDoesNotClaimNothingWasNeeded()
    {
        WriteDuplicateReference();
        MakeReadOnly();
        var console = new FakeConsoleService();

        var exitCode = await ProgramRunner.RunAsync(
            ["--analyze", "--fix", "--no-backup", "--quiet", "-s", _root],
            console
        );

        var everything = console.OutputMessages.Concat(console.ErrorMessages).ToList();

        everything
            .Should()
            .NotContain(
                message => message.Contains("No changes were needed"),
                "changes were needed; they could not be made"
            );
        everything
            .Should()
            .Contain(
                message => message.Contains("could not be fixed automatically"),
                "the run has to say the finding is still there"
            );
        everything
            .Should()
            .Contain(
                message => message.Contains("Api.csproj"),
                "the file that could not be written is the actionable part"
            );

        exitCode
            .Should()
            .Be(
                ExitCodes.AnalysisIssuesFound,
                "the finding survives, so the gate must still close on it"
            );
    }

    [Fact]
    public async Task AFixThatCannotWrite_LeavesTheFileExactlyAsItWas()
    {
        // A failed write must not be a partial write.
        WriteDuplicateReference();
        var before = await File.ReadAllTextAsync(ProjectPath);
        MakeReadOnly();

        await ProgramRunner.RunAsync(
            ["--analyze", "--fix", "--no-backup", "--quiet", "-s", _root],
            new FakeConsoleService()
        );

        (await File.ReadAllTextAsync(ProjectPath)).Should().Be(before);
    }

    [Fact]
    public async Task AFixThatSucceeds_StillReportsSuccessAndSaysNothingAboutFailures()
    {
        // The other half of the contract: the new failure reporting must not fire on a clean run.
        WriteDuplicateReference();
        var console = new FakeConsoleService();

        var exitCode = await ProgramRunner.RunAsync(
            ["--analyze", "--fix", "--no-backup", "--quiet", "-s", _root],
            console
        );

        var everything = console.OutputMessages.Concat(console.ErrorMessages).ToList();

        exitCode.Should().Be(ExitCodes.Success);
        everything.Should().Contain(message => message.Contains("fix(es) affecting"));
        everything.Should().NotContain(message => message.Contains("could not be fixed"));
        (await File.ReadAllTextAsync(ProjectPath))
            .Split("<PackageReference")
            .Length.Should()
            .Be(
                2,
                "one reference left, so one <PackageReference occurrence plus the leading segment"
            );
    }

    [Fact]
    public async Task AFixThatChangesOneFileButNotAnother_IsNotReportedAsFixed()
    {
        // Cross-review caught this: an issue spanning several projects where one write succeeds and another
        // fails returned Succeeded, so the issue counted as fixed, GetFailedFixes omitted it, and the
        // summary said nothing about the half that is still broken.
        WriteProject("""<PackageReference Include="Serilog" Version="1.0.0" />""");
        var second = Path.Combine(_root, "src", "Api", "Worker.csproj");
        File.WriteAllText(
            second,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Serilog" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """
        );
        File.WriteAllText(
            Path.Combine(_root, "App.sln"),
            "Microsoft Visual Studio Solution File, Format Version 12.00\n"
                + "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Api\", "
                + "\"src\\Api\\Api.csproj\", \"{11111111-2222-3333-4444-555555555555}\"\nEndProject\n"
                + "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Worker\", "
                + "\"src\\Api\\Worker.csproj\", \"{22222222-2222-3333-4444-555555555555}\"\nEndProject\n"
        );

        // Api holds the version that needs raising to 2.0.0, and it is the one that cannot be written.
        MakeReadOnly();
        var console = new FakeConsoleService();

        await ProgramRunner.RunAsync(
            ["--analyze", "--fix", "--no-backup", "--quiet", "-s", _root],
            console
        );

        var everything = console.OutputMessages.Concat(console.ErrorMessages).ToList();

        everything
            .Should()
            .Contain(
                message => message.Contains("could not be fixed automatically"),
                "the part that failed has to be reported even though another part succeeded"
            );
        everything.Should().NotContain(message => message.Contains("No changes were needed"));

        // Cross-review round two: the partial result must not also be rendered as a success, and the
        // summary must not claim zero fixes applied while a file was demonstrably modified.
        everything
            .Should()
            .NotContain(
                message => message.Contains("Fixed:") && !message.Contains("Partially"),
                "a green \"Fixed\" line directly under the error explaining the failure reads as a lie"
            );
        everything
            .Should()
            .NotContain(
                message => message.Contains("Applied 0 fix(es)"),
                "a file was changed, so zero is the wrong count"
            );
    }

    [Fact]
    public async Task AFindingWithNoFixer_StillSaysNoChangesWereNeeded()
    {
        // The message is not removed, only made conditional — it is the right thing to say when it is true.
        // Reaching it needs a fix pass that attempted something and changed nothing, which is what an
        // unfixable finding produces: FrameworkAlignment has no fixer, so nothing is written and nothing
        // failed.
        WriteProject("""<PackageReference Include="Serilog" Version="4.3.0" />""");
        File.WriteAllText(
            Path.Combine(_root, "src", "Api", "Legacy.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.3.0" />
              </ItemGroup>
            </Project>
            """
        );
        File.WriteAllText(
            Path.Combine(_root, "App.sln"),
            "Microsoft Visual Studio Solution File, Format Version 12.00\n"
                + "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Api\", "
                + "\"src\\Api\\Api.csproj\", \"{11111111-2222-3333-4444-555555555555}\"\nEndProject\n"
                + "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Legacy\", "
                + "\"src\\Api\\Legacy.csproj\", \"{22222222-2222-3333-4444-555555555555}\"\nEndProject\n"
        );
        var console = new FakeConsoleService();

        await ProgramRunner.RunAsync(
            ["--analyze", "--fix", "--no-backup", "--quiet", "-s", _root],
            console
        );

        console
            .OutputMessages.Concat(console.ErrorMessages)
            .Should()
            .Contain(message => message.Contains("No changes were needed"));
    }

    private void WriteDuplicateReference()
    {
        WriteProject(
            """
                <PackageReference Include="Newtonsoft.Json" Version="13.0.1" />
                    <PackageReference Include="Newtonsoft.Json" Version="13.0.1" />
            """
        );
    }

    private void WriteProject(string references)
    {
        File.WriteAllText(
            ProjectPath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                {references}
              </ItemGroup>
            </Project>
            """
        );

        File.WriteAllText(
            Path.Combine(_root, "App.sln"),
            "Microsoft Visual Studio Solution File, Format Version 12.00\n"
                + "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Api\", "
                + "\"src\\Api\\Api.csproj\", \"{11111111-2222-3333-4444-555555555555}\"\nEndProject\n"
        );
    }

    private void MakeReadOnly()
    {
        File.SetAttributes(ProjectPath, FileAttributes.ReadOnly);
    }
}
