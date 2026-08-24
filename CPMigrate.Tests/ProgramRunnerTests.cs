using System.Text.Json;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests;

[Collection("Sequential")]
public class ProgramRunnerTests
{
    [Fact]
    public async Task RunAsync_NoArgs_StartsInteractiveMode()
    {
        // Arrange
        var fakeConsole = new FakeConsoleService();
        // Setup responses to exit interactive mode immediately
        fakeConsole.SelectionResponses = new Queue<string>(new[] { "Exit" });

        // Act
        var exitCode = await ProgramRunner.RunAsync(Array.Empty<string>(), fakeConsole);

        // Assert
        exitCode.Should().Be(ExitCodes.Success);
    }

    [Theory]
    [InlineData("Json")]
    [InlineData("Sarif")]
    public async Task RunAsync_VerboseWithMachineOutput_SuppressesTheLogNotice(string format)
    {
        // The notice is written before the payload, so leaking it puts prose ahead of the
        // opening brace and stops the document parsing as JSON or SARIF at all.
        var fakeConsole = new FakeConsoleService();
        var directory = Path.Combine(Path.GetTempPath(), $"CPMigrateVerbose_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            await ProgramRunner.RunAsync(
                new[] { "--analyze", "--verbose", "--quiet", "--output", format, "-s", directory },
                fakeConsole
            );

            fakeConsole
                .OutputMessages.Should()
                .NotContain(m => m.Contains("Verbose logging enabled"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("analyze")]
    [InlineData("audit")]
    [InlineData("licenses")]
    [InlineData("update")]
    public async Task RunAsync_LeadingVerb_IsRejectedInsteadOfRunningAMigration(string verb)
    {
        // CPMigrate has no sub-commands, so CommandLineParser used to discard the bare word and
        // fall through to the default action — a real, file-rewriting migration. A read-only
        // intent must never silently become a write.
        var fakeConsole = new FakeConsoleService();

        var exitCode = await ProgramRunner.RunAsync(new[] { verb, "-s", "." }, fakeConsole);

        exitCode.Should().Be(ExitCodes.ValidationError);
        fakeConsole.ErrorMessages.Should().ContainSingle(m => m.Contains($"'{verb}'"));
    }

    [Fact]
    public async Task RunAsync_LeadingVerb_SuggestsTheEquivalentFlag()
    {
        var fakeConsole = new FakeConsoleService();

        await ProgramRunner.RunAsync(new[] { "analyze", "-s", "." }, fakeConsole);

        fakeConsole.OutputMessages.Should().Contain(m => m.Contains("--analyze"));
    }

    [Fact]
    public async Task RunAsync_ValuelessWhy_IsRejectedInsteadOfRunningAMigration()
    {
        // A valueless --why parses as "flag not set", and an unset --why means the default action —
        // a real, file-rewriting migration. A read-only intent must never silently become a write.
        var fakeConsole = new FakeConsoleService();

        var exitCode = await ProgramRunner.RunAsync(["--why"], fakeConsole);

        exitCode.Should().Be(ExitCodes.ValidationError);
        fakeConsole.ErrorMessages.Should().Contain(m => m.Contains("--why"));
    }

    [Fact]
    public async Task RunAsync_WhyWithSarifOutput_IsRejectedInsteadOfTracing()
    {
        // SARIF carries analyzer findings, and --why produces none. The diagnostic modes return
        // before CommandRouter validates, so the rejection must happen in this file — otherwise the
        // scan would run and a terminal tree would follow a caller's explicit request for SARIF.
        var fakeConsole = new FakeConsoleService();

        var exitCode = await ProgramRunner.RunAsync(
            new[] { "--why", "Newtonsoft.Json", "--output", "Sarif", "-s", "." },
            fakeConsole
        );

        exitCode.Should().Be(ExitCodes.ValidationError);
        fakeConsole.ErrorMessages.Should().Contain(m => m.Contains("--output Sarif"));
    }

    [Fact]
    public async Task RunAsync_AmbiguousVerb_SuggestsBothCandidates()
    {
        // "update" could mean --update (update CPMigrate itself) or --update-packages (update the
        // solution's NuGet references). Offer both rather than guessing which one was meant.
        var fakeConsole = new FakeConsoleService();

        await ProgramRunner.RunAsync(new[] { "update" }, fakeConsole);

        fakeConsole.OutputMessages.Should().Contain(m => m.Contains("--update-packages"));
        fakeConsole.OutputMessages.Should().Contain(m => m.EndsWith("--update"));
    }

    [Fact]
    public async Task RunAsync_HelpArg_ReturnsSuccess()
    {
        // Arrange
        var fakeConsole = new FakeConsoleService();

        // Act
        var exitCode = await ProgramRunner.RunAsync(new[] { "--help" }, fakeConsole);

        // Assert
        // Parser.Default.ParseArguments returns 0 for help usually, but let's check what ExitCodes.ValidationError is
        // Actually help doesn't map to MapResult second param usually if it's handled by Parser
        // But CommandLineParser might exit or return 0.
        exitCode.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_InvalidArgs_ReturnsValidationError()
    {
        // Arrange
        var fakeConsole = new FakeConsoleService();

        // Act
        var exitCode = await ProgramRunner.RunAsync(new[] { "--invalid-arg" }, fakeConsole);

        // Assert
        exitCode.Should().Be(ExitCodes.ValidationError);
    }

    [Fact]
    public async Task RunAsync_ValidArgs_CallsRouteCommand()
    {
        // Arrange
        var fakeConsole = new FakeConsoleService();
        // Use a non-existent path to get an error we can recognize
        var args = new[] { "-s", "non_existent_folder" };

        // Act
        var exitCode = await ProgramRunner.RunAsync(args, fakeConsole);

        // Assert
        // It should reach RouteCommand, which calls RunMigrationAsync, which calls DiscoverProjects, which fails
        exitCode.Should().Be(ExitCodes.NoProjectsFound);
    }

    [Fact]
    public async Task DetermineStartDirectory_UsesBatchDirIfSpecified()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "Test.sln"), "");

        try
        {
            var fakeConsole = new FakeConsoleService();
            var args = new[] { "--batch", tempDir, "-o", "output" };

            // Act
            var exitCode = await ProgramRunner.RunAsync(args, fakeConsole);

            // Assert
            exitCode.Should().BeOneOf(ExitCodes.Success, ExitCodes.AnalysisIssuesFound);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task DetermineStartDirectory_UsesProjectDirIfSpecified()
    {
        // Arrange
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempPath);

        try
        {
            var fakeConsole = new FakeConsoleService();
            // Point to a directory that exists but has no projects
            var args = new[] { "-p", tempPath };

            // Act
            var exitCode = await ProgramRunner.RunAsync(args, fakeConsole);

            // Assert
            if (exitCode != ExitCodes.NoProjectsFound)
            {
                var errors = string.Join("\n", fakeConsole.ErrorMessages);
                throw new Exception(
                    $"Expected NoProjectsFound (4), but got {exitCode}. Errors: {errors}"
                );
            }
            exitCode.Should().Be(ExitCodes.NoProjectsFound);
        }
        finally
        {
            Directory.Delete(tempPath, true);
        }
    }

    [Fact]
    public async Task MergeConfigWithCliArgsAsync_HandlesExistingConfig()
    {
        // Arrange
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempPath);
        var configPath = Path.Combine(tempPath, ".cpmigrate.json");
        File.WriteAllText(configPath, "{\"backup\": false}");

        var fakeConsole = new FakeConsoleService();
        var args = new[] { "-s", tempPath };

        try
        {
            // Act
            var exitCode = await ProgramRunner.RunAsync(args, fakeConsole);

            // Assert
            exitCode.Should().Be(ExitCodes.NoProjectsFound);
            fakeConsole.OutputMessages.Count(m => m.Contains("Loaded config from:")).Should().Be(1);
        }
        finally
        {
            Directory.Delete(tempPath, true);
        }
    }

