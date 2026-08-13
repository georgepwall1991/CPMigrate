using CPMigrate.Licensing;

namespace CPMigrate.Models;

/// <summary>
/// License metadata resolved for one package reference. Permissive licenses are still recorded so
/// the analyzer can choose not to report them, rather than the scan pretending they were never seen.
/// </summary>
public record LicenseInfo(
    string PackageName,
    string Version,
    string ProjectPath,
    string ProjectName,
    string License,
    LicenseClassification Classification,
    string LicenseType,
    bool IsTransitive = false
);
