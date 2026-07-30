using System.Collections.Concurrent;
using System.Xml;
using System.Xml.Linq;

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

    /// <summary>
    /// Waits for exclusive access to wherever this project's assets file lives, returning a handle to
    /// release it.
    /// </summary>
    public static async Task<IDisposable> AcquireAsync(string projectPath)
    {
        var gate = Locks.GetOrAdd(AssetsKey(projectPath), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();

        return new Holder(gate);
    }

    /// <summary>
    /// Where the project's assets file will be written, as far as can be told cheaply.
    ///
    /// The project directory in the ordinary case, since <c>obj/</c> hangs off it. A project that sets
    /// <c>BaseIntermediateOutputPath</c> or <c>MSBuildProjectExtensionsPath</c> in its own file redirects
    /// that, and two projects in *different* directories pointed at the same one share an assets file just
    /// as surely as two in one directory do — so the declared value is what the lock keys on.
    ///
    /// <para><b>Known limit.</b> Read with <see cref="XDocument"/>, not MSBuild: this runs in the concurrent
    /// phase, and MSBuild's object model is exactly what may not run there. So a value set in an imported
    /// <c>Directory.Build.props</c>, or built from MSBuild properties, is not seen — the key falls back to
    /// the project directory and two such projects could still race. Closing that would mean evaluating the
    /// project, which is the thing this phase exists to avoid. Stated rather than implied, because a lock
    /// that looks total and is not is worse than one with a documented edge.</para>
    /// </summary>
    private static string AssetsKey(string projectPath)
    {
        var fullPath = Path.GetFullPath(projectPath);
        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;

        try
        {
            var declared = XDocument
                .Load(fullPath)
                .Descendants()
                .Where(element =>
                    element.Name.LocalName is "BaseIntermediateOutputPath"
                        or "MSBuildProjectExtensionsPath"
                )
                .Select(element => element.Value.Trim())
                .FirstOrDefault(value =>
                    !string.IsNullOrEmpty(value) && !value.Contains("$(", StringComparison.Ordinal)
                );

            if (declared is not null)
            {
                return Path.GetFullPath(declared, directory);
            }
        }
        catch (Exception exception) when (exception is IOException or XmlException)
        {
            // Unreadable or malformed. The project directory is the right answer for every project that
            // does not redirect its intermediate output, which is nearly all of them.
        }

        return directory;
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
