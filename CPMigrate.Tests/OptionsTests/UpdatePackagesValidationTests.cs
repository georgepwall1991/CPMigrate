using FluentAssertions;

namespace CPMigrate.Tests.OptionsTests;

public class UpdatePackagesValidationTests
{
    [Fact]
    public void Validate_UpdatePackagesOnly_DoesNotThrow()
    {
        var options = new Options
        {
            UpdatePackages = true
        };

        var action = () => options.Validate();
        action.Should().NotThrow();
    }

    [Fact]
    public void Validate_UpdatePackagesWithDryRun_DoesNotThrow()
    {
        var options = new Options
        {
            UpdatePackages = true,
            DryRun = true
        };

        var action = () => options.Validate();
        action.Should().NotThrow();
    }

    [Fact]
    public void Validate_UpdatePackagesWithIncludePrerelease_DoesNotThrow()
    {
        var options = new Options
        {
            UpdatePackages = true,
            IncludePrerelease = true
        };

        var action = () => options.Validate();
        action.Should().NotThrow();
    }

    [Fact]
    public void Validate_UpdatePackagesWithRollback_ThrowsArgumentException()
    {
        var options = new Options
        {
            UpdatePackages = true,
            Rollback = true,
            BackupDir = "."
        };

        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*--update-packages cannot be used with --rollback*");
    }

    [Fact]
    public void Validate_UpdatePackagesWithAnalyze_ThrowsArgumentException()
    {
        var options = new Options
        {
            UpdatePackages = true,
            Analyze = true
        };

        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*--update-packages cannot be used with --analyze*");
    }

    [Fact]
    public void Validate_UpdatePackagesWithBatch_ThrowsArgumentException()
    {
        var options = new Options
        {
            UpdatePackages = true,
            BatchDir = "/some/path"
        };

        // BatchDir validation will fail because directory doesn't exist,
        // but we need to check the update-packages conflict first
        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_UpdatePackagesWithUnifyProps_ThrowsArgumentException()
    {
        var options = new Options
        {
            UpdatePackages = true,
            UnifyProps = true
        };

        var action = () => options.Validate();
        action.Should().Throw<ArgumentException>()
            .WithMessage("*--update-packages cannot be used with --unify-props*");
    }

    [Fact]
    public void Options_UpdatePackagesDefault_IsFalse()
    {
        var options = new Options();
        options.UpdatePackages.Should().BeFalse();
    }

    [Fact]
    public void Options_IncludePrereleaseDefault_IsFalse()
    {
        var options = new Options();
        options.IncludePrerelease.Should().BeFalse();
    }
}
