using FluentAssertions;
using CPMigrate.Services;

namespace CPMigrate.Tests.OptionsTests;

public class OptionsValidationTests
{
    [Fact]
    public void Validate_NoBackupWithGitignore_ThrowsArgumentException()
    {
        var options = new CPMigrate.Options
        {
            NoBackup = true,
            AddBackupToGitignore = true
        };

        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*--add-gitignore cannot be used with --no-backup*");
    }

    [Fact]
    public void Validate_NoBackupWithEmptyBackupDir_ThrowsArgumentException()
    {
        var options = new CPMigrate.Options
        {
            NoBackup = false,
            BackupDir = ""
        };

        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*backup-dir must be specified*");
    }

    [Fact]
    public void Validate_ValidOptions_DoesNotThrow()
    {
        var options = new CPMigrate.Options
        {
            NoBackup = false,
            BackupDir = ".",
            AddBackupToGitignore = false
        };

        var action = () => options.Validate();
        action.Should().NotThrow();
    }

    [Fact]
    public void Options_DefaultValues_AreCorrect()
    {
        var options = new CPMigrate.Options();

        options.DryRun.Should().BeFalse();
        options.NoBackup.Should().BeFalse();
        options.KeepAttributes.Should().BeFalse();
        options.MergeExisting.Should().BeFalse();
        options.ConflictStrategy.Should().Be(ConflictStrategy.Highest);
    }

    [Fact]
    public void Validate_RollbackWithDryRun_ThrowsArgumentException()
    {
        var options = new CPMigrate.Options
        {
            Rollback = true,
            DryRun = true,
            BackupDir = "."
        };

        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*--rollback cannot be used with --dry-run*");
    }

    [Fact]
    public void Validate_RollbackWithEmptyBackupDir_ThrowsArgumentException()
    {
        var options = new CPMigrate.Options
        {
            Rollback = true,
            BackupDir = ""
        };

        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*backup-dir must be specified for rollback*");
    }

    [Fact]
    public void Validate_RollbackWithValidBackupDir_DoesNotThrow()
    {
        var options = new CPMigrate.Options
        {
            Rollback = true,
            BackupDir = "."
        };

        var action = () => options.Validate();
        action.Should().NotThrow();
    }

    [Fact]
    public void Options_RollbackDefault_IsFalse()
    {
        var options = new CPMigrate.Options();
        options.Rollback.Should().BeFalse();
    }

    [Fact]
    public void Validate_AnalyzeWithDryRun_ThrowsArgumentException()
    {
        var options = new CPMigrate.Options
        {
            Analyze = true,
            DryRun = true
        };

        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*--analyze cannot be used with --dry-run*");
    }

    [Fact]
    public void Validate_AnalyzeWithRollback_ThrowsArgumentException()
    {
        var options = new CPMigrate.Options
        {
            Analyze = true,
            Rollback = true,
            BackupDir = "."
        };

        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*--analyze cannot be used with --rollback*");
    }

    [Fact]
    public void Validate_AnalyzeOnly_DoesNotThrow()
    {
        var options = new CPMigrate.Options
        {
            Analyze = true
        };

        var action = () => options.Validate();
        action.Should().NotThrow();
    }

    [Fact]
    public void Options_AnalyzeDefault_IsFalse()
    {
        var options = new CPMigrate.Options();
        options.Analyze.Should().BeFalse();
    }

    [Fact]
    public void Validate_OutdatedWithoutAnalyze_ThrowsArgumentException()
    {
        var options = new CPMigrate.Options
        {
            AnalyzeOutdated = true
        };

        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*--outdated requires --analyze*");
    }

    [Fact]
    public void Validate_DeprecatedWithoutAnalyze_ThrowsArgumentException()
    {
        var options = new CPMigrate.Options
        {
            AnalyzeDeprecated = true
        };

        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*--deprecated requires --analyze*");
    }

    [Fact]
    public void Validate_LicensesWithoutAnalyze_ThrowsArgumentException()
    {
        var options = new CPMigrate.Options
        {
            AnalyzeLicenses = true
        };

        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*--licenses requires --analyze*");
    }

    [Fact]
    public void Validate_AnalyzeWithOutdatedDeprecatedAndLicenses_DoesNotThrow()
    {
        var options = new CPMigrate.Options
        {
            Analyze = true,
            AnalyzeOutdated = true,
            AnalyzeDeprecated = true,
            AnalyzeLicenses = true
        };

        var action = () => options.Validate();
        action.Should().NotThrow();
    }

    [Fact]
    public void Validate_JsonOutputWithInteractiveConflicts_ThrowsArgumentException()
    {
        var options = new CPMigrate.Options
        {
            Output = OutputFormat.Json,
            InteractiveConflicts = true
        };

        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*--output Json cannot be used with --interactive-conflicts*");
    }

    [Fact]
    public void Validate_RollbackWithJsonOutputWithoutForce_ThrowsArgumentException()
    {
        var options = new CPMigrate.Options
        {
            Rollback = true,
            BackupDir = ".",
            Output = OutputFormat.Json
        };

        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*--rollback requires --force when --output Json is used*");
    }

    [Fact]
    public void Validate_PruneWithJsonOutputWithoutForce_ThrowsArgumentException()
    {
        var options = new CPMigrate.Options
        {
            PruneBackups = true,
            Output = OutputFormat.Json
        };

        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*--prune-backups and --prune-all require --force when --output Json is used*");
    }
}
