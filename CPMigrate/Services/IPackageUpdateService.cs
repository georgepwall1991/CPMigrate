using CPMigrate.Models;

namespace CPMigrate.Services;

/// <summary>
/// Interface for updating NuGet packages to their latest versions with test verification.
/// </summary>
public interface IPackageUpdateService
{
    /// <summary>
    /// Updates packages in Directory.Packages.props to latest versions, runs tests, and rolls back on failure.
    /// </summary>
    /// <param name="options">CLI options including dry-run, include-prerelease, etc.</param>
    /// <returns>Result of the update operation.</returns>
    Task<PackageUpdateResult> UpdatePackagesAsync(Options options);
}
