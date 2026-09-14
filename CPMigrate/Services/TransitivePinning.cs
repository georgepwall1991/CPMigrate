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
    /// The workspace's effective setting, resolved the way MSBuild does: the packages props file
    /// is imported after <c>Directory.Build.props</c>, so a value set there wins over the build
    /// props value; the build props value applies only when the props file is silent.
    /// </summary>
    /// <param name="propsPath">Path to the governing <c>Directory.Packages.props</c>.</param>
    /// <param name="basePath">The scan root the run was pointed at; the build-props walk starts here.</param>
    public static bool IsEnabled(string propsPath, string? basePath) =>
        Resolve(propsPath, basePath) == true;

    /// <summary>
    /// The three-state answer <see cref="IsEnabled"/> flattens: <c>true</c> opted in,
    /// <c>false</c> explicitly opted out somewhere that governs, <c>null</c> never set. Callers
    /// deciding whether to *write* the property need the distinction — an explicit
    /// <c>false</c> in <c>Directory.Build.props</c> is still the workspace's own choice, even
    /// though a value written into the packages props file would silently outrank it.
    /// </summary>
    public static bool? Resolve(string propsPath, string? basePath)
    {
        var inProps = TryRead(propsPath);
        if (inProps.HasValue)
        {
            return inProps.Value;
        }

        var buildProps = FindNearestBuildProps(basePath)
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(propsPath))!, BuildPropsFileName);

        return TryRead(buildProps);
    }

    /// <summary>
    /// The last-wins value of the property in one file — <c>null</c> when the file is missing,
    /// unreadable, or never sets it. Callers distinguishing "explicitly off" from "unset" need the
    /// three states: an absent value can be filled in, an explicit <c>false</c> is the workspace's
    /// own choice and is not ours to flip.
    /// </summary>
    public static bool? TryRead(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var document = XDocument.Load(path);
            var effective = document
                .Descendants()
                .Where(e => string.Equals(e.Name.LocalName, Property, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Value.Trim())
                .LastOrDefault();

            return effective is null
                ? null
                : string.Equals(effective, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            // Unreadable means unproven, and an unproven pin is one that might do nothing.
            return null;
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
