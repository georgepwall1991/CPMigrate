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

    /// <summary>
    /// A directory to redirect this invocation's MSBuild intermediate output to, or null to use the
    /// project's own <c>obj</c>.
    ///
    /// <c>dotnet package list</c> restores, and the assets file it writes lives under the project's
    /// intermediate directory — so two projects that share one cannot be queried concurrently: the loser
    /// comes back reporting the other project's packages, and a version-inconsistency finding disappears
    /// with a clean exit code. Giving each concurrent invocation its own directory makes that collision
    /// impossible rather than something to detect, which matters because *whether* two projects share an
    /// assets file is not answerable without evaluating MSBuild — conditions, imported props,
    /// <c>ProjectAssetsFile</c>, and <c>$(…)</c> paths all feed into it.
    ///
    /// Passed as an environment variable rather than a command-line property: <c>dotnet package list</c>
    /// rejects <c>-p:</c> arguments, but MSBuild reads environment variables as properties.
    /// </summary>
    public string? IsolatedIntermediateDirectory { get; init; }
}
