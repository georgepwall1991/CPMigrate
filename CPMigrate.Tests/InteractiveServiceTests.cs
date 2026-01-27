using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

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
        var fakeConsole = new FakeConsoleService();
        // The quick action label when no CPM is detected
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            "⚡️ Migrate to Central Package Management (Clean Path)",
            "Yes" // Confirmation
        });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole, _testDirectory);
        var options = service.RunWizard();

        options.Should().NotBeNull();
        options!.Analyze.Should().BeFalse();
        // Since test dir is empty, it uses the directory we passed
        options.SolutionFileDir.Should().NotBeEmpty();
    }

    [Fact]
    public void RunWizard_AnalyzeMode_ReturnsCorrectOptions()
    {
        // Arrange
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, "<Project></Project>");

        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            "🔍 Analyze current CPM setup for issues",
            "🎯 Use current directory: " + Path.GetFileName(_testDirectory),
            "No - just report",
            "Yes" // Confirmation
        });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole, _testDirectory);
        var options = service.RunWizard();

        options.Should().NotBeNull();
        options!.Analyze.Should().BeTrue();
    }

    [Fact]
    public void RunWizard_ExitMode_ReturnsNull()
    {
        // Arrange
        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            "Exit"
        });

        var service = new InteractiveService(fakeConsole, _testDirectory);
        var options = service.RunWizard();

        options.Should().BeNull();
    }

    [Fact]
    public void RunWizard_EnterPathManually_UsesTextInput()
    {
        // Arrange
        var fakeConsole = new FakeConsoleService();
        var manualPath = "custom_manual_path"; // Use relative to avoid drive ambiguity

        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            "⚙️  Custom Migration (Manual Setup)",
            "✏️  Enter path manually...",
            "🎯 Use current directory: custom", // Satisfy the browser loop after path entry
            "⬆️  Highest version (recommended)",
            "No", // backup
            "No - make changes immediately",
            "No - remove them (recommended for clean CPM)",
            "Yes" // Confirmation
        });
        fakeConsole.TextResponses = new Queue<string>(new[] { manualPath });
        fakeConsole.ConfirmationResponse = true;

        var expectedPath = Path.GetFullPath(Path.Combine(_testDirectory, manualPath));

        var service = new InteractiveService(fakeConsole, _testDirectory);
        var options = service.RunWizard();

        options.Should().NotBeNull();
        options!.SolutionFileDir.Should().Be(expectedPath);
    }
    [Fact]
    public void RunWizard_CustomMigrationWithBackup_ConfiguresBackupCorrectly()
    {
        // Arrange
        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            "⚙️  Custom Migration (Manual Setup)",
            "🎯 Use current directory: " + Path.GetFileName(_testDirectory),
            "⬆️  Highest version (recommended)", // Conflict Strategy
            "Yes (recommended)", // Create Backup
            "Current directory (./.cpmigrate_backup)", // Backup Location
            "Yes", // Add to gitignore
            "No - make changes immediately", // Dry Run
            "No - remove them (recommended for clean CPM)", // Keep Attrs
            "No (recommended for clean CPM)", // Transitive
            "Yes" // Confirmation
        });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole, _testDirectory);
        var options = service.RunWizard();

        options.Should().NotBeNull();
        options!.NoBackup.Should().BeFalse();
        options.BackupDir.Should().Be(".");
        options.AddBackupToGitignore.Should().BeTrue();
    }

    [Fact]
    public void RunWizard_UnifyPropsMode_ReturnsCorrectOptions()
    {
        // Arrange
        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            "🏗  Unify Directory.Build.props (Clean Architecture)",
            "🎯 Use current directory: " + Path.GetFileName(_testDirectory),
            "Yes" // Confirmation
        });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole, _testDirectory);
        var options = service.RunWizard();

        options.Should().NotBeNull();
        options!.UnifyProps.Should().BeTrue();
        options.SolutionFileDir.Should().NotBeNull();
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
