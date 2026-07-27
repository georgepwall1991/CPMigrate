using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CPMigrate.Tests;

/// <summary>
/// Guards NuGet/GitHub discoverability assets: package description/tags, README funnel,
/// and product-flow visuals that ship with PackageReadmeFile.
/// </summary>
public sealed class DiscoverabilityMetadataTests
{
    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));

    private const string ExpectedVersion = "3.6.1";

    private const string RawBase =
        "https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/";

    [Fact]
    public void Package_description_title_and_tags_include_high_intent_cpm_terms()
    {
        var csproj = XDocument.Load(Path.Combine(RepositoryRoot, "CPMigrate", "CPMigrate.csproj"));

        var description = Assert.Single(csproj.Descendants("Description")).Value;
        var tags = Assert.Single(csproj.Descendants("PackageTags")).Value;
        var title = Assert.Single(csproj.Descendants("Title")).Value;
        var readmeFile = Assert.Single(csproj.Descendants("PackageReadmeFile")).Value;
        var version = Assert.Single(csproj.Descendants("Version")).Value;

        Assert.Equal("README.md", readmeFile);
        Assert.Equal(ExpectedVersion, version);
        Assert.Contains("Central Package Management", title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Dependency Analysis", title, StringComparison.OrdinalIgnoreCase);

        foreach (
            var term in new[]
            {
                "Central Package Management",
                "CPM",
                "Directory.Packages.props",
                "dependency health",
                "Directory.Build.props",
                "rollback",
                "--bisect",
            }
        )
        {
            Assert.True(
                description.Contains(term, StringComparison.Ordinal),
                $"Package Description must contain '{term}' for NuGet search discoverability."
            );
        }

        foreach (
            var tag in new[]
            {
                "central-package-management",
                "CPM",
                "CentralPackageManagement",
                "directory-packages-props",
                "Directory.Build.props",
                "dotnet-tool",
                "dependency-analysis",
                "package-updates",
                "rollback",
                "bisect",
                "migrator",
                "vulnerability",
                "centralised",
            }
        )
        {
            Assert.True(
                tags.Contains(tag, StringComparison.Ordinal),
                $"PackageTags must include '{tag}'."
            );
        }
    }

    [Fact]
    public void Readme_conversion_funnel_and_product_visuals_exist_with_resolvable_paths()
    {
        var readmePath = Path.Combine(RepositoryRoot, "README.md");
        var readme = File.ReadAllText(readmePath);

        foreach (
            var section in new[]
            {
                "## The problem",
                "## What it catches",
                "## Install",
                "## See it work",
                "## 30-second path",
                "## Feature snapshot",
                "## Compatibility",
            }
        )
        {
            Assert.Contains(section, readme, StringComparison.Ordinal);
        }

        Assert.Contains(
            $"dotnet tool install --global CPMigrate --version {ExpectedVersion}",
            readme,
            StringComparison.Ordinal
        );
        Assert.Contains("Directory.Packages.props", readme, StringComparison.Ordinal);
        Assert.Contains("Central Package Management", readme, StringComparison.Ordinal);
        Assert.Contains("--bisect", readme, StringComparison.Ordinal);

        var visualAssets = new[]
        {
            "assets/flow-analyze-scoreboard.svg",
            "assets/flow-cpm-migration.svg",
            "assets/flow-update-bisect.svg",
        };

        foreach (var asset in visualAssets)
        {
            Assert.Contains(RawBase + asset, readme, StringComparison.Ordinal);
            var fullPath = Path.Combine(RepositoryRoot, asset);
            Assert.True(File.Exists(fullPath), $"Missing README visual: {asset}");
            Assert.True(new FileInfo(fullPath).Length > 0, $"Empty README visual: {asset}");
        }

        // NuGet.org requires absolute HTTPS image URLs in PackageReadmeFile content.
        var imageRefs = Regex
            .Matches(readme, @"!\[[^\]]*\]\(([^)]+)\)")
            .Select(m => m.Groups[1].Value)
            .Concat(
                Regex
                    .Matches(readme, @"<img[^>]+src=""([^""]+)""")
                    .Select(m => m.Groups[1].Value)
            )
            .Distinct(StringComparer.Ordinal);

        foreach (var imageRef in imageRefs)
        {
            // shields.io / nuget / github badges are https; product images must be too.
            Assert.True(
                imageRef.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
                $"README image must use absolute HTTPS for NuGet rendering: {imageRef}"
            );
        }
    }

    [Fact]
    public void Package_packs_assets_for_nuget_readme_rendering()
    {
        var csproj = XDocument.Load(Path.Combine(RepositoryRoot, "CPMigrate", "CPMigrate.csproj"));

        Assert.Contains(
            csproj.Descendants("None"),
            n =>
                (n.Attribute("Include")?.Value ?? string.Empty).Contains(
                    "assets",
                    StringComparison.Ordinal
                )
                && string.Equals(
                    n.Attribute("Pack")?.Value,
                    "true",
                    StringComparison.OrdinalIgnoreCase
                )
                && (n.Attribute("PackagePath")?.Value ?? string.Empty).Contains(
                    "assets",
                    StringComparison.Ordinal
                )
        );
    }
}
