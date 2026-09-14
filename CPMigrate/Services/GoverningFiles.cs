namespace CPMigrate.Services;

/// <summary>
/// Locates the files that govern a workspace the way NuGet and MSBuild locate them:
/// <c>Directory.Packages.props</c> is the nearest one in a project's ancestry, so every
/// workflow that reads or writes it must walk up the same way rather than probing a single
/// directory and missing (or shadowing) the file that actually applies.
/// </summary>
internal static class GoverningFiles
{
    /// <summary>
    /// The closest <c>Directory.Packages.props</c> at or above <paramref name="startDirectory"/>,
    /// or null when none exists — the same nearest-wins walk NuGet performs from each project.
    /// A null, empty, or nonexistent start answers null: walking up from a path that names
    /// nothing resolves a file governing a tree that isn't there, and an empty path would
    /// anchor the search at the process working directory.
    /// </summary>
    /// <remarks>
    /// Not handled: a repository that redirects the file with <c>DirectoryPackagesPropsPath</c>.
    /// Resolving that needs full MSBuild evaluation, which these callers do not perform.
    /// </remarks>
    public static string? FindNearestPropsFile(string? startDirectory)
    {
        if (string.IsNullOrEmpty(startDirectory) || !Directory.Exists(startDirectory))
        {
            return null;
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
