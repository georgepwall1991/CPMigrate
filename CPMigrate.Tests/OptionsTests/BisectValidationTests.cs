using CPMigrate.Services.Update;
using FluentAssertions;

namespace CPMigrate.Tests.OptionsTests;

public class BisectValidationTests
{
    [Fact]
    public void Validate_BisectWithUpdatePackages_DoesNotThrow()
    {
        var options = new Options { UpdatePackages = true, Bisect = true };

        options.Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Fact]
    public void Validate_BisectWithoutUpdatePackages_Throws()
    {
        var options = new Options { Bisect = true };

        options.Invoking(o => o.Validate())
            .Should().Throw<ArgumentException>()
            .WithMessage("*--bisect requires --update-packages*");
    }

    [Fact]
    public void Validate_BisectWithDryRun_Throws()
    {
        // Bisection has to observe real test runs, so there is nothing meaningful to preview.
        var options = new Options { UpdatePackages = true, Bisect = true, DryRun = true };

        options.Invoking(o => o.Validate())
            .Should().Throw<ArgumentException>()
            .WithMessage("*--bisect cannot be used with --dry-run*");
    }

    [Fact]
    public void Validate_TestFilterWithoutBisect_Throws()
    {
        var options = new Options { UpdatePackages = true, BisectTestFilter = "Category=Fast" };

        options.Invoking(o => o.Validate())
            .Should().Throw<ArgumentException>()
            .WithMessage("*--bisect-test-filter requires --bisect*");
    }

    [Fact]
    public void Validate_BudgetWithoutBisect_Throws()
    {
        var options = new Options { UpdatePackages = true, BisectBudget = 4 };

        options.Invoking(o => o.Validate())
            .Should().Throw<ArgumentException>()
            .WithMessage("*--bisect-budget requires --bisect*");
    }

    [Fact]
    public void Validate_UnspecifiedBudgetWithoutBisect_DoesNotThrow()
    {
        // Unspecified is the state every non-bisect run is in, so it must not trip the guard.
        var options = new Options { UpdatePackages = true };

        options.Invoking(o => o.Validate()).Should().NotThrow();
        options.EffectiveBisectBudget.Should().Be(BisectSearchStrategy.DefaultBudget);
    }

    [Fact]
    public void EffectiveBisectBudget_ExplicitValue_Wins()
    {
        new Options { BisectBudget = 24 }.EffectiveBisectBudget.Should().Be(24);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Validate_NonPositiveBudget_Throws(int budget)
    {
        var options = new Options { UpdatePackages = true, Bisect = true, BisectBudget = budget };

        options.Invoking(o => o.Validate())
            .Should().Throw<ArgumentException>()
            .WithMessage("*--bisect-budget must be at least 1*");
    }

    [Fact]
    public void Validate_OnlyWithoutUpdatePackages_Throws()
    {
        var options = new Options { Only = "Serilog" };

        options.Invoking(o => o.Validate())
            .Should().Throw<ArgumentException>()
            .WithMessage("*--only requires --update-packages*");
    }

    [Theory]
    [InlineData("Serilog", new[] { "Serilog" })]
    [InlineData("Serilog,AutoMapper", new[] { "Serilog", "AutoMapper" })]
    [InlineData(" Serilog , AutoMapper ", new[] { "Serilog", "AutoMapper" })]
    [InlineData("Serilog,,AutoMapper", new[] { "Serilog", "AutoMapper" })]
    public void ParseOnlyPackages_SplitsAndTrims(string input, string[] expected)
    {
        new Options { Only = input }.ParseOnlyPackages().Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    public void ParseOnlyPackages_BlankInput_ReturnsNull(string? input)
    {
        new Options { Only = input }.ParseOnlyPackages().Should().BeNull();
    }
}
