using CPMigrate.Services.Interactive;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

public class EnvironmentAnalyzerTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public EnvironmentAnalyzerTests()
    {
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
    public void Analyze_ExpressionTargetFramework_IsNotCountedAsAFramework()
    {
        WriteProject("A.csproj", "<TargetFrameworks>$(SharedTfms)</TargetFrameworks>");
        WriteProject("B.csproj", "<TargetFramework>net8.0</TargetFramework>");

        var ctx = new EnvironmentAnalyzer(new FakeConsoleService(), _testDirectory).Analyze();

        ctx.TargetFrameworks.Should().ContainKey("net8.0").WhoseValue.Should().Be(1);
        ctx.TargetFrameworks.Should().HaveCount(1,
            "an MSBuild expression is not a runtime the project targets");
    }

    [Fact]
    public void Analyze_MultiTargetProject_CountsEachDeclaredFramework()
    {
        WriteProject("A.csproj", "<TargetFrameworks>net8.0;net10.0</TargetFrameworks>");

        var ctx = new EnvironmentAnalyzer(new FakeConsoleService(), _testDirectory).Analyze();

        ctx.TargetFrameworks["net8.0"].Should().Be(1);
        ctx.TargetFrameworks["net10.0"].Should().Be(1);
    }

    private void WriteProject(string name, string targetFrameworkElement)
    {
        File.WriteAllText(
            Path.Combine(_testDirectory, name),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{targetFrameworkElement}</PropertyGroup></Project>");
    }
}
