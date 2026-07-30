namespace CPMigrate.Services;

/// <summary>
/// Bounds how many package queries run at once across the whole process.
///
/// The limit exists to protect something external — the NuGet feed, which rate-limits — so it has to
/// be a process-wide budget rather than a per-scan one. <c>--batch-parallel</c> processes several
/// solutions concurrently, and each solution's scan asking for its own N queries would multiply the
/// advertised cap by the number of solutions, producing exactly the rate-limiting the option is meant
/// to avoid.
/// </summary>
internal static class ScanConcurrencyGate
{
    private static readonly Lock InitLock = new();
    private static SemaphoreSlim? _gate;
    private static int _permits;

    /// <summary>
    /// The concurrency currently in force, or 0 before the gate has been sized. Exposed for tests.
    /// </summary>
    internal static int Permits => Volatile.Read(ref _permits);

    /// <summary>
    /// Waits for a slot, returning a handle to release it.
    ///
    /// The first caller sizes the gate. Later callers asking for a different limit do not resize it:
    /// a budget that shrank and grew mid-run would not be a budget, and within one CLI invocation the
    /// value is the same everywhere anyway — batch clones inherit it from the parent options.
    /// </summary>
    /// <param name="maxConcurrency">Desired ceiling, used only if the gate is not yet sized.</param>
    public static async Task<IDisposable> AcquireAsync(int maxConcurrency)
    {
        var gate = GetOrCreate(Math.Max(1, maxConcurrency));
        await gate.WaitAsync();

        return new Slot(gate);
    }

    /// <summary>
    /// Resets the gate. Tests only: production sizes it once per process, and a reset mid-run would
    /// silently widen the budget.
    /// </summary>
    internal static void ResetForTests()
    {
        lock (InitLock)
        {
            _gate?.Dispose();
            _gate = null;
            Volatile.Write(ref _permits, 0);
        }
    }

    private static SemaphoreSlim GetOrCreate(int maxConcurrency)
    {
        var existing = Volatile.Read(ref _gate);
        if (existing is not null)
        {
            return existing;
        }

        lock (InitLock)
        {
            if (_gate is null)
            {
                Volatile.Write(ref _permits, maxConcurrency);
                Volatile.Write(ref _gate, new SemaphoreSlim(maxConcurrency, maxConcurrency));
            }

            return _gate;
        }
    }

    private sealed class Slot(SemaphoreSlim gate) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            gate.Release();
        }
    }
}
