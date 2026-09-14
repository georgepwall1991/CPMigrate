using System.Xml.Linq;

namespace CPMigrate.Services;

/// <summary>
/// Whether a workspace has opted into central transitive pinning — the switch that makes a
/// <c>PackageVersion</c> for a package nothing references directly actually govern the resolved
/// graph. Without it such a pin is inert: restore ignores it, verification passes because the
/// graph never moved, and the tool has written an entry that changes nothing.
/// </summary>
internal static class TransitivePinning
{
    private const string Property = "CentralPackageTransitivePinningEnabled";
    private const string BuildPropsFileName = "Directory.Build.props";

    /// <summary>
    /// True when the property resolves to <c>true</c> in the central props file or in the nearest
    /// <c>Directory.Build.props</c> MSBuild would import for the workspace. The property is a build
    /// property like any other, so a repo that sets it in build props rather than the packages file
    /// has opted in just the same — checking only <paramref name="propsPath"/> would report a live
    /// workspace as unopted and withhold pins that would have worked.
    /// </summary>
    /// <param name="propsPath">Path to the governing <c>Directory.Packages.props</c>.</param>
    /// <param name="basePath">The scan root the run was pointed at; the build-props walk starts here.</param>
    public static bool IsEnabled(string propsPath, string? basePath)
    {
        if (IsPropertyTrue(propsPath))
        {
            return true;
        }

        var buildProps = FindNearestBuildProps(basePath)
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(propsPath))!, BuildPropsFileName);

        return IsPropertyTrue(buildProps);
    }

    /// <summary>
    /// Last-wins value of the property in one file, matching how MSBuild resolves a repeated
    /// assignment — an earlier <c>true</c> overridden by a later <c>false</c> is off.
    /// </summary>
    private static bool IsPropertyTrue(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var document = XDocument.Load(path);
            var effective = document
                .Descendants()
                .Where(e => string.Equals(e.Name.LocalName, Property, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Value.Trim())
                .LastOrDefault();

            return string.Equals(effective, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            // Unreadable means unproven, and an unproven pin is one that might do nothing.
            return false;
        }
    }

    /// <summary>
    /// The nearest <c>Directory.Build.props</c> at or above the scan root — the same walk MSBuild
    /// performs from each project, so the file found here is the file that would govern.
    /// </summary>
    private static string? FindNearestBuildProps(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, BuildPropsFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
