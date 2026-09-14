using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

public class TransitivePinningTests : IDisposable
{
    private readonly string _directory;
    private readonly string _propsPath;

    public TransitivePinningTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"CPMigratePin_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _propsPath = Path.Combine(_directory, "Directory.Packages.props");
        File.WriteAllText(_propsPath, "<Project />");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void IsEnabled_PropertyTrueInProps_ReturnsTrue()
    {
        WritePropsProperty("true");

        TransitivePinning.IsEnabled(_propsPath, _directory).Should().BeTrue();
    }

    [Fact]
    public void IsEnabled_PropertyAbsent_ReturnsFalse()
    {
        TransitivePinning.IsEnabled(_propsPath, _directory).Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_PropertyFalse_ReturnsFalse()
    {
        WritePropsProperty("false");

        TransitivePinning.IsEnabled(_propsPath, _directory).Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_LastAssignmentWins()
    {
        // MSBuild resolves a repeated assignment last-wins; an earlier true overridden by a later
        // false is off.
        File.WriteAllText(_propsPath,
            """
            <Project>
              <PropertyGroup>
                <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
                <CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled>
              </PropertyGroup>
            </Project>
            """);

        TransitivePinning.IsEnabled(_propsPath, _directory).Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_MalformedProps_ReturnsFalse()
    {
        // Unreadable means unproven, and an unproven pin is one that might do nothing.
        File.WriteAllText(_propsPath, "<Project><PropertyGroup><CentralPackageTransitive");

        TransitivePinning.IsEnabled(_propsPath, _directory).Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_MissingProps_ReturnsFalse()
    {
        File.Delete(_propsPath);

        TransitivePinning.IsEnabled(_propsPath, _directory).Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_PropertyInBuildProps_ReturnsTrue()
    {
        File.WriteAllText(
            Path.Combine(_directory, "Directory.Build.props"),
            """
            <Project>
              <PropertyGroup>
                <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
              </PropertyGroup>
            </Project>
            """);

        TransitivePinning.IsEnabled(_propsPath, _directory).Should().BeTrue();
    }

    [Fact]
    public void IsEnabled_BuildPropsAboveScanRoot_ReturnsTrue()
    {
        // MSBuild walks up from each project for Directory.Build.props — the walk here starts at
        // the scan root, so a build props beside the props file above it still governs.
        var nested = Path.Combine(_directory, "src", "App");
        Directory.CreateDirectory(nested);
        File.WriteAllText(
            Path.Combine(_directory, "Directory.Build.props"),
            """
            <Project>
              <PropertyGroup>
                <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
              </PropertyGroup>
            </Project>
            """);

        TransitivePinning.IsEnabled(_propsPath, nested).Should().BeTrue();
    }

    private void WritePropsProperty(string value)
    {
        File.WriteAllText(_propsPath,
            $"""
            <Project>
              <PropertyGroup>
                <CentralPackageTransitivePinningEnabled>{value}</CentralPackageTransitivePinningEnabled>
              </PropertyGroup>
            </Project>
            """);
    }
}
