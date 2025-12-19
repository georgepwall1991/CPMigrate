using CPMigrate.Services;
using FluentAssertions;
using Xunit;

namespace CPMigrate.Tests.Services;

public class PropsGeneratorTests : IDisposable
{
    private readonly PropsGenerator _generator = new();
    private readonly string _testDirectory;

    public PropsGeneratorTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateProps_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

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

    [Fact]
    public void ReadExistingPackageVersions_ReadsIncludeAndUpdateItems()
    {
        var propsPath = WritePropsFile("""
            <Project>
              <ItemGroup>
                <PackageVersion Include="Newtonsoft.Json" Version="13.0.1" />
                <PackageVersion Update="Serilog" Version="3.0.0" />
              </ItemGroup>
            </Project>
            """);

        var result = _generator.ReadExistingPackageVersions(propsPath, out var hasConditional);

        result.Should().ContainKey("Newtonsoft.Json");
        result["Newtonsoft.Json"].Should().Contain("13.0.1");
        result.Should().ContainKey("Serilog");
        result["Serilog"].Should().Contain("3.0.0");
        hasConditional.Should().BeFalse();
    }

    [Fact]
    public void ReadExistingPackageVersions_DetectsConditionalItems()
    {
        var propsPath = WritePropsFile("""
            <Project>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageVersion Include="Conditional.Package" Version="1.2.3" />
              </ItemGroup>
            </Project>
            """);

        var result = _generator.ReadExistingPackageVersions(propsPath, out var hasConditional);

        result.Should().ContainKey("Conditional.Package");
        hasConditional.Should().BeTrue();
    }

    [Fact]
    public void ReadExistingPackageVersions_SkipsMissingVersion()
    {
        var propsPath = WritePropsFile("""
            <Project>
              <ItemGroup>
                <PackageVersion Include="NoVersion" />
              </ItemGroup>
            </Project>
            """);

        var result = _generator.ReadExistingPackageVersions(propsPath, out _);

        result.Should().NotContainKey("NoVersion");
        result.Should().BeEmpty();
    }

    [Fact]
    public void MergeExisting_AddsAndUpdatesPackages()
    {
        var propsPath = WritePropsFile("""
            <Project>
              <ItemGroup>
                <PackageVersion Include="Existing.Package" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            ["Existing.Package"] = new() { "2.0.0" },
            ["New.Package"] = new() { "1.1.0" }
        };

        var (content, added, updated, hasConditional) = _generator.MergeExisting(
            propsPath, packageVersions, ConflictStrategy.Highest);

        added.Should().Be(1);
        updated.Should().Be(1);
        hasConditional.Should().BeFalse();
        content.Should().Contain("<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>");
        content.Should().Contain("Existing.Package");
        content.Should().Contain("2.0.0");
        content.Should().Contain("New.Package");
        content.Should().Contain("1.1.0");
    }

    [Fact]
    public void MergeExisting_PreservesExistingPackagesNotInInput()
    {
        var propsPath = WritePropsFile("""
            <Project>
              <ItemGroup>
                <PackageVersion Include="Keep.Me" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            ["Other.Package"] = new() { "2.0.0" }
        };

        var (content, added, updated, _) = _generator.MergeExisting(
            propsPath, packageVersions, ConflictStrategy.Highest);

        added.Should().Be(1);
        updated.Should().Be(0);
        content.Should().Contain("Keep.Me");
        content.Should().Contain("1.0.0");
    }

    [Fact]
    public void MergeExisting_EscapesSpecialCharactersInPackageName()
    {
        var propsPath = WritePropsFile("""
            <Project>
              <ItemGroup>
                <PackageVersion Include="Existing.Package" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            ["Foo.Bar&Baz"] = new() { "1.2.3" }
        };

        var (content, _, _, _) = _generator.MergeExisting(
            propsPath, packageVersions, ConflictStrategy.Highest);

        content.Should().Contain("Foo.Bar&amp;Baz");
        content.Should().NotContain("Foo.Bar&amp;amp;Baz");
    }

    [Fact]
    public void MergeExisting_MissingFile_ThrowsFileNotFoundException()
    {
        var missingPath = Path.Combine(_testDirectory, "missing.props");
        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            ["Some.Package"] = new() { "1.0.0" }
        };

        var action = () => _generator.MergeExisting(missingPath, packageVersions);

        action.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void ReadExistingPackageVersions_InvalidXml_ThrowsException()
    {
        var propsPath = WritePropsFile("not valid xml");

        var action = () => _generator.ReadExistingPackageVersions(propsPath, out _);

        action.Should().Throw<Exception>();
    }

    private string WritePropsFile(string content)
    {
        var filePath = Path.Combine(_testDirectory, $"Directory.Packages.{Guid.NewGuid():N}.props");
        File.WriteAllText(filePath, content);
        return filePath;
    }
}
