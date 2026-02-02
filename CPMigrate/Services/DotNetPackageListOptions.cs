namespace CPMigrate.Services;

/// <summary>
/// Options for running dotnet package list in JSON mode.
/// </summary>
public record DotNetPackageListOptions
{
    public bool IncludeTransitive { get; init; }
    public bool Vulnerable { get; init; }
    public bool Outdated { get; init; }
    public bool Deprecated { get; init; }
    public bool IncludePrerelease { get; init; }
}
