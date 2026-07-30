using System.Xml.Linq;

namespace CPMigrate.Services;

/// <summary>
/// Decides whether a project's restore can be redirected into a private directory.
///
/// <para><b>Why this exists.</b> <c>dotnet package list</c> restores, and two projects sharing a
/// <c>project.assets.json</c> cannot be queried at the same time: the loser comes back reporting the other
/// project's packages, so two projects with different versions of a package report the same one and the
/// version-inconsistency finding disappears with a successful exit code. Giving each concurrent invocation
/// its own intermediate directory removes the collision — but only if the redirection actually takes
/// effect.</para>
///
/// <para><b>The question this answers, and why it is answerable.</b> The redirection is passed as
/// environment variables, because <c>dotnet package list</c> rejects <c>-p:</c> arguments. An assignment in
/// the project or in an imported <c>Directory.Build.props</c> overrides them — measured: a props file
/// setting <c>MSBuildProjectExtensionsPath</c> defeats the environment entirely, and both projects go back
/// to one shared assets file.
///
/// So the question is <em>"can this project override the redirection"</em>, not <em>"where would its assets
/// file go"</em>. That distinction is the whole reason this is tractable. 3.24.0 tried the second question
/// and abandoned it after eight review rounds, because the answer depends on conditions, <c>$(…)</c>
/// expansion and import order — it needs full MSBuild evaluation, which is exactly what the concurrent phase
/// cannot do. The first question needs only this: MSBuild has no way to assign a property without naming it
/// literally, so if none of the three names appears anywhere reachable, none is assigned, and the
/// environment holds.</para>
///
/// <para>Conservative in the one direction that matters. A name appearing in a comment, or an import that
/// cannot be followed, both answer "no" — costing concurrency, never correctness.</para>
/// </summary>
internal static class RestoreIsolation
{
    /// <summary>
    /// The properties that can redirect where <c>project.assets.json</c> is written, and therefore override
    /// the environment values used to isolate a restore.
    /// </summary>
    private static readonly string[] RedirectingProperties =
    [
        "MSBuildProjectExtensionsPath",
        "BaseIntermediateOutputPath",
        "ProjectAssetsFile",
    ];

    /// <summary>
    /// Whether every one of these projects can have its restore isolated — and so whether they may be
    /// queried concurrently.
    ///
    /// All or nothing, deliberately. Only two <em>non</em>-isolated projects can collide with each other, so
    /// a mixed run could in principle isolate some and serialise the rest — but "which subset is safe
    /// together" is another thing to get subtly wrong, and the configured case is rare enough that the
    /// simpler answer costs almost nothing.
    /// </summary>
    public static bool CanIsolate(IEnumerable<string> projectPaths)
    {
        return projectPaths.All(CanIsolate);
    }

    private static bool CanIsolate(string projectPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(projectPath);
            var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;

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

                // Ordinal: on a case-sensitive filesystem a.props and A.props are two files, and treating
                // them as one would skip whatever the second assigns.
                if (!visited.Add(file))
                {
                    continue;
                }

                var text = ReadOrNull(file);
                if (text is null)
                {
                    return false;
                }

                if (
                    RedirectingProperties.Any(property =>
                        text.Contains(property, StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    return false;
                }

                var imports = ImportedFiles(file, out var unresolvable);
                if (unresolvable)
                {
                    return false;
                }

                foreach (var import in imports)
                {
                    pending.Enqueue(import);
                }
            }

            return true;
        }
        catch (Exception)
        {
            // Unreadable, unauthorised, malformed, path too long — anything. An unanswerable question costs
            // concurrency rather than correctness, and never throws: this runs before the project is
            // scanned, and the scanners are the ones equipped to report an unreadable project as an
            // incomplete scan and carry on past it.
            return false;
        }
    }

    /// <summary>
    /// The MSBuild files above a project that can contribute properties to it: the nearest
    /// <c>Directory.Build.props</c> and the nearest <c>Directory.Build.targets</c>.
    ///
    /// The nearest of each, then stop — MSBuild's own rule, and what keeps this affordable. Anything further
    /// up is reached by import from these, and imports are followed.
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

    /// <summary>
    /// The files an MSBuild file imports, as resolvable absolute paths. Sets <paramref name="unresolvable"/>
    /// when an import cannot be followed — a path built from properties, a wildcard, or a missing file —
    /// because an import that cannot be read cannot be ruled out.
    ///
    /// Parsed, not pattern-matched: <c>&lt;Import Project='x.props' /&gt;</c> is as valid as double quotes,
    /// and a regex over the raw text missed it.
    /// </summary>
    private static List<string> ImportedFiles(string file, out bool unresolvable)
    {
        unresolvable = false;
        List<string> imports = [];

        List<string> declaredPaths;
        try
        {
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
            unresolvable = true;
            return imports;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(file)) ?? string.Empty;

        foreach (var declared in declaredPaths)
        {
            if (declared.Contains("$(", StringComparison.Ordinal) || declared.Contains('*'))
            {
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
            catch (Exception exception)
                when (exception is ArgumentException or NotSupportedException)
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
}
