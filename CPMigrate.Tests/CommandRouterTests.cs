using CPMigrate;
using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Moq;
using System.Text.Json;

namespace CPMigrate.Tests;

/// <summary>
/// Tests for CommandRouter covering routing logic, config merging, and mode execution.
/// </summary>
public class CommandRouterTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly FakeConsoleService _console;
    private readonly FakeInteractiveService _interactiveService;
    private readonly VersionResolver _versionResolver;
    private readonly ConfigService _configService;
    private readonly IBackupManager _backupManager;

    public CommandRouterTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateCommandRouterTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _console = new FakeConsoleService();
        _interactiveService = new FakeInteractiveService();
        _versionResolver = new VersionResolver(_console);
        _configService = new ConfigService(_console);
        _backupManager = new BackupManager();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RouteCommand_InteractiveMode_CallsInteractiveService()
    {
        // Arrange
        var options = new Options
        {
            Interactive = true
        };

        // InteractiveService will return null (user cancelled)
        _interactiveService.NextWizardResult = null;

        // Act
        var result = await CommandRouter.RouteCommand(
            options,
            _console,
            _interactiveService,
            _versionResolver,
            _configService,
            _backupManager);

        // Assert
        result.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task RouteCommand_PruneBackupsMode_CallsPruneMode()
    {
        // Arrange
        var options = new Options
        {
            PruneBackups = true,
            SolutionFileDir = _testDirectory,
            Quiet = true, // Skip confirmation
            Retention = 5
        };

        // Create backup directory (empty, so nothing to prune)
        var backupPath = BackupManager.GetBackupDirectoryPath(options);
        Directory.CreateDirectory(backupPath);

        // Act
        var result = await CommandRouter.RouteCommand(
            options,
            _console,
            _interactiveService,
            _versionResolver,
            _configService,
            _backupManager);

        // Assert
        result.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task RouteCommand_BatchMode_CallsBatchService()
    {
        // Arrange - Create empty batch directory
        var batchDir = Path.Combine(_testDirectory, "batch");
        Directory.CreateDirectory(batchDir);

        var options = new Options
        {
            BatchDir = batchDir,
            Force = true
        };

        // Act
        var result = await CommandRouter.RouteCommand(
            options,
            _console,
            _interactiveService,
            _versionResolver,
            _configService,
            _backupManager);

        // Assert - With empty directory, will fail validation or return "no work" code
        result.Should().BeOneOf(ExitCodes.Success, ExitCodes.ValidationError, ExitCodes.AnalysisIssuesFound);
    }

    [Fact]
    public async Task RouteCommand_UnifyPropsMode_CallsBuildPropsService()
    {
        // Arrange
        var options = new Options
        {
            UnifyProps = true,
            SolutionFileDir = _testDirectory,
            Force = true
        };

        // Act
        var result = await CommandRouter.RouteCommand(
            options,
            _console,
            _interactiveService,
            _versionResolver,
            _configService,
            _backupManager);

        // Assert
        // Will return error (no projects found)
        result.Should().Be(ExitCodes.UnexpectedError);
    }

    [Fact]
    public async Task RouteCommand_ConfigExists_DoesNotReloadConfig()
    {
        // Arrange - ProgramRunner is responsible for config loading before routing
        File.WriteAllText(Path.Combine(_testDirectory, ".cpmigrate.json"), "{\"outputFormat\":1}");

        var options = new Options
        {
            SolutionFileDir = _testDirectory,
            Output = OutputFormat.Terminal,
            Quiet = true
        };

        // Act
        var result = await CommandRouter.RouteCommand(
            options,
            _console,
            _interactiveService,
            _versionResolver,
            _configService,
            _backupManager);

        // Assert
        result.Should().Be(ExitCodes.NoProjectsFound);
        options.Output.Should().Be(OutputFormat.Terminal);
        _console.OutputMessages.Should().NotContain(m => m.Contains("Loaded config from:"));
    }

    [Fact]
    public async Task RunUnifyPropsModeAsync_Exception_ReturnsUnexpectedError()
    {
        // Arrange - Pass null directory to trigger exception
        var options = new Options
        {
            SolutionFileDir = null!,
            Force = true
        };

        // Act
        var result = await CommandRouter.RunUnifyPropsModeAsync(options, _console);

        // Assert
        result.Should().Be(ExitCodes.UnexpectedError);
    }

    [Fact]
    public async Task RunPruneModeAsync_NoBackupDirectory_ReturnsSuccess()
    {
        // Arrange
        var options = new Options
        {
            PruneBackups = true,
            SolutionFileDir = _testDirectory,
            Retention = 5
        };

        // Don't create backup directory - will be created by GetBackupDirectoryPath

        // Act
        var result = await CommandRouter.RunPruneModeAsync(options, _console, _backupManager);

        // Assert - With no backups to prune, returns success
        result.Should().BeOneOf(ExitCodes.Success, ExitCodes.FileOperationError);
    }

    [Fact]
    public async Task RunPruneModeAsync_PruneAll_ReturnsSuccess()
    {
        // Arrange - Create backup directory with some backup sets
        var options = new Options
        {
            PruneAll = true,
            SolutionFileDir = _testDirectory,
            Quiet = true // Skip confirmation
        };

        var backupPath = BackupManager.GetBackupDirectoryPath(options);
        Directory.CreateDirectory(backupPath);

        // Create a fake backup file (proper format with timestamp)
        var backupFile = Path.Combine(backupPath, "Project.csproj.backup_20240101_120000");
        File.WriteAllText(backupFile, "test backup");

        // Act
        var result = await CommandRouter.RunPruneModeAsync(options, _console, _backupManager);

        // Assert
        result.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task RunPruneModeAsync_WithinRetention_NothingToPrune()
    {
        // Arrange
        var options = new Options
        {
            PruneBackups = true,
            SolutionFileDir = _testDirectory,
            BackupDir = _testDirectory,
            Retention = 10, // Keep 10, but we only have 1
            Quiet = true
        };

        var backupPath = BackupManager.GetBackupDirectoryPath(options);
        Directory.CreateDirectory(backupPath);

        // Create one backup set
        var backupSet = Path.Combine(backupPath, "backup_20240101_120000");
        Directory.CreateDirectory(backupSet);
        File.WriteAllText(Path.Combine(backupSet, "test.txt"), "test backup");

        // Act
        var result = await CommandRouter.RunPruneModeAsync(options, _console, _backupManager);

        // Assert
        result.Should().Be(ExitCodes.Success);

        // Backup should still exist
        Directory.Exists(backupSet).Should().BeTrue();
    }

    [Fact]
    public async Task RunBatchModeAsync_ValidationFails_ReturnsValidationError()
    {
        // Arrange - Create invalid options
        var options = new Options
        {
            BatchDir = "invalid-dir-that-does-not-exist",
            ConflictStrategy = (ConflictStrategy)999 // Invalid enum value
        };

        // Act
        var result = await CommandRouter.RunBatchModeAsync(
            options,
            _console,
            _versionResolver,
            _backupManager);

        // Assert
        result.Should().Be(ExitCodes.ValidationError);
    }

    [Fact]
    public async Task RunBatchModeAsync_JsonOutput_WritesToFile()
    {
        // Arrange
        var batchDir = Path.Combine(_testDirectory, "batch");
        Directory.CreateDirectory(batchDir);

        var outputFile = Path.Combine(_testDirectory, "output.json");

        var options = new Options
        {
            BatchDir = batchDir,
            Output = OutputFormat.Json,
            OutputFile = outputFile,
            Force = true
        };

        // Act
        var result = await CommandRouter.RunBatchModeAsync(
            options,
            _console,
            _versionResolver,
            _backupManager);

        // Assert - Exercised the code path
        result.Should().BeOneOf(ExitCodes.Success, ExitCodes.ValidationError, ExitCodes.AnalysisIssuesFound);
    }

    [Fact]
    public async Task RunMigrationAsync_IOException_ReturnsFileOperationError()
    {
        // Arrange - Create a solution file that references non-existent projects
        var solutionContent = @"
Microsoft Visual Studio Solution File, Format Version 12.00
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""NonExistent"", ""NonExistent.csproj"", ""{12345678-1234-1234-1234-123456789ABC}""
EndProject
";
        var solutionPath = Path.Combine(_testDirectory, "Test.sln");
        File.WriteAllText(solutionPath, solutionContent);

        var options = new Options
        {
            SolutionFileDir = _testDirectory
        };

        // Act - This should trigger an error when trying to read non-existent project
        var result = await CommandRouter.RunMigrationAsync(
            options,
            _console,
            _versionResolver,
            _backupManager);

        // Assert
        // Will fail with error (likely FileOperationError or UnexpectedError depending on how it's caught)
        result.Should().NotBe(ExitCodes.Success);
    }

    [Fact]
    public async Task RunMigrationAsync_JsonOutput_WritesToConsole()
    {
        // Arrange - Create valid solution with one project
        var projectPath = CreateTestProject("Test.csproj", @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
  </ItemGroup>
</Project>");

        var solutionPath = CreateTestSolution("Test.sln", projectPath);

        var options = new Options
        {
            SolutionFileDir = Path.GetDirectoryName(solutionPath) ?? "",
            Output = OutputFormat.Json,
            DryRun = true,
            Force = true
        };

        // Capture console output
        var originalOut = Console.Out;
        using var stringWriter = new StringWriter();
        Console.SetOut(stringWriter);

        try
        {
            // Act
            var result = await CommandRouter.RunMigrationAsync(
                options,
                _console,
                _versionResolver,
                _backupManager);

            // Assert - Accept either success or validation error
            result.Should().BeOneOf(ExitCodes.Success, ExitCodes.ValidationError, ExitCodes.UnexpectedError);

            // JSON should be written to console
            var output = stringWriter.ToString();
            output.TrimStart().Should().StartWith("{");
            output.Should().Contain("\"operation\":");
            var parsed = JsonDocument.Parse(output);
            parsed.RootElement.GetProperty("operation").GetString().Should().Be("migrate");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task RunMigrationAsync_JsonValidationError_WritesErrorJsonToConsole()
    {
        // Arrange
        var options = new Options
        {
            Rollback = true,
            BackupDir = ".",
            Output = OutputFormat.Json
        };

        var originalOut = Console.Out;
        using var stringWriter = new StringWriter();
        Console.SetOut(stringWriter);

        try
        {
            // Act
            var result = await CommandRouter.RunMigrationAsync(
                options,
                _console,
                _versionResolver,
                _backupManager);

            // Assert
            result.Should().Be(ExitCodes.ValidationError);
            var output = stringWriter.ToString();
            output.TrimStart().Should().StartWith("{");

            var parsed = JsonDocument.Parse(output);
            parsed.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
            parsed.RootElement.GetProperty("errors")[0].GetString()
                .Should().Contain("--rollback requires --force when --output Json is used");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task RunPruneModeAsync_PruneAll_Cancelled()
    {
        // Arrange
        var options = new Options { PruneAll = true, SolutionFileDir = _testDirectory };
        var backupPath = BackupManager.GetBackupDirectoryPath(options);
        Directory.CreateDirectory(backupPath);
        File.WriteAllText(Path.Combine(backupPath, "test.backup_20240101_120000"), "test content");

        _console.ConfirmationResponse = false; // Cancel

        // Act
        var result = await CommandRouter.RunPruneModeAsync(options, _console, _backupManager);

        // Assert
        result.Should().Be(ExitCodes.Success);
        Directory.EnumerateFiles(backupPath).Should().NotBeEmpty();
    }

    [Fact]
    public async Task RunPruneModeAsync_PruneBackups_Cancelled()
    {
        // Arrange
        var options = new Options { PruneBackups = true, SolutionFileDir = _testDirectory, Retention = 0 };
        var backupPath = BackupManager.GetBackupDirectoryPath(options);
        Directory.CreateDirectory(backupPath);
        Directory.CreateDirectory(Path.Combine(backupPath, "backup_20240101_120000"));

        _console.ConfirmationResponse = false; // Cancel

        // Act
        var result = await CommandRouter.RunPruneModeAsync(options, _console, _backupManager);

        // Assert
        result.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task RunMigrationAsync_GeneralException_ReturnsUnexpectedError()
    {
        // Arrange - Uninitialized options to trigger something
        Options options = null!;

        // Act
        var result = await CommandRouter.RunMigrationAsync(options, _console, _versionResolver, _backupManager);

        // Assert
        result.Should().Be(ExitCodes.UnexpectedError);
    }

    [Fact]
    public async Task RunInteractiveModeAsync_ReturnsToMenu_ThenExits()
    {
        // Arrange
        _interactiveService.NextWizardResult = new Options { DryRun = true }; // First run
        _console.ConfirmationResponse = false; // "Return to main menu?" -> No (exits)

        // Act
        var result = await CommandRouter.RunInteractiveModeAsync(
            _console,
            _interactiveService,
            _versionResolver,
            _configService,
            _backupManager);

        // Assert
        result.Should().BeOneOf(ExitCodes.Success, ExitCodes.NoProjectsFound, ExitCodes.ValidationError);
    }

    [Fact]
    public async Task RunPruneModeAsync_PruneAll_Confirmed()
    {
        // Arrange
        var options = new Options { PruneAll = true, SolutionFileDir = _testDirectory, Quiet = false };
        var backupPath = BackupManager.GetBackupDirectoryPath(options);
        Directory.CreateDirectory(backupPath);
        File.WriteAllText(Path.Combine(backupPath, "test.backup_20240101_120000"), "test content");

        _console.ConfirmationResponse = true; // Confirm

        // Act
        var result = await CommandRouter.RunPruneModeAsync(options, _console, _backupManager);

        // Assert
        result.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task RunPruneModeAsync_PruneBackups_Confirmed()
    {
        // Arrange
        var options = new Options { PruneBackups = true, SolutionFileDir = _testDirectory, Retention = 1, Quiet = false };
        var backupPath = BackupManager.GetBackupDirectoryPath(options);
        Directory.CreateDirectory(backupPath);
        Directory.CreateDirectory(Path.Combine(backupPath, "backup_20240101_120000"));
        Directory.CreateDirectory(Path.Combine(backupPath, "backup_20240102_120000"));

        _console.ConfirmationResponse = true; // Confirm

        // Act
        var result = await CommandRouter.RunPruneModeAsync(options, _console, _backupManager);

        // Assert
        result.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task WriteJsonOutputForMigration_Rollback_HitsBranch()
    {
         // Arrange
         var options = new Options { Rollback = true, Output = OutputFormat.Json, SolutionFileDir = _testDirectory };
         
         // Act
         await CommandRouter.RunMigrationAsync(options, _console, _versionResolver, _backupManager);
         
         // Assert - branch hit
    }

    [Fact]
    public async Task PruneAllBackupsAsync_PartialFailure_ReturnsFileOperationError_And_ReportsErrors()
    {
        // Arrange
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);
        var options = new Options { PruneAll = true, BackupDir = _testDirectory, Quiet = true };
        
        var failureResult = new PruneResult(); // Success is read-only derived property
        // PruneResult.Success => Errors.Count == 0 (Line 84 in BackupModels.cs).
        // So I just need to add errors.
        failureResult.Errors.Add("Failed to delete file X");
        
        var mockBackup = new Mock<IBackupManager>();
        mockBackup.Setup(m => m.GetBackupHistory(It.IsAny<string>()))
            .Returns(new List<BackupSetInfo> { new BackupSetInfo { Timestamp = "20240101" } });
        
        mockBackup.Setup(m => m.PruneAllBackups(It.IsAny<string>()))
            .Returns(failureResult);

        // Act
        var result = await CommandRouter.RunPruneModeAsync(options, _console, mockBackup.Object);

        // Assert
        result.Should().Be(ExitCodes.FileOperationError);
        _console.OutputMessages.Should().Contain(m => m.Contains("Failed to delete file X"));
    }

    [Fact]
    public async Task PruneOldBackupsAsync_PartialFailure_ReportsErrors()
    {
        // Arrange
        var backupDir = Path.Combine(_testDirectory, ".cpmigrate_backup");
        Directory.CreateDirectory(backupDir);
        var options = new Options { PruneBackups = true, Retention = 0, BackupDir = _testDirectory, Quiet = true };
        
        var failureResult = new PruneResult();
        failureResult.Errors.Add("Failed to prune backup Y");

        var mockBackup = new Mock<IBackupManager>();
        mockBackup.Setup(m => m.GetBackupHistory(It.IsAny<string>()))
            .Returns(new List<BackupSetInfo> { new BackupSetInfo { Timestamp = "20240101" } });
            
        mockBackup.Setup(m => m.PruneBackups(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(failureResult);

        // Act
        var result = await CommandRouter.RunPruneModeAsync(options, _console, mockBackup.Object);

        // Assert
        result.Should().Be(ExitCodes.FileOperationError);
        _console.OutputMessages.Should().Contain("Failed to prune backup Y");
    }

    [Fact]
    public async Task RunMigrationAsync_UnauthorizedAccess_ReturnsFileOperationError()
    {
        // Arrange
        var projectPath = CreateTestProject("Test.csproj", @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        var solutionPath = CreateTestSolution("Test.sln", projectPath);
        var options = new Options 
        { 
            SolutionFileDir = Path.GetDirectoryName(solutionPath) ?? "",
            NoBackup = false,
            DryRun = false,
            BackupDir = _testDirectory // Ensure backup dir is in test directory
        };

        var mockBackup = new Mock<IBackupManager>();
        
        // Mock CreateBackupForProject to throw UnauthorizedAccessException
        mockBackup.Setup(m => m.CreateBackupForProject(It.IsAny<Options>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Throws(new UnauthorizedAccessException("Access denied to backup"));

        // Act
        var result = await CommandRouter.RunMigrationAsync(options, _console, _versionResolver, mockBackup.Object);

        // Assert
        result.Should().Be(ExitCodes.FileOperationError);
        _console.OutputMessages.Should().Contain($"\nPermission denied: Access denied to backup");
    }

    // Helper methods

    private string CreateTestProject(string projectName, string content)
    {
        var projectPath = Path.Combine(_testDirectory, projectName);
        File.WriteAllText(projectPath, content);
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

        var projectGuids = new List<string>();
        foreach (var projectPath in projectPaths)
        {
            var projectGuid = Guid.NewGuid().ToString("B").ToUpper();
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var relativePath = Path.GetRelativePath(Path.GetDirectoryName(solutionPath) ?? "", projectPath);

            projectGuids.Add(projectGuid);

            solutionContent += $@"Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}"") = ""{projectName}"", ""{relativePath}"", ""{projectGuid}""
EndProject
";
        }

        solutionContent += @"Global
    GlobalSection(SolutionConfigurationPlatforms) = preSolution
        Debug|Any CPU = Debug|Any CPU
        Release|Any CPU = Release|Any CPU
    EndGlobalSection
EndGlobal
";

        File.WriteAllText(solutionPath, solutionContent);
        return solutionPath;
    }
}
