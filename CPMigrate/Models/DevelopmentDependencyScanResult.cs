namespace CPMigrate.Models;

/// <summary>
/// Which referenced packages self-declared <c>developmentDependency="true"</c> in their nuspec.
/// Two precisions, because the two consumers ask different questions: <see cref="Projects"/> says
/// whether the version a particular project resolves is development-only — the answer for
/// <c>PackageReference</c> declarations — and <see cref="Versions"/> says whether a particular
/// id/version is — the answer for central pins, which carry a version but no project.
/// </summary>
public sealed record DevelopmentDependencyScanResult(
    IReadOnlySet<ProjectDevelopmentDependency> Projects,
    IReadOnlySet<PackageVersionKey> Versions
);

/// <summary>
/// A project whose resolved version of the package self-declares development-only. Package ids are
/// NuGet ids — compared case-insensitively — while the path is compared ordinally: both sides of a
/// lookup were read from the same scan of the same file.
/// </summary>
public readonly record struct ProjectDevelopmentDependency(string ProjectPath, string PackageName)
{
    public bool Equals(ProjectDevelopmentDependency other)
    {
        return string.Equals(ProjectPath, other.ProjectPath, StringComparison.Ordinal)
            && string.Equals(PackageName, other.PackageName, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(ProjectPath),
            StringComparer.OrdinalIgnoreCase.GetHashCode(PackageName)
        );
    }
}

/// <summary>
/// An id/version pair whose nuspec self-declares development-only. The id is a NuGet id; the
/// version is already normalized by the scan, so both compare case-insensitively.
/// </summary>
public readonly record struct PackageVersionKey(string PackageName, string Version)
{
    public bool Equals(PackageVersionKey other)
    {
        return string.Equals(PackageName, other.PackageName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Version, other.Version, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(PackageName),
            StringComparer.OrdinalIgnoreCase.GetHashCode(Version)
        );
    }
}
