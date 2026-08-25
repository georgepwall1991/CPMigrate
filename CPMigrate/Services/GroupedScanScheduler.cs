namespace CPMigrate.Services;

/// <summary>
/// Schedules one scan's per-project subprocess work, and is the single implementation of the
/// scheduling contract every scan shares.
///
/// <para>
/// <c>dotnet package list</c> restores, and its assets file goes in the project's <c>obj</c>. Two
/// projects in one directory therefore share a <c>project.assets.json</c> and querying them at once
/// corrupts both — the loser reports the other project's packages, so two projects with different
/// versions report the same one and a version-inconsistency finding disappears with a clean exit code.
/// Projects are therefore grouped by directory and run in sequence within their group, while distinct
/// directories run against each other. A solution that redirects its intermediate output somewhere the
/// paths cannot see runs serially under the process-wide redirect lock, because the sharing is not
/// visible in the paths. The global <see cref="ScanConcurrencyGate"/> keeps every scan in the process
/// inside the advertised cap, which <c>--batch-parallel</c> turns into a per-process budget rather
/// than a per-solution one.
/// </para>
///
/// <para>
/// Grouping rather than a per-directory lock is deliberate: a lock would let several projects from one
/// directory start, all but one waiting on the semaphore while still holding a worker and a global
/// scan slot, so crowded directories starved unrelated ones close to serial. With grouping nothing ever
/// waits for a directory — only one project from it is ever in flight.
/// </para>
///
/// <para>
/// Results are indexed by discovery position regardless of completion order,
/// so a merge can replay discovery order and a report cannot depend on which scan won the race. Tests
/// drive this class directly with counting fakes: peak observed concurrency must stay within the cap
/// and above one for a multi-directory fixture, and two same-directory projects must never overlap in
/// time.
/// </para>
/// </summary>
internal static class GroupedScanScheduler
{
    /// <summary>
    /// Runs <paramref name="scanProject"/> once per path under the shared scheduling contract,
    /// returning results in discovery order.
    /// </summary>
    /// <param name="projectPaths">Project file paths, in the order they were discovered.</param>
    /// <param name="maxConcurrency">Process-wide ceiling handed to <see cref="ScanConcurrencyGate"/>.</param>
    /// <param name="scanProject">
    /// The per-project work, given its discovery index and path. Called exactly once per path.
    /// </param>
    public static async Task<TResult[]> RunAsync<TResult>(
        IReadOnlyList<string> projectPaths,
        int maxConcurrency,
        Func<int, string, CancellationToken, Task<TResult>> scanProject
    )
    {
        var results = new TResult[projectPaths.Count];

        var groups = Enumerable
            .Range(0, projectPaths.Count)
            .GroupBy(
                index => ProjectDirectoryScanLock.DirectoryKeyFor(projectPaths[index]),
                StringComparer.OrdinalIgnoreCase
            )
            .ToList();

        // A solution that redirects intermediate output somewhere shared cannot be grouped by directory
        // at all, because the sharing is not visible in the paths. Those run one project at a time and,
        // since --batch-parallel has other solutions running too, hold a process-wide lock while they
        // do. Ordinary scans take the shared side of that lock; a redirecting scan takes it exclusively.
        var mightRedirect = ProjectDirectoryScanLock.MightRedirectIntermediateOutput(projectPaths);
        using var redirectLock = mightRedirect
            ? await ProjectDirectoryScanLock.AcquireRedirectingAsync()
            : await ProjectDirectoryScanLock.AcquireOrdinaryAsync();

        await Parallel.ForEachAsync(
            groups,
            new ParallelOptions { MaxDegreeOfParallelism = mightRedirect ? 1 : maxConcurrency },
            async (group, cancellationToken) =>
            {
                foreach (var index in group)
                {
                    using var slot = await ScanConcurrencyGate.AcquireAsync(maxConcurrency);

                    // Grouping keeps one solution's same-directory projects in sequence; this keeps them
                    // in sequence against other solutions', which --batch-parallel runs at the same time.
                    using var directorySlot = await ProjectDirectoryScanLock.AcquireDirectoryAsync(
                        projectPaths[index]
                    );

                    results[index] = await scanProject(
                        index,
                        projectPaths[index],
                        cancellationToken
                    );
                }
            }
        );

        return results;
    }
}
