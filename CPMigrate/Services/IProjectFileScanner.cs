using CPMigrate.Models;

namespace CPMigrate.Services;

public interface IProjectFileScanner
{
    string GetTargetFramework(string projectFilePath);
    string ProcessProject(
        string projectFilePath,
        Dictionary<string, HashSet<string>> packageVersions,
        bool keepVersionAttributes = false);
    (List<PackageReference> References, bool Success) ScanProjectPackages(string projectFilePath);
}

