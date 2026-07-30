using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// The scan concurrency cap protects an external resource — the NuGet feed, which rate-limits — so it
/// has to be a process-wide budget. Per-scan limits would multiply by the number of solutions under
/// <c>--batch-parallel</c>, producing exactly the throttling the cap exists to avoid.
/// </summary>
[Collection("Sequential")]
public class ScanConcurrencyGateTests : IDisposable
{
    public ScanConcurrencyGateTests()
    {
        ScanConcurrencyGate.ResetForTests();
    }

    public void Dispose()
    {
        ScanConcurrencyGate.ResetForTests();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AcquireAsync_NeverExceedsTheCeiling_EvenAcrossIndependentScans()
    {
        // Two "solutions" each asking for 3 slots must still total 3, not 6.
        const int ceiling = 3;
        var concurrent = 0;
        var peak = 0;
        var sync = new Lock();

        async Task ScanAsync()
        {
            using var slot = await ScanConcurrencyGate.AcquireAsync(ceiling);

            lock (sync)
            {
                concurrent++;
                peak = Math.Max(peak, concurrent);
            }

            await Task.Delay(15);

            lock (sync)
            {
                concurrent--;
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => ScanAsync()));

        peak.Should().BeLessThanOrEqualTo(ceiling);
        peak.Should().BeGreaterThan(1, "the gate must still allow real concurrency");
    }

    [Fact]
    public async Task AcquireAsync_SizesOnceAndIgnoresALaterLargerRequest()
    {
        // A budget that grew mid-run would not be a budget.
        using (await ScanConcurrencyGate.AcquireAsync(2))
        {
            ScanConcurrencyGate.Permits.Should().Be(2);
        }

        using (await ScanConcurrencyGate.AcquireAsync(64))
        {
            ScanConcurrencyGate.Permits.Should().Be(2);
        }
    }

    [Fact]
    public async Task AcquireAsync_TreatsANonPositiveCeilingAsSerial()
    {
        using var slot = await ScanConcurrencyGate.AcquireAsync(0);

        ScanConcurrencyGate.Permits.Should().Be(1);
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        // Releasing twice would widen the budget permanently.
        var slot = await ScanConcurrencyGate.AcquireAsync(1);
        slot.Dispose();
        slot.Dispose();

        using var next = await ScanConcurrencyGate.AcquireAsync(1);
        ScanConcurrencyGate.Permits.Should().Be(1);
    }
}
