using System.Collections.Concurrent;

namespace CPMigrate.Services;

/// <summary>
/// Serialises work against a single project directory, across the whole process.
///
/// <c>dotnet package list</c> restores, and the assets file it writes lives at
/// <c>obj/project.assets.json</c> relative to the <em>project</em> directory — so two projects in one
/// directory share it. Running those at the same time races on that file, and the loser comes back
/// reporting the other project's packages: two projects with different versions of a package report the
/// same one, and the version-inconsistency finding disappears. Cleanly, with a successful exit code.
///
/// Process-wide rather than per-solution, because <c>--batch-parallel</c> hands several solutions to
/// separate scans at once and two of them can reference projects in the same directory.
/// <see cref="ScanConcurrencyGate"/> bounds the total number of queries; it does not stop two of them
/// being aimed at the same directory.
/// </summary>
internal static class ProjectDirectoryLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>Waits for exclusive access to a project's directory, returning a handle to release it.</summary>
    public static async Task<IDisposable> AcquireAsync(string projectPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? string.Empty;
        var gate = Locks.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();

        return new Holder(gate);
    }

    private sealed class Holder(SemaphoreSlim gate) : IDisposable
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
