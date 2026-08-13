using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests;

/// <summary>
/// The wizard used to decide what each answer meant by reading its label back: <c>StartsWith("Yes")</c>,
/// <c>selection[3..].TrimEnd('/')</c>, an exact match against a display string, or a dictionary lookup
/// with a permissive fallback. Every one of those made the wording load-bearing without saying so, and
/// each had a silent wrong answer waiting behind it — a reworded option flips to its opposite, a
/// directory whose name starts with the browser's own decoration navigates somewhere else, an
/// unmatched conflict label takes the highest version of everything.
///
/// These tests assert the property that replaced all of it: an answer is only ever the value that was
/// attached to the label offered, and an answer that was never offered is an error rather than a
/// default. The <see cref="InteractiveServiceTests"/> above cover the happy paths; these cover the
/// cases where the old label-reading silently produced something plausible.
/// </summary>
public class InteractivePromptRoutingTests : IDisposable
{
    private readonly string _testDirectory;

    private const string ConflictHighest = "⬆️  Highest version (recommended)";
    private const string ConflictLowest = "⬇️  Lowest version";
    private const string ConflictFail = "⛔️ Fail on conflict";
    private const string ConflictInteractive = "🤝 Resolve each conflict interactively";
    private const string CustomMigration = "⚙️  Custom Migration (Manual Setup)";

