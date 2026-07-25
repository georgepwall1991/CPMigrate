using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Update;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Moq;

namespace CPMigrate.Tests.Services.Update;

public class VerificationRunnerTests
{
    private readonly Mock<IDotNetCliService> _cli = new();
    private readonly FakeConsoleService _console = new();

    [Fact]
    public async Task VerifyAsync_RestoreFails_DoesNotRunTests()
    {
        _cli.Setup(c => c.RunRestoreAsync(It.IsAny<string>())).ReturnsAsync(("restore blew up", false));

        var result = await CreateRunner().VerifyAsync([Update("A")]);

        result.Outcome.Should().Be(VerificationOutcome.RestoreFailed);
        result.Output.Should().Be("restore blew up");
        _cli.Verify(c => c.RunTestAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task VerifyAsync_TestsFail_ReturnsTestFailureWithOutput()
    {
        SetupRestoreSuccess();
        _cli.Setup(c => c.RunTestAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(("3 tests failed", false));

        var result = await CreateRunner().VerifyAsync([Update("A")]);

        result.Outcome.Should().Be(VerificationOutcome.TestsFailed);
        result.Passed.Should().BeFalse();
        result.Output.Should().Be("3 tests failed");
    }

    [Fact]
    public async Task VerifyAsync_RepeatedSubset_IsServedFromCache()
    {
        SetupRestoreSuccess();
        SetupTestSuccess();
        var runner = CreateRunner();

        await runner.VerifyAsync([Update("A"), Update("B")]);
        await runner.VerifyAsync([Update("A"), Update("B")]);

        runner.RunCount.Should().Be(1);
        _cli.Verify(c => c.RunTestAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task VerifyAsync_SameSubsetDifferentOrder_SharesCacheEntry()
    {
        SetupRestoreSuccess();
        SetupTestSuccess();
        var runner = CreateRunner();

        await runner.VerifyAsync([Update("A"), Update("B")]);
        await runner.VerifyAsync([Update("B"), Update("A")]);

        // Both produce byte-identical props files, so re-running the suite would be pure waste.
        runner.RunCount.Should().Be(1);
    }

    [Fact]
    public async Task VerifyAsync_DifferentVersionOfSamePackage_IsNotACacheHit()
    {
        SetupRestoreSuccess();
        SetupTestSuccess();
        var runner = CreateRunner();

        await runner.VerifyAsync([new PackageUpdateEntry("A", "1.0.0", "2.0.0", false, true)]);
        await runner.VerifyAsync([new PackageUpdateEntry("A", "1.0.0", "3.0.0", false, true)]);

        runner.RunCount.Should().Be(2);
    }

    [Fact]
    public async Task VerifyAsync_PassesTestFilterThrough()
    {
        SetupRestoreSuccess();
        SetupTestSuccess();

        await CreateRunner("Category=Fast").VerifyAsync([Update("A")]);

        _cli.Verify(c => c.RunTestAsync(It.IsAny<string>(), "Category=Fast"), Times.Once);
    }

    [Fact]
    public async Task VerifyAsync_BlankTestFilter_IsTreatedAsNoFilter()
    {
        SetupRestoreSuccess();
        SetupTestSuccess();

        await CreateRunner("   ").VerifyAsync([Update("A")]);

        _cli.Verify(c => c.RunTestAsync(It.IsAny<string>(), null), Times.Once);
    }

    [Fact]
    public void BuildCacheKey_EmptySubset_IsDistinctFromAnyPackageSet()
    {
        DotNetVerificationRunner.BuildCacheKey([])
            .Should().NotBe(DotNetVerificationRunner.BuildCacheKey([Update("A")]));
    }

    [Theory]
    [InlineData(null, "test \"S.sln\" --no-restore")]
    [InlineData("", "test \"S.sln\" --no-restore")]
    [InlineData("Category=Fast", "test \"S.sln\" --no-restore --filter \"Category=Fast\"")]
    public void BuildTestArguments_AppendsFilterOnlyWhenPresent(string? filter, string expected)
    {
        DotNetCliService.BuildTestArguments("S.sln", filter).Should().Be(expected);
    }

    [Fact]
    public void BuildTestArguments_EscapesQuotesInFilter()
    {
        DotNetCliService.BuildTestArguments("S.sln", "Name~\"x\"")
            .Should().Be("test \"S.sln\" --no-restore --filter \"Name~\\\"x\\\"\"");
    }

    private DotNetVerificationRunner CreateRunner(string? testFilter = null) =>
        new(_cli.Object, _console, "S.sln", testFilter);

    private void SetupRestoreSuccess() =>
        _cli.Setup(c => c.RunRestoreAsync(It.IsAny<string>())).ReturnsAsync((string.Empty, true));

    private void SetupTestSuccess() =>
        _cli.Setup(c => c.RunTestAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((string.Empty, true));

    private static PackageUpdateEntry Update(string name) => new(name, "1.0.0", "2.0.0", false, true);
}

public class AllOrNothingSearchStrategyTests
{
    [Fact]
    public async Task SearchAsync_Passes_KeepsEverything()
    {
        var candidates = new List<PackageUpdateEntry> { Update("A"), Update("B") };
        var transaction = new RecordingUpdateTransaction();
        var runner = new ScriptedVerificationRunner(_ => true);

        var result = await new AllOrNothingSearchStrategy().SearchAsync(candidates, transaction, runner);

        result.Applied.Should().BeEquivalentTo(candidates);
        result.HeldBack.Should().BeEmpty();
        transaction.RevertCount.Should().Be(0);
    }

    [Fact]
    public async Task SearchAsync_Fails_RevertsAndHoldsEverythingBack()
    {
        var candidates = new List<PackageUpdateEntry> { Update("A"), Update("B") };
        var transaction = new RecordingUpdateTransaction();
        var runner = new ScriptedVerificationRunner(_ => false);

        var result = await new AllOrNothingSearchStrategy().SearchAsync(candidates, transaction, runner);

        result.Applied.Should().BeEmpty();
        result.HeldBack.Should().HaveCount(2);
        result.HeldBack.Should().OnlyContain(u => u.HeldBack);
        result.FailureOutput.Should().Be("scripted failure");
        transaction.RevertCount.Should().Be(1);
        // One run only: the whole point of the non-bisect path is that it never probes subsets.
        result.VerificationRuns.Should().Be(1);
    }

    private static PackageUpdateEntry Update(string name) => new(name, "1.0.0", "2.0.0", false, true);
}
