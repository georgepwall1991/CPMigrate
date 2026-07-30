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
    /// Waits for exclusive access to this project's <c>obj</c> directory, returning a handle to release it.
    ///
    /// Safe to key on the directory only because <see cref="CanRedirectAssetsFile"/> has been asked about
    /// every project in the scan first, and the caller runs the phase serially if any of them said yes. A
    /// per-project decision cannot work: a redirect's *target* may be another project's default directory, so
    /// the redirected project and the ordinary one would need to agree on a key without either knowing the
    /// other exists.
    /// </summary>
    public static async Task<IDisposable> AcquireAsync(string projectPath)
    {
        var key = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? projectPath;
        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();

        return new Holder(gate);
    }

    /// <summary>
    /// Whether the resolved-package phase can safely run concurrently over these projects.
    ///
    /// False as soon as one of them could move its assets file, because then no per-project key is provably
    /// right — see <see cref="CanRedirectAssetsFile"/> for why the question is put this way round.
    /// </summary>
    public static bool CanScanConcurrently(IEnumerable<string> projectPaths)
    {
        return !projectPaths.Any(CanRedirectAssetsFile);
    }

    /// <summary>
    /// Answers already computed, per project directory.
    ///
    /// The walk reads every props and targets file from the project up to the filesystem root, and projects
    /// in one solution share almost all of those ancestors — so without this, asking once per project made a
    /// 60-project scan twice as slow as the concurrency it was there to enable. Keyed on the directory
    /// because that is what determines both the answer's inputs.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> RedirectAnswers = new(
        StringComparer.Ordinal
    );

    /// <summary>Clears the memo. Tests only, so one fixture's layout cannot answer for another's.</summary>
    internal static void ResetForTests()
    {
        RedirectAnswers.Clear();
        Locks.Clear();
    }

    /// <summary>
    /// Whether anything reachable from this project could move its assets file.
    ///
    /// Searched as text across the project file and every MSBuild file beside or above it, because a
    /// property name cannot hide from a substring search the way it can from a structural one — an import of
    /// an import still has to spell it out somewhere. Conservative in both directions that matter: a mention
    /// inside a comment triggers serial mode, which is harmless.
    /// </summary>
    private static bool CanRedirectAssetsFile(string projectPath)
    {
        string directoryKey;
        try
        {
            directoryKey = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? projectPath;
        }
        catch (Exception)
        {
            return true;
        }

        return RedirectAnswers.GetOrAdd(directoryKey, _ => ComputeCanRedirect(projectPath));
    }

    private static bool ComputeCanRedirect(string projectPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(projectPath);
            var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;

            // Ordinal, not OrdinalIgnoreCase: on a case-sensitive filesystem a.props and A.props are two
            // files, and skipping the second because the first was visited would miss whatever it declares.
            // Revisiting one file under two casings on Windows costs a read.
            HashSet<string> visited = new(StringComparer.Ordinal);
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

    /// <summary>
    /// The MSBuild files above a project that can contribute properties to it: the nearest
    /// <c>Directory.Build.props</c> and the nearest <c>Directory.Build.targets</c>.
    ///
    /// The *nearest*, and then stop — which is MSBuild's own rule, and also what keeps this affordable. An
    /// earlier version enumerated every <c>*.props</c> and <c>*.targets</c> in every ancestor up to the
    /// filesystem root, which on a 60-project solution cost more than the concurrency it was guarding.
    /// Anything further up is reached by import from these, and imports are followed.
    /// </summary>
    private static IEnumerable<string> AncestorBuildFiles(string directory)
    {
        foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
        {
            for (
                var current = new DirectoryInfo(directory);
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
