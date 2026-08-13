using NuGet.Versioning;

namespace CPMigrate.Licensing;

/// <summary>
/// Locates the NuGet global packages folder the same way restore does: <c>NUGET_PACKAGES</c>
/// first, then <c>~/.nuget/packages</c>.
/// </summary>
public static class GlobalPackagesFolder
{
    public const string EnvironmentVariableName = "NUGET_PACKAGES";

    public static string Resolve(string? environmentValue = null, string? userProfile = null)
    {
        var fromEnvironment = environmentValue ?? Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment.Trim());
        }

        var profile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.GetFullPath(Path.Combine(profile, ".nuget", "packages"));
    }

    public static string NuspecPath(string globalPackagesFolder, string packageId, string version)
    {
        var id = packageId.ToLowerInvariant();
        var versionFolder = VersionFolder(version);
        return Path.GetFullPath(Path.Combine(globalPackagesFolder, id, versionFolder, id + ".nuspec"));
    }

    /// <summary>
    /// NuGet writes version folders as <c>ToNormalizedString().ToLowerInvariant()</c>: three-digit,
    /// lowercase prerelease labels, no build metadata.
    /// </summary>
    public static string VersionFolder(string version)
    {
        return NuGetVersion.TryParse(version, out var parsed)
            ? parsed.ToNormalizedString().ToLowerInvariant()
            : version;
    }
}
