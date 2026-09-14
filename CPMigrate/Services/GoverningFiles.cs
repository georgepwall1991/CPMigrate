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
