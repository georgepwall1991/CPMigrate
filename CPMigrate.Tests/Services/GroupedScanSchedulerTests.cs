using System.Collections.Concurrent;
using System.Diagnostics;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Pins the scheduling contract of <see cref="GroupedScanScheduler"/> with counting fakes instead of
/// wall-clock comparisons, because a timing assertion in CI is a flake generator while the failures
/// this guards are structural: a cap that leaks lets parallel scans multiply past what the NuGet feed
/// tolerates, and a directory group that stops serialising reintroduces the shared-assets-file race
/// that made two same-directory projects report one version and silently drop a finding.
///
/// <para>
/// The fakes never shell out, so these run in milliseconds and hold on any machine: peak observed
/// concurrency must stay within the requested cap and above one for a multi-directory fixture, two
/// same-directory projects must never overlap in time, and results must come back indexed by
/// discovery position rather than completion order.
/// </para>
/// </summary>
[Collection("Sequential")]
public class GroupedScanSchedulerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"CPMigrateScheduler_{Guid.NewGuid():N}"
    );

    public void Dispose()
    {
        ScanConcurrencyGate.ResetForTests();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task MultiDirectoryFixture_PeakConcurrencyStaysWithinTheCap_AndReachesAtLeastTwo()
    {
        ScanConcurrencyGate.ResetForTests();
        const int ceiling = 3;
        var paths = DistinctDirectoryPaths(projectCount: 8);
        var tracker = new ConcurrencyTracker();

        var results = await GroupedScanScheduler.RunAsync(
            paths,
            ceiling,
            async (index, _, _) =>
            {
                using var _ = tracker.Enter();
                // Long enough that three slots genuinely overlap on a loaded CI runner.
                await Task.Delay(50);
                return index;
            }
        );

        tracker.Peak.Should().BeLessThanOrEqualTo(ceiling, "the advertised cap must hold");
        tracker.Peak.Should().BeGreaterThanOrEqualTo(2, "distinct directories must scan together");
        results.Should().Equal(0, 1, 2, 3, 4, 5, 6, 7);
    }

    [Fact]
    public async Task ProjectsSharingADirectory_NeverOverlapInTime()
    {
        ScanConcurrencyGate.ResetForTests();
        const int ceiling = 4;
        var sharedDirectory = Directory.CreateDirectory(
            Path.Combine(_root, "shared")
        ).FullName;
        var paths = new List<string>
        {
            Path.Combine(sharedDirectory, "First.csproj"),
            Path.Combine(sharedDirectory, "Second.csproj"),
            // Unrelated directories prove the group did not drag the whole scan down to serial.
            Path.Combine(_root, "other-a", "OtherA.csproj"),
            Path.Combine(_root, "other-b", "OtherB.csproj"),
        };
        var windows = new ConcurrentDictionary<string, (long Start, long End)>();

        await GroupedScanScheduler.RunAsync(
            paths,
            ceiling,
            async (_, projectPath, _) =>
            {
                var start = Stopwatch.GetTimestamp();
                await Task.Delay(40);
                windows[projectPath] = (start, Stopwatch.GetTimestamp());
                return true;
            }
        );

        var first = windows[paths[0]];
        var second = windows[paths[1]];

        var serialized = first.End <= second.Start || second.End <= first.Start;
        serialized.Should().BeTrue(
            "two projects in one directory share obj/project.assets.json and must take turns"
        );
    }

    [Fact]
    public async Task ResultsComeBackInDiscoveryOrder_EvenWhenLaterProjectsFinishFirst()
    {
        ScanConcurrencyGate.ResetForTests();
        var paths = DistinctDirectoryPaths(projectCount: 6);

        var results = await GroupedScanScheduler.RunAsync(
            paths,
            6,
            async (index, _, _) =>
            {
                // Reverse the delays so completion order is the opposite of discovery order: a merge
                // keyed on completion would produce a report that differs between runs.
                await Task.Delay((paths.Count - index) * 10);
                return paths[index];
            }
        );

        results.Should().Equal(paths);
    }

    [Fact]
    public async Task EveryProjectIsScannedExactlyOnce()
    {
        ScanConcurrencyGate.ResetForTests();
        var paths = DistinctDirectoryPaths(projectCount: 5);
        var counts = new ConcurrentDictionary<string, int>();

        await GroupedScanScheduler.RunAsync(
            paths,
            3,
            (_, projectPath, _) =>
            {
                counts.AddOrUpdate(projectPath, 1, (_, count) => count + 1);
                return Task.FromResult(true);
            }
        );

        counts.Values.Should().OnlyContain(count => count == 1);
        counts.Should().HaveCount(paths.Count);
    }

    /// <summary>
    /// Fake absolute project paths in distinct directories. No files are created: the redirect
    /// heuristic reads nothing for paths that do not exist and answers "ordinary", which is the
    /// layout under test.
    /// </summary>
    private List<string> DistinctDirectoryPaths(int projectCount)
    {
        return Enumerable
            .Range(0, projectCount)
            .Select(i => Path.Combine(_root, $"dir-{i}", $"Project{i}.csproj"))
            .ToList();
    }

    private sealed class ConcurrencyTracker
    {
        private readonly Lock _lock = new();
        private int _current;
        private int _peak;

        public int Peak => Volatile.Read(ref _peak);

        public IDisposable Enter()
        {
            lock (_lock)
            {
                _current++;
                _peak = Math.Max(_peak, _current);
            }

            return new Scope(this);
        }

        private sealed class Scope(ConcurrencyTracker tracker) : IDisposable
        {
            public void Dispose()
            {
                lock (tracker._lock)
                {
                    tracker._current--;
                }
            }
        }
    }
}
