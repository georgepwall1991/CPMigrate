namespace CPMigrate.Services;

/// <summary>
/// Locates the files that govern a workspace the way NuGet and MSBuild locate them:
/// <c>Directory.Packages.props</c> is the nearest one in a project's ancestry unless the
/// workspace redirects it with <c>DirectoryPackagesPropsPath</c>, so every workflow that reads
/// or writes it must resolve both rather than probing a single directory and missing (or
/// shadowing) the file that actually applies.
/// </summary>
internal static class GoverningFiles
{
    /// <summary>
    /// The props file that governs <paramref name="startDirectory"/> right now: a declared
    /// <c>DirectoryPackagesPropsPath</c> that exists, else the nearest conventional
    /// <c>Directory.Packages.props</c> at or above the directory — the same nearest-wins walk
    /// NuGet performs from each project. A null, empty, or nonexistent start answers null:
    /// walking up from a path that names nothing resolves a file governing a tree that isn't
    /// there, and an empty path would anchor the search at the process working directory.
    /// </summary>
    /// <remarks>
    /// A redirect that is declared but names a file that does not exist — or cannot be resolved
    /// by reading XML — answers null rather than the conventional file: NuGet imports the
    /// declared path, so the conventional one would be inert, and claiming it would report
    /// against a file the build never reads.
    /// </remarks>
    public static string? FindNearestPropsFile(string? startDirectory)
    {
        if (string.IsNullOrEmpty(startDirectory) || !Directory.Exists(startDirectory))
        {
            return null;
        }

        if (
            MsBuildProps.TryReadRedirectedPropsPath(
                startDirectory,
                MsBuildProps.PathComparerFor(startDirectory),
                out var redirected
            )
        )
        {
            return redirected;
        }

        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Directory.Packages.props");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// The path a write should target when <paramref name="startDirectory"/> declares a
    /// <c>DirectoryPackagesPropsPath</c> redirect — whether or not the file exists yet, because
    /// NuGet imports the declared path and creating it there is what makes the write live. Null
    /// when no resolvable redirect is declared, leaving the caller to its conventional answer.
    /// </summary>
    public static string? ResolveDeclaredPropsPath(string? startDirectory)
    {
        if (string.IsNullOrEmpty(startDirectory) || !Directory.Exists(startDirectory))
        {
            return null;
        }

        return MsBuildProps.TryGetDeclaredPropsPath(
            startDirectory,
            MsBuildProps.PathComparerFor(startDirectory),
            out var declaredPath
        )
            ? declaredPath
            : null;
    }

    /// <summary>
    /// Every conventional <c>Directory.Packages.props</c> that shares one ancestry with
    /// another — anywhere under <paramref name="startDirectory"/> or in its ancestor chain.
    /// NuGet evaluates only the nearest file per project, so when one of these directories
    /// is an ancestor of another's, projects beneath the deeper file silently lose every pin
    /// the shallower one holds. The returned set is sorted for deterministic reporting.
    /// </summary>
    public static IReadOnlyList<string> FindConflictingPropsFiles(string? startDirectory)
    {
        if (string.IsNullOrEmpty(startDirectory) || !Directory.Exists(startDirectory))
        {
            return [];
        }

        var comparer = MsBuildProps.PathComparerFor(startDirectory);
        var all = new List<string>();

        CollectPropsRecursive(Path.GetFullPath(startDirectory), all, new HashSet<string>(comparer));

        var ancestor = Directory.GetParent(Path.GetFullPath(startDirectory));
        while (ancestor is not null)
        {
            var candidate = Path.Combine(ancestor.FullName, "Directory.Packages.props");
            if (File.Exists(candidate))
            {
                all.Add(candidate);
            }

            ancestor = ancestor.Parent;
        }

        // Two files conflict when one's directory is a strict ancestor of the other's — a
        // project beneath the deeper file's directory then sees both, and NuGet keeps only
        // the nearer. Files in sibling subtrees never meet in one ancestry and are fine.
        var conflicting = new SortedSet<string>(comparer);
        for (var i = 0; i < all.Count; i++)
        {
            for (var j = i + 1; j < all.Count; j++)
            {
                var dirA = Path.GetDirectoryName(all[i]);
                var dirB = Path.GetDirectoryName(all[j]);
                if (dirA is null || dirB is null)
                {
                    continue;
                }

                if (IsStrictAncestor(dirA, dirB, comparer) || IsStrictAncestor(dirB, dirA, comparer))
                {
                    conflicting.Add(all[i]);
                    conflicting.Add(all[j]);
                }
            }
        }

        return [.. conflicting];
    }

    private static bool IsStrictAncestor(string ancestorDir, string descendantDir, StringComparer comparer)
    {
        var comparison = comparer == StringComparer.OrdinalIgnoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var ancestor = ancestorDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var descendant = descendantDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return descendant.StartsWith(ancestor + Path.DirectorySeparatorChar, comparison);
    }

    private static void CollectPropsRecursive(string directory, List<string> results, HashSet<string> visited)
    {
        try
        {
            // The real path is what a symlink loop revisits, not the path that reached it —
            // same rule project discovery keeps.
            if (!visited.Add(Path.GetFullPath(directory)))
            {
                return;
            }

            var dirInfo = new DirectoryInfo(directory);
            if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }

            results.AddRange(
                Directory.EnumerateFiles(directory, "Directory.Packages.props", SearchOption.TopDirectoryOnly)
            );

            foreach (var subDir in Directory
                .EnumerateDirectories(directory)
                .Where(d => !BatchService.DefaultExcludedDirectories.Contains(Path.GetFileName(d))))
            {
                CollectPropsRecursive(subDir, results, visited);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // A subtree we cannot read is skipped, not fatal — same rule batch discovery keeps.
        }
    }

    /// <summary>
    /// A solution file — <c>.sln</c> or <c>.slnx</c> — directly in
    /// <paramref name="basePath"/>, chosen in a fixed order when more than one exists so the
    /// verification target does not depend on filesystem enumeration order.
    /// </summary>
    public static string? FindSolutionFile(string basePath)
    {
        if (!Directory.Exists(basePath))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(basePath, "*.sln*", SearchOption.TopDirectoryOnly)
            .Where(f =>
                f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}
