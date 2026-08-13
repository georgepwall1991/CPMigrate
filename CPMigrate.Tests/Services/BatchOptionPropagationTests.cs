using System.Reflection;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Batch mode runs each solution through a cloned <see cref="Options"/>. The clone used to be an
/// explicit allow-list, which meant every option added afterwards was silently dropped: a batch run
/// quietly ignored <c>--audit</c>, <c>--outdated</c>, <c>--deprecated</c>, and <c>--transitive</c>,
/// so a monorepo security scan reported no vulnerabilities because it never looked for any.
///
/// The clone now copies everything and overrides only what must be per-solution. This test enforces
/// that direction so the next option added cannot reintroduce the bug.
/// </summary>
public class BatchOptionPropagationTests
{
    /// <summary>
    /// Options a batch clone is expected to change, with why. Everything else must be carried over
    /// verbatim.
    /// </summary>
    private static readonly Dictionary<string, string> ExpectedOverrides = new()
    {
        [nameof(Options.SolutionFileDir)] = "points at the solution being processed",
        [nameof(Options.OutputDir)] = "props file belongs beside its own solution",
        [nameof(Options.ProjectFileDir)] = "batch works per solution, never per project",
        [nameof(Options.BackupDir)] = "per-solution, so parallel runs cannot collide",
        [nameof(Options.GitignoreDir)] = "belongs to the solution being processed",
        [nameof(Options.Quiet)] = "individual solution output would drown the batch summary",
        [nameof(Options.BatchDir)] = "cleared so a solution run cannot recurse into another batch",
        [nameof(Options.Rollback)] = "batch never rolls back on behalf of a solution",
        [nameof(Options.Interactive)] = "a batch run cannot prompt per solution",
    };

    [Fact]
    public void CloneForBatchSolution_CarriesOverEveryOptionItDoesNotDeliberatelyOverride()
    {
        var source = FullyPopulatedOptions();

        var clone = source.CloneForBatchSolution("/repo/solution", ".cpmigrate_backup_Sln");

        foreach (var property in SettableProperties())
        {
            var original = property.GetValue(source);
            var cloned = property.GetValue(clone);

            if (ExpectedOverrides.TryGetValue(property.Name, out var reason))
            {
                cloned
                    .Should()
                    .NotBe(original, $"{property.Name} is overridden because it {reason}");
                continue;
            }

            cloned
                .Should()
                .BeEquivalentTo(
                    original,
                    $"{property.Name} describes what to do, so a batch solution must inherit it. "
                        + "If this option genuinely needs a per-solution value, add it to ExpectedOverrides."
                );
        }
    }

    [Fact]
    public void CloneForBatchSolution_PropagatesTheAnalysisFlagsThatWerePreviouslyDropped()
    {
        var source = new Options
        {
            Analyze = true,
            AuditSecurity = true,
            AnalyzeOutdated = true,
            AnalyzeDeprecated = true,
            IncludeTransitive = true,
            AnalyzeLicenses = true,
            FailOn = FailOnSeverity.High,
        };

        var clone = source.CloneForBatchSolution("/repo/solution", ".cpmigrate_backup");

        clone.AuditSecurity.Should().BeTrue();
        clone.AnalyzeOutdated.Should().BeTrue();
        clone.AnalyzeDeprecated.Should().BeTrue();
        clone.AnalyzeLicenses.Should().BeTrue();
        clone.IncludeTransitive.Should().BeTrue();
        clone.FailOn.Should().Be(FailOnSeverity.High);
    }

    [Fact]
    public void CloneForBatchSolution_AppliesThePerSolutionPaths()
    {
        var source = new Options { Analyze = true };

        var clone = source.CloneForBatchSolution("/repo/solution", ".cpmigrate_backup_Sln");

        clone.SolutionFileDir.Should().Be("/repo/solution");
        clone.OutputDir.Should().Be("/repo/solution");
        clone.GitignoreDir.Should().Be("/repo/solution");
        clone.BackupDir.Should().Be(Path.Combine("/repo/solution", ".cpmigrate_backup_Sln"));
        clone.ProjectFileDir.Should().BeEmpty();
        clone.Quiet.Should().BeTrue();
        clone.BatchDir.Should().BeEmpty("a solution run must not recurse into another batch");
        clone.Rollback.Should().BeFalse();
        clone.Interactive.Should().BeFalse();
    }

    [Fact]
    public void CloneForBatchSolution_DoesNotMutateTheSource()
    {
        var source = new Options
        {
            Analyze = true,
            BatchDir = "/repo",
            Quiet = false,
        };

        source.CloneForBatchSolution("/repo/solution", ".cpmigrate_backup");

        source.BatchDir.Should().Be("/repo");
        source.Quiet.Should().BeFalse();
        source.SolutionFileDir.Should().BeEmpty();
    }

    /// <summary>
    /// Sets every settable option to a non-default value, so a dropped copy shows up as a
    /// difference rather than coinciding with the default.
    /// </summary>
    private static Options FullyPopulatedOptions()
    {
        return new Options
        {
            SolutionFileDir = "/source/sln",
            ProjectFileDir = "/source/project.csproj",
            OutputDir = "/source/out",
            KeepAttributes = true,
            NoBackup = true,
            BackupDir = "/source/backup",
            AddBackupToGitignore = true,
            GitignoreDir = "/source/gitignore",
            DryRun = true,
            MergeExisting = true,
            ConflictStrategy = ConflictStrategy.Lowest,
            Rollback = true,
            Analyze = true,
            IncludeTransitive = true,
            AuditSecurity = true,
            AnalyzeOutdated = true,
            AnalyzeDeprecated = true,
            AnalyzeLicenses = true,
            FailOn = FailOnSeverity.Critical,
            Interactive = true,
            Output = OutputFormat.Json,
            OutputFile = "/source/out.json",
            Quiet = false,
            Verbose = true,
            BatchDir = "/source/batch",
            BatchParallel = true,
            BatchContinue = true,
            PruneBackups = true,
            PruneAll = true,
            Retention = 9,
            ListBackups = true,
            InteractiveConflicts = true,
            UnifyProps = true,
            Force = true,
            Fix = true,
            FixDryRun = true,
            Update = true,
            UpdatePackages = true,
            IncludePrerelease = true,
            Bisect = true,
            BisectBudget = 7,
            BisectTestFilter = "Category=Fast",
            Only = "Newtonsoft.Json",
        };
    }

    private static IEnumerable<PropertyInfo> SettableProperties()
    {
        return typeof(Options)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite);
    }
}
