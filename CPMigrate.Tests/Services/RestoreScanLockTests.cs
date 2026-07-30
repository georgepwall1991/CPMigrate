using System.Diagnostics;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Covers the coordination that stops an un-isolated restore running alongside another one.
///
/// The exclusive mode drains a counting semaphore, which is not an atomic operation — two callers draining
/// it at once each take a subset of the permits and then wait forever for the ones the other holds. That is
/// a deadlock, and a worse outcome than the corruption the lock exists to prevent, because a hung scan never
/// finishes at all. It reached review before being caught, which is the argument for testing the primitive
/// directly rather than only through a scan.
/// </summary>
public class RestoreScanLockTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task TwoExclusiveAcquisitionsDoNotDeadlock()
    {
        // The shape that deadlocks: two callers draining permits at the same time.
        //
        // Verified against the unguarded version — but note this one does *not* reliably reproduce it, since
        // with only two contenders the first often drains all the permits before the second starts.
        // ManyExclusiveAcquisitionsAllComplete is the one that failed every time. Kept because it states the
        // property at its simplest; not relied on as the guard.
        var first = Task.Run(async () =>
        {
            using var held = await RestoreScanLock.AcquireExclusiveAsync();
            await Task.Delay(20);
        });

        var second = Task.Run(async () =>
        {
            using var held = await RestoreScanLock.AcquireExclusiveAsync();
            await Task.Delay(20);
        });

        var completed = await Task.WhenAny(Task.WhenAll(first, second), Task.Delay(Generous));

        completed
            .Should()
            .NotBeOfType<Task>(
                "a timeout here means the two exclusive acquisitions are waiting on each other"
            );
        first.IsCompletedSuccessfully.Should().BeTrue();
        second.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task ManyExclusiveAcquisitionsAllComplete()
    {
        // The reliable reproduction: with enough contenders they interleave, each takes a subset of the
        // permits, and none can ever finish. Confirmed by running it against the unguarded version, where it
        // hung until the timeout every time — which is what makes this test the real guard rather than a
        // description of one.
        var contenders = Enumerable
            .Range(0, 8)
            .Select(_ =>
                Task.Run(async () =>
                {
                    using var held = await RestoreScanLock.AcquireExclusiveAsync();
                    await Task.Delay(5);
                })
            )
            .ToArray();

        var all = Task.WhenAll(contenders);
        var completed = await Task.WhenAny(all, Task.Delay(Generous));

        completed.Should().Be(all, "every exclusive acquisition must eventually be granted");
    }

    [Fact]
    public async Task AnExclusiveHolderExcludesSharedHolders()
    {
        // The property the lock exists for: a retake waits for every other restore in the process.
        using var shared = await RestoreScanLock.AcquireSharedAsync();

        var exclusive = Task.Run(async () =>
        {
            using var held = await RestoreScanLock.AcquireExclusiveAsync();
        });

        var raced = await Task.WhenAny(exclusive, Task.Delay(200));
        raced.Should().NotBe(exclusive, "the shared holder must still be blocking it");

        shared.Dispose();

        var completed = await Task.WhenAny(exclusive, Task.Delay(Generous));
        completed.Should().Be(exclusive, "releasing the shared holder must let it through");
    }

    [Fact]
    public async Task SharedHoldersRunConcurrently()
    {
        // The other half: the ordinary path must not be serialised by this.
        var started = 0;
        var release = new TaskCompletionSource();

        var holders = Enumerable
            .Range(0, 4)
            .Select(_ =>
                Task.Run(async () =>
                {
                    using var held = await RestoreScanLock.AcquireSharedAsync();
                    Interlocked.Increment(ref started);
                    await release.Task;
                })
            )
            .ToArray();

        var stopwatch = Stopwatch.StartNew();
        while (Volatile.Read(ref started) < 4 && stopwatch.Elapsed < Generous)
        {
            await Task.Delay(10);
        }

        Volatile
            .Read(ref started)
            .Should()
            .Be(4, "shared holders are the common path and must not exclude each other");

        release.SetResult();
        await Task.WhenAll(holders);
    }
}
