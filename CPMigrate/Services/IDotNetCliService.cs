namespace CPMigrate.Services;

/// <summary>
/// Interface for interacting with the .NET CLI.
/// </summary>
public interface IDotNetCliService
{
    /// <summary>
    /// Execute 'dotnet list package' command.
    /// </summary>
    Task<(string Output, bool Success)> RunListPackageAsync(string projectDir, bool includeTransitive, bool vulnerable);
}
