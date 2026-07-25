using CPMigrate.Models;
using CPMigrate.Services.Update;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests.Services.Update;

public class BisectSearchStrategyTests
{
    private readonly FakeConsoleService _console = new();

    [Fact]
    public async Task SearchAsync_AllUpdatesGood_AppliesEverythingInOneRun()
    {
        var candidates = Candidates("A", "B", "C", "D");
        var transaction = new RecordingUpdateTransaction();
        var runner = new ScriptedVerificationRunner(_ => true);

        var result = await new BisectSearchStrategy(_console).SearchAsync(candidates, transaction, runner);

        result.Applied.Should().BeEquivalentTo(candidates);
        result.HeldBack.Should().BeEmpty();
        result.AllApplied.Should().BeTrue();
        // The happy path must not pay any bisection overhead.
        result.VerificationRuns.Should().Be(1);
    }

    [Fact]
    public async Task SearchAsync_SingleCulprit_HoldsBackOnlyThatPackage()
    {
        var candidates = Candidates("A", "B", "C", "D", "E", "F", "G", "H");
        var transaction = new RecordingUpdateTransaction();
        var runner = new ScriptedVerificationRunner(subset => !ContainsPackage(subset, "F"));

        var result = await new BisectSearchStrategy(_console).SearchAsync(candidates, transaction, runner);

        Names(result.HeldBack).Should().BeEquivalentTo(["F"]);
        Names(result.Applied).Should().BeEquivalentTo(["A", "B", "C", "D", "E", "G", "H"]);
        result.BudgetExhausted.Should().BeFalse();
        transaction.LastAppliedNames.Should().BeEquivalentTo(Names(result.Applied));
    }

    [Fact]
    public async Task SearchAsync_InteractingPair_KeepsRemainderGreen()
    {
        // C and G are only fatal together — the failure mode plain binary search cannot model.
        var candidates = Candidates("A", "B", "C", "D", "E", "F", "G", "H");
        var transaction = new RecordingUpdateTransaction();
        var runner = new ScriptedVerificationRunner(
            subset => !(ContainsPackage(subset, "C") && ContainsPackage(subset, "G")));

        var result = await new BisectSearchStrategy(_console, budget: 32)
            .SearchAsync(candidates, transaction, runner);

        // Exactly one of the interacting pair must be dropped, and everything else kept.
        result.HeldBack.Should().HaveCount(1);
        Names(result.HeldBack).Single().Should().BeOneOf("C", "G");
        result.Applied.Should().HaveCount(7);

        // Whatever it settled on must genuinely verify.
        var finalCheck = await runner.VerifyAsync(result.Applied);
        finalCheck.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task SearchAsync_EveryUpdateBroken_HoldsAllBackAndRevertsFile()
    {
        var candidates = Candidates("A", "B", "C", "D");
        var transaction = new RecordingUpdateTransaction();
        // Only the empty baseline passes.
        var runner = new ScriptedVerificationRunner(subset => subset.Count == 0);

        var result = await new BisectSearchStrategy(_console, budget: 32)
            .SearchAsync(candidates, transaction, runner);

        result.Applied.Should().BeEmpty();
        Names(result.HeldBack).Should().BeEquivalentTo(["A", "B", "C", "D"]);
        result.BaselineBroken.Should().BeFalse();
        transaction.LastAppliedNames.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_BaselineAlreadyFailing_ReportsBrokenBaseline()
    {
        var candidates = Candidates("A", "B");
        var transaction = new RecordingUpdateTransaction();
        var runner = new ScriptedVerificationRunner(_ => false);

        var result = await new BisectSearchStrategy(_console, budget: 32)
            .SearchAsync(candidates, transaction, runner);

        result.BaselineBroken.Should().BeTrue();
        result.Applied.Should().BeEmpty();
        _console.OutputMessages
            .Should().Contain(m => m.Contains("zero updates", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchAsync_BudgetExhausted_KeepsBankedProgressAndFlagsIt()
    {
        var candidates = Candidates("A", "B", "C", "D", "E", "F", "G", "H");
        var transaction = new RecordingUpdateTransaction();
        var runner = new ScriptedVerificationRunner(subset => !ContainsPackage(subset, "H"));

        var result = await new BisectSearchStrategy(_console, budget: 2)
            .SearchAsync(candidates, transaction, runner);

        result.BudgetExhausted.Should().BeTrue();
        result.VerificationRuns.Should().BeLessThanOrEqualTo(2);
        // Run 1 rejects the whole set, run 2 banks the first half; the rest is held back unresolved.
        result.Applied.Should().NotBeEmpty();
        result.HeldBack.Should().NotBeEmpty();
        (result.Applied.Count + result.HeldBack.Count).Should().Be(candidates.Count);
        transaction.LastAppliedNames.Should().BeEquivalentTo(Names(result.Applied));
    }

    [Fact]
    public async Task SearchAsync_NoCandidates_DoesNotVerify()
    {
        var transaction = new RecordingUpdateTransaction();
        var runner = new ScriptedVerificationRunner(_ => true);

        var result = await new BisectSearchStrategy(_console).SearchAsync([], transaction, runner);

        result.Applied.Should().BeEmpty();
        result.HeldBack.Should().BeEmpty();
        result.VerificationRuns.Should().Be(0);
    }

    [Fact]
    public async Task SearchAsync_LeavesFileInTheStateItReports()
    {
        var candidates = Candidates("A", "B", "C", "D", "E", "F");
        var transaction = new RecordingUpdateTransaction();
        var runner = new ScriptedVerificationRunner(
            subset => !ContainsPackage(subset, "B") && !ContainsPackage(subset, "E"));

        var result = await new BisectSearchStrategy(_console, budget: 32)
            .SearchAsync(candidates, transaction, runner);

        // The contract every caller depends on: what is on disk equals what was reported as applied.
        transaction.LastAppliedNames.Should().BeEquivalentTo(Names(result.Applied));
        Names(result.HeldBack).Should().BeEquivalentTo(["B", "E"]);
    }

    private static List<PackageUpdateEntry> Candidates(params string[] names) =>
        names.Select(n => new PackageUpdateEntry(n, "1.0.0", "2.0.0", false, true)).ToList();

    private static bool ContainsPackage(IReadOnlyCollection<PackageUpdateEntry> subset, string name) =>
        subset.Any(u => u.PackageName == name);

    private static IEnumerable<string> Names(IEnumerable<PackageUpdateEntry> entries) =>
        entries.Select(e => e.PackageName);
}
