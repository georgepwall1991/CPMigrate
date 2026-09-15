using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

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
        var resultXml = ProjectAnalyzer.ProcessProject(filePath, versions, keepVersionAttributes: false);

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
        var resultXml = ProjectAnalyzer.ProcessProject(filePath, versions, keepVersionAttributes: true);

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
        var resultXml = ProjectAnalyzer.ProcessProject(filePath, versions, keepVersionAttributes: false);

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
        ProjectAnalyzer.ProcessProject(filePath, versions);

        // Assert
        versions["LibA"].Should().HaveCount(2);
        versions["LibA"].Should().Contain("1.0.0");
        versions["LibA"].Should().Contain("2.0.0");
    }

    [Fact]
    public void ProcessProject_UpdateOnlyReference_RecordsUnderPackageName_NotEmptyKey()
    {
        // <PackageReference Update="X" Version="…"> amends a reference; its Include is empty.
        // Reading Include alone used to record the version under "" — which generated a
        // <PackageVersion Include=""> pin.
        var projectContent = "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                             "  <ItemGroup>\n" +
                             "    <PackageReference Update=\"Newtonsoft.Json\" Version=\"13.0.1\" />\n" +
                             "  </ItemGroup>\n" +
                             "</Project>";
        var filePath = CreateTestProject("TestUpdate.csproj", projectContent);
        var versions = new Dictionary<string, HashSet<string>>();

        var resultXml = ProjectAnalyzer.ProcessProject(filePath, versions, keepVersionAttributes: false);

        versions.Should().ContainKey("Newtonsoft.Json");
        versions["Newtonsoft.Json"].Should().Contain("13.0.1");
        versions.Should().NotContainKey("");
        resultXml.Should().NotContain("Version=\"13.0.1\"");
    }

    [Fact]
    public void ProcessProject_ExpressionVersion_RenamesToVersionOverride_AndKeepsOutOfLiterals()
    {
        var projectContent = "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                             "  <ItemGroup>\n" +
                             "    <PackageReference Include=\"LibA\" Version=\"$(LibAVer)\" />\n" +
                             "    <PackageReference Include=\"LibB\" Version=\"2.0.0\" />\n" +
                             "  </ItemGroup>\n" +
                             "</Project>";
        var filePath = CreateTestProject("TestExpr.csproj", projectContent);
        var versions = new Dictionary<string, HashSet<string>>();
        var expressions = new Dictionary<string, HashSet<string>>();

        var resultXml = ProjectAnalyzer.ProcessProject(
            filePath,
            versions,
            keepVersionAttributes: false,
            expressionVersions: expressions);

        // The expression is a project-scoped value, not a literal competing for the pin.
        versions.Should().NotContainKey("LibA");
        versions["LibB"].Should().Contain("2.0.0");
        expressions["LibA"].Should().Contain("$(LibAVer)");

        // And it stays in the project as VersionOverride — identical evaluation scope,
        // legal under CPM — rather than being stripped.
        resultXml.Should().Contain("VersionOverride=\"$(LibAVer)\"");
        resultXml.Should().NotContain("Version=\"$(LibAVer)\"");
    }

    [Fact]
    public void ProcessProject_ExpressionVersion_WithoutTracking_FallsBackToForwarding()
    {
        // Callers that cannot track expressions keep the historical behavior: the expression
        // still lands in the pin set so the package does not lose its version.
        var projectContent = "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                             "  <ItemGroup>\n" +
                             "    <PackageReference Include=\"LibA\" Version=\"$(LibAVer)\" />\n" +
                             "  </ItemGroup>\n" +
                             "</Project>";
        var filePath = CreateTestProject("TestExprFallback.csproj", projectContent);
        var versions = new Dictionary<string, HashSet<string>>();

        ProjectAnalyzer.ProcessProject(filePath, versions);

        versions["LibA"].Should().Contain("$(LibAVer)");
    }

    [Fact]
    public void ProcessProject_ExpressionVersion_WithKeepAttributes_StaysVersion()
    {
        var projectContent = "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                             "  <ItemGroup>\n" +
                             "    <PackageReference Include=\"LibA\" Version=\"$(LibAVer)\" />\n" +
                             "  </ItemGroup>\n" +
                             "</Project>";
        var filePath = CreateTestProject("TestExprKeep.csproj", projectContent);
        var versions = new Dictionary<string, HashSet<string>>();
        var expressions = new Dictionary<string, HashSet<string>>();

        var resultXml = ProjectAnalyzer.ProcessProject(
            filePath,
            versions,
            keepVersionAttributes: true,
            expressionVersions: expressions);

        resultXml.Should().Contain("Version=\"$(LibAVer)\"");
        expressions["LibA"].Should().Contain("$(LibAVer)");
    }

    [Fact]
    public void ProcessProject_VersionRange_IsLeftInPlace_WithWarning()
    {
        // "[1.0,2.0)" has no central form — PackageVersion requires an exact version. Stripping
        // it would silently rebind the project to an unrelated pin.
        var projectContent = "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                             "  <ItemGroup>\n" +
                             "    <PackageReference Include=\"LibA\" Version=\"[1.0,2.0)\" />\n" +
                             "  </ItemGroup>\n" +
                             "</Project>";
        var filePath = CreateTestProject("TestRange.csproj", projectContent);
        var versions = new Dictionary<string, HashSet<string>>();
        var expressions = new Dictionary<string, HashSet<string>>();
        var console = new FakeConsoleService();
        var scanner = new ProjectFileScanner(console);

        var resultXml = scanner.ProcessProject(filePath, versions, expressionVersions: expressions);

        versions.Should().BeEmpty();
        expressions.Should().BeEmpty();
        resultXml.Should().Contain("Version=\"[1.0,2.0)\"");
        console.OutputMessages.Should().Contain(m => m.Contains("LibA") && m.Contains("range"));
    }

    [Fact]
    public void ProcessProject_ExpressionVersion_WithExistingVersionOverride_IsNotRenamed()
    {
        // Two VersionOverride entries would be last-wins; renaming here would let the wrong
        // one decide.
        var projectContent = "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                             "  <ItemGroup>\n" +
                             "    <PackageReference Include=\"LibA\" Version=\"$(LibAVer)\" VersionOverride=\"9.9.9\" />\n" +
                             "  </ItemGroup>\n" +
                             "</Project>";
        var filePath = CreateTestProject("TestExprOverride.csproj", projectContent);
        var versions = new Dictionary<string, HashSet<string>>();
        var expressions = new Dictionary<string, HashSet<string>>();
        var console = new FakeConsoleService();
        var scanner = new ProjectFileScanner(console);

        var resultXml = scanner.ProcessProject(filePath, versions, expressionVersions: expressions);

        resultXml.Should().Contain("Version=\"$(LibAVer)\"");
        resultXml.Should().Contain("VersionOverride=\"9.9.9\"");
        console.OutputMessages.Should().Contain(m => m.Contains("LibA") && m.Contains("VersionOverride"));
    }

    [Fact]
    public void ProcessProject_UpdateOnlyReference_WithVersionOverride_RecordedUnderPackageName()
    {
        var projectContent = "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                             "  <ItemGroup>\n" +
                             "    <PackageReference Update=\"Serilog\" Version=\"3.0.0\" />\n" +
                             "  </ItemGroup>\n" +
                             "</Project>";
        var filePath = CreateTestProject("TestUpdateOnly.csproj", projectContent);
        var versions = new Dictionary<string, HashSet<string>>();

        ProjectAnalyzer.ProcessProject(filePath, versions);

        versions.Should().ContainKey("Serilog");
        versions.Should().NotContainKey("");
    }

    [Fact]
    public void ProcessProject_MultiNameInclude_ExpandsEachName()
    {
        // Include="A;B" amends both references — the read path expands the list, so the write
        // path must too or the props file gets a pin literally named "A;B".
        var projectContent = "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                             "  <ItemGroup>\n" +
                             "    <PackageReference Include=\"LibA;LibB\" Version=\"1.5.0\" />\n" +
                             "  </ItemGroup>\n" +
                             "</Project>";
        var filePath = CreateTestProject("TestMulti.csproj", projectContent);
        var versions = new Dictionary<string, HashSet<string>>();

        ProjectAnalyzer.ProcessProject(filePath, versions);

        versions["LibA"].Should().Contain("1.5.0");
        versions["LibB"].Should().Contain("1.5.0");
        versions.Should().NotContainKey("LibA;LibB");
    }
}
