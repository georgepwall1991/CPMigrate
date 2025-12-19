using CPMigrate.Services;
using FluentAssertions;
using Xunit;

namespace CPMigrate.Tests.Services;

public class PropsGeneratorTests
{
    private readonly PropsGenerator _generator = new();

    [Fact]
    public void Generate_SinglePackage_GeneratesValidXml()
    {
        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            ["Newtonsoft.Json"] = new() { "13.0.1" }
        };

        var result = _generator.Generate(packageVersions);

        result.Should().Contain("<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>");
        result.Should().Contain("<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />");
        result.Should().Contain("<Project>");
        result.Should().Contain("</Project>");
    }

    [Fact]
    public void Generate_MultiplePackages_GeneratesAllEntries()
    {
        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            ["Newtonsoft.Json"] = new() { "13.0.1" },
            ["Serilog"] = new() { "3.0.0" },
            ["xunit"] = new() { "2.9.0" }
        };

        var result = _generator.Generate(packageVersions);

        result.Should().Contain("<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />");
        result.Should().Contain("<PackageVersion Include=\"Serilog\" Version=\"3.0.0\" />");
        result.Should().Contain("<PackageVersion Include=\"xunit\" Version=\"2.9.0\" />");
    }

    [Fact]
    public void Generate_EmptyDictionary_GeneratesValidStructure()
    {
        var packageVersions = new Dictionary<string, HashSet<string>>();

        var result = _generator.Generate(packageVersions);

        result.Should().Contain("<Project>");
        result.Should().Contain("<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>");
        result.Should().Contain("<ItemGroup>");
        result.Should().Contain("</ItemGroup>");
        result.Should().Contain("</Project>");
    }

    [Fact]
    public void Generate_PackageWithMultipleVersions_ResolvesToHighestByDefault()
    {
        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            ["Newtonsoft.Json"] = new() { "12.0.0", "13.0.1" }
        };

        var result = _generator.Generate(packageVersions, ConflictStrategy.Highest);

        result.Should().Contain("Newtonsoft.Json");
        result.Should().Contain("13.0.1");
        result.Should().NotContain("12.0.0");
    }

    [Fact]
    public void Generate_PackagesAreSortedAlphabetically()
    {
        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            ["Zulu"] = new() { "1.0.0" },
            ["Alpha"] = new() { "2.0.0" },
            ["Mango"] = new() { "3.0.0" }
        };

        var result = _generator.Generate(packageVersions);

        var alphaIndex = result.IndexOf("Alpha");
        var mangoIndex = result.IndexOf("Mango");
        var zuluIndex = result.IndexOf("Zulu");

        alphaIndex.Should().BeLessThan(mangoIndex);
        mangoIndex.Should().BeLessThan(zuluIndex);
    }
}
