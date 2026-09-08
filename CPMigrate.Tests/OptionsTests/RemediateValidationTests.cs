using FluentAssertions;

namespace CPMigrate.Tests.OptionsTests;

/// <summary>
/// Pins the rejections around <c>--remediate</c>.
///
/// The stray-flag cases matter more than the combination cases: a silently ignored
/// <c>--allow-major</c> looks exactly like one that was honoured, and the user only finds out when a
/// CVE they believed cleared is still there.
/// </summary>
public class RemediateValidationTests
{
    [Fact]
    public void Validate_RemediateAlone_DoesNotThrow()
    {
        var options = new Options { Remediate = true };

        options.Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Fact]
    public void Validate_RemediateWithDryRun_DoesNotThrow()
    {
        // A plan-only run is the recommended first step, so it has to be allowed.
        var options = new Options { Remediate = true, DryRun = true };

        options.Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Fact]
    public void Validate_RemediateWithAllowMajor_DoesNotThrow()
    {
        var options = new Options { Remediate = true, AllowMajor = true };

        options.Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Fact]
    public void Validate_AllowMajorWithoutRemediate_Throws()
    {
        var options = new Options { AllowMajor = true };

        options
            .Invoking(o => o.Validate())
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*--allow-major requires --remediate*");
    }

    [Fact]
    public void Validate_RemediateWithUpdatePackages_Throws()
    {
        // The two disagree about which version to move to, so honouring both is not possible.
        var options = new Options { Remediate = true, UpdatePackages = true };

        options
            .Invoking(o => o.Validate())
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*--remediate cannot be used with --update-packages*");
    }

    [Fact]
    public void Validate_RemediateWithAnalyze_Throws()
    {
        var options = new Options { Remediate = true, Analyze = true };

        options
            .Invoking(o => o.Validate())
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*--remediate cannot be used with --analyze*");
    }

    [Fact]
    public void Validate_RemediateWithVerify_Throws()
    {
        // --verify proves the resolved graph did not move; remediation moves it deliberately.
        //
        // The rejection comes from the shared "a mode that runs instead of a migration" list rather
        // than from remediation's own validation, which is the point of this test: that list is
        // hand-maintained, and a mode missing from it is silently accepted instead of rejected.
        var options = new Options { Remediate = true, Verify = true };

        options
            .Invoking(o => o.Validate())
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*--verify cannot be combined with --remediate*");
    }

    [Theory]
    [InlineData("interactive")]
    [InlineData("rollback")]
    [InlineData("unify-props")]
    public void Validate_RemediateWithAnotherMode_Throws(string mode)
    {
        var options = new Options
        {
            Remediate = true,
            Interactive = mode == "interactive",
            Rollback = mode == "rollback",
            UnifyProps = mode == "unify-props",
        };

        options
            .Invoking(o => o.Validate())
            .Should()
            .Throw<ArgumentException>()
            .WithMessage($"*--remediate cannot be used with --{mode}*");
    }

    [Fact]
    public void Validate_BisectWithRemediate_DoesNotThrow()
    {
        var options = new Options { Remediate = true, Bisect = true };

        options.Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Fact]
    public void Validate_BisectWithRemediateAndDryRun_Throws()
    {
        var options = new Options { Remediate = true, Bisect = true, DryRun = true };

        options
            .Invoking(o => o.Validate())
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*--bisect cannot be used with --dry-run*");
    }

    [Fact]
    public void Validate_OnlyWithRemediate_DoesNotThrow()
    {
        var options = new Options { Remediate = true, Only = "Newtonsoft.Json" };

        options.Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Fact]
    public void Validate_BisectWithNeitherCommand_NamesBoth()
    {
        var options = new Options { Bisect = true };

        options
            .Invoking(o => o.Validate())
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*--bisect requires --update-packages or --remediate*");
    }
}
