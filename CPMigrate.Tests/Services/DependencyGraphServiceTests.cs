using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CPMigrate.Tests.Services;

public class DependencyGraphServiceTests : IDisposable
{
    private readonly string _testDir;

    public DependencyGraphServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"DependencyGraphTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        Directory.CreateDirectory(Path.Combine(_testDir, "obj"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Fact]
    public void IdentifyRedundantDirectReferences_WithRedundantDep_ReturnsPackage()
    {
        // Arrange
        var assetsJson = @"{
  ""version"": 3,
  ""targets"": {
    ""net8.0"": {
      ""TopLevel/1.0.0"": {
        ""type"": ""package"",
        ""dependencies"": {
          ""Transitive"": ""1.0.0""
        }
      },
      ""Transitive/1.0.0"": {
        ""type"": ""package""
      }
    }
  },
  ""project"": {
    ""frameworks"": {
      ""net8.0"": {
        ""dependencies"": {
          ""TopLevel"": { ""version"": ""1.0.0"" },
          ""Transitive"": { ""version"": ""1.0.0"" }
        }
      }
    }
  }
}";
        File.WriteAllText(Path.Combine(_testDir, "obj", "project.assets.json"), assetsJson);
        var service = new DependencyGraphService(new FakeConsoleService());

        // Act
        var redundant = service.IdentifyRedundantDirectReferences(Path.Combine(_testDir, "Project.csproj"));

        // Assert
        redundant.Should().Contain("Transitive");
    }
}
