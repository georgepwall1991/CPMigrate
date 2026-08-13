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
        return Path.GetFullPath(Path.Combine(globalPackagesFolder, id, version, id + ".nuspec"));
    }
}
