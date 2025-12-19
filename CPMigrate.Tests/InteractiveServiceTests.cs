using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CPMigrate.Tests;

public class InteractiveServiceTests : IDisposable
{
    private readonly string _testDirectory;

    // Match the emoji-prefixed constants from InteractiveService
    private const string ModeMigrate = "🚀 Migrate to Central Package Management";
    private const string ModeAnalyze = "🔍 Analyze packages for issues";
    private const string ModeRollback = "↩️  Rollback a previous migration";
    private const string ModeExit = "❌ Exit";
    private const string ConflictHighest = "⬆️  Highest version (recommended)";
    private const string ConflictLowest = "⬇️  Lowest version";
    private const string ConflictFail = "⛔️ Fail on conflict";
    private const string EnterPathManually = "✏️  Enter path manually...";

    public InteractiveServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateInteractiveTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void RunWizard_MigrationMode_ReturnsCorrectOptions()
    {
        // Arrange
        // Note: When there are no .sln files in current directory, it goes directly to text prompt
        // so we don't need "Enter path manually..." in the selection queue
        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            ModeMigrate,
            // No selection for solution path when no .sln files exist - goes to text prompt
            ConflictHighest,
            "Yes (recommended)",  // backup
            "Yes",                // gitignore
            "Yes - preview changes without modifying files",
            "No - remove them (recommended for clean CPM)"
        });
        fakeConsole.TextResponses = new Queue<string>(new[]
        {
            _testDirectory,     // solution path (text prompt when no .sln)
            "./cpm-backup"      // backup directory
        });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole);

        // Act
        var options = service.RunWizard();

        // Assert
        options.Should().NotBeNull();
        options!.Analyze.Should().BeFalse();
        options.Rollback.Should().BeFalse();
        options.SolutionFileDir.Should().Be(_testDirectory);
        options.ConflictStrategy.Should().Be(ConflictStrategy.Highest);
        options.NoBackup.Should().BeFalse();
        options.DryRun.Should().BeTrue();
        options.KeepAttributes.Should().BeFalse();
    }

    [Fact]
    public void RunWizard_AnalyzeMode_ReturnsCorrectOptions()
    {
        // Arrange
        // No .sln files in test directory, so goes directly to text prompt
        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            ModeAnalyze
        });
        fakeConsole.TextResponses = new Queue<string>(new[] { _testDirectory });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole);

        // Act
        var options = service.RunWizard();

        // Assert
        options.Should().NotBeNull();
        options!.Analyze.Should().BeTrue();
        options.Rollback.Should().BeFalse();
        options.SolutionFileDir.Should().Be(_testDirectory);
    }

    [Fact]
    public void RunWizard_RollbackMode_ReturnsCorrectOptions()
    {
        // Arrange
        // No .sln files in test directory, so goes directly to text prompt
        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            ModeRollback
        });
        fakeConsole.TextResponses = new Queue<string>(new[]
        {
            _testDirectory,    // solution path
            "./cpm-backup"     // backup directory
        });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole);

        // Act
        var options = service.RunWizard();

        // Assert
        options.Should().NotBeNull();
        options!.Rollback.Should().BeTrue();
        options.Analyze.Should().BeFalse();
        options.BackupDir.Should().Be("./cpm-backup");
    }

    [Fact]
    public void RunWizard_UserCancelsAtConfirmation_ReturnsNull()
    {
        // Arrange
        // No .sln files in test directory, so goes directly to text prompt
        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            ModeAnalyze
        });
        fakeConsole.TextResponses = new Queue<string>(new[] { _testDirectory });
        fakeConsole.ConfirmationResponse = false; // User says No

        var service = new InteractiveService(fakeConsole);

        // Act
        var options = service.RunWizard();

        // Assert
        options.Should().BeNull();
    }

    [Fact]
    public void RunWizard_MigrationWithNoBackup_SetsNoBackupTrue()
    {
        // Arrange
        // No .sln files in test directory, so goes directly to text prompt
        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            ModeMigrate,
            ConflictHighest,
            "No",  // No backup
            "No - make changes immediately",
            "No - remove them (recommended for clean CPM)"
        });
        fakeConsole.TextResponses = new Queue<string>(new[] { _testDirectory });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole);

        // Act
        var options = service.RunWizard();

        // Assert
        options.Should().NotBeNull();
        options!.NoBackup.Should().BeTrue();
    }

    [Fact]
    public void RunWizard_MigrationWithLowestConflictStrategy_SetsStrategy()
    {
        // Arrange
        // No .sln files in test directory, so goes directly to text prompt
        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            ModeMigrate,
            ConflictLowest,
            "No",  // No backup
            "No - make changes immediately",
            "No - remove them (recommended for clean CPM)"
        });
        fakeConsole.TextResponses = new Queue<string>(new[] { _testDirectory });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole);

        // Act
        var options = service.RunWizard();

        // Assert
        options.Should().NotBeNull();
        options!.ConflictStrategy.Should().Be(ConflictStrategy.Lowest);
    }

    [Fact]
    public void RunWizard_MigrationWithFailConflictStrategy_SetsStrategy()
    {
        // Arrange
        // No .sln files in test directory, so goes directly to text prompt
        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            ModeMigrate,
            ConflictFail,
            "No",  // No backup
            "No - make changes immediately",
            "No - remove them (recommended for clean CPM)"
        });
        fakeConsole.TextResponses = new Queue<string>(new[] { _testDirectory });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole);

        // Act
        var options = service.RunWizard();

        // Assert
        options.Should().NotBeNull();
        options!.ConflictStrategy.Should().Be(ConflictStrategy.Fail);
    }

    [Fact]
    public void RunWizard_MigrationKeepAttributes_SetsKeepAttributesTrue()
    {
        // Arrange
        // No .sln files in test directory, so goes directly to text prompt
        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            ModeMigrate,
            ConflictHighest,
            "No",  // No backup
            "No - make changes immediately",
            "Yes - keep alongside CPM"
        });
        fakeConsole.TextResponses = new Queue<string>(new[] { _testDirectory });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole);

        // Act
        var options = service.RunWizard();

        // Assert
        options.Should().NotBeNull();
        options!.KeepAttributes.Should().BeTrue();
    }

    [Fact]
    public void RunWizard_MigrationWithExistingPropsAndMergeSelected_SetsMergeExistingTrue()
    {
        // Arrange
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, "<Project></Project>");

        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            ModeMigrate,
            ConflictHighest,
            "No",  // No backup
            "No - make changes immediately",
            "No - remove them (recommended for clean CPM)",
            "Merge into existing file"
        });
        fakeConsole.TextResponses = new Queue<string>(new[] { _testDirectory });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole);

        // Act
        var options = service.RunWizard();

        // Assert
        options.Should().NotBeNull();
        options!.MergeExisting.Should().BeTrue();
    }

    [Fact]
    public void RunWizard_MigrationWithExistingPropsAndFailSelected_LeavesMergeExistingFalse()
    {
        // Arrange
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, "<Project></Project>");

        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            ModeMigrate,
            ConflictHighest,
            "No",  // No backup
            "No - make changes immediately",
            "No - remove them (recommended for clean CPM)",
            "Fail (recommended)"
        });
        fakeConsole.TextResponses = new Queue<string>(new[] { _testDirectory });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole);

        // Act
        var options = service.RunWizard();

        // Assert
        options.Should().NotBeNull();
        options!.MergeExisting.Should().BeFalse();
    }

    [Fact]
    public void RunWizard_SolutionAutoDetect_FindsSolutionFile()
    {
        // Arrange - create a .sln file in test directory
        var slnPath = Path.Combine(_testDirectory, "TestSolution.sln");
        File.WriteAllText(slnPath, "Microsoft Visual Studio Solution File");

        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            ModeAnalyze,
            "TestSolution.sln"  // Select the found solution
        });
        fakeConsole.ConfirmationResponse = true;

        // Need to change current directory for the test
        var originalDir = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_testDirectory);
            var currentDir = Directory.GetCurrentDirectory();

            var service = new InteractiveService(fakeConsole);

            // Act
            var options = service.RunWizard();

            // Assert
            options.Should().NotBeNull();
            // Use GetCurrentDirectory() to handle symlink resolution on macOS
            options!.SolutionFileDir.Should().Be(currentDir);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
        }
    }

    [Fact]
    public void RunWizard_MigrationMode_SetsOutputDirToSolutionPath()
    {
        // Arrange - verify OutputDir is set correctly for Directory.Packages.props generation
        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            ModeMigrate,
            ConflictHighest,
            "No",  // No backup
            "Yes - preview changes without modifying files",
            "No - remove them (recommended for clean CPM)"
        });
        fakeConsole.TextResponses = new Queue<string>(new[] { _testDirectory });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole);

        // Act
        var options = service.RunWizard();

        // Assert
        options.Should().NotBeNull();
        options!.OutputDir.Should().Be(_testDirectory);
        options.SolutionFileDir.Should().Be(_testDirectory);
    }

    [Fact]
    public void RunWizard_AnalyzeMode_SetsOutputDirToSolutionPath()
    {
        // Arrange - verify OutputDir is set for analyze mode too
        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            ModeAnalyze
        });
        fakeConsole.TextResponses = new Queue<string>(new[] { _testDirectory });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole);

        // Act
        var options = service.RunWizard();

        // Assert
        options.Should().NotBeNull();
        options!.OutputDir.Should().Be(_testDirectory);
    }

    [Fact]
    public void RunWizard_RollbackMode_SetsOutputDirToSolutionPath()
    {
        // Arrange - verify OutputDir is set for rollback mode
        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            ModeRollback
        });
        fakeConsole.TextResponses = new Queue<string>(new[]
        {
            _testDirectory,    // solution path
            "./cpm-backup"     // backup directory
        });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole);

        // Act
        var options = service.RunWizard();

        // Assert
        options.Should().NotBeNull();
        options!.OutputDir.Should().Be(_testDirectory);
    }

    [Fact]
    public void RunWizard_MigrationWithBackupAndGitignore_SetsAllOptions()
    {
        // Arrange - full migration with backup and gitignore
        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            ModeMigrate,
            ConflictHighest,
            "Yes (recommended)",  // backup
            "Yes",                // add to gitignore
            "No - make changes immediately",  // not dry run
            "No - remove them (recommended for clean CPM)"
        });
        fakeConsole.TextResponses = new Queue<string>(new[]
        {
            _testDirectory,     // solution path
            "./my-backup"       // custom backup directory
        });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole);

        // Act
        var options = service.RunWizard();

        // Assert
        options.Should().NotBeNull();
        options!.NoBackup.Should().BeFalse();
        options.BackupDir.Should().Be("./my-backup");
        options.AddBackupToGitignore.Should().BeTrue();
        options.GitignoreDir.Should().Be(".");
        options.DryRun.Should().BeFalse();
    }

    [Fact]
    public void RunWizard_MigrationWithBackupNoGitignore_SetsGitignoreFalse()
    {
        // Arrange - backup but no gitignore
        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            ModeMigrate,
            ConflictHighest,
            "Yes (recommended)",  // backup
            "No",                 // don't add to gitignore
            "Yes - preview changes without modifying files",
            "No - remove them (recommended for clean CPM)"
        });
        fakeConsole.TextResponses = new Queue<string>(new[]
        {
            _testDirectory,
            "./cpm-backup"
        });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole);

        // Act
        var options = service.RunWizard();

        // Assert
        options.Should().NotBeNull();
        options!.NoBackup.Should().BeFalse();
        options.AddBackupToGitignore.Should().BeFalse();
    }

    [Fact]
    public void RunWizard_EmptySolutionPath_ReturnsNull()
    {
        // Arrange - empty/whitespace solution path should cancel
        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            ModeAnalyze
        });
        fakeConsole.TextResponses = new Queue<string>(new[] { "   " }); // whitespace only
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole);

        // Act
        var options = service.RunWizard();

        // Assert
        options.Should().BeNull();
    }

    [Fact]
    public void RunWizard_ExitMode_ReturnsNull()
    {
        // Arrange - selecting Exit should return null immediately
        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            ModeExit
        });

        var service = new InteractiveService(fakeConsole);

        // Act
        var options = service.RunWizard();

        // Assert
        options.Should().BeNull();
    }

    [Fact]
    public void RunWizard_EnterPathManually_UsesTextInput()
    {
        // Arrange - create a .sln file but select "Enter path manually..."
        var slnPath = Path.Combine(_testDirectory, "TestSolution.sln");
        File.WriteAllText(slnPath, "Microsoft Visual Studio Solution File");

        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            ModeAnalyze,
            EnterPathManually  // Choose manual entry
        });
        fakeConsole.TextResponses = new Queue<string>(new[] { "/custom/path" });
        fakeConsole.ConfirmationResponse = true;

        var originalDir = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_testDirectory);

            var service = new InteractiveService(fakeConsole);

            // Act
            var options = service.RunWizard();

            // Assert
            options.Should().NotBeNull();
            options!.SolutionFileDir.Should().Be("/custom/path");
            options.OutputDir.Should().Be("/custom/path");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
        }
    }
}

public class InteractiveOptionsTests
{
    [Fact]
    public void Options_InteractiveDefault_IsFalse()
    {
        // Arrange & Act
        var options = new Options();

        // Assert
        options.Interactive.Should().BeFalse();
    }

    [Fact]
    public void Options_InteractiveCanBeSetTrue()
    {
        // Arrange & Act
        var options = new Options { Interactive = true };

        // Assert
        options.Interactive.Should().BeTrue();
    }
}
