using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Fixtures here declare dependencies the way NuGet does — a version *range* in
/// <c>project.frameworks.&lt;tf&gt;.dependencies</c>, against a <c>targets</c> section keyed by the
/// resolved version. They used to carry a bare <c>"version": "1.0.0"</c>, which real restore output never
/// contains, and that is precisely why a lookup composed from the declared version passed these tests
/// while finding nothing on any actual project. See <see cref="DependencyGraphRealAssetsTests"/>.
/// </summary>
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
        {
            Directory.Delete(_testDir, recursive: true);
        }
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
          ""TopLevel"": { ""target"": ""Package"", ""version"": ""[1.0.0, )"" },
          ""Transitive"": { ""target"": ""Package"", ""version"": ""[1.0.0, )"" }
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

    [Fact]
    public void IdentifyRedundantDirectReferences_NoAssetsFile_ReturnsEmpty()
    {
        // Arrange
        var service = new DependencyGraphService(new FakeConsoleService());

        // Act
        var redundant = service.IdentifyRedundantDirectReferences(Path.Combine(_testDir, "Project.csproj"));

        // Assert
        redundant.Should().BeEmpty();
    }

    [Fact]
    public void IdentifyRedundantDirectReferences_InvalidJson_ReturnsEmptyAndWarns()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testDir, "obj", "project.assets.json"), "not json");
        var console = new FakeConsoleService();
        var service = new DependencyGraphService(console);

        // Act
        var redundant = service.IdentifyRedundantDirectReferences(Path.Combine(_testDir, "Project.csproj"));

        // Assert
        redundant.Should().BeEmpty();
        console.OutputMessages.Should().ContainMatch("*Could not analyze dependency graph*");
    }

    [Fact]
    public void IdentifyRedundantDirectReferences_NoRedundantDeps_ReturnsEmpty()
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
          ""TopLevel"": { ""target"": ""Package"", ""version"": ""[1.0.0, )"" }
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
        redundant.Should().BeEmpty();
    }
}
