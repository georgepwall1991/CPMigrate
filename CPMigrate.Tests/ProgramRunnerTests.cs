using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using System.Text.Json;

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
                throw new Exception($"Expected NoProjectsFound (4), but got {exitCode}. Errors: {errors}");
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
        File.WriteAllText(Path.Combine(tempPath, ".cpmigrate.json"), "{\"outputFormat\":1}");

        var fakeConsole = new FakeConsoleService();
        var stdout = new StringWriter();
        var originalOut = Console.Out;

        try
        {
            Console.SetOut(stdout);

            var exitCode = await ProgramRunner.RunAsync(
                new[] { "-s", tempPath, "--output", "Terminal" },
                fakeConsole);

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
        File.WriteAllText(Path.Combine(tempPath, ".cpmigrate.json"), "{\"outputFormat\":1}");

        var fakeConsole = new FakeConsoleService();
        var stdout = new StringWriter();
        var originalOut = Console.Out;

        try
        {
            Console.SetOut(stdout);

            var exitCode = await ProgramRunner.RunAsync(
                new[] { "-s", tempPath, "--quiet" },
                fakeConsole);

            exitCode.Should().Be(ExitCodes.NoProjectsFound);
            fakeConsole.OutputMessages.Should().NotContain(m => m.Contains("Loaded config from:"));

            var payload = stdout.ToString();
            payload.Should().NotBeNullOrWhiteSpace();
            using var document = JsonDocument.Parse(payload);
            document.RootElement.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.NoProjectsFound);
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
        File.WriteAllText(Path.Combine(tempPath, ".cpmigrate.json"), "{\"outputFormat\":1}");

        var outputFile = Path.Combine(tempPath, "result.json");
        var fakeConsole = new FakeConsoleService();
        var stdout = new StringWriter();
        var originalOut = Console.Out;

        try
        {
            Console.SetOut(stdout);

            var exitCode = await ProgramRunner.RunAsync(
                new[] { "-s", tempPath, "--quiet", "--output-file", outputFile },
                fakeConsole);

            exitCode.Should().Be(ExitCodes.NoProjectsFound);
            stdout.ToString().Should().BeEmpty();
            fakeConsole.OutputMessages.Should().NotContain(m => m.Contains("Loaded config from:"));

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputFile));
            document.RootElement.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.NoProjectsFound);
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
        var projectExample = Options.Examples.Single(example => example.HelpText == "Convert only one project");
        var sample = projectExample.Sample.Should().BeOfType<Options>().Subject;

        sample.ProjectFileDir.Should().Be(Path.Combine("path", "to", "project.csproj"));
        sample.SolutionFileDir.Should().BeEmpty();
    }
}
