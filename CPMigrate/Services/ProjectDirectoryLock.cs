using System.Collections.Concurrent;
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
    /// Waits for exclusive access to every location this project's assets file might be written to,
    /// returning a handle that releases them.
    ///
    /// <para><b>Every location, not the most likely one.</b> Which path is in force depends on MSBuild
    /// conditions, on imported <c>Directory.Build.props</c>, and on properties this phase cannot evaluate —
    /// evaluating them is precisely what it must not do. Picking one candidate therefore means guessing, and
    /// a wrong guess splits a lock that should have been shared: two projects that do write to the same file
    /// get separate locks, race, and one comes back reporting the other's packages. So both candidates are
    /// locked — the project directory, and any intermediate path the file declares — and the cost of being
    /// wrong is that two projects are serialised when they need not have been. Slower is a strictly better
    /// error than silently fewer findings.</para>
    ///
    /// <para>Acquired in a fixed order so two callers holding overlapping candidates cannot deadlock.</para>
    /// </summary>
    public static async Task<IDisposable> AcquireAsync(string projectPath)
    {
        var keys = AssetsKeys(projectPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<SemaphoreSlim> held = [];

        foreach (var key in keys)
        {
            var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            held.Add(gate);
        }

        return new Holder(held);
    }

    /// <summary>
    /// Every place this project's assets file could plausibly land: its own directory, plus any
    /// intermediate path the project file declares, conditional or not.
    /// </summary>
    private static IEnumerable<string> AssetsKeys(string projectPath)
    {
        var fullPath = Path.GetFullPath(projectPath);
        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;

        // Always. A declared path may be conditional and not apply, in which case obj/ under here is where
        // the file goes.
        yield return directory;

        List<string> declared;
        try
        {
            declared = XDocument
                .Load(fullPath)
                .Descendants()
                .Where(element =>
                    element.Name.LocalName
                        is "MSBuildProjectExtensionsPath"
                            or "BaseIntermediateOutputPath"
                )
                .Select(element => element.Value.Trim())
                .Where(value =>
                    !string.IsNullOrEmpty(value) && !value.Contains("$(", StringComparison.Ordinal)
                )
                .ToList();
        }
        catch (Exception)
        {
            // Every failure, deliberately: unreadable, unauthorised, malformed, anything. This runs before
            // the project is scanned, so throwing would abort the whole analysis over one project the
            // scanners are equipped to report as an incomplete scan and carry on past.
            yield break;
        }

        foreach (var value in declared)
        {
            yield return Path.GetFullPath(value, directory);
        }
    }

    private sealed class Holder(List<SemaphoreSlim> gates) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;

            foreach (var gate in gates)
            {
                gate.Release();
            }
        }
    }
}
