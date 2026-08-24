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
    string ProcessProject(
        string projectFilePath,
        Dictionary<string, HashSet<string>> packageVersions,
        bool keepVersionAttributes = false);
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

