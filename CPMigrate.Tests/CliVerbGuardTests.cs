using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests;

/// <summary>
/// The verb guard exists because a bare leading word is a silently-rewriting mistake:
/// <c>cpmigrate analyze</c> would otherwise parse as a migration. Its suggestion map has to cover
/// every flag a user is likely to reach for — a missing entry reports "unrecognized" with no
/// hint, which is exactly the dead end the guard exists to prevent.
/// </summary>
public class CliVerbGuardTests
{
    [Fact]
    public void RejectsLeadingVerb_FlagFirst_AllowsTheArgs()
    {
        CliVerbGuard.RejectsLeadingVerb(["--analyze", "-s", "."], new FakeConsoleService())
            .Should().BeFalse();
    }

    [Fact]
    public void RejectsLeadingVerb_EmptyArgs_AllowsThem()
    {
        CliVerbGuard.RejectsLeadingVerb([], new FakeConsoleService()).Should().BeFalse();
    }

    [Theory]
    [InlineData("tree", "--tree")]
    [InlineData("verify", "--verify")]
    [InlineData("outdated", "--outdated")]
    [InlineData("deprecated", "--deprecated")]
    [InlineData("unify-props", "--unify-props")]
    [InlineData("unify", "--unify-props")]
    [InlineData("completions", "--completions")]
    [InlineData("list-backups", "--list-backups")]
    [InlineData("doctor", "--doctor")]
    [InlineData("status", "--status")]
    [InlineData("why", "--why")]
    [InlineData("remediate", "--remediate")]
    public void RejectsLeadingVerb_KnownVerb_SuggestsItsFlag(string verb, string flag)
    {
        var console = new FakeConsoleService();

        var rejected = CliVerbGuard.RejectsLeadingVerb([verb, "-s", "."], console);

        rejected.Should().BeTrue();
        console.OutputMessages.Should().Contain(m => m.Contains($"cpmigrate {flag}"),
            $"the suggestion should carry the flag '{verb}' meant");
    }

    [Fact]
    public void RejectsLeadingVerb_Completions_CarriesTheShellArgumentThrough()
    {
        var console = new FakeConsoleService();

        CliVerbGuard.RejectsLeadingVerb(["completions", "zsh"], console);

        console.OutputMessages.Should().Contain(m => m.Contains("cpmigrate --completions zsh"));
    }

    [Fact]
    public void RejectsLeadingVerb_UnknownWord_StillRejectsWithoutSuggestion()
    {
        var console = new FakeConsoleService();

        var rejected = CliVerbGuard.RejectsLeadingVerb(["frobnicate"], console);

        rejected.Should().BeTrue();
        console.ErrorMessages.Should().Contain(m => m.Contains("frobnicate"));
        console.OutputMessages.Should().NotContain(m => m.Contains("Did you mean"));
    }
}