    [Fact]
    public async Task DetermineStartDirectory_UsesCurrentDirIfNoDirSpecified()
    {
        // Arrange
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempPath);
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(tempPath);

        try
        {
            var fakeConsole = new FakeConsoleService();
            // Use an option that doesn't imply a directory, like -d (dry run)
            var args = new[] { "-d" };

            // Act
            var exitCode = await ProgramRunner.RunAsync(args, fakeConsole);

            // Assert
            exitCode.Should().Be(ExitCodes.NoProjectsFound);
        }
        finally
        {
            Directory.SetCurrentDirectory(oldDir);
            Directory.Delete(tempPath, true);
        }
    }

    [Fact]
    public async Task DetermineStartDirectory_SkipsDotSolutionDir()
    {
        // Arrange
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempPath);

        var fakeConsole = new FakeConsoleService();
        // Set solution dir to "." explicitly, but pass an actual directory to RunAsync
        var args = new[] { "-s", tempPath };

        // Act
        var exitCode = await ProgramRunner.RunAsync(args, fakeConsole);

        // Assert
        exitCode.Should().Be(ExitCodes.NoProjectsFound);
        Directory.Delete(tempPath, true);
    }

    [Fact]
    public async Task RunAsync_WithNullConsole_UsesDefaultConsole()
    {
        // Act
        var exitCode = await ProgramRunner.RunAsync(new[] { "--help" }, null);

        // Assert
        exitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task DetermineStartDirectory_HandlesEmptySolutionDir()
    {
        // Arrange
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempPath);
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(tempPath);

        try
        {
            var fakeConsole = new FakeConsoleService();
            var args = new[] { "-s", "" }; // Empty string should fallback to "."

            // Act
            var exitCode = await ProgramRunner.RunAsync(args, fakeConsole);

            // Assert
            exitCode.Should().Be(ExitCodes.NoProjectsFound);
        }
        finally
        {
            Directory.SetCurrentDirectory(oldDir);
            Directory.Delete(tempPath, true);
        }
    }

    [Fact]
    public async Task MergeConfigWithCliArgsAsync_CliOutputOverridesConfigOutput()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempPath);
        File.WriteAllText(Path.Combine(tempPath, ".cpmigrate.json"), "{\"outputFormat\":\"Json\"}");

        var fakeConsole = new FakeConsoleService();
        var stdout = new StringWriter();
        var originalOut = Console.Out;

        try
        {
            Console.SetOut(stdout);

            var exitCode = await ProgramRunner.RunAsync(
                new[] { "-s", tempPath, "--output", "Terminal" },
                fakeConsole
            );

            exitCode.Should().Be(ExitCodes.NoProjectsFound);
            stdout.ToString().Should().BeEmpty();
            fakeConsole.OutputMessages.Count(m => m.Contains("Loaded config from:")).Should().Be(1);
        }
        finally
        {
            Console.SetOut(originalOut);
            stdout.Dispose();
            Directory.Delete(tempPath, true);
        }
    }

    [Fact]
    public async Task RunAsync_ConfigDrivenJsonOutput_ProducesJsonOnlyStdout()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempPath);
        File.WriteAllText(Path.Combine(tempPath, ".cpmigrate.json"), "{\"outputFormat\":\"Json\"}");

        var fakeConsole = new FakeConsoleService();
        var stdout = new StringWriter();
        var originalOut = Console.Out;

        try
        {
            Console.SetOut(stdout);

            var exitCode = await ProgramRunner.RunAsync(
                new[] { "-s", tempPath, "--quiet" },
                fakeConsole
            );

            exitCode.Should().Be(ExitCodes.NoProjectsFound);
            fakeConsole.OutputMessages.Should().NotContain(m => m.Contains("Loaded config from:"));

            var payload = stdout.ToString();
            payload.Should().NotBeNullOrWhiteSpace();
            using var document = JsonDocument.Parse(payload);
            document
                .RootElement.GetProperty("exitCode")
                .GetInt32()
                .Should()
                .Be(ExitCodes.NoProjectsFound);
        }
        finally
        {
            Console.SetOut(originalOut);
            stdout.Dispose();
            Directory.Delete(tempPath, true);
        }
    }

    [Fact]
    public async Task RunAsync_JsonOutputFile_DoesNotWriteToStdout()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempPath);
        File.WriteAllText(Path.Combine(tempPath, ".cpmigrate.json"), "{\"outputFormat\":\"Json\"}");

        var outputFile = Path.Combine(tempPath, "result.json");
        var fakeConsole = new FakeConsoleService();
        var stdout = new StringWriter();
        var originalOut = Console.Out;

        try
        {
            Console.SetOut(stdout);

            var exitCode = await ProgramRunner.RunAsync(
                new[] { "-s", tempPath, "--quiet", "--output-file", outputFile },
                fakeConsole
            );

            exitCode.Should().Be(ExitCodes.NoProjectsFound);
            stdout.ToString().Should().BeEmpty();
            fakeConsole.OutputMessages.Should().NotContain(m => m.Contains("Loaded config from:"));

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputFile));
            document
                .RootElement.GetProperty("exitCode")
                .GetInt32()
                .Should()
                .Be(ExitCodes.NoProjectsFound);
        }
        finally
        {
            Console.SetOut(originalOut);
            stdout.Dispose();
            Directory.Delete(tempPath, true);
        }
    }

    [Fact]
    public void Examples_ProjectExample_DoesNotCarryDefaultSolutionPath()
    {
        var projectExample = Options.Examples.Single(example =>
            example.HelpText == "Convert only one project"
        );
        var sample = projectExample.Sample.Should().BeOfType<Options>().Subject;

        sample.ProjectFileDir.Should().Be(Path.Combine("path", "to", "project.csproj"));
        sample.SolutionFileDir.Should().BeEmpty();
    }
}
