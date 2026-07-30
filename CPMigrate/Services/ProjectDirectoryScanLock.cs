using System.Collections.Concurrent;
using System.Xml.Linq;

namespace CPMigrate.Services;

/// <summary>
/// Serialises restores for projects that live in the same directory, and only those.
///
/// <para><b>The collision.</b> <c>dotnet package list</c> restores, and its assets file goes in the
/// project's <c>obj</c>. Two projects in one directory therefore share a <c>project.assets.json</c>, and
/// querying them at once corrupts both — the loser reports the other project's packages, so two projects
/// with different versions report the same one and a version-inconsistency finding disappears with a clean
/// exit code. Verified: two projects in one directory, pinned to 13.0.1 and 12.0.3, both reported 13.0.1.</para>
///
/// <para><b>Why this is keyed on the directory.</b> The obvious alternative — redirecting each invocation to
/// a private intermediate directory — was tried, shipped in 3.26.0, and was wrong. The redirection has to go
/// in as environment variables, since <c>dotnet package list</c> rejects <c>-p:</c> arguments, and an
/// environment property applies to <em>every project in the build graph</em>, not just the one being
/// queried. So restoring a project with a <c>ProjectReference</c> told both it and its referenced projects
/// to write into the same directory, they collided inside a single invocation, and the query returned no
/// frameworks at all. Measured on Serilog: three of six projects came back empty, and it reproduced with no
/// concurrency whatsoever — the isolation alone was enough to break it.</para>
///
/// <para>A project's own directory is a fact, not a prediction, which is what makes this tractable where
/// predicting the assets-file location was not. It handles the collision that has actually been
/// demonstrated. A project that redirects its <c>obj</c> to somewhere another project also uses would still
/// collide, and is not handled here — that is the question 3.24.0 spent eight review rounds failing to
/// answer, it needs full MSBuild evaluation, and it is rarer than the layout this covers.</para>
/// </summary>
internal static class ProjectDirectoryScanLock
{
    /// <summary>
    /// Taken for each project scanned, so that two restores in the same directory never overlap.
    ///
    /// Process-wide, because <c>--batch-parallel</c> runs several solutions at once and two of them can
    /// hold projects in the same directory — or the same project. Grouping within a scan is what keeps this
    /// from blocking: only one project per directory is ever in flight from a given solution, so the wait
    /// here is for another solution, which is rare.
    /// </summary>
    public static async Task<IDisposable> AcquireDirectoryAsync(string projectPath)
    {
        var gate = Directories.GetOrAdd(DirectoryKeyFor(projectPath), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();

        return new Release(gate);
    }

    /// <summary>
    /// The key two projects must share before they take turns: the directory their <c>obj</c> goes in.
    ///
    /// Case-insensitive, because macOS and Windows treat <c>src/Api</c> and <c>src/api</c> as one directory
    /// and two projects the comparison split apart would be exactly the pair that needs sequencing.
    /// </summary>
    public static string DirectoryKeyFor(string projectPath)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? projectPath;
        }
        catch (Exception)
        {
            return projectPath;
        }
    }

    /// <summary>
    /// Taken by an ordinary scan. Excludes scans that might redirect their output, and nothing else.
    /// </summary>
    public static async Task<IDisposable> AcquireOrdinaryAsync()
    {
        await Redirects.WaitAsync();

        return new Release(Redirects, permits: 1);
    }

    /// <summary>
    /// Taken for the whole of a scan whose projects might redirect their intermediate output somewhere
    /// shared.
    ///
    /// Exclusive against every scan in the process, not merely against other redirecting ones — a redirect
    /// aimed at an ordinary project's directory races that project's restore, and directory keys cannot see
    /// it because the sharing is not in the paths. Draining is guarded so two of these cannot each take a
    /// subset of the permits and wait forever on the other.
    /// </summary>
    public static async Task<IDisposable> AcquireRedirectingAsync()
    {
        await RedirectEntry.WaitAsync();

        try
        {
            for (var i = 0; i < OrdinaryPermits; i++)
            {
                await Redirects.WaitAsync();
            }
        }
        catch
        {
            RedirectEntry.Release();
            throw;
        }

        return new Release(Redirects, OrdinaryPermits, RedirectEntry);
    }

    private const int OrdinaryPermits = 1024;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Directories = new(
        StringComparer.OrdinalIgnoreCase
    );

    private static readonly SemaphoreSlim Redirects = new(OrdinaryPermits, OrdinaryPermits);

    private static readonly SemaphoreSlim RedirectEntry = new(1, 1);

    /// <summary>
    /// The properties that can move a project's <c>obj</c> somewhere another project also writes.
    /// </summary>
    private static readonly string[] RedirectingProperties =
    [
        "MSBuildProjectExtensionsPath",
        "BaseIntermediateOutputPath",
        "ProjectAssetsFile",
    ];

    /// <summary>
    /// Whether any of these projects might redirect its intermediate output somewhere shared, in which case
    /// grouping by directory is not enough and the whole scan runs serially.
    ///
    /// <para><b>A heuristic, and deliberately labelled as one.</b> It looks for the three property names in
    /// each project and in the nearest implicitly-imported ancestor files. MSBuild cannot assign a property
    /// without naming it literally, so a file that does redirect will contain the name — but the name can
    /// also arrive through an import this does not follow, a custom SDK's implicit <c>Sdk.props</c>, or a
    /// path built from other properties. Cross-review found each of those against an earlier, more ambitious
    /// version of this check.</para>
    ///
    /// <para>Which is survivable here only because of how it is used. This decides <em>serial or
    /// concurrent</em>, not where a file will be written, and a miss leaves the scan exactly as concurrent
    /// as every release before 3.26.0 was. It buys back the common redirect layouts without claiming to
    /// catch all of them — and it does not, so the limitation is in the changelog rather than implied
    /// away.</para>
    /// </summary>
    public static bool MightRedirectIntermediateOutput(IEnumerable<string> projectPaths)
    {
        HashSet<string> checkedFiles = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> pending = new();

        foreach (var projectPath in projectPaths)
        {
            foreach (var file in RelevantFiles(projectPath))
            {
                pending.Enqueue(file);
            }
        }

        while (pending.Count > 0)
        {
            var file = pending.Dequeue();
            if (!checkedFiles.Add(file))
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception)
            {
                // Unreadable is unanswerable, and the safe answer costs only speed.
                return true;
            }

            if (
                RedirectingProperties.Any(property =>
                    text.Contains(property, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                return true;
            }

            // Imports are followed, because a redirect one file deeper is not meaningfully rarer than a
            // redirect in the file itself — a repository that factors its build into shared props is
            // exactly the sort that redirects intermediate output.
            var imports = ImportsOf(file, out var unresolvable);
            if (unresolvable)
            {
                return true;
            }

            foreach (var import in imports)
            {
                pending.Enqueue(import);
            }
        }

        return false;
    }

    /// <summary>
    /// The files an MSBuild file imports, as resolvable absolute paths. <paramref name="unresolvable"/> is
    /// set when an import cannot be followed — a path built from properties, a wildcard, or an unparseable
    /// file — because an import that cannot be read cannot be ruled out.
    ///
    /// Parsed rather than pattern-matched: <c>&lt;Import Project='x.props' /&gt;</c> is as valid as double
    /// quotes, and a semicolon-separated list is several files rather than one path that happens not to
    /// exist. Both were cross-review findings against an earlier version of this.
    /// </summary>
    private static List<string> ImportsOf(string file, out bool unresolvable)
    {
        unresolvable = false;
        List<string> imports = [];

        List<string> declared;
        try
        {
            declared = XDocument
                .Load(file)
                .Descendants()
                .Where(element =>
                    string.Equals(element.Name.LocalName, "Import", StringComparison.Ordinal)
                )
                .Select(element => element.Attribute("Project")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .SelectMany(value => value!.Split(';', StringSplitOptions.RemoveEmptyEntries))
                .ToList();
        }
        catch (Exception)
        {
            unresolvable = true;
            return imports;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(file)) ?? string.Empty;

        foreach (var entry in declared)
        {
            var trimmed = entry.Trim();
            if (trimmed.Contains("$(", StringComparison.Ordinal) || trimmed.Contains('*'))
            {
                unresolvable = true;
                continue;
            }

            try
            {
                var resolved = Path.GetFullPath(
                    trimmed.Replace('\\', Path.DirectorySeparatorChar),
                    directory
                );

                if (File.Exists(resolved))
                {
                    imports.Add(resolved);
                }
            }
            catch (Exception exception)
                when (exception is ArgumentException or NotSupportedException)
            {
                unresolvable = true;
            }
        }

        return imports;
    }

    private static IEnumerable<string> RelevantFiles(string projectPath)
    {
        string full;
        string? directory;
        try
        {
            full = Path.GetFullPath(projectPath);
            directory = Path.GetDirectoryName(full);
        }
        catch (Exception)
        {
            yield break;
        }

        if (File.Exists(full))
        {
            yield return full;
        }

        foreach (
            var name in new[]
            {
                "Directory.Build.props",
                "Directory.Build.targets",
                "Directory.Packages.props",
            }
        )
        {
            for (
                var current = directory is null ? null : new DirectoryInfo(directory);
                current is not null;
                current = current.Parent
            )
            {
                var candidate = Path.Combine(current.FullName, name);
                if (File.Exists(candidate))
                {
                    yield return candidate;
                    break;
                }
            }
        }
    }

    private sealed class Release(SemaphoreSlim gate, int permits = 1, SemaphoreSlim? entry = null)
        : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            gate.Release(permits);
            entry?.Release();
        }
    }
}
