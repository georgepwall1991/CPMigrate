using FluentAssertions;
using Xunit;

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
}
