namespace CPMigrate.Services;

/// <summary>
/// Interface for interacting with the .NET CLI.
/// </summary>
public interface IDotNetCliService
{
    /// <summary>
    /// Execute 'dotnet list package' command.
    /// </summary>
    Task<(string Output, bool Success)> RunListPackageAsync(string projectPathOrDirectory, bool includeTransitive, bool vulnerable);

    /// <summary>
    /// Execute package list command with JSON output (prefers 'dotnet list package').
    /// </summary>
    Task<(string Output, bool Success)> RunPackageListJsonAsync(string projectPathOrDirectory, DotNetPackageListOptions options);

    /// <summary>
    /// Execute 'dotnet restore' command.
    /// </summary>
    Task<(string Output, bool Success)> RunRestoreAsync(string solutionOrProjectPath);

    /// <summary>
    /// Execute 'dotnet nuget list source' so callers can see the feeds restore will use.
    /// </summary>
    /// <param name="workingDirectory">
    /// Directory whose NuGet.Config chain should be listed. NuGet walks from here to the user- and
    /// machine-level configs, so a repo with a private feed only appears when this is that repo.
    /// </param>
    Task<(string Output, bool Success)> RunNugetListSourceAsync(string workingDirectory);

    /// <summary>
    /// Execute 'dotnet test' command.
    /// </summary>
    /// <param name="solutionOrProjectPath">Solution or project to test.</param>
    /// <param name="testFilter">
    /// Optional <c>--filter</c> expression. Used by bisection to narrow each probe to a fast subset of the
    /// suite; a null or blank value runs the whole suite.
    /// </param>
    Task<(string Output, bool Success)> RunTestAsync(string solutionOrProjectPath, string? testFilter = null);
}
