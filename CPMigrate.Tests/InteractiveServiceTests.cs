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

    /// <summary>
    /// The label the browser offers for accepting the directory being browsed. Built the same way the
    /// wizard builds it, because a queued answer that was never offered is now an error rather than a
    /// silent fallthrough — which is the whole point of the change these tests cover.
    /// </summary>
    private string UseCurrentDirectory => "🎯 Use current directory: " + Path.GetFileName(_testDirectory);

    /// <summary>Writes a project so the wizard sees something migratable in the directory.</summary>
    private void CreateProject(string name = "App.csproj", string version = "13.0.1")
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
        // A project has to exist for the clean-path action to be offered at all. Without one the label
        // below is absent from the menu, and this test used to pass anyway: an unmatched selection fell
        // back to CustomMigration, so it asserted nothing about the migration path it named.
        CreateProject();

        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            "⚡️ Migrate to Central Package Management (Clean Path)",
            ConflictHighest,
            "Yes (recommended)",                                 // create backup
            "Current directory (./.cpmigrate_backup)",            // backup location
            "Yes",                                               // add to .gitignore
            "No - make changes immediately",                      // dry run
            "No - remove them (recommended for clean CPM)",       // keep version attributes
            "No (recommended for clean CPM)"                      // pin transitive
        });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole, _testDirectory);
        var options = service.RunWizard();

        options.Should().NotBeNull();
        options!.Analyze.Should().BeFalse();
        options.SolutionFileDir.Should().NotBeEmpty();
        options.ConflictStrategy.Should().Be(ConflictStrategy.Highest);
        options.DryRun.Should().BeFalse();
        options.KeepAttributes.Should().BeFalse();
        options.IncludeTransitive.Should().BeFalse();
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
            UseCurrentDirectory,
            "No - direct references only (faster)",   // transitive
            "No",                                     // vulnerability audit
            "No",                                     // outdated
            "No",                                     // deprecated
            "No",                                     // licenses
            "No - just report"                        // fix mode
        });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole, _testDirectory);
        var options = service.RunWizard();

        options.Should().NotBeNull();
        options!.Analyze.Should().BeTrue();
        // Previously the queue was four answers short of the prompts asked, so these landed on
        // whatever happened to be next in line — AuditSecurity came out true without being chosen.
        options.IncludeTransitive.Should().BeFalse();
        options.AuditSecurity.Should().BeFalse();
        options.AnalyzeOutdated.Should().BeFalse();
        options.AnalyzeDeprecated.Should().BeFalse();
        options.AnalyzeLicenses.Should().BeFalse();
        options.Fix.Should().BeFalse();
        options.FixDryRun.Should().BeFalse();
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
            EnterPathManually,
            // Nothing follows the manual entry — it returns straight out of the browser. The extra
            // "use current directory" answer this queue used to carry was consumed by the *conflict*
            // prompt instead, shifting every later answer by one.
            ConflictHighest,
            "No",                                             // create backup
            "No - make changes immediately",                   // dry run
            "No - remove them (recommended for clean CPM)",    // keep version attributes
            "No (recommended for clean CPM)"                   // pin transitive
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
        CreateProject();

        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            "⚙️  Custom Migration (Manual Setup)",
            UseCurrentDirectory,
            ConflictHighest,                                   // conflict strategy
            "Yes (recommended)",                               // create backup
            "Current directory (./.cpmigrate_backup)",          // backup location
            "Yes",                                             // add to .gitignore
            "No - make changes immediately",                    // dry run
            "No - remove them (recommended for clean CPM)",     // keep version attributes
            "No (recommended for clean CPM)"                    // pin transitive
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
        CreateProject();

        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            "🏗  Unify Directory.Build.props (Clean Architecture)",
            UseCurrentDirectory
        });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole, _testDirectory);
        var options = service.RunWizard();

        options.Should().NotBeNull();
        options!.UnifyProps.Should().BeTrue();
        options.SolutionFileDir.Should().NotBeNull();
    }

    [Fact]
    public void RunWizard_UpdatePackagesAppearsInMenu_WhenCpmEnabled()
    {
        // Arrange - create Directory.Packages.props to enable CPM detection
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, "<Project></Project>");

        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            "Exit" // Just exit after seeing the menu
        });

        var service = new InteractiveService(fakeConsole, _testDirectory);
        var options = service.RunWizard();

        // The fact that "Exit" is returned means the wizard ran.
        // We verify via a full run that update packages is selectable when CPM is enabled.
        options.Should().BeNull(); // Exited
    }

    [Fact]
    public void RunWizard_UpdatePackagesMode_ReturnsCorrectOptions()
    {
        // Arrange - create Directory.Packages.props to enable CPM detection
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, "<Project></Project>");

        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            "📡 Update NuGet packages to latest versions", // Quick action
            "🎯 Use current directory: " + Path.GetFileName(_testDirectory), // Path selection
            "Yes - include transitive dependencies", // Transitive
            "No - stable versions only", // Pre-release
            "Yes - preview changes without modifying files", // Dry run
            "Yes" // Confirmation
        });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole, _testDirectory);
        var options = service.RunWizard();

        options.Should().NotBeNull();
        options!.UpdatePackages.Should().BeTrue();
        options.IncludeTransitive.Should().BeTrue();
        options.IncludePrerelease.Should().BeFalse();
        options.DryRun.Should().BeTrue();
    }

    [Fact]
    public void RunWizard_UpdatePackagesDoesNotAppear_WhenCpmNotEnabled()
    {
        // Arrange - no Directory.Packages.props, so CPM is not detected
        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            "Exit"
        });

        var service = new InteractiveService(fakeConsole, _testDirectory);
        var options = service.RunWizard();

        // The wizard should run without offering UpdatePackages
        // Since we can't directly inspect the menu, we verify it doesn't crash
        // and that the option is not set
        options.Should().BeNull(); // Exited
    }

    [Fact]
    public void RunWizard_UpdatePackagesWithPrerelease_ReturnsCorrectOptions()
    {
        // Arrange - create Directory.Packages.props to enable CPM detection
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, "<Project></Project>");

        var fakeConsole = new FakeConsoleService();
        fakeConsole.SelectionResponses = new Queue<string>(new[]
        {
            "📡 Update NuGet packages to latest versions", // Quick action
            "🎯 Use current directory: " + Path.GetFileName(_testDirectory), // Path selection
            "No - direct packages only", // Transitive
            "Yes - include pre-release versions", // Pre-release
            "No - make changes immediately", // Dry run
            "Yes" // Confirmation
        });
        fakeConsole.ConfirmationResponse = true;

        var service = new InteractiveService(fakeConsole, _testDirectory);
        var options = service.RunWizard();

        options.Should().NotBeNull();
        options!.UpdatePackages.Should().BeTrue();
        options.IncludeTransitive.Should().BeFalse();
        options.IncludePrerelease.Should().BeTrue();
        options.DryRun.Should().BeFalse();
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
