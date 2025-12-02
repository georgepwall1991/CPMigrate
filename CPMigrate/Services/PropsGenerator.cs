using System.Text;

namespace CPMigrate.Services;

/// <summary>
/// Generates Directory.Packages.props content from collected package versions.
/// </summary>
public class PropsGenerator
{
    private readonly VersionResolver _versionResolver;

    public PropsGenerator(VersionResolver? versionResolver = null)
    {
        _versionResolver = versionResolver ?? new VersionResolver();
    }

    /// <summary>
    /// Generates the Directory.Packages.props XML content from collected package versions.
    /// Resolves version conflicts based on the specified strategy.
    /// </summary>
    /// <param name="packageVersions">Dictionary mapping package names to their version sets.</param>
    /// <param name="strategy">Strategy for resolving version conflicts.</param>
    /// <returns>Complete XML content for Directory.Packages.props file.</returns>
    public string Generate(Dictionary<string, HashSet<string>> packageVersions,
        ConflictStrategy strategy = ConflictStrategy.Highest)
    {
        var header = """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <ItemGroup>
                """;

        var stringBuilder = new StringBuilder();
        stringBuilder.AppendLine(header);

        foreach (var kvp in packageVersions.OrderBy(x => x.Key))
        {
            // Skip packages with no versions (shouldn't happen, but defensive)
            if (kvp.Value.Count == 0)
                continue;

            // Resolve to single version if multiple exist
            var version = kvp.Value.Count > 1
                ? _versionResolver.ResolveVersion(kvp.Value, strategy)
                : kvp.Value.First();

            stringBuilder.AppendLine($"""    <PackageVersion Include="{kvp.Key}" Version="{version}" />""");
        }

        stringBuilder.AppendLine("""
                                   </ItemGroup>
                                 </Project>
                                 """);
        return stringBuilder.ToString();
    }
}
