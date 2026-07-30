namespace CPMigrate.Services;

/// <summary>
/// Coordinates restores across the whole process so that an un-isolated one never runs alongside another.
///
/// <para><b>The problem.</b> <c>dotnet package list</c> restores, and two restores writing the same
/// <c>project.assets.json</c> corrupt each other: the loser reports the other project's packages, so two
/// projects with different versions of a package report the same one and a version-inconsistency finding
/// disappears with a clean exit code. Each invocation is given its own intermediate directory to prevent
/// that, but the redirection is passed as environment variables and a project can override it — so some
/// restores escape their isolation, and which ones is only knowable afterwards.</para>
///
/// <para><b>Why two modes.</b> A scan runs its projects concurrently, then checks which ones escaped and
/// takes those again. The retake must not overlap <em>any</em> other restore, because an escaped restore
/// writes somewhere shared and <c>--batch-parallel</c> has other solutions' scans running at the same time.
/// So a retake is <see cref="AcquireExclusiveAsync"/> and everything else is
/// <see cref="AcquireSharedAsync"/>.</para>
///
/// <para><b>Why the shared mode is enough for the first pass.</b> Two escaped restores in different scans
/// can still collide there — and it does not matter, because an escaped result is always discarded and
/// retaken under exclusivity. The first pass never has to be correct for the projects that escape; it only
/// has to not corrupt the ones that did not, and an isolated restore cannot be corrupted by anything.</para>
/// </summary>
internal static class RestoreScanLock
{
    /// <summary>
    /// Permits, sized so an exclusive holder can drain every plausible concurrent scan. Far above any
    /// reachable concurrency: <c>--max-parallelism</c> is capped well below this, and the process-wide
    /// <see cref="ScanConcurrencyGate"/> caps the total independently.
    /// </summary>
    private const int Permits = 1024;

    private static readonly SemaphoreSlim Gate = new(Permits, Permits);

    /// <summary>
    /// Held for the whole of an exclusive acquisition, so only one caller is ever draining permits.
    ///
    /// Without it, two callers each take a subset of the permits and then wait forever for the ones the
    /// other is holding — a deadlock, and a worse outcome than the corruption this lock exists to prevent,
    /// because a hung scan never finishes at all. Draining a counting semaphore is not atomic; this makes
    /// the attempt to drain it exclusive instead.
    /// </summary>
    private static readonly SemaphoreSlim ExclusiveEntry = new(1, 1);

    /// <summary>Taken by a restore that is isolated, or whose result will be discarded if it is not.</summary>
    public static async Task<IDisposable> AcquireSharedAsync()
    {
        await Gate.WaitAsync();

        return new Holder(1);
    }

    /// <summary>
    /// Taken by a restore that is known not to be isolated, and whose result has to be trusted. Waits for
    /// every other restore in the process to finish first.
    /// </summary>
    public static async Task<IDisposable> AcquireExclusiveAsync()
    {
        await ExclusiveEntry.WaitAsync();

        try
        {
            for (var i = 0; i < Permits; i++)
            {
                await Gate.WaitAsync();
            }
        }
        catch
        {
            ExclusiveEntry.Release();
            throw;
        }

        return new ExclusiveHolder();
    }

    private sealed class Holder(int permits) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            Gate.Release(permits);
        }
    }

    private sealed class ExclusiveHolder : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            Gate.Release(Permits);
            ExclusiveEntry.Release();
        }
    }
}
