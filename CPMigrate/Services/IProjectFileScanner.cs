using CPMigrate.Models;

namespace CPMigrate.Services;

public interface IProjectFileScanner
{
    string GetTargetFramework(string projectFilePath);

    /// <summary>
    /// Every literal target framework the project file declares, across all
    /// <c>TargetFramework</c>/<c>TargetFrameworks</c> assignments. Empty when nothing readable is
    /// declared — callers must treat empty as "unexamined", never as "supported".
    /// </summary>
    IReadOnlyList<string> GetDeclaredTargetFrameworks(string projectFilePath);
    /// <param name="expressionVersions">
    /// Collects version values that are MSBuild expressions (<c>$(X)</c>) rather than literals,
    /// keyed by package name. Those references keep their value as a <c>VersionOverride</c> —
    /// project-scoped, where it still resolves — and the caller forwards the expression as the
    /// package's central pin when no literal version exists. When null, expression values fall
    /// back into <paramref name="packageVersions"/> for callers that cannot track them.
    /// </param>
    /// <param name="expressionPinnedPackages">
    /// Packages whose existing central pin is an MSBuild expression — their literal
    /// <c>Version</c> declarations become <c>VersionOverride</c> rather than displacing the pin.
    /// </param>
    string ProcessProject(
        string projectFilePath,
        Dictionary<string, HashSet<string>> packageVersions,
        bool keepVersionAttributes = false,
        Dictionary<string, HashSet<string>>? expressionVersions = null,
        IReadOnlySet<string>? expressionPinnedPackages = null);
    (List<PackageReference> References, bool Success) ScanProjectPackages(string projectFilePath);

    /// <summary>
    /// Every <c>PackageReference</c> the project file declares, as written.
    ///
    /// Distinct from <see cref="ScanProjectPackages"/>, which exists to stand in for a resolved scan and
    /// therefore drops anything it cannot turn into a usable version — items with no <c>Version</c>, and
    /// versions behind an MSBuild property. Under central package management a reference normally *has* no
    /// version, so for the majority of this tool's users that scan returns nothing at all. A rule about
    /// what the file says needs every declaration, version or not, plus whether it was conditional.
    /// </summary>
    (List<PackageReference> References, bool Success) ScanDeclaredPackages(string projectFilePath);
}

