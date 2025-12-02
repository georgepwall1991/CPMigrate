using CPMigrate.Services;
using FluentAssertions;
using Xunit;

namespace CPMigrate.Tests;

public class ProjectAnalyzerLogicTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly ProjectAnalyzer _analyzer;
    private readonly FakeConsoleService _consoleService;

    public ProjectAnalyzerLogicTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigrateLogicTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _consoleService = new FakeConsoleService();
        _analyzer = new ProjectAnalyzer(_consoleService);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private string CreateTestProject(string fileName, string content)
    {
        var filePath = Path.Combine(_testDirectory, fileName);
        File.WriteAllText(filePath, content);
        return filePath;
    }

    [Fact]
    public void ProcessProject_RemovesVersionAttributes_AndCollectsVersions()
    {
        // Arrange
        var projectContent = "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                             "  <ItemGroup>\n" +
                             "    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />\n" +
                             "    <PackageReference Include=\"Serilog\" Version=\"2.10.0\" />\n" +
                             "  </ItemGroup>\n" +
                             "</Project>";
        var filePath = CreateTestProject("Test.csproj", projectContent);
        var versions = new Dictionary<string, HashSet<string>>();

        // Act
        var resultXml = _analyzer.ProcessProject(filePath, versions, keepVersionAttributes: false);

        // Assert
        versions.Should().ContainKey("Newtonsoft.Json");
        versions["Newtonsoft.Json"].Should().Contain("13.0.1");
        versions.Should().ContainKey("Serilog");
        versions["Serilog"].Should().Contain("2.10.0");

        resultXml.Should().NotContain("Version=\"13.0.1\"");
        resultXml.Should().NotContain("Version=\"2.10.0\"");
        resultXml.Should().Contain("<PackageReference Include=\"Newtonsoft.Json\" />");
    }

    [Fact]
    public void ProcessProject_KeepsVersionAttributes_WhenRequested()
    {
        // Arrange
        var projectContent = "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                             "  <ItemGroup>\n" +
                             "    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />\n" +
                             "  </ItemGroup>\n" +
                             "</Project>";
        var filePath = CreateTestProject("TestKeep.csproj", projectContent);
        var versions = new Dictionary<string, HashSet<string>>();

        // Act
        var resultXml = _analyzer.ProcessProject(filePath, versions, keepVersionAttributes: true);

        // Assert
        versions.Should().ContainKey("Newtonsoft.Json");
        resultXml.Should().Contain("Version=\"13.0.1\"");
    }

    [Fact]
    public void ProcessProject_HandlesNestedVersionElement()
    {
        // Arrange
        var projectContent = "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                             "  <ItemGroup>\n" +
                             "    <PackageReference Include=\"Newtonsoft.Json\">\n" +
                             "      <Version>13.0.1</Version>\n" +
                             "    </PackageReference>\n" +
                             "  </ItemGroup>\n" +
                             "</Project>";
        var filePath = CreateTestProject("TestNested.csproj", projectContent);
        var versions = new Dictionary<string, HashSet<string>>();

        // Act
        var resultXml = _analyzer.ProcessProject(filePath, versions, keepVersionAttributes: false);

        // Assert
        versions.Should().ContainKey("Newtonsoft.Json");
        versions["Newtonsoft.Json"].Should().Contain("13.0.1");
        
        resultXml.Should().NotContain("<Version>13.0.1</Version>");
    }

    [Fact]
    public void ScanProjectPackages_ExtractsPackagesCorrectly()
    {
        // Arrange
        var projectContent = "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                             "  <ItemGroup>\n" +
                             "    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />\n" +
                             "    <PackageReference Include=\"Serilog\" Version=\"2.10.0\" />\n" +
                             "    <PackageReference Include=\"NoVersionPackage\" /> \n" +
                             "  </ItemGroup>\n" +
                             "</Project>";
        var filePath = CreateTestProject("TestScan.csproj", projectContent);

        // Act
        var (references, success) = _analyzer.ScanProjectPackages(filePath);

        // Assert
        success.Should().BeTrue();
        references.Should().HaveCount(2); // Should skip NoVersionPackage
        references.Should().Contain(r => r.PackageName == "Newtonsoft.Json" && r.Version == "13.0.1");
        references.Should().Contain(r => r.PackageName == "Serilog" && r.Version == "2.10.0");
    }

    [Fact]
    public void ScanProjectPackages_HandlesMalformedProjectGracefully()
    {
        // Arrange
        var filePath = CreateTestProject("Malformed.csproj", "<Project><InvalidXml");

        // Act
        var (references, success) = _analyzer.ScanProjectPackages(filePath);

        // Assert
        success.Should().BeFalse();
        references.Should().BeEmpty();
    }

    [Fact]
    public void ProcessProject_AccumulatesMultipleVersions()
    {
        // Arrange
        var projectContent = "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                             "  <ItemGroup>\n" +
                             "    <PackageReference Include=\"LibA\" Version=\"1.0.0\" />\n" +
                             "  </ItemGroup>\n" +
                             "</Project>";
        var filePath = CreateTestProject("TestAccum.csproj", projectContent);
        var versions = new Dictionary<string, HashSet<string>> 
        {
            ["LibA"] = new HashSet<string> { "2.0.0" }
        };

        // Act
        _analyzer.ProcessProject(filePath, versions);

        // Assert
        versions["LibA"].Should().HaveCount(2);
        versions["LibA"].Should().Contain("1.0.0");
        versions["LibA"].Should().Contain("2.0.0");
    }
}