using CPMigrate.Services.Migration;
using FluentAssertions;

namespace CPMigrate.Tests.Services.Migration;

/// <summary>
/// Pins the one property the resolved-package concurrency depends on: it is only ever enabled when each
/// invocation can be given its own MSBuild intermediate directory.
///
/// Without that, two projects sharing a <c>project.assets.json</c> are queried at once, the loser reports
/// the other project's packages, and a version-inconsistency finding disappears with a successful exit code.
///
/// This exists because the first version of the change only *claimed* the property — in a comment on the
/// method that creates the directory — while the caller went on running concurrently whether or not the
/// directory had been created. A comment asserting a safety property the code does not implement is the
/// exact failure this release series has spent its time removing, so the property is asserted here instead.
/// </summary>
public class ScanConcurrencySafetyTests
{
    [Theory]
    [InlineData(8, "/tmp/isolation-root", 8)]
    [InlineData(4, "/tmp/isolation-root", 4)]
    [InlineData(2, "/tmp/isolation-root", 2)]
    public void ConcurrencyIsHonoured_WhenIsolationIsAvailable(
        int requested,
        string isolationRoot,
        int expected
    )
    {
        AnalysisHandler.ResolveSafeConcurrency(requested, isolationRoot).Should().Be(expected);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(4)]
    [InlineData(2)]
    public void ConcurrencyDropsToOne_WhenIsolationCouldNotBeCreated(int requested)
    {
        AnalysisHandler
            .ResolveSafeConcurrency(requested, isolationRoot: null)
            .Should()
            .Be(
                1,
                "a concurrent scan with no isolation is the collision the isolation exists to prevent"
            );
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(1, "/tmp/isolation-root")]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    public void ASerialRequestStaysSerial_AndNeedsNoIsolation(int requested, string? isolationRoot)
    {
        // One at a time cannot collide, so isolation is irrelevant — and a nonsensical request must not
        // become concurrency by accident.
        AnalysisHandler.ResolveSafeConcurrency(requested, isolationRoot).Should().Be(1);
    }
}
