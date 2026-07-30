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
    /// <summary>
    /// The properties that can move a project's assets file. If none of the XML reachable from a project
    /// mentions any of them, the file is at <c>obj/project.assets.json</c> under the project — provably, not
    /// probably, because these are the only ways to move it.
    /// </summary>
    private static readonly string[] RedirectingProperties =
    [
        "BaseIntermediateOutputPath",
        "MSBuildProjectExtensionsPath",
        "ProjectAssetsFile",
    ];

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>
    /// Waits for exclusive access to this project's assets file, returning a handle to release it.
    ///
    /// <para>Keyed on the project directory, which is where <c>obj/project.assets.json</c> lives — but only
    /// once <see cref="RedirectsAssetsFile"/> has confirmed nothing redirects it. When something might,
    /// every project shares a single key and the phase runs serially.</para>
    ///
    /// <para><b>Why not compute the real path.</b> Successive review rounds each found another route to a
    /// shared assets file: a conditional property, one set in an imported <c>Directory.Build.props</c>, one
    /// built from <c>$(…)</c>, <c>MSBuildProjectExtensionsPath</c> outranking
    /// <c>BaseIntermediateOutputPath</c>, <c>ProjectAssetsFile</c> naming the file outright, an import of an
    /// import. Each fix was correct and the next round found another, which is the signal that the approach
    /// was wrong rather than unfinished: knowing where the file really goes means evaluating the project, and
    /// evaluating the project is the one thing this phase must not do — MSBuild's object model is not
    /// thread-safe, which is why the phase exists.</para>
    ///
    /// <para>So the question asked is not "where does it go" but "could it have moved", which
    /// <em>is</em> answerable from text: those three property names appear somewhere, or they do not. The
    /// ordinary layout keeps the full concurrency; anything unusual gets the previous serial behaviour and
    /// no possibility of a race. A missed redirect can silently erase findings; an unnecessary
    /// serialisation just costs seconds.</para>
    /// </summary>
    public static async Task<IDisposable> AcquireAsync(string projectPath)
    {
        var key = RedirectsAssetsFile(projectPath)
            ? SharedConservativeKey
            : Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? projectPath;

        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();

        return new Holder(gate);
    }

    /// <summary>One lock for every project whose assets file might have been moved.</summary>
    private const string SharedConservativeKey = "\u0000conservative";

    /// <summary>
    /// Whether anything reachable from this project could move its assets file.
    ///
    /// Searched as text across the project file and every MSBuild file beside or above it, because a
    /// property name cannot hide from a substring search the way it can from a structural one — an import of
    /// an import still has to spell it out somewhere. Conservative in both directions that matter: a mention
    /// inside a comment triggers serial mode, which is harmless.
    /// </summary>
    private static bool RedirectsAssetsFile(string projectPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(projectPath);
            var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;

            HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
            Queue<string> pending = new();

            pending.Enqueue(fullPath);
            foreach (var file in AncestorBuildFiles(directory))
            {
                pending.Enqueue(file);
            }

            while (pending.Count > 0)
            {
                var file = pending.Dequeue();
                if (!visited.Add(file))
                {
                    continue;
                }

                var text = ReadOrNull(file);
                if (text is null)
                {
                    // Cannot be read, so cannot be ruled out.
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

                // Imports have to be followed, and not only upwards: a Directory.Build.props that imports
                // build/Paths.props reaches a *child* directory, so searching ancestors alone missed the
                // declaration entirely and the fast path raced. An import whose path cannot be resolved
                // here — a property, a wildcard, a missing file — cannot be ruled out either.
                var imports = ImportedFiles(file, out var unresolvable);

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
        catch (Exception)
        {
            // Unreadable, unauthorised, malformed, too long — anything. Treated as "it might redirect", so
            // an unanswerable question costs concurrency rather than correctness. Never throws: this runs
            // before the project is scanned, and the scanners report an unreadable project as an incomplete
            // scan and carry on.
            return true;
        }
    }



    /// <summary>
    /// The files an MSBuild file imports, as resolvable absolute paths. Sets <paramref name="unresolvable"/>
    /// when an import cannot be followed — a path built from properties, a wildcard, or a file that is not
    /// there — because an import that cannot be read cannot be ruled out.
    /// </summary>
    private static List<string> ImportedFiles(string file, out bool unresolvable)
    {
        unresolvable = false;
        List<string> imports = [];

        List<string> declaredPaths;
        try
        {
            // Parsed, not pattern-matched. A regex over the raw text missed
            // <Import Project='build/Paths.props' /> — single quotes are just as valid — and would have gone
            // on missing whatever else an attribute is allowed to look like. An import that is not seen is a
            // redirect that is not seen, which is a silent race.
            declaredPaths = XDocument
                .Load(file)
                .Descendants()
                .Where(element =>
                    string.Equals(element.Name.LocalName, "Import", StringComparison.Ordinal)
                )
                .Select(element => element.Attribute("Project")?.Value?.Trim())
                .Where(value => !string.IsNullOrEmpty(value))
                .Select(value => value!)
                .ToList();
        }
        catch (Exception)
        {
            // Unreadable or not well-formed. Cannot be ruled out.
            unresolvable = true;
            return imports;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(file)) ?? string.Empty;

        foreach (var declared in declaredPaths)
        {
            if (declared.Contains("$(", StringComparison.Ordinal) || declared.Contains('*'))
            {
                // A path built from properties, or a wildcard. Not resolvable without evaluating.
                unresolvable = true;
                continue;
            }

            try
            {
                var resolved = Path.GetFullPath(
                    declared.Replace('\\', Path.DirectorySeparatorChar),
                    directory
                );

                if (File.Exists(resolved))
                {
                    imports.Add(resolved);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                unresolvable = true;
            }
        }

        return imports;
    }

    private static string? ReadOrNull(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Every MSBuild props/targets file beside or above a project directory.</summary>
    private static IEnumerable<string> AncestorBuildFiles(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            foreach (
                var file in Directory
                    .EnumerateFiles(current.FullName, "*.props", SearchOption.TopDirectoryOnly)
                    .Concat(
                        Directory.EnumerateFiles(
                            current.FullName,
                            "*.targets",
                            SearchOption.TopDirectoryOnly
                        )
                    )
            )
            {
                yield return file;
            }
        }
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
