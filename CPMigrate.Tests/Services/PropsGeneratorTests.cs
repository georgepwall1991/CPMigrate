using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Tests for PropsGenerator covering XML generation, merging, security, and edge cases.
/// Hunting for production bugs around version resolution, XML injection, and merge logic.
/// </summary>
public class PropsGeneratorTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly PropsGenerator _generator;

    public PropsGeneratorTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CPMigratePropsGeneratorTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _generator = new PropsGenerator();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void Generate_SinglePackage_CreatesValidXml()
    {
        // Arrange
        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            { "Newtonsoft.Json", new HashSet<string> { "13.0.1" } }
        };

        // Act
        var xml = _generator.Generate(packageVersions);

        // Assert
        xml.Should().Contain("<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>");
        xml.Should().Contain("<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />");
    }

    [Fact]
    public void Generate_MultiplePackages_SortsAlphabetically()
    {
        // Arrange
        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            { "Zebra.Package", new HashSet<string> { "1.0.0" } },
            { "Alpha.Package", new HashSet<string> { "2.0.0" } },
            { "Bravo.Package", new HashSet<string> { "3.0.0" } }
        };

        // Act
        var xml = _generator.Generate(packageVersions);

        // Assert
        var alphaIndex = xml.IndexOf("Alpha.Package", StringComparison.Ordinal);
        var bravoIndex = xml.IndexOf("Bravo.Package", StringComparison.Ordinal);
        var zebraIndex = xml.IndexOf("Zebra.Package", StringComparison.Ordinal);

        alphaIndex.Should().BeLessThan(bravoIndex);
        bravoIndex.Should().BeLessThan(zebraIndex);
    }

    [Fact]
    public void Generate_VersionConflict_HighestStrategy_SelectsHighestVersion()
    {
        // Arrange
        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            { "TestPackage", new HashSet<string> { "1.0.0", "2.0.0", "1.5.0" } }
        };

        // Act
        var xml = _generator.Generate(packageVersions, ConflictStrategy.Highest);

        // Assert
        xml.Should().Contain("Version=\"2.0.0\"");
        xml.Should().NotContain("Version=\"1.0.0\"");
        xml.Should().NotContain("Version=\"1.5.0\"");
    }

    [Fact]
    public void Generate_VersionConflict_LowestStrategy_SelectsLowestVersion()
    {
        // Arrange
        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            { "TestPackage", new HashSet<string> { "1.0.0", "2.0.0", "1.5.0" } }
        };

        // Act
        var xml = _generator.Generate(packageVersions, ConflictStrategy.Lowest);

        // Assert
        xml.Should().Contain("Version=\"1.0.0\"");
        xml.Should().NotContain("Version=\"2.0.0\"");
        xml.Should().NotContain("Version=\"1.5.0\"");
    }

    [Fact]
    public void Generate_EmptyPackageVersionSet_SkipsPackage()
    {
        // Arrange
        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            { "ValidPackage", new HashSet<string> { "1.0.0" } },
            { "EmptyPackage", new HashSet<string>() }
        };

        // Act
        var xml = _generator.Generate(packageVersions);

        // Assert
        xml.Should().Contain("ValidPackage");
        xml.Should().NotContain("EmptyPackage");
    }

    [Fact]
    public void Generate_XmlSpecialCharactersInPackageName_EscapesCorrectly()
    {
        // Arrange - Package name with XML special characters
        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            { "Package<Test>&\"Quotes\"", new HashSet<string> { "1.0.0" } }
        };

        // Act
        var xml = _generator.Generate(packageVersions);

        // Assert - Should escape XML characters
        xml.Should().Contain("&lt;");
        xml.Should().Contain("&gt;");
        xml.Should().Contain("&amp;");
        xml.Should().Contain("&quot;");
        xml.Should().NotContain("<Test>");
    }

    [Fact]
    public void Generate_XmlSpecialCharactersInVersion_EscapesCorrectly()
    {
        // Arrange - Version with XML special characters (unusual but possible)
        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            { "TestPackage", new HashSet<string> { "1.0.0<script>" } }
        };

        // Act
        var xml = _generator.Generate(packageVersions);

        // Assert - Should escape XML characters
        xml.Should().Contain("&lt;script&gt;");
        xml.Should().NotContain("<script>");
    }

    [Fact]
    public void ReadExistingPackageVersions_ValidFile_ReadsCorrectly()
    {
        // Arrange
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, @"<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include=""Newtonsoft.Json"" Version=""13.0.1"" />
    <PackageVersion Include=""Serilog"" Version=""2.10.0"" />
  </ItemGroup>
</Project>");

        // Act
        var result = PropsGenerator.ReadExistingPackageVersions(propsPath, out var hasConditional);

        // Assert
        result.Should().HaveCount(2);
        result["Newtonsoft.Json"].Should().Contain("13.0.1");
        result["Serilog"].Should().Contain("2.10.0");
        hasConditional.Should().BeFalse();
    }

    [Fact]
    public void ReadExistingPackageVersions_ConditionalPackages_SetsFlag()
    {
        // Arrange
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, @"<Project>
  <ItemGroup>
    <PackageVersion Include=""NormalPackage"" Version=""1.0.0"" />
  </ItemGroup>
  <ItemGroup Condition=""'$(TargetFramework)' == 'net8.0'"">
    <PackageVersion Include=""ConditionalPackage"" Version=""2.0.0"" />
  </ItemGroup>
</Project>");

        // Act
        var result = PropsGenerator.ReadExistingPackageVersions(propsPath, out var hasConditional);

        // Assert
        hasConditional.Should().BeTrue();
        result.Should().HaveCount(2);
    }

    [Fact]
    public void ReadExistingPackageVersions_NonExistentFile_ThrowsFileNotFoundException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "DoesNotExist.props");

        // Act & Assert
        var act = () => PropsGenerator.ReadExistingPackageVersions(nonExistentPath, out _);
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void ReadExistingPackageVersions_PackageWithUpdate_ReadsCorrectly()
    {
        // Arrange - PackageVersion can use Update instead of Include
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, @"<Project>
  <ItemGroup>
    <PackageVersion Update=""TestPackage"" Version=""1.0.0"" />
  </ItemGroup>
</Project>");

        // Act
        var result = PropsGenerator.ReadExistingPackageVersions(propsPath, out _);

        // Assert
        result.Should().ContainKey("TestPackage");
        result["TestPackage"].Should().Contain("1.0.0");
    }

    [Fact]
    public void ReadExistingPackageVersions_MissingVersion_SkipsPackage()
    {
        // Arrange - PackageVersion without Version metadata
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, @"<Project>
  <ItemGroup>
    <PackageVersion Include=""NoVersion"" />
    <PackageVersion Include=""HasVersion"" Version=""1.0.0"" />
  </ItemGroup>
</Project>");

        // Act
        var result = PropsGenerator.ReadExistingPackageVersions(propsPath, out _);

        // Assert
        result.Should().HaveCount(1);
        result.Should().ContainKey("HasVersion");
        result.Should().NotContainKey("NoVersion");
    }

    [Fact]
    public void MergeExisting_NonExistentFile_ThrowsFileNotFoundException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "DoesNotExist.props");
        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            { "TestPackage", new HashSet<string> { "1.0.0" } }
        };

        // Act & Assert
        var act = () => _generator.MergeExisting(nonExistentPath, packageVersions);
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void MergeExisting_NewPackage_AddsPackage()
    {
        // Arrange
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, @"<Project>
  <ItemGroup>
    <PackageVersion Include=""ExistingPackage"" Version=""1.0.0"" />
  </ItemGroup>
</Project>");

        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            { "NewPackage", new HashSet<string> { "2.0.0" } }
        };

        // Act
        var (content, addedCount, updatedCount, hasConditional) = _generator.MergeExisting(propsPath, packageVersions);

        // Assert
        addedCount.Should().Be(1);
        updatedCount.Should().Be(0);
        hasConditional.Should().BeFalse();
        content.Should().Contain("NewPackage");
        content.Should().Contain("2.0.0");
    }

    [Fact]
    public void MergeExisting_ExistingPackage_UpdatesVersion()
    {
        // Arrange
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, @"<Project>
  <ItemGroup>
    <PackageVersion Include=""TestPackage"" Version=""1.0.0"" />
  </ItemGroup>
</Project>");

        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            { "TestPackage", new HashSet<string> { "2.0.0" } }
        };

        // Act
        var (content, addedCount, updatedCount, _) = _generator.MergeExisting(propsPath, packageVersions);

        // Assert
        addedCount.Should().Be(0);
        updatedCount.Should().Be(1);
        content.Should().Contain("Version=\"2.0.0\"");
        content.Should().NotContain("Version=\"1.0.0\"");
    }

    [Fact]
    public void MergeExisting_ExistingPackageWithSameVersion_NoUpdate()
    {
        // Arrange
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, @"<Project>
  <ItemGroup>
    <PackageVersion Include=""TestPackage"" Version=""1.0.0"" />
  </ItemGroup>
</Project>");

        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            { "TestPackage", new HashSet<string> { "1.0.0" } }
        };

        // Act
        var (_, addedCount, updatedCount, _) = _generator.MergeExisting(propsPath, packageVersions);

        // Assert
        addedCount.Should().Be(0);
        updatedCount.Should().Be(0);
    }

    [Fact]
    public void MergeExisting_MissingManagePackageVersionsCentrally_AddsProperty()
    {
        // Arrange
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, @"<Project>
  <ItemGroup>
    <PackageVersion Include=""TestPackage"" Version=""1.0.0"" />
  </ItemGroup>
</Project>");

        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            { "NewPackage", new HashSet<string> { "2.0.0" } }
        };

        // Act
        var (content, _, _, _) = _generator.MergeExisting(propsPath, packageVersions);

        // Assert
        content.Should().Contain("<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>");
    }

    [Fact]
    public void MergeExisting_MultipleVersions_ResolvesWithStrategy()
    {
        // Arrange
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, @"<Project>
  <ItemGroup>
    <PackageVersion Include=""TestPackage"" Version=""1.0.0"" />
  </ItemGroup>
</Project>");

        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            { "TestPackage", new HashSet<string> { "1.0.0", "2.0.0", "1.5.0" } }
        };

        // Act
        var (content, _, updatedCount, _) = _generator.MergeExisting(propsPath, packageVersions, ConflictStrategy.Highest);

        // Assert
        updatedCount.Should().Be(1);
        content.Should().Contain("Version=\"2.0.0\"");
    }

    [Fact]
    public void MergeExisting_DetectsConditionalPackages()
    {
        // Arrange
        var propsPath = Path.Combine(_testDirectory, "Directory.Packages.props");
        File.WriteAllText(propsPath, @"<Project>
  <ItemGroup Condition=""'$(TargetFramework)' == 'net8.0'"">
    <PackageVersion Include=""ConditionalPackage"" Version=""1.0.0"" />
  </ItemGroup>
</Project>");

        var packageVersions = new Dictionary<string, HashSet<string>>
        {
            { "NewPackage", new HashSet<string> { "2.0.0" } }
        };

        // Act
        var (_, _, _, hasConditional) = _generator.MergeExisting(propsPath, packageVersions);

        // Assert
        hasConditional.Should().BeTrue();
    }
}