    public InteractivePromptRoutingTests()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"CPMigratePromptRouting_{Guid.NewGuid():N}"
        );
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

    private string UseCurrentDirectory =>
        "🎯 Use current directory: " + Path.GetFileName(_testDirectory);

    [Fact]
    public void AnswerThatWasNeverOffered_Throws_RatherThanPickingSomethingPlausible()
    {
        // The failure this prevents: the wizard proceeds with an option the user did not choose, and
        // the run looks exactly like they chose it. Nothing downstream can tell the difference, so the
        // only place it can be caught is here.
        CreateProject();
        var console = new FakeConsoleService();
        console.SelectionResponses = new Queue<string>([
            CustomMigration,
            UseCurrentDirectory,
            "Whatever seems best",
        ]);

        var service = new InteractiveService(console, _testDirectory);

        var act = () => service.RunWizard();

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Whatever seems best*not offered*");
    }

    [Fact]
    public void MissionThatWasNeverOffered_Throws_RatherThanStartingAMigration()
    {
        // The old fallback was WizardAction.CustomMigration, so an unrecognised mission began rewriting
        // project files. That is the worst possible default for an answer nobody gave.
        var console = new FakeConsoleService();
        console.SelectionResponses = new Queue<string>([
            "📡 Update NuGet packages to latest versions",
        ]);

        var service = new InteractiveService(console, _testDirectory);

        // No Directory.Packages.props, so the update action is not in the menu.
        var act = () => service.RunWizard();

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*not one of the offered actions*");
    }

    [Theory]
    [InlineData(ConflictHighest, ConflictStrategy.Highest, false)]
    [InlineData(ConflictLowest, ConflictStrategy.Lowest, false)]
    [InlineData(ConflictFail, ConflictStrategy.Fail, false)]
    [InlineData(ConflictInteractive, ConflictStrategy.Highest, true)]
    public void EveryConflictAnswer_MapsToItsOwnStrategy(
        string label,
        ConflictStrategy expected,
        bool reviewIndividually
    )
    {
        // The switch this replaced ended in `_ => Highest`, so only two of these four were actually
        // distinguishable from a miss.
        CreateProject();
        var console = new FakeConsoleService();
        console.SelectionResponses = new Queue<string>([
            CustomMigration,
            UseCurrentDirectory,
            label,
            "No", // create backup
            "No - make changes immediately",
            "No - remove them (recommended for clean CPM)",
            "No (recommended for clean CPM)",
        ]);
        console.ConfirmationResponse = true;

        var options = new InteractiveService(console, _testDirectory).RunWizard();

        options.Should().NotBeNull();
        options!.ConflictStrategy.Should().Be(expected);
        options.InteractiveConflicts.Should().Be(reviewIndividually);
    }

    [Fact]
    public void ReviewingConflictsIndividually_SkipsTheRemainingMigrationPrompts()
    {
        // Interactive conflict resolution asks its questions during the migration itself, so the wizard
        // stops here. Asserted because the queue below deliberately has nothing after the conflict
        // answer: if the flow asked anything more, it would now throw rather than silently continue.
        CreateProject();
        var console = new FakeConsoleService();
        console.SelectionResponses = new Queue<string>([
            CustomMigration,
            UseCurrentDirectory,
            ConflictInteractive,
        ]);
        console.ConfirmationResponse = true;

        var options = new InteractiveService(console, _testDirectory).RunWizard();

        options.Should().NotBeNull();
        options!.InteractiveConflicts.Should().BeTrue();
    }

    [Fact]
    public void DecliningABackup_DoesNotAskWhereToPutIt()
    {
        CreateProject();
        var console = new FakeConsoleService();
        console.SelectionResponses = new Queue<string>([
            CustomMigration,
            UseCurrentDirectory,
            ConflictHighest,
            "No", // create backup — the location and gitignore prompts must not follow
            "No - make changes immediately",
            "No - remove them (recommended for clean CPM)",
            "No (recommended for clean CPM)",
        ]);
        console.ConfirmationResponse = true;

        var options = new InteractiveService(console, _testDirectory).RunWizard();

        options.Should().NotBeNull();
        options!.NoBackup.Should().BeTrue();
        options.AddBackupToGitignore.Should().BeFalse();
    }

    [Fact]
    public void BrowsingIntoADirectoryNamedLikeTheBrowsersOwnDecoration_StillGoesThere()
    {
        // The concrete bug in `selection[3..].TrimEnd('/')`: it assumed the first three characters were
        // the emoji it added. A directory literally named "📁 src" produced a label of "📁 📁 src/",
        // which sliced back to "📁 src" only by luck of the emoji's byte width — and a directory whose
        // name ended in a slash-like character lost it. The destination now travels with the entry.
        var awkward = Path.Combine(_testDirectory, "📁 src");
        Directory.CreateDirectory(awkward);
        CreateProject(directory: awkward);

        var console = new FakeConsoleService();
        console.SelectionResponses = new Queue<string>([
            CustomMigration,
            "📁 📁 src/",
            "🎯 Use current directory: 📁 src",
            ConflictHighest,
            "No",
            "No - make changes immediately",
            "No - remove them (recommended for clean CPM)",
            "No (recommended for clean CPM)",
        ]);
        console.ConfirmationResponse = true;

        var options = new InteractiveService(console, _testDirectory).RunWizard();

        options.Should().NotBeNull();
        options!.SolutionFileDir.Should().Be(awkward);
    }

    [Fact]
    public void ACpmRootWhoseProjectsAreNested_CanBeSelected()
    {
        // A repository with Directory.Packages.props at the top and projects under src/ is the ordinary
        // shape of a migrated solution, and it is exactly what --analyze is pointed at. The browser used
        // to offer no way to accept that directory: the option was gated on solutions or projects being
        // present in the directory itself. The gap was invisible because selecting an option that had
        // not been offered fell through to returning the current directory anyway.
        File.WriteAllText(
            Path.Combine(_testDirectory, "Directory.Packages.props"),
            "<Project></Project>"
        );
        var nested = Path.Combine(_testDirectory, "src");
        Directory.CreateDirectory(nested);
        CreateProject(directory: nested);

        var console = new FakeConsoleService();
        console.SelectionResponses = new Queue<string>([
            "🔍 Analyze current CPM setup for issues",
            UseCurrentDirectory,
            "No - direct references only (faster)",
            "No",
            "No",
            "No",
            "No",
            "No - just report",
        ]);
        console.ConfirmationResponse = true;

        var options = new InteractiveService(console, _testDirectory).RunWizard();

        options.Should().NotBeNull();
        options!.Analyze.Should().BeTrue();
        options.SolutionFileDir.Should().Be(_testDirectory);
    }

    [Fact]
    public void GoingUpAndBackDown_LandsWhereTheUserPointed()
    {
        // Navigation is two entries whose destinations are computed when the list is built, rather than
        // a parent recomputed from a label and a child rebuilt with Path.Combine.
        var nested = Path.Combine(_testDirectory, "inner");
        Directory.CreateDirectory(nested);
        CreateProject(directory: nested);
        CreateProject(name: "Outer.csproj");

        var console = new FakeConsoleService();
        console.SelectionResponses = new Queue<string>([
            CustomMigration,
            "📁 inner/",
            "⬅️  Go up to parent directory",
            "🎯 Use current directory: " + Path.GetFileName(_testDirectory),
            ConflictHighest,
            "No",
            "No - make changes immediately",
            "No - remove them (recommended for clean CPM)",
            "No (recommended for clean CPM)",
        ]);
        console.ConfirmationResponse = true;

        var options = new InteractiveService(console, _testDirectory).RunWizard();

        options.Should().NotBeNull();
        options!.SolutionFileDir.Should().Be(_testDirectory);
    }

    [Fact]
    public void SelectingASolutionListing_AcceptsTheDirectoryHoldingIt()
    {
        // Solution and project entries are labels for the directory, not separate destinations. That was
        // previously expressed as "no branch matched, so fall through to return rootPath" — true by
        // omission. Now it is the entry's own value, and this pins the behaviour either way.
        CreateProject();
        File.WriteAllText(
            Path.Combine(_testDirectory, "App.sln"),
            "Microsoft Visual Studio Solution File, Format Version 12.00\n"
        );

        var console = new FakeConsoleService();
        console.SelectionResponses = new Queue<string>([
            CustomMigration,
            "🟦 Solution: App.sln",
            ConflictHighest,
            "No",
            "No - make changes immediately",
            "No - remove them (recommended for clean CPM)",
            "No (recommended for clean CPM)",
        ]);
        console.ConfirmationResponse = true;

        var options = new InteractiveService(console, _testDirectory).RunWizard();

        options.Should().NotBeNull();
        options!.SolutionFileDir.Should().Be(_testDirectory);
    }

    [Fact]
    public void CancellingTheManualPathPrompt_AbandonsTheWizard()
    {
        var console = new FakeConsoleService();
        console.SelectionResponses = new Queue<string>([
            CustomMigration,
            "✏️  Enter path manually...",
        ]);
        console.TextResponses = new Queue<string>([string.Empty]);

        var options = new InteractiveService(console, _testDirectory).RunWizard();

        options.Should().BeNull("an empty path is a cancellation, not the current directory");
    }

    private void CreateProject(
        string name = "App.csproj",
        string version = "13.0.1",
        string? directory = null
    )
    {
        File.WriteAllText(
            Path.Combine(directory ?? _testDirectory, name),
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
}
